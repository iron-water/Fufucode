using System.Diagnostics;
using System.Text.Json;
using Fufucode.Core.Automation;
using Fufucode.Core.Automation.Harnesses;
using Fufucode.Core.Automation.Hosting;
using static Fufucode.Hosting.Tests.Fixture;
using static Fufucode.Hosting.Tests.Verify;

namespace Fufucode.Hosting.Tests;

/// <summary>
/// C2 checks: the durable host queue and single-host claim, the one background host as a real
/// process, client close/reconnect with event replay, and restart recovery that never resends.
/// Everything runs through real child processes, real SQLite journals and a real loopback socket;
/// no model is contacted.
/// </summary>
internal static class HostScenarios
{
    public static async Task Run(string root, IProcessProbe self, IGitEvidence git)
    {
        await SnapshotChecks.Run(root);
        await QueueContract(root, self);
        await SingleHost(root, self);
        await ClientGoesAway(root, self, git);
        await RestartBacklog(root, self);
        await HostDiesMidRun(root, self);
    }

    private static HarnessRequest Request(string runId, string prompt) =>
        new(runId, "thread", "task", "devin/swe-2", prompt);

    private static string Payload(Scenario scenario, string runId, string prompt) =>
        HostJournal.Payload(Runtime(scenario), Request(runId, prompt));

    // -------------------------------------- 10. host queue and the single-host claim contract
    private static bool Same(IReadOnlyList<string> actual, params string[] expected) =>
        actual.Count == expected.Length && actual.SequenceEqual(expected);

