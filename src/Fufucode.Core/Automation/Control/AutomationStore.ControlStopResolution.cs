using System.Text.Json;
using Fufucode.Core.Automation.Harnesses;
using Microsoft.Data.Sqlite;

namespace Fufucode.Core.Automation;

public sealed partial class AutomationStore
{
    private sealed record StopResolutionContext(HarnessRun Run, string StopCommandId, string StopStatus,
        int QueueState, bool Resolved, string RequestHash, string StopContext);

    private static StopResolutionContext ReadStopResolutionContext(SqliteConnection c, SqliteTransaction tx, string runId)
    {
        using var q = ControlSql(c, tx, """
            SELECT h.thread_id,h.task_id,h.worktree,h.status,h.host_id,h.supervisor_pid,h.supervisor_start_utc,
              h.writer_pid,h.writer_start_utc,h.worktree_head,h.settle_head,s.command_id,s.status,q.state,
              EXISTS(SELECT 1 FROM harness_resolutions r WHERE r.run_id=h.run_id),h.request_hash,s.context_json,thread.worktree
            FROM harness_runs h JOIN control_run_stops s ON s.run_id=h.run_id
            JOIN threads thread ON thread.thread_id=h.thread_id
            JOIN tasks task ON task.task_id=h.task_id AND task.thread_id=h.thread_id
            JOIN host_requests q ON q.run_id=h.run_id AND q.thread_id=h.thread_id AND q.task_id=h.task_id
            WHERE h.run_id=$run
            """, ("$run", runId));
        using var r = q.ExecuteReader();
        if (!r.Read()) throw new InvalidOperationException("Select a run with its original durable cancellation request.");
        string? Text(int i) => r.IsDBNull(i) ? null : r.GetString(i);
        int? Pid(int i) => r.IsDBNull(i) ? null : r.GetInt32(i);
        long? Birth(int i) => r.IsDBNull(i) ? null : r.GetInt64(i);
        var run = new HarnessRun(runId, r.GetString(0), Enum.Parse<HarnessRunStatus>(r.GetString(3)), null, null, null, null)
        { TaskId = Text(1), Worktree = Text(2), HostId = Text(4), SupervisorPid = Pid(5), SupervisorStartUtc = Birth(6),
            WriterPid = Pid(7), WriterStartUtc = Birth(8), WorktreeHead = Text(9), SettleHead = Text(10) };
        if (run.Worktree is null || !Path.IsPathFullyQualified(run.Worktree) || !Path.IsPathFullyQualified(r.GetString(17)) ||
            !string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(run.Worktree!)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(r.GetString(17))),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException("Cancelled run worktree no longer matches its original thread.");
        var original = JsonSerializer.Deserialize<ControlRunningExecution>(r.GetString(16))!;
        if (original.RunId != run.RunId || original.ThreadId != run.ThreadId || original.TaskId != run.TaskId ||
            original.HostId != run.HostId || original.SupervisorPid != run.SupervisorPid || original.SupervisorStartUtc != run.SupervisorStartUtc ||
            original.WriterPid != run.WriterPid || original.WriterStartUtc != run.WriterStartUtc)
            throw new InvalidOperationException("Cancelled run identity no longer matches its original stop request.");
        return new(run, r.GetString(11), r.GetString(12), r.GetInt32(13), r.GetBoolean(14), r.GetString(15), r.GetString(16));
    }

    public async Task<ControlStopReconciliation> ReadControlStopReconciliationAsync(string runId,
        CancellationToken cancellationToken = default)
        => (await InspectStopResolutionAsync(runId, cancellationToken).ConfigureAwait(false)).Audit;

    private async Task<(ControlStopReconciliation Audit, StopResolutionContext Context)> InspectStopResolutionAsync(string runId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        StopResolutionContext context; long revision;
        using (var c = Open()) using (var tx = c.BeginTransaction())
        { context = ReadStopResolutionContext(c, tx, runId); revision = ReadControlSnapshot(c, tx).Revision; }
        var audit = await RunReconciler.AuditRunAsync(context.Run, SystemProcessProbe.Shared,
            GitCliEvidence.Shared, "native-control-observer", cancellationToken).ConfigureAwait(false);
        var clean = await GitCliEvidence.Shared.IsCleanAsync(context.Run.Worktree ?? "", cancellationToken).ConfigureAwait(false);
        var canInterrupt = context.Run.Status == HarnessRunStatus.Unknown && context.Run.TaskId is not null &&
            context.StopStatus == "attempt_finished_reconciliation_required" && context.QueueState == 2 && !context.Resolved &&
            audit.WriterStopped && audit.Verdict != OwnerVerdict.Unverifiable &&
            audit.ObservedHead is { } head && GitCliEvidence.IsSha(head) && clean is not null;
        return (new(revision, runId, context.Run.ThreadId, context.Run.TaskId ?? "", context.StopCommandId,
            Sha(JsonSerializer.Serialize(new { context, audit.Verdict, audit.WriterStopped, audit.LiveChildren, audit.ObservedHead, clean })),
            audit.ObservedHead, clean, canInterrupt,
            $"{audit.Verdict}: {audit.Reason}; run={context.Run.Status}; cancellation={context.StopStatus}; queue={context.QueueState}")
            { Worktree = context.Run.Worktree }, context);
    }

