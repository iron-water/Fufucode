using System.Globalization;
using System.Text;
using Fufucode.App.Abstractions;

namespace Fufucode.Fui;

internal enum FuiAgentState
{
    Unknown,
    Running,
    Completed,
    Failed,
    Cancelled,
    Truncated
}

internal sealed record FuiAgentActivityEvent(DateTimeOffset At, string Kind, string Detail);

internal sealed record FuiAgentJob(
    string JobId,
    string Agent,
    FuiAgentState State,
    string LastDetail,
    DateTimeOffset FirstAt,
    DateTimeOffset LastAt,
    DateTimeOffset? EndedAt,
    int ToolStarted,
    int ToolCompleted,
    int EventCount,
    IReadOnlyList<FuiAgentActivityEvent> Events);

internal sealed record FuiAgentActivitySnapshot(IReadOnlyList<FuiAgentJob> Jobs, DateTimeOffset At)
{
    internal static FuiAgentActivitySnapshot Empty { get; } = new(Array.Empty<FuiAgentJob>(), DateTimeOffset.MinValue);

    internal int RunningCount => Jobs.Count(job => job.State == FuiAgentState.Running);

    internal FuiAgentJob? Find(string jobId) =>
        Jobs.FirstOrDefault(job => string.Equals(job.JobId, jobId, StringComparison.Ordinal));
}

/// <summary>Projects the real SubagentUpdated event stream into a bounded in-memory roster.</summary>
internal sealed class FuiAgentActivityTracker
{
    internal const int MaxEventsPerJob = 32;

    private readonly Dictionary<string, MutableJob> _jobs = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;
    private readonly object _gate = new();

    internal FuiAgentActivityTracker(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

    internal FuiAgentActivitySnapshot Current
    {
        get
        {
            lock (_gate) return Snapshot(_clock.GetUtcNow());
        }
    }

    internal FuiAgentActivitySnapshot Apply(SessionEvent.SubagentUpdated update)
    {
        var at = _clock.GetUtcNow();
        var jobId = TerminalText.SingleLine(update.JobId);
        var agent = TerminalText.SingleLine(update.Agent);
        var kind = TerminalText.SingleLine(update.Kind);
        var detail = TerminalText.SingleLine(update.Detail ?? "");

        lock (_gate)
        {
            if (!_jobs.TryGetValue(jobId, out var job))
                _jobs.Add(jobId, job = new MutableJob(jobId, agent, at));
            else if (agent.Length != 0)
                job.Agent = agent;

            job.LastAt = at;
            if (detail.Length != 0) job.LastDetail = detail;
            switch (kind)
            {
                case "started":
                    job.State = FuiAgentState.Running;
                    job.EndedAt = null;
                    break;
                case "tool_start":
                    job.ToolStarted++;
                    if (job.State is FuiAgentState.Unknown or FuiAgentState.Running) job.State = FuiAgentState.Running;
                    break;
                case "tool_end":
                    job.ToolCompleted++;
                    if (job.State is FuiAgentState.Unknown or FuiAgentState.Running) job.State = FuiAgentState.Running;
                    break;
                case "assistant":
                    if (job.State is FuiAgentState.Unknown or FuiAgentState.Running) job.State = FuiAgentState.Running;
                    break;
                case "completed":
                    job.State = FuiAgentState.Completed;
                    job.EndedAt ??= at;
                    break;
                case "failed":
                    job.State = FuiAgentState.Failed;
                    job.EndedAt ??= at;
                    break;
                case "cancelled":
                    job.State = FuiAgentState.Cancelled;
                    job.EndedAt ??= at;
                    break;
                case "truncated":
                    job.State = FuiAgentState.Truncated;
                    job.EndedAt ??= at;
                    break;
            }

            job.EventCount++;
            job.Events.Add(new(at, kind, detail));
            if (job.Events.Count > MaxEventsPerJob) job.Events.RemoveAt(0);
            return Snapshot(at);
        }
    }

    private FuiAgentActivitySnapshot Snapshot(DateTimeOffset at)
    {
        var jobs = _jobs.Values
            .OrderBy(job => StateOrder(job.State))
            .ThenByDescending(job => job.LastAt)
            .ThenByDescending(job => job.FirstAt)
            .ThenBy(job => job.JobId, StringComparer.Ordinal)
            .Select(job => job.ToImmutable())
            .ToArray();
        return new(Array.AsReadOnly(jobs), at);
    }

    private static int StateOrder(FuiAgentState state) => state switch
    {
        FuiAgentState.Running => 0,
        FuiAgentState.Unknown => 1,
        FuiAgentState.Failed => 2,
        FuiAgentState.Truncated => 3,
        FuiAgentState.Cancelled => 4,
        _ => 5
    };

    private sealed class MutableJob(string jobId, string agent, DateTimeOffset firstAt)
    {
        internal string JobId { get; } = jobId;
        internal string Agent { get; set; } = agent;
        internal FuiAgentState State { get; set; }
        internal string LastDetail { get; set; } = "";
        internal DateTimeOffset FirstAt { get; } = firstAt;
        internal DateTimeOffset LastAt { get; set; } = firstAt;
        internal DateTimeOffset? EndedAt { get; set; }
        internal int ToolStarted { get; set; }
        internal int ToolCompleted { get; set; }
        internal int EventCount { get; set; }
        internal List<FuiAgentActivityEvent> Events { get; } = [];

        internal FuiAgentJob ToImmutable() => new(
            JobId, Agent, State, LastDetail, FirstAt, LastAt, EndedAt,
            ToolStarted, ToolCompleted, EventCount, Array.AsReadOnly(Events.ToArray()));
    }
}

/// <summary>Pure presentation helpers for the agent roster and its read-only detail.</summary>
internal static class FuiAgentActivityRoster
{
    internal static IReadOnlyList<FuiChoice> Choices(FuiAgentActivitySnapshot snapshot) =>
        Groups(snapshot)
            .SelectMany((group, groupIndex) => new[]
            {
                new FuiChoice(
                    GroupValuePrefix + groupIndex.ToString(CultureInfo.InvariantCulture),
                    $"AGENT · {group.Agent} · 运行 {group.Jobs.Count(job => job.State == FuiAgentState.Running)}/{group.Jobs.Count}",
                    GroupDetail(group.Jobs))
            }.Concat(group.Jobs.Select(job => JobChoice(snapshot, job))))
            .ToArray();

