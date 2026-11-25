using System.Threading.Channels;
using Fufucode.App.Abstractions;

namespace Fufucode.Fui.Tests;

internal static class AgentActivityTests
{
    internal static void Run()
    {
        Empty();
        LifecycleAndCounts();
        TerminalTransitions();
        TimelineAndBounds();
        Ordering();
        Cleaning();
        Projection();
        Controller();
    }

    private static void Empty()
    {
        var now = new DateTimeOffset(2026, 11, 25, 1, 0, 0, TimeSpan.Zero);
        var tracker = new FuiAgentActivityTracker(new ManualClock(now));
        Check(tracker.Current.Jobs.Count == 0 && tracker.Current.At == now, "empty snapshot");
        Check(FuiAgentActivityRoster.RunningSummary(tracker.Current).Length == 0, "empty status");
        Check(FuiAgentActivityRoster.Choices(tracker.Current).Count == 0, "empty choices");

        var job = new FuiAgentJob("quiet", "observer", FuiAgentState.Unknown, "", now, now, null,
            0, 0, 0, Array.Empty<FuiAgentActivityEvent>());
        var snapshot = new FuiAgentActivitySnapshot([job], now.AddSeconds(4));
        Check(FuiAgentActivityRoster.Detail(snapshot, job).Contains("尚无事件", StringComparison.Ordinal),
            "no-event detail is explicit");
        Check(FuiAgentActivityRoster.Choices(snapshot).Single(choice => !FuiAgentActivityRoster.IsGroupChoice(choice))
            .Detail!.Contains("尚无事件", StringComparison.Ordinal), "no-event roster is explicit");
        Check(FuiAgentActivityRoster.Detail(snapshot, job).Contains("已观察 4.0秒", StringComparison.Ordinal),
            "unknown duration uses the snapshot observation time without inventing a terminal state");
    }

    private static void LifecycleAndCounts()
    {
        var clock = new ManualClock(new(2026, 11, 25, 1, 0, 0, TimeSpan.Zero));
        var tracker = new FuiAgentActivityTracker(clock);
        var snapshot = tracker.Apply(new("job-1", "researcher", "started", "inspect repository"));
        var job = snapshot.Jobs.Single();
        Check(job.State == FuiAgentState.Running && job.ToolStarted == 0 && job.ToolCompleted == 0, "started state");
        Check(FuiAgentActivityRoster.RunningSummary(snapshot) == "子代理 1 个运行中", "running status");

        clock.Advance(TimeSpan.FromSeconds(1));
        tracker.Apply(new("job-1", "researcher", "tool_start", "grep_search"));
        clock.Advance(TimeSpan.FromSeconds(1));
        tracker.Apply(new("job-1", "researcher", "tool_start", "read_file"));
        clock.Advance(TimeSpan.FromSeconds(1));
        snapshot = tracker.Apply(new("job-1", "researcher", "tool_end", "read_file"));
        clock.Advance(TimeSpan.FromSeconds(1));
        snapshot = tracker.Apply(new("job-1", "researcher", "assistant", "found the cause"));
        job = snapshot.Jobs.Single();
        Check(job.State == FuiAgentState.Running && job.ToolStarted == 2 && job.ToolCompleted == 1, "tool counts");
        Check(job.LastDetail == "found the cause" && job.EventCount == 5, "recent detail and events");
        Check(job.FirstAt == new DateTimeOffset(2026, 11, 25, 1, 0, 0, TimeSpan.Zero) &&
            job.LastAt == new DateTimeOffset(2026, 11, 25, 1, 0, 4, TimeSpan.Zero), "first and last times");

        clock.Advance(TimeSpan.FromSeconds(2));
        var current = tracker.Current;
        var roster = FuiAgentActivityRoster.Choices(current)
            .Single(choice => !FuiAgentActivityRoster.IsGroupChoice(choice));
        Check(roster.Label.Contains("● 运行中", StringComparison.Ordinal), "running job has a status glyph and word");
        Check(roster.Detail!.Contains("工具 开始 2 / 完成 1", StringComparison.Ordinal), "roster shows tool counts");
        Check(roster.Detail.Contains("assistant · found the cause", StringComparison.Ordinal), "roster shows recent activity");
        Check(roster.Detail.Contains("已运行 6.0秒", StringComparison.Ordinal), "running duration advances with the clock");

        clock.Advance(TimeSpan.FromSeconds(1));
        snapshot = tracker.Apply(new("job-1", "researcher", "completed", null));
        job = snapshot.Jobs.Single();
        Check(job.State == FuiAgentState.Completed && job.EndedAt == job.LastAt, "completed state");
        Check(job.ToolStarted == 2 && job.ToolCompleted == 1 && job.EventCount == 6, "completed keeps counts");
        Check(FuiAgentActivityRoster.RunningSummary(snapshot).Length == 0, "settled leaves running status");
    }