    // Only an explicit Interrupted decision is offered. Neither success provenance nor a new run is invented.
    public async Task<ControlStopResolutionReceipt> ApplyControlStopResolutionAsync(ControlStopResolutionCommand command,
        long nowUtc, CancellationToken cancellationToken = default)
    {
        foreach (var text in new[] { command.CommandId, command.DecidedBy, command.RunId, command.ThreadId, command.TaskId,
            command.StopCommandId, command.ExpectedEvidenceToken, command.ExpectedHead, command.Reason, command.Evidence })
            ArgumentException.ThrowIfNullOrWhiteSpace(text);
        RequireKnowledgeText(command.Reason, "Resolution reason"); RequireKnowledgeText(command.Evidence, "Resolution evidence");
        var digest = Sha("stop-resolution:" + JsonSerializer.Serialize(command));
        ControlStopResolutionReceipt? Replay(SqliteConnection c, SqliteTransaction tx)
        {
            using var q = ControlSql(c, tx, "SELECT payload_hash,result_json FROM control_receipts WHERE command_id=$id", ("$id", command.CommandId));
            using var r = q.ExecuteReader(); if (!r.Read()) return null;
            if (r.GetString(0) != digest) throw new InvalidOperationException("Resolution command identity reused with different input.");
            return JsonSerializer.Deserialize<ControlStopResolutionReceipt>(r.GetString(1))!;
        }
        using (var c = Open()) using (var tx = c.BeginTransaction()) if (Replay(c, tx) is { } original) return original;
        var inspected = await InspectStopResolutionAsync(command.RunId, cancellationToken).ConfigureAwait(false);
        var current = inspected.Audit;
        if (!current.CanInterrupt || current.ThreadId != command.ThreadId || current.TaskId != command.TaskId ||
            current.StopCommandId != command.StopCommandId || current.EvidenceToken != command.ExpectedEvidenceToken ||
            current.ObservedHead != command.ExpectedHead || current.Revision != command.ExpectedRevision)
            throw new InvalidOperationException("Cancellation reconciliation changed or its writer cannot be verified; inspect again, without automatic retry.");
        using var connection = Open(); using var transaction = connection.BeginTransaction();
        if (Replay(connection, transaction) is { } replay) return replay;
        if (ReadControlSnapshot(connection, transaction).Revision != command.ExpectedRevision)
            throw new InvalidOperationException("Control revision changed during reconciliation.");
        var locked = ReadStopResolutionContext(connection, transaction, command.RunId);
        // Re-read the journal under the mutation transaction; a second resolver/owner cannot replace the inspected run.
        if (locked != inspected.Context) throw new InvalidOperationException("Run journal changed during reconciliation.");
        var lockedToken = Sha(JsonSerializer.Serialize(new { context = locked }));
        // The audit token includes live OS/Git facts; metadata must also still match the audited context.
        if (locked.Run.Status != HarnessRunStatus.Unknown || locked.StopCommandId != command.StopCommandId ||
            locked.StopStatus != "attempt_finished_reconciliation_required" || locked.QueueState != 2 || locked.Resolved ||
            locked.Run.ThreadId != command.ThreadId || locked.Run.TaskId != command.TaskId)
            throw new InvalidOperationException("Only a settled cancelled Unknown run can be recorded as Interrupted.");
        var evidence = JsonSerializer.Serialize(new { runId = command.RunId, threadId = command.ThreadId, taskId = command.TaskId,
            headSha = command.ExpectedHead, writerStopped = true, terminalSignal = "interrupted", command.Reason, command.Evidence,
            stopCommandId = command.StopCommandId, current.TrackedClean, command.ExpectedEvidenceToken, journalToken = lockedToken });
        using (var q = ControlSql(connection, transaction, "UPDATE harness_runs SET status='Interrupted',error=$reason WHERE run_id=$run AND status='Unknown'",
            ("$reason", "Human cancellation reconciliation: " + command.Reason), ("$run", command.RunId)))
            if (q.ExecuteNonQuery() != 1) throw new InvalidOperationException("Run already resolved.");
        using (var q = ControlSql(connection, transaction, "INSERT INTO harness_resolutions VALUES($run,$evidence,'Unknown','Interrupted',$actor,$now)",
            ("$run", command.RunId), ("$evidence", evidence), ("$actor", command.DecidedBy), ("$now", nowUtc))) q.ExecuteNonQuery();
        using (var q = ControlSql(connection, transaction, "UPDATE delivery_claims SET state=$resolved,resolved_utc=$now WHERE run_id=$run AND state IN ($claimed,$unknown)",
            ("$resolved", (int)ClaimState.Resolved), ("$claimed", (int)ClaimState.Claimed), ("$unknown", (int)ClaimState.Unknown), ("$now", nowUtc), ("$run", command.RunId))) q.ExecuteNonQuery();
        using (var q = ControlSql(connection, transaction, "UPDATE control_run_stops SET status='reconciled_interrupted' WHERE command_id=$id", ("$id", command.StopCommandId))) q.ExecuteNonQuery();
        var next = checked(command.ExpectedRevision + 1);
        using (var q = ControlSql(connection, transaction, "UPDATE control_state SET revision=$next WHERE id=1 AND revision=$old", ("$next", next), ("$old", command.ExpectedRevision)))
            if (q.ExecuteNonQuery() != 1) throw new InvalidOperationException("Concurrent resolution.");
        var receipt = new ControlStopResolutionReceipt(command.CommandId, next, command.RunId, "Interrupted");
        AppendEvent(connection, transaction, "control:" + command.CommandId, "control.run.stop_reconciled", command.ThreadId,
            command.TaskId, null, command.RunId, evidence, nowUtc);
        AppendEvent(connection, transaction, "resolve:" + command.RunId, "harness.resolved", command.ThreadId,
            command.TaskId, null, command.RunId, evidence, nowUtc);
        using (var q = ControlSql(connection, transaction, "INSERT INTO control_receipts VALUES($id,$digest,$result)",
            ("$id", command.CommandId), ("$digest", digest), ("$result", JsonSerializer.Serialize(receipt)))) q.ExecuteNonQuery();
        transaction.Commit(); return receipt;
    }
}
