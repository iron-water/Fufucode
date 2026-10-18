using System.Text.Json;
using Fufucode.Core.Automation.Harnesses;
using Fufucode.Core.Automation.Hosting;
using Microsoft.Data.Sqlite;

namespace Fufucode.Core.Automation;

public sealed partial class AutomationStore
{
    private static void MigrateControlRepairs(SqliteConnection c, SqliteTransaction tx) => Execute(c, tx, """
        CREATE TABLE IF NOT EXISTS control_repairs (
          repair_id TEXT PRIMARY KEY, package_id TEXT NOT NULL, package_version INTEGER NOT NULL,
          task_id TEXT NOT NULL REFERENCES tasks(task_id), thread_id TEXT NOT NULL REFERENCES threads(thread_id),
          part_id TEXT NOT NULL REFERENCES delivery_parts(part_id), part_revision INTEGER NOT NULL,
          decision_id TEXT NOT NULL UNIQUE REFERENCES part_decisions(decision_id), source_run_id TEXT NOT NULL,
          head_sha TEXT NOT NULL, context_json TEXT NOT NULL, requested_by TEXT NOT NULL, reason TEXT NOT NULL,
          status TEXT NOT NULL, run_id TEXT, payload_hash TEXT);
        CREATE TABLE IF NOT EXISTS control_repair_bindings (
          repair_id TEXT PRIMARY KEY REFERENCES control_repairs(repair_id), intent_id TEXT NOT NULL REFERENCES control_intents(intent_id),
          run_id TEXT NOT NULL UNIQUE REFERENCES host_requests(run_id), thread_id TEXT NOT NULL, task_id TEXT NOT NULL, payload_hash TEXT NOT NULL);
        CREATE VIEW IF NOT EXISTS control_writer_bindings AS
          SELECT intent_id,run_id,thread_id,task_id,payload_hash FROM control_bindings UNION ALL
          SELECT intent_id,run_id,thread_id,task_id,payload_hash FROM control_repair_bindings;
        """);

