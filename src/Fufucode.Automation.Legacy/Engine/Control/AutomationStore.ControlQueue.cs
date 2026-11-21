using System.Text.Json;
using Fufucode.App.Abstractions.Automation.Harnesses;
using Fufucode.Core.Automation.Hosting;
using Microsoft.Data.Sqlite;

namespace Fufucode.Core.Automation;

public sealed partial class AutomationStore
{
    internal ControlQueueReceipt? ReplayControlBinding(ControlExecutionBinding binding)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(binding.CommandId);
        using var c = Open();
        return ReplayControlBinding(c, null, binding);
    }

    private static ControlQueueReceipt? ReplayControlBinding(SqliteConnection c, SqliteTransaction? tx,
        ControlExecutionBinding binding)
    {
        using var q = ControlSql(c, tx,
            "SELECT payload_hash,result_json FROM control_receipts WHERE command_id=$id", ("$id", binding.CommandId));
        using var r = q.ExecuteReader();
        if (!r.Read()) return null;
        if (r.GetString(0) != BindingHash(binding))
            throw new InvalidOperationException("Control binding command identity reused with different input.");
        return JsonSerializer.Deserialize<ControlQueueReceipt>(r.GetString(1))!;
    }

    internal ControlQueueReceipt QueueControlExecution(ControlExecutionBinding binding, long nowUtc)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        if (ReplayControlBinding(c, tx, binding) is { } replay) return replay;
        using var state = ControlSql(c, tx, "SELECT revision,mode FROM control_state WHERE id=1");
        long revision;
        using (var r = state.ExecuteReader())
        {
            r.Read(); revision = r.GetInt64(0);
            if (revision != binding.ExpectedRevision || !ControlExecutionMode.AllowsDispatch(r.GetString(1)))
                throw new InvalidOperationException("Binding requires current state and an autonomous or semi-automatic mode.");
        }
        WorkPackageSpec spec;
        using (var intent = ControlSql(c, tx, """
            SELECT i.package_id,i.version,i.status,p.version,p.approved_version,v.spec_json
            FROM control_intents i JOIN control_packages p ON p.package_id=i.package_id
            JOIN control_package_versions v ON v.package_id=i.package_id AND v.version=i.version
            WHERE i.intent_id=$id
            """, ("$id", binding.IntentId)))
        using (var r = intent.ExecuteReader())
        {
            if (!r.Read()) throw new KeyNotFoundException(binding.IntentId);
            if (r.GetInt64(1) != binding.PackageVersion || r.GetString(2) != "awaiting_binding" ||
                r.GetInt64(3) != binding.PackageVersion || r.IsDBNull(4) || r.GetInt64(4) != binding.PackageVersion)
                throw new InvalidOperationException("The intent is obsolete, unapproved or already bound.");
            spec = JsonSerializer.Deserialize<WorkPackageSpec>(r.GetString(5))!;
        }
        ValidatePackage(spec);
        if (!ControlKnowledgeSatisfied(c, tx, CurrentControlPackage(c, tx, spec.Id)))
            throw new InvalidOperationException("A published knowledge update requires an explicit adoption decision before binding.");
        ArgumentException.ThrowIfNullOrWhiteSpace(binding.Runtime.Version);
        _ = HarnessAdapters.Get(binding.Runtime.Harness);
        var thread = GetThread(c, tx, binding.ThreadId);
        if (thread.Status != ThreadStatus.Active || thread.SavedSessionId is not null ||
            thread.Harness != spec.Harness || binding.Runtime.Harness != spec.Harness || thread.Model != spec.Model)
            throw new InvalidOperationException("Registered thread/runtime/model is incompatible or needs explicit ownership import.");
        using (var task = ControlSql(c, tx,
            "SELECT thread_id,status FROM tasks WHERE task_id=$id", ("$id", binding.TaskId)))
        using (var r = task.ExecuteReader())
        {
            if (!r.Read() || r.GetString(0) != binding.ThreadId ||
                r.GetInt32(1) != (int)AutomationTaskStatus.Planned)
                throw new InvalidOperationException("Initial execution must bind a planned task in the registered thread.");
        }
        if (!ControlDependenciesSatisfied(c, tx, spec, binding.TaskId) ||
            !ControlRelationsAdmitBinding(c, tx, spec.Id, binding.TaskId))
            throw new InvalidOperationException("Dependency evidence or the shared group baseline is not ready.");
        var worktree = NormalizeControlWorktree(thread.Worktree);
        using (var busy = ControlSql(c, tx, """
            SELECT EXISTS(SELECT 1 FROM harness_runs
                WHERE (thread_id=$thread OR worktree=$path) AND status IN ('Dispatching','Running','Unknown'))
              OR EXISTS(SELECT 1 FROM host_requests q JOIN threads t ON t.thread_id=q.thread_id
                WHERE q.state IN (0,1) AND (q.thread_id=$thread OR upper(t.worktree)=upper($path)))
            """, ("$thread", binding.ThreadId), ("$path", worktree)))
            if (Convert.ToInt64(busy.ExecuteScalar()) != 0)
                throw new InvalidOperationException("A queued, active or unknown writer already holds this thread/worktree.");

        // Stable run identity derives from the durable intent, not from retries or a new GUID.
        var runId = "control-" + Sha(binding.IntentId);
        var request = new HarnessRequest(runId, binding.ThreadId, binding.TaskId, spec.Model,
            BuildControlPrompt(spec, binding.PackageVersion) + BuildControlKnowledgePrompt(c, tx, spec.Id, binding.PackageVersion), binding.Provider);
        var payload = HostJournal.Payload(binding.Runtime, request);
        if (!HostJournal.Enqueue(c, tx, runId, binding.ThreadId, binding.TaskId, payload, nowUtc))
            throw new InvalidOperationException("A queue row already exists without this binding; reconcile, never redispatch.");
        using (var save = ControlSql(c, tx, """
            INSERT INTO control_bindings(intent_id,run_id,thread_id,task_id,payload_hash)
            VALUES($intent,$run,$thread,$task,$hash);
            UPDATE control_intents SET status='queued' WHERE intent_id=$intent;
            UPDATE control_state SET revision=revision+1 WHERE id=1;
            """, ("$intent", binding.IntentId), ("$run", runId), ("$thread", binding.ThreadId),
            ("$task", binding.TaskId), ("$hash", HostJournal.Hash(payload))))
            save.ExecuteNonQuery();
        var receipt = new ControlQueueReceipt(binding.CommandId, checked(revision + 1), binding.IntentId,
            runId, binding.ThreadId, binding.TaskId);
        AppendEvent(c, tx, "control:" + binding.CommandId, "control.intent.queued", binding.ThreadId,
            binding.TaskId, null, runId, JsonSerializer.Serialize(receipt), nowUtc);
        using (var save = ControlSql(c, tx,
            "INSERT INTO control_receipts VALUES($cmd,$hash,$json)", ("$cmd", binding.CommandId),
            ("$hash", BindingHash(binding)), ("$json", JsonSerializer.Serialize(receipt))))
            save.ExecuteNonQuery();
        tx.Commit();
        return receipt;
    }

    private static string BindingHash(ControlExecutionBinding binding) => Sha("binding:" + JsonSerializer.Serialize(binding));
    private static string NormalizeControlWorktree(string path)
    {
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;
    }

    private static string BuildControlPrompt(WorkPackageSpec spec, long version) =>
        "Execute the approved work package. Do not expand its scope or acceptance requirements.\n" +
        JsonSerializer.Serialize(new { packageId = spec.Id, version, spec.Title, spec.Goal,
            spec.Scope, spec.Acceptance, spec.DependsOn }) +
        "\nKeep existing user changes. Report real evidence and unverified items; do not claim integration.";

    /// <summary>Checked at binding and again inside the queue's atomic start transition.</summary>
    internal static bool ControlRunAdmissible(SqliteConnection c, SqliteTransaction? tx, string runId)
    {
        if (ControlRepairRunAdmissible(c, tx, runId) is { } repairAllowed) return repairAllowed;
        using (var exists = ControlSql(c, tx, "SELECT EXISTS(SELECT 1 FROM control_bindings WHERE run_id=$run)",
            ("$run", runId)))
            if (Convert.ToInt64(exists.ExecuteScalar()) == 0) return true; // Explicit legacy queue, not adopted.
        WorkPackageSpec spec;
        string taskId;
        using (var q = ControlSql(c, tx, """
            SELECT i.status,i.version,p.version,p.approved_version,s.mode,v.spec_json,b.task_id,
                   t.status,t.saved_session_id,t.harness,t.model,b.payload_hash,q.request_hash,target.status
            FROM control_bindings b JOIN control_intents i ON i.intent_id=b.intent_id
            JOIN control_packages p ON p.package_id=i.package_id
            JOIN control_package_versions v ON v.package_id=p.package_id AND v.version=p.version
            JOIN control_state s ON s.id=1 JOIN threads t ON t.thread_id=b.thread_id
            JOIN tasks target ON target.task_id=b.task_id AND target.thread_id=b.thread_id
            JOIN host_requests q ON q.run_id=b.run_id WHERE b.run_id=$run
            """, ("$run", runId)))
        using (var r = q.ExecuteReader())
        {
            if (!r.Read()) return false; // A known binding with missing authority is never legacy work.
            if (r.GetString(0) != "queued" || r.GetInt64(1) != r.GetInt64(2) || r.IsDBNull(3) ||
                r.GetInt64(3) != r.GetInt64(2) || !ControlExecutionMode.AllowsDispatch(r.GetString(4)) ||
                r.GetInt32(7) != (int)ThreadStatus.Active || !r.IsDBNull(8) || r.GetString(11) != r.GetString(12) ||
                r.GetInt32(13) != (int)AutomationTaskStatus.Planned)
                return false;
            spec = JsonSerializer.Deserialize<WorkPackageSpec>(r.GetString(5))!;
            taskId = r.GetString(6);
            if (r.GetString(9) != spec.Harness || r.GetString(10) != spec.Model) return false;
        }
        return ControlKnowledgeSatisfied(c, tx, CurrentControlPackage(c, tx, spec.Id)) &&
            ControlDependenciesSatisfied(c, tx, spec, taskId) && ControlRelationsAdmitBinding(c, tx, spec.Id, taskId);
    }

    private static bool ControlDependenciesSatisfied(SqliteConnection c, SqliteTransaction? tx,
        WorkPackageSpec spec, string taskId)
    {
        foreach (var dependency in spec.DependsOn)
        {
            if (!ControlKnowledgeSatisfied(c, tx, CurrentControlPackage(c, tx, dependency))) return false;
            string upstreamTask;
            bool requiresIntegration;
            using (var q = ControlSql(c, tx, """
                SELECT b.task_id,v.spec_json FROM control_packages p
                JOIN control_package_versions v ON v.package_id=p.package_id AND v.version=p.version
                JOIN control_intents i ON i.package_id=p.package_id AND i.version=p.version
                JOIN control_bindings b ON b.intent_id=i.intent_id WHERE p.package_id=$id
                """, ("$id", dependency)))
            using (var r = q.ExecuteReader())
            {
                if (!r.Read()) return false;
                upstreamTask = r.GetString(0);
                requiresIntegration = JsonSerializer.Deserialize<WorkPackageSpec>(r.GetString(1))!.Artifact == "code";
            }
            if (!ControlPartsSatisfied(c, tx, upstreamTask, null, requiresIntegration)) return false;
        }
        var edges = new List<(string Task, string? Part, bool Integrated)>();
        using (var q = ControlSql(c, tx, """
            SELECT depends_on_task_id,depends_on_part_id,requires_integration FROM task_dependencies WHERE task_id=$id
            """, ("$id", taskId)))
        using (var r = q.ExecuteReader())
            while (r.Read()) edges.Add((r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetInt32(2) == 1));
        return edges.All(e => ControlPartsSatisfied(c, tx, e.Task, e.Part, e.Integrated));
    }

    private static bool ControlPartsSatisfied(SqliteConnection c, SqliteTransaction? tx,
        string taskId, string? partId, bool requiresIntegration)
    {
        using var q = ControlSql(c, tx, """
            SELECT p.status,p.current_revision,EXISTS(SELECT 1 FROM integration_marks m
              WHERE m.part_id=p.part_id AND m.revision_number=p.current_revision)
            FROM delivery_parts p WHERE p.task_id=$task AND ($part IS NULL OR p.part_id=$part)
            """, ("$task", taskId), ("$part", (object?)partId ?? DBNull.Value));
        using var r = q.ExecuteReader();
        var any = false;
        while (r.Read())
        {
            any = true;
            if (r.GetInt32(0) != (int)DeliveryPartStatus.Accepted || r.GetInt64(1) <= 0 ||
                (requiresIntegration && r.GetInt32(2) != 1)) return false;
        }
        return any;
    }

    private static void EnsureControlPackagesStopped(SqliteConnection c, SqliteTransaction tx, IEnumerable<string> ids)
    {
        foreach (var id in ids)
        {
            if (!ControlPackageHasBinding(c, tx, id)) continue;
            if (ControlAdditionalWriterBlock(c, tx, id) is { } blocked)
                throw new InvalidOperationException("Contract mutation is blocked: " + blocked);
            using var q = ControlSql(c, tx, """
                SELECT EXISTS(SELECT 1 FROM control_writer_bindings b JOIN control_intents i ON i.intent_id=b.intent_id
                  JOIN host_requests q ON q.run_id=b.run_id LEFT JOIN harness_runs h ON h.run_id=b.run_id
                  WHERE i.package_id=$id AND (q.state=1 OR h.status IN ('Dispatching','Running','Unknown')))
                """, ("$id", id));
            if (Convert.ToInt64(q.ExecuteScalar()) != 0)
                throw new InvalidOperationException("An affected writer has started; require stopped evidence before changing its contract.");
        }
    }

    private static void SupersedeControlIntents(SqliteConnection c, SqliteTransaction tx, string packageId, long nowUtc)
    {
        if (ControlPackageHasBinding(c, tx, packageId))
        {
            using var cancel = ControlSql(c, tx, """
            UPDATE host_requests SET state=3,settled_utc=$now,detail='Control contract superseded before start.'
            WHERE state=0 AND run_id IN (SELECT b.run_id FROM control_writer_bindings b
              JOIN control_intents i ON i.intent_id=b.intent_id WHERE i.package_id=$id);
            """, ("$id", packageId), ("$now", nowUtc));
            cancel.ExecuteNonQuery();
        }
        using var q = ControlSql(c, tx, "UPDATE control_intents SET status='superseded' WHERE package_id=$id",
            ("$id", packageId));
        q.ExecuteNonQuery();
        using var repairs = ControlSql(c, tx, "UPDATE control_repairs SET status='superseded' WHERE package_id=$id AND status IN ('awaiting_consumer','queued')", ("$id", packageId));
        repairs.ExecuteNonQuery();
    }

    private static bool ControlPackageHasBinding(SqliteConnection c, SqliteTransaction tx, string packageId)
    {
        using var q = ControlSql(c, tx, """
            SELECT EXISTS(SELECT 1 FROM control_bindings b JOIN control_intents i ON i.intent_id=b.intent_id
              WHERE i.package_id=$id)
            """, ("$id", packageId));
        return Convert.ToInt64(q.ExecuteScalar()) != 0;
    }

    internal static void RecordControlQueueTransition(SqliteConnection c, SqliteTransaction tx,
        string runId, string status, long nowUtc)
    {
        if (RecordControlRepairTransition(c, tx, runId, status, nowUtc)) return;
        string thread;
        string task;
        string intent;
        using (var q = ControlSql(c, tx,
            "SELECT thread_id,task_id,intent_id FROM control_bindings WHERE run_id=$run", ("$run", runId)))
        using (var r = q.ExecuteReader())
        {
            if (!r.Read()) return;
            thread = r.GetString(0); task = r.GetString(1); intent = r.GetString(2);
        }
        using (var q = ControlSql(c, tx, """
            UPDATE control_intents SET status=$status WHERE intent_id=$id;
            UPDATE control_state SET revision=revision+1 WHERE id=1;
            """, ("$status", status), ("$id", intent))) q.ExecuteNonQuery();
        AppendEvent(c, tx, "control.transport:" + runId + ":" + status, "control.intent." + status,
            thread, task, null, runId, JsonSerializer.Serialize(new { intentId = intent, status }), nowUtc);
    }
}
