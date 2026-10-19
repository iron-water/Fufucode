using System.Text.Json;
using Fufucode.App.Abstractions.Automation.Harnesses;
using Microsoft.Data.Sqlite;

namespace Fufucode.Core.Automation;

public sealed partial class AutomationStore
{
    private static void MigrateControlStops(SqliteConnection c, SqliteTransaction tx) => Execute(c, tx, """
        CREATE TABLE IF NOT EXISTS control_run_stops(
          command_id TEXT PRIMARY KEY, run_id TEXT NOT NULL UNIQUE,
          context_json TEXT NOT NULL, requested_by TEXT NOT NULL, reason TEXT NOT NULL,
          requested_utc INTEGER NOT NULL, status TEXT NOT NULL, completed_utc INTEGER);
        """);

    private static IReadOnlyList<ControlRunningExecution> ReadControlRunningExecutions(SqliteConnection c, SqliteTransaction tx)
    {
        using var tables = ControlSql(c, tx, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('harness_runs','host_instances','host_requests','control_run_stops')");
        if (Convert.ToInt64(tables.ExecuteScalar()) != 4) return [];
        var result = new List<ControlRunningExecution>();
        using var q = ControlSql(c, tx, """
            SELECT h.run_id,h.thread_id,h.task_id,h.host_id,h.supervisor_pid,h.supervisor_start_utc,h.writer_pid,h.writer_start_utc,
              q.request_hash,s.command_id,s.status
            FROM harness_runs h JOIN host_requests q ON q.run_id=h.run_id AND q.thread_id=h.thread_id AND q.task_id=h.task_id
            JOIN host_instances owner ON owner.id=1 AND owner.host_id=h.host_id AND owner.process_id=h.supervisor_pid
              AND owner.start_utc=h.supervisor_start_utc AND owner.stopped_utc IS NULL
            LEFT JOIN control_run_stops s ON s.run_id=h.run_id
            WHERE h.status='Running' AND q.state=1 AND h.writer_pid IS NOT NULL AND h.writer_start_utc IS NOT NULL
            ORDER BY h.run_id
            """);
        using var r = q.ExecuteReader();
        while (r.Read())
        {
            var context = new { RunId = r.GetString(0), ThreadId = r.GetString(1), TaskId = r.GetString(2), HostId = r.GetString(3),
                SupervisorPid = r.GetInt32(4), SupervisorStartUtc = r.GetInt64(5), WriterPid = r.GetInt32(6), WriterStartUtc = r.GetInt64(7), RequestHash = r.GetString(8) };
            result.Add(new(context.RunId, context.ThreadId, context.TaskId, context.HostId, context.SupervisorPid, context.SupervisorStartUtc,
                context.WriterPid, context.WriterStartUtc, Sha(JsonSerializer.Serialize(context)), r.IsDBNull(9) ? null : r.GetString(9), r.IsDBNull(10) ? null : r.GetString(10)));
        }
        return result;
    }
    public ControlStopRunReceipt ApplyControlStopRun(ControlStopRunCommand command, long nowUtc)
    {
        foreach (var value in new[] { command.CommandId, command.DecidedBy, command.RunId, command.ThreadId, command.TaskId, command.ExpectedEvidenceToken, command.Reason })
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
        RequireKnowledgeText(command.Reason, "Cancellation reason");
        var digest = Sha("run-stop:" + JsonSerializer.Serialize(command));
        using var c = Open(); using var tx = c.BeginTransaction();
        using (var q = ControlSql(c, tx, "SELECT payload_hash,result_json FROM control_receipts WHERE command_id=$id", ("$id", command.CommandId)))
        using (var r = q.ExecuteReader()) if (r.Read())
        {
            if (r.GetString(0) != digest) throw new InvalidOperationException("Stop command identity reused with different input.");
            return JsonSerializer.Deserialize<ControlStopRunReceipt>(r.GetString(1))!;
        }
        var state = ReadControlSnapshot(c, tx);
        if (state.Revision != command.ExpectedRevision) throw new InvalidOperationException("Refresh changed state before cancelling an execution.");
        var execution = ReadControlRunningExecutions(c, tx).SingleOrDefault(run => run.RunId == command.RunId);
        if (execution is null || execution.ThreadId != command.ThreadId || execution.TaskId != command.TaskId ||
            execution.EvidenceToken != command.ExpectedEvidenceToken || execution.StopCommandId is not null)
            throw new InvalidOperationException("Cancel only the current owned running execution and its exact identity, once.");
        var owner = SystemProcessProbe.Shared.Probe(execution.SupervisorPid);
        var writer = SystemProcessProbe.Shared.Probe(execution.WriterPid);
        if (owner.Liveness != ProcessLiveness.Alive || !owner.SameIdentityAs(DateTimeOffset.FromUnixTimeMilliseconds(execution.SupervisorStartUtc)) ||
            writer.Liveness != ProcessLiveness.Alive || !writer.SameIdentityAs(DateTimeOffset.FromUnixTimeMilliseconds(execution.WriterStartUtc)))
            throw new InvalidOperationException("Recorded owner or writer identity cannot be verified; reconcile instead of cancelling.");
        using (var q = ControlSql(c, tx, "INSERT INTO control_run_stops VALUES($cmd,$run,$context,$actor,$reason,$now,'requested',NULL)",
            ("$cmd", command.CommandId), ("$run", execution.RunId), ("$context", JsonSerializer.Serialize(execution)),
            ("$actor", command.DecidedBy), ("$reason", command.Reason), ("$now", nowUtc))) q.ExecuteNonQuery();
        var next = checked(state.Revision + 1);
        using (var q = ControlSql(c, tx, "UPDATE control_state SET revision=$next WHERE id=1 AND revision=$old", ("$next", next), ("$old", state.Revision)))
            if (q.ExecuteNonQuery() != 1) throw new InvalidOperationException("Concurrent stop request.");
        var receipt = new ControlStopRunReceipt(command.CommandId, next, execution.RunId, "requested");
        AppendEvent(c, tx, "control:" + command.CommandId, "control.run.stop_requested", execution.ThreadId, execution.TaskId, null,
            execution.RunId, JsonSerializer.Serialize(new { command, receipt }), nowUtc);
        using (var q = ControlSql(c, tx, "INSERT INTO control_receipts VALUES($id,$digest,$result)", ("$id", command.CommandId),
            ("$digest", digest), ("$result", JsonSerializer.Serialize(receipt)))) q.ExecuteNonQuery();
        tx.Commit(); return receipt;
    }
    private static IReadOnlyList<ControlRunStopState> ReadControlRunStops(SqliteConnection c, SqliteTransaction tx)
    {
        using var table = ControlSql(c, tx, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='control_run_stops'");
        if (Convert.ToInt64(table.ExecuteScalar()) == 0) return [];
        var result = new List<ControlRunStopState>();
        using var q = ControlSql(c, tx, "SELECT command_id,run_id,context_json,requested_by,reason,status FROM control_run_stops ORDER BY requested_utc,command_id");
        using var r = q.ExecuteReader();
        while (r.Read())
        {
            var execution = JsonSerializer.Deserialize<ControlRunningExecution>(r.GetString(2))!;
            result.Add(new(r.GetString(0), r.GetString(1), execution.ThreadId, execution.TaskId, r.GetString(3), r.GetString(4), r.GetString(5)));
        }
        return result;
    }
    internal ControlRunningExecution? PendingControlStop(string runId, string hostId, int supervisorPid, long supervisorStart)
    {
        using var c = Open(); using var tx = c.BeginTransaction();
        var execution = ReadControlRunningExecutions(c, tx).SingleOrDefault(run => run.RunId == runId && run.StopStatus == "requested" &&
            run.HostId == hostId && run.SupervisorPid == supervisorPid && run.SupervisorStartUtc == supervisorStart);
        if (execution is null) return null;
        using var q = ControlSql(c, tx, "SELECT context_json FROM control_run_stops WHERE command_id=$id", ("$id", execution.StopCommandId!));
        var expected = JsonSerializer.Deserialize<ControlRunningExecution>((string)q.ExecuteScalar()!)!;
        return expected.EvidenceToken == execution.EvidenceToken ? execution : null;
    }
    internal void RecordControlStopAttempt(ControlRunningExecution execution, string status, long nowUtc)
    {
        if (status is not ("attempt_finished_reconciliation_required" or "finished_before_cancel")) throw new ArgumentException("Unknown stop outcome.");
        using var c = Open(); using var tx = c.BeginTransaction();
        using var q = ControlSql(c, tx, "UPDATE control_run_stops SET status=$status,completed_utc=$now WHERE command_id=$id AND run_id=$run AND status='requested'",
            ("$status", status), ("$now", nowUtc), ("$id", execution.StopCommandId!), ("$run", execution.RunId));
        if (q.ExecuteNonQuery() == 1) AppendEvent(c, tx, "stop-attempt:" + execution.StopCommandId, "control.run.stop_attempt_finished",
            execution.ThreadId, execution.TaskId, null, execution.RunId, JsonSerializer.Serialize(new { execution.StopCommandId, status }), nowUtc);
        tx.Commit();
    }
    internal ControlRunningExecution? UnconsumedControlStop(string runId, string hostId, int supervisorPid, long supervisorStart)
    {
        using var c = Open(); using var q = c.CreateCommand();
        q.CommandText = "SELECT command_id,context_json FROM control_run_stops WHERE run_id=$run AND status='requested'";
        q.Parameters.AddWithValue("$run", runId); using var r = q.ExecuteReader();
        if (!r.Read()) return null;
        var execution = JsonSerializer.Deserialize<ControlRunningExecution>(r.GetString(1))!;
        return execution.RunId == runId && execution.HostId == hostId && execution.SupervisorPid == supervisorPid && execution.SupervisorStartUtc == supervisorStart
            ? execution with { StopCommandId = r.GetString(0), StopStatus = "requested" } : null;
    }
}