    public ControlRepairReceipt ApplyControlRepair(ControlRepairCommand command, long nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command.CommandId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.DecidedBy);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Reason);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.TaskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.PartId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ExpectedRunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ExpectedHeadSha);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ExpectedDecisionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ExpectedEvidenceToken);
        var digest = Sha("repair-request:" + JsonSerializer.Serialize(command));
        using var c = Open(); using var tx = c.BeginTransaction();
        if (ReplayRepair<ControlRepairReceipt>(c, tx, command.CommandId, digest) is { } replay) return replay;
        var inbox = ReadControlInbox(c, tx);
        if (!ControlExecutionMode.AllowsDispatch(inbox.Mode) || inbox.Revision != command.ExpectedRevision)
            throw new InvalidOperationException("Repair intent requires an autonomous or semi-automatic mode and current revision.");
        var item = inbox.Items.SingleOrDefault(i => i.Kind == "repair" && i.ObjectId == command.PartId);
        var part = ReadControlDecisionParts(c, tx, command.TaskId).SingleOrDefault(p => p.PartId == command.PartId);
        if (item is not { Actionable: true } || part is null || part.ReviewId != command.ExpectedDecisionId ||
            part.Revision != command.ExpectedPartRevision || part.RunId != command.ExpectedRunId || part.HeadSha != command.ExpectedHeadSha ||
            item.EvidenceToken != command.ExpectedEvidenceToken || !ControlReviewAdmissible(c, tx, command.TaskId))
            throw new InvalidOperationException("Authorize only the latest stopped rework decision and its exact original delivery.");
        string package; long version; string thread;
        using (var q = ControlSql(c, tx, """
            SELECT i.package_id,i.version,b.thread_id FROM control_writer_bindings b JOIN control_intents i ON i.intent_id=b.intent_id
            WHERE b.run_id=$run AND b.task_id=$task
            """, ("$run", command.ExpectedRunId), ("$task", command.TaskId)))
        using (var r = q.ExecuteReader())
        { if (!r.Read()) throw new InvalidOperationException("Repair cannot adopt an unregistered delivery."); package = r.GetString(0); version = r.GetInt64(1); thread = r.GetString(2); }
        using (var q = ControlSql(c, tx, "INSERT INTO control_repairs VALUES($id,$package,$v,$task,$thread,$part,$rev,$decision,$source,$head,$context,$actor,$reason,'awaiting_consumer',NULL,NULL)",
            ("$id", command.CommandId), ("$package", package), ("$v", version), ("$task", command.TaskId), ("$thread", thread), ("$part", command.PartId),
            ("$rev", command.ExpectedPartRevision), ("$decision", command.ExpectedDecisionId), ("$source", command.ExpectedRunId), ("$head", command.ExpectedHeadSha),
            ("$context", item.ContextJson), ("$actor", command.DecidedBy), ("$reason", command.Reason))) q.ExecuteNonQuery();
        SetThreadStatus(c, tx, thread, ThreadStatus.Active, null);
        var receipt = new ControlRepairReceipt(command.CommandId, checked(inbox.Revision + 1), command.CommandId, "awaiting_consumer");
        CommitRepairCommand(c, tx, command.CommandId, digest, inbox.Revision, receipt, "control.repair.requested", thread,
            command.TaskId, command.PartId, null, nowUtc);
        tx.Commit(); return receipt;
    }

    private static T? ReplayRepair<T>(SqliteConnection c, SqliteTransaction? tx, string id, string digest) where T : class
    {
        using var q = ControlSql(c, tx, "SELECT payload_hash,result_json FROM control_receipts WHERE command_id=$id", ("$id", id));
        using var r = q.ExecuteReader(); if (!r.Read()) return null;
        if (r.GetString(0) != digest) throw new InvalidOperationException("Repair command identity reused with different input.");
        return JsonSerializer.Deserialize<T>(r.GetString(1))!;
    }

    private static void CommitRepairCommand(SqliteConnection c, SqliteTransaction tx, string id, string digest, long revision,
        object receipt, string type, string thread, string task, string part, string? run, long now)
    {
        using (var q = ControlSql(c, tx, "UPDATE control_state SET revision=revision+1 WHERE id=1 AND revision=$old", ("$old", revision)))
            if (q.ExecuteNonQuery() != 1) throw new InvalidOperationException("Concurrent repair command.");
        AppendEvent(c, tx, "control:" + id, type, thread, task, part, run, JsonSerializer.Serialize(receipt), now);
        using var save = ControlSql(c, tx, "INSERT INTO control_receipts VALUES($id,$digest,$json)",
            ("$id", id), ("$digest", digest), ("$json", JsonSerializer.Serialize(receipt))); save.ExecuteNonQuery();
    }

    private static IReadOnlyList<ControlRepairState> ReadControlRepairs(SqliteConnection c, SqliteTransaction? tx)
    {
        // A planning-only database has not initialized HostJournal yet.
        using (var any = ControlSql(c, tx, "SELECT EXISTS(SELECT 1 FROM control_repairs)"))
            if (Convert.ToInt64(any.ExecuteScalar()) == 0) return [];
        using var q = ControlSql(c, tx, """
            SELECT r.repair_id,r.package_id,r.package_version,r.task_id,r.thread_id,r.part_id,r.part_revision,r.decision_id,
              r.source_run_id,r.head_sha,r.requested_by,r.reason,r.status,r.run_id,CASE WHEN q.state=3 THEN q.detail ELSE NULL END
              FROM control_repairs r LEFT JOIN host_requests q ON q.run_id=r.run_id ORDER BY r.rowid
            """);
        using var r = q.ExecuteReader(); var rows = new List<ControlRepairState>();
        while (r.Read()) rows.Add(new(r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetString(3), r.GetString(4), r.GetString(5),
            r.GetInt64(6), r.GetString(7), r.GetString(8), r.GetString(9), r.GetString(10), r.GetString(11), r.GetString(12), r.IsDBNull(13) ? null : r.GetString(13))
            { BlockingReason = r.IsDBNull(14) ? null : r.GetString(14) });
        return rows;
    }

    internal sealed record RepairExecution(ControlRepairState State, HarnessRuntime Runtime, string? Provider, string SessionLocator, string IntentId);
    internal RepairExecution ReadRepairExecution(string id)
    { using var c = Open(); using var tx = c.BeginTransaction(); var value = ReadRepairExecution(c, tx, id); tx.Commit(); return value; }

    private static RepairExecution ReadRepairExecution(SqliteConnection c, SqliteTransaction tx, string id)
    {
        var state = ReadControlRepairs(c, tx).Single(r => r.Id == id);
        var package = CurrentControlPackage(c, tx, state.PackageId);
        if (state.Status != "awaiting_consumer" || package.Superseded || !package.Approved || package.Version != state.PackageVersion ||
            !ControlReviewAdmissible(c, tx, state.TaskId) || !ControlKnowledgeSatisfied(c, tx, package) ||
            !ControlDependenciesSatisfied(c, tx, package.Spec, state.TaskId) || !ControlRelationsAdmitBinding(c, tx, state.PackageId, state.TaskId))
            throw new InvalidOperationException("Repair contract or ownership is no longer eligible.");
        var part = ReadControlDecisionParts(c, tx, state.TaskId).Single(p => p.PartId == state.PartId);
        if (part.Status != DeliveryPartStatus.ReworkRequested || part.Revision != state.PartRevision || part.ReviewId != state.DecisionId ||
            part.RunId != state.SourceRunId || part.HeadSha != state.HeadSha)
            throw new InvalidOperationException("Repair decision or delivery changed before dispatch.");
        using var q = ControlSql(c, tx, """
            SELECT q.payload_json,h.session_locator,b.intent_id,q.request_hash FROM host_requests q JOIN harness_runs h ON h.run_id=q.run_id
              JOIN control_writer_bindings b ON b.run_id=q.run_id JOIN harness_bindings s ON s.thread_id=b.thread_id
              JOIN threads t ON t.thread_id=b.thread_id
            WHERE q.run_id=$run AND q.state=2 AND h.status='Completed' AND lower(h.settle_head)=lower($head)
              AND h.session_locator IS NOT NULL AND h.session_locator=s.session_locator AND h.session_id=s.session_id
              AND h.output IS NOT NULL AND t.saved_session_id IS NULL
              AND t.harness=$harness AND t.model=$model AND t.status=0
            """, ("$run", state.SourceRunId), ("$head", state.HeadSha), ("$harness", package.Spec.Harness), ("$model", package.Spec.Model));
        using var r = q.ExecuteReader();
        if (!r.Read()) throw new InvalidOperationException("Repair requires the original managed session, runtime and settled repository provenance.");
        var payload = r.GetString(0);
        if (HostJournal.Hash(payload) != r.GetString(3)) throw new InvalidOperationException("Original runtime payload no longer matches its journal.");
        var dispatch = HostJournal.ReadPayload(payload);
        if (dispatch.Request.ThreadId != state.ThreadId || dispatch.Request.TaskId != state.TaskId ||
            dispatch.Request.Model != package.Spec.Model || dispatch.Runtime.Harness != package.Spec.Harness)
            throw new InvalidOperationException("Original runtime and execution identity do not match the repair contract.");
        return new(state, dispatch.Runtime, dispatch.Request.Provider, r.GetString(1), r.GetString(2));
    }

    internal ControlRepairQueueReceipt? ReplayRepairBinding(ControlRepairBinding binding)
    { using var c = Open(); return ReplayRepair<ControlRepairQueueReceipt>(c, null, binding.CommandId, Sha("repair-binding:" + JsonSerializer.Serialize(binding))); }

    internal ControlRepairQueueReceipt QueueControlRepair(ControlRepairBinding binding, long nowUtc)
    {
        var digest = Sha("repair-binding:" + JsonSerializer.Serialize(binding));
        using var c = Open(); using var tx = c.BeginTransaction();
        if (ReplayRepair<ControlRepairQueueReceipt>(c, tx, binding.CommandId, digest) is { } replay) return replay;
        var state = ReadControlSnapshot(c, tx);
        if (state.Revision != binding.ExpectedRevision || !ControlExecutionMode.AllowsDispatch(state.Mode))
            throw new InvalidOperationException("Repair dispatch requires current autonomous or semi-automatic authority.");
        var execution = ReadRepairExecution(c, tx, binding.RepairId); var repair = execution.State;
        var package = CurrentControlPackage(c, tx, repair.PackageId);
        var thread = GetThread(c, tx, repair.ThreadId);
        using (var busy = ControlSql(c, tx, """
            SELECT EXISTS(SELECT 1 FROM host_requests q JOIN threads t ON t.thread_id=q.thread_id
              WHERE q.state IN (0,1) AND upper(t.worktree)=upper($path)) OR
              EXISTS(SELECT 1 FROM harness_runs WHERE worktree=$normalized AND status IN ('Dispatching','Running','Unknown'))
            """, ("$path", thread.Worktree), ("$normalized", NormalizeControlWorktree(thread.Worktree))))
            if (Convert.ToInt64(busy.ExecuteScalar()) != 0) throw new InvalidOperationException("A writer already owns the repair worktree.");
        var runId = "repair-" + Sha(repair.Id);
        var findings = ReadControlDecisionParts(c, tx, repair.TaskId).Single(p => p.PartId == repair.PartId).ReviewFindings;
        var prompt = BuildControlPrompt(package.Spec, package.Version) + BuildControlKnowledgePrompt(c, tx, repair.PackageId, repair.PackageVersion) +
            "\nRepair only the named delivery part. Preserve other accepted parts. Return evidence; never claim acceptance or integration.\n" +
            JsonSerializer.Serialize(new { repair.PartId, sourceRevision = repair.PartRevision, repair.HeadSha, findings, repair.Reason });
        var request = new HarnessRequest(runId, repair.ThreadId, repair.TaskId, package.Spec.Model, prompt, execution.Provider);
        var payload = HostJournal.Payload(execution.Runtime, request); var hash = HostJournal.Hash(payload);
        if (!HostJournal.Enqueue(c, tx, runId, repair.ThreadId, repair.TaskId, payload, nowUtc))
            throw new InvalidOperationException("An unmatched repair queue row requires reconciliation.");
        using (var q = ControlSql(c, tx, """
            INSERT INTO control_repair_bindings VALUES($id,$intent,$run,$thread,$task,$hash);
            UPDATE control_repairs SET status='queued',run_id=$run,payload_hash=$hash WHERE repair_id=$id;
            """, ("$id", repair.Id), ("$intent", execution.IntentId), ("$run", runId), ("$thread", repair.ThreadId), ("$task", repair.TaskId), ("$hash", hash))) q.ExecuteNonQuery();
        var receipt = new ControlRepairQueueReceipt(binding.CommandId, checked(state.Revision + 1), repair.Id, runId, repair.ThreadId, repair.TaskId, repair.PartId);
        CommitRepairCommand(c, tx, binding.CommandId, digest, state.Revision, receipt, "control.repair.queued", repair.ThreadId, repair.TaskId, repair.PartId, runId, nowUtc);
        tx.Commit(); return receipt;
    }

    private static bool? ControlRepairRunAdmissible(SqliteConnection c, SqliteTransaction? tx, string run)
    {
        using (var known = ControlSql(c, tx, "SELECT EXISTS(SELECT 1 FROM control_repairs WHERE run_id=$run) OR EXISTS(SELECT 1 FROM control_repair_bindings WHERE run_id=$run)", ("$run", run)))
            if (Convert.ToInt64(known.ExecuteScalar()) == 0) return null;
        using var q = ControlSql(c, tx, """
            SELECT r.package_id,r.package_version,r.task_id,r.part_id,r.part_revision,r.decision_id,q.request_hash,r.payload_hash,
              t.model,t.harness,t.status,t.saved_session_id,s.mode,r.status
            FROM control_repairs r JOIN control_repair_bindings b ON b.repair_id=r.repair_id AND b.run_id=r.run_id
              JOIN host_requests q ON q.run_id=b.run_id JOIN threads t ON t.thread_id=b.thread_id
              JOIN tasks a ON a.task_id=b.task_id AND a.thread_id=b.thread_id JOIN control_state s ON s.id=1
              JOIN harness_runs h ON h.run_id=r.source_run_id JOIN harness_bindings hb ON hb.thread_id=b.thread_id
            WHERE r.run_id=$run AND h.status='Completed' AND h.session_locator=hb.session_locator AND h.session_id=hb.session_id
            """, ("$run", run));
        string package; long version; string task; string part; long partVersion; string decision; string model; string harness;
        using (var r = q.ExecuteReader())
        {
            if (!r.Read() || r.GetString(6) != r.GetString(7) || r.GetInt32(10) != (int)ThreadStatus.Active || !r.IsDBNull(11) ||
                !ControlExecutionMode.AllowsDispatch(r.GetString(12)) || r.GetString(13) != "queued") return false;
            package = r.GetString(0); version = r.GetInt64(1); task = r.GetString(2); part = r.GetString(3); partVersion = r.GetInt64(4);
            decision = r.GetString(5); model = r.GetString(8); harness = r.GetString(9);
        }
        var current = CurrentControlPackage(c, tx, package);
        if (current.Superseded || !current.Approved || current.Version != version || current.Spec.Model != model || current.Spec.Harness != harness) return false;
        using (var p = ControlSql(c, tx, """
            SELECT EXISTS(SELECT 1 FROM delivery_parts p WHERE p.part_id=$part AND p.task_id=$task AND p.status=2 AND p.current_revision=$v
              AND $decision=(SELECT decision_id FROM part_decisions WHERE part_id=p.part_id AND revision_number=p.current_revision ORDER BY rowid DESC LIMIT 1))
            """, ("$part", part), ("$task", task), ("$v", partVersion), ("$decision", decision)))
            if (Convert.ToInt64(p.ExecuteScalar()) == 0) return false;
        return ControlKnowledgeSatisfied(c, tx, current) && ControlDependenciesSatisfied(c, tx, current.Spec, task) && ControlRelationsAdmitBinding(c, tx, package, task);
    }

    private static bool RecordControlRepairTransition(SqliteConnection c, SqliteTransaction tx, string run, string status, long now)
    {
        using var q = ControlSql(c, tx, "SELECT repair_id,thread_id,task_id,part_id FROM control_repairs WHERE run_id=$run", ("$run", run));
        string id; string thread; string task; string part;
        using (var r = q.ExecuteReader())
        { if (!r.Read()) return false; id = r.GetString(0); thread = r.GetString(1); task = r.GetString(2); part = r.GetString(3); }
        using (var update = ControlSql(c, tx, "UPDATE control_repairs SET status=$status WHERE repair_id=$id; UPDATE control_state SET revision=revision+1 WHERE id=1", ("$status", status), ("$id", id))) update.ExecuteNonQuery();
        AppendEvent(c, tx, "control.repair.transport:" + run + ":" + status, "control.repair." + status, thread, task, part, run,
            JsonSerializer.Serialize(new { repairId = id, status }), now);
        return true;
    }

    internal bool ControlRepairDeliveryAdmissible(string run, string part)
    {
        using var c = Open(); using var tx = c.BeginTransaction();
        return ControlRepairDeliveryAdmissible(c, tx, run, part);
    }

    private static bool ControlRepairDeliveryAdmissible(SqliteConnection c, SqliteTransaction tx, string run, string part)
    {
        var repair = ReadControlRepairs(c, tx).SingleOrDefault(r => r.RunId == run);
        if (repair is null)
        {
            using var known = ControlSql(c, tx, "SELECT EXISTS(SELECT 1 FROM control_repair_bindings WHERE run_id=$run)", ("$run", run));
            return Convert.ToInt64(known.ExecuteScalar()) == 0; // Known damaged repair authority is never a legacy queue.
        }
        var package = CurrentControlPackage(c, tx, repair.PackageId);
        if (repair.PartId != part || package.Superseded || !package.Approved || package.Version != repair.PackageVersion ||
            !ControlReviewAdmissible(c, tx, repair.TaskId)) return false;
        using var q = ControlSql(c, tx, """
            SELECT EXISTS(SELECT 1 FROM delivery_parts p JOIN harness_runs h ON h.run_id=$run
              JOIN harness_runs source ON source.run_id=$source
              WHERE p.part_id=$part AND p.status=2 AND p.current_revision=$v AND h.status='Completed'
                AND h.session_id=source.session_id AND h.session_locator=source.session_locator)
            """, ("$part", part), ("$run", run), ("$source", repair.SourceRunId), ("$v", repair.PartRevision));
        return Convert.ToInt64(q.ExecuteScalar()) != 0;
    }
}
