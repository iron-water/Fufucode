using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fufucode.Core.Automation.Harnesses;
using Microsoft.Data.Sqlite;

namespace Fufucode.Core.Automation.Hosting;

/// <summary>The one background host recorded in this project database, plus its process identity.</summary>
public sealed record HostInstance(string HostId, int ProcessId, long StartUtc, long HeartbeatUtc);

internal sealed record HostJournalSnapshot(int Queued, int Started, int Settled, int Abandoned,
    long EventCursor, IReadOnlyList<string> ActiveRuns);

/// <summary>
/// Lifecycle of one accepted dispatch intent. Only Queued rows may be started; a Started row whose
/// outcome is uncertain becomes Abandoned, never Queued again.
/// </summary>
public enum HostRequestState { Queued = 0, Started = 1, Settled = 2, Abandoned = 3 }

public sealed record HostRequest(string RunId, string ThreadId, string TaskId, string PayloadJson,
    HostRequestState State, long AcceptedUtc, long? StartedUtc, long? SettledUtc, string? Detail);

/// <summary>What a restarting host found in its own durable queue. Nothing here is a resend.</summary>
public sealed record HostRecovery(IReadOnlyList<HostRequest> Pending,
    IReadOnlyList<string> Requeued, IReadOnlyList<string> Abandoned, IReadOnlyList<string> Settled)
{
    /// <summary>Started rows whose harness run still looks open or unverifiable: left for reconciliation.</summary>
    public IReadOnlyList<string> Uncertain => Abandoned;

    /// <summary>Every row recovery changed, in acceptance order, for the client's first HELLO.</summary>
    public IReadOnlyList<string> Actions =>
        Requeued.Concat(Abandoned).Concat(Settled).OrderBy(runId => runId).ToList();
}

/// <summary>
/// Durable dispatch queue plus the single-host claim. It lives in the same project database as the
/// U1 store and the harness journal, so there is no second source of truth and no new scheduler:
/// the queue only records what has been accepted for the one background host, and the harness
/// journal still decides what may run and what its outcome was.
/// </summary>
public sealed class HostJournal
{
    private readonly string _database;

