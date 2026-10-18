using System.Collections.Concurrent;
using Fufucode.App.Abstractions;
using Fufucode.Core;
using Fufucode.Pi;

internal static class AdapterStopTests
{
    public static async Task RunAsync(string root, string fake, string dotnet)
    {
        var release = Path.Combine(Path.GetTempPath(), "fufucode-stop-release-" + Guid.NewGuid().ToString("N"));
        await using var adapter = await PiSessions.ConnectAsync(new PiRpcOptions(dotnet, [fake, "--settle-release", release], root));
        var events = new ConcurrentQueue<SessionEvent>();
        var terminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        adapter.Event += (_, ev) => { events.Enqueue(ev); if (ev is SessionEvent.TurnSettled) terminal.TrySetResult(); };
        await adapter.SendAsync("hold_late_settled");
        await adapter.SendAsync("queued");
        await adapter.StopAsync();
        Console.WriteLine($"STOP returned: activity={adapter.Activity}, queued={adapter.QueuedCount}, outcome={adapter.LastOutcome?.ToString() ?? "null"}");
        Check(adapter.Activity == SessionActivity.Idle && adapter.QueuedCount == 0 &&
            adapter.LastOutcome == SessionOutcome.Interrupted, "stop acknowledgement exposes interrupted outcome before delayed terminal event");
        Check(events.OfType<SessionEvent.QueueCleared>().Single().ClearedTexts.SequenceEqual(["queued"]),
            "stop returns queued draft once");
        Check(!terminal.Task.IsCompleted, "stop does not invent protocol terminal event");
        // Release the delayed wire event explicitly; timing sleeps cannot make the assertion pass.
        await File.WriteAllTextAsync(release, "release");
        await terminal.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(events.OfType<SessionEvent.TurnSettled>().Count() == 1,
            "delayed protocol event is published once");
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        Console.WriteLine("PASS " + name);
    }
}