    private static void TerminalTransitions()
    {
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        var tracker = new FuiAgentActivityTracker(clock);

        tracker.Apply(new("failed", "reviewer", "started", "review"));
        var failed = tracker.Apply(new("failed", "reviewer", "failed", "compile error")).Jobs.Single();
        Check(failed.State == FuiAgentState.Failed && failed.LastDetail == "compile error" && failed.EndedAt is not null,
            "running to failed");

        tracker.Apply(new("cancelled", "worker", "started", "work"));
        var cancelled = tracker.Apply(new("cancelled", "worker", "cancelled", "stopped by parent")).Jobs
            .Single(job => job.JobId == "cancelled");
        Check(cancelled.State == FuiAgentState.Cancelled && cancelled.LastDetail == "stopped by parent",
            "running to cancelled");

        tracker.Apply(new("truncated", "worker", "started", "work"));
        var truncated = tracker.Apply(new("truncated", "worker", "truncated", "turn limit")).Jobs
            .Single(job => job.JobId == "truncated");
        Check(truncated.State == FuiAgentState.Truncated && truncated.LastDetail == "turn limit", "truncated state");

        tracker.Apply(new("unknown", "observer", "custom_progress", "ordinary detail"));
        var unknown = tracker.Apply(new("unknown", "observer", "heartbeat", "still ordinary")).Jobs
            .Single(job => job.JobId == "unknown");
        Check(unknown.State == FuiAgentState.Unknown, "unknown kinds do not invent a lifecycle state");

        tracker.Apply(new("running-unknown", "observer", "started", "start"));
        var runningUnknown = tracker.Apply(new("running-unknown", "observer", "heartbeat", "still running")).Jobs
            .Single(job => job.JobId == "running-unknown");
        Check(runningUnknown.State == FuiAgentState.Running &&
            runningUnknown.Events[^1] is { Kind: "heartbeat", Detail: "still running" },
            "unknown kinds preserve a real running state");
    }

