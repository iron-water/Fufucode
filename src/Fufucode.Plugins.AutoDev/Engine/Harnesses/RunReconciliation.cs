using System.Text.Json;
using Fufucode.App.Abstractions.Automation.Harnesses;

namespace Fufucode.Core.Automation.Harnesses;

public enum OwnerVerdict
{
    /// <summary>
    /// This host process is the journaled supervisor and nothing journaled can still write.
    /// Only this verdict lets the asking host await, interrupt or continue the run.
    /// </summary>
    OwnedHere,
    /// <summary>
    /// The journaled writer is alive, a child of it survived, or another live host supervises the run.
    /// No other host may take this over, whatever the status column says.
    /// </summary>
    ActiveElsewhere,
    /// <summary>The journaled supervisor and writer are both provably gone: safe to settle.</summary>
    ConfirmedFinished,
    /// <summary>No identity recorded, pid reused, or liveness unverifiable: never take over or settle.</summary>
    Unverifiable
}

/// <summary>
/// Read-only observation of who owns a run and whether anything can still write. Ownership is two
/// levels: the supervising host process and the harness writer process that can modify the worktree.
/// </summary>
public sealed record OwnerAudit(string RunId, HarnessRunStatus Status, OwnerVerdict Verdict,
    string? HostId, int? SupervisorPid, long? SupervisorStartUtc, int? WriterPid, long? WriterStartUtc,
    bool WriterStopped,
    IReadOnlyList<int> LiveChildren, string? Worktree, string? RecordedHead, string? ObservedHead,
    string Reason, string ThreadId);

/// <summary>Explicit request to settle one Unknown run. Never dispatches, restarts or accepts.</summary>
public sealed record RunResolution(string RunId, HarnessRunStatus TargetStatus, string DecidedBy,
    string EvidenceJson);

public sealed record ResolutionOutcome(bool Applied, string Code, string Detail);

