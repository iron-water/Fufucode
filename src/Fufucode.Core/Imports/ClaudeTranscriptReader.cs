using System.Text;
using System.Text.Json;
using Fufucode.Core.Backend;

namespace Fufucode.Core.Imports;

/// <summary>
/// Claude Code 转录文件（<c>~/.claude/projects/&lt;编码路径&gt;/&lt;session&gt;.jsonl</c>）的只读解析结果。
/// <see cref="Messages"/> 已经是 FUMO 可直接回放的历史形态：只保留人与模型的正文，
/// 工具调用压成一行摘要，工具结果、思考块、附件与快照等运行期噪音全部丢弃。
/// </summary>
public sealed record ClaudeTranscript(string? WorkingDirectory, string? Title, DateTimeOffset? StartedUtc,
    DateTimeOffset? UpdatedUtc, IReadOnlyList<PureAgentMessage> Messages)
{
    /// <summary>首条用户正文，用于回退命名（与桌面端自动命名口径一致）。</summary>
    public string? FirstUserText => Messages
        .FirstOrDefault(message => message.Role == PureAgentRole.User && !string.IsNullOrWhiteSpace(message.Content))
        ?.Content;
}

/// <summary>
/// 单条转录的解析。逐行流式读取，先按类型子串便宜过滤再解析 JSON：
/// 一份转录里体积最大的是工具结果，而它们从不进入历史。
/// </summary>
public static class ClaudeTranscriptReader
{
    /// <summary>单段正文上限。超长粘贴（整篇文档、整段日志）保留开头，避免单个会话文件失控。</summary>
    public const int MaxTextBlockChars = 16_000;

    /// <summary>工具摘要行上限。</summary>
    public const int MaxToolSummaryChars = 160;

    private static readonly string[] ToolSummaryKeys =
        ["command", "file_path", "path", "pattern", "query", "url", "description", "prompt", "notebook_path"];

