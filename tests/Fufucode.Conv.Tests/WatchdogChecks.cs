using System.Text.Json;
using Fufucode.Core.Automation;
using Fufucode.Core.Automation.Harnesses;
using Fufucode.Core.Automation.Hosting;

namespace Fufucode.Conv.Tests;

/// <summary>
/// CONV-1 host watchdog: the host captures the terminal state of a run by itself. A valid delivery
/// card is admitted through the real receiving gate; a missing, broken, mismatched or dishonest card
/// and an abnormal exit all become a structured failure event instead of resting silently.
/// </summary>
internal static class WatchdogChecks
{
    public static async Task Run(string root, Func<bool, string, Task> check)
    {
        await Received(root, check);
        await ReplayIsIdempotent(root, check);
        await CardMissing(root, check);
        await CardInvalid(root, check);
        await CardWriterNotStopped(root, check);
        await CardHeadNotFromRun(root, check);
        await CardTaskMismatch(root, check);
        await RunFailed(root, check);
        await AbnormalExit(root, check);
    }

    private sealed record Harness(AutomationStore Store, HarnessRunStore Runs, HostDeliveryWatchdog Watchdog);

    private static Harness Host(ConvScenario scenario)
    {
        var store = ConvFixture.Seed(scenario);
        var runs = new HarnessRunStore(store);
        var receiver = new DeliveryReceiver(store, runs, GitCliEvidence.Shared);
        var watchdog = new HostDeliveryWatchdog(store, runs, receiver, probe: null,
            git: GitCliEvidence.Shared);
        return new(store, runs, watchdog);
    }

