using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Fufucode.Core.Automation.Harnesses;

/// <summary>Dispatch journal in the U1 project database; no second project truth store.</summary>
public sealed class HarnessRunStore
{
    private readonly string _database;
    public HarnessRunStore(AutomationStore store)
    {
        _database = store.DatabasePath;
        using var c = Open();
        using var q = c.CreateCommand();
        q.CommandText = """
            CREATE TABLE IF NOT EXISTS harness_bindings (
                thread_id TEXT PRIMARY KEY REFERENCES threads(thread_id), fingerprint TEXT NOT NULL,
                session_id TEXT, session_locator TEXT);
            CREATE TABLE IF NOT EXISTS harness_runs (
                run_id TEXT PRIMARY KEY, thread_id TEXT NOT NULL REFERENCES threads(thread_id),
                worktree TEXT NOT NULL, request_hash TEXT NOT NULL, status TEXT NOT NULL,
                session_id TEXT, session_locator TEXT, output TEXT, error TEXT);
            CREATE UNIQUE INDEX IF NOT EXISTS harness_active_thread ON harness_runs(thread_id)
                WHERE status IN ('Dispatching','Running','Unknown');
            CREATE UNIQUE INDEX IF NOT EXISTS harness_active_worktree ON harness_runs(worktree)
                WHERE status IN ('Dispatching','Running','Unknown');
            """;
        q.ExecuteNonQuery();
        AddColumn(c, "harness_runs", "supervisor_pid", "INTEGER");
        AddColumn(c, "harness_runs", "supervisor_start_utc", "INTEGER");
        AddColumn(c, "harness_runs", "writer_pid", "INTEGER");
        AddColumn(c, "harness_runs", "writer_start_utc", "INTEGER");
        AddColumn(c, "harness_runs", "host_id", "TEXT");
        AddColumn(c, "harness_runs", "worktree_head", "TEXT");
        AddColumn(c, "harness_runs", "task_id", "TEXT");
        // Repository provenance: where the repository stood when this run's own process exited.
        // A delivery is bound to that value, never to whatever the worktree points at later.
        AddColumn(c, "harness_runs", "settle_head", "TEXT");
        using var journal = c.CreateCommand();
        journal.CommandText = """
            CREATE TABLE IF NOT EXISTS harness_resolutions (
                run_id TEXT PRIMARY KEY REFERENCES harness_runs(run_id),
                evidence_json TEXT NOT NULL, from_status TEXT NOT NULL, to_status TEXT NOT NULL,
                decided_by TEXT NOT NULL, decided_utc INTEGER NOT NULL);
            """;
        journal.ExecuteNonQuery();
    }

