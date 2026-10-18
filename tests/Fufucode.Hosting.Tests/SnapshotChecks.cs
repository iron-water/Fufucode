using Fufucode.Core.Automation.Harnesses;
using Fufucode.Core.Automation.Hosting;
using static Fufucode.Hosting.Tests.Fixture;
using static Fufucode.Hosting.Tests.Verify;

namespace Fufucode.Hosting.Tests;

internal static class SnapshotChecks
{
    internal static async Task Run(string root)
    {
        var scenario = New(root, "snapshot-race");
        using var store = Seed(scenario);
        var queue = new HostJournal(store);
        const int count = 80;
        for (var i = 0; i < count; i++)
        {
            var id = "snapshot-" + i;
            queue.Enqueue(id, "thread", "task", HostJournal.Payload(Runtime(scenario),
                new HarnessRequest(id, "thread", "task", "devin/swe-2", "fixture")), i);
        }
        await using var host = new BackgroundHost(new(scenario.Database, scenario.Runs,
            "snapshot-reader", EndpointFile(scenario)));
        // No harness process or model: a real concurrent SQLite writer moves existing queue rows.
        var worker = Task.Run(() =>
        {
            for (var i = 0; i < count; i++)
            {
                var id = "snapshot-" + i;
                if (!queue.MarkStarted(id, 100 + i) || !queue.MarkSettled(id, "fixture-state-transition", 200 + i))
                    throw new Exception("Snapshot fixture failed to move its queued row.");
            }
        });
        var consistent = true;
        var samples = 0;
        do
        {
            var snapshot = host.Snapshot();
            consistent &= snapshot.Queued + snapshot.Started + snapshot.Settled + snapshot.Abandoned == count;
            samples++;
            await Task.Yield();
        } while (!worker.IsCompleted);
        await worker;
        var final = host.Snapshot();
        Check(consistent && samples > 0 && final.Settled == count,
            "snapshot: concurrent queue moves preserve the exact total in every published snapshot");
    }
}