    // -------------------------------------- 10. host queue and the single-host claim contract
    private static async Task QueueContract(string root, IProcessProbe self)
    {
        var scenario = New(root, "journal");
        GitInit(scenario.Worktree);
        using var store = Seed(scenario);
        var queue = new HostJournal(store);
        var runs = new HarnessRunStore(store);
        var ours = new HostInstance("host-A@1", 4242, 1_000, 1_000);
        // A probe that agrees the recorded pid is still the same process, and one that does not.
        var owning = new FakeProbe(ProcessLiveness.Alive,
            DateTimeOffset.FromUnixTimeMilliseconds(ours.StartUtc));
        var recycled = new FakeProbe(ProcessLiveness.Alive, DateTimeOffset.UtcNow);
        var gone = new FakeProbe(ProcessLiveness.Dead);

        Check(queue.Current(owning) is null && queue.SlotState(gone) == HostSlotState.Empty,
            "journal: an empty slot is empty, not a host");
        Check(queue.Enqueue("run-q1", "thread", "task",
                Payload(scenario, "run-q1", "echo:a"), Now()),
            "journal: an intent is accepted and durable before anything runs");
        Check(!queue.Enqueue("run-q1", "thread", "task",
                Payload(scenario, "run-q1", "echo:a"), Now()),
            "journal: replaying the same intent is a no-op, not a second queue row");
        Check(queue.Count(HostRequestState.Queued) == 1, "journal: the replay added no queue row");
        Throws(() => queue.Enqueue("run-q1", "thread", "task",
                Payload(scenario, "run-q1", "echo:b"), Now()),
            "journal: the same run with a different payload is refused, not overwritten");
        // The queue borrows U1 identity: an intent naming a thread that does not exist cannot be
        // accepted, so a client cannot invent work for a thread the project has never registered.
        Throws(() => queue.Enqueue("run-ghost-thread", "ghost", "task",
                Payload(scenario, "run-ghost-thread", "echo:x"), Now()),
            "journal: an intent for an unregistered thread is refused by the durable foreign key");
        Check(queue.Get("run-ghost-thread") is null && queue.Count(HostRequestState.Queued) == 1,
            "journal: the refused intent left no queue row behind");
        Check(queue.TryClaim(ours, owning), "journal: the first host claims the slot");
        Check(queue.TryClaim(ours, owning),
            "journal: the same host process re-claiming its own live slot is idempotent");
        Check(!queue.TryClaim(new HostInstance("host-B@2", 9090, 2_000, 2_000), owning),
            "journal: a live recorded host is never taken over");
        Check(!queue.TryClaim(new HostInstance("host-B@2", 9090, 2_000, 2_000),
                new FakeProbe(ProcessLiveness.Indeterminate)),
            "journal: an uninspectable recorded host still owns the slot");
        Check(queue.Current(owning)!.ProcessId == 4242,
            "journal: the occupant is the recorded process");
        Check(queue.SlotState(owning) == HostSlotState.Occupied, "journal: the slot reads occupied");
        // A recycled pid: the process exists, but its start time cannot be the recorded identity.
        Check(queue.Current(recycled) is null,
            "journal: a live pid whose start time differs is not an occupant");
        Check(queue.SlotState(recycled) == HostSlotState.Crashed,
            "journal: a recycled host pid reads as a crash, never as the same host");
        Check(queue.SlotState(gone) == HostSlotState.Crashed,
            "journal: a dead recorded host without a release is a crash, not a stop");
        Check(queue.TryClaim(new HostInstance("host-B@2", 9090, 2_000, 2_000), gone),
            "journal: only a provably gone host can be replaced");
        Check(queue.MarkStarted("run-q1", Now()), "journal: a queued intent starts");
        Check(queue.Get("run-q1")!.StartedUtc > 0, "journal: the start time is recorded once");
        Check(!queue.MarkStarted("run-q1", Now()), "journal: a started intent cannot start again");
        Check(queue.MarkSettled("run-q1", "Completed", Now()), "journal: a started intent settles");
        Check(queue.Get("run-q1")!.State == HostRequestState.Settled &&
              queue.Get("run-q1")!.Detail == "Completed",
            "journal: settling keeps the outcome detail with the queue row");
        Check(!queue.MarkAbandoned("run-q1", "late", Now()),
            "journal: a settled intent cannot be abandoned");
        Check(!queue.Enqueue("run-q1", "thread", "task",
                Payload(scenario, "run-q1", "echo:a"), Now()) &&
              queue.Get("run-q1")!.State == HostRequestState.Settled,
            "journal: a settled intent is not reopened by a replay");

        // Recovery reads the harness journal, not its own column, and never resends an open run.
        Check(queue.Enqueue("run-q2", "thread", "task", Payload(scenario, "run-q2", "echo:b"), Now()),
            "journal: a new intent is accepted next to a settled one");
        SeedRunning(scenario.Database, "run-open", "thread", scenario.Worktree);
        queue.Enqueue("run-open", "thread", "task",
            Payload(scenario, "run-open", "echo:c"), Now());
        queue.MarkStarted("run-open", Now());
        // One open claim per thread is the journal's rule, so the terminal row is seeded as terminal.
        SeedClosed(scenario.Database, "run-done", "thread", scenario.Worktree, "Completed");
        queue.Enqueue("run-done", "thread", "task",
            Payload(scenario, "run-done", "echo:e"), Now());
        queue.MarkStarted("run-done", Now());
        queue.Enqueue("run-nothing", "thread", "task",
            Payload(scenario, "run-nothing", "echo:d"), Now());
        queue.MarkStarted("run-nothing", Now());
        var recovery = queue.RecoverAfterRestart(runs, Now());
        Check(Same(recovery.Requeued, "run-nothing"),
            "recovery: only an intent that never reached the dispatcher is queued again");
        Check(queue.Get("run-nothing")!.State == HostRequestState.Queued &&
              queue.Get("run-nothing")!.Detail == "Restart: never dispatched",
            "recovery: a requeued intent says so in its own row, at the time recovery acts");
        Check(queue.Get("run-nothing")!.StartedUtc > 0,
            "recovery: the row keeps the fact that the dead host had already marked it started");
        Check(Same(recovery.Abandoned, "run-open"),
            "recovery: an intent whose run is still open is abandoned for reconciliation, never resent");
        Check(queue.Get("run-open")!.Detail!.StartsWith("Restart: run is Running"),
            "recovery: an abandoned intent records why it was dropped");
        Check(Same(recovery.Settled, "run-done"),
            "recovery: an intent whose run is already terminal settles without running again");
        Check(queue.Get("run-open")!.State == HostRequestState.Abandoned &&
              GetStatus(scenario.Database, "run-open") == "Running",
            "recovery: abandoning the queue row leaves the harness journal untouched");
        Check(queue.NextQueued()!.RunId == "run-q2",
            "recovery: the surviving backlog stays in acceptance order");
        queue.Release("host-B@2", Now());
        Check(queue.SlotState(gone) == HostSlotState.Stopped,
            "recovery: an explicit release is a stop, recorded apart from a crash");
        Check(queue.Current(gone) is null, "recovery: a released slot has no occupant");
        Check(queue.LastEventCursor() >= 0 && queue.Get("run-q2")!.State == HostRequestState.Queued,
            "recovery: the queue is readable without touching the event journal");
        await Task.CompletedTask;
    }