    private static void TimelineAndBounds()
    {
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        var tracker = new FuiAgentActivityTracker(clock);
        tracker.Apply(new("timeline", "observer", "started", "start"));
        clock.Advance(TimeSpan.FromSeconds(1));
        tracker.Apply(new("timeline", "observer", "tool_start", "grep_search"));
        clock.Advance(TimeSpan.FromSeconds(1));
        tracker.Apply(new("timeline", "observer", "tool_end", "grep_search"));
        clock.Advance(TimeSpan.FromSeconds(1));
        var snapshot = tracker.Apply(new("timeline", "observer", "assistant", "finished scan"));
        var job = snapshot.Jobs.Single();
        var detail = FuiAgentActivityRoster.Detail(snapshot, job);
        Check(detail.Contains("工具 开始 1 / 完成 1 · 事件 4", StringComparison.Ordinal), "detail shows counts");
        Check(detail.Contains("最近事件 · 新 → 旧", StringComparison.Ordinal), "timeline direction is explicit");
        Check(detail.IndexOf("assistant", StringComparison.Ordinal) <
            detail.IndexOf("tool_end", StringComparison.Ordinal), "newest activity appears first");

        for (var i = 0; i < 40; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(10));
            snapshot = tracker.Apply(new("bounded", "observer", "custom", i.ToString()));
        }
        job = snapshot.Jobs.Single(item => item.JobId == "bounded");
        Check(job.EventCount == 40 && job.Events.Count == FuiAgentActivityTracker.MaxEventsPerJob, "event list bound");
        Check(job.Events[0].Detail == "8" && job.Events[^1].Detail == "39", "newest events retained");
        Check(FuiAgentActivityRoster.Detail(snapshot, job)
            .Contains("仅保留最近 32 条，共 40 条", StringComparison.Ordinal), "bound is truthful");
    }

    private static void Ordering()
    {
        var clock = new ManualClock(new(2026, 11, 25, 2, 0, 0, TimeSpan.Zero));
        var tracker = new FuiAgentActivityTracker(clock);
        tracker.Apply(new("beta-run", "beta", "started", null));
        clock.Advance(TimeSpan.FromSeconds(1));
        tracker.Apply(new("alpha-old", "alpha", "started", null));
        clock.Advance(TimeSpan.FromSeconds(1));
        tracker.Apply(new("alpha-old", "alpha", "completed", null));
        clock.Advance(TimeSpan.FromSeconds(1));
        tracker.Apply(new("beta-done", "beta", "started", null));
        clock.Advance(TimeSpan.FromSeconds(1));
        tracker.Apply(new("beta-done", "beta", "completed", null));
        clock.Advance(TimeSpan.FromSeconds(1));
        tracker.Apply(new("alpha-fail", "alpha", "started", null));
        clock.Advance(TimeSpan.FromSeconds(1));
        tracker.Apply(new("alpha-fail", "alpha", "failed", null));
        clock.Advance(TimeSpan.FromSeconds(1));
        tracker.Apply(new("gamma-unknown", "gamma", "custom", null));
        clock.Advance(TimeSpan.FromSeconds(1));
        var snapshot = tracker.Apply(new("beta-run", "beta", "assistant", "newest running"));

        var choices = FuiAgentActivityRoster.Choices(snapshot);
        Check(string.Join(',', choices.Select(choice => choice.Value)) ==
            "agent-group:0,beta-run,beta-done,agent-group:1,gamma-unknown,agent-group:2,alpha-fail,alpha-old",
            "running group first; jobs stay with their agent; running/failed jobs outrank settled jobs");
        Check(choices[0].Label.StartsWith("AGENT · beta", StringComparison.Ordinal) &&
            choices[0].Detail!.Contains("1 个运行中", StringComparison.Ordinal),
            "agent group summarizes status");
        Check(choices[1].Label.Contains("● 运行中", StringComparison.Ordinal) &&
            choices[2].Label.Contains("✓ 已完成", StringComparison.Ordinal), "job rows expose real lifecycle states");
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
        var detail = FuiAgentActivityRoster.Detail(snapshot, job);
        Check(NoUnsafeControl(detail), "detail cleaned");
        var choices = FuiAgentActivityRoster.Choices(snapshot);
        Check(choices.All(choice => NoControl(choice.Value) && NoControl(choice.Label) &&
            NoControl(choice.Detail ?? "")), "all roster fields cleaned");
        var picker = new FuiPicker(FuiAgentActivityRoster.HubTitle, choices,
            options: new(Detail: choice => FuiAgentActivityRoster.Detail(snapshot, job),
                GroupHeader: FuiAgentActivityRoster.IsGroupChoice, PreserveOrder: true));
        Check(NoUnsafeControl(picker.View.Message ?? ""), "overlay detail cleaned");
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

            var clock = new ManualClock(DateTimeOffset.UnixEpoch);
            var tracker = new FuiAgentActivityTracker(clock);
            tracker.Apply(new("visible", "worker", "started", "inspect"));
            clock.Advance(TimeSpan.FromSeconds(1));
            tracker.Apply(new("visible", "worker", "tool_start", "grep_search"));
            clock.Advance(TimeSpan.FromSeconds(1));
            var running = tracker.Apply(new("visible", "worker", "tool_end", "grep_search"));
            Emit(new TuiEvent.AgentsChanged(running));
            Check(ui.View().Status.StartsWith("子代理 1 个运行中", StringComparison.Ordinal), "running activity visible in status");

            Command("agents");
            var overlay = ui.View().Overlay;
            Check(overlay is { Title: FuiAgentActivityRoster.HubTitle, Items.Count: 2, Selected: 1 },
                "agent hub opens with a non-selectable group header and selected job");
            Check(overlay!.Message!.Contains("最近事件 · 新 → 旧", StringComparison.Ordinal) &&
                overlay.Message.Contains("tool_end", StringComparison.Ordinal) &&
                overlay.Message.Contains("工具 开始 1 / 完成 1", StringComparison.Ordinal),
                "selected job shows its read-only timeline");
            Check(ui.View().Draft == "", "draft remains empty while using picker search");
            Type("worker");
            Check(ui.View() is { Draft: "", Overlay.Query: "worker", Overlay.Selected: 0 } filtered &&
                filtered.Overlay.Items.Count == 1 && filtered.Overlay.Items[0].Value == "visible",
                "picker search never edits the composer draft and excludes group headers");
            Key(ConsoleKey.Escape);
            Check(ui.View().Overlay is null, "agent picker closes without changing the draft");

            Command("agents");
            clock.Advance(TimeSpan.FromSeconds(1));
            tracker.Apply(new("new-failure", "worker", "started", "inspect second job"));
            clock.Advance(TimeSpan.FromSeconds(1));
            var withFailure = tracker.Apply(new("new-failure", "worker", "failed", "second job failed"));
            Emit(new TuiEvent.AgentsChanged(withFailure));
            overlay = ui.View().Overlay;
            Check(overlay!.Items.Count(item => !FuiAgentActivityRoster.IsGroupChoice(item)) == 2 &&
                ui.View().Overlay is { Selected: var selected } &&
                selected == overlay.Items.ToList().FindIndex(item => item.Value == "visible"),
                "live update keeps the current job selected");
            Key(ConsoleKey.UpArrow);
            Check(ui.View().Overlay is { Selected: var up } && up == 1, "group headers are skipped by navigation");
            Key(ConsoleKey.DownArrow);
            Check(ui.View().Overlay!.Items[ui.View().Overlay!.Selected].Value == "new-failure", "down selects the failed job");
            Key(ConsoleKey.Enter);
            Check(ui.View() is { Overlay: null, Lines: var failedDetail } &&
                failedDetail[^1] is { Kind: "info", Text: var failedText } &&
                failedText.Contains("× 失败 · worker", StringComparison.Ordinal) &&
                failedText.Contains("second job failed", StringComparison.Ordinal),
                "enter opens the selected job's read-only event timeline");

            var completed = tracker.Apply(new("visible", "worker", "completed", "done"));
            Emit(new TuiEvent.AgentsChanged(completed));
            Check(!ui.View().Status.Contains("子代理 1 个运行中", StringComparison.Ordinal), "settled activity leaves running status");

            var pending = tracker.Apply(new("approval", "worker", "started", "approval overlap"));
            Emit(new TuiEvent.AgentsChanged(pending));
            Type("draft stays fixed");
            Key(ConsoleKey.P, true);
            Type("agents");
            Key(ConsoleKey.Enter);
            Check(ui.View() is { Overlay: not null, Draft: "draft stays fixed" }, "agent picker keeps the existing draft");
            Emit(new TuiEvent.Approval("approval-1", "write_file\n{\"path\":\"fixture\"}"));
            Check(ui.View() is { Approval: not null, Overlay: null, Draft: "draft stays fixed" },
                "approval preempts agent picker without changing the draft");
            Paint();
            Key(ConsoleKey.F3);
            ui.Update();
            Check(session.Answers.SequenceEqual([("approval-1", false)]), "approval still rejects");
            Emit(new TuiEvent.ApprovalClosed("approval-1"));
            Paint();
            Key(ConsoleKey.P, true);
            Type("agents");
            Key(ConsoleKey.Enter);
            Check(ui.View() is { Overlay: not null, Draft: "draft stays fixed" },
                "agent picker reopens after approval with the draft fixed");
            Key(ConsoleKey.Escape);
        }
        finally
        {
            ui.FinishAsync().GetAwaiter().GetResult();
        }
    }

    private static bool NoControl(string text) => !text.Any(char.IsControl);

    private static bool NoUnsafeControl(string text) =>
        !text.Any(c => char.IsControl(c) && c is not '\n' and not '\r' and not '\t');

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
