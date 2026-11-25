using System.Globalization;
using System.Text;
using Fufucode.App.Abstractions;

namespace Fufucode.Fui;

internal static class FuiContextWorkbench
{
    internal const string Title = "上下文 · 只读";
    internal const string Footer = "↑↓ 选择 · Enter 查看/定位 · 输入搜索 · Esc 关闭";
    internal const string GroupPrefix = "context-group:";
    internal const string AttachmentPrefix = "context:attachment:";
    internal const string LinePrefix = "context:line:";

    internal static IReadOnlyList<FuiChoice> Build(
        FuiSessionInfo info,
        IReadOnlyList<FileAttachment> attachments,
        FuiPlanSnapshot plan,
        IReadOnlyList<TuiLine> lines,
        IReadOnlyList<string?>? sources = null,
        bool factsAvailable = true)
    {
        if (sources is not null && sources.Count != lines.Count)
            throw new ArgumentException("Context line sources do not align.", nameof(sources));
        var choices = new List<FuiChoice>();
        AddGroup("facts", "会话事实");
        Add("model", "模型", info.Model ?? "未知", info.Model is null ? "FUMO 未提供当前模型。" : "来源 FUMO 会话");
        Add("session", "会话", info.Id, "FUMO 会话 ID");
        Add("project", "项目", info.Project, "当前工作目录");
        Add("window", "上下文窗口",
            factsAvailable && info.ContextWindow is > 0 ? FormatCount(info.ContextWindow.Value) + " tokens" : "未知",
            factsAvailable && info.ContextWindow is > 0
                ? "来源 " + (info.ContextWindowSource ?? "Core")
                : factsAvailable
                    ? info.ContextWindowUnknownReason ?? "FUMO 未提供可证明的上下文窗口。"
                    : "当前会话没有可证明的上下文事实接口。");
        Add("mode", "执行模式", factsAvailable ? info.ExecutionMode ?? "未知" : "未知",
            !factsAvailable ? "当前会话没有可证明的执行模式事实。"
                : info.ExecutionMode is null ? "FUMO 未提供执行模式。" : "当前会话的实际执行模式");

        AddGroup("usage", "已报告 Token 用量");
        if (!factsAvailable)
        {
            Add("usage-unavailable", "用量", "未知", "当前会话没有可证明的 token 用量事实；未按字符估算。");
        }
        else if (info.Usage is not { } usage)
        {
            Add("usage-unknown", "用量", "未知",
                info.HistoryPartial
                    ? "历史不可完整读取；未按字符估算 token。"
                    : "尚无端点报告的 token 用量。");
        }
        else
        {
            Add("usage-input", "输入", Counter(usage.Input), "端点或历史报告的输入 token");
            Add("usage-output", "输出", Counter(usage.Output), "端点或历史报告的输出 token");
            Add("usage-total", "合计", Counter(usage.Total), "只使用端点报告的合计；不从字符数推导");
            Add("usage-cache-read", "缓存读取", Counter(usage.CacheRead), "端点或历史报告的缓存读取 token");
            Add("usage-cache-write", "缓存写入", Counter(usage.CacheWrite), "端点或历史报告的缓存写入 token");
        }

        AddGroup("counts", "会话计数");
        if (!factsAvailable)
        {
            Add("counts-unavailable", "计数", "未知", "当前会话没有可证明的消息、思考、工具或错误计数。");
        }
        else
        {
            Add("count-messages", "消息", info.Messages.ToString(CultureInfo.InvariantCulture), "当前分支消息快照数量");
            Add("count-thinking", "思考", info.Thinking.ToString(CultureInfo.InvariantCulture), "实际思考块数量");
            Add("count-tools", "工具", info.Tools.ToString(CultureInfo.InvariantCulture), "实际工具调用数量");
            Add("count-errors", "错误", info.Errors.ToString(CultureInfo.InvariantCulture), "本次运行观察到的错误事件数量");
            Add("count-history-images", "已发送图片", info.Attachments.ToString(CultureInfo.InvariantCulture), "当前分支历史中的图片附件数量");
            if (info.HistoryPartial)
                Add("history-partial", "历史", "不完整", "计数与用量只覆盖可读部分。");
        }

        AddGroup("attachments", $"待发送附件 · {attachments.Count}");
        if (attachments.Count == 0)
        {
            Add("attachments-empty", "附件", "无", "当前草稿没有待发送附件。");
        }
        else
        {
            for (var index = 0; index < attachments.Count; index++)
            {
                var file = attachments[index];
                var name = Path.GetFileName(file.Path);
                if (name.Length == 0) name = file.Path;
                var image = file.MimeType is not null;
                var dimensions = image && file.ImageWidth > 0 && file.ImageHeight > 0
                    ? $" · {file.ImageWidth}×{file.ImageHeight}"
                    : "";
                var detail = AttachmentDetail(file);
                choices.Add(new FuiChoice(
                    AttachmentPrefix + index,
                    $"{(image ? "▣" : "▤")} {name} · {(image ? "图片" + dimensions : "文本")} · {Size(file)}",
                    detail,
                    SearchText: file.Path + "\n" + detail));
            }
        }

        var queued = info.QueueEntries ?? [];
        var recoverable = info.RecoverableQueue ?? [];
        AddGroup("queue", factsAvailable ? $"队列 · {queued.Count + recoverable.Count}" : "队列 · 未知");
        if (!factsAvailable)
        {
            Add("queue-unavailable", "队列", "未知", "当前会话没有可证明的队列事实。");
        }
        else if (queued.Count == 0 && recoverable.Count == 0)
        {
            Add("queue-empty", "队列", "空", "当前没有排队、插话或已取消的待发消息。");
        }
        else
        {
            for (var index = 0; index < queued.Count; index++)
            {
                var item = queued[index];
                var text = TerminalText.Clean(item.Text);
                var kind = item.Steering ? "插话" : "排队";
                AddChoice($"context:queue:{index}", $"{kind} {index + 1} · {Summary(text)}",
                    $"待发送{kind}\n" + text);
            }
            for (var index = 0; index < recoverable.Count; index++)
            {
                var text = TerminalText.Clean(recoverable[index]);
                AddChoice($"context:recoverable:{index}", $"已取消 {index + 1} · {Summary(text)}",
                    "已取消，尚未发送\n" + text);
            }
        }

        AddGroup("plan", plan.HasItems ? $"计划 · {plan.Completed}/{plan.Items.Count}" : "计划 · 未提供");
        if (!plan.HasItems)
        {
            AddChoice("context:plan:empty", "计划 · 未提供",
                "当前会话没有可证明的 update_plan 快照；未合成计划。");
        }
        else
        {
            foreach (var item in plan.Items)
                AddChoice($"context:plan:{item.Number}",
                    $"{item.Glyph} {item.Number}. {item.Title} · {item.StateLabel}",
                    $"计划步骤 {item.Number}\n{item.Title}\n状态 {item.StateLabel}" +
                    (string.IsNullOrWhiteSpace(plan.Notes) ? "" : "\n备注 " + plan.Notes));
        }

        var tools = lines.Select((line, index) => (line, index))
            .Where(item => item.line.Kind == "tool")
            .ToArray();
        AddGroup("tools", $"工具活动 · {tools.Length}");
        if (tools.Length == 0)
        {
            AddChoice("context:tools:empty", "工具活动 · 0", "当前分支没有工具调用。");
        }
        else
        {
            foreach (var (line, index) in tools)
            {
                var text = TerminalText.Clean(line.Text);
                var snapshot = line.Tool;
                var summary = snapshot is null
                    ? Summary(text)
                    : $"{snapshot.Name} · {ToolStatus(snapshot.Status)}";
                AddChoice(LinePrefix + index,
                    $"工具 · {summary}",
                    ToolDetail(line, index, SourceSuffix(index)));
            }
        }

        var imageLines = lines.Select((line, index) => (line, index))
            .Where(item => item.line.Images is { Count: > 0 } ||
                item.line.Text.Contains("[图片 × ", StringComparison.Ordinal))
            .ToArray();
        if (imageLines.Length != 0)
        {
            AddGroup("attachment-lines", $"附件消息 · {imageLines.Length}");
            foreach (var (line, index) in imageLines)
            {
                var text = TerminalText.Clean(line.Text);
                var images = line.Images;
                var summary = images is { Count: > 0 }
                    ? $"{images.Count} 张 · {images[0].Label}"
                    : Summary(text);
                AddChoice(LinePrefix + index,
                    $"附件消息 · {summary}",
                    ImageDetail(line, index, SourceSuffix(index)));
            }
        }

        return choices;

        void AddGroup(string key, string label) =>
            choices.Add(new FuiChoice(GroupPrefix + key, label));

        void Add(string key, string label, string value, string detail) =>
            AddChoice("context:" + key, $"{label} · {value}", $"{label}\n值 {value}\n{detail}");

        void AddChoice(string value, string label, string detail) =>
            choices.Add(new FuiChoice(value, label, TerminalText.Clean(detail), SearchText: detail));

        string SourceSuffix(int index) =>
            sources?[index] is { Length: > 0 } source ? "\n来源 " + source : "";
    }

