using System.Text.Json;
using Fufucode.Core.Automation;
using Fufucode.Core.Automation.Harnesses;
using Fufucode.Core.Automation.Hosting;
using static Fufucode.Hosting.Tests.Fixture;
using static Fufucode.Hosting.Tests.Verify;

namespace Fufucode.Hosting.Tests;

/// <summary>
/// Optional real-model integration probe, run only on an explicit request. It uses the cheapest
/// allowed model (devin/swe-2, free tier), a short memory-answer prompt, and the same background
/// host the checks use, so what is proven is the host path with a real harness binary: queue,
/// client disconnect, background completion, reconnect replay, delivery gate.
/// </summary>
internal static class Probe
{
    public static async Task<int> Run(string executable, string model)
    {
        if (!File.Exists(executable)) throw new FileNotFoundException(executable);
        var root = Path.Combine(Path.GetTempPath(), "fufucode-host-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Console.WriteLine("Probe evidence root: " + root);
        Console.WriteLine("Harness: " + executable);
        Console.WriteLine("Model: " + model + " (explicit, cheapest allowed tier)");
        var scenario = New(root, "probe");
        GitInit(scenario.Worktree);
        var git = GitCliEvidence.Shared;
        using var store = Seed(scenario);
        var runs = new HarnessRunStore(store);
        var queue = new HostJournal(store);
        var runtime = new HarnessRuntime("codex-real", "codex", Path.GetFullPath(executable), [], "0.162.x");
        var payload = HostJournal.Payload(runtime,
            new HarnessRequest("run-probe", "thread", "task", model,
                "只回答两个字：在线。不要读写任何文件，不要派子代理。"));
        var hostOptions = new HostOptions(scenario.Database, scenario.Runs, "probe-host@1",
            EndpointFile(scenario));
        await using var host = new BackgroundHost(hostOptions);
        Check(await host.StartAsync(), "probe: the background host claims its slot");
        await using (var client = await HostClient.ConnectAsync(host.Port))
        {
            var hello = await client.SendAsync(HostProtocol.Hello);
            Check(hello.Payload.GetProperty("hostId").GetString() == "probe-host@1",
                "probe: the host answers HELLO before any work is queued");
            var queued = await client.DispatchAsync("run-probe", payload);
            Check(queued.GetProperty("type").GetString() == HostProtocol.Queued &&
                  queued.GetProperty("accepted").GetBoolean(),
                "probe: a real-model dispatch intent is accepted over the wire");
        }
        var journaled = await AwaitIdentity(runs, "run-probe");
        Check(journaled.SupervisorPid == Environment.ProcessId && journaled.HostId == "probe-host@1",
            "probe: the real harness child is supervised by this host process");
        Check(SystemProcessProbe.Shared.Probe(journaled.WriterPid!.Value).Liveness ==
              ProcessLiveness.Alive, "probe: a real harness writer process started");
        // The client is already gone. The model call continues regardless. Both facts are awaited
        // together: the host journals the run outcome first and settles the queue row right after,
        // so waiting only on the journal would read a half-finished settle.
        await WaitFor(() => Task.FromResult(runs.Get("run-probe").Status is not (HarnessRunStatus.Dispatching
                or HarnessRunStatus.Running) &&
            queue.Get("run-probe")!.State != HostRequestState.Started),
            "probe: the real model run reaches a journaled outcome and settles its queue row", 300_000);
        var settled = runs.Get("run-probe");
        Console.WriteLine("Journaled status: " + settled.Status);
        Console.WriteLine("Journaled output: " + (settled.Output ?? "(none)"));
        Console.WriteLine("Session id: " + (settled.SessionId ?? "(none)"));
        Check(settled.Status == HarnessRunStatus.Completed && settled.Output is { Length: > 0 } &&
              settled.SessionId is { Length: > 0 },
            "probe: a real Codex run through the host completes with session identity and output");
        var row = queue.Get("run-probe")!;
        Console.WriteLine($"Queue row: {row.State} / {row.Detail}");
        // The host settles a queue row with whatever the journal says, including a failure: the row
        // reports the outcome, it never claims one of its own.
        Check(row.State == HostRequestState.Settled && row.Detail == settled.Status.ToString(),
            "probe: the queue row ends with the journaled outcome, not the client's hope");
        await using var reconnected = await HostClient.ConnectAsync(host.Port);
        var (cursor, events) = await reconnected.CatchUpAsync(0);
        Check(cursor > 0 && events.Any(automationEvent => automationEvent.RunId == "run-probe"),
            "probe: the finished real run is readable by a client that reconnects");
        var head = (await git.HeadAsync(scenario.Worktree))!;
        var receiver = new DeliveryReceiver(store, runs, git);
        var received = await receiver.ReceiveAsync(new DeliveryCandidate("run-probe", "thread", "task",
            "part", head, JsonSerializer.Serialize(new { summary = "probe answer",
            output = settled.Output }), "rev-probe", "evt-probe", Now())
        { CardStatus = "module_ready" });
        Check(received.AcceptedForReview && received.Code == "delivered",
            "probe: the real run's answer passes the C1 receiving gate against the real HEAD");
        Check(!store.IsIntegrated("part", received.RevisionNumber!.Value),
            "probe: receiving is not integration; integration stays with the upper layer");
        await host.StopAsync();
        Check(queue.SlotState(SystemProcessProbe.Shared) == HostSlotState.Stopped,
            "probe: the host stops cleanly after the real run");
        Console.WriteLine($"PROBE PASS {Passed} checks. Evidence: {root}");
        return 0;
    }

}