    // -------------------------------------- 11. one background host per project
    private static async Task SingleHost(string root, IProcessProbe self)
    {
        var scenario = New(root, "single");
        GitInit(scenario.Worktree);
        using var store = Seed(scenario);
        var queue = new HostJournal(store);
        using var host = LaunchBackgroundHost(scenario, "host-A@1");
        var port = await AwaitPort(scenario);
        Check(port > 0 && queue.SlotState(self) == HostSlotState.Occupied,
            "single: a real host process claims the slot and publishes its port");
        Check(queue.Current(self)!.ProcessId == host.Id,
            "single: the occupant is the real host process, not the one asking");
        await using (var client = await HostClient.ConnectAsync(port))
        {
            var hello = await client.SendAsync(HostProtocol.Hello);
            Check(hello.Type == HostProtocol.Hello &&
                  hello.Payload.GetProperty("hostId").GetString() == "host-A@1" &&
                  hello.Payload.GetProperty("processId").GetInt32() == host.Id &&
                  hello.Payload.GetProperty("protocol").GetInt32() == HostProtocol.Version &&
                  hello.Payload.GetProperty("slot").GetString() == "Occupied",
                "single: HELLO answers with the host process identity and slot state");
            var snapshot = await client.SnapshotAsync();
            Check(snapshot is not null && snapshot.HostId == "host-A@1" &&
                  snapshot.ProcessId == host.Id && snapshot.Port == port && !snapshot.Stopping,
                "single: SNAPSHOT reports the live host");
            var junk = await client.SendAsync("NOPE");
            Check(junk.Type == HostProtocol.Error &&
                  junk.Payload.GetProperty("code").GetString() == "unknown-request",
                "single: an unknown request is refused with a reason");
            var ping = await client.SendAsync(HostProtocol.Ping);
            Check(ping.Type == HostProtocol.Pong &&
                  ping.Payload.GetProperty("heartbeatUtc").GetInt64() > 0,
                "single: PING answers with the host's durable heartbeat");
        }
        // A competitor in this process, on the same project database, is refused by real evidence.
        var competing = new BackgroundHost(new HostOptions(scenario.Database, scenario.Runs,
            "host-B@2", Path.Combine(scenario.State, "competing.endpoint")));
        Check(!await competing.StartAsync(),
            "single: a second host is refused while the first is alive");
        Check(queue.Current(self)!.ProcessId == host.Id,
            "single: the refusal left the owner untouched");
        using (var second = LaunchBackgroundHost(scenario, "host-C@3"))
            Check(await Exit(second, 60) == 4,
                "single: a second host process exits instead of taking over");
        Check(StartCount(scenario.HostCounter) == 2, "single: two host processes started, no more");
        Check(StartCount(scenario.WriterCounter) == 0,
            "single: neither claim started any harness writer");
        await competing.DisposeAsync();
        KillTree(host.Id);
        await WaitFor(() => Task.FromResult(queue.SlotState(self) == HostSlotState.Crashed),
            "single: a killed host reads as a crash, not a stop", 20000);
        Check(File.Exists(EndpointFile(scenario)),
            "single: a crashed host leaves its endpoint behind; only a clean stop deletes it");
        var successor = new BackgroundHost(new HostOptions(scenario.Database, scenario.Runs,
            "host-D@4", Path.Combine(scenario.State, "successor.endpoint")));
        Check(await successor.StartAsync(),
            "single: only after the crash can another host take the slot");
        Check(queue.Current(self)!.ProcessId == self.CurrentPid,
            "single: the slot now records the new host's own process identity");
        await successor.StopAsync();
        Check(queue.SlotState(self) == HostSlotState.Stopped,
            "single: the successor's explicit stop is journaled as a stop");
        await successor.DisposeAsync();
    }