    internal static bool IsGroupChoice(FuiChoice choice) =>
        choice.Value.StartsWith(GroupPrefix, StringComparison.Ordinal);

    internal static bool TryLineIndex(FuiChoice choice, out int index)
    {
        index = -1;
        return choice.Value.StartsWith(LinePrefix, StringComparison.Ordinal) &&
            int.TryParse(choice.Value.AsSpan(LinePrefix.Length), NumberStyles.None,
                CultureInfo.InvariantCulture, out index) && index >= 0;
    }

    private static string AttachmentDetail(FileAttachment file)
    {
        var image = file.MimeType is not null;
        var kind = image ? file.MimeType! : "UTF-8 文本";
        var characters = image ? "不适用" :
            file.Text.EnumerateRunes().Count().ToString(CultureInfo.InvariantCulture);
        var source = file.Source == FileAttachmentSource.Clipboard ? "剪贴板" : "项目文件";
        var lines = new List<string>
        {
            "名称 " + file.Path,
            "类型 " + kind,
            "字节 " + (file.ByteCount is { } bytes
                ? bytes.ToString("N0", CultureInfo.InvariantCulture)
                : Size(file)),
            "字符 " + characters,
            "图片 " + (image ? "是" : "否"),
            "来源 " + source
        };
        if (image)
        {
            if (file.ImageWidth > 0 && file.ImageHeight > 0)
                lines.Add($"尺寸 {file.ImageWidth}×{file.ImageHeight}");
            lines.Add("格式 " + (file.FormatLabel ?? file.MimeType!));
            if (file.ImageBytes is { } encoded)
                lines.Add("编码 " + encoded.Length.ToString("N0", CultureInfo.InvariantCulture) + " B");
        }
        return string.Join('\n', lines);
    }