    public HostJournal(AutomationStore store)
    {
        _database = store.DatabasePath;
        using var c = Open();
        using var q = c.CreateCommand();
        q.CommandText = """
            CREATE TABLE IF NOT EXISTS host_instances (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                host_id TEXT NOT NULL, process_id INTEGER NOT NULL,
                start_utc INTEGER NOT NULL, heartbeat_utc INTEGER NOT NULL,
                stopped_utc INTEGER);
            CREATE TABLE IF NOT EXISTS host_requests (
                run_id TEXT PRIMARY KEY,
                thread_id TEXT NOT NULL REFERENCES threads(thread_id),
                task_id TEXT NOT NULL REFERENCES tasks(task_id),
                payload_json TEXT NOT NULL, request_hash TEXT NOT NULL,
                state INTEGER NOT NULL, accepted_utc INTEGER NOT NULL,
                started_utc INTEGER, settled_utc INTEGER, detail TEXT);
            CREATE INDEX IF NOT EXISTS host_requests_pending
                ON host_requests(state, accepted_utc);
            """;
        q.ExecuteNonQuery();
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

    public static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    // -------------------------------------------------------------- single host claim

    /// <summary>
    /// Claim the one background host slot. An unreleased live or uninspectable recorded process is
    /// never taken over. A cleanly released instance, or a process identity proven gone, can be
    /// replaced. Keep the row on death to distinguish a crash from an explicit completed stop.
    /// </summary>
    public bool TryClaim(HostInstance instance, IProcessProbe probe)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        using (var read = c.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = "SELECT host_id, process_id, start_utc, stopped_utc FROM host_instances WHERE id=1";
            using var reader = read.ExecuteReader();
            // Release is recorded only after the host's drain completed. A cleanly stopped
            // instance has relinquished this slot even if its containing CLI/App process lives on.
            // Unreleased live/unknown owners still go through the original process-identity guard.
            if (reader.Read() && reader.IsDBNull(3))
            {
                var occupantId = reader.GetString(0);
                var pid = reader.GetInt32(1);
                var recordedStart = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2));
                var facts = probe.Probe(pid);
                var occupied = facts.Liveness switch
                {
                    // Same pid and matching start time: the same process is still holding the slot.
                    ProcessLiveness.Alive => facts.SameIdentityAs(recordedStart),
                    // Cannot inspect it: never assume it is gone.
                    ProcessLiveness.Indeterminate => true,
                    _ => false
                };
                // Re-claiming while our own recorded process is still the occupant is idempotent:
                // same host id, same pid, same start time. It refreshes, it does not duplicate.
                if (occupied && reader.GetString(0) == instance.HostId && pid == instance.ProcessId &&
                    recordedStart.ToUnixTimeMilliseconds() == instance.StartUtc)
                { tx.Commit(); return true; }
                // A live or uninspectable foreign process owns the slot. A recorded host that is
                // provably gone (dead pid, or a recycled pid whose start time differs) does not, and
                // the replacement below overwrites its identity.
                if (occupied)
                { tx.Commit(); return false; }
            }
        }
        using (var upsert = c.CreateCommand())
        {
            upsert.Transaction = tx;
            upsert.CommandText = """
                INSERT INTO host_instances(id,host_id,process_id,start_utc,heartbeat_utc,stopped_utc)
                VALUES(1,$host,$pid,$start,$beat,NULL)
                ON CONFLICT(id) DO UPDATE SET host_id=$host, process_id=$pid,
                  start_utc=$start, heartbeat_utc=$beat, stopped_utc=NULL
                """;
            upsert.Parameters.AddWithValue("$host", instance.HostId);
            upsert.Parameters.AddWithValue("$pid", instance.ProcessId);
            upsert.Parameters.AddWithValue("$start", instance.StartUtc);
            upsert.Parameters.AddWithValue("$beat", instance.HeartbeatUtc);
            upsert.ExecuteNonQuery();
        }
        tx.Commit();
        return true;
    }

    /// <summary>
    /// The host currently holding the slot, or null when none does. A recorded process that is dead,
    /// or a recycled pid whose start time differs, is not an occupant: the slot is stale and free.
    /// </summary>
    public HostInstance? Current(IProcessProbe probe)
    {
        using var c = Open();
        using var q = c.CreateCommand();
        q.CommandText = "SELECT host_id,process_id,start_utc,heartbeat_utc,stopped_utc FROM host_instances WHERE id=1";
        using var r = q.ExecuteReader();
        if (!r.Read()) return null;
        var instance = new HostInstance(r.GetString(0), r.GetInt32(1), r.GetInt64(2), r.GetInt64(3));
        if (!r.IsDBNull(4)) return null; // cleanly stopped: the slot was released
        var facts = probe.Probe(instance.ProcessId);
        return facts.Liveness switch
        {
            ProcessLiveness.Alive => facts.SameIdentityAs(DateTimeOffset.FromUnixTimeMilliseconds(instance.StartUtc))
                ? instance : null,
            // Uninspectable is treated as occupied: never declare the slot free on a guess.
            ProcessLiveness.Indeterminate => instance,
            _ => null
        };
    }

    public void Heartbeat(string hostId, long nowUtc)
    {
        using var c = Open();
        using var q = c.CreateCommand();
        q.CommandText = "UPDATE host_instances SET heartbeat_utc=$now WHERE id=1 AND host_id=$host";
        q.Parameters.AddWithValue("$now", nowUtc);
        q.Parameters.AddWithValue("$host", hostId);
        q.ExecuteNonQuery();
    }

    /// <summary>The heartbeat recorded for a host id, or null when that host does not hold the slot.</summary>
    public long? GetHeartbeat(string hostId)
    {
        using var c = Open();
        using var q = c.CreateCommand();
        q.CommandText = "SELECT heartbeat_utc FROM host_instances WHERE id=1 AND host_id=$host";
        q.Parameters.AddWithValue("$host", hostId);
        return q.ExecuteScalar() is long value ? value : null;
    }

    /// <summary>
    /// Observe the slot the way an auditor would: the row alone is not evidence, the recorded
    /// process is. A dead pid or a recycled pid with a different start time is a crash, an explicit
    /// release is a stop, and a process that cannot be inspected counts as occupied.
    /// </summary>
    public HostSlotState SlotState(IProcessProbe probe)
    {
        using var c = Open();
        using var q = c.CreateCommand();
        q.CommandText = "SELECT process_id,start_utc,stopped_utc FROM host_instances WHERE id=1";
        using var r = q.ExecuteReader();
        if (!r.Read()) return HostSlotState.Empty;
        if (!r.IsDBNull(2)) return HostSlotState.Stopped;
        var facts = probe.Probe(r.GetInt32(0));
        return facts.Liveness switch
        {
            ProcessLiveness.Alive => facts.SameIdentityAs(
                DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(1)))
                ? HostSlotState.Occupied : HostSlotState.Crashed,
            ProcessLiveness.Indeterminate => HostSlotState.Occupied,
            _ => HostSlotState.Crashed
        };
    }

    /// <summary>
    /// Newest durable event cursor in the project journal, read-only. Clients use it as the snapshot
    /// watermark before subscribing, so a reconnect can prove it missed nothing.
    /// </summary>
    public long LastEventCursor()
    {
        using var c = Open();
        using var q = c.CreateCommand();
        q.CommandText = "SELECT COALESCE(MAX(cursor),0) FROM automation_events";
        return Convert.ToInt64(q.ExecuteScalar());
    }

    /// <summary>
    /// Explicit stop: the slot stays recorded but is marked stopped, so a later host can tell a
    /// clean shutdown apart from a crash and the client can see that nothing is running.
    /// </summary>
    public void Release(string hostId, long nowUtc)
    {
        using var c = Open();
        using var q = c.CreateCommand();
        q.CommandText = """
            UPDATE host_instances SET stopped_utc=$now WHERE id=1 AND host_id=$host AND stopped_utc IS NULL
            """;
        q.Parameters.AddWithValue("$now", nowUtc);
        q.Parameters.AddWithValue("$host", hostId);
        q.ExecuteNonQuery();
    }

    // ------------------------------------------------------------------------ queue

    /// <summary>
    /// Accept one dispatch intent and persist it before anything starts. Replaying the same run with
    /// the same payload is a no-op, and the same run with a different payload is refused: a queue row
    /// is a durable record of what was asked for, not a mutable slot.
    /// </summary>
    public bool Enqueue(string runId, string threadId, string taskId, string payloadJson, long nowUtc)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        var inserted = Enqueue(c, tx, runId, threadId, taskId, payloadJson, nowUtc);
        tx.Commit();
        return inserted;
    }

    // The control binder joins the existing queue insertion to its receipt/event transaction.
    internal static bool Enqueue(SqliteConnection c, SqliteTransaction tx, string runId,
        string threadId, string taskId, string payloadJson, long nowUtc)
    {
        var hash = Hash(payloadJson);
        using (var probe = c.CreateCommand())
        {
            probe.Transaction = tx;
            probe.CommandText = "SELECT request_hash, state FROM host_requests WHERE run_id=$id";
            probe.Parameters.AddWithValue("$id", runId);
            using var reader = probe.ExecuteReader();
            if (reader.Read())
            {
                var previous = reader.GetString(0);
                // Same identity and same payload is a replay: the row keeps its own state, so a
                // settled or abandoned intent is never reopened by a retry.
                if (previous != hash) throw new InvalidOperationException(
                    $"Queued run '{runId}' already carries a different payload.");
                return false;
            }
        }
        using (var insert = c.CreateCommand())
        {
            insert.Transaction = tx;
            insert.CommandText = """
                INSERT INTO host_requests(run_id,thread_id,task_id,payload_json,request_hash,
                                         state,accepted_utc)
                VALUES($id,$thread,$task,$payload,$hash,$queued,$now)
                """;
            insert.Parameters.AddWithValue("$id", runId);
            insert.Parameters.AddWithValue("$thread", threadId);
            insert.Parameters.AddWithValue("$task", taskId);
            insert.Parameters.AddWithValue("$payload", payloadJson);
            insert.Parameters.AddWithValue("$hash", hash);
            insert.Parameters.AddWithValue("$queued", (int)HostRequestState.Queued);
            insert.Parameters.AddWithValue("$now", nowUtc);
            insert.ExecuteNonQuery();
        }
        return true;
    }

    public List<HostRequest> List(HostRequestState? state = null)
    {
        using var c = Open();
        using var q = c.CreateCommand();
        q.CommandText = state is null
            ? "SELECT run_id,thread_id,task_id,payload_json,state,accepted_utc,started_utc,settled_utc,detail FROM host_requests ORDER BY accepted_utc, run_id"
            : "SELECT run_id,thread_id,task_id,payload_json,state,accepted_utc,started_utc,settled_utc,detail FROM host_requests WHERE state=$state ORDER BY accepted_utc, run_id";
        if (state is not null) q.Parameters.AddWithValue("$state", (int)state.Value);
        using var r = q.ExecuteReader();
        var list = new List<HostRequest>();
        while (r.Read())
            list.Add(new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
                (HostRequestState)r.GetInt32(4), r.GetInt64(5),
                r.IsDBNull(6) ? null : r.GetInt64(6), r.IsDBNull(7) ? null : r.GetInt64(7),
                r.IsDBNull(8) ? null : r.GetString(8)));
        return list;
    }

    public HostRequest? Get(string runId) =>
        List().FirstOrDefault(request => request.RunId == runId);

    internal string? ControlBaseline(string runId)
    {
        using var c = Open();
        using var q = c.CreateCommand();
        q.CommandText = """
            SELECT t.base_sha FROM control_bindings b JOIN tasks t ON t.task_id=b.task_id WHERE b.run_id=$id
            UNION ALL SELECT r.head_sha FROM control_repair_bindings b JOIN control_repairs r ON r.repair_id=b.repair_id WHERE b.run_id=$id
            """;
        q.Parameters.AddWithValue("$id", runId);
        return q.ExecuteScalar() as string;
    }

    internal bool IsControlRepair(string runId)
    {
        using var c = Open(); using var q = c.CreateCommand();
        q.CommandText = "SELECT EXISTS(SELECT 1 FROM control_repairs WHERE run_id=$run)";
        q.Parameters.AddWithValue("$run", runId); return Convert.ToInt64(q.ExecuteScalar()) != 0;
    }

    public int Count(HostRequestState state) => List(state).Count;

    /// <summary>Queue counts, run state and event watermark from the same SQLite read snapshot.</summary>
    internal HostJournalSnapshot ReadSnapshot()
    {
        using var c = Open(); using var tx = c.BeginTransaction(deferred: true);
        int queued, started, settled, abandoned;
        using (var q = c.CreateCommand())
        {
            q.Transaction = tx;
            q.CommandText = """
                SELECT COALESCE(SUM(state=0),0),COALESCE(SUM(state=1),0),
                  COALESCE(SUM(state=2),0),COALESCE(SUM(state=3),0) FROM host_requests
                """;
            using var r = q.ExecuteReader(); r.Read();
            queued = r.GetInt32(0); started = r.GetInt32(1); settled = r.GetInt32(2); abandoned = r.GetInt32(3);
        }
        long cursor;
        using (var q = c.CreateCommand())
        {
            q.Transaction = tx; q.CommandText = "SELECT COALESCE(MAX(cursor),0) FROM automation_events";
            cursor = Convert.ToInt64(q.ExecuteScalar());
        }
        var active = new List<string>();
        using (var q = c.CreateCommand())
        {
            q.Transaction = tx;
            q.CommandText = "SELECT run_id FROM harness_runs WHERE status IN ('Dispatching','Running','Unknown') ORDER BY run_id";
            using var r = q.ExecuteReader(); while (r.Read()) active.Add(r.GetString(0));
        }
        tx.Commit(); return new(queued, started, settled, abandoned, cursor, active);
    }

    /// <summary>
    /// Only a queued intent may start, and the start time is recorded once. A refused transition is
    /// reported, never thrown: an intent that raced with a client's abandon is a fact to record, not
    /// a reason for the host's drain loop to die.
    /// </summary>
    public bool MarkStarted(string runId, long nowUtc)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        if (!AutomationStore.ControlRunAdmissible(c, tx, runId)) return false;
        using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText = "UPDATE host_requests SET state=1,started_utc=$now WHERE run_id=$run AND state=0";
        q.Parameters.AddWithValue("$run", runId);
        q.Parameters.AddWithValue("$now", nowUtc);
        if (q.ExecuteNonQuery() != 1) return false;
        AutomationStore.RecordControlQueueTransition(c, tx, runId, "dispatched", nowUtc);
        tx.Commit();
        return true;
    }

    /// <summary>Only a started intent may settle; the outcome itself lives in the harness journal.</summary>
    public bool MarkSettled(string runId, string detail, long nowUtc) =>
        Move(runId, HostRequestState.Settled, from: [HostRequestState.Started], null, nowUtc, detail);

    /// <summary>
    /// Give up on an intent: it is never started by this host and never resent. Both the queue row
    /// and the reason stay readable, so an auditor can see what was dropped and why. A settled intent
    /// cannot be abandoned, and an abandoned one cannot be settled: this is a one-way state machine.
    /// </summary>
    public bool MarkAbandoned(string runId, string reason, long nowUtc) =>
        Move(runId, HostRequestState.Abandoned,
            from: [HostRequestState.Queued, HostRequestState.Started], null, nowUtc, reason);

    private bool Move(string runId, HostRequestState to, HostRequestState[] from, long? startedUtc,
        long? settledUtc, string? detail)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        using var q = c.CreateCommand();
        q.Transaction = tx;
        var allowed = string.Join(",", from.Select((_, index) => "$from" + index));
        q.CommandText = $"""
            UPDATE host_requests SET state=$state,
              started_utc=COALESCE($started,started_utc),
              settled_utc=COALESCE($settled,settled_utc),
              detail=COALESCE($detail,detail)
            WHERE run_id=$id AND state IN ({allowed})
            """;
        q.Parameters.AddWithValue("$state", (int)to);
        q.Parameters.AddWithValue("$started", (object?)startedUtc ?? DBNull.Value);
        q.Parameters.AddWithValue("$settled", (object?)settledUtc ?? DBNull.Value);
        q.Parameters.AddWithValue("$detail", (object?)detail ?? DBNull.Value);
        q.Parameters.AddWithValue("$id", runId);
        for (var index = 0; index < from.Length; index++)
            q.Parameters.AddWithValue("$from" + index, (int)from[index]);
        var moved = q.ExecuteNonQuery() > 0;
        if (moved)
            AutomationStore.RecordControlQueueTransition(c, tx, runId,
                to switch { HostRequestState.Queued => "queued", HostRequestState.Settled => "transport_settled", _ => "blocked" },
                settledUtc ?? startedUtc ?? 0);
        tx.Commit();
        return moved;
    }

    // ---------------------------------------------------------------------- recovery

    /// <summary>
    /// Recovery after the host process restarts. The harness journal is the truth about what was
    /// dispatched, so recovery re-reads it instead of trusting its own queue column:
    ///  - a Started row with no harness run at all never reached the dispatcher: safe to queue again;
    ///  - a Started row whose run is Dispatching/Running/Unknown has an uncertain outcome and an
    ///    owner we may not be: abandoned, left for the reconciler, never resent;
    ///  - a Started row whose run is already terminal: settled, nothing to do.
    /// Queued rows are returned untouched, which is what makes a restart continue the backlog.
    /// </summary>
    public HostRecovery RecoverAfterRestart(HarnessRunStore runs, long nowUtc)
    {
        var requeued = new List<string>();
        var abandoned = new List<string>();
        var settled = new List<string>();
        foreach (var request in List(HostRequestState.Started))
        {
            HarnessRunStatus status;
            try { status = runs.Get(request.RunId).Status; }
            catch (KeyNotFoundException)
            {
                // No dispatch record exists: the intent died with the host before anything started.
                Move(request.RunId, HostRequestState.Queued, from: [HostRequestState.Started],
                    null, null, "Restart: never dispatched");
                requeued.Add(request.RunId);
                continue;
            }
            if (status is HarnessRunStatus.Dispatching or HarnessRunStatus.Running
                or HarnessRunStatus.Unknown)
            {
                // Something may still be running or its outcome is unknown. Reconciliation owns that,
                // the queue does not; abandoning is what stops an automatic second execution.
                if (!Move(request.RunId, HostRequestState.Abandoned,
                        from: [HostRequestState.Started], null, nowUtc,
                        $"Restart: run is {status}; reconcile, never resend"))
                    continue;
                abandoned.Add(request.RunId);
                continue;
            }
            Move(request.RunId, HostRequestState.Settled, from: [HostRequestState.Started],
                null, nowUtc, $"Restart: run already {status}");
            settled.Add(request.RunId);
        }
        return new HostRecovery(List(HostRequestState.Queued), requeued, abandoned, settled);
    }

    /// <summary>The next intent this host may start, oldest accepted first.</summary>
    public HostRequest? NextQueued()
    {
        using var c = Open();
        // A suspended control task must not block unrelated explicit legacy work.
        // MarkStarted repeats this check under its write transaction.
        return List(HostRequestState.Queued).FirstOrDefault(q => AutomationStore.ControlRunAdmissible(c, null, q.RunId));
    }

    public static string Payload(HarnessRuntime runtime, HarnessRequest request) =>
        JsonSerializer.Serialize(new HostDispatch(runtime, request));

    public static HostDispatch ReadPayload(string payloadJson) =>
        JsonSerializer.Deserialize<HostDispatch>(payloadJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
        ?? throw new InvalidDataException("The queued payload is not a host dispatch.");
}

/// <summary>One queued execution: the runtime to launch and the request to send it.</summary>
public sealed record HostDispatch(HarnessRuntime Runtime, HarnessRequest Request);

/// <summary>
/// The single-host slot as observed through real process evidence. Stopped (explicit release) and
/// Crashed (recorded process gone without a release) are different facts: a client and a later host
/// must be able to tell a clean exit from a crash.
/// </summary>
public enum HostSlotState { Empty, Occupied, Stopped, Crashed }

/// <summary>
/// What a client sees when it asks where things stand: host identity, queue counts, the active runs
/// and the newest durable event cursor. Snapshots are read-only and never start or stop anything.
///</summary>
public sealed record HostSnapshot(string HostId, int ProcessId, long StartUtc, long ObservedUtc,
    bool Stopping, int Port, int Queued, int Started, int Settled, int Abandoned, long EventCursor,
    IReadOnlyList<string> ActiveRuns, IReadOnlyList<string> RecoveryActions);
