using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Fufucode.Fui;

internal enum FuiTranscriptKind
{
    User,
    Assistant,
    Tool,
    Error,
    Approval
}

internal enum FuiTranscriptFilter
{
    All,
    User,
    Assistant,
    Tool,
    Error,
    Approval
}

internal sealed record FuiCodeBlock(string Language, string Text);

internal sealed record FuiTranscriptEntry(
    FuiTranscriptKind Kind,
    int Sequence,
    int LineIndex,
    string? SourceId,
    string Text,
    string Summary,
    IReadOnlyList<FuiCodeBlock> CodeBlocks,
    IReadOnlyList<string> Urls);

internal sealed record FuiTranscriptCatalog(IReadOnlyList<FuiTranscriptEntry> Entries)
{
    internal static FuiTranscriptCatalog Empty { get; } = new([]);

    internal IReadOnlyList<FuiChoice> Choices(FuiTranscriptFilter filter) =>
        Entries.Where(entry => Includes(filter, entry.Kind)).Select(entry =>
        {
            var label = $"{(filter == FuiTranscriptFilter.All ? $"{entry.Sequence}  " : "")}{FuiTranscriptWorkbench.KindLabel(entry.Kind)} · {entry.Summary}";
            return new FuiChoice(
                $"transcript:{entry.LineIndex}",
                label,
                FuiTranscriptWorkbench.EntryDetail(entry),
                SearchText: $"{FuiTranscriptWorkbench.KindLabel(entry.Kind)}\n{entry.Text}");
        }).ToArray();

    internal static bool Includes(FuiTranscriptFilter filter, FuiTranscriptKind kind) => filter switch
    {
        FuiTranscriptFilter.All => true,
        FuiTranscriptFilter.User => kind == FuiTranscriptKind.User,
        FuiTranscriptFilter.Assistant => kind == FuiTranscriptKind.Assistant,
        FuiTranscriptFilter.Tool => kind == FuiTranscriptKind.Tool,
        FuiTranscriptFilter.Error => kind == FuiTranscriptKind.Error,
        FuiTranscriptFilter.Approval => kind == FuiTranscriptKind.Approval,
        _ => false
    };
}

internal sealed record FuiCopyTarget(string Id, string Label, string Text, string Detail);

internal sealed record FuiCopyCatalog(IReadOnlyList<FuiChoice> Choices, IReadOnlyDictionary<string, FuiCopyTarget> Targets)
{
    internal static FuiCopyCatalog Empty { get; } = new([], new Dictionary<string, FuiCopyTarget>(StringComparer.Ordinal));
}

internal static partial class FuiTranscriptWorkbench
{
    private static readonly Regex Heading = HeadingRegex();
    private static readonly Regex Url = UrlRegex();

