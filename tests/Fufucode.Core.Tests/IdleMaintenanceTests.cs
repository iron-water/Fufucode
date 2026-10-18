using Fufucode.Core;
using Fufucode.Pi;

internal static class IdleMaintenanceTests
{
    public static async Task RunAsync(string root, string fake, string dotnet)
    {
        void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL " + name); Console.WriteLine("PASS Maintenance: " + name); }
        bool Refused(ChatSessionCoordinator session)
        {
            try { using var unexpected = session.BeginIdleMaintenance(); return false; }
            catch (InvalidOperationException) { return true; }
        }
        await using var session = PiSessions.Coordinator(new PiRpcOptions(dotnet, [fake], root));
        Check(Refused(session), "stopped connections cannot reserve an idle restart");
        await session.StartAsync();
        var generation = session.Snapshot.Generation;
        using (var reserved = session.BeginIdleMaintenance())
        {
            Check(session.IsInIdleMaintenance && !session.CanSendPrompt, "idle reservation changes live send capability");
            Check(Refused(session), "two owners cannot reserve the same connection");
            var sent = false;
            try { await session.SendPromptAsync("should-never-run"); sent = true; }
            catch (InvalidOperationException) { }
            Check(!sent && session.Snapshot.Activity == ActivityState.Idle, "rejected prompt does not start a fake run");
        }
        Check(!session.IsInIdleMaintenance && session.CanSendPrompt, "reservation disposal restores capability");
        await session.SendPromptAsync("hold");
        Check(session.Snapshot.Activity == ActivityState.Running, "fixture is actually running before maintenance rejection");
        Check(Refused(session) && session.Snapshot.Activity == ActivityState.Running && session.Snapshot.Generation == generation,
            "running task is preserved and not stopped or replaced to obtain maintenance");
        await session.SendPromptAsync("queued", PromptDelivery.FollowUp);
        var queued = session.Snapshot.QueuedTexts.Count;
        Check(queued > 0 && Refused(session) && session.Snapshot.QueuedTexts.Count == queued,
            "queued work is preserved when maintenance is refused");
        await session.StopAsync();
        Check(session.Snapshot.Connection == ConnectionState.Connected, "explicit fixture stop retains the host");
        using var next = session.BeginIdleMaintenance();
        Check(session.IsInIdleMaintenance, "settled fixture can reserve maintenance again");
    }
}
