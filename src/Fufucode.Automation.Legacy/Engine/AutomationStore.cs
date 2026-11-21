using Microsoft.Data.Sqlite;

namespace Fufucode.Core.Automation;

/// <summary>
/// U1 persistent automation store. Owns threads, tasks, delivery parts/revisions,
/// part decisions, deduplicated events and commands, delivery claims, the
/// versioned task DAG, single-writer leases and crash recovery.
/// One SQLite file per project; every mutation runs inside a transaction.
/// </summary>
public sealed partial class AutomationStore : IDisposable
{
    public const int SchemaVersion = 2;
    private const int DecisionSchemaVersion = 1;

    private readonly string _path;
    private readonly bool _existingOnly;
    private string? _clientAuthority;

    /// <summary>An explicit client connection never creates or upgrades the authority database.</summary>
    public static AutomationStore AttachControlClient(string databasePath) => new(databasePath, existingOnly: true);

    private AutomationStore(string databasePath, bool existingOnly)
    {
        if (!Path.IsPathFullyQualified(databasePath) || !File.Exists(databasePath))
            throw new FileNotFoundException("Select an existing absolute workbench database.", databasePath);
        _path = databasePath; _existingOnly = existingOnly;
        using var c = Open();
        using var q = c.CreateCommand(); q.CommandText = "PRAGMA user_version";
        if (Convert.ToInt32(q.ExecuteScalar()) != SchemaVersion)
            throw new InvalidOperationException("The selected workbench database needs its owner's schema upgrade before connecting.");
        q.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name IN ('control_state','control_receipts','control_repairs') AND type='table'";
        if (Convert.ToInt64(q.ExecuteScalar()) != 3)
            throw new InvalidOperationException("The selected database is not a current control workbench. No migration was applied.");
        using var tx = c.BeginTransaction(); _clientAuthority = ReadControlAuthorityIdentity(c, tx);
    }

