using System.Diagnostics;
using Fufucode.Core.Automation.Harnesses;
using Fufucode.Core.Automation.Hosting;
using static Fufucode.Hosting.Tests.Verify;

namespace Fufucode.Hosting.Tests;

internal static class HostBoundaryChecks
{
    public static async Task Run(string root)
    {
        var directory = Path.Combine(root, "boundaries"); Directory.CreateDirectory(directory);
        using var child = Fixture.Launch("--child", "hold", Path.Combine(directory, "child.log"), "unused", "30");
        try
        {
            var start = new DateTimeOffset(child.StartTime.ToUniversalTime());
            Check(ProcessTree.Children(Environment.ProcessId).Contains(child.Id), "birth boundary uses an actual held child in the OS process tree");
            Check(!ProcessTree.ChildrenOfWriter(Environment.ProcessId, start.AddMinutes(1).ToUnixTimeMilliseconds(),
                new BirthProbe(child.Id, start)).Contains(child.Id), "a live process born before a recycled writer identity is not that writer's child");
            Check(ProcessTree.ChildrenOfWriter(Environment.ProcessId, start.AddMinutes(-1).ToUnixTimeMilliseconds(),
                new BirthProbe(child.Id, start)).Contains(child.Id), "a genuine surviving child born after its parent still blocks ownership");
            Check(ProcessTree.ChildrenOfWriter(Environment.ProcessId, start.AddMinutes(1).ToUnixTimeMilliseconds(),
                new BirthProbe(child.Id, null)).Contains(child.Id), "unverifiable child birth remains blocking instead of assuming it is old");
            Check(ProcessTree.ChildrenOfWriter(Environment.ProcessId, null, new BirthProbe(child.Id, start)).Contains(child.Id),
                "a legacy writer without birth evidence retains the conservative child fence");
        }
        finally { if (!child.HasExited) child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); }

        var endpoint = Path.Combine(directory, "host.endpoint"); File.WriteAllText(endpoint, "12345");
        using (var writer = new FileStream(endpoint, FileMode.Open, FileAccess.Write, FileShare.Read))
            Check(HostClient.ReadEndpoint(endpoint) == 12345, "endpoint reader admits an existing publisher write handle without a sharing violation");
        File.WriteAllText(endpoint, "0"); Check(HostClient.ReadEndpoint(endpoint) is null, "endpoint rejects a zero port");
        File.WriteAllText(endpoint, "65536"); Check(HostClient.ReadEndpoint(endpoint) is null, "endpoint rejects an out-of-range port");
        File.WriteAllText(endpoint, "12345");
        using (var reader = new FileStream(endpoint, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            await using var host = new BackgroundHost(new(Path.Combine(directory, "control.db"), Path.Combine(directory, "runs"), "boundary-host", endpoint));
            Check(await host.StartAsync() && HostClient.ReadEndpoint(endpoint) == host.Port, "actual host atomically replaces its endpoint while a prior reader holds the old file");
            await host.StopAsync();
            Check(HostClient.ReadEndpoint(endpoint) is null, "actual host stop removes publication without disturbing the old read handle");
        }
    }

    private sealed class BirthProbe(int childId, DateTimeOffset? birth) : IProcessProbe
    {
        public int CurrentPid => Environment.ProcessId;
        public ProcessFacts Probe(int pid) => pid == childId ? birth is { } time
            ? new(pid, ProcessLiveness.Alive, time) : ProcessFacts.Indeterminate(pid) : SystemProcessProbe.Shared.Probe(pid);
    }
}
