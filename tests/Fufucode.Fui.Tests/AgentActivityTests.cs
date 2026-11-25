using System.Threading.Channels;
using Fufucode.App.Abstractions;

namespace Fufucode.Fui.Tests;

internal static class AgentActivityTests
{
    internal static void Run()
    {
        Lifecycle();
        TerminalStates();
        UnknownAndBounds();
        Ordering();
        Cleaning();
        Projection();
        Controller();
    }

    private static void Lifecycle()
    {
        var clock = new ManualClock(new(2026, 11, 25, 1, 0, 0, TimeSpan.Zero));
        var tracker = new FuiAgentActivityTracker(clock);
        Check(tracker.Current.Jobs.Count == 0, "empty tracker");
        Check(FuiAgentActivityRoster.RunningSummary(tracker.Current).Length == 0, "empty status");
        Check(FuiAgentActivityRoster.Choices(tracker.Current).Count == 0, "empty choices");

        var snapshot = tracker.Apply(new("job-1", "researcher", "started", "inspect repository"));
        var job = snapshot.Jobs.Single();
        Check(job.State == FuiAgentState.Running && job.ToolStarted == 0 && job.ToolCompleted == 0, "started state");
        Check(FuiAgentActivityRoster.RunningSummary(snapshot) == "子代理 1 个运行中", "running status");

        clock.Advance(TimeSpan.FromSeconds(1));
        snapshot = tracker.Apply(new("job-1", "researcher", "tool_start", "grep_search"));
        clock.Advance(TimeSpan.FromSeconds(1));
        snapshot = tracker.Apply(new("job-1", "researcher", "tool_start", "read_file"));
        clock.Advance(TimeSpan.FromSeconds(1));
        snapshot = tracker.Apply(new("job-1", "researcher", "tool_end", "read_file"));
        clock.Advance(TimeSpan.FromSeconds(1));
        snapshot = tracker.Apply(new("job-1", "researcher", "assistant", "found the cause"));
        job = snapshot.Jobs.Single();
        Check(job.State == FuiAgentState.Running && job.ToolStarted == 2 && job.ToolCompleted == 1, "tool counts");
        Check(job.LastDetail == "found the cause" && job.EventCount == 5, "recent detail and events");
        Check(job.FirstAt == new DateTimeOffset(2026, 11, 25, 1, 0, 0, TimeSpan.Zero) &&
            job.LastAt == new DateTimeOffset(2026, 11, 25, 1, 0, 4, TimeSpan.Zero), "first and last times");

        clock.Advance(TimeSpan.FromSeconds(1));
        snapshot = tracker.Apply(new("job-1", "researcher", "completed", null));
        job = snapshot.Jobs.Single();
        Check(job.State == FuiAgentState.Completed && job.EndedAt == job.LastAt, "completed state");
        Check(job.ToolStarted == 2 && job.ToolCompleted == 1 && job.EventCount == 6, "completed keeps counts");
        Check(FuiAgentActivityRoster.RunningSummary(snapshot).Length == 0, "settled leaves running status");
    }

    private static void TerminalStates()
    {
        var tracker = new FuiAgentActivityTracker(new ManualClock(DateTimeOffset.UnixEpoch));
        tracker.Apply(new("failed", "reviewer", "started", "review"));
        var failed = tracker.Apply(new("failed", "reviewer", "failed", "compile error")).Jobs.Single();
        Check(failed.State == FuiAgentState.Failed && failed.LastDetail == "compile error" && failed.EndedAt is not null, "failed state");

        tracker.Apply(new("cancelled", "worker", "started", "work"));
        var cancelled = tracker.Apply(new("cancelled", "worker", "cancelled", "stopped by parent")).Jobs
            .Single(job => job.JobId == "cancelled");
        Check(cancelled.State == FuiAgentState.Cancelled && cancelled.LastDetail == "stopped by parent", "cancelled state");

        tracker.Apply(new("truncated", "worker", "started", "work"));
        var truncated = tracker.Apply(new("truncated", "worker", "truncated", "turn limit")).Jobs
            .Single(job => job.JobId == "truncated");
        Check(truncated.State == FuiAgentState.Truncated && truncated.LastDetail == "turn limit", "truncated state");
    }

