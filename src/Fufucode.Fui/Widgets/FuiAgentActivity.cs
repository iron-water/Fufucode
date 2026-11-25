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

internal sealed record FuiAgentActivitySnapshot(IReadOnlyList<FuiAgentJob> Jobs)
{
    internal static FuiAgentActivitySnapshot Empty { get; } = new(Array.Empty<FuiAgentJob>());

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
            lock (_gate) return Snapshot();
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
            return Snapshot();
        }
    }

    private FuiAgentActivitySnapshot Snapshot()
    {
        var jobs = _jobs.Values
            .OrderBy(job => StateOrder(job.State))
            .ThenByDescending(job => job.LastAt)
            .ThenByDescending(job => job.FirstAt)
            .ThenBy(job => job.JobId, StringComparer.Ordinal)
            .Select(job => job.ToImmutable())
            .ToArray();
        return new(Array.AsReadOnly(jobs));
    }

    private static int StateOrder(FuiAgentState state) => state switch
    {
        FuiAgentState.Running => 0,
        FuiAgentState.Unknown => 1,
        _ => 2
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
        snapshot.Jobs.Select(job =>
        {
            var latest = job.Events.Count == 0 ? "" : job.Events[^1].Kind;
            var activity = string.Join(" · ", new[] { latest, job.LastDetail }.Where(value => value.Length != 0));
            var summary = string.Join(" · ", new[]
            {
                activity,
                $"工具 {job.ToolStarted}/{job.ToolCompleted}",
                Duration(job),
                JobIdLabel(job.JobId)
            }.Where(value => value.Length != 0));
            return new FuiChoice(job.JobId, $"{StateLabel(job.State)} · {AgentLabel(job.Agent)}", summary);
        }).ToArray();

    internal static string Detail(FuiAgentJob job)
    {
        var text = new StringBuilder();
        text.AppendLine($"{StateLabel(job.State)} · {AgentLabel(job.Agent)}");
        text.AppendLine($"作业 {JobIdLabel(job.JobId)} · 工具开始 {job.ToolStarted} · 工具结束 {job.ToolCompleted}");
        text.AppendLine($"时间 {Time(job.FirstAt)} → {Time(job.LastAt)} · {Duration(job)}");
        text.AppendLine("最近事件");
        var firstIndex = Math.Max(0, job.EventCount - job.Events.Count);
        for (var i = 0; i < job.Events.Count; i++)
        {
            var item = job.Events[i];
            var kind = item.Kind.Length == 0 ? "未提供类型" : item.Kind;
            var detail = item.Detail.Length == 0 ? "" : " · " + item.Detail;
            text.AppendLine($"{firstIndex + i + 1}. {Time(item.At)} {kind}{detail}");
        }
        if (job.EventCount > job.Events.Count)
            text.AppendLine($"仅保留最近 {job.Events.Count} 条，共 {job.EventCount} 条");
        return TerminalText.Clean(text.ToString()).TrimEnd();
    }

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
        FuiAgentState.Truncated => "结果不完整",
        _ => "状态未知"
    };

    private static string AgentLabel(string agent) => agent.Length == 0 ? "未提供代理" : agent;
    private static string JobIdLabel(string jobId) => jobId.Length == 0 ? "未提供 ID" : jobId;
    private static string Time(DateTimeOffset at) => at.ToLocalTime().ToString("HH:mm:ss");

    private static string Duration(FuiAgentJob job)
    {
        var elapsed = (job.EndedAt ?? job.LastAt) - job.FirstAt;
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        if (elapsed.TotalHours >= 1) return $"{(int)elapsed.TotalHours}时{elapsed.Minutes:00}分";
        if (elapsed.TotalMinutes >= 1) return $"{(int)elapsed.TotalMinutes}分{elapsed.Seconds:00}秒";
        return $"{elapsed.TotalSeconds:0.0}秒";
    }
}