    internal static FuiTranscriptCatalog Build(IReadOnlyList<TuiLine> lines, IReadOnlyList<string?> sources)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Count != lines.Count) throw new ArgumentException("Transcript line sources do not align.", nameof(sources));
        var entries = new List<FuiTranscriptEntry>();
        for (var i = 0; i < lines.Count; i++)
        {
            if (Kind(lines[i].Kind) is not { } kind) continue;
            var text = TerminalText.Clean(lines[i].Text).Trim();
            if (text.Length == 0) continue;
            var code = kind == FuiTranscriptKind.Assistant ? ExtractCodeBlocks(text) : [];
            var urls = ExtractUrls(text);
            entries.Add(new(kind, entries.Count + 1, i, sources[i], text, Summary(kind, text), code, urls));
        }
        return new(entries);
    }

    internal static IReadOnlyList<FuiChoice> OutlineChoices(FuiTranscriptCatalog catalog) =>
        catalog.Entries.Select(entry => new FuiChoice(
                $"outline:{entry.LineIndex}",
                $"{entry.Sequence}  {KindLabel(entry.Kind)} · {entry.Summary}",
                EntryDetail(entry),
                SearchText: $"{KindLabel(entry.Kind)}\n{entry.Text}"))
            .ToArray();

    internal static FuiCopyCatalog BuildCopy(
        IReadOnlyList<TuiLine> lines,
        IReadOnlyList<string?> sources,
        string sessionId)
    {
        var targets = new List<FuiCopyTarget>();
        var messages = MessageCopies(lines, sources);
        var assistants = messages.Where(message => message.Kind == FuiTranscriptKind.Assistant).ToArray();
        var latest = assistants.LastOrDefault();
        if (latest is not null)
            Add("copy:recent-assistant", "最近助手回复", latest.Text);

        var userNumber = 0;
        var assistantNumber = 0;
        foreach (var message in messages)
        {
            if (message.Kind == FuiTranscriptKind.User)
                Add($"copy:user:{message.LineIndex}", $"用户 {++userNumber} · {FirstLine(message.Text)}", message.Text);
            else
                Add($"copy:assistant:{message.LineIndex}", $"助手 {++assistantNumber} · {MarkdownSummary(message.Text)}", message.Text);
        }

        var codeNumber = 0;
        FuiCodeBlock? latestCode = null;
        foreach (var message in assistants)
        {
            foreach (var block in ExtractCodeBlocks(message.Text))
            {
                latestCode = block;
                Add($"copy:code:{message.LineIndex}:{codeNumber++}",
                    $"代码块 {codeNumber} · {(block.Language.Length == 0 ? "text" : block.Language)} · {LineCount(block.Text)} 行",
                    block.Text);
            }
        }
        if (latestCode is not null)
            Add("copy:code:current",
                $"当前代码块 · {(latestCode.Language.Length == 0 ? "text" : latestCode.Language)} · {LineCount(latestCode.Text)} 行",
                latestCode.Text);

        var toolNumber = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            if (!string.Equals(lines[i].Kind, "tool", StringComparison.Ordinal)) continue;
            var text = TerminalText.Clean(lines[i].Text).Trim();
            if (text.Length == 0) continue;
            Add($"copy:tool:{i}", $"工具结果 {++toolNumber} · {ToolSummary(text)}", text);
        }

        var urlNumber = 0;
        foreach (var (url, lineIndex) in ExtractUrlsWithLines(lines))
            Add($"copy:url:{lineIndex}:{urlNumber++}", $"URL {urlNumber} · {url}", url);

        Add("copy:session-id", $"会话 ID · {sessionId}", sessionId);
        var choices = targets.Select(target => new FuiChoice(target.Id, target.Label, target.Detail, SearchText: target.Text)).ToArray();
        return new(choices, targets.ToDictionary(target => target.Id, StringComparer.Ordinal));

        void Add(string id, string label, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            var clean = TerminalText.Clean(text).TrimEnd('\r', '\n');
            targets.Add(new(id, label, clean, CopyDetail(label, clean)));
        }
    }

    internal static string KindLabel(FuiTranscriptKind kind) => kind switch
    {
        FuiTranscriptKind.User => "用户",
        FuiTranscriptKind.Assistant => "助手",
        FuiTranscriptKind.Tool => "工具",
        FuiTranscriptKind.Error => "错误",
        FuiTranscriptKind.Approval => "审批",
        _ => "消息"
    };

    internal static string FilterLabel(FuiTranscriptFilter filter) => filter switch
    {
        FuiTranscriptFilter.All => "全部",
        FuiTranscriptFilter.User => "用户",
        FuiTranscriptFilter.Assistant => "助手",
        FuiTranscriptFilter.Tool => "工具",
        FuiTranscriptFilter.Error => "错误",
        FuiTranscriptFilter.Approval => "审批",
        _ => "全部"
    };

    internal static FuiTranscriptFilter NextFilter(FuiTranscriptFilter filter, bool backward = false)
    {
        var values = Enum.GetValues<FuiTranscriptFilter>();
        var index = Array.IndexOf(values, filter);
        index = backward ? (index - 1 + values.Length) % values.Length : (index + 1) % values.Length;
        return values[index];
    }

    internal static string EntryDetail(FuiTranscriptEntry entry)
    {
        var source = string.IsNullOrWhiteSpace(entry.SourceId) ? "" : $"\n来源 {entry.SourceId}";
        return $"{KindLabel(entry.Kind)} #{entry.Sequence} · 对话行 {entry.LineIndex + 1}{source}\n" + Preview(entry.Text);
    }

    internal static string FormatInfo(FuiSessionInfo info, int draftAttachments)
    {
        var usage = info.Usage;
        string Counter(long? value) => value?.ToString() ?? "未知";
        return $"会话  {info.Id}\n" +
               $"项目  {info.Project}\n" +
               $"模型  {info.Model ?? "未知"}\n" +
               $"思考  {info.ThinkingLevel ?? "未知"}\n" +
               $"消息  {info.Messages}\n" +
               $"工具  {info.Tools}\n" +
               $"错误  {info.Errors}\n" +
               $"附件  {info.Attachments} · 草稿 {draftAttachments}\n" +
               $"队列  {info.QueueCount}\n" +
               $"Token  输入 {Counter(usage?.Input)} · 输出 {Counter(usage?.Output)} · 合计 {Counter(usage?.Total)}\n" +
               $"缓存  读取 {Counter(usage?.CacheRead)} · 写入 {Counter(usage?.CacheWrite)}" +
               (info.HistoryPartial ? "\n历史尾部不完整：计数仅含可读部分" : "");
    }

    internal static string MarkdownSummary(string text)
    {
        var inFence = false;
        char fence = default;
        var fenceLength = 0;
        foreach (var raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            var line = raw.Trim();
            if (TryFence(line, out var marker, out var length))
            {
                if (!inFence)
                {
                    inFence = true;
                    fence = marker;
                    fenceLength = length;
                }
                else if (marker == fence && length >= fenceLength)
                {
                    inFence = false;
                }
                continue;
            }
            if (inFence) continue;
            var match = Heading.Match(line);
            if (!match.Success) continue;
            var title = Inline(match.Groups[1].Value);
            if (title.Length != 0) return Abbreviate(title);
        }
        return FirstLine(text);
    }

    internal static IReadOnlyList<FuiCodeBlock> ExtractCodeBlocks(string text)
    {
        var blocks = new List<FuiCodeBlock>();
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = normalized.Split('\n');
        char? fence = null;
        var fenceLength = 0;
        var language = "";
        var body = new StringBuilder();
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            var trimmed = line.TrimStart();
            if (fence is null && TryFence(trimmed, out var marker, out var length))
            {
                fence = marker;
                fenceLength = length;
                language = trimmed[length..].Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault() ?? "";
                body.Clear();
                continue;
            }
            if (fence is { } active && IsClosingFence(trimmed, active, fenceLength))
            {
                blocks.Add(new(language, body.ToString().TrimEnd('\n')));
                fence = null;
                fenceLength = 0;
                language = "";
                body.Clear();
                continue;
            }
            if (fence is not null) body.Append(line).Append('\n');
        }
        if (fence is not null && body.Length != 0)
            blocks.Add(new(language, body.ToString().TrimEnd('\n')));
        return blocks;
    }

    internal static IReadOnlyList<string> ExtractUrls(string text) => ExtractUrlsWithText(text);

    internal static string Preview(string text, int limit = 1600)
    {
        var clean = TerminalText.Clean(text).Trim();
        if (clean.Length <= limit) return clean;
        return clean[..limit] + "…";
    }

    internal static string CopyDetail(string label, string text) =>
        $"{label}\n{Encoding.UTF8.GetByteCount(text)} 字节 UTF-8 · {text.EnumerateRunes().Count()} 字符\n\n" + Preview(text, 1200);

    private static IReadOnlyList<FuiMessageCopy> MessageCopies(IReadOnlyList<TuiLine> lines, IReadOnlyList<string?> sources)
    {
        var result = new List<FuiMessageCopy>();
        FuiMessageCopy? current = null;
        for (var i = 0; i < lines.Count; i++)
        {
            var kind = Kind(lines[i].Kind);
            if (kind is not (FuiTranscriptKind.User or FuiTranscriptKind.Assistant))
            {
                current = null;
                continue;
            }
            var source = sources[i];
            var text = TerminalText.Clean(lines[i].Text).Trim();
            if (text.Length == 0) continue;
            if (source is { Length: > 0 } && current is not null && current.Kind == kind && current.SourceId == source)
            {
                current.Text = current.Text.Length == 0 ? text : current.Text + "\n" + text;
                continue;
            }
            current = new(kind.Value, i, source ?? "line:" + i, text);
            result.Add(current);
        }
        return result;
    }

    private static IReadOnlyList<string> ExtractUrlsWithText(string text)
    {
        var urls = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var inFence = false;
        char fence = default;
        var fenceLength = 0;
        foreach (var raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            var trimmed = raw.TrimStart();
            if (TryFence(trimmed, out var marker, out var length))
            {
                if (!inFence)
                {
                    inFence = true;
                    fence = marker;
                    fenceLength = length;
                }
                else if (marker == fence && length >= fenceLength)
                {
                    inFence = false;
                }
                continue;
            }
            if (inFence) continue;
            foreach (Match match in Url.Matches(raw))
            {
                var value = TrimUrl(match.Value);
                if (value.Length == 0 || !seen.Add(value)) continue;
                urls.Add(value);
            }
        }
        return urls;
    }

    private static IEnumerable<(string Url, int LineIndex)> ExtractUrlsWithLines(IReadOnlyList<TuiLine> transcript)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < transcript.Count; i++)
        {
            if (Kind(transcript[i].Kind) is not (FuiTranscriptKind.User or FuiTranscriptKind.Assistant or FuiTranscriptKind.Tool)) continue;
            foreach (var url in ExtractUrlsWithText(TerminalText.Clean(transcript[i].Text)))
                if (seen.Add(url)) yield return (url, i);
        }
    }

    private static string Summary(FuiTranscriptKind kind, string text) => kind switch
    {
        FuiTranscriptKind.Assistant => MarkdownSummary(text),
        FuiTranscriptKind.Tool => ToolSummary(text),
        _ => FirstLine(text)
    };

    private static string ToolSummary(string text)
    {
        var line = FirstLine(text);
        var space = line.IndexOf(' ');
        if (space < 0) return line;
        var name = line[..space];
        var rest = line[(space + 1)..].Trim();
        if (rest.StartsWith('['))
        {
            var close = rest.IndexOf(']');
            if (close > 0) rest = rest[(close + 1)..].Trim();
        }
        if (rest.StartsWith('{'))
        {
            try
            {
                using var document = JsonDocument.Parse(rest);
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var key in new[] { "path", "file_path", "command", "pattern", "query", "url" })
                    {
                        if (!document.RootElement.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String) continue;
                        rest = value.GetString() ?? rest;
                        break;
                    }
                }
            }
            catch (JsonException) { }
        }
        return Abbreviate(rest.Length == 0 ? name : name + " " + rest);
    }

    private static string FirstLine(string text)
    {
        var line = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')
            .Split('\n').Select(InBrEf).FirstOrDefault(value => value.Length != 0) ?? "";
        return Abbreviate(line);
    }

    private static string InBrEf(string line)
    {
        var value = line.Trim();
        while (value.Length != 0 && value[0] is '#' or '>' or '-' or '*' or '+' or '`')
            value = value[1..].TrimStart();
        return Inline(value);
    }

    private static string Inline(string value)
    {
        var clean = value.Replace("**", "", StringComparison.Ordinal).Replace("__", "", StringComparison.Ordinal)
            .Replace('`', ' ').Trim();
        clean = Regex.Replace(clean, @"\[([^\]]+)\]\([^)]+\)", "$1");
        return TerminalText.Clean(Regex.Replace(clean, @"\s+", " ")).Trim();
    }

    private static string Abbreviate(string text)
    {
        var clean = TerminalText.SingleLine(text).Trim();
        return TerminalText.Width(clean) <= 120 ? clean : TerminalText.Clip(clean, 120);
    }

    private static int LineCount(string text) => text.Length == 0 ? 0 : text.Count(c => c == '\n') + 1;

    private static FuiTranscriptKind? Kind(string kind) => kind switch
    {
        "user" => FuiTranscriptKind.User,
        "assistant" => FuiTranscriptKind.Assistant,
        "tool" => FuiTranscriptKind.Tool,
        "error" => FuiTranscriptKind.Error,
        "approval" => FuiTranscriptKind.Approval,
        _ => null
    };

    private static bool TryFence(string line, out char marker, out int length)
    {
        marker = default;
        length = 0;
        var trimmed = line.TrimStart();
        if (trimmed.Length < 3 || trimmed[0] is not ('`' or '~')) return false;
        marker = trimmed[0];
        while (length < trimmed.Length && trimmed[length] == marker) length++;
        return length >= 3;
    }

    private static bool IsClosingFence(string line, char marker, int minimum)
    {
        var trimmed = line.Trim();
        if (trimmed.Length < minimum || trimmed[0] != marker) return false;
        var length = 0;
        while (length < trimmed.Length && trimmed[length] == marker) length++;
        return length >= minimum && trimmed[length..].Trim().Length == 0;
    }

    private static string TrimUrl(string value)
    {
        var end = value.Length;
        while (end > 0 && value[end - 1] is '.' or ',' or ';' or ':' or '!' or '?' or ')' or ']' or '}' or '>' or '"' or '\'')
            end--;
        return value[..end];
    }

    private sealed class FuiMessageCopy(FuiTranscriptKind kind, int lineIndex, string sourceId, string text)
    {
        internal FuiTranscriptKind Kind { get; } = kind;
        internal int LineIndex { get; } = lineIndex;
        internal string SourceId { get; } = sourceId;
        internal string Text { get; set; } = text;
    }

    [GeneratedRegex(@"^\s{0,3}#{1,6}\s+(.+?)\s*#*\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"https?://[^\s<>`""']+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlRegex();
}