    private static void UnknownAndBounds()
    {
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        var tracker = new FuiAgentActivityTracker(clock);
        var snapshot = tracker.Apply(new("unknown", "observer", "custom_progress", "ordinary detail"));
        var job = snapshot.Jobs.Single();
        Check(job.State == FuiAgentState.Unknown, "unknown kind must not invent a status");
        Check(job.Events.Single() is { Kind: "custom_progress", Detail: "ordinary detail" }, "unknown kind kept as event");

        tracker.Apply(new("running", "observer", "started", "start"));
        snapshot = tracker.Apply(new("running", "observer", "heartbeat", "still ordinary"));
        job = snapshot.Jobs.Single(item => item.JobId == "running");
        Check(job.State == FuiAgentState.Running && job.Events[^1].Kind == "heartbeat", "unknown kind preserves running state");

        for (var i = 0; i < 40; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(10));
            snapshot = tracker.Apply(new("bounded", "observer", "custom", i.ToString()));
        }
        job = snapshot.Jobs.Single(item => item.JobId == "bounded");
        Check(job.EventCount == 40 && job.Events.Count == FuiAgentActivityTracker.MaxEventsPerJob, "event list bound");
        Check(job.Events[0].Detail == "8" && job.Events[^1].Detail == "39", "newest events retained");
        Check(FuiAgentActivityRoster.Detail(job).Contains("仅保留最近 32 条，共 40 条", StringComparison.Ordinal), "bound is truthful");
    }

    private static void Ordering()
    {
        var clock = new ManualClock(new(2026, 11, 25, 2, 0, 0, TimeSpan.Zero));
        var tracker = new FuiAgentActivityTracker(clock);
        tracker.Apply(new("run-old", "one", "started", null));
        clock.Advance(TimeSpan.FromSeconds(1));
        tracker.Apply(new("run-new", "two", "started", null));
        clock.Advance(TimeSpan.FromSeconds(1));
        tracker.Apply(new("run-new", "two", "assistant", "newest running"));
        clock.Advance(TimeSpan.FromSeconds(1));
        tracker.Apply(new("done-old", "three", "started", null));
        clock.Advance(TimeSpan.FromSeconds(1));
        tracker.Apply(new("done-old", "three", "completed", null));
        clock.Advance(TimeSpan.FromSeconds(1));
        tracker.Apply(new("done-new", "four", "started", null));
        clock.Advance(TimeSpan.FromSeconds(1));
        var snapshot = tracker.Apply(new("done-new", "four", "completed", null));
        Check(string.Join(',', snapshot.Jobs.Select(job => job.JobId)) == "run-new,run-old,done-new,done-old",
            "running first, then most recently active");
    }

    private static void Cleaning()
    {
        var tracker = new FuiAgentActivityTracker(new ManualClock(DateTimeOffset.UnixEpoch));
        var snapshot = tracker.Apply(new(
            "\x1b[31mjob\x1b[0m\t1",
            "agent\x1b]0;title\a\nname",
            "started",
            "line one\x1b[2Jline two\a"));
        var job = snapshot.Jobs.Single();
        Check(NoControl(job.JobId) && NoControl(job.Agent), "identity cleaned");
        Check(job.Events.Single().Kind == "started" && NoControl(job.Events.Single().Detail), "event cleaned");
        var detail = FuiAgentActivityRoster.Detail(job);
        Check(!detail.Contains('\x1b') && !detail.Contains('\a') && !detail.Contains('\u009b'), "detail cleaned");
        var choice = FuiAgentActivityRoster.Choices(snapshot).Single();
        Check(NoControl(choice.Value) && NoControl(choice.Label) && NoControl(choice.Detail!), "choice cleaned");
    }

    private static void Projection()
    {
        var channel = Channel.CreateUnbounded<TuiEvent>();
        var projection = new FumoEventProjection(channel.Writer);
        projection.OnEvent(null, new SessionEvent.SubagentUpdated("route-job", "router", "started", "route detail"));
        Check(channel.Reader.TryRead(out var first) && first is TuiEvent.AgentsChanged
        {
            Snapshot.Jobs: var jobs
        } && jobs.Count == 1 && jobs[0] is
        {
            JobId: "route-job",
            Agent: "router",
            State: FuiAgentState.Running,
            LastDetail: "route detail"
        }, "real SubagentUpdated projected into roster");

        projection.OnEvent(null, new SessionEvent.SubagentUpdated("route-job", "router", "custom", "preserved"));
        Check(channel.Reader.TryRead(out var second) && second is TuiEvent.AgentsChanged
        {
            Snapshot.Jobs: var updated
        } && updated.Single() is { State: FuiAgentState.Running, Events.Count: 2 }, "unknown kind reaches roster");
    }

    private static void Controller()
    {
        var session = new Session();
        var ui = new TuiController(session, CancellationToken.None);
        void Type(string text)
        {
            foreach (var ch in text) ui.Key(new(ch, (ConsoleKey)0, false, false, false), 12, 25);
        }
        void Key(ConsoleKey key, bool ctrl = false) => ui.Key(new('\0', key, false, false, ctrl), 12, 25);
        void Command(string command) { Type("/" + command); Key(ConsoleKey.Enter); }
        void Emit(TuiEvent item) { session.Channel.Writer.TryWrite(item); ui.Update(); }
        void Paint() => ui.Painted(25, ui.View().Approval is not null);

        try
        {
            Command("agents");
            Check(ui.View() is { Overlay: null, Lines: var empty } &&
                empty[^1] is { Kind: "info", Text: "尚无子代理活动" }, "empty agents state");

            var tracker = new FuiAgentActivityTracker(new ManualClock(DateTimeOffset.UnixEpoch));
            tracker.Apply(new("visible", "worker", "started", "inspect"));
            tracker.Apply(new("visible", "worker", "tool_start", "grep_search"));
            var running = tracker.Apply(new("visible", "worker", "tool_end", "grep_search"));
            Emit(new TuiEvent.AgentsChanged(running));
            Check(ui.View().Status.StartsWith("子代理 1 个运行中", StringComparison.Ordinal), "running activity visible in status");

            Command("agents");
            Check(ui.View().Overlay is { Title: "子代理活动 · 只读", Items.Count: 1 }, "agent selector opens");
            Key(ConsoleKey.Enter);
            Check(ui.View() is { Overlay: null, Lines: var detail } &&
                detail[^1] is { Kind: "info", Text: var text } &&
                text.Contains("运行中 · worker", StringComparison.Ordinal) &&
                text.Contains("最近事件", StringComparison.Ordinal) &&
                text.Contains("tool_start", StringComparison.Ordinal) &&
                text.Contains("tool_end", StringComparison.Ordinal), "read-only event detail");

            var completed = tracker.Apply(new("visible", "worker", "completed", "done"));
            Emit(new TuiEvent.AgentsChanged(completed));
            Check(!ui.View().Status.Contains("子代理 1 个运行中", StringComparison.Ordinal), "settled activity leaves running status");

            var pending = tracker.Apply(new("approval", "worker", "started", "approval overlap"));
            Emit(new TuiEvent.AgentsChanged(pending));
            Command("agents");
            Check(ui.View().Overlay is not null, "agent picker before approval");
            Emit(new TuiEvent.Approval("approval-1", "write_file\n{\"path\":\"fixture\"}"));
            Check(ui.View() is { Approval: not null, Overlay: null }, "approval preempts agent picker");
            Paint();
            Key(ConsoleKey.F3);
            ui.Update();
            Check(session.Answers.SequenceEqual([("approval-1", false)]), "approval still rejects");
            Emit(new TuiEvent.ApprovalClosed("approval-1"));
            Paint();
            Command("agents");
            Check(ui.View().Overlay is not null, "agent picker reopens after approval");
            Key(ConsoleKey.Escape);
        }
        finally
        {
            ui.FinishAsync().GetAwaiter().GetResult();
        }
    }

    private static bool NoControl(string text) => !text.Any(char.IsControl);

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        internal void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed class Session : ITuiSession
    {
        public string Project => "project";
        public string Id => "session";
        public string? Model => "fixture";
        public IReadOnlyList<TuiLine> History => [];
        public Channel<TuiEvent> Channel { get; } = System.Threading.Channels.Channel.CreateUnbounded<TuiEvent>();
        public ChannelReader<TuiEvent> Events => Channel.Reader;
        public List<(string, bool)> Answers { get; } = [];
        public Task SendAsync(string text, CancellationToken ct) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
        public Task RespondAsync(string id, bool allow, CancellationToken ct)
        {
            Answers.Add((id, allow));
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
