using System.Globalization;
using System.Text;
using Fufucode.Core;

namespace Fufucode.Fui;

internal enum FuiPlanItemState { Pending, InProgress, Completed }

internal sealed record FuiPlanItem(int Number, string Title, FuiPlanItemState State)
{
    internal string Glyph => State switch
    {
        FuiPlanItemState.Completed => "✓",
        FuiPlanItemState.InProgress => "▸",
        _ => "·"
    };

    internal string StateLabel => State switch
    {
        FuiPlanItemState.Completed => "完成",
        FuiPlanItemState.InProgress => "进行中",
        _ => "待办"
    };
}

internal sealed record FuiPlanSnapshot(IReadOnlyList<FuiPlanItem> Items, string? Notes)
{
    internal static FuiPlanSnapshot Empty { get; } = new([], null);
    internal int Completed => Items.Count(item => item.State == FuiPlanItemState.Completed);
    internal bool HasItems => Items.Count != 0;
    internal FuiPlanItem? Active => Items.FirstOrDefault(item => item.State == FuiPlanItemState.InProgress) ??
        Items.FirstOrDefault(item => item.State == FuiPlanItemState.Pending);

    internal string StatusSummary => !HasItems ? "" : $"计划 {Completed}/{Items.Count}" +
        (Active is { } active ? " · " + TerminalText.Clip(active.Title, 28) : "");

    internal string Detail => HasItems
        ? string.Join('\n', Items.Select(item => $"{item.Glyph} {item.Number}. {item.Title} · {item.StateLabel}")) +
          (string.IsNullOrWhiteSpace(Notes) ? "" : "\n备注 · " + TerminalText.Clean(Notes).Trim())
        : "当前会话没有 update_plan 计划。";
}

internal interface IFuiPlanSession
{
    string? CurrentPlanSnippet { get; }
    string ExecutionMode { get; }
}

internal static class FuiPlanProjection
{
    internal static FuiPlanSnapshot FromSnippet(string? snippet)
    {
        if (string.IsNullOrWhiteSpace(snippet)) return FuiPlanSnapshot.Empty;
        var text = snippet.Replace("\r\n", "\n", StringComparison.Ordinal);
        var items = new List<FuiPlanItem>();
        string? notes = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("备注:", StringComparison.Ordinal))
            {
                notes = line[3..].Trim();
                continue;
            }
            if (line.Length < 6) continue;
            var state = line[0] switch
            {
                '[' when line.Length >= 3 && line[1] is 'x' or 'X' && line[2] == ']' => FuiPlanItemState.Completed,
                '[' when line.Length >= 3 && line[1] == '>' && line[2] == ']' => FuiPlanItemState.InProgress,
                '[' when line.Length >= 3 && line[1] == ' ' && line[2] == ']' => FuiPlanItemState.Pending,
                _ => (FuiPlanItemState?)null
            };
            if (state is null) continue;
            var body = line[3..].Trim();
            var dot = body.IndexOf('.');
            var number = items.Count + 1;
            if (dot > 0 && int.TryParse(body[..dot], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
            {
                number = parsed;
                body = body[(dot + 1)..].Trim();
            }
            var status = body.LastIndexOf(" (", StringComparison.Ordinal);
            if (status > 0 && body.EndsWith(')')) body = body[..status].Trim();
            if (body.Length == 0) continue;
            items.Add(new(number, TerminalText.SingleLine(body), state.Value));
        }
        return items.Count == 0 ? FuiPlanSnapshot.Empty : new(items, notes);
    }

    internal static FuiPlanSnapshot FromLines(IEnumerable<TuiLine> lines)
    {
        foreach (var line in lines.Reverse())
        {
            if (line.Kind != "tool" || !line.Text.Contains("update_plan", StringComparison.OrdinalIgnoreCase)) continue;
            var marker = line.Text.IndexOf("[计划已更新]", StringComparison.Ordinal);
            if (marker >= 0)
            {
                var parsed = FromSnippet(line.Text[marker..]);
                if (parsed.HasItems) return parsed;
            }
            var parsedFallback = FromSnippet(line.Text);
            if (parsedFallback.HasItems) return parsedFallback;
        }
        return FuiPlanSnapshot.Empty;
    }
}

internal sealed record FuiChangeEntry(ChangedFile File)
{
    internal string Glyph => File.Kind switch
    {
        "added" or "untracked" => "+",
        "deleted" => "-",
        "renamed" or "copied" => "→",
        "unmerged" => "!",
        _ => "•"
    };
    internal string Detail => string.Join(" · ",
        new[] { File.Kind, File.Path, File.AddedLines is null && File.RemovedLines is null ? null :
            $"+{File.AddedLines?.ToString(CultureInfo.InvariantCulture) ?? "?"} / -{File.RemovedLines?.ToString(CultureInfo.InvariantCulture) ?? "?"}",
            File.IsBinary == true ? "二进制" : null }.Where(value => value is not null));
}

internal static class FuiChangeProjection
{
    internal static IReadOnlyList<FuiChangeEntry> Entries(ChangeRangeReport report) =>
        report.Available ? report.Files.Select(file => new FuiChangeEntry(file)).ToArray() : [];

    internal static string Overview(ChangeRangeReport report)
    {
        if (!report.Available)
            return report.Error switch
            {
                "not-a-git-worktree" => "当前项目不是 Git 工作树。",
                "git-unavailable" => "找不到 Git，无法读取工作区变更。",
                _ => "无法读取 Git 变更：" + (report.Error ?? "未知原因")
            };
        if (report.Files.Count == 0) return "工作区干净。";
        var added = report.Files.Sum(file => file.AddedLines ?? 0);
        var removed = report.Files.Sum(file => file.RemovedLines ?? 0);
        return $"工作区变更 {report.Files.Count} 个文件 · +{added} / -{removed}\n" +
            string.Join('\n', report.Files.Select(file => new FuiChangeEntry(file).Glyph + " " + new FuiChangeEntry(file).Detail));
    }
}

internal sealed record FuiStats(
    int User, int Assistant, int Thinking, int Tools, int Errors, int Attachments,
    int RunningAgents, int TotalAgents, string ExecutionMode, FuiPlanSnapshot Plan)
{
    internal static FuiStats From(IReadOnlyList<TuiLine> lines, IReadOnlyList<FileAttachment> attachments,
        FuiAgentActivitySnapshot agents, string mode)
    {
        var plan = FuiPlanProjection.FromLines(lines);
        return new(
            lines.Count(line => line.Kind == "user"),
            lines.Count(line => line.Kind == "assistant"),
            lines.Count(line => line.Kind == "thinking"),
            lines.Count(line => line.Kind == "tool"),
            lines.Count(line => line.Kind == "error"),
            attachments.Count,
            agents.RunningCount,
            agents.Jobs.Count,
            mode,
            plan);
    }

    internal string Detail(string? usage)
    {
        var text = new StringBuilder();
        text.AppendLine($"消息 · 用户 {User} · 助手 {Assistant} · 思考 {Thinking}");
        text.AppendLine($"工具 {Tools} · 错误 {Errors} · 附件 {Attachments}");
        text.AppendLine($"子代理 · 运行 {RunningAgents} / 总计 {TotalAgents}");
        text.AppendLine($"执行模式 · {ExecutionMode}");
        text.AppendLine($"计划 · {(Plan.HasItems ? $"{Plan.Completed}/{Plan.Items.Count}" : "未提供")}");
        if (!string.IsNullOrWhiteSpace(usage)) text.AppendLine(usage.TrimEnd());
        return TerminalText.Clean(text.ToString()).TrimEnd();
    }
}