    // ---------------------- 12. the client goes away, the run continues, events replay
    private static async Task ClientGoesAway(string root, IProcessProbe self, IGitEvidence git)
    {
        var scenario = New(root, "continue");
        GitInit(scenario.Worktree);
        using var store = Seed(scenario);
        var runs = new HarnessRunStore(store);
        var queue = new HostJournal(store);
        using var host = LaunchBackgroundHost(scenario, "host-A@1");
        var port = await AwaitPort(scenario);
        long watermark;
        await using (var client = await HostClient.ConnectAsync(port))
        {
            var hello = await client.SendAsync(HostProtocol.Hello);
            watermark = hello.Payload.GetProperty("eventCursor").GetInt64();
            var first = await client.DispatchAsync("run-bg", Payload(scenario, "run-bg", "hang:5"));
            Check(first.GetProperty("type").GetString() == HostProtocol.Queued &&
                  first.GetProperty("accepted").GetBoolean(),
                "continue: the host accepts a dispatch intent over the wire");
            var later = await client.DispatchAsync("run-later", Payload(scenario, "run-later", "echo:x"));
            Check(later.GetProperty("accepted").GetBoolean(), "continue: a second intent queues behind it");
            var journaled = await AwaitIdentity(runs, "run-bg");
            Check(journaled.SupervisorPid == host.Id && journaled.HostId == "host-A@1",
                "continue: the run belongs to the background host process, not to the client");
            Check(self.Probe(journaled.WriterPid!.Value).Liveness == ProcessLiveness.Alive,
                "continue: a real harness writer is in flight");
            var dropped = await client.AbandonAsync("run-later", "client changed its mind");
            Check(dropped.GetProperty("type").GetString() == HostProtocol.Ack &&
                  dropped.GetProperty("applied").GetBoolean() &&
                  queue.Get("run-later")!.State == HostRequestState.Abandoned,
                "continue: a queued intent is abandoned before it ever starts");
        }
        Check(self.Probe(host.Id).Liveness == ProcessLiveness.Alive,
            "continue: closing the client did not close the host");
        Check(runs.Get("run-bg").Status is HarnessRunStatus.Running or HarnessRunStatus.Completed,
            "continue: the run is still going with no client attached");
        await WaitFor(() => Task.FromResult(runs.Get("run-bg").Status == HarnessRunStatus.Completed &&
            queue.Get("run-bg")!.State == HostRequestState.Settled),
            "continue: the background host finishes the run on its own", 40000);
        Check(StartCount(scenario.WriterCounter) == 1,
            "continue: exactly one writer ran; the abandoned intent never started");
        await using (var reconnected = await HostClient.ConnectAsync(port))
        {
            var (cursor, missed) = await reconnected.CatchUpAsync(watermark);
            Check(cursor > watermark && missed.Count > 0,
                "reconnect: EVENTS after the old cursor returns what happened while disconnected");
            Check(missed.Any(automationEvent => automationEvent.RunId == "run-bg"),
                "reconnect: the missed run events are replayed, not lost");
            Check(missed.Any(automationEvent => automationEvent.Type == "harness.settled"),
                "reconnect: the terminal event of the finished run is replayed too");
            Check(missed.All(automationEvent => automationEvent.Cursor > watermark),
                "reconnect: replay never returns events the client already processed");
            var snapshot = await reconnected.SnapshotAsync();
            Check(snapshot is not null && snapshot.Queued == 0 && snapshot.Started == 0 &&
                  snapshot.Settled == 1 && snapshot.Abandoned == 1 &&
                  snapshot.ActiveRuns.Count == 0 && snapshot.EventCursor >= cursor,
                "reconnect: the snapshot shows one settled run, one abandoned intent and the watermark");
            await reconnected.SubscribeAsync(snapshot!.EventCursor);
            var pushed = await NextPushedEventAsync(reconnected, scenario);
            Check(pushed.runId == "run-push" && pushed.cursor > snapshot.EventCursor,
                "reconnect: an event appended after subscribing is pushed with its cursor");
            Check(pushed.frame.Type == HostProtocol.Event,
                "reconnect: live frames arrive as EVENT, not as a reply");
            await reconnected.ByeAsync();
        }
        // The first pushed frame is the dispatching event, which is published before the child starts,
        // so the writer count is checked only after that run really finished.
        await WaitFor(() => Task.FromResult(runs.Get("run-push").Status == HarnessRunStatus.Completed &&
            queue.Get("run-push")!.State == HostRequestState.Settled),
            "continue: the run dispatched by a subscribed client finishes", 40000);
        Check(StartCount(scenario.WriterCounter) == 2,
            "continue: the pushed run is the only extra execution; nothing resent");
        // The host's own run is deliverable through the C1 gate afterwards, with no client involved.
        var head = (await git.HeadAsync(scenario.Worktree))!;
        var receiver = new DeliveryReceiver(store, runs, git);
        var received = await receiver.ReceiveAsync(new DeliveryCandidate("run-bg", "thread", "task",
            "part", head, "{\"summary\":\"queued through the host\"}", "rev-bg", "evt-bg", Now())
        { CardStatus = "module_ready" });
        Check(received.AcceptedForReview && received.Code == "delivered",
            "continue: a background host run is received by the delivery gate afterwards");
        Check(!store.IsIntegrated("part", received.RevisionNumber!.Value),
            "continue: receiving is not integration; that stays with the upper layer");
        var stop = await StopHostAsync(port);
        Check(stop.Type == HostProtocol.Stopping, "stop: the host acknowledges an explicit stop");
        Check(await Exit(host, 60) == 0, "stop: the host process exits on request");
        Check(queue.SlotState(self) == HostSlotState.Stopped,
            "stop: an explicit stop is journaled as a stop, apart from a crash");
        Check(!File.Exists(EndpointFile(scenario)),
            "stop: a clean stop removes the published endpoint");
        await Reject(() => HostClient.ConnectAsync(port),
            "stop: nobody is listening after the host exits");
        Check(StartCount(scenario.WriterCounter) == 2,
            "stop: shutting down started no new harness run");
    }

