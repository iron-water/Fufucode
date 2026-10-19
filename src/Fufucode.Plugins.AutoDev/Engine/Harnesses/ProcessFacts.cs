using System.Diagnostics;

namespace Fufucode.Core.Automation.Harnesses;

public enum ProcessLiveness
{
    /// <summary>The pid exists now and its recorded start time matches.</summary>
    Alive,
    /// <summary>The pid is not present. Termination is confirmed.</summary>
    Dead,
    /// <summary>The pid may exist but cannot be verified (access denied, permission loss).</summary>
    Indeterminate
}

/// <summary>
/// Observed facts about one local process. A pid alone is never an identity: the OS
/// reuses pids, so the start time is compared with a tolerance and must match.
/// </summary>
public sealed record ProcessFacts(int ProcessId, ProcessLiveness Liveness, DateTimeOffset? StartUtc)
{
    /// <summary>Clock granularity differs between platforms; three seconds is accepted.</summary>
    public const double StartToleranceSeconds = 3;

    public bool SameIdentityAs(DateTimeOffset recordedStartUtc) =>
        Liveness == ProcessLiveness.Alive && StartUtc is { } observed &&
        Math.Abs((observed - recordedStartUtc).TotalSeconds) <= StartToleranceSeconds;

    public static ProcessFacts Dead(int processId) => new(processId, ProcessLiveness.Dead, null);
    public static ProcessFacts Indeterminate(int processId) => new(processId, ProcessLiveness.Indeterminate, null);
}

/// <summary>
/// Liveness source, injectable so pid reuse and unverifiable processes can be tested
/// deterministically instead of waiting for the operating system to recycle a pid.
/// </summary>
public interface IProcessProbe
{
    ProcessFacts Probe(int processId);
    int CurrentPid { get; }
    bool IdentityAlive(int processId, DateTimeOffset recordedStartUtc) =>
        Probe(processId).SameIdentityAs(recordedStartUtc);
    bool Exists(int processId) => Probe(processId).Liveness != ProcessLiveness.Dead;
}

public sealed class SystemProcessProbe : IProcessProbe
{
    public static readonly SystemProcessProbe Shared = new();

    private readonly Lazy<ProcessFacts> _self = new(() =>
    {
        using var self = Process.GetCurrentProcess();
        return new(self.Id, ProcessLiveness.Alive,
            new DateTimeOffset(self.StartTime.ToUniversalTime(), TimeSpan.Zero));
    });

    public int CurrentPid => _self.Value.ProcessId;
    public ProcessFacts Self => _self.Value;

    public ProcessFacts Probe(int processId)
    {
        if (processId <= 0) return ProcessFacts.Dead(processId);
        Process? process = null;
        try
        {
            process = Process.GetProcessById(processId);
            var start = process.StartTime.ToUniversalTime();
            return new(processId, ProcessLiveness.Alive, new DateTimeOffset(start, TimeSpan.Zero));
        }
        catch (ArgumentException) { return ProcessFacts.Dead(processId); }
        catch (InvalidOperationException) { return ProcessFacts.Dead(processId); }
        catch (System.ComponentModel.Win32Exception) { return ProcessFacts.Indeterminate(processId); }
        catch (NotSupportedException) { return ProcessFacts.Indeterminate(processId); }
        finally { process?.Dispose(); }
    }
}