    /// <summary>Journal columns added after the first shipped schema; prior rows are preserved.</summary>
    private static void AddColumn(SqliteConnection connection, string table, string column, string type)
    {
        using var check = connection.CreateCommand();
        check.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name=$name";
        check.Parameters.AddWithValue("$name", column);
        if (Convert.ToInt64(check.ExecuteScalar()) > 0) return;
        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {type}";
        alter.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = _database, Pooling = false, DefaultTimeout = 10 }.ToString());
        c.Open();
        using var q = c.CreateCommand();
        q.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        q.ExecuteNonQuery();
        return c;
    }

    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    // Return false for an exact replay, including a dispatch of uncertain outcome.
    internal bool Claim(HarnessRequest request, AutomationThread thread, HarnessRuntime runtime, bool mayStart)
    {
        var worktree = Path.TrimEndingDirectorySeparator(Path.GetFullPath(thread.Worktree));
        if (OperatingSystem.IsWindows()) worktree = worktree.ToUpperInvariant();
        var fingerprint = Hash(JsonSerializer.Serialize(new { runtime, request.Model, request.Provider, worktree }));
        var hash = Hash(JsonSerializer.Serialize(new { request, fingerprint }));
        using var c = Open();
        using var tx = c.BeginTransaction();
        using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText = "SELECT request_hash FROM harness_runs WHERE run_id=$run";
        q.Parameters.AddWithValue("$run", request.RunId);
        if (q.ExecuteScalar() is string previous)
        {
            if (previous != hash) throw new InvalidOperationException("Run identity reused with different input.");
            return false;
        }
        if (!mayStart) throw new InvalidOperationException("Thread is not active or task dependencies are unsatisfied.");
        q.Parameters.AddWithValue("$thread", request.ThreadId);
        q.CommandText = "SELECT fingerprint FROM harness_bindings WHERE thread_id=$thread";
        if (q.ExecuteScalar() is string binding && binding != fingerprint)
            throw new InvalidOperationException("A saved thread cannot change runtime, model, provider or worktree.");
        // The dispatched task identity is journaled from the request itself at claim time. It is the
        // only place a run's task identity comes from: never the streamed output, never a hash.
        q.CommandText = """
            INSERT OR IGNORE INTO harness_bindings(thread_id, fingerprint) VALUES($thread,$fingerprint);
            INSERT INTO harness_runs(run_id,thread_id,task_id,worktree,request_hash,status,
                                     session_id,session_locator)
            SELECT $run,$thread,$task,$worktree,$hash,'Dispatching',session_id,session_locator
            FROM harness_bindings WHERE thread_id=$thread;
            """;
        q.Parameters.AddWithValue("$task", request.TaskId);
        q.Parameters.AddWithValue("$fingerprint", fingerprint);
        q.Parameters.AddWithValue("$worktree", worktree);
        q.Parameters.AddWithValue("$hash", hash);
        q.ExecuteNonQuery();
        tx.Commit();
        return true;
    }

    public HarnessRun Get(string runId)
    {
        using var c = Open();
        using var q = c.CreateCommand();
        q.CommandText = """
            SELECT run_id,thread_id,status,session_id,session_locator,output,error,
                   task_id,worktree,supervisor_pid,supervisor_start_utc,host_id,worktree_head,
                   writer_pid,writer_start_utc,settle_head
            FROM harness_runs WHERE run_id=$id
            """;
        q.Parameters.AddWithValue("$id", runId);
        using var r = q.ExecuteReader();
        if (!r.Read()) throw new KeyNotFoundException(runId);
        return new(r.GetString(0), r.GetString(1), Enum.Parse<HarnessRunStatus>(r.GetString(2)),
            r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4),
            r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6))
        {
            TaskId = r.IsDBNull(7) ? null : r.GetString(7),
            Worktree = r.IsDBNull(8) ? null : r.GetString(8),
            SupervisorPid = r.IsDBNull(9) ? null : r.GetInt32(9),
            SupervisorStartUtc = r.IsDBNull(10) ? null : r.GetInt64(10),
            HostId = r.IsDBNull(11) ? null : r.GetString(11),
            WorktreeHead = r.IsDBNull(12) ? null : r.GetString(12),
            WriterPid = r.IsDBNull(13) ? null : r.GetInt32(13),
            WriterStartUtc = r.IsDBNull(14) ? null : r.GetInt64(14),
            SettleHead = r.IsDBNull(15) ? null : r.GetString(15)
        };
    }

    /// <summary>Runs still holding a thread or worktree. Read-only inventory, no state change.</summary>
    public List<HarnessRun> ListActive()
    {
        using var c = Open();
        using var q = c.CreateCommand();
        q.CommandText = """
            SELECT run_id FROM harness_runs
            WHERE status IN ('Dispatching','Running','Unknown') ORDER BY run_id
            """;
        using var r = q.ExecuteReader();
        var ids = new List<string>();
        while (r.Read()) ids.Add(r.GetString(0));
        r.Close();
        return ids.Select(Get).ToList();
    }

    /// <summary>All recorded runs for this worktree, including terminal runs that may have orphan writers.</summary>
    public List<HarnessRun> ListWorktreeRuns(string worktree)
    {
        worktree = Path.TrimEndingDirectorySeparator(Path.GetFullPath(worktree));
        if (OperatingSystem.IsWindows()) worktree = worktree.ToUpperInvariant();
        using var c = Open();
        using var q = c.CreateCommand();
        q.CommandText = "SELECT run_id FROM harness_runs WHERE worktree=$path ORDER BY run_id";
        q.Parameters.AddWithValue("$path", worktree);
        using var r = q.ExecuteReader();
        var ids = new List<string>();
        while (r.Read()) ids.Add(r.GetString(0));
        r.Close();
        return ids.Select(Get).ToList();
    }

    /// <summary>
    /// Persist the supervising host process before its child is awaited. Identity is pid plus start
    /// time; a bare pid can be recycled by the operating system.
    /// </summary>
    internal void SaveSupervisor(string runId, string hostId, int processId, DateTimeOffset startUtc,
        string? worktreeHead)
    {
        using var c = Open();
        using var q = c.CreateCommand();
        q.CommandText = """
            UPDATE harness_runs SET host_id=$host, supervisor_pid=$pid, supervisor_start_utc=$start,
              worktree_head=COALESCE($head,worktree_head) WHERE run_id=$id
            """;
        q.Parameters.AddWithValue("$host", hostId);
        q.Parameters.AddWithValue("$pid", processId);
        q.Parameters.AddWithValue("$start", startUtc.ToUnixTimeMilliseconds());
        q.Parameters.AddWithValue("$head", (object?)worktreeHead ?? DBNull.Value);
        q.Parameters.AddWithValue("$id", runId);
        if (q.ExecuteNonQuery() == 0) throw new KeyNotFoundException(runId);
    }

    /// <summary>Persist the harness child process that can write into the worktree.</summary>
    internal void SaveWriter(string runId, int processId, DateTimeOffset startUtc)
    {
        using var c = Open();
        using var q = c.CreateCommand();
        q.CommandText = "UPDATE harness_runs SET writer_pid=$pid, writer_start_utc=$start WHERE run_id=$id";
        q.Parameters.AddWithValue("$pid", processId);
        q.Parameters.AddWithValue("$start", startUtc.ToUnixTimeMilliseconds());
        q.Parameters.AddWithValue("$id", runId);
        if (q.ExecuteNonQuery() == 0) throw new KeyNotFoundException(runId);
    }

    /// <summary>
    /// Project a Dispatching/Running row whose owner is gone as Unknown. Moves only toward less
    /// certainty and never resends. Returns false when the row is already terminal or Unknown.
    /// </summary>
    internal bool TryProjectUnknown(string runId, string reason)
    {
        using var c = Open();
        using var q = c.CreateCommand();
        q.CommandText = """
            UPDATE harness_runs SET status='Unknown', error=$reason
            WHERE run_id=$id AND status IN ('Dispatching','Running')
            """;
        q.Parameters.AddWithValue("$reason", reason);
        q.Parameters.AddWithValue("$id", runId);
        return q.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// Settle an unresolved Unknown run into a terminal status and journal the decision atomically.
    /// A run already carrying a resolution is never rewritten; the journal is append-only.
    /// </summary>
    internal bool TryResolve(string runId, HarnessRunStatus to, string reason, string decidedBy,
        string evidenceJson, long nowUtc)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        using (var guard = c.CreateCommand())
        {
            guard.Transaction = tx;
            guard.CommandText = "SELECT EXISTS(SELECT 1 FROM harness_resolutions WHERE run_id=$id)";
            guard.Parameters.AddWithValue("$id", runId);
            if (Convert.ToInt64(guard.ExecuteScalar()) == 1) { tx.Commit(); return false; }
        }
        using (var update = c.CreateCommand())
        {
            update.Transaction = tx;
            update.CommandText = """
                UPDATE harness_runs SET status=$status, error=$reason
                WHERE run_id=$id AND status='Unknown'
                """;
            update.Parameters.AddWithValue("$status", to.ToString());
            update.Parameters.AddWithValue("$reason", reason);
            update.Parameters.AddWithValue("$id", runId);
            if (update.ExecuteNonQuery() == 0) { tx.Rollback(); return false; }
        }
        using (var insert = c.CreateCommand())
        {
            insert.Transaction = tx;
            insert.CommandText = """
                INSERT INTO harness_resolutions(run_id,evidence_json,from_status,to_status,
                                                decided_by,decided_utc)
                VALUES($id,$evidence,'Unknown',$to,$by,$now)
                """;
            insert.Parameters.AddWithValue("$id", runId);
            insert.Parameters.AddWithValue("$evidence", evidenceJson);
            insert.Parameters.AddWithValue("$to", to.ToString());
            insert.Parameters.AddWithValue("$by", decidedBy);
            insert.Parameters.AddWithValue("$now", nowUtc);
            insert.ExecuteNonQuery();
        }
        tx.Commit();
        return true;
    }

    /// <summary>Resolution journal, for auditors. Read-only.</summary>
    public List<(string RunId, string EvidenceJson, string DecidedBy, long DecidedUtc)> ListResolutions()
    {
        using var c = Open();
        using var q = c.CreateCommand();
        q.CommandText = "SELECT run_id,evidence_json,decided_by,decided_utc FROM harness_resolutions ORDER BY run_id";
        using var r = q.ExecuteReader();
        var list = new List<(string, string, string, long)>();
        while (r.Read()) list.Add((r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt64(3)));
        return list;
    }

    internal void Save(string runId, HarnessRunStatus status, string? sessionId = null,
        string? locator = null, string? output = null, string? error = null,
        string? settleHead = null)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText = """
            UPDATE harness_runs SET status=$status, session_id=COALESCE($session,session_id),
              session_locator=COALESCE($locator,session_locator),output=$output,error=$error,
              settle_head=COALESCE($settle,settle_head) WHERE run_id=$id;
            UPDATE harness_bindings SET session_id=COALESCE($session,session_id),
              session_locator=COALESCE($locator,session_locator)
              WHERE thread_id=(SELECT thread_id FROM harness_runs WHERE run_id=$id);
            """;
        q.Parameters.AddWithValue("$id", runId);
        q.Parameters.AddWithValue("$status", status.ToString());
        q.Parameters.AddWithValue("$session", (object?)sessionId ?? DBNull.Value);
        q.Parameters.AddWithValue("$locator", (object?)locator ?? DBNull.Value);
        q.Parameters.AddWithValue("$output", (object?)output ?? DBNull.Value);
        q.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        // Status and provenance are written in one statement: a crash cannot leave a Completed row
        // whose delivery binding is missing, which would strand a run that really finished.
        q.Parameters.AddWithValue("$settle", (object?)settleHead ?? DBNull.Value);
        q.ExecuteNonQuery();
        tx.Commit();
    }

    /// <summary>Read-only reconciliation never interprets a lost owner as permission to resend.</summary>
    public HarnessRun Reconcile(string runId)
    {
        var run = Get(runId);
        return run.Status is HarnessRunStatus.Dispatching or HarnessRunStatus.Running
            ? run with { Status = HarnessRunStatus.Unknown, Error = "Owner must reconcile persisted evidence; automatic resend is forbidden." }
            : run;
    }
}