    /// <summary>Dispatch one run through the live client and read the frame the host pushes back.</summary>
    private static async Task<(HostFrame frame, string runId, long cursor)> NextPushedEventAsync(
        HostClient client, Scenario scenario)
    {
        var queued = await client.DispatchAsync("run-push", Payload(scenario, "run-push", "echo:pushed"));
        Check(queued.GetProperty("accepted").GetBoolean(), "reconnect: the subscribed client can still dispatch");
        var deadline = Now() + 60_000;
        while (true)
        {
            if (Now() > deadline) throw new TimeoutException("No event was pushed to the subscriber.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var frame = await client.NextPushedAsync(timeout.Token);
            if (frame is null) throw new InvalidOperationException("The host closed the link.");
            if (frame.Type != HostProtocol.Event) continue;
            var item = frame.Payload.GetProperty("event");
            var runId = item.TryGetProperty("runId", out var value) ? value.GetString() ?? "" : "";
            if (runId.Length == 0) continue;
            return (frame, runId, item.GetProperty("cursor").GetInt64());
        }
    }

    // ---------------------- 13. a restarting host continues the backlog without resending
    private static async Task RestartBacklog(string root, IProcessProbe self)
    {
        var scenario = New(root, "restart");
        GitInit(scenario.Worktree);
        using var store = Seed(scenario);
        var runs = new HarnessRunStore(store);
        var queue = new HostJournal(store);
        // The backlog left behind by a host that died: two intents never started, one was already
        // marked started while its run never got a dispatch record.
        queue.Enqueue("run-backlog-a", "thread", "task",
            Payload(scenario, "run-backlog-a", "echo:first"), Now());
        queue.Enqueue("run-backlog-b", "thread", "task",
            Payload(scenario, "run-backlog-b", "echo:second"), Now());
        queue.Enqueue("run-ghost", "thread", "task", Payload(scenario, "run-ghost", "echo:never"), Now());
        queue.MarkStarted("run-ghost", Now());
        using var host = LaunchBackgroundHost(scenario, "host-A@1");
        var port = await AwaitPort(scenario);
        await using (var client = await HostClient.ConnectAsync(port))
        {
            var hello = await client.SendAsync(HostProtocol.Hello);
            var recovered = hello.Payload.GetProperty("recovered").EnumerateArray()
                .Select(value => value.GetString()).ToList();
            Check(recovered.Count == 1 && recovered[0] == "run-ghost",
                "restart: the host reports what recovery did before serving anything");
            var snapshot = await client.SnapshotAsync();
            Check(Same(snapshot!.RecoveryActions, "run-ghost") &&
                  snapshot.Queued + snapshot.Started + snapshot.Settled + snapshot.Abandoned == 3,
                "restart: the snapshot lists the recovery action and the whole surviving backlog");
        }
        await WaitFor(() => Task.FromResult(queue.Count(HostRequestState.Settled) == 3),
            "restart: the queued backlog is drained by the new host process", 60000);
        Check(queue.Get("run-ghost")!.State == HostRequestState.Settled &&
              queue.Get("run-ghost")!.Detail == "Completed",
            "restart: the re-queued intent ran once and settled with its real outcome");
        Check(store.ListEventsAfter(0, 500).Count(automationEvent =>
                automationEvent.RunId == "run-ghost") >= 2,
            "restart: the re-queued intent's own events are in the durable journal");
        Check(StartCount(scenario.WriterCounter) == 3,
            "restart: each intent ran exactly once, in acceptance order");
        Check(runs.Get("run-backlog-a").Status == HarnessRunStatus.Completed &&
              runs.Get("run-backlog-b").Status == HarnessRunStatus.Completed,
            "restart: the backlog runs through the same dispatch journal as any other run");
        Check(queue.RecoverAfterRestart(runs, Now()).Pending.Count == 0,
            "restart: a second recovery finds nothing left behind");
        Check(queue.Get("run-ghost")!.State == HostRequestState.Settled,
            "restart: a re-queued intent settles once it actually runs");
        using (var competitor = LaunchBackgroundHost(scenario, "host-B@9"))
            Check(await Exit(competitor, 60) == 4,
                "restart: a host cannot start to 'recover' while another live host owns the slot");
        Check(StartCount(scenario.WriterCounter) == 3,
            "restart: the refused recovery dispatched nothing");
        var sameId = new BackgroundHost(new HostOptions(scenario.Database, scenario.Runs,
            "host-A@1", Path.Combine(scenario.State, "resume.endpoint")));
        Check(!await sameId.StartAsync(),
            "restart: the same host id does not resume while its old process is still live");
        await sameId.DisposeAsync();
        KillTree(host.Id);
        await WaitFor(() => Task.FromResult(queue.SlotState(self) == HostSlotState.Crashed),
            "restart: the killed host leaves a crash residue", 20000);
        var successor = new BackgroundHost(new HostOptions(scenario.Database, scenario.Runs,
            "host-A@1", EndpointFile(scenario)));
        Check(await successor.StartAsync(),
            "restart: after the crash the same host id may resume its slot");
        Check(successor.Recovery!.Pending.Count == 0 && successor.Recovery.Actions.Count == 0,
            "restart: resuming finds an empty backlog and invents no recovery work");
        await successor.StopAsync();
        await successor.DisposeAsync();
        Check(StartCount(scenario.WriterCounter) == 3,
            "restart: recovery, the refused claim and the resume dispatched no extra harness run");
    }
    // -------- 14. a killed host's in-flight run is never resent by the host that takes over
    private static async Task HostDiesMidRun(string root, IProcessProbe self)
    {
        var scenario = New(root, "midrun");
        GitInit(scenario.Worktree);
        using var store = Seed(scenario);
        var runs = new HarnessRunStore(store);
        var queue = new HostJournal(store);
        using var host = LaunchBackgroundHost(scenario, "host-A@1");
        var port = await AwaitPort(scenario);
        await using (var client = await HostClient.ConnectAsync(port))
        {
            var queued = await client.DispatchAsync("run-mid", Payload(scenario, "run-mid", "hang:120"));
            Check(queued.GetProperty("accepted").GetBoolean(), "midrun: the host accepted the intent");
        }
        var journaled = await AwaitIdentity(runs, "run-mid");
        var writer = journaled.WriterPid!.Value;
        var supervisor = host.Id; // captured before the kill, so identity stays readable afterwards
        Check(self.Probe(writer).Liveness == ProcessLiveness.Alive,
            "midrun: a real harness writer is in flight when the host dies");
        // The whole tree dies: this is the crash case, not the client-disconnect case.
        KillTree(supervisor);
        await WaitFor(() => Task.FromResult(runs.Get("run-mid").Status is HarnessRunStatus.Running
                or HarnessRunStatus.Unknown && self.Probe(writer).Liveness == ProcessLiveness.Dead),
            "midrun: the killed host's writer stops", 30000);
        Check(queue.SlotState(self) == HostSlotState.Crashed,
            "midrun: the dead host leaves a crash residue, not a stop");
        Check(queue.Get("run-mid")!.State == HostRequestState.Started,
            "midrun: the queue row is left exactly as the dead host wrote it");
        var survivor = new BackgroundHost(new HostOptions(scenario.Database, scenario.Runs,
            "host-B@2", Path.Combine(scenario.State, "survivor.endpoint")));
        Check(await survivor.StartAsync(), "midrun: a new host can claim the crashed slot");
        Check(self.Probe(supervisor).Liveness == ProcessLiveness.Dead,
            "midrun: the previous host process really is gone");
        Check(Same(survivor.Recovery!.Abandoned, "run-mid"),
            "midrun: recovery abandons the in-flight intent instead of resending it");
        Check(survivor.Recovery.Requeued.Count == 0 && survivor.Recovery.Settled.Count == 0,
            "midrun: recovery invents no requeue and no settlement for an uncertain outcome");
        var open = runs.Get("run-mid");
        Check(open.Status is HarnessRunStatus.Running or HarnessRunStatus.Unknown &&
              open.WriterPid == writer && open.SupervisorPid == supervisor,
            "midrun: the dispatch journal still names the dead owner; the takeover host did not rewrite it");
        var audit = await survivor.Reconciler.AuditAsync("run-mid");
        Check(audit.Verdict != OwnerVerdict.OwnedHere,
            "midrun: the new host does not claim ownership of a run it never launched");
        Check(StartCount(scenario.WriterCounter) == 1,
            "midrun: the takeover host dispatched nothing new");
        var delivery = await new DeliveryReceiver(store, runs, GitCliEvidence.Shared)
            .ReceiveAsync(new DeliveryCandidate("run-mid", "thread", "task", "part",
                (await GitCliEvidence.Shared.HeadAsync(scenario.Worktree))!, "{}", "rev-mid", "evt-mid", Now())
            { CardStatus = "module_ready" });
        Check(!delivery.AcceptedForReview &&
              delivery.Code is "unknown-requires-resolution" or "not-terminal",
            "midrun: an unsettled run cannot be presented as a delivery by the new host");
        Check(store.ListRevisions("part").Count == 0, "midrun: the refused delivery wrote nothing");
        var settled = await survivor.Reconciler.MarkOwnerLostAsync("run-mid", Now());
        Check(settled.Status == HarnessRunStatus.Unknown,
            "midrun: the uncertain run is projected Unknown for controlled resolution");
        Check(StartCount(scenario.WriterCounter) == 1,
            "midrun: reconciliation still launched no harness run");
        var head = (await GitCliEvidence.Shared.HeadAsync(scenario.Worktree))!;
        var resolution = await survivor.Reconciler.ResolveAsync(Resolution("run-mid", head,
            HarnessRunStatus.Interrupted), Now());
        Check(resolution.Applied,
            "midrun: the inherited unknown is settled only through the controlled entry");
        Check(!survivor.Queue.Enqueue("run-mid", "thread", "task",
                Payload(scenario, "run-mid", "hang:120"), Now()) &&
              survivor.Queue.Get("run-mid")!.State == HostRequestState.Abandoned,
            "midrun: an abandoned intent stays abandoned; replaying it does not requeue the work");
        await survivor.StopAsync();
        await survivor.DisposeAsync();
        Check(StartCount(scenario.WriterCounter) == 1,
            "midrun: the whole takeover path dispatched exactly one run, the original one");
    }

    private static RunResolution Resolution(string runId, string head, HarnessRunStatus to) =>
        new(runId, to, "M", JsonSerializer.Serialize(new
        {
            runId,
            threadId = "thread",
            headSha = head,
            writerStopped = true,
            terminalSignal = to switch
            {
                HarnessRunStatus.Completed => "completed",
                HarnessRunStatus.Interrupted => "interrupted",
                _ => "failed"
            }
        }));
}