    private static string ToolDetail(TuiLine line, int index, string source)
    {
        if (line.Tool is not { } tool)
            return $"对话行 {index + 1}\n{TerminalText.Clean(line.Text)}{source}";
        var duration = tool.DurationMs is { } ms
            ? "\n耗时 " + ms.ToString(CultureInfo.InvariantCulture) + " ms"
            : "";
        var output = string.IsNullOrWhiteSpace(tool.Output)
            ? ""
            : "\n输出\n" + FuiTranscriptWorkbench.Preview(tool.Output);
        return $"工具 {tool.Name}\n状态 {ToolStatus(tool.Status)}" +
            (string.IsNullOrWhiteSpace(tool.StatusText) ? "" : " · " + tool.StatusText) +
            $"\n对话行 {index + 1}{source}{duration}\n参数\n{FuiTranscriptWorkbench.Preview(TerminalText.Clean(tool.ArgumentsJson))}{output}";
    }

    private static string ImageDetail(TuiLine line, int index, string source)
    {
        var text = TerminalText.Clean(line.Text);
        if (line.Images is not { Count: > 0 } images)
            return $"对话行 {index + 1}{source}\n{text}";
        var lines = new List<string>
        {
            $"对话行 {index + 1}{source}",
            $"图片 {images.Count}"
        };
        for (var i = 0; i < images.Count; i++)
        {
            var image = images[i];
            var bytes = image.Bytes is { } data ? $" · {data.Length.ToString("N0", CultureInfo.InvariantCulture)} B" : "";
            lines.Add($"{i + 1}. {image.Label} · {image.MimeType}{bytes}");
        }
        if (text.Length != 0) lines.Add(text);
        return string.Join('\n', lines);
    }

    private static string ToolStatus(ToolCallStatus? status) => status switch
    {
        ToolCallStatus.Pending => "待执行",
        ToolCallStatus.Running => "执行中",
        ToolCallStatus.Completed => "完成",
        ToolCallStatus.Failed => "失败",
        ToolCallStatus.Cancelled => "已取消",
        _ => "状态未知"
    };

    private static string Size(FileAttachment file)
    {
        long? bytes = file.ByteCount;
        if (bytes is null && file.MimeType is null)
            bytes = Encoding.UTF8.GetByteCount(file.Text);
        if (bytes is null && file.Payload is { } payload)
            bytes = payload.Length;
        return bytes is { } known ? known.ToString("N0", CultureInfo.InvariantCulture) + " B" : "未知";
    }

    private static string Counter(long? value) =>
        value?.ToString("N0", CultureInfo.InvariantCulture) ?? "未知";

    private static string FormatCount(long value) =>
        value.ToString("N0", CultureInfo.InvariantCulture);

    private static string Summary(string text)
    {
        var line = TerminalText.SingleLine(text).Trim();
        return line.Length <= 96 ? line : TerminalText.Clip(line, 96);
    }
}
