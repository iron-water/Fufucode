using System.Diagnostics;
using Fufucode.Core.Automation.Harnesses;

namespace Fufucode.Hosting.Tests;

/// <summary>Assertions for the lifecycle checks. A failure throws: nothing silently reports pass.</summary>
internal static class Verify
{
    public static int Passed;

    public static void Check(bool value, string name)
    {
        if (!value) throw new Exception("FAIL " + name);
        Passed++;
        Console.WriteLine("PASS " + name);
    }

    public static async Task Reject(Func<Task> action, string name)
    {
        try { await action(); }
        catch (Exception) { Check(true, name); return; }
        throw new Exception("FAIL " + name);
    }

    /// <summary>A synchronous refusal: the contract itself must throw before it changes anything.</summary>
    public static void Throws(Action action, string name)
    {
        try { action(); }
        catch (Exception) { Check(true, name); return; }
        throw new Exception("FAIL " + name);
    }

    public static async Task<HarnessRun> AwaitIdentity(HarnessRunStore runs, string runId)
    {
        for (var attempt = 0; attempt < 600; attempt++)
        {
            try
            {
                var run = runs.Get(runId);
                if (run.SupervisorPid is > 0 && run.WriterPid is > 0 && run.WorktreeHead is not null)
                    return run;
            }
            catch (KeyNotFoundException) { /* the claim is not visible yet */ }
            await Task.Delay(50);
        }
        throw new TimeoutException($"Owner identity of {runId} was never journaled.");
    }

    public static async Task WaitFor(Func<Task<bool>> condition, string name, int timeoutMs)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)timeoutMs * Stopwatch.Frequency / 1000;
        while (!await condition())
        {
            if (Stopwatch.GetTimestamp() > deadline) throw new Exception("TIMEOUT " + name);
            await Task.Delay(100);
        }
    }

    public static async Task<int> Exit(Process process, int seconds)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { return -1; }
        return process.ExitCode;
    }

    /// <summary>Deterministic liveness source, so pid reuse and unreadable processes need no OS luck.</summary>
    internal sealed class FakeProbe(ProcessLiveness liveness = ProcessLiveness.Dead,
        DateTimeOffset? startUtc = null) : IProcessProbe
    {
        public int CurrentPid => Environment.ProcessId;
        public ProcessFacts Probe(int processId) => new(processId, liveness,
            liveness == ProcessLiveness.Alive ? startUtc : null);
    }
}