/// <summary>
/// C1 reconciliation: verify project/thread/worktree ownership against real process and
/// repository evidence, project lost owners as Unknown without resending, and provide the only
/// entry that can settle an Unknown run. Direct database edits are not an interface; a resolution
/// is re-validated at call time and journaled once.
/// </summary>
public sealed class RunReconciler(AutomationStore store, HarnessRunStore runs, IProcessProbe probe,
    IGitEvidence git, string hostId)
{
    public async Task<OwnerAudit> AuditAsync(string runId, CancellationToken cancellationToken = default)
    {
        var run = runs.Get(runId);
        return await AuditRunAsync(run, probe, git, hostId, cancellationToken).ConfigureAwait(false);
    }

    // Native clients reuse the exact ownership rules without constructing a migrating run store.
    internal static async Task<OwnerAudit> AuditRunAsync(HarnessRun run, IProcessProbe probe,
        IGitEvidence git, string hostId, CancellationToken cancellationToken = default)
    {
        var runId = run.RunId;
        var recordedHead = run.WorktreeHead;
        var observedHead = await git.HeadAsync(run.Worktree ?? "", cancellationToken).ConfigureAwait(false);
        var writer = Inspect(run.WriterPid, run.WriterStartUtc, probe);
        var supervisor = Inspect(run.SupervisorPid, run.SupervisorStartUtc, probe);
        // The writer is the process that can modify the worktree; it is checked first, and an
        // unverified identity never outranks an observed live writer.
        if (writer.Alive)
            return new(runId, run.Status, OwnerVerdict.ActiveElsewhere, run.HostId,
                run.SupervisorPid, run.SupervisorStartUtc, run.WriterPid, run.WriterStartUtc, false,
                ProcessTree.Children(run.WriterPid!.Value).Where(probe.Exists).ToList(),
                run.Worktree, recordedHead, observedHead,
                "The journaled writer process is still alive.", run.ThreadId);
        // Children of the journaled writer, plus (where the platform reparents orphans) any process
        // whose working directory is inside the worktree. Never include this host's own pid.
        var survivors = (run.WriterPid is { } writerPid
                ? ProcessTree.ChildrenOfWriter(writerPid, run.WriterStartUtc, probe).Where(child => probe.Exists(child))
                : [])
            .Concat(ProcessTree.WritersAt(run.Worktree ?? "").Where(candidate =>
                candidate != probe.CurrentPid && candidate != run.WriterPid && probe.Exists(candidate)))
            .Distinct().OrderBy(pid => pid).ToList();
        if (survivors.Count > 0)
            return new(runId, run.Status, OwnerVerdict.ActiveElsewhere, run.HostId,
                run.SupervisorPid, run.SupervisorStartUtc, run.WriterPid, run.WriterStartUtc, false,
                survivors, run.Worktree, recordedHead, observedHead,
                "A child of the journaled writer is still alive.", run.ThreadId);
        if (writer.Unverifiable || supervisor.Unverifiable)
            return new(runId, run.Status, OwnerVerdict.Unverifiable, run.HostId, run.SupervisorPid,
                run.SupervisorStartUtc, run.WriterPid, run.WriterStartUtc, false, [], run.Worktree,
                recordedHead, observedHead, writer.Reason + " " + supervisor.Reason, run.ThreadId);
        if (run.Status is HarnessRunStatus.Completed or HarnessRunStatus.Failed
            or HarnessRunStatus.Interrupted)
            // Nothing can write any more and the outcome is already journaled, so any host may
            // verify this read-only. Ownership only matters while the outcome is still open.
            return new(runId, run.Status, OwnerVerdict.ConfirmedFinished, run.HostId,
                run.SupervisorPid, run.SupervisorStartUtc, run.WriterPid, run.WriterStartUtc, true, [],
                run.Worktree, recordedHead, observedHead,
                "The run is terminal and no journaled writer survives.", run.ThreadId);
        if (supervisor.Alive)
            // Both must agree: same process and the same host instance id. Two supervisors on one
            // machine are competitors, never the same owner.
            return run.SupervisorPid == probe.CurrentPid && run.HostId == hostId
                ? new(runId, run.Status, OwnerVerdict.OwnedHere, run.HostId, run.SupervisorPid,
                    run.SupervisorStartUtc, run.WriterPid, run.WriterStartUtc, true, [], run.Worktree,
                    recordedHead, observedHead, "This host supervises the run and its writer exited.",
                    run.ThreadId)
                : new(runId, run.Status, OwnerVerdict.ActiveElsewhere, run.HostId, run.SupervisorPid,
                    run.SupervisorStartUtc, run.WriterPid, run.WriterStartUtc, true, [], run.Worktree,
                    recordedHead, observedHead,
                    "A live host that is not this process supervises the run.", run.ThreadId);
        return new(runId, run.Status, OwnerVerdict.ConfirmedFinished, run.HostId, run.SupervisorPid,
            run.SupervisorStartUtc, run.WriterPid, run.WriterStartUtc, true, [], run.Worktree,
            recordedHead, observedHead, "The journaled supervisor and writer are both gone.",
            run.ThreadId);
    }

    private readonly record struct Inspection(bool Alive, bool Unverifiable, string Reason);

    private static Inspection Inspect(int? pid, long? startMillis, IProcessProbe probe)
    {
        if (pid is not { } id || startMillis is not { } millis)
            return new(false, true, "No complete identity is journaled.");
        var facts = probe.Probe(id);
        return facts.Liveness switch
        {
            ProcessLiveness.Indeterminate => new(false, true, $"Process {id} cannot be inspected."),
            ProcessLiveness.Dead => new(false, false, $"Process {id} is gone."),
            _ => facts.SameIdentityAs(DateTimeOffset.FromUnixTimeMilliseconds(millis))
                ? new(true, false, $"Process {id} is alive with the recorded start time.")
                : new(false, true, $"Pid {id} exists but its start time differs: the pid was reused.")
        };
    }

    public async Task<List<OwnerAudit>> AuditAllAsync(CancellationToken cancellationToken = default)
    {
        var audits = new List<OwnerAudit>();
        foreach (var run in runs.ListActive())
            audits.Add(await AuditAsync(run.RunId, cancellationToken).ConfigureAwait(false));
        return audits;
    }

    /// <summary>
    /// Project a Dispatching/Running row whose owner identity is provably gone as Unknown and
    /// freeze the thread. This is the only automatic status write: it moves toward less certainty
    /// and never resends. Live or unverifiable owners are left untouched.
    /// </summary>
    public async Task<OwnerAudit> MarkOwnerLostAsync(string runId, long nowUtc,
        CancellationToken cancellationToken = default)
    {
        var audit = await AuditAsync(runId, cancellationToken).ConfigureAwait(false);
        if (audit.Verdict != OwnerVerdict.ConfirmedFinished) return audit;
        if (runs.TryProjectUnknown(runId, "Owner gone; reconcile before any further dispatch."))
        {
            store.FreezeThread(audit.ThreadId, $"run {runId} owner lost", $"owner:{runId}:lost", nowUtc);
            store.AppendEvent($"owner:{runId}:settled", "harness.owner_lost", audit.ThreadId,
                null, null, runId, JsonSerializer.Serialize(new
                {
                    from = audit.Status.ToString(), to = "Unknown", worktree = audit.Worktree,
                    recordedHead = audit.RecordedHead, observedHead = audit.ObservedHead,
                    supervisorPid = audit.SupervisorPid, writerPid = audit.WriterPid
                }), nowUtc);
        }
        return await AuditAsync(runId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>True when this process is the recorded owner of the run, identity included.</summary>
    public async Task<bool> OwnedHereAsync(string runId, CancellationToken cancellationToken = default) =>
        (await AuditAsync(runId, cancellationToken).ConfigureAwait(false)).Verdict == OwnerVerdict.OwnedHere;

    /// <summary>
    /// Settle one Unknown run into a terminal status using caller-supplied evidence, re-validated
    /// now: no journaled writer can still write, thread identity intact, full SHA present and equal
    /// to the real HEAD, writer stopped, and a complete journal for a success. One resolution per
    /// run, append-only. An unresolved active or unverifiable owner is always refused.
    /// </summary>
    public async Task<ResolutionOutcome> ResolveAsync(RunResolution resolution, long nowUtc,
        CancellationToken cancellationToken = default)
    {
        if (resolution.TargetStatus is not (HarnessRunStatus.Completed or HarnessRunStatus.Failed
            or HarnessRunStatus.Interrupted))
            return new(false, "target-status", "Only Completed, Failed or Interrupted can settle a run.");
        if (string.IsNullOrWhiteSpace(resolution.DecidedBy))
            return new(false, "decided-by", "A resolution must name its author.");
        var run = runs.Get(resolution.RunId);
        if (run.Status != HarnessRunStatus.Unknown)
            return new(false, "not-unknown", $"Run '{resolution.RunId}' is {run.Status}, not Unknown.");
        var audit = await AuditAsync(resolution.RunId, cancellationToken).ConfigureAwait(false);
        if (!audit.WriterStopped)
            return new(false, "owner", $"Owner state is {audit.Verdict}: {audit.Reason}");
        var evidence = TryEvidence(resolution.EvidenceJson);
        if (evidence is null)
            return new(false, "evidence", "Evidence must be one JSON object.");
        if (evidence.RunId != resolution.RunId || evidence.ThreadId != run.ThreadId)
            return new(false, "identity", "Evidence must name this run and its recorded thread.");
        // A resolution never rewrites the dispatched task identity, and evidence naming a different
        // task is refused: settlement must be about the run that really happened.
        if (evidence.TaskId is not null && evidence.TaskId != run.TaskId)
            return new(false, "run-task-mismatch",
                $"Evidence names task '{evidence.TaskId}' but the run was dispatched for '{run.TaskId ?? "(never journaled)"}'.");
        if (run.TaskId is null && resolution.TargetStatus == HarnessRunStatus.Completed)
            return new(false, "run-task-unknown",
                $"Run '{resolution.RunId}' never journaled the task it was dispatched for; it cannot be " +
                "certified as a delivery. Settle it as Failed or Interrupted, then re-dispatch so the " +
                "identity is journaled. Identity is never inferred from the request hash or output text.");
        if (!GitCliEvidence.IsSha(evidence.HeadSha))
            return new(false, "head-format", "Evidence must carry a full commit SHA.");
        if (evidence.WriterStopped != true)
            return new(false, "writer", "Evidence must state that the writer stopped.");
        var expectedTerminal = resolution.TargetStatus switch
        {
            HarnessRunStatus.Completed => "completed",
            HarnessRunStatus.Failed => "failed",
            _ => "interrupted"
        };
        if (evidence.TerminalSignal != expectedTerminal)
            return new(false, "terminal",
                $"Evidence terminal signal must be '{expectedTerminal}' for {resolution.TargetStatus}.");
        var observedHead = audit.ObservedHead;
        if (observedHead is null)
            return new(false, "worktree", "The recorded worktree is not readable as a repository.");
        if (!string.Equals(observedHead, evidence.HeadSha, StringComparison.OrdinalIgnoreCase))
            return new(false, "head-mismatch",
                $"Repository HEAD is {observedHead}, evidence claims {evidence.HeadSha}.");
        if (resolution.TargetStatus == HarnessRunStatus.Completed)
        {
            if (run.Output is null || run.SessionId is null)
                return new(false, "journal", "A success needs the journaled output and session identity.");
            // Settling an Unknown as a success may not borrow the live repository HEAD to cover a run
            // whose own provenance was never observed: the host that lost the writer cannot attest what
            // the repository held when that writer exited, so no commit can be credited to it now.
            if (run.SettleHead is null)
                return new(false, "settle-head-unobserved",
                    $"Run '{resolution.RunId}' recorded no repository provenance when its own writer exited; " +
                    "it cannot be certified as a success from a HEAD read later. Settle it as Failed or " +
                    "Interrupted and re-dispatch with an evidence observer. Provenance is never invented here.");
            if (!string.Equals(run.SettleHead, evidence.HeadSha, StringComparison.OrdinalIgnoreCase))
                return new(false, "head-not-from-run",
                    $"Run '{resolution.RunId}' left HEAD {run.SettleHead}; evidence names {evidence.HeadSha}, " +
                    "which this run never produced.");
            if (await git.IsCleanAsync(run.Worktree ?? "", cancellationToken).ConfigureAwait(false) != true)
                return new(false, "dirty", "A success needs a clean tracked worktree at the recorded HEAD.");
        }
        var reason = $"Settled by {resolution.DecidedBy}: {audit.Reason}";
        if (!runs.TryResolve(resolution.RunId, resolution.TargetStatus, reason,
                resolution.DecidedBy, resolution.EvidenceJson, nowUtc))
            return new(false, "already-resolved", "This run already has an immutable resolution.");
        foreach (var claim in store.ListUnresolvedClaims()
                     .Where(claim => claim.RunId == resolution.RunId))
            store.ResolveClaim(claim.ClaimId, resolution.RunId);
        store.AppendEvent($"resolve:{resolution.RunId}", "harness.resolved", run.ThreadId,
            null, null, resolution.RunId, JsonSerializer.Serialize(new
            {
                run = resolution.RunId, to = resolution.TargetStatus.ToString(), head = observedHead,
                decidedBy = resolution.DecidedBy, reason = audit.Reason
            }), nowUtc);
        return new(true, "resolved",
            $"Run '{resolution.RunId}' settled as {resolution.TargetStatus} against HEAD {observedHead}.");
    }

    private static Evidence? TryEvidence(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            string? Text(string name) => document.RootElement.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            bool? Flag(string name) => document.RootElement.TryGetProperty(name, out var value)
                && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;
            return new Evidence(Text("runId"), Text("threadId"), Text("taskId"), Text("headSha") ?? "",
                Flag("writerStopped"), Text("terminalSignal"));
        }
        catch (JsonException) { return null; }
    }

    private sealed record Evidence(string? RunId, string? ThreadId, string? TaskId, string HeadSha,
        bool? WriterStopped, string? TerminalSignal);
}