    internal const string GroupValuePrefix = "agent-group:";
    internal const string HubTitle = "子代理活动 · 只读";

    internal static bool IsHubTitle(string title) =>
        string.Equals(title, HubTitle, StringComparison.Ordinal);

    internal static bool IsJobViewTitle(string title) =>
        IsHubTitle(title) || FuiJobsWorkbench.IsTitle(title);

    internal static bool IsGroupChoice(FuiChoice choice) =>
        choice.Value.StartsWith(GroupValuePrefix, StringComparison.Ordinal) ||
        choice.Value.StartsWith(FuiJobsWorkbench.GroupPrefix, StringComparison.Ordinal);

    internal static Ink InkFor(FuiChoice choice)
    {
        if (IsGroupChoice(choice)) return Ink.PanelTitle;
        var label = TerminalText.SingleLine(choice.Label);
        if (label.StartsWith(StateGlyph(FuiAgentState.Running), StringComparison.Ordinal)) return Ink.Header;
        if (label.StartsWith(StateGlyph(FuiAgentState.Failed), StringComparison.Ordinal)) return Ink.Error;
        if (label.StartsWith(StateGlyph(FuiAgentState.Truncated), StringComparison.Ordinal)) return Ink.Heading;
        if (label.StartsWith(StateGlyph(FuiAgentState.Unknown), StringComparison.Ordinal) ||
            label.StartsWith(StateGlyph(FuiAgentState.Cancelled), StringComparison.Ordinal))
            return Ink.Quiet;
        return Ink.Plain;
    }

    private static FuiChoice JobChoice(FuiAgentActivitySnapshot snapshot, FuiAgentJob job)
    {
        return new FuiChoice(
            job.JobId,
            $"{StateGlyph(job.State)} {StateLabel(job.State)} · {JobIdLabel(job.JobId)}",
            JobSummary(snapshot, job));
    }

    internal static string JobSummary(FuiAgentActivitySnapshot snapshot, FuiAgentJob job) =>
        Join(" · ",
            $"工具 开始 {job.ToolStarted} / 完成 {job.ToolCompleted}",
            $"{DurationLabel(job)} {Duration(snapshot, job)}",
            RecentActivity(job),
            AgentLabel(job.Agent));

    internal static string Detail(FuiAgentActivitySnapshot snapshot, FuiAgentJob job)
    {
        var text = new StringBuilder();
        text.AppendLine($"{StateGlyph(job.State)} {StateLabel(job.State)} · {AgentLabel(job.Agent)}");
        text.AppendLine($"作业 {JobIdLabel(job.JobId)} · 工具 开始 {job.ToolStarted} / 完成 {job.ToolCompleted} · 事件 {job.EventCount}");
        text.AppendLine($"开始 {Time(job.FirstAt)} · 最近 {Time(job.LastAt)} · {DurationLabel(job)} {Duration(snapshot, job)}");
        text.AppendLine("最近事件 · 新 → 旧");
        if (job.Events.Count == 0)
        {
            text.AppendLine("尚无事件");
        }
        else
        {
            var firstIndex = Math.Max(0, job.EventCount - job.Events.Count);
            for (var i = job.Events.Count - 1; i >= 0; i--)
            {
                var item = job.Events[i];
                var kind = TerminalText.SingleLine(item.Kind) is { Length: > 0 } value ? value : "未提供类型";
                var detail = TerminalText.SingleLine(item.Detail);
                text.AppendLine($"{firstIndex + i + 1:00}  {Time(item.At)}  {kind}" +
                    (detail.Length == 0 ? "" : " · " + detail));
            }
            if (job.EventCount > job.Events.Count)
                text.AppendLine($"仅保留最近 {job.Events.Count} 条，共 {job.EventCount} 条");
        }
        return TerminalText.Clean(text.ToString()).TrimEnd();
    }