    public AutomationStore(string databasePath)
    {
        _path = databasePath;
        var directory = Path.GetDirectoryName(Path.GetFullPath(_path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        Migrate();
        MigrateControl();
    }

    public string DatabasePath => _path;

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _path, Mode = _existingOnly ? SqliteOpenMode.ReadWrite : SqliteOpenMode.ReadWriteCreate, Pooling = false,
            DefaultTimeout = 10
        }.ToString());
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        pragma.ExecuteNonQuery();
        if (_clientAuthority is not null)
        {
            using var q = connection.CreateCommand();
            q.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='control_authority'";
            var present = Convert.ToInt64(q.ExecuteScalar()) == 1;
            q.CommandText = "SELECT identity FROM control_authority WHERE id=1";
            if (!present || !string.Equals(q.ExecuteScalar() as string, _clientAuthority, StringComparison.Ordinal))
            { connection.Dispose(); throw new InvalidOperationException("Workbench authority changed; reconnect and inspect before any operation."); }
        }
        return connection;
    }

    private void Migrate()
    {
        using var connection = Open();
        using var versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version";
        var version = Convert.ToInt32(versionCommand.ExecuteScalar());
        if (version > SchemaVersion)
            throw new InvalidOperationException($"Automation schema {version} is newer than this build.");
        if (version == SchemaVersion) return;
        if (version == 1) { MigrateReviewHistory(connection); return; }
        using var tx = connection.BeginTransaction();
        Execute(connection, tx, """
            CREATE TABLE threads (
              thread_id TEXT PRIMARY KEY,
              project_id TEXT NOT NULL,
              harness TEXT NOT NULL,
              model TEXT NOT NULL,
              worktree TEXT NOT NULL,
              saved_session_id TEXT,
              status INTEGER NOT NULL,
              freeze_reason TEXT,
              created_utc INTEGER NOT NULL
            );
            CREATE TABLE tasks (
              task_id TEXT PRIMARY KEY,
              thread_id TEXT NOT NULL REFERENCES threads(thread_id),
              title TEXT NOT NULL,
              status INTEGER NOT NULL,
              base_sha TEXT NOT NULL,
              head_sha TEXT,
              run_id TEXT,
              created_utc INTEGER NOT NULL
            );
            CREATE INDEX idx_tasks_thread ON tasks(thread_id);
            CREATE TABLE delivery_parts (
              part_id TEXT PRIMARY KEY,
              task_id TEXT NOT NULL REFERENCES tasks(task_id),
              title TEXT NOT NULL,
              status INTEGER NOT NULL,
              current_revision INTEGER NOT NULL DEFAULT 0,
              last_decision_by TEXT,
              updated_utc INTEGER NOT NULL
            );
            CREATE INDEX idx_parts_task ON delivery_parts(task_id);
            CREATE TABLE delivery_revisions (
              revision_id TEXT PRIMARY KEY,
              part_id TEXT NOT NULL REFERENCES delivery_parts(part_id),
              revision_number INTEGER NOT NULL,
              run_id TEXT NOT NULL,
              head_sha TEXT NOT NULL,
              card_json TEXT NOT NULL,
              received_utc INTEGER NOT NULL,
              UNIQUE(part_id, revision_number)
            );
            -- Recorded decisions are immutable: one row per (part, revision),
            -- insert-only enforced by the primary key and code path.
            CREATE TABLE part_decisions (
              decision_id TEXT PRIMARY KEY,
              part_id TEXT NOT NULL REFERENCES delivery_parts(part_id),
              revision_number INTEGER NOT NULL,
              decision INTEGER NOT NULL,
              findings TEXT,
              evidence_json TEXT,
              decided_by TEXT NOT NULL,
              decided_utc INTEGER NOT NULL,
              decision_schema_version INTEGER NOT NULL,
              UNIQUE(part_id, revision_number)
            );
            -- Undo fence: integration marks a baseline so a later rework
            -- never silently rewinds already-integrated state.
            CREATE TABLE integration_marks (
              part_id TEXT NOT NULL REFERENCES delivery_parts(part_id),
              revision_number INTEGER NOT NULL,
              main_sha TEXT NOT NULL,
              integrated_utc INTEGER NOT NULL,
              PRIMARY KEY(part_id, revision_number)
            );
            CREATE TABLE task_dependencies (
              task_id TEXT NOT NULL REFERENCES tasks(task_id),
              depends_on_task_id TEXT NOT NULL REFERENCES tasks(task_id),
              depends_on_part_id TEXT,
              requires_integration INTEGER NOT NULL,
              PRIMARY KEY(task_id, depends_on_task_id, depends_on_part_id)
            );
            -- Event dedup: same event_id replays only with an identical payload.
            CREATE TABLE automation_events (
              cursor INTEGER PRIMARY KEY AUTOINCREMENT,
              event_id TEXT NOT NULL UNIQUE,
              type TEXT NOT NULL,
              thread_id TEXT NOT NULL,
              task_id TEXT,
              part_id TEXT,
              run_id TEXT,
              payload_hash TEXT NOT NULL,
              payload_json TEXT NOT NULL,
              occurred_utc INTEGER NOT NULL
            );
            -- Command dedup: a repeated command id is a no-op, not a second action.
            CREATE TABLE automation_commands (
              command_id TEXT PRIMARY KEY,
              type TEXT NOT NULL,
              thread_id TEXT NOT NULL,
              task_id TEXT,
              part_id TEXT,
              payload_json TEXT NOT NULL,
              issued_utc INTEGER NOT NULL,
              applied_utc INTEGER
            );
            -- Delivery claims persist before dispatch; unknown outcomes freeze
            -- the thread and are never auto-redelivered.
            CREATE TABLE delivery_claims (
              claim_id TEXT PRIMARY KEY,
              task_id TEXT NOT NULL REFERENCES tasks(task_id),
              part_id TEXT NOT NULL,
              run_id TEXT NOT NULL,
              state INTEGER NOT NULL,
              claimed_utc INTEGER NOT NULL,
              resolved_utc INTEGER
            );
            CREATE INDEX idx_claims_task ON delivery_claims(task_id, state);
            -- Single-writer leases: one managed task writer per task,
            -- one integrator per project.
            CREATE TABLE write_leases (
              lease_id TEXT PRIMARY KEY,
              kind INTEGER NOT NULL,
              scope_id TEXT NOT NULL,
              holder_id TEXT NOT NULL,
              acquired_utc INTEGER NOT NULL,
              expires_utc INTEGER NOT NULL,
              released_utc INTEGER
            );
            CREATE INDEX idx_leases_scope ON write_leases(kind, scope_id, released_utc);
            """);
        Execute(connection, tx, "PRAGMA user_version=1");
        tx.Commit();
        MigrateReviewHistory(connection);
    }

    private static void MigrateReviewHistory(SqliteConnection connection)
    {
        using var tx = connection.BeginTransaction();
        using (var version = connection.CreateCommand())
        {
            version.Transaction = tx; version.CommandText = "PRAGMA user_version";
            var current = Convert.ToInt32(version.ExecuteScalar());
            if (current == SchemaVersion) { tx.Commit(); return; }
            if (current != 1) throw new InvalidOperationException("Automation schema changed during migration; reopen with a compatible reader.");
        }
        // Preserve every immutable decision and its insertion order. Old binaries
        // reject schema 2 rather than silently interpreting a reopened review.
        Execute(connection, tx, """
            CREATE TABLE part_decisions_v2 (
              decision_id TEXT PRIMARY KEY, part_id TEXT NOT NULL REFERENCES delivery_parts(part_id),
              revision_number INTEGER NOT NULL, decision INTEGER NOT NULL, findings TEXT,
              evidence_json TEXT, decided_by TEXT NOT NULL, decided_utc INTEGER NOT NULL,
              decision_schema_version INTEGER NOT NULL);
            INSERT INTO part_decisions_v2 SELECT * FROM part_decisions ORDER BY rowid;
            DROP TABLE part_decisions;
            ALTER TABLE part_decisions_v2 RENAME TO part_decisions;
            CREATE INDEX idx_decisions_part_revision ON part_decisions(part_id,revision_number);
            PRAGMA user_version=2;
            """);
        tx.Commit();
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction? tx, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string Sha(string value)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes);
    }

    // ---------------------------------------------------------------- threads

    public AutomationThread CreateThread(string threadId, string projectId, string harness,
        string model, string worktree, string? savedSessionId, long nowUtc)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO threads(thread_id, project_id, harness, model, worktree,
                                saved_session_id, status, freeze_reason, created_utc)
            VALUES($id, $project, $harness, $model, $worktree, $session, $status, NULL, $now);
            """;
        command.Parameters.AddWithValue("$id", threadId);
        command.Parameters.AddWithValue("$project", projectId);
        command.Parameters.AddWithValue("$harness", harness);
        command.Parameters.AddWithValue("$model", model);
        command.Parameters.AddWithValue("$worktree", worktree);
        command.Parameters.AddWithValue("$session", (object?)savedSessionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", (int)ThreadStatus.Active);
        command.Parameters.AddWithValue("$now", nowUtc);
        command.ExecuteNonQuery();
        return new AutomationThread(threadId, projectId, harness, model, worktree,
            savedSessionId, ThreadStatus.Active, nowUtc, null);
    }

    public AutomationThread GetThread(string threadId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM threads WHERE thread_id=$id";
        command.Parameters.AddWithValue("$id", threadId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new KeyNotFoundException($"Thread '{threadId}' was not found.");
        return ReadThread(reader);
    }

    public List<AutomationThread> ListThreads()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM threads ORDER BY created_utc";
        using var reader = command.ExecuteReader();
        var list = new List<AutomationThread>();
        while (reader.Read()) list.Add(ReadThread(reader));
        return list;
    }

    private static AutomationThread ReadThread(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
        r.IsDBNull(5) ? null : r.GetString(5), (ThreadStatus)r.GetInt32(6),
        r.IsDBNull(8) ? 0 : r.GetInt64(8), r.IsDBNull(7) ? null : r.GetString(7));

    /// <summary>Freeze a thread: shared-M state or a delivery outcome is unknown.</summary>
    public void FreezeThread(string threadId, string reason, string eventId, long nowUtc)
    {
        using var connection = Open();
        using var tx = connection.BeginTransaction();
        var thread = GetThread(connection, tx, threadId);
        if (thread.Status == ThreadStatus.Frozen &&
            string.Equals(thread.FreezeReason, reason, StringComparison.Ordinal))
        {
            tx.Commit();
            return; // idempotent
        }
        SetThreadStatus(connection, tx, threadId, ThreadStatus.Frozen, reason);
        AppendEvent(connection, tx, eventId, "thread.frozen", threadId, null, null, null,
            "{\"reason\":\"" + reason.Replace("\"", "\\\"") + "\"}", nowUtc);
        tx.Commit();
    }

    public void UnfreezeThread(string threadId, string eventId, long nowUtc)
    {
        using var connection = Open();
        using var tx = connection.BeginTransaction();
        var thread = GetThread(connection, tx, threadId);
        if (thread.Status != ThreadStatus.Frozen) { tx.Commit(); return; }
        SetThreadStatus(connection, tx, threadId, ThreadStatus.Active, null);
        AppendEvent(connection, tx, eventId, "thread.unfrozen", threadId, null, null, null, "{}", nowUtc);
        tx.Commit();
    }

    private static AutomationThread GetThread(SqliteConnection connection, SqliteTransaction tx, string threadId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT * FROM threads WHERE thread_id=$id";
        command.Parameters.AddWithValue("$id", threadId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new KeyNotFoundException($"Thread '{threadId}' was not found.");
        return ReadThread(reader);
    }

    private static void SetThreadStatus(SqliteConnection connection, SqliteTransaction tx,
        string threadId, ThreadStatus status, string? freezeReason)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "UPDATE threads SET status=$s, freeze_reason=$r WHERE thread_id=$id";
        command.Parameters.AddWithValue("$s", (int)status);
        command.Parameters.AddWithValue("$r", (object?)freezeReason ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", threadId);
        command.ExecuteNonQuery();
    }

    // ------------------------------------------------------------------ tasks

    public AutomationTask CreateTask(string taskId, string threadId, string title,
        string baseSha, long nowUtc)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO tasks(task_id, thread_id, title, status, base_sha, head_sha, run_id, created_utc)
            VALUES($id, $thread, $title, $status, $base, NULL, NULL, $now);
            """;
        command.Parameters.AddWithValue("$id", taskId);
        command.Parameters.AddWithValue("$thread", threadId);
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$status", (int)AutomationTaskStatus.Planned);
        command.Parameters.AddWithValue("$base", baseSha);
        command.Parameters.AddWithValue("$now", nowUtc);
        command.ExecuteNonQuery();
        return new AutomationTask(taskId, threadId, title, AutomationTaskStatus.Planned,
            baseSha, null, null, nowUtc);
    }

    public AutomationTask GetTask(string taskId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM tasks WHERE task_id=$id";
        command.Parameters.AddWithValue("$id", taskId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new KeyNotFoundException($"Task '{taskId}' was not found.");
        return ReadTask(reader);
    }

    public List<AutomationTask> ListTasks(string threadId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM tasks WHERE thread_id=$id ORDER BY created_utc";
        command.Parameters.AddWithValue("$id", threadId);
        using var reader = command.ExecuteReader();
        var list = new List<AutomationTask>();
        while (reader.Read()) list.Add(ReadTask(reader));
        return list;
    }

    private static AutomationTask ReadTask(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), (AutomationTaskStatus)r.GetInt32(3),
        r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5),
        r.IsDBNull(6) ? null : r.GetString(6), r.GetInt64(7));

    private static void SetTaskStatus(SqliteConnection connection, SqliteTransaction tx,
        string taskId, AutomationTaskStatus status, string? headSha = null, string? runId = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            UPDATE tasks SET status=$s,
              head_sha = COALESCE($head, head_sha),
              run_id = COALESCE($run, run_id)
            WHERE task_id=$id
            """;
        command.Parameters.AddWithValue("$s", (int)status);
        command.Parameters.AddWithValue("$head", (object?)headSha ?? DBNull.Value);
        command.Parameters.AddWithValue("$run", (object?)runId ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", taskId);
        command.ExecuteNonQuery();
    }

    // ------------------------------------------------------------ parts & revisions

    public DeliveryPart CreatePart(string partId, string taskId, string title, long nowUtc)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO delivery_parts(part_id, task_id, title, status, current_revision,
                                       last_decision_by, updated_utc)
            VALUES($id, $task, $title, $status, 0, NULL, $now);
            """;
        command.Parameters.AddWithValue("$id", partId);
        command.Parameters.AddWithValue("$task", taskId);
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$status", (int)DeliveryPartStatus.Delivered);
        command.Parameters.AddWithValue("$now", nowUtc);
        command.ExecuteNonQuery();
        return new DeliveryPart(partId, taskId, title, DeliveryPartStatus.Delivered, 0, null, nowUtc);
    }

    public DeliveryPart GetPart(string partId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM delivery_parts WHERE part_id=$id";
        command.Parameters.AddWithValue("$id", partId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new KeyNotFoundException($"Part '{partId}' was not found.");
        return ReadPart(reader);
    }

    public List<DeliveryPart> ListParts(string taskId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM delivery_parts WHERE task_id=$id ORDER BY part_id";
        command.Parameters.AddWithValue("$id", taskId);
        using var reader = command.ExecuteReader();
        var list = new List<DeliveryPart>();
        while (reader.Read()) list.Add(ReadPart(reader));
        return list;
    }

    private static DeliveryPart ReadPart(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), (DeliveryPartStatus)r.GetInt32(3),
        r.GetInt64(4), r.IsDBNull(5) ? null : r.GetString(5), r.GetInt64(6));

    /// <summary>
    /// Record a delivered revision for a part. A reworked part gets a new revision
    /// number; the prior decision on the older revision stays immutable history.
    /// </summary>
    public DeliveryRevision RecordDelivery(string revisionId, string partId, string runId,
        string headSha, string cardJson, string eventId, long nowUtc)
    {
        using var connection = Open();
        using var tx = connection.BeginTransaction();
        long next;
        string taskId;
        using (var probe = connection.CreateCommand())
        {
            probe.Transaction = tx;
            probe.CommandText = "SELECT task_id, current_revision FROM delivery_parts WHERE part_id=$id";
            probe.Parameters.AddWithValue("$id", partId);
            using var reader = probe.ExecuteReader();
            if (!reader.Read()) throw new KeyNotFoundException($"Part '{partId}' was not found.");
            taskId = reader.GetString(0);
            next = reader.GetInt64(1) + 1;
        }
        // Idempotency guard: the bound identity of a delivery is
        // (partId, runId, headSha). A replay of that identity must return the
        // persisted revision without mutating anything; a conflicting HEAD for
        // the same run is rejected before any mutation.
        using (var replay = connection.CreateCommand())
        {
            replay.Transaction = tx;
            replay.CommandText = """
                SELECT revision_id, revision_number, head_sha, card_json, received_utc
                FROM delivery_revisions WHERE part_id=$part AND run_id=$run
                """;
            replay.Parameters.AddWithValue("$part", partId);
            replay.Parameters.AddWithValue("$run", runId);
            using var reader = replay.ExecuteReader();
            if (reader.Read())
            {
                var existing = new DeliveryRevision(reader.GetString(0), partId,
                    reader.GetInt64(1), runId, reader.GetString(2), reader.GetString(3),
                    reader.GetInt64(4));
                // Bound identity is (partId, runId, headSha); the delivery payload
                // (cardJson) is part of the immutable record. A replay must match
                // both; a differing card for the same identity is a conflict, not
                // a replay, and is rejected before any mutation.
                if (existing.HeadSha == headSha && existing.CardJson == cardJson)
                {
                    tx.Commit();
                    return existing;
                }
                tx.Commit();
                throw new InvalidOperationException(
                    $"Part '{partId}' run '{runId}' conflicts with an already recorded delivery; refusing to overwrite.");
            }
        }
        if (!ControlRepairDeliveryAdmissible(connection, tx, runId, partId))
            throw new InvalidOperationException("Repair delivery must match its authorized part, stopped original session and contract.");
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = tx;
            insert.CommandText = """
                INSERT INTO delivery_revisions(revision_id, part_id, revision_number, run_id,
                                               head_sha, card_json, received_utc)
                VALUES($id, $part, $rev, $run, $head, $card, $now);
                """;
            insert.Parameters.AddWithValue("$id", revisionId);
            insert.Parameters.AddWithValue("$part", partId);
            insert.Parameters.AddWithValue("$rev", next);
            insert.Parameters.AddWithValue("$run", runId);
            insert.Parameters.AddWithValue("$head", headSha);
            insert.Parameters.AddWithValue("$card", cardJson);
            insert.Parameters.AddWithValue("$now", nowUtc);
            insert.ExecuteNonQuery();
        }
        UpdatePart(connection, tx, partId, DeliveryPartStatus.Delivered, next, null, nowUtc);
        SetTaskStatus(connection, tx, taskId, AutomationTaskStatus.Delivered, headSha, runId);
        AppendEvent(connection, tx, eventId, "delivery.received",
            ThreadOfTask(connection, tx, taskId), taskId, partId, runId,
            cardJson, nowUtc);
        tx.Commit();
        return new DeliveryRevision(revisionId, partId, next, runId, headSha, cardJson, nowUtc);
    }

    public List<DeliveryRevision> ListRevisions(string partId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM delivery_revisions WHERE part_id=$id ORDER BY revision_number";
        command.Parameters.AddWithValue("$id", partId);
        using var reader = command.ExecuteReader();
        var list = new List<DeliveryRevision>();
        while (reader.Read())
            list.Add(new DeliveryRevision(reader.GetString(0), reader.GetString(1),
                reader.GetInt64(2), reader.GetString(3), reader.GetString(4),
                reader.GetString(5), reader.GetInt64(6)));
        return list;
    }

    private static void UpdatePart(SqliteConnection connection, SqliteTransaction tx,
        string partId, DeliveryPartStatus status, long revision, string? decidedBy, long nowUtc)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            UPDATE delivery_parts SET status=$s, current_revision=$rev,
              last_decision_by=COALESCE($by, last_decision_by), updated_utc=$now
            WHERE part_id=$id
            """;
        command.Parameters.AddWithValue("$s", (int)status);
        command.Parameters.AddWithValue("$rev", revision);
        command.Parameters.AddWithValue("$by", (object?)decidedBy ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", nowUtc);
        command.Parameters.AddWithValue("$id", partId);
        command.ExecuteNonQuery();
    }

    private static string ThreadOfTask(SqliteConnection connection, SqliteTransaction tx, string taskId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT thread_id FROM tasks WHERE task_id=$id";
        command.Parameters.AddWithValue("$id", taskId);
        return (string)command.ExecuteScalar()!;
    }

    // --------------------------------------------------------------- decisions

    /// <summary>
    /// Record a local M decision (accept/rework/blocked/defer) against one concrete
    /// part revision. Decisions are insert-only; deciding a different part of the
    /// same task or thread never touches existing rows.
    /// </summary>
    /// <param name="expectedRunId">Run that produced the revision under review. Required.</param>
    /// <param name="expectedHeadSha">HEAD of the revision under review. Required.</param>
    /// <param name="expectedRevisionNumber">Revision number under review. Required.</param>
    public PartDecision RecordDecision(string decisionId, string partId,
        AutomationDecision decision, string? findings, string? evidenceJson,
        string decidedBy, string eventId, long nowUtc,
        string expectedRunId, string expectedHeadSha,
        long expectedRevisionNumber)
    {
        using var connection = Open();
        using var tx = connection.BeginTransaction();
        var result = RecordDecision(connection, tx, decisionId, partId, decision, findings,
            evidenceJson, decidedBy, eventId, nowUtc, expectedRunId, expectedHeadSha, expectedRevisionNumber);
        tx.Commit();
        return result;
    }

    private PartDecision RecordDecision(SqliteConnection connection, SqliteTransaction tx,
        string decisionId, string partId, AutomationDecision decision, string? findings,
        string? evidenceJson, string decidedBy, string eventId, long nowUtc,
        string expectedRunId, string expectedHeadSha, long expectedRevisionNumber)
    {
        if (decision == AutomationDecision.Rework && string.IsNullOrWhiteSpace(findings))
            throw new InvalidOperationException("A rework decision requires findings.");
        if (string.IsNullOrWhiteSpace(expectedRunId) || string.IsNullOrWhiteSpace(expectedHeadSha))
            throw new ArgumentException(
                "A decision must bind to a concrete reviewed runId and HEAD; unbound reviews are rejected.");
        string taskId;
        long revision;
        DeliveryPartStatus status;
        using (var probe = connection.CreateCommand())
        {
            probe.Transaction = tx;
            probe.CommandText = "SELECT task_id, current_revision, status FROM delivery_parts WHERE part_id=$id";
            probe.Parameters.AddWithValue("$id", partId);
            using var reader = probe.ExecuteReader();
            if (!reader.Read()) throw new KeyNotFoundException($"Part '{partId}' was not found.");
            taskId = reader.GetString(0);
            revision = reader.GetInt64(1);
            status = (DeliveryPartStatus)reader.GetInt32(2);
        }
        if (status != DeliveryPartStatus.Delivered)
            throw new InvalidOperationException(
                $"Part '{partId}' is '{status}', not Delivered; only the latest delivered revision can be decided.");
        if (!ControlReviewAdmissible(connection, tx, taskId))
            throw new InvalidOperationException("Controlled review requires the current approved contract and confirmed stopped writer.");
        // A decision must bind to the concrete delivery under review: expected
        // revision number, runId and HEAD. A stale review written against an
        // older revision can never accept a replacement.
        if (expectedRevisionNumber != revision)
            throw new InvalidOperationException(
                $"Part '{partId}' current revision is {revision}, not expected {expectedRevisionNumber}; stale review.");
        using (var rev = connection.CreateCommand())
        {
            rev.Transaction = tx;
            rev.CommandText = """
                SELECT run_id, head_sha FROM delivery_revisions
                WHERE part_id=$p AND revision_number=$r
                """;
            rev.Parameters.AddWithValue("$p", partId);
            rev.Parameters.AddWithValue("$r", revision);
            using var reader = rev.ExecuteReader();
            if (!reader.Read())
                throw new InvalidOperationException(
                    $"Part '{partId}' has no recorded revision {revision}; cannot decide.");
            var runId = reader.GetString(0);
            var headSha = reader.GetString(1);
            if (!string.Equals(runId, expectedRunId, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Part '{partId}' revision {revision} was delivered by run '{runId}', not expected '{expectedRunId}'; stale review.");
            if (!string.Equals(headSha, expectedHeadSha, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Part '{partId}' revision {revision} HEAD is '{headSha}', not expected '{expectedHeadSha}'; stale review.");
        }
        using (var prior = ControlSql(connection, tx, """
            SELECT decision_id FROM part_decisions WHERE part_id=$part AND revision_number=$rev ORDER BY rowid DESC LIMIT 1
            """, ("$part", partId), ("$rev", revision)))
            if (prior.ExecuteScalar() is string previous)
            {
                using var reopen = ControlSql(connection, tx, "SELECT EXISTS(SELECT 1 FROM control_review_reopens WHERE previous_decision_id=$id)", ("$id", previous));
                if (Convert.ToInt64(reopen.ExecuteScalar()) == 0)
                    throw new InvalidOperationException("A decided revision requires an explicit current review reopening.");
            }
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = tx;
            insert.CommandText = """
                INSERT INTO part_decisions(decision_id, part_id, revision_number, decision,
                                           findings, evidence_json, decided_by, decided_utc,
                                           decision_schema_version)
                VALUES($id, $part, $rev, $decision, $findings, $evidence, $by, $now, $sv);
                """;
            insert.Parameters.AddWithValue("$id", decisionId);
            insert.Parameters.AddWithValue("$part", partId);
            insert.Parameters.AddWithValue("$rev", revision);
            insert.Parameters.AddWithValue("$decision", (int)decision);
            insert.Parameters.AddWithValue("$findings", (object?)findings ?? DBNull.Value);
            insert.Parameters.AddWithValue("$evidence", (object?)evidenceJson ?? DBNull.Value);
            insert.Parameters.AddWithValue("$by", decidedBy);
            insert.Parameters.AddWithValue("$now", nowUtc);
            insert.Parameters.AddWithValue("$sv", DecisionSchemaVersion);
            insert.ExecuteNonQuery();
        }
        var newStatus = decision switch
        {
            AutomationDecision.Accept => DeliveryPartStatus.Accepted,
            AutomationDecision.Rework => DeliveryPartStatus.ReworkRequested,
            AutomationDecision.Blocked => DeliveryPartStatus.Blocked,
            _ => DeliveryPartStatus.Deferred
        };
        UpdatePart(connection, tx, partId, newStatus, revision, decidedBy, nowUtc);
        RefreshTaskStatus(connection, tx, taskId);
        var threadId = ThreadOfTask(connection, tx, taskId);
        AppendEvent(connection, tx, eventId, "review.decided", threadId, taskId, partId, null,
            "{\"decision\":\"" + decision.ToString().ToLowerInvariant() + "\"}", nowUtc);
        MaybeCompleteThread(connection, tx, threadId, nowUtc);
        return new PartDecision(decisionId, partId, revision, decision, findings,
            evidenceJson, decidedBy, nowUtc);
    }

    public List<PartDecision> ListDecisions(string partId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM part_decisions WHERE part_id=$id ORDER BY rowid";
        command.Parameters.AddWithValue("$id", partId);
        using var reader = command.ExecuteReader();
        var list = new List<PartDecision>();
        while (reader.Read())
            list.Add(new PartDecision(reader.GetString(0), reader.GetString(1),
                reader.GetInt64(2), (AutomationDecision)reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetString(6), reader.GetInt64(7)));
        return list;
    }

    /// <summary>True when a concrete (part, revision) decision already exists.</summary>
    public bool HasDecision(string partId, long revisionNumber)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM part_decisions WHERE part_id=$p AND revision_number=$r";
        command.Parameters.AddWithValue("$p", partId);
        command.Parameters.AddWithValue("$r", revisionNumber);
        return Convert.ToInt64(command.ExecuteScalar()) > 0;
    }

    private static void RefreshTaskStatus(SqliteConnection connection, SqliteTransaction tx, string taskId)
    {
        var counts = new Dictionary<DeliveryPartStatus, int>();
        using (var probe = connection.CreateCommand())
        {
            probe.Transaction = tx;
            probe.CommandText = "SELECT status, COUNT(*) FROM delivery_parts WHERE task_id=$id GROUP BY status";
            probe.Parameters.AddWithValue("$id", taskId);
            using var reader = probe.ExecuteReader();
            while (reader.Read()) counts[(DeliveryPartStatus)reader.GetInt32(0)] = reader.GetInt32(1);
        }
        int total = counts.Values.Sum();
        int Get(DeliveryPartStatus s) => counts.TryGetValue(s, out var n) ? n : 0;
        var status =
            total == 0 ? AutomationTaskStatus.Planned :
            Get(DeliveryPartStatus.Blocked) > 0 ? AutomationTaskStatus.Blocked :
            Get(DeliveryPartStatus.ReworkRequested) > 0 ? AutomationTaskStatus.Rework :
            Get(DeliveryPartStatus.Accepted) == total ? AutomationTaskStatus.Accepted :
            Get(DeliveryPartStatus.Accepted) > 0 ? AutomationTaskStatus.PartiallyAccepted :
            Get(DeliveryPartStatus.Deferred) == total ? AutomationTaskStatus.Deferred :
            AutomationTaskStatus.Delivered;
        SetTaskStatus(connection, tx, taskId, status);
    }

    private static void MaybeCompleteThread(SqliteConnection connection, SqliteTransaction tx,
        string threadId, long nowUtc)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            SELECT COUNT(*) FROM tasks WHERE thread_id=$t AND status NOT IN ($acc, $def, $blk)
            """;
        command.Parameters.AddWithValue("$t", threadId);
        command.Parameters.AddWithValue("$acc", (int)AutomationTaskStatus.Accepted);
        command.Parameters.AddWithValue("$def", (int)AutomationTaskStatus.Deferred);
        command.Parameters.AddWithValue("$blk", (int)AutomationTaskStatus.Blocked);
        var remaining = Convert.ToInt64(command.ExecuteScalar());
        if (remaining == 0)
            SetThreadStatus(connection, tx, threadId, ThreadStatus.Completed, null);
    }

    // ------------------------------------------------------ integration marks

    /// <summary>
    /// Undo fence: record that an accepted part revision was wired into main.
    /// Only accepted revisions may be marked integrated, and each mark is unique.
    /// </summary>
    public void MarkIntegrated(string partId, long revisionNumber, string mainSha,
        string eventId, long nowUtc)
    {
        using var connection = Open();
        using var tx = connection.BeginTransaction();
        using (var control = ControlSql(connection, tx, "SELECT task_id FROM delivery_parts WHERE part_id=$id", ("$id", partId)))
        {
            var controlledTask = control.ExecuteScalar() as string;
            if (controlledTask is not null && !ControlIntegrationAdmissible(connection, tx, controlledTask, partId, revisionNumber))
                throw new InvalidOperationException("Control contract, evidence, dependencies, relationship or explicit authorization no longer admits integration.");
        }
        using (var probe = connection.CreateCommand())
        {
            probe.Transaction = tx;
            probe.CommandText = """
                SELECT COUNT(*) FROM part_decisions
                WHERE part_id=$p AND revision_number=$r AND decision=$acc
                """;
            probe.Parameters.AddWithValue("$p", partId);
            probe.Parameters.AddWithValue("$r", revisionNumber);
            probe.Parameters.AddWithValue("$acc", (int)AutomationDecision.Accept);
            if (Convert.ToInt64(probe.ExecuteScalar()) == 0)
                throw new InvalidOperationException(
                    $"Part '{partId}' revision {revisionNumber} has no accept decision; cannot mark integrated.");
        }
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = tx;
            insert.CommandText = """
                INSERT INTO integration_marks(part_id, revision_number, main_sha, integrated_utc)
                VALUES($p, $r, $sha, $now);
                """;
            insert.Parameters.AddWithValue("$p", partId);
            insert.Parameters.AddWithValue("$r", revisionNumber);
            insert.Parameters.AddWithValue("$sha", mainSha);
            insert.Parameters.AddWithValue("$now", nowUtc);
            insert.ExecuteNonQuery();
        }
        var taskId = "";
        using (var probe2 = connection.CreateCommand())
        {
            probe2.Transaction = tx;
            probe2.CommandText = "SELECT task_id FROM delivery_parts WHERE part_id=$p";
            probe2.Parameters.AddWithValue("$p", partId);
            taskId = (string)probe2.ExecuteScalar()!;
        }
        AppendEvent(connection, tx, eventId, "integration.marked",
            ThreadOfTask(connection, tx, taskId), taskId, partId, null,
            "{\"mainSha\":\"" + mainSha + "\"}", nowUtc);
        tx.Commit();
    }

    public bool IsIntegrated(string partId, long revisionNumber)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM integration_marks WHERE part_id=$p AND revision_number=$r";
        command.Parameters.AddWithValue("$p", partId);
        command.Parameters.AddWithValue("$r", revisionNumber);
        return Convert.ToInt64(command.ExecuteScalar()) > 0;
    }

    // ------------------------------------------------------------------- DAG

    public void AddDependency(string taskId, string dependsOnTaskId,
        string? dependsOnPartId, bool requiresIntegration)
    {
        using var connection = Open();
        using var tx = connection.BeginTransaction();
        if (WouldCycle(connection, tx, taskId, dependsOnTaskId))
            throw new InvalidOperationException(
                $"Dependency {taskId} -> {dependsOnTaskId} would create a cycle.");
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            INSERT INTO task_dependencies(task_id, depends_on_task_id, depends_on_part_id,
                                          requires_integration)
            VALUES($t, $dep, $part, $integ);
            """;
        command.Parameters.AddWithValue("$t", taskId);
        command.Parameters.AddWithValue("$dep", dependsOnTaskId);
        command.Parameters.AddWithValue("$part", (object?)dependsOnPartId ?? DBNull.Value);
        command.Parameters.AddWithValue("$integ", requiresIntegration ? 1 : 0);
        command.ExecuteNonQuery();
        tx.Commit();
    }

    private static bool WouldCycle(SqliteConnection connection, SqliteTransaction tx,
        string taskId, string dependsOnTaskId)
    {
        // Walk upstream from dependsOnTaskId; reaching taskId means a cycle.
        var stack = new Stack<string>();
        var seen = new HashSet<string>();
        stack.Push(dependsOnTaskId);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current == taskId) return true;
            if (!seen.Add(current)) continue;
            using var command = connection.CreateCommand();
            command.Transaction = tx;
            command.CommandText =
                "SELECT DISTINCT depends_on_task_id FROM task_dependencies WHERE task_id=$id";
            command.Parameters.AddWithValue("$id", current);
            using var reader = command.ExecuteReader();
            var upstream = new List<string>();
            while (reader.Read()) upstream.Add(reader.GetString(0));
            foreach (var u in upstream) stack.Push(u);
        }
        return false;
    }

    /// <summary>
    /// A task is unlocked only when every required upstream part is accepted and,
    /// for edges that require it, actually marked integrated. A thread that merely
    /// "has finished items" never unlocks dependents.
    /// </summary>
    public bool IsTaskUnlocked(string taskId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT depends_on_task_id, depends_on_part_id, requires_integration
            FROM task_dependencies WHERE task_id=$id
            """;
        command.Parameters.AddWithValue("$id", taskId);
        using var reader = command.ExecuteReader();
        var edges = new List<(string Task, string? Part, bool Integ)>();
        while (reader.Read())
            edges.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetInt32(2) == 1));
        foreach (var (depTask, depPart, requiresIntegration) in edges)
        {
            var parts = depPart is null
                ? ListParts(depTask).Select(p => p.PartId).ToList()
                : new List<string> { depPart };
            if (parts.Count == 0) return false;
            foreach (var partId in parts)
            {
                var part = GetPart(partId);
                if (part.Status != DeliveryPartStatus.Accepted) return false;
                if (requiresIntegration && !IsIntegrated(partId, part.CurrentRevision))
                    return false;
            }
        }
        return true;
    }

    // ------------------------------------------------------------------ events

    /// <summary>
    /// Append a deduplicated event. The same eventId with an identical payload is a
    /// replay no-op; the same eventId with a different payload is rejected.
    /// Returns the event cursor (existing cursor on replay).
    /// </summary>
    public long AppendEvent(string eventId, string type, string threadId,
        string? taskId, string? partId, string? runId, string payloadJson, long occurredUtc)
    {
        using var connection = Open();
        using var tx = connection.BeginTransaction();
        var cursor = AppendEvent(connection, tx, eventId, type, threadId, taskId, partId,
            runId, payloadJson, occurredUtc);
        tx.Commit();
        return cursor;
    }

    private static long AppendEvent(SqliteConnection connection, SqliteTransaction tx,
        string eventId, string type, string threadId, string? taskId, string? partId,
        string? runId, string payloadJson, long occurredUtc)
    {
        var hash = Sha(payloadJson);
        using (var probe = connection.CreateCommand())
        {
            probe.Transaction = tx;
            probe.CommandText = "SELECT cursor, payload_hash FROM automation_events WHERE event_id=$id";
            probe.Parameters.AddWithValue("$id", eventId);
            using var reader = probe.ExecuteReader();
            if (reader.Read())
            {
                if (reader.GetString(1) != hash)
                    throw new InvalidOperationException(
                        $"Event '{eventId}' already exists with a different payload.");
                return reader.GetInt64(0);
            }
        }
        using var insert = connection.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = """
            INSERT INTO automation_events(event_id, type, thread_id, task_id, part_id, run_id,
                                          payload_hash, payload_json, occurred_utc)
            VALUES($id, $type, $thread, $task, $part, $run, $hash, $payload, $now);
            SELECT last_insert_rowid();
            """;
        insert.Parameters.AddWithValue("$id", eventId);
        insert.Parameters.AddWithValue("$type", type);
        insert.Parameters.AddWithValue("$thread", threadId);
        insert.Parameters.AddWithValue("$task", (object?)taskId ?? DBNull.Value);
        insert.Parameters.AddWithValue("$part", (object?)partId ?? DBNull.Value);
        insert.Parameters.AddWithValue("$run", (object?)runId ?? DBNull.Value);
        insert.Parameters.AddWithValue("$hash", hash);
        insert.Parameters.AddWithValue("$payload", payloadJson);
        insert.Parameters.AddWithValue("$now", occurredUtc);
        return Convert.ToInt64(insert.ExecuteScalar());
    }

    /// <summary>Events after a cursor for snapshot-then-catch-up reconnect.</summary>
    public List<AutomationEvent> ListEventsAfter(long cursor, int limit = 500)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT * FROM automation_events WHERE cursor>$c ORDER BY cursor LIMIT $l";
        command.Parameters.AddWithValue("$c", cursor);
        command.Parameters.AddWithValue("$l", limit);
        using var reader = command.ExecuteReader();
        var list = new List<AutomationEvent>();
        while (reader.Read())
            list.Add(new AutomationEvent(reader.GetInt64(0), reader.GetString(1),
                reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.GetString(8), reader.GetInt64(9)));
        return list;
    }

    // ----------------------------------------------------------------- commands

    /// <summary>
    /// Run a deduplicated command inside one transaction. The first call applies
    /// the handler; a replayed commandId returns the recorded no-op result.
    /// </summary>
    public CommandResult RunCommand(AutomationCommand command,
        Action<SqliteConnection, SqliteTransaction> apply, long nowUtc)
    {
        using var connection = Open();
        using var tx = connection.BeginTransaction();
        using (var probe = connection.CreateCommand())
        {
            probe.Transaction = tx;
            probe.CommandText = "SELECT applied_utc FROM automation_commands WHERE command_id=$id";
            probe.Parameters.AddWithValue("$id", command.CommandId);
            var existing = probe.ExecuteScalar();
            if (existing is not null && existing is not DBNull)
            {
                tx.Commit();
                return new CommandResult(command.CommandId, false, "duplicate");
            }
            if (existing is not null && existing is DBNull)
            {
                tx.Commit();
                return new CommandResult(command.CommandId, false, "in-flight");
            }
        }
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = tx;
            insert.CommandText = """
                INSERT INTO automation_commands(command_id, type, thread_id, task_id, part_id,
                                                payload_json, issued_utc, applied_utc)
                VALUES($id, $type, $thread, $task, $part, $payload, $issued, NULL);
                """;
            insert.Parameters.AddWithValue("$id", command.CommandId);
            insert.Parameters.AddWithValue("$type", command.Type);
            insert.Parameters.AddWithValue("$thread", command.ThreadId);
            insert.Parameters.AddWithValue("$task", (object?)command.TaskId ?? DBNull.Value);
            insert.Parameters.AddWithValue("$part", (object?)command.PartId ?? DBNull.Value);
            insert.Parameters.AddWithValue("$payload", command.PayloadJson);
            insert.Parameters.AddWithValue("$issued", command.IssuedUtc);
            insert.ExecuteNonQuery();
        }
        apply(connection, tx);
        using (var mark = connection.CreateCommand())
        {
            mark.Transaction = tx;
            mark.CommandText = "UPDATE automation_commands SET applied_utc=$now WHERE command_id=$id";
            mark.Parameters.AddWithValue("$now", nowUtc);
            mark.Parameters.AddWithValue("$id", command.CommandId);
            mark.ExecuteNonQuery();
        }
        tx.Commit();
        return new CommandResult(command.CommandId, true, null);
    }

    // ------------------------------------------------------------------ claims

    /// <summary>Persist a delivery claim before dispatch.</summary>
    public AutomationClaim CreateClaim(string claimId, string taskId, string partId,
        string runId, long nowUtc)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO delivery_claims(claim_id, task_id, part_id, run_id, state,
                                        claimed_utc, resolved_utc)
            VALUES($id, $task, $part, $run, $state, $now, NULL);
            """;
        command.Parameters.AddWithValue("$id", claimId);
        command.Parameters.AddWithValue("$task", taskId);
        command.Parameters.AddWithValue("$part", partId);
        command.Parameters.AddWithValue("$run", runId);
        command.Parameters.AddWithValue("$state", (int)ClaimState.Claimed);
        command.Parameters.AddWithValue("$now", nowUtc);
        command.ExecuteNonQuery();
        return new AutomationClaim(claimId, taskId, partId, runId, ClaimState.Claimed, nowUtc, null);
    }

    /// <summary>Resolve a claim only when terminal state and identity match.</summary>
    public void ResolveClaim(string claimId, string runId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE delivery_claims SET state=$s, resolved_utc=$now
            WHERE claim_id=$id AND run_id=$run AND state = $claimed
            """;
        command.Parameters.AddWithValue("$s", (int)ClaimState.Resolved);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$id", claimId);
        command.Parameters.AddWithValue("$run", runId);
        command.Parameters.AddWithValue("$claimed", (int)ClaimState.Claimed);
        if (command.ExecuteNonQuery() == 0)
            throw new InvalidOperationException($"Claim '{claimId}' was not resolved for run '{runId}'.");
    }

    /// <summary>Mark a claimed delivery as unknown; callers must freeze the thread.</summary>
    public void MarkClaimUnknown(string claimId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE delivery_claims SET state=$s WHERE claim_id=$id AND state=$claimed";
        command.Parameters.AddWithValue("$s", (int)ClaimState.Unknown);
        command.Parameters.AddWithValue("$id", claimId);
        command.Parameters.AddWithValue("$claimed", (int)ClaimState.Claimed);
        command.ExecuteNonQuery();
    }

    public List<AutomationClaim> ListUnresolvedClaims()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM delivery_claims WHERE state != $resolved";
        command.Parameters.AddWithValue("$resolved", (int)ClaimState.Resolved);
        using var reader = command.ExecuteReader();
        var list = new List<AutomationClaim>();
        while (reader.Read())
            list.Add(new AutomationClaim(reader.GetString(0), reader.GetString(1),
                reader.GetString(2), reader.GetString(3), (ClaimState)reader.GetInt32(4),
                reader.GetInt64(5), reader.IsDBNull(6) ? null : reader.GetInt64(6)));
        return list;
    }

    // ------------------------------------------------------------------ leases

    /// <summary>
    /// Acquire a single-writer lease. Returns null when another live holder owns the
    /// scope; expired leases are taken over by writing a new lease row.
    /// For <see cref="WriteLeaseKind.TaskWriter"/> the scope is always resolved to
    /// the owning thread: a thread has a fixed harness/model/session/worktree and
    /// exactly one managed writer across all of its tasks.
    /// </summary>
    public WriteLease? TryAcquireLease(string leaseId, WriteLeaseKind kind, string scopeId,
        string holderId, long nowUtc, long ttlMs)
    {
        using var connection = Open();
        using var tx = connection.BeginTransaction();
        if (kind == WriteLeaseKind.TaskWriter)
        {
            // Callers pass a taskId; the effective lease scope is its thread.
            using var resolve = connection.CreateCommand();
            resolve.Transaction = tx;
            resolve.CommandText = "SELECT thread_id FROM tasks WHERE task_id=$id";
            resolve.Parameters.AddWithValue("$id", scopeId);
            var threadId = resolve.ExecuteScalar() as string;
            if (threadId is null)
            {
                // Maybe the caller already passed a threadId.
                resolve.CommandText = "SELECT thread_id FROM threads WHERE thread_id=$id";
                threadId = resolve.ExecuteScalar() as string;
            }
            if (threadId is null)
            {
                tx.Commit();
                throw new KeyNotFoundException(
                    $"Task or thread '{scopeId}' was not found.");
            }
            scopeId = threadId;
        }
        using (var probe = connection.CreateCommand())
        {
            probe.Transaction = tx;
            probe.CommandText = """
                SELECT holder_id, expires_utc FROM write_leases
                WHERE kind=$k AND scope_id=$s AND released_utc IS NULL
                ORDER BY acquired_utc DESC LIMIT 1
                """;
            probe.Parameters.AddWithValue("$k", (int)kind);
            probe.Parameters.AddWithValue("$s", scopeId);
            using var reader = probe.ExecuteReader();
            if (reader.Read())
            {
                var holder = reader.GetString(0);
                var expires = reader.GetInt64(1);
                if (holder != holderId && nowUtc < expires)
                {
                    tx.Commit();
                    return null;
                }
            }
        }
        // Take over: mark prior rows released, then insert ours.
        using (var release = connection.CreateCommand())
        {
            release.Transaction = tx;
            release.CommandText = """
                UPDATE write_leases SET released_utc=$now
                WHERE kind=$k AND scope_id=$s AND released_utc IS NULL
                """;
            release.Parameters.AddWithValue("$now", nowUtc);
            release.Parameters.AddWithValue("$k", (int)kind);
            release.Parameters.AddWithValue("$s", scopeId);
            release.ExecuteNonQuery();
        }
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = tx;
            insert.CommandText = """
                INSERT INTO write_leases(lease_id, kind, scope_id, holder_id,
                                         acquired_utc, expires_utc, released_utc)
                VALUES($id, $k, $s, $h, $now, $exp, NULL);
                """;
            insert.Parameters.AddWithValue("$id", leaseId);
            insert.Parameters.AddWithValue("$k", (int)kind);
            insert.Parameters.AddWithValue("$s", scopeId);
            insert.Parameters.AddWithValue("$h", holderId);
            insert.Parameters.AddWithValue("$now", nowUtc);
            insert.Parameters.AddWithValue("$exp", nowUtc + ttlMs);
            insert.ExecuteNonQuery();
        }
        tx.Commit();
        return new WriteLease(leaseId, kind, scopeId, holderId, nowUtc, nowUtc + ttlMs);
    }

    public void ReleaseLease(string leaseId, string holderId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE write_leases SET released_utc=$now
            WHERE lease_id=$id AND holder_id=$h AND released_utc IS NULL
            """;
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$id", leaseId);
        command.Parameters.AddWithValue("$h", holderId);
        if (command.ExecuteNonQuery() == 0)
            throw new InvalidOperationException($"Lease '{leaseId}' is not held by '{holderId}'.");
    }

    // ------------------------------------------------------------ crash recovery

    /// <summary>
    /// Reconcile persisted state after a host crash. Claims left in Claimed are
    /// marked Unknown (the outcome can never be trusted) and their threads frozen;
    /// unresolved claims and frozen threads are returned for manual review.
    /// Never re-dispatches anything.
    /// </summary>
    public RecoveryReport RecoverAfterCrash(long nowUtc)
    {
        using var connection = Open();
        using var tx = connection.BeginTransaction();
        var stale = new List<(string ClaimId, string TaskId)>();
        using (var probe = connection.CreateCommand())
        {
            probe.Transaction = tx;
            probe.CommandText = "SELECT claim_id, task_id FROM delivery_claims WHERE state=$c";
            probe.Parameters.AddWithValue("$c", (int)ClaimState.Claimed);
            using var reader = probe.ExecuteReader();
            while (reader.Read()) stale.Add((reader.GetString(0), reader.GetString(1)));
        }
        var frozenThreads = new List<string>();
        foreach (var (claimId, taskId) in stale)
        {
            using (var mark = connection.CreateCommand())
            {
                mark.Transaction = tx;
                mark.CommandText =
                    "UPDATE delivery_claims SET state=$s WHERE claim_id=$id";
                mark.Parameters.AddWithValue("$s", (int)ClaimState.Unknown);
                mark.Parameters.AddWithValue("$id", claimId);
                mark.ExecuteNonQuery();
            }
            var threadId = ThreadOfTask(connection, tx, taskId);
            SetThreadStatus(connection, tx, threadId, ThreadStatus.Frozen,
                $"claim {claimId} outcome unknown after crash");
            if (!frozenThreads.Contains(threadId)) frozenThreads.Add(threadId);
        }
        tx.Commit();
        return new RecoveryReport(stale.Select(s => s.ClaimId).ToList(), frozenThreads);
    }

    public sealed record RecoveryReport(List<string> UnknownClaims, List<string> FrozenThreads);

    public void Dispose() { /* connections are per-call; nothing held open */ }
}
