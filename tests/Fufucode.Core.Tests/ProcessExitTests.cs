using Fufucode.Core;
using Fufucode.Pi;

internal static class ProcessExitTests
{
    public static async Task RunAsync(string root, string fake, string dotnet)
    {
        void Check(bool condition, string name)
        { if (!condition) throw new Exception("FAIL Process exit: " + name); Console.WriteLine("PASS Process exit: " + name); }
        await using var session = PiSessions.Coordinator(new PiRpcOptions(dotnet, [fake], root));
        Check(session.LastProcessExit is null, "a never-started coordinator has no invented termination");
        await session.StartAsync();
        var firstGeneration = session.Snapshot.Generation;
        Check(firstGeneration is not null && session.LastProcessExit is null,
            "a live first generation has no closed-process evidence");
        await session.CloseAsync();
        var firstExit = session.LastProcessExit;
        Check(firstExit is not null && firstExit.Generation == firstGeneration && firstExit.ProcessId > 0 &&
            firstExit.ObservedAtUtc != default && session.Snapshot.Generation is null && session.Snapshot.Connection == ConnectionState.Stopped,
            "completed close retains actual exit after clearing the live client");
        await session.CloseAsync();
        Check(session.LastProcessExit == firstExit, "closing again does not replace the original observed exit");
        await session.StartAsync();
        var secondGeneration = session.Snapshot.Generation;
        Check(secondGeneration is not null && secondGeneration != firstGeneration && session.LastProcessExit == firstExit,
            "starting a new generation retains historical exit without claiming that the new process exited");
        Check(session.LastProcessExit?.Generation != secondGeneration && (await session.QueryStateAsync()).Success,
            "old exit cannot match a live new connection and read-only queries still work");
        await session.CloseAsync();
        Check(session.LastProcessExit is { } secondExit && secondExit.Generation == secondGeneration &&
            secondExit != firstExit && session.Snapshot.Generation is null,
            "the second close records only its own actual generation");
    }
}