    private static string Card(string taskId, string head, string status = "module_ready",
        bool writerStopped = true, string[]? parts = null) => JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            type = "delivery.card",
            taskId,
            status,
            head,
            parts = parts ?? ["part"],
            writerStopped
        });

    private static void WriteCard(ConvScenario scenario, string json)
    {
        var directory = Path.Combine(scenario.Worktree, "tmp");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "delivery.json"), json);
    }

    private static async Task<string> Deliver(ConvScenario scenario, Harness harness, string runId,
        string prompt = "commit")
    {
        var executor = new HarnessExecutor(harness.Store, scenario.Runs, "host-A@1", GitCliEvidence.Shared);
        var run = await executor.ExecuteAsync(ConvFixture.Runtime(scenario),
            new HarnessRequest(runId, "thread", "task", "devin/swe-2", prompt));
        if (run.Status != HarnessRunStatus.Completed || run.SettleHead is null)
            throw new InvalidOperationException($"fixture run did not complete: {run.Status}");
        return run.SettleHead;
    }

    /// <summary>
    /// The watchdog's own terminal capture events. They are written as `conv.received` / `conv.failed`
    /// (the run+part+kind is in the event id), so counting the literal "delivery" type misses the
    /// failure path entirely; this counts the real durable capture events.
    /// </summary>
    private static int DeliverableEvents(AutomationStore store) =>
        store.ListEventsAfter(0).Count(e => e.Type is "conv.received" or "conv.failed");

    private static async Task Received(string root, Func<bool, string, Task> check)
    {
        var scenario = ConvFixture.New(root, "watchdog-received");
        ConvFixture.GitInit(scenario.Worktree);
        var harness = Host(scenario);
        var head = await Deliver(scenario, harness, "run-conv-received");
        WriteCard(scenario, Card("task", head));
        var report = await harness.Watchdog.ObserveAsync("run-conv-received", ConvFixture.Now());
        await check(report.Captures.Count == 1 && report.Captures[0].Outcome == "delivery.received",
            "watchdog: a real terminal run with a valid card is captured as received");
        await check(harness.Store.ListRevisions("part").Count == 1,
            "watchdog: the captured delivery is recorded through the real receiving gate");
        await check(harness.Store.ListRevisions("part").Single().HeadSha == head,
            "watchdog: the recorded revision names the run's own settle HEAD");
        await check(!harness.Store.HasDecision("part", 1) && !harness.Store.IsIntegrated("part", 1),
            "watchdog: capture is not acceptance and not integration");
        await check(DeliverableEvents(harness.Store) >= 1,
            "watchdog: the capture is recorded as a durable event");
    }

    private static async Task ReplayIsIdempotent(string root, Func<bool, string, Task> check)
    {
        var scenario = ConvFixture.New(root, "watchdog-replay");
        ConvFixture.GitInit(scenario.Worktree);
        var harness = Host(scenario);
        var head = await Deliver(scenario, harness, "run-conv-replay");
        WriteCard(scenario, Card("task", head));
        await harness.Watchdog.ObserveAsync("run-conv-replay", ConvFixture.Now());
        var before = harness.Store.ListEventsAfter(0).Count;
        var replay = await harness.Watchdog.ObserveAsync("run-conv-replay", ConvFixture.Now());
        await check(replay.Captures.Single().Outcome == "delivery.received" &&
              harness.Store.ListRevisions("part").Count == 1,
            "watchdog: re-observing a captured run replays instead of duplicating the delivery");
        await check(harness.Store.ListEventsAfter(0).Count == before,
            "watchdog: re-observing writes no duplicate event");
    }

    private static async Task CardMissing(string root, Func<bool, string, Task> check)
    {
        var scenario = ConvFixture.New(root, "watchdog-missing");
        ConvFixture.GitInit(scenario.Worktree);
        var harness = Host(scenario);
        await Deliver(scenario, harness, "run-conv-missing");
        var report = await harness.Watchdog.ObserveAsync("run-conv-missing", ConvFixture.Now());
        await check(report.Captures.Single().Outcome == "delivery.failed" &&
              report.Captures.Single().Code == "card-missing",
            "watchdog: a completed run with no card is a structured failure, not a silent wait");
        await check(harness.Store.ListRevisions("part").Count == 0,
            "watchdog: the missing card wrote no delivery");
        await check(DeliverableEvents(harness.Store) >= 1,
            "watchdog: the failure is recorded as a durable event");
    }

    private static async Task CardInvalid(string root, Func<bool, string, Task> check)
    {
        var scenario = ConvFixture.New(root, "watchdog-invalid");
        ConvFixture.GitInit(scenario.Worktree);
        var harness = Host(scenario);
        await Deliver(scenario, harness, "run-conv-invalid");
        WriteCard(scenario, "this is not a card");
        var report = await harness.Watchdog.ObserveAsync("run-conv-invalid", ConvFixture.Now());
        await check(report.Captures.Single().Outcome == "delivery.failed" &&
              report.Captures.Single().Code == "card-invalid",
            "watchdog: a card that is not JSON is refused with a machine-readable code");
        await check(harness.Store.ListRevisions("part").Count == 0,
            "watchdog: a broken card wrote no delivery");
    }

    private static async Task CardWriterNotStopped(string root, Func<bool, string, Task> check)
    {
        var scenario = ConvFixture.New(root, "watchdog-not-stopped");
        ConvFixture.GitInit(scenario.Worktree);
        var harness = Host(scenario);
        var head = await Deliver(scenario, harness, "run-conv-not-stopped");
        WriteCard(scenario, Card("task", head, writerStopped: false));
        var report = await harness.Watchdog.ObserveAsync("run-conv-not-stopped", ConvFixture.Now());
        await check(report.Captures.Single().Outcome == "delivery.failed" &&
              report.Captures.Single().Code == "writer-not-stopped",
            "watchdog: a card that does not claim the writer stopped is refused");
        await check(harness.Store.ListRevisions("part").Count == 0,
            "watchdog: the not-stopped card wrote no delivery");
    }

    private static async Task CardHeadNotFromRun(string root, Func<bool, string, Task> check)
    {
        var scenario = ConvFixture.New(root, "watchdog-head");
        var baseline = ConvFixture.GitInit(scenario.Worktree);
        var harness = Host(scenario);
        await Deliver(scenario, harness, "run-conv-head");
        WriteCard(scenario, Card("task", baseline));
        var report = await harness.Watchdog.ObserveAsync("run-conv-head", ConvFixture.Now());
        await check(report.Captures.Single().Outcome == "delivery.failed" &&
              report.Captures.Single().Code == "head-not-from-run",
            "watchdog: a card naming a HEAD the run never produced is refused");
        await check(harness.Store.ListRevisions("part").Count == 0,
            "watchdog: the borrowed HEAD wrote no delivery");
    }

    private static async Task CardTaskMismatch(string root, Func<bool, string, Task> check)
    {
        var scenario = ConvFixture.New(root, "watchdog-task");
        ConvFixture.GitInit(scenario.Worktree);
        var harness = Host(scenario);
        harness.Store.CreateTask("task-other", "thread", "other", "base", 2);
        harness.Store.CreatePart("part-other", "task-other", "other part", 2);
        var head = await Deliver(scenario, harness, "run-conv-task");
        WriteCard(scenario, Card("task-other", head, parts: ["part-other"]));
        var report = await harness.Watchdog.ObserveAsync("run-conv-task", ConvFixture.Now());
        await check(report.Captures.Single().Outcome == "delivery.failed" &&
              report.Captures.Single().Code == "run-task-mismatch",
            "watchdog: a card for another task cannot be captured against this run");
        await check(harness.Store.ListRevisions("part-other").Count == 0,
            "watchdog: the mismatched card wrote no delivery");
    }

    private static async Task RunFailed(string root, Func<bool, string, Task> check)
    {
        var scenario = ConvFixture.New(root, "watchdog-run-failed");
        ConvFixture.GitInit(scenario.Worktree);
        var harness = Host(scenario);
        var executor = new HarnessExecutor(harness.Store, scenario.Runs, "host-A@1", GitCliEvidence.Shared);
        var run = await executor.ExecuteAsync(ConvFixture.Runtime(scenario),
            new HarnessRequest("run-conv-failed", "thread", "task", "devin/swe-2", "fail"));
        await check(run.Status == HarnessRunStatus.Failed, "watchdog: the fixture really failed the run");
        var report = await harness.Watchdog.ObserveAsync("run-conv-failed", ConvFixture.Now());
        await check(report.Captures.Single().Outcome == "delivery.failed" &&
              report.Captures.Single().Code == "run-failed",
            "watchdog: a failed run becomes an explicit failure event");
    }

    private static async Task AbnormalExit(string root, Func<bool, string, Task> check)
    {
        var scenario = ConvFixture.New(root, "watchdog-abnormal");
        ConvFixture.GitInit(scenario.Worktree);
        using var store = ConvFixture.Seed(scenario);
        var runs = new HarnessRunStore(store);
        var receiver = new DeliveryReceiver(store, runs, GitCliEvidence.Shared);
        var watchdog = new HostDeliveryWatchdog(store, runs, receiver, probe: null,
            git: GitCliEvidence.Shared);

        using var host = ConvFixture.LaunchHost(scenario, "host-A@1", "run-conv-hang", "hang:120");
        HarnessRun journaled;
        var deadline = ConvFixture.Now() + 30_000;
        while (true)
        {
            try
            {
                journaled = runs.Get("run-conv-hang");
                if (journaled.Status == HarnessRunStatus.Running && journaled.WriterPid is > 0) break;
            }
            catch (KeyNotFoundException) { /* not journaled yet */ }
            if (ConvFixture.Now() > deadline)
                throw new TimeoutException("the fixture host never journaled a running writer");
            await Task.Delay(50);
        }

        var whileAlive = await watchdog.ObserveAsync("run-conv-hang", ConvFixture.Now());
        await check(whileAlive.Captures.Single().Outcome == "delivery.deferred",
            "watchdog: a run whose writer is still alive is deferred, never captured");
        await check(store.GetThread("thread").Status == ThreadStatus.Active,
            "watchdog: deferring does not freeze a thread with a live writer");

        var sweep = await watchdog.SweepAsync(ConvFixture.Now());
        await check(sweep.Captures.Any(c => c.RunId == "run-conv-hang" && c.Outcome == "delivery.deferred"),
            "watchdog: the sweep reports the same in-flight run without touching it");

        ConvFixture.KillTree(host.Id);
        var writerPid = journaled.WriterPid!.Value;
        var probe = SystemProcessProbe.Shared;
        var until = ConvFixture.Now() + 20_000;
        while (probe.Probe(writerPid).Liveness != ProcessLiveness.Dead)
        {
            if (ConvFixture.Now() > until)
                throw new TimeoutException("the fixture writer never exited");
            await Task.Delay(50);
        }

        var abandoned = await watchdog.ObserveAsync("run-conv-hang", ConvFixture.Now());
        await check(abandoned.Captures.Single().Outcome == "delivery.failed" &&
              abandoned.Captures.Single().Code == "abandoned-writer",
            "watchdog: a writer that died without a terminal state is an explicit failure");
        await check(store.ListRevisions("part").Count == 0,
            "watchdog: an abnormal exit wrote no delivery");
        await check(DeliverableEvents(store) >= 1,
            "watchdog: the abnormal exit is recorded as a durable event");
    }
}