    public static ClaudeTranscript Parse(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var messages = new List<PureAgentMessage>();
        var assistant = new StringBuilder();
        var assistantDirty = false;
        string? workingDirectory = null;
        string? title = null;
        DateTimeOffset? started = null, updated = null;

        void FlushAssistant()
        {
            if (!assistantDirty) return;
            var text = assistant.ToString().Trim('\n', '\r', ' ', '\t');
            if (text.Length > 0) messages.Add(new PureAgentMessage(PureAgentRole.Assistant, text));
            assistant.Clear();
            assistantDirty = false;
        }

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true,
            bufferSize: 64 * 1024, leaveOpen: true);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length < 8 || !CarriesHistory(line)) continue;
            JsonDocument document;
            try { document = JsonDocument.Parse(line); }
            catch (JsonException) { continue; } // 单行损坏不放弃整份转录。
            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                if (workingDirectory is null && String(root, "cwd") is { Length: > 0 } found) workingDirectory = found;
                TrackTimestamp(root, ref started, ref updated);

                switch (String(root, "type"))
                {
                    case "summary":
                        title ??= String(root, "summary");
                        break;
                    case "custom-title":
                        title = String(root, "customTitle") ?? title;
                        break;
                    case "agent-name":
                        title = String(root, "agentName") ?? title;
                        break;
                    case "user":
                        // 用户轮次先落地，因此累积的助手正文在这里截断。
                        if (UserText(root) is { } userText)
                        {
                            FlushAssistant();
                            messages.Add(new PureAgentMessage(PureAgentRole.User, userText));
                        }
                        break;
                    case "assistant":
                        assistantDirty |= AppendAssistant(root, assistant);
                        break;
                }
            }
        }

        FlushAssistant();
        return new ClaudeTranscript(workingDirectory, title, started, updated, messages);
    }

    /// <summary>只解析可能带正文的行；快照、计费、队列操作等类型在这里被整体跳过。</summary>
    private static bool CarriesHistory(string line) =>
        line.Contains("\"type\":\"user\"", StringComparison.Ordinal) ||
        line.Contains("\"type\":\"assistant\"", StringComparison.Ordinal) ||
        line.Contains("\"type\":\"summary\"", StringComparison.Ordinal) ||
        line.Contains("\"type\":\"custom-title\"", StringComparison.Ordinal) ||
        line.Contains("\"type\":\"agent-name\"", StringComparison.Ordinal);

    private static bool AppendAssistant(JsonElement root, StringBuilder sink)
    {
        if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
            return false;
        if (message.TryGetProperty("content", out var content) && Blocks(content, sink, isAssistant: true)) return true;
        return false;
    }

    private static string? UserText(JsonElement root)
    {
        if (root.TryGetProperty("isMeta", out var meta) && meta.ValueKind == JsonValueKind.True) return null;
        if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object) return null;
        if (!message.TryGetProperty("content", out var content)) return null;

        var sink = new StringBuilder();
        if (!Blocks(content, sink, isAssistant: false)) return null;
        return NormalizeUserText(sink.ToString());
    }

    /// <summary>
    /// 展开 content（字符串或块数组）。助手侧把工具调用压成一行摘要写进正文；
    /// 用户侧的工具结果块被丢弃——它们既是体积主因，也是恢复后不该回灌给模型的东西。
    /// </summary>
    private static bool Blocks(JsonElement content, StringBuilder sink, bool isAssistant)
    {
        switch (content.ValueKind)
        {
            case JsonValueKind.String:
                return AppendText(sink, content.GetString());
            case JsonValueKind.Array:
                var appended = false;
                foreach (var block in content.EnumerateArray())
                {
                    if (block.ValueKind != JsonValueKind.Object) continue;
                    switch (String(block, "type"))
                    {
                        case "text":
                            appended |= AppendText(sink, String(block, "text"));
                            break;
                        case "image":
                            appended |= AppendText(sink, "[图片]");
                            break;
                        case "tool_use" when isAssistant:
                            appended |= AppendText(sink, ToolLine(block));
                            break;
                    }
                }
                return appended;
            default:
                return false;
        }
    }

    private static bool AppendText(StringBuilder sink, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var value = text.Length > MaxTextBlockChars
            ? text[..MaxTextBlockChars] + "\n…（内容过长，已截断）"
            : text;
        if (sink.Length > 0) sink.Append("\n\n");
        sink.Append(value);
        return true;
    }

    private static string ToolLine(JsonElement block)
    {
        var name = String(block, "name") ?? "tool";
        var summary = SummarizeInput(block);
        return summary.Length == 0 ? $"▸ {name}" : $"▸ {name}: {summary}";
    }

    private static string SummarizeInput(JsonElement block)
    {
        if (!block.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Object) return "";
        string? candidate = null;
        foreach (var key in ToolSummaryKeys)
        {
            if (!input.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String) continue;
            candidate = value.GetString();
            break;
        }
        if (string.IsNullOrWhiteSpace(candidate))
        {
            foreach (var property in input.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String) continue;
                candidate = property.Value.GetString();
                break;
            }
        }
        if (string.IsNullOrWhiteSpace(candidate)) return "";
        var single = candidate.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return single.Length > MaxToolSummaryChars ? single[..MaxToolSummaryChars] + "…" : single;
    }

    /// <summary>
    /// 去掉注入的 system-reminder，并丢弃只有命令回显/中断提示的伪用户轮次。
    /// 这些在 Claude Code 里是 UI 事件，不是用户说过的话。
    /// </summary>
    private static string? NormalizeUserText(string text)
    {
        var cleaned = StripSystemReminders(text).Trim();
        if (cleaned.Length == 0) return null;
        foreach (var prefix in (string[])
                 ["<command-name>", "<local-command-stdout>", "<local-command-caveat>",
                     "<user-prompt-submit-hook>", "<task-notification>", "<tool-use-id>",
                     "[Request interrupted by user]", "Caveat: The messages below were generated"])
        {
            if (cleaned.StartsWith(prefix, StringComparison.Ordinal)) return null;
        }
        if (cleaned.Length > MaxTextBlockChars) cleaned = cleaned[..MaxTextBlockChars] + "\n…（内容过长，已截断）";
        return cleaned;
    }

    private static string StripSystemReminders(string text)
    {
        const string open = "<system-reminder>";
        const string close = "</system-reminder>";
        var index = text.IndexOf(open, StringComparison.Ordinal);
        if (index < 0) return text;
        var sink = new StringBuilder(text.Length);
        var cursor = 0;
        while (index >= 0)
        {
            sink.Append(text, cursor, index - cursor);
            var end = text.IndexOf(close, index, StringComparison.Ordinal);
            if (end < 0) { cursor = text.Length; break; }
            cursor = end + close.Length;
            index = text.IndexOf(open, cursor, StringComparison.Ordinal);
        }
        if (cursor < text.Length) sink.Append(text, cursor, text.Length - cursor);
        return sink.ToString();
    }

    private static void TrackTimestamp(JsonElement root, ref DateTimeOffset? started, ref DateTimeOffset? updated)
    {
        if (String(root, "timestamp") is not { Length: > 0 } raw) return;
        if (!DateTimeOffset.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var value)) return;
        if (started is null || value < started) started = value;
        if (updated is null || value > updated) updated = value;
    }

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