    internal static string Detail(FuiAgentJob job) => Detail(
        new FuiAgentActivitySnapshot([job], job.EndedAt ?? job.LastAt), job);

    internal static string RunningSummary(FuiAgentActivitySnapshot snapshot) => snapshot.RunningCount switch
    {
        <= 0 => "",
        1 => "子代理 1 个运行中",
        var count => $"子代理 {count} 个运行中"
    };

    internal static string StateLabel(FuiAgentState state) => state switch
    {
        FuiAgentState.Running => "运行中",
        FuiAgentState.Completed => "已完成",
        FuiAgentState.Failed => "失败",
        FuiAgentState.Cancelled => "已取消",
        FuiAgentState.Truncated => "已截断",
        _ => "状态未知"
    };

    private static IReadOnlyList<AgentGroup> Groups(FuiAgentActivitySnapshot snapshot) =>
        snapshot.Jobs
            .GroupBy(job => AgentLabel(job.Agent), StringComparer.Ordinal)
            .Select(group =>
            {
                var jobs = group
                    .OrderBy(job => StateOrder(job.State))
                    .ThenByDescending(job => job.LastAt)
                    .ThenByDescending(job => job.FirstAt)
                    .ThenBy(job => job.JobId, StringComparer.Ordinal)
                    .ToArray();
                return new AgentGroup(
                    group.Key,
                    jobs,
                    StateOrder(jobs[0].State),
                    jobs.Max(job => job.LastAt));
            })
            .OrderBy(group => group.StateOrder)
            .ThenByDescending(group => group.LastAt)
            .ThenBy(group => group.Agent, StringComparer.Ordinal)
            .ToArray();

    private static string GroupDetail(IReadOnlyList<FuiAgentJob> jobs)
    {
        var running = jobs.Count(job => job.State == FuiAgentState.Running);
        return Join(" · ",
            $"{jobs.Count} 个作业",
            running == 0 ? "无运行中" : $"{running} 个运行中",
            $"{jobs.Sum(job => job.ToolStarted)} 次工具开始",
            $"{jobs.Sum(job => job.ToolCompleted)} 次工具完成");
    }

    private static string RecentActivity(FuiAgentJob job)
    {
        if (job.Events.Count == 0) return "尚无事件";
        var latest = job.Events[^1];
        var kind = TerminalText.SingleLine(latest.Kind);
        var detail = TerminalText.SingleLine(latest.Detail);
        var text = kind.Length == 0
            ? detail.Length == 0 ? "最近活动未提供" : detail
            : detail.Length == 0 ? kind : kind + " · " + detail;
        return text == "最近活动未提供" ? text : "最近 " + text;
    }

    internal static int StateOrder(FuiAgentState state) => state switch
    {
        FuiAgentState.Running => 0,
        FuiAgentState.Unknown => 1,
        FuiAgentState.Failed => 2,
        FuiAgentState.Truncated => 3,
        FuiAgentState.Cancelled => 4,
        _ => 5
    };

    internal static string StateGlyph(FuiAgentState state) => state switch
    {
        FuiAgentState.Running => "●",
        FuiAgentState.Completed => "✓",
        FuiAgentState.Failed => "×",
        FuiAgentState.Cancelled => "⊘",
        FuiAgentState.Truncated => "…",
        _ => "?"
    };

    private static string AgentLabel(string agent)
    {
        var clean = TerminalText.SingleLine(agent);
        return clean.Length == 0 ? "未提供代理" : clean;
    }

    private static string JobIdLabel(string jobId)
    {
        var clean = TerminalText.SingleLine(jobId);
        return clean.Length == 0 ? "未提供 ID" : clean;
    }

    private static string Time(DateTimeOffset at) => at.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    private static string Duration(FuiAgentActivitySnapshot snapshot, FuiAgentJob job)
    {
        var end = job.State is FuiAgentState.Running or FuiAgentState.Unknown
            ? snapshot.At
            : job.EndedAt ?? job.LastAt;
        var elapsed = end - job.FirstAt;
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        if (elapsed.TotalHours >= 1) return $"{(int)elapsed.TotalHours}时{elapsed.Minutes:00}分";
        if (elapsed.TotalMinutes >= 1) return $"{(int)elapsed.TotalMinutes}分{elapsed.Seconds:00}秒";
        return elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "秒";
    }

    private static string DurationLabel(FuiAgentJob job) => job.State switch
    {
        FuiAgentState.Running => "已运行",
        FuiAgentState.Unknown => "已观察",
        _ => "耗时"
    };

    private static string Join(string separator, params string[] values) =>
        string.Join(separator, values.Where(value => value.Length != 0));

    private sealed record AgentGroup(string Agent, IReadOnlyList<FuiAgentJob> Jobs, int StateOrder, DateTimeOffset LastAt);
}
