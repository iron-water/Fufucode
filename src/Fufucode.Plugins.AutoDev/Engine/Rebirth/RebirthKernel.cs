using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace Fufucode.Core.Automation.Rebirth;

/// <summary>
/// Additive project kernel for AutoDev rebirth. It owns project briefs, tickets, runs, evidence,
/// human decisions/inputs and attention in its own SQLite tables. It does not mutate the legacy
/// AutomationStore and does not call a model or start a process.
/// </summary>
public sealed class RebirthKernel : IDisposable
{
    private const int SchemaVersion = 2;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _path;
    private SqliteConnection? _scopeConnection;
    private SqliteTransaction? _scopeTransaction;

    // A Graph command and the authority changes it invokes commit together. The owner serializes
    // access to this kernel instance; independent instances still serialize through SQLite.
    internal T Atomic<T>(Func<SqliteConnection, SqliteTransaction, T> action)
    {
        if (_scopeConnection is not null) return action(_scopeConnection, _scopeTransaction!);
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        _scopeConnection = connection;
        _scopeTransaction = transaction;
        try
        {
            var result = action(connection, transaction);
            transaction.Commit();
            return result;
        }
        finally { _scopeConnection = null; _scopeTransaction = null; }
    }

    public RebirthKernel(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _path = Path.GetFullPath(databasePath);
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        Migrate();
    }

    public string DatabasePath => _path;

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = 10
        }.ToString());
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    private void Migrate()
    {
        using var c = Open();
        using var version = c.CreateCommand();
        version.CommandText = "PRAGMA user_version";
        var current = Convert.ToInt32(version.ExecuteScalar());
        if (current > SchemaVersion)
            throw new InvalidOperationException($"Rebirth schema {current} is newer than this build.");
        if (current == SchemaVersion) return;
        if (current == 1) { MigrateV2(c); return; }
        using var tx = c.BeginTransaction();
        Execute(c, tx, """
            CREATE TABLE IF NOT EXISTS rb_projects (
              project_id TEXT PRIMARY KEY, title TEXT NOT NULL, status TEXT NOT NULL,
              brief_version INTEGER NOT NULL, revision INTEGER NOT NULL,
              created_utc INTEGER NOT NULL, updated_utc INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS rb_briefs (
              project_id TEXT NOT NULL REFERENCES rb_projects(project_id), version INTEGER NOT NULL,
              statement TEXT NOT NULL, objectives_json TEXT NOT NULL, non_goals_json TEXT NOT NULL,
              constraints_json TEXT NOT NULL, acceptance_json TEXT NOT NULL, allowed_paths_json TEXT NOT NULL,
              budget_minutes INTEGER NOT NULL, autonomy TEXT NOT NULL, compute_quota REAL NOT NULL DEFAULT 1.0,
              created_by TEXT NOT NULL, created_utc INTEGER NOT NULL,
              PRIMARY KEY(project_id,version));
            CREATE TABLE IF NOT EXISTS rb_tickets (
              ticket_id TEXT PRIMARY KEY, project_id TEXT NOT NULL REFERENCES rb_projects(project_id),
              title TEXT NOT NULL, goal TEXT NOT NULL, acceptance_json TEXT NOT NULL, route TEXT NOT NULL,
              status TEXT NOT NULL, owner TEXT NOT NULL, plan_version INTEGER NOT NULL,
              mandate_json TEXT, task_class TEXT NOT NULL DEFAULT 'AdvancementTask',
              priority TEXT NOT NULL DEFAULT 'P1', resume_when TEXT, validation_command TEXT,
              acceptance_digest TEXT NOT NULL DEFAULT '', created_utc INTEGER NOT NULL, updated_utc INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS rb_tickets_project ON rb_tickets(project_id,status);
            CREATE TABLE IF NOT EXISTS rb_runs (
              run_id TEXT PRIMARY KEY, ticket_id TEXT NOT NULL REFERENCES rb_tickets(ticket_id),
              executor TEXT NOT NULL, model TEXT NOT NULL, workspace TEXT NOT NULL, status TEXT NOT NULL,
              started_utc INTEGER NOT NULL, ended_utc INTEGER, terminal_code TEXT, detail TEXT,
              result_kind TEXT, settled INTEGER NOT NULL DEFAULT 0);
            CREATE INDEX IF NOT EXISTS rb_runs_ticket ON rb_runs(ticket_id,status);
            CREATE TABLE IF NOT EXISTS rb_evidence (
              evidence_id TEXT PRIMARY KEY, ticket_id TEXT NOT NULL REFERENCES rb_tickets(ticket_id),
              run_id TEXT, kind TEXT NOT NULL, criterion TEXT NOT NULL, reference TEXT NOT NULL,
              passed INTEGER NOT NULL, verifier TEXT NOT NULL, created_utc INTEGER NOT NULL, valid INTEGER NOT NULL,
              acceptance_digest TEXT NOT NULL DEFAULT '');
            CREATE INDEX IF NOT EXISTS rb_evidence_ticket ON rb_evidence(ticket_id,criterion);
            CREATE TABLE IF NOT EXISTS rb_decisions (
              decision_id TEXT PRIMARY KEY, project_id TEXT NOT NULL REFERENCES rb_projects(project_id),
              ticket_id TEXT, kind TEXT NOT NULL, actor TEXT NOT NULL, target TEXT NOT NULL,
              scope TEXT NOT NULL, text TEXT NOT NULL, created_utc INTEGER NOT NULL, status TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS rb_decisions_project ON rb_decisions(project_id,status);
            CREATE TABLE IF NOT EXISTS rb_inputs (
              input_id TEXT PRIMARY KEY, project_id TEXT NOT NULL REFERENCES rb_projects(project_id),
              ticket_id TEXT, actor TEXT NOT NULL, kind TEXT NOT NULL, text TEXT NOT NULL,
              state TEXT NOT NULL, created_utc INTEGER NOT NULL, delivered_utc INTEGER,
              read_utc INTEGER, applied_utc INTEGER);
            CREATE INDEX IF NOT EXISTS rb_inputs_project ON rb_inputs(project_id,state);
            CREATE TABLE IF NOT EXISTS rb_attention (
              attention_id TEXT PRIMARY KEY, project_id TEXT NOT NULL REFERENCES rb_projects(project_id),
              ticket_id TEXT, question TEXT NOT NULL, status TEXT NOT NULL,
              created_utc INTEGER NOT NULL, answered_utc INTEGER, answer TEXT);
            CREATE INDEX IF NOT EXISTS rb_attention_project ON rb_attention(project_id,status);
            CREATE TABLE IF NOT EXISTS rb_events (
              cursor INTEGER PRIMARY KEY AUTOINCREMENT, event_id TEXT NOT NULL UNIQUE,
              type TEXT NOT NULL, project_id TEXT NOT NULL, ticket_id TEXT,
              payload_json TEXT NOT NULL, occurred_utc INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS rb_receipts (
              command_id TEXT PRIMARY KEY, payload_hash TEXT NOT NULL, result_json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS rb_leases (
              lease_id TEXT PRIMARY KEY, ticket_id TEXT NOT NULL REFERENCES rb_tickets(ticket_id),
              owner TEXT NOT NULL, mode TEXT NOT NULL, epoch INTEGER NOT NULL,
              expires_utc INTEGER NOT NULL, status TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS rb_leases_ticket ON rb_leases(ticket_id,status);
            CREATE TABLE IF NOT EXISTS rb_quota (
              project_id TEXT PRIMARY KEY REFERENCES rb_projects(project_id),
              compute_quota REAL NOT NULL, spent_slots INTEGER NOT NULL DEFAULT 0, paused INTEGER NOT NULL DEFAULT 0);
            """);
        Execute(c, tx, "PRAGMA user_version=2");
        tx.Commit();
    }

    private static void MigrateV2(SqliteConnection c)
    {
        using var tx = c.BeginTransaction();
        Execute(c, tx, "ALTER TABLE rb_briefs ADD COLUMN compute_quota REAL NOT NULL DEFAULT 1.0");
        Execute(c, tx, "ALTER TABLE rb_tickets ADD COLUMN task_class TEXT NOT NULL DEFAULT 'AdvancementTask'");
        Execute(c, tx, "ALTER TABLE rb_tickets ADD COLUMN priority TEXT NOT NULL DEFAULT 'P1'");
        Execute(c, tx, "ALTER TABLE rb_tickets ADD COLUMN resume_when TEXT");
        Execute(c, tx, "ALTER TABLE rb_tickets ADD COLUMN validation_command TEXT");
        Execute(c, tx, "ALTER TABLE rb_tickets ADD COLUMN acceptance_digest TEXT NOT NULL DEFAULT ''");
        Execute(c, tx, "ALTER TABLE rb_runs ADD COLUMN result_kind TEXT");
        Execute(c, tx, "ALTER TABLE rb_runs ADD COLUMN settled INTEGER NOT NULL DEFAULT 0");
        Execute(c, tx, "ALTER TABLE rb_evidence ADD COLUMN acceptance_digest TEXT NOT NULL DEFAULT ''");
        Execute(c, tx, """
            CREATE TABLE IF NOT EXISTS rb_leases (
              lease_id TEXT PRIMARY KEY, ticket_id TEXT NOT NULL REFERENCES rb_tickets(ticket_id),
              owner TEXT NOT NULL, mode TEXT NOT NULL, epoch INTEGER NOT NULL,
              expires_utc INTEGER NOT NULL, status TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS rb_leases_ticket ON rb_leases(ticket_id,status);
            CREATE TABLE IF NOT EXISTS rb_quota (
              project_id TEXT PRIMARY KEY REFERENCES rb_projects(project_id),
              compute_quota REAL NOT NULL, spent_slots INTEGER NOT NULL DEFAULT 0, paused INTEGER NOT NULL DEFAULT 0);
            """);
        Execute(c, tx, "PRAGMA user_version=2");
        tx.Commit();
    }

    private static void Execute(SqliteConnection c, SqliteTransaction? tx, string sql,
        params (string Name, object? Value)[] args)
    {
        using var command = c.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        foreach (var (name, value) in args) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static T Command<T>(SqliteConnection c, SqliteTransaction tx, string commandId, string payload,
        Func<SqliteConnection, SqliteTransaction, T> action)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandId);
        var hash = Sha(payload);
        using (var replay = c.CreateCommand())
        {
            replay.Transaction = tx;
            replay.CommandText = "SELECT payload_hash,result_json FROM rb_receipts WHERE command_id=$id";
            replay.Parameters.AddWithValue("$id", commandId);
            using var reader = replay.ExecuteReader();
            if (reader.Read())
            {
                if (reader.GetString(0) != hash)
                    throw new InvalidOperationException("Command identity reused with different input.");
                return JsonSerializer.Deserialize<T>(reader.GetString(1), Json)
                    ?? throw new InvalidOperationException("Stored receipt is unreadable.");
            }
        }
        var result = action(c, tx);
        using (var save = c.CreateCommand())
        {
            save.Transaction = tx;
            save.CommandText = "INSERT INTO rb_receipts(command_id,payload_hash,result_json) VALUES($id,$hash,$json)";
            save.Parameters.AddWithValue("$id", commandId);
            save.Parameters.AddWithValue("$hash", hash);
            save.Parameters.AddWithValue("$json", JsonSerializer.Serialize(result, Json));
            save.ExecuteNonQuery();
        }
        return result;
    }

    private T Mutate<T>(string commandId, string payload, Func<SqliteConnection, SqliteTransaction, T> action)
    {
        if (_scopeConnection is not null)
            return Command(_scopeConnection, _scopeTransaction!, commandId, payload, action);
        using var c = Open();
        using var tx = c.BeginTransaction();
        var result = Command(c, tx, commandId, payload, action);
        tx.Commit();
        return result;
    }

    private static string Sha(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string[] Clean(string[]? values) =>
        (values ?? []).Select(value => value?.Trim() ?? "").Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal).ToArray();

    private static string Required(string value, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        return value.Trim();
    }

    private static RebirthBrief NormalizeBrief(RebirthBrief brief)
    {
        ArgumentNullException.ThrowIfNull(brief);
        var statement = Required(brief.Statement, nameof(brief.Statement));
        var objectives = Clean(brief.Objectives);
        var acceptance = Clean(brief.Acceptance);
        var paths = Clean(brief.AllowedPaths);
        if (objectives.Length == 0) throw new ArgumentException("A project brief needs at least one objective.", nameof(brief));
        if (acceptance.Length == 0) throw new ArgumentException("A project brief needs at least one acceptance condition.", nameof(brief));
        if (brief.BudgetMinutes <= 0) throw new ArgumentOutOfRangeException(nameof(brief), "Budget must be positive.");
        if (brief.ComputeQuota is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(brief), "Compute quota must be between 0 and 1.");
        if (!Enum.IsDefined(brief.Autonomy)) throw new ArgumentOutOfRangeException(nameof(brief), "Unknown autonomy level.");
        return new RebirthBrief(statement, objectives, Clean(brief.NonGoals), Clean(brief.Constraints),
            acceptance, paths, brief.BudgetMinutes, brief.Autonomy) { ComputeQuota = brief.ComputeQuota };
    }

    private static RebirthProject GetProject(SqliteConnection c, SqliteTransaction? tx, string projectId)
    {
        using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText = "SELECT project_id,title,status,brief_version,revision,created_utc,updated_utc FROM rb_projects WHERE project_id=$id";
        q.Parameters.AddWithValue("$id", projectId);
        using var r = q.ExecuteReader();
        if (!r.Read()) throw new KeyNotFoundException("Unknown project: " + projectId);
        return new RebirthProject(r.GetString(0), r.GetString(1), Enum.Parse<RebirthProjectStatus>(r.GetString(2)),
            r.GetInt64(3), r.GetInt64(4), r.GetInt64(5), r.GetInt64(6));
    }

    private static RebirthBriefVersion GetBrief(SqliteConnection c, SqliteTransaction? tx, string projectId, long version)
    {
        using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText = """
            SELECT version,statement,objectives_json,non_goals_json,constraints_json,acceptance_json,
                   allowed_paths_json,budget_minutes,autonomy,compute_quota,created_by,created_utc
            FROM rb_briefs WHERE project_id=$id AND version=$version
            """;
        q.Parameters.AddWithValue("$id", projectId);
        q.Parameters.AddWithValue("$version", version);
        using var r = q.ExecuteReader();
        if (!r.Read()) throw new KeyNotFoundException("Unknown project brief version.");
        var brief = new RebirthBrief(r.GetString(1), DeserializeArray(r.GetString(2)),
            DeserializeArray(r.GetString(3)), DeserializeArray(r.GetString(4)), DeserializeArray(r.GetString(5)),
            DeserializeArray(r.GetString(6)), r.GetInt32(7), Enum.Parse<RebirthAutonomy>(r.GetString(8)))
            { ComputeQuota = r.GetDouble(9) };
        return new RebirthBriefVersion(r.GetInt64(0), brief, r.GetString(10), r.GetInt64(11));
    }

    private static string[] DeserializeArray(string json) =>
        JsonSerializer.Deserialize<string[]>(json, Json) ?? [];

    private static RebirthTicket GetTicket(SqliteConnection c, SqliteTransaction? tx, string ticketId)
    {
        using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText = """
            SELECT ticket_id,project_id,title,goal,acceptance_json,route,status,owner,plan_version,
                   mandate_json,task_class,priority,resume_when,validation_command,acceptance_digest,
                   created_utc,updated_utc FROM rb_tickets WHERE ticket_id=$id
            """;
        q.Parameters.AddWithValue("$id", ticketId);
        using var r = q.ExecuteReader();
        if (!r.Read()) throw new KeyNotFoundException("Unknown ticket: " + ticketId);
        return new RebirthTicket(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
            DeserializeArray(r.GetString(4)), r.GetString(5), Enum.Parse<RebirthTicketStatus>(r.GetString(6)),
            r.GetString(7), r.GetInt64(8), r.IsDBNull(9) ? null : r.GetString(9), r.GetInt64(15), r.GetInt64(16))
        {
            TaskClass = Enum.Parse<RebirthTaskClass>(r.GetString(10)),
            Priority = Enum.Parse<RebirthPriority>(r.GetString(11)),
            ResumeWhen = r.IsDBNull(12) ? null : r.GetString(12),
            ValidationCommand = r.IsDBNull(13) ? null : r.GetString(13),
            AcceptanceDigest = r.GetString(14)
        };
    }

    private static RebirthRun GetRun(SqliteConnection c, SqliteTransaction? tx, string runId)
    {
        using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText = """
            SELECT run_id,ticket_id,executor,model,workspace,status,started_utc,ended_utc,terminal_code,detail,
                   result_kind,settled FROM rb_runs WHERE run_id=$id
            """;
        q.Parameters.AddWithValue("$id", runId);
        using var r = q.ExecuteReader();
        if (!r.Read()) throw new KeyNotFoundException("Unknown run: " + runId);
        return new RebirthRun(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
            r.GetString(4), Enum.Parse<RebirthRunStatus>(r.GetString(5)), r.GetInt64(6),
            r.IsDBNull(7) ? null : r.GetInt64(7), r.IsDBNull(8) ? null : r.GetString(8),
            r.IsDBNull(9) ? null : r.GetString(9))
        {
            ResultKind = r.IsDBNull(10) ? null : Enum.Parse<RebirthTurnResultKind>(r.GetString(10)),
            Settled = r.GetInt32(11) != 0
        };
    }

    private static RebirthAttention GetAttention(SqliteConnection c, SqliteTransaction? tx, string id)
    {
        using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText = "SELECT attention_id,project_id,ticket_id,question,status,created_utc,answered_utc,answer FROM rb_attention WHERE attention_id=$id";
        q.Parameters.AddWithValue("$id", id);
        using var r = q.ExecuteReader();
        if (!r.Read()) throw new KeyNotFoundException("Unknown attention: " + id);
        return new RebirthAttention(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2),
            r.GetString(3), Enum.Parse<RebirthAttentionStatus>(r.GetString(4)), r.GetInt64(5),
            r.IsDBNull(6) ? null : r.GetInt64(6), r.IsDBNull(7) ? null : r.GetString(7));
    }

    private static RebirthDecision GetDecision(SqliteConnection c, SqliteTransaction tx, string id)
    {
        using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText = "SELECT decision_id,project_id,ticket_id,kind,actor,target,scope,text,created_utc,status FROM rb_decisions WHERE decision_id=$id";
        q.Parameters.AddWithValue("$id", id);
        using var r = q.ExecuteReader();
        if (!r.Read()) throw new KeyNotFoundException("Unknown decision: " + id);
        return new RebirthDecision(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2),
            Enum.Parse<RebirthDecisionKind>(r.GetString(3)), r.GetString(4), r.GetString(5),
            r.GetString(6), r.GetString(7), r.GetInt64(8), r.GetString(9));
    }

    private static void TouchProject(SqliteConnection c, SqliteTransaction tx, string projectId, long nowUtc)
    {
        Execute(c, tx, "UPDATE rb_projects SET revision=revision+1,updated_utc=$now WHERE project_id=$id",
            ("$now", nowUtc), ("$id", projectId));
    }

    private static void AppendEvent(SqliteConnection c, SqliteTransaction tx, string eventId, string type,
        string projectId, string? ticketId, object payload, long nowUtc)
    {
        Execute(c, tx, """
            INSERT OR IGNORE INTO rb_events(event_id,type,project_id,ticket_id,payload_json,occurred_utc)
            VALUES($event,$type,$project,$ticket,$payload,$now)
            """, ("$event", eventId), ("$type", type), ("$project", projectId), ("$ticket", ticketId),
            ("$payload", JsonSerializer.Serialize(payload, Json)), ("$now", nowUtc));
    }

    private static bool IsVetoed(SqliteConnection c, SqliteTransaction? tx, RebirthTicket ticket)
    {
        using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText = """
            SELECT COUNT(*) FROM rb_decisions WHERE project_id=$project AND kind='Veto' AND status='Active'
              AND (target='project:' || $project OR ticket_id=$ticket OR target='route:' || $route)
            """;
        q.Parameters.AddWithValue("$project", ticket.ProjectId);
        q.Parameters.AddWithValue("$ticket", ticket.Id);
        q.Parameters.AddWithValue("$route", ticket.Route);
        return Convert.ToInt64(q.ExecuteScalar()) > 0;
    }

    private static int CountOpenAttention(SqliteConnection c, SqliteTransaction? tx, string projectId)
    {
        using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText = "SELECT COUNT(*) FROM rb_attention WHERE project_id=$project AND status='Open'";
        q.Parameters.AddWithValue("$project", projectId);
        return Convert.ToInt32(q.ExecuteScalar());
    }

    /// <summary>Creates a real project identity before the human has supplied a purpose. No work is authorized.</summary>
    internal RebirthProject EnsureProjectShell(string commandId, string projectId, string title, long nowUtc) =>
        Mutate(commandId, JsonSerializer.Serialize(new { projectId, title, shell = true }, Json), (c, tx) =>
        {
            Execute(c, tx, """
                INSERT INTO rb_projects(project_id,title,status,brief_version,revision,created_utc,updated_utc)
                VALUES($id,$title,'Active',1,1,$now,$now)
                """, ("$id", projectId), ("$title", Required(title, nameof(title))), ("$now", nowUtc));
            InsertBrief(c, tx, projectId, 1, RebirthBrief.Empty, "system", nowUtc);
            Execute(c, tx, "INSERT INTO rb_quota(project_id,compute_quota,spent_slots,paused) VALUES($id,0,0,1)",
                ("$id", projectId));
            AppendEvent(c, tx, commandId + ":project.registered", "project.registered", projectId, null,
                new { projectId, title }, nowUtc);
            return GetProject(c, tx, projectId);
        });

    public RebirthProject CreateProject(string commandId, string title, RebirthBrief brief, string actor,
        long nowUtc, string? projectId = null)
    {
        var normalized = NormalizeBrief(brief);
        var id = string.IsNullOrWhiteSpace(projectId) ? "project-" + Guid.NewGuid().ToString("N")[..12] : projectId!;
        var payload = JsonSerializer.Serialize(new { title, brief = normalized, actor, projectId = id }, Json);
        return Mutate(commandId, payload, (c, tx) =>
        {
            using (var probe = c.CreateCommand())
            {
                probe.Transaction = tx;
                probe.CommandText = "SELECT COUNT(*) FROM rb_projects WHERE project_id=$id";
                probe.Parameters.AddWithValue("$id", id);
                if (Convert.ToInt64(probe.ExecuteScalar()) != 0)
                    throw new InvalidOperationException("Project already exists: " + id);
            }
            Execute(c, tx, """
                INSERT INTO rb_projects(project_id,title,status,brief_version,revision,created_utc,updated_utc)
                VALUES($id,$title,'Active',1,1,$now,$now)
                """, ("$id", id), ("$title", Required(title, nameof(title))), ("$now", nowUtc));
            InsertBrief(c, tx, id, 1, normalized, actor, nowUtc);
            Execute(c, tx, "INSERT INTO rb_quota(project_id,compute_quota,spent_slots,paused) VALUES($id,$quota,0,0)",
                ("$id", id), ("$quota", normalized.ComputeQuota));
            AppendEvent(c, tx, commandId + ":project.created", "project.created", id, null,
                new { projectId = id, title, brief = normalized }, nowUtc);
            return GetProject(c, tx, id);
        });
    }

    private static void InsertBrief(SqliteConnection c, SqliteTransaction tx, string projectId, long version,
        RebirthBrief brief, string actor, long nowUtc)
    {
        Execute(c, tx, """
            INSERT INTO rb_briefs(project_id,version,statement,objectives_json,non_goals_json,constraints_json,
              acceptance_json,allowed_paths_json,budget_minutes,autonomy,compute_quota,created_by,created_utc)
            VALUES($id,$version,$statement,$objectives,$nongoals,$constraints,$acceptance,$paths,$budget,$autonomy,$quota,$actor,$now)
            """, ("$id", projectId), ("$version", version), ("$statement", brief.Statement),
            ("$objectives", JsonSerializer.Serialize(brief.Objectives, Json)),
            ("$nongoals", JsonSerializer.Serialize(brief.NonGoals, Json)),
            ("$constraints", JsonSerializer.Serialize(brief.Constraints, Json)),
            ("$acceptance", JsonSerializer.Serialize(brief.Acceptance, Json)),
            ("$paths", JsonSerializer.Serialize(brief.AllowedPaths, Json)),
            ("$budget", brief.BudgetMinutes), ("$autonomy", brief.Autonomy.ToString()),
            ("$quota", brief.ComputeQuota),
            ("$actor", Required(actor, nameof(actor))), ("$now", nowUtc));
    }

    public RebirthBriefVersion UpdateBrief(string commandId, string projectId, long expectedVersion,
        RebirthBrief brief, string actor, long nowUtc)
    {
        var normalized = NormalizeBrief(brief);
        var payload = JsonSerializer.Serialize(new { projectId, expectedVersion, brief = normalized, actor }, Json);
        return Mutate(commandId, payload, (c, tx) =>
        {
            var project = GetProject(c, tx, projectId);
            if (project.BriefVersion != expectedVersion)
                throw new InvalidOperationException("The project brief changed; refresh before updating.");
            if (project.Status is RebirthProjectStatus.Completed or RebirthProjectStatus.Archived)
                throw new InvalidOperationException("A completed or archived project cannot change its brief.");
            var next = checked(project.BriefVersion + 1);
            InsertBrief(c, tx, projectId, next, normalized, actor, nowUtc);
            Execute(c, tx, "UPDATE rb_projects SET brief_version=$version,status='Active',revision=revision+1,updated_utc=$now WHERE project_id=$id",
                ("$version", next), ("$now", nowUtc), ("$id", projectId));
            Execute(c, tx, """
                INSERT INTO rb_quota(project_id,compute_quota,spent_slots,paused)
                VALUES($id,$quota,0,CASE WHEN $quota<=0 THEN 1 ELSE 0 END)
                ON CONFLICT(project_id) DO UPDATE SET compute_quota=$quota,
                  paused=CASE WHEN $quota<=0 THEN 1 ELSE 0 END
                """, ("$quota", normalized.ComputeQuota), ("$id", projectId));
            AppendEvent(c, tx, commandId + ":brief.updated", "brief.updated", projectId, null,
                new { projectId, version = next, brief = normalized }, nowUtc);
            return new RebirthBriefVersion(next, normalized, actor, nowUtc);
        });
    }

    public RebirthTicket CreateTicket(string commandId, string projectId, string title, string goal,
        string[] acceptance, string route, string owner, long nowUtc, long planVersion = 1,
        string? ticketId = null, string? mandateJson = null,
        RebirthTaskClass taskClass = RebirthTaskClass.AdvancementTask,
        RebirthPriority? priority = null, string? resumeWhen = null, string? validationCommand = null)
    {
        var criteria = Clean(acceptance);
        if (criteria.Length == 0) throw new ArgumentException("A ticket needs at least one acceptance condition.", nameof(acceptance));
        if (!Enum.IsDefined(taskClass)) throw new ArgumentOutOfRangeException(nameof(taskClass));
        var normalizedRoute = string.IsNullOrWhiteSpace(route) ? "default" : route.Trim();
        var normalizedResume = NormalizeResumeWhen(resumeWhen);
        var selectedPriority = priority ?? (taskClass is RebirthTaskClass.UserGate or RebirthTaskClass.Blocker
            ? RebirthPriority.P0 : RebirthPriority.P1);
        if (!Enum.IsDefined(selectedPriority)) throw new ArgumentOutOfRangeException(nameof(priority));
        var digest = Sha(JsonSerializer.Serialize(criteria, Json));
        var id = string.IsNullOrWhiteSpace(ticketId) ? "ticket-" + Guid.NewGuid().ToString("N")[..12] : ticketId!;
        var payload = JsonSerializer.Serialize(new { projectId, title, goal, acceptance = criteria, route = normalizedRoute,
            owner, planVersion, ticketId = id, taskClass, priority = selectedPriority, resumeWhen = normalizedResume, validationCommand }, Json);
        return Mutate(commandId, payload, (c, tx) =>
        {
            var project = GetProject(c, tx, projectId);
            if (project.Status != RebirthProjectStatus.Active)
                throw new InvalidOperationException("The project is not active.");
            var brief = GetBrief(c, tx, projectId, project.BriefVersion).Brief;
            if (IsRouteVetoed(c, tx, projectId, normalizedRoute))
                throw new InvalidOperationException("This route is blocked by an active human veto.");
            var status = taskClass switch
            {
                RebirthTaskClass.UserGate => RebirthTicketStatus.WaitingPerson,
                RebirthTaskClass.UserAction => RebirthTicketStatus.Authorized,
                RebirthTaskClass.Blocker => RebirthTicketStatus.Proposed,
                _ => brief.Autonomy == RebirthAutonomy.WithinMandate
                    ? RebirthTicketStatus.Authorized : RebirthTicketStatus.Proposed
            };
            Execute(c, tx, """
                INSERT INTO rb_tickets(ticket_id,project_id,title,goal,acceptance_json,route,status,owner,
                  plan_version,mandate_json,task_class,priority,resume_when,validation_command,acceptance_digest,created_utc,updated_utc)
                VALUES($id,$project,$title,$goal,$acceptance,$route,$status,$owner,$plan,$mandate,$class,$priority,$resume,$validation,$digest,$now,$now)
                """, ("$id", id), ("$project", projectId), ("$title", Required(title, nameof(title))),
                ("$goal", Required(goal, nameof(goal))), ("$acceptance", JsonSerializer.Serialize(criteria, Json)),
                ("$route", normalizedRoute), ("$status", status.ToString()),
                ("$owner", Required(owner, nameof(owner))), ("$plan", planVersion),
                ("$mandate", (object?)mandateJson ?? DBNull.Value), ("$class", taskClass.ToString()),
                ("$priority", selectedPriority.ToString()), ("$resume", (object?)normalizedResume ?? DBNull.Value),
                ("$validation", (object?)validationCommand ?? DBNull.Value), ("$digest", digest), ("$now", nowUtc));
            TouchProject(c, tx, projectId, nowUtc);
            AppendEvent(c, tx, commandId + ":ticket.created", "ticket.created", projectId, id,
                new { ticketId = id, title, acceptance = criteria, route = normalizedRoute, status = status.ToString(),
                    taskClass = taskClass.ToString(), priority = selectedPriority.ToString(), resumeWhen = normalizedResume }, nowUtc);
            return GetTicket(c, tx, id);
        });
    }

    private static string? NormalizeResumeWhen(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var candidate = value.Trim();
        var prefixes = new[] { "todo_done:", "monitor_changed:", "capacity_available:", "pr_merged:", "resume_at:" };
        if (prefixes.Any(prefix => candidate.StartsWith(prefix, StringComparison.Ordinal)))
            return candidate;
        throw new ArgumentException("resumeWhen must use todo_done, monitor_changed, capacity_available, pr_merged or resume_at.", nameof(value));
    }

    private static bool IsRouteVetoed(SqliteConnection c, SqliteTransaction tx, string projectId, string route)
    {
        using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText = """
            SELECT COUNT(*) FROM rb_decisions
            WHERE project_id=$project AND kind='Veto' AND status='Active'
              AND (target='project:' || $project OR target='route:' || $route)
            """;
        q.Parameters.AddWithValue("$project", projectId);
        q.Parameters.AddWithValue("$route", route);
        return Convert.ToInt64(q.ExecuteScalar()) > 0;
    }

    public RebirthTicket AuthorizeTicket(string commandId, string ticketId, string actor, long nowUtc)
    {
        var payload = JsonSerializer.Serialize(new { ticketId, actor }, Json);
        return Mutate(commandId, payload, (c, tx) =>
        {
            var ticket = GetTicket(c, tx, ticketId);
            var project = GetProject(c, tx, ticket.ProjectId);
            if (project.Status != RebirthProjectStatus.Active)
                throw new InvalidOperationException("The project is not active.");
            if (ticket.Status != RebirthTicketStatus.Proposed)
                throw new InvalidOperationException("Only a proposed ticket can be authorized.");
            if (IsVetoed(c, tx, ticket))
                throw new InvalidOperationException("This ticket is blocked by an active human veto.");
            Execute(c, tx, "UPDATE rb_tickets SET status='Authorized',updated_utc=$now WHERE ticket_id=$id",
                ("$now", nowUtc), ("$id", ticketId));
            TouchProject(c, tx, ticket.ProjectId, nowUtc);
            AppendEvent(c, tx, commandId + ":ticket.authorized", "ticket.authorized", ticket.ProjectId, ticketId,
                new { ticketId, actor }, nowUtc);
            return GetTicket(c, tx, ticketId);
        });
    }

    public RebirthTurnAdmission InspectAdmission(string ticketId, string owner, long nowUtc)
    {
        if (_scopeConnection is not null)
            return EvaluateAdmission(_scopeConnection, _scopeTransaction,
                GetTicket(_scopeConnection, _scopeTransaction, ticketId), owner, nowUtc);
        using var c = Open();
        var ticket = GetTicket(c, null, ticketId);
        return EvaluateAdmission(c, null, ticket, owner, nowUtc);
    }

    private static RebirthTurnAdmission EvaluateAdmission(SqliteConnection c, SqliteTransaction? tx,
        RebirthTicket ticket, string owner, long nowUtc)
    {
        var project = GetProject(c, tx, ticket.ProjectId);
        if (project.Status != RebirthProjectStatus.Active)
            return new(false, RebirthTurnRoute.Blocked, "Project is not active.");
        var quota = GetQuota(c, tx, ticket.ProjectId);
        if (quota.Paused || quota.ComputeQuota <= 0)
            return new(false, RebirthTurnRoute.Blocked, "Compute quota is paused or zero.");
        if (ticket.TaskClass == RebirthTaskClass.UserAction)
            return new(false, RebirthTurnRoute.UserActionRequired, "This is a non-blocking user action.");
        if (ticket.TaskClass == RebirthTaskClass.Blocker)
            return new(false, RebirthTurnRoute.Blocked, "This is a blocker, not advancement work.");
        if (ticket.TaskClass == RebirthTaskClass.UserGate && ticket.Status is not (RebirthTicketStatus.Authorized or RebirthTicketStatus.Active))
            return new(false, RebirthTurnRoute.UserActionRequired, "The user gate needs an explicit decision.");
        if (ticket.Status is RebirthTicketStatus.Proposed or RebirthTicketStatus.HumanOwned
            or RebirthTicketStatus.ReplanRequired or RebirthTicketStatus.NeedsReconciliation
            or RebirthTicketStatus.Done or RebirthTicketStatus.Cancelled or RebirthTicketStatus.Failed)
            return new(false, RebirthTurnRoute.Blocked, "The ticket cannot run in state " + ticket.Status + ".");
        if (ticket.Status == RebirthTicketStatus.WaitingPerson && CountOpenAttention(c, tx, ticket.ProjectId) > 0)
            return new(false, RebirthTurnRoute.UserActionRequired, "The ticket is waiting for a human answer.");
        if (IsVetoed(c, tx, ticket))
            return new(false, RebirthTurnRoute.Blocked, "The ticket is blocked by an active human veto.");
        if (!ResumeDue(c, tx, ticket, nowUtc))
            return new(false, RebirthTurnRoute.Wait, "The ticket resume condition is not due.");
        var lease = ReadActiveLease(c, tx, ticket.Id, nowUtc);
        if (lease is not null && !string.Equals(lease.Owner, owner, StringComparison.Ordinal))
            return new(false, RebirthTurnRoute.Blocked, "The ticket is leased by " + lease.Owner + ".");
        return new(true, RebirthTurnRoute.ReadyForHost, "Admitted.",
            lease?.Id, lease?.ExpiresUtc);
    }

    private static RebirthQuotaState GetQuota(SqliteConnection c, SqliteTransaction? tx, string projectId)
    {
        using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText = "SELECT compute_quota,spent_slots,paused FROM rb_quota WHERE project_id=$id";
        q.Parameters.AddWithValue("$id", projectId);
        using var r = q.ExecuteReader();
        return r.Read()
            ? new RebirthQuotaState(r.GetDouble(0), r.GetInt64(1), r.GetInt32(2) != 0)
            : new RebirthQuotaState(1.0, 0, false);
    }

    private static RebirthLease? ReadActiveLease(SqliteConnection c, SqliteTransaction? tx, string ticketId, long nowUtc)
    {
        using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText = """
            SELECT lease_id,ticket_id,owner,mode,epoch,expires_utc,status FROM rb_leases
            WHERE ticket_id=$ticket AND status='Active' AND expires_utc>$now
            ORDER BY epoch DESC LIMIT 1
            """;
        q.Parameters.AddWithValue("$ticket", ticketId);
        q.Parameters.AddWithValue("$now", nowUtc);
        using var r = q.ExecuteReader();
        return r.Read()
            ? new RebirthLease(r.GetString(0), r.GetString(1), r.GetString(2),
                Enum.Parse<RebirthLeaseMode>(r.GetString(3)), r.GetInt64(4), r.GetInt64(5), r.GetString(6))
            : null;
    }

    private static RebirthLease AcquireLease(SqliteConnection c, SqliteTransaction tx, RebirthTicket ticket,
        string owner, long nowUtc, int ttlSeconds)
    {
        var current = ReadActiveLease(c, tx, ticket.Id, nowUtc);
        if (current is not null)
        {
            if (!string.Equals(current.Owner, owner, StringComparison.Ordinal))
                throw new InvalidOperationException("The ticket is leased by another actor.");
            return current;
        }
        long epoch;
        using (var q = c.CreateCommand())
        {
            q.Transaction = tx;
            q.CommandText = "SELECT COALESCE(MAX(epoch),0)+1 FROM rb_leases WHERE ticket_id=$ticket";
            q.Parameters.AddWithValue("$ticket", ticket.Id);
            epoch = Convert.ToInt64(q.ExecuteScalar());
        }
        var expires = nowUtc + Math.Clamp(ttlSeconds, 30, 3600) * 1000L;
        var lease = new RebirthLease("lease-" + Guid.NewGuid().ToString("N")[..12], ticket.Id, owner,
            RebirthLeaseMode.SoftClaim, epoch, expires, "Active");
        Execute(c, tx, """
            INSERT INTO rb_leases(lease_id,ticket_id,owner,mode,epoch,expires_utc,status)
            VALUES($id,$ticket,$owner,$mode,$epoch,$expires,'Active')
            """, ("$id", lease.Id), ("$ticket", ticket.Id), ("$owner", owner),
            ("$mode", lease.Mode.ToString()), ("$epoch", epoch), ("$expires", expires));
        return lease;
    }

    private static void ReleaseActiveLease(SqliteConnection c, SqliteTransaction tx, string ticketId)
    {
        Execute(c, tx, "UPDATE rb_leases SET status='Released' WHERE ticket_id=$ticket AND status='Active'",
            ("$ticket", ticketId));
    }

    private static bool ResumeDue(SqliteConnection c, SqliteTransaction? tx, RebirthTicket ticket, long nowUtc)
    {
        if (string.IsNullOrWhiteSpace(ticket.ResumeWhen)) return true;
        var value = ticket.ResumeWhen.Trim();
        if (value.StartsWith("resume_at:", StringComparison.Ordinal))
            return DateTimeOffset.TryParse(value["resume_at:".Length..], out var due) && due.ToUnixTimeMilliseconds() <= nowUtc;
        if (value.StartsWith("todo_done:", StringComparison.Ordinal))
        {
            try { return GetTicket(c, tx, value["todo_done:".Length..]).Status == RebirthTicketStatus.Done; }
            catch (KeyNotFoundException) { return false; }
        }
        if (value.StartsWith("capacity_available:", StringComparison.Ordinal)) return true;
        return false;
    }

    public RebirthRun StartRun(string commandId, string ticketId, string executor, string model,
        string workspace, long nowUtc, string? runId = null, int leaseTtlSeconds = 300)
    {
        var id = string.IsNullOrWhiteSpace(runId) ? "run-" + Guid.NewGuid().ToString("N")[..12] : runId!;
        var payload = JsonSerializer.Serialize(new { ticketId, executor, model, workspace, runId = id, leaseTtlSeconds }, Json);
        return Mutate(commandId, payload, (c, tx) =>
        {
            var ticket = GetTicket(c, tx, ticketId);
            var admission = EvaluateAdmission(c, tx, ticket, ticket.Owner, nowUtc);
            if (!admission.ShouldRun)
                throw new InvalidOperationException(admission.Reason);
            using (var active = c.CreateCommand())
            {
                active.Transaction = tx;
                active.CommandText = "SELECT COUNT(*) FROM rb_runs WHERE ticket_id=$ticket AND status IN ('Queued','Running')";
                active.Parameters.AddWithValue("$ticket", ticketId);
                if (Convert.ToInt64(active.ExecuteScalar()) > 0)
                    throw new InvalidOperationException("The ticket already has an active run.");
            }
            var lease = AcquireLease(c, tx, ticket, ticket.Owner, nowUtc, leaseTtlSeconds);
            Execute(c, tx, """
                INSERT INTO rb_runs(run_id,ticket_id,executor,model,workspace,status,started_utc)
                VALUES($id,$ticket,$executor,$model,$workspace,'Running',$now)
                """, ("$id", id), ("$ticket", ticketId), ("$executor", Required(executor, nameof(executor))),
                ("$model", Required(model, nameof(model))), ("$workspace", Required(workspace, nameof(workspace))),
                ("$now", nowUtc));
            Execute(c, tx, "UPDATE rb_tickets SET status='Active',updated_utc=$now WHERE ticket_id=$id",
                ("$now", nowUtc), ("$id", ticketId));
            Execute(c, tx, "UPDATE rb_quota SET spent_slots=spent_slots+1 WHERE project_id=$id",
                ("$id", ticket.ProjectId));
            TouchProject(c, tx, ticket.ProjectId, nowUtc);
            AppendEvent(c, tx, commandId + ":run.started", "run.started", ticket.ProjectId, ticketId,
                new { runId = id, executor, model, workspace, leaseId = lease.Id, route = admission.Route.ToString() }, nowUtc);
            return GetRun(c, tx, id);
        });
    }

    public RebirthRun CompleteRun(string commandId, string runId, RebirthRunOutcome outcome,
        string? terminalCode, string? detail, long nowUtc)
    {
        var payload = JsonSerializer.Serialize(new { runId, outcome, terminalCode, detail }, Json);
        return Mutate(commandId, payload, (c, tx) =>
        {
            var run = GetRun(c, tx, runId);
            if (run.Status != RebirthRunStatus.Running)
                throw new InvalidOperationException("Only a running run can be completed.");
            var status = outcome switch
            {
                RebirthRunOutcome.Completed => RebirthRunStatus.Completed,
                RebirthRunOutcome.Failed => RebirthRunStatus.Failed,
                RebirthRunOutcome.Interrupted => RebirthRunStatus.Interrupted,
                _ => RebirthRunStatus.Unknown
            };
            var resultKind = outcome switch
            {
                RebirthRunOutcome.Completed => RebirthTurnResultKind.Progress,
                RebirthRunOutcome.Failed => RebirthTurnResultKind.Failed,
                RebirthRunOutcome.Interrupted => RebirthTurnResultKind.Failed,
                _ => RebirthTurnResultKind.Unknown
            };
            var ticket = GetTicket(c, tx, run.TicketId);
            Execute(c, tx, """
                UPDATE rb_runs SET status=$status,ended_utc=$now,terminal_code=$code,detail=$detail,
                  result_kind=$kind,settled=1 WHERE run_id=$id
                """, ("$status", status.ToString()), ("$now", nowUtc), ("$code", (object?)terminalCode ?? DBNull.Value),
                ("$detail", (object?)detail ?? DBNull.Value), ("$kind", resultKind.ToString()), ("$id", runId));
            var ticketStatus = outcome switch
            {
                RebirthRunOutcome.Unknown => RebirthTicketStatus.NeedsReconciliation,
                _ when ticket.Status is RebirthTicketStatus.HumanOwned or RebirthTicketStatus.Cancelled => ticket.Status,
                _ when IsVetoed(c, tx, ticket) => RebirthTicketStatus.ReplanRequired,
                RebirthRunOutcome.Completed => RebirthTicketStatus.Review,
                _ => RebirthTicketStatus.ReplanRequired
            };
            Execute(c, tx, "UPDATE rb_decisions SET status=$status WHERE ticket_id=$id AND kind='Stop' AND status='Requested'",
                ("$status", outcome == RebirthRunOutcome.Interrupted ? "Acknowledged" : "Settled:" + status), ("$id", run.TicketId));
            Execute(c, tx, "UPDATE rb_tickets SET status=$status,updated_utc=$now WHERE ticket_id=$id",
                ("$status", ticketStatus.ToString()), ("$now", nowUtc), ("$id", run.TicketId));
            ReleaseActiveLease(c, tx, run.TicketId);
            TouchProject(c, tx, ticket.ProjectId, nowUtc);
            AppendEvent(c, tx, commandId + ":run.completed", "run.completed", ticket.ProjectId, run.TicketId,
                new { runId, outcome = outcome.ToString(), resultKind = resultKind.ToString(),
                    terminalCode, detail, ticketStatus = ticketStatus.ToString() }, nowUtc);
            return GetRun(c, tx, runId);
        });
    }

    public RebirthEvidence AddEvidence(string commandId, string ticketId, string? runId,
        RebirthEvidenceKind kind, string criterion, string reference, bool passed, string verifier, long nowUtc,
        string? evidenceId = null)
    {
        var id = string.IsNullOrWhiteSpace(evidenceId) ? "evidence-" + Guid.NewGuid().ToString("N")[..12] : evidenceId!;
        var payload = JsonSerializer.Serialize(new { ticketId, runId, kind, criterion, reference, passed, verifier, evidenceId = id }, Json);
        return Mutate(commandId, payload, (c, tx) =>
        {
            var ticket = GetTicket(c, tx, ticketId);
            if (runId is not null)
            {
                var run = GetRun(c, tx, runId);
                if (run.TicketId != ticketId) throw new InvalidOperationException("The run belongs to another ticket.");
                if (run.Status is RebirthRunStatus.Queued or RebirthRunStatus.Running)
                    throw new InvalidOperationException("Evidence cannot be attached before the run ends.");
            }
            Execute(c, tx, """
                INSERT INTO rb_evidence(evidence_id,ticket_id,run_id,kind,criterion,reference,passed,verifier,created_utc,valid,acceptance_digest)
                VALUES($id,$ticket,$run,$kind,$criterion,$reference,$passed,$verifier,$now,1,$digest)
                """, ("$id", id), ("$ticket", ticketId), ("$run", (object?)runId ?? DBNull.Value),
                ("$kind", kind.ToString()), ("$criterion", Required(criterion, nameof(criterion))),
                ("$reference", Required(reference, nameof(reference))), ("$passed", passed ? 1 : 0),
                ("$verifier", Required(verifier, nameof(verifier))), ("$now", nowUtc),
                ("$digest", ticket.AcceptanceDigest));
            if (!passed && ticket.Status is not (RebirthTicketStatus.Done or RebirthTicketStatus.Cancelled))
                Execute(c, tx, "UPDATE rb_tickets SET status='ReplanRequired',updated_utc=$now WHERE ticket_id=$id",
                    ("$now", nowUtc), ("$id", ticketId));
            TouchProject(c, tx, ticket.ProjectId, nowUtc);
            AppendEvent(c, tx, commandId + ":evidence.recorded", "evidence.recorded", ticket.ProjectId, ticketId,
                new { evidenceId = id, runId, kind = kind.ToString(), criterion, passed, reference }, nowUtc);
            using var q = c.CreateCommand();
            q.Transaction = tx;
            q.CommandText = "SELECT evidence_id,ticket_id,run_id,kind,criterion,reference,passed,verifier,created_utc,valid FROM rb_evidence WHERE evidence_id=$id";
            q.Parameters.AddWithValue("$id", id);
            using var r = q.ExecuteReader();
            r.Read();
            return new RebirthEvidence(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2),
                Enum.Parse<RebirthEvidenceKind>(r.GetString(3)), r.GetString(4), r.GetString(5),
                r.GetInt32(6) != 0, r.GetString(7), r.GetInt64(8), r.GetInt32(9) != 0);
        });
    }

    internal void InvalidateEvidence(string commandId, string evidenceId, string reason, long nowUtc) =>
        Mutate(commandId, JsonSerializer.Serialize(new { evidenceId, reason }, Json), (c, tx) =>
        {
            using var q = c.CreateCommand(); q.Transaction = tx;
            q.CommandText = "SELECT ticket_id FROM rb_evidence WHERE evidence_id=$id";
            q.Parameters.AddWithValue("$id", evidenceId);
            var ticketId = q.ExecuteScalar() as string ?? throw new KeyNotFoundException("Unknown evidence: " + evidenceId);
            var ticket = GetTicket(c, tx, ticketId);
            Execute(c, tx, "UPDATE rb_evidence SET valid=0 WHERE evidence_id=$id", ("$id", evidenceId));
            TouchProject(c, tx, ticket.ProjectId, nowUtc);
            AppendEvent(c, tx, commandId + ":evidence.invalidated", "evidence.invalidated", ticket.ProjectId, ticketId,
                new { evidenceId, reason }, nowUtc);
            return true;
        });

    /// <summary>Human acceptance is separate from an executor finishing a run. Evidence is never manufactured here.</summary>
    internal RebirthTicket AcceptTicket(string commandId, string ticketId, string reference, long nowUtc) =>
        Atomic((c, tx) =>
        {
            var ticket = GetTicket(c, tx, ticketId);
            if (ticket.Status != RebirthTicketStatus.Review)
                throw new InvalidOperationException("Only a reviewing ticket can be accepted by a human.");
            using var evidence = c.CreateCommand();
            evidence.Transaction = tx;
            evidence.CommandText = "SELECT COUNT(*) FROM rb_evidence WHERE ticket_id=$id AND reference=$ref AND valid=1 AND passed=1 AND kind<>'Note' AND acceptance_digest=$digest";
            evidence.Parameters.AddWithValue("$id", ticketId);
            evidence.Parameters.AddWithValue("$ref", Required(reference, nameof(reference)));
            evidence.Parameters.AddWithValue("$digest", ticket.AcceptanceDigest);
            if (Convert.ToInt64(evidence.ExecuteScalar()) == 0)
                throw new InvalidOperationException("Acceptance must reference existing, valid passing evidence.");
            foreach (var criterion in ticket.Acceptance)
            {
                using var passing = c.CreateCommand(); passing.Transaction = tx;
                passing.CommandText = "SELECT COUNT(*) FROM rb_evidence WHERE ticket_id=$id AND criterion=$criterion AND valid=1 AND passed=1 AND kind<>'Note' AND acceptance_digest=$digest";
                passing.Parameters.AddWithValue("$id", ticketId); passing.Parameters.AddWithValue("$criterion", criterion);
                passing.Parameters.AddWithValue("$digest", ticket.AcceptanceDigest);
                if (Convert.ToInt64(passing.ExecuteScalar()) == 0)
                    throw new InvalidOperationException("Missing valid non-note passing evidence for: " + criterion);
            }
            var completed = CompleteTicket(commandId, ticketId, nowUtc);
            Execute(c, tx, """
                INSERT INTO rb_decisions(decision_id,project_id,ticket_id,kind,actor,target,scope,text,created_utc,status)
                VALUES($id,$project,$ticket,'Acceptance','human',$target,'ticket',$text,$now,'Accepted')
                """, ("$id", "accept-" + Sha(commandId)[..24]), ("$project", ticket.ProjectId),
                ("$ticket", ticketId), ("$target", "ticket:" + ticketId), ("$text", reference), ("$now", nowUtc));
            return completed;
        });

    public RebirthTicket CompleteTicket(string commandId, string ticketId, long nowUtc)
    {
        var payload = JsonSerializer.Serialize(new { ticketId }, Json);
        return Mutate(commandId, payload, (c, tx) =>
        {
            var ticket = GetTicket(c, tx, ticketId);
            if (ticket.Status is not (RebirthTicketStatus.Review or RebirthTicketStatus.Active))
                throw new InvalidOperationException("Only a reviewing or active ticket can be completed.");
            if (CountOpenAttention(c, tx, ticket.ProjectId) > 0)
                throw new InvalidOperationException("Resolve the open attention before completing the ticket.");
            if (IsVetoed(c, tx, ticket))
                throw new InvalidOperationException("The ticket is blocked by an active human veto.");
            foreach (var criterion in ticket.Acceptance)
            {
                using var q = c.CreateCommand();
                q.Transaction = tx;
                q.CommandText = "SELECT COUNT(*) FROM rb_evidence WHERE ticket_id=$ticket AND criterion=$criterion AND passed=1 AND valid=1 AND acceptance_digest=$digest";
                q.Parameters.AddWithValue("$ticket", ticketId);
                q.Parameters.AddWithValue("$criterion", criterion);
                q.Parameters.AddWithValue("$digest", ticket.AcceptanceDigest);
                if (Convert.ToInt64(q.ExecuteScalar()) == 0)
                    throw new InvalidOperationException("Missing valid passing evidence for: " + criterion);
            }
            Execute(c, tx, "UPDATE rb_tickets SET status='Done',updated_utc=$now WHERE ticket_id=$id",
                ("$now", nowUtc), ("$id", ticketId));
            TouchProject(c, tx, ticket.ProjectId, nowUtc);
            AppendEvent(c, tx, commandId + ":ticket.completed", "ticket.completed", ticket.ProjectId, ticketId,
                new { ticketId, acceptance = ticket.Acceptance }, nowUtc);
            return GetTicket(c, tx, ticketId);
        });
    }

    public RebirthProject CompleteProject(string commandId, string projectId, long nowUtc)
    {
        var payload = JsonSerializer.Serialize(new { projectId }, Json);
        return Mutate(commandId, payload, (c, tx) =>
        {
            var project = GetProject(c, tx, projectId);
            if (project.Status != RebirthProjectStatus.Active)
                throw new InvalidOperationException("The project is not active.");
            using (var q = c.CreateCommand())
            {
                q.Transaction = tx;
                q.CommandText = "SELECT COUNT(*) FROM rb_tickets WHERE project_id=$project AND status NOT IN ('Done','Cancelled')";
                q.Parameters.AddWithValue("$project", projectId);
                if (Convert.ToInt64(q.ExecuteScalar()) > 0)
                    throw new InvalidOperationException("Every ticket must be done or cancelled before the project can complete.");
            }
            if (CountOpenAttention(c, tx, projectId) > 0)
                throw new InvalidOperationException("Resolve open attention before completing the project.");
            Execute(c, tx, "UPDATE rb_projects SET status='Completed',revision=revision+1,updated_utc=$now WHERE project_id=$id",
                ("$now", nowUtc), ("$id", projectId));
            AppendEvent(c, tx, commandId + ":project.completed", "project.completed", projectId, null,
                new { projectId }, nowUtc);
            return GetProject(c, tx, projectId);
        });
    }

    public RebirthTicket ResumeTicket(string commandId, string ticketId, string actor, long nowUtc)
    {
        var payload = JsonSerializer.Serialize(new { ticketId, actor, operation = "resume" }, Json);
        return Mutate(commandId, payload, (c, tx) =>
        {
            var ticket = GetTicket(c, tx, ticketId);
            if (ticket.Status != RebirthTicketStatus.ReplanRequired)
                throw new InvalidOperationException("Only a ticket waiting for replan can resume.");
            if (IsVetoed(c, tx, ticket))
                throw new InvalidOperationException("Lift the active human veto before resuming this route.");
            Execute(c, tx, "UPDATE rb_tickets SET status='Authorized',updated_utc=$now WHERE ticket_id=$id",
                ("$now", nowUtc), ("$id", ticketId));
            TouchProject(c, tx, ticket.ProjectId, nowUtc);
            AppendEvent(c, tx, commandId + ":ticket.resumed", "ticket.resumed", ticket.ProjectId, ticketId,
                new { ticketId, actor }, nowUtc);
            return GetTicket(c, tx, ticketId);
        });
    }

    public RebirthTicket ReconcileTicket(string commandId, string ticketId, string resolution, long nowUtc)
    {
        var payload = JsonSerializer.Serialize(new { ticketId, resolution, operation = "reconcile" }, Json);
        return Mutate(commandId, payload, (c, tx) =>
        {
            var ticket = GetTicket(c, tx, ticketId);
            if (ticket.Status != RebirthTicketStatus.NeedsReconciliation)
                throw new InvalidOperationException("Only an unknown-outcome ticket can be reconciled.");
            if (IsVetoed(c, tx, ticket))
                throw new InvalidOperationException("Lift the active human veto before reconciling this route.");
            Execute(c, tx, "UPDATE rb_tickets SET status='Authorized',updated_utc=$now WHERE ticket_id=$id",
                ("$now", nowUtc), ("$id", ticketId));
            TouchProject(c, tx, ticket.ProjectId, nowUtc);
            AppendEvent(c, tx, commandId + ":ticket.reconciled", "ticket.reconciled", ticket.ProjectId, ticketId,
                new { ticketId, resolution = Required(resolution, nameof(resolution)) }, nowUtc);
            return GetTicket(c, tx, ticketId);
        });
    }

    public RebirthDecision LiftVeto(string commandId, string projectId, string decisionId, string actor, long nowUtc)
    {
        var payload = JsonSerializer.Serialize(new { projectId, decisionId, actor, operation = "lift" }, Json);
        return Mutate(commandId, payload, (c, tx) =>
        {
            var decision = GetDecision(c, tx, decisionId);
            if (decision.ProjectId != projectId || decision.Kind != RebirthDecisionKind.Veto)
                throw new InvalidOperationException("The decision is not a veto of this project.");
            if (decision.Status != "Active")
                throw new InvalidOperationException("The veto is not active.");
            Execute(c, tx, "UPDATE rb_decisions SET status='Lifted' WHERE decision_id=$id",
                ("$id", decisionId));
            TouchProject(c, tx, projectId, nowUtc);
            AppendEvent(c, tx, commandId + ":veto.lifted", "veto.lifted", projectId, decision.TicketId,
                new { decisionId, actor }, nowUtc);
            return GetDecision(c, tx, decisionId);
        });
    }

    private static IReadOnlyList<RebirthLease> ReadLeases(SqliteConnection c, string projectId, SqliteTransaction? tx = null)
    {
        using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText = """
            SELECT l.lease_id,l.ticket_id,l.owner,l.mode,l.epoch,l.expires_utc,l.status
            FROM rb_leases l JOIN rb_tickets t ON t.ticket_id=l.ticket_id
            WHERE t.project_id=$project ORDER BY l.epoch,l.lease_id
            """;
        q.Parameters.AddWithValue("$project", projectId);
        using var r = q.ExecuteReader();
        var items = new List<RebirthLease>();
        while (r.Read())
            items.Add(new(r.GetString(0), r.GetString(1), r.GetString(2),
                Enum.Parse<RebirthLeaseMode>(r.GetString(3)), r.GetInt64(4), r.GetInt64(5), r.GetString(6)));
        return items;
    }

    /// <summary>Only a UserGate ticket can consume a gate decision. Approval is scoped, not a global write grant.</summary>
    public RebirthGateResult RecordGateDecision(string commandId, string projectId, string ticketId,
        RebirthGateDecision decision, string scope, string text, string actor, long nowUtc)
    {
        var payload = JsonSerializer.Serialize(new { projectId, ticketId, decision, scope, text, actor }, Json);
        return Mutate(commandId, payload, (c, tx) =>
        {
            var ticket = GetTicket(c, tx, ticketId);
            if (ticket.ProjectId != projectId) throw new InvalidOperationException("The ticket belongs to another project.");
            if (ticket.TaskClass != RebirthTaskClass.UserGate)
                throw new InvalidOperationException("Only user_gate tickets take a gate decision.");
            var status = decision switch
            {
                RebirthGateDecision.Approve => RebirthTicketStatus.Authorized,
                RebirthGateDecision.Reject => RebirthTicketStatus.Cancelled,
                _ => RebirthTicketStatus.WaitingPerson
            };
            var id = "gate-" + Guid.NewGuid().ToString("N")[..12];
            Execute(c, tx, """
                INSERT INTO rb_decisions(decision_id,project_id,ticket_id,kind,actor,target,scope,text,created_utc,status)
                VALUES($id,$project,$ticket,'Gate',$actor,$target,$scope,$text,$now,$status)
                """, ("$id", id), ("$project", projectId), ("$ticket", ticketId),
                ("$actor", Required(actor, nameof(actor))), ("$target", "ticket:" + ticketId),
                ("$scope", Required(scope, nameof(scope))), ("$text", Required(text, nameof(text))),
                ("$now", nowUtc), ("$status", decision.ToString()));
            Execute(c, tx, "UPDATE rb_tickets SET status=$status,updated_utc=$now WHERE ticket_id=$id",
                ("$status", status.ToString()), ("$now", nowUtc), ("$id", ticketId));
            TouchProject(c, tx, projectId, nowUtc);
            AppendEvent(c, tx, commandId + ":gate.recorded", "gate.recorded", projectId, ticketId,
                new { gateId = id, decision = decision.ToString(), scope, text, status = status.ToString() }, nowUtc);
            return new RebirthGateResult(new RebirthDecision(id, projectId, ticketId, RebirthDecisionKind.Gate,
                actor, "ticket:" + ticketId, scope, text, nowUtc, decision.ToString()), status);
        });
    }

    public RebirthVetoResult RecordVeto(string commandId, string projectId, string? ticketId,
        string target, string scope, string text, string actor, long nowUtc)
    {
        var payload = JsonSerializer.Serialize(new { projectId, ticketId, target, scope, text, actor }, Json);
        return Mutate(commandId, payload, (c, tx) =>
        {
            GetProject(c, tx, projectId);
            return InsertVeto(c, tx, commandId, projectId, ticketId, target, scope, text, actor, nowUtc);
        });
    }

    private static RebirthVetoResult InsertVeto(SqliteConnection c, SqliteTransaction tx, string commandId,
        string projectId, string? ticketId, string target, string scope, string text, string actor, long nowUtc)
    {
        var normalizedTarget = !string.IsNullOrWhiteSpace(target) ? target.Trim()
            : ticketId is not null ? "ticket:" + ticketId : "project:" + projectId;
        var id = "veto-" + Guid.NewGuid().ToString("N")[..12];
        Execute(c, tx, """
            INSERT INTO rb_decisions(decision_id,project_id,ticket_id,kind,actor,target,scope,text,created_utc,status)
            VALUES($id,$project,$ticket,'Veto',$actor,$target,$scope,$text,$now,'Active')
            """, ("$id", id), ("$project", projectId), ("$ticket", (object?)ticketId ?? DBNull.Value),
            ("$actor", Required(actor, nameof(actor))), ("$target", normalizedTarget),
            ("$scope", Required(scope, nameof(scope))), ("$text", Required(text, nameof(text))), ("$now", nowUtc));
        var replanned = 0;
        using (var q = c.CreateCommand())
        {
            q.Transaction = tx;
            q.CommandText = "SELECT ticket_id,route,status FROM rb_tickets WHERE project_id=$project";
            q.Parameters.AddWithValue("$project", projectId);
            using var r = q.ExecuteReader();
            var targets = new List<string>();
            while (r.Read())
            {
                var status = Enum.Parse<RebirthTicketStatus>(r.GetString(2));
                var idValue = r.GetString(0);
                var route = r.GetString(1);
                var matches = normalizedTarget == "project:" + projectId ||
                              ticketId == idValue || normalizedTarget == "ticket:" + idValue ||
                              normalizedTarget == "route:" + route;
                if (matches && status is not (RebirthTicketStatus.Done or RebirthTicketStatus.Cancelled))
                    targets.Add(idValue);
            }
            foreach (var targetId in targets)
            {
                Execute(c, tx, "UPDATE rb_tickets SET status='ReplanRequired',updated_utc=$now WHERE ticket_id=$id",
                    ("$now", nowUtc), ("$id", targetId));
                replanned++;
            }
        }
        TouchProject(c, tx, projectId, nowUtc);
        AppendEvent(c, tx, commandId + ":veto.recorded", "veto.recorded", projectId, ticketId,
            new { decisionId = id, target = normalizedTarget, scope, text, replanned }, nowUtc);
        return new RebirthVetoResult(new RebirthDecision(id, projectId, ticketId, RebirthDecisionKind.Veto,
            actor, normalizedTarget, scope, text, nowUtc, "Active"), replanned);
    }

    internal RebirthDecision RequestStop(string commandId, string ticketId, string reason, long nowUtc, string actor = "human") =>
        Mutate(commandId, JsonSerializer.Serialize(new { ticketId, reason, actor, request = true }, Json), (c, tx) =>
        {
            var ticket = GetTicket(c, tx, ticketId);
            var id = "stop-" + Sha(commandId)[..24];
            Execute(c, tx, """
                INSERT INTO rb_decisions(decision_id,project_id,ticket_id,kind,actor,target,scope,text,created_utc,status)
                VALUES($id,$project,$ticket,'Stop',$actor,$target,'ticket',$text,$now,'Requested')
                """, ("$id", id), ("$project", ticket.ProjectId), ("$ticket", ticketId),
                ("$actor", actor), ("$target", "ticket:" + ticketId), ("$text", Required(reason, nameof(reason))), ("$now", nowUtc));
            TouchProject(c, tx, ticket.ProjectId, nowUtc);
            AppendEvent(c, tx, commandId + ":run.stop_requested", "run.stop_requested", ticket.ProjectId, ticketId,
                new { ticketId, reason }, nowUtc);
            return GetDecision(c, tx, id);
        });

    public RebirthTicket StopTicket(string commandId, string ticketId, string reason, string actor, long nowUtc)
    {
        var payload = JsonSerializer.Serialize(new { ticketId, reason, actor }, Json);
        return Mutate(commandId, payload, (c, tx) =>
        {
            var ticket = GetTicket(c, tx, ticketId);
            if (ticket.Status is RebirthTicketStatus.Done or RebirthTicketStatus.Cancelled)
                throw new InvalidOperationException("The ticket already ended.");
            Execute(c, tx, "UPDATE rb_tickets SET status='Cancelled',updated_utc=$now WHERE ticket_id=$id",
                ("$now", nowUtc), ("$id", ticketId));
            Execute(c, tx, """
                UPDATE rb_runs SET status='Interrupted',ended_utc=$now,terminal_code='human-stop',detail=$reason
                WHERE ticket_id=$ticket AND status IN ('Queued','Running')
                """, ("$now", nowUtc), ("$reason", Required(reason, nameof(reason))), ("$ticket", ticketId));
            TouchProject(c, tx, ticket.ProjectId, nowUtc);
            AppendEvent(c, tx, commandId + ":ticket.stopped", "ticket.stopped", ticket.ProjectId, ticketId,
                new { ticketId, reason, actor }, nowUtc);
            return GetTicket(c, tx, ticketId);
        });
    }

    public RebirthTicket TakeoverTicket(string commandId, string ticketId, string actor, long nowUtc)
    {
        var payload = JsonSerializer.Serialize(new { ticketId, actor }, Json);
        return Mutate(commandId, payload, (c, tx) =>
        {
            var ticket = GetTicket(c, tx, ticketId);
            if (ticket.Status is RebirthTicketStatus.Done or RebirthTicketStatus.Cancelled)
                throw new InvalidOperationException("The ticket already ended.");
            Execute(c, tx, "UPDATE rb_tickets SET status='HumanOwned',owner=$actor,updated_utc=$now WHERE ticket_id=$id",
                ("$actor", Required(actor, nameof(actor))), ("$now", nowUtc), ("$id", ticketId));
            Execute(c, tx, """
                UPDATE rb_runs SET status='Interrupted',ended_utc=$now,terminal_code='human-takeover'
                WHERE ticket_id=$ticket AND status IN ('Queued','Running')
                """, ("$now", nowUtc), ("$ticket", ticketId));
            TouchProject(c, tx, ticket.ProjectId, nowUtc);
            AppendEvent(c, tx, commandId + ":ticket.takeover", "ticket.takeover", ticket.ProjectId, ticketId,
                new { ticketId, actor }, nowUtc);
            return GetTicket(c, tx, ticketId);
        });
    }

    public RebirthInput RecordHumanInput(string commandId, string projectId, string? ticketId,
        string actor, RebirthInputKind kind, string text, long nowUtc, string? target = null,
        string? scope = null, string? inputId = null)
    {
        var id = string.IsNullOrWhiteSpace(inputId) ? "input-" + Guid.NewGuid().ToString("N")[..12] : inputId!;
        var payload = JsonSerializer.Serialize(new { projectId, ticketId, actor, kind, text, target, scope, inputId = id }, Json);
        return Mutate(commandId, payload, (c, tx) =>
        {
            GetProject(c, tx, projectId);
            var state = RebirthInputState.Delivered;
            long? applied = null;
            if (kind == RebirthInputKind.Veto)
            {
                InsertVeto(c, tx, commandId + ":input-veto", projectId, ticketId, target ?? "", scope ?? "human input",
                    text, actor, nowUtc);
                state = RebirthInputState.Applied; applied = nowUtc;
            }
            else if (kind == RebirthInputKind.Stop)
            {
                if (ticketId is null) throw new ArgumentException("Stop needs a ticket target.", nameof(ticketId));
                StopTicketInternal(c, tx, ticketId, text, actor, nowUtc, commandId + ":input-stop");
                state = RebirthInputState.Applied; applied = nowUtc;
            }
            else if (kind == RebirthInputKind.Takeover)
            {
                if (ticketId is null) throw new ArgumentException("Takeover needs a ticket target.", nameof(ticketId));
                TakeoverTicketInternal(c, tx, ticketId, actor, nowUtc, commandId + ":input-takeover");
                state = RebirthInputState.Applied; applied = nowUtc;
            }
            Execute(c, tx, """
                INSERT INTO rb_inputs(input_id,project_id,ticket_id,actor,kind,text,state,created_utc,delivered_utc,read_utc,applied_utc)
                VALUES($id,$project,$ticket,$actor,$kind,$text,$state,$now,$delivered,$read,$applied)
                """, ("$id", id), ("$project", projectId), ("$ticket", (object?)ticketId ?? DBNull.Value),
                ("$actor", Required(actor, nameof(actor))), ("$kind", kind.ToString()),
                ("$text", string.IsNullOrWhiteSpace(text) ? throw new ArgumentException("Input text is required.", nameof(text)) : text), ("$state", state.ToString()), ("$now", nowUtc),
                ("$delivered", ticketId is null ? DBNull.Value : nowUtc), ("$read", DBNull.Value),
                ("$applied", (object?)applied ?? DBNull.Value));
            TouchProject(c, tx, projectId, nowUtc);
            AppendEvent(c, tx, commandId + ":input.recorded", "input.recorded", projectId, ticketId,
                new { inputId = id, kind = kind.ToString(), state = state.ToString(), target }, nowUtc);
            return GetInput(c, tx, id);
        });
    }

    private static RebirthInput GetInput(SqliteConnection c, SqliteTransaction tx, string id)
    {
        using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText = "SELECT input_id,project_id,ticket_id,actor,kind,text,state,created_utc,delivered_utc,read_utc,applied_utc FROM rb_inputs WHERE input_id=$id";
        q.Parameters.AddWithValue("$id", id);
        using var r = q.ExecuteReader();
        if (!r.Read()) throw new KeyNotFoundException("Unknown input: " + id);
        return new RebirthInput(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2),
            r.GetString(3), Enum.Parse<RebirthInputKind>(r.GetString(4)), r.GetString(5),
            Enum.Parse<RebirthInputState>(r.GetString(6)), r.GetInt64(7),
            r.IsDBNull(8) ? null : r.GetInt64(8), r.IsDBNull(9) ? null : r.GetInt64(9),
            r.IsDBNull(10) ? null : r.GetInt64(10));
    }

    private static void StopTicketInternal(SqliteConnection c, SqliteTransaction tx, string ticketId,
        string reason, string actor, long nowUtc, string eventId)
    {
        var ticket = GetTicket(c, tx, ticketId);
        if (ticket.Status is RebirthTicketStatus.Done or RebirthTicketStatus.Cancelled)
            throw new InvalidOperationException("The ticket already ended.");
        Execute(c, tx, "UPDATE rb_tickets SET status='Cancelled',updated_utc=$now WHERE ticket_id=$id",
            ("$now", nowUtc), ("$id", ticketId));
        Execute(c, tx, "UPDATE rb_runs SET status='Interrupted',ended_utc=$now,terminal_code='human-stop',detail=$reason WHERE ticket_id=$ticket AND status IN ('Queued','Running')",
            ("$now", nowUtc), ("$reason", Required(reason, "reason")), ("$ticket", ticketId));
        ReleaseActiveLease(c, tx, ticketId);
        TouchProject(c, tx, ticket.ProjectId, nowUtc);
        AppendEvent(c, tx, eventId, "ticket.stopped", ticket.ProjectId, ticketId,
            new { ticketId, reason, actor }, nowUtc);
    }

    private static void TakeoverTicketInternal(SqliteConnection c, SqliteTransaction tx, string ticketId,
        string actor, long nowUtc, string eventId)
    {
        var ticket = GetTicket(c, tx, ticketId);
        if (ticket.Status is RebirthTicketStatus.Done or RebirthTicketStatus.Cancelled)
            throw new InvalidOperationException("The ticket already ended.");
        Execute(c, tx, "UPDATE rb_tickets SET status='HumanOwned',owner=$actor,updated_utc=$now WHERE ticket_id=$id",
            ("$actor", Required(actor, "actor")), ("$now", nowUtc), ("$id", ticketId));
        Execute(c, tx, "UPDATE rb_runs SET status='Interrupted',ended_utc=$now,terminal_code='human-takeover' WHERE ticket_id=$ticket AND status IN ('Queued','Running')",
            ("$now", nowUtc), ("$ticket", ticketId));
        ReleaseActiveLease(c, tx, ticketId);
        TouchProject(c, tx, ticket.ProjectId, nowUtc);
        AppendEvent(c, tx, eventId, "ticket.takeover", ticket.ProjectId, ticketId, new { ticketId, actor }, nowUtc);
    }

    public RebirthInput MarkInputRead(string commandId, string inputId, long nowUtc)
    {
        var payload = JsonSerializer.Serialize(new { inputId }, Json);
        return Mutate(commandId, payload, (c, tx) =>
        {
            var input = GetInput(c, tx, inputId);
            if (input.State is RebirthInputState.Applied or RebirthInputState.Failed)
                throw new InvalidOperationException("The input is already terminal.");
            Execute(c, tx, "UPDATE rb_inputs SET state='Read',read_utc=$now WHERE input_id=$id",
                ("$now", nowUtc), ("$id", inputId));
            TouchProject(c, tx, input.ProjectId, nowUtc);
            AppendEvent(c, tx, commandId + ":input.read", "input.read", input.ProjectId, input.TicketId,
                new { inputId }, nowUtc);
            return GetInput(c, tx, inputId);
        });
    }

    public RebirthInput ApplyInput(string commandId, string inputId, long nowUtc)
    {
        var payload = JsonSerializer.Serialize(new { inputId }, Json);
        return Mutate(commandId, payload, (c, tx) =>
        {
            var input = GetInput(c, tx, inputId);
            if (input.State is RebirthInputState.Applied or RebirthInputState.Failed)
                throw new InvalidOperationException("The input is already terminal.");
            Execute(c, tx, "UPDATE rb_inputs SET state='Applied',applied_utc=$now WHERE input_id=$id",
                ("$now", nowUtc), ("$id", inputId));
            TouchProject(c, tx, input.ProjectId, nowUtc);
            AppendEvent(c, tx, commandId + ":input.applied", "input.applied", input.ProjectId, input.TicketId,
                new { inputId }, nowUtc);
            return GetInput(c, tx, inputId);
        });
    }

    public RebirthAttention CreateAttention(string commandId, string ticketId, string question, long nowUtc,
        string? attentionId = null)
    {
        var id = string.IsNullOrWhiteSpace(attentionId) ? "attention-" + Guid.NewGuid().ToString("N")[..12] : attentionId!;
        var payload = JsonSerializer.Serialize(new { ticketId, question, attentionId = id }, Json);
        return Mutate(commandId, payload, (c, tx) =>
        {
            var ticket = GetTicket(c, tx, ticketId);
            if (ticket.Status is RebirthTicketStatus.Done or RebirthTicketStatus.Cancelled or RebirthTicketStatus.HumanOwned)
                throw new InvalidOperationException("The ticket cannot ask for attention in state " + ticket.Status + ".");
            Execute(c, tx, """
                INSERT INTO rb_attention(attention_id,project_id,ticket_id,question,status,created_utc)
                VALUES($id,$project,$ticket,$question,'Open',$now)
                """, ("$id", id), ("$project", ticket.ProjectId), ("$ticket", ticketId),
                ("$question", Required(question, nameof(question))), ("$now", nowUtc));
            Execute(c, tx, "UPDATE rb_tickets SET status='WaitingPerson',updated_utc=$now WHERE ticket_id=$id",
                ("$now", nowUtc), ("$id", ticketId));
            TouchProject(c, tx, ticket.ProjectId, nowUtc);
            AppendEvent(c, tx, commandId + ":attention.opened", "attention.opened", ticket.ProjectId, ticketId,
                new { attentionId = id, question }, nowUtc);
            return GetAttention(c, tx, id);
        });
    }

    public RebirthAttention AnswerAttention(string commandId, string attentionId, string answer, long nowUtc)
    {
        var payload = JsonSerializer.Serialize(new { attentionId, answer }, Json);
        return Mutate(commandId, payload, (c, tx) =>
        {
            var attention = GetAttention(c, tx, attentionId);
            if (attention.Status != RebirthAttentionStatus.Open)
                throw new InvalidOperationException("The attention is not open.");
            Execute(c, tx, "UPDATE rb_attention SET status='Answered',answered_utc=$now,answer=$answer WHERE attention_id=$id",
                ("$now", nowUtc), ("$answer", Required(answer, nameof(answer))), ("$id", attentionId));
            if (attention.TicketId is { } ticketId)
            {
                var ticket = GetTicket(c, tx, ticketId);
                if (ticket.Status == RebirthTicketStatus.WaitingPerson)
                    Execute(c, tx, "UPDATE rb_tickets SET status='Active',updated_utc=$now WHERE ticket_id=$id",
                        ("$now", nowUtc), ("$id", ticketId));
            }
            TouchProject(c, tx, attention.ProjectId, nowUtc);
            AppendEvent(c, tx, commandId + ":attention.answered", "attention.answered", attention.ProjectId,
                attention.TicketId, new { attentionId, answer }, nowUtc);
            return GetAttention(c, tx, attentionId);
        });
    }

    public RebirthSnapshot ReadSnapshot(string projectId)
    {
        if (_scopeConnection is not null) return ReadSnapshot(_scopeConnection, _scopeTransaction, projectId);
        using var c = Open();
        using var tx = c.BeginTransaction(deferred: true);
        return ReadSnapshot(c, tx, projectId);
    }

    private static RebirthSnapshot ReadSnapshot(SqliteConnection c, SqliteTransaction? tx, string projectId)
    {
        var project = GetProject(c, tx, projectId);
        var brief = GetBrief(c, tx, projectId, project.BriefVersion);
        return new RebirthSnapshot(project, brief, ReadTickets(c, projectId, tx), ReadRuns(c, projectId, tx),
            ReadEvidence(c, projectId, tx), ReadDecisions(c, projectId, tx), ReadInputs(c, projectId, tx), ReadAttention(c, projectId, tx))
        {
            Quota = GetQuota(c, tx, projectId),
            Leases = ReadLeases(c, projectId, tx)
        };
    }

    private static IReadOnlyList<RebirthTicket> ReadTickets(SqliteConnection c, string projectId, SqliteTransaction? tx = null)
    {
        var ids = new List<string>();
        using (var q = c.CreateCommand())
        {
            q.Transaction = tx;
            q.CommandText = "SELECT ticket_id FROM rb_tickets WHERE project_id=$project ORDER BY created_utc,ticket_id";
            q.Parameters.AddWithValue("$project", projectId);
            using var r = q.ExecuteReader();
            while (r.Read()) ids.Add(r.GetString(0));
        }
        return ids.Select(id => GetTicket(c, tx, id)).ToArray();
    }

    private static IReadOnlyList<RebirthRun> ReadRuns(SqliteConnection c, string projectId, SqliteTransaction? tx = null)
    {
        using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText = """
            SELECT r.run_id,r.ticket_id,r.executor,r.model,r.workspace,r.status,r.started_utc,r.ended_utc,r.terminal_code,r.detail,r.result_kind,r.settled
            FROM rb_runs r JOIN rb_tickets t ON t.ticket_id=r.ticket_id
            WHERE t.project_id=$project ORDER BY r.started_utc,r.run_id
            """;
        q.Parameters.AddWithValue("$project", projectId);
        using var r = q.ExecuteReader();
        var items = new List<RebirthRun>();
        while (r.Read())
            items.Add(new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
                Enum.Parse<RebirthRunStatus>(r.GetString(5)), r.GetInt64(6), r.IsDBNull(7) ? null : r.GetInt64(7),
                r.IsDBNull(8) ? null : r.GetString(8), r.IsDBNull(9) ? null : r.GetString(9))
            { ResultKind = r.IsDBNull(10) ? null : Enum.Parse<RebirthTurnResultKind>(r.GetString(10)), Settled = r.GetInt32(11) != 0 });
        return items;
    }

    private static IReadOnlyList<RebirthEvidence> ReadEvidence(SqliteConnection c, string projectId, SqliteTransaction? tx = null)
    {
        using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText = """
            SELECT e.evidence_id,e.ticket_id,e.run_id,e.kind,e.criterion,e.reference,e.passed,e.verifier,e.created_utc,e.valid,e.acceptance_digest
            FROM rb_evidence e JOIN rb_tickets t ON t.ticket_id=e.ticket_id
            WHERE t.project_id=$project ORDER BY e.created_utc,e.evidence_id
            """;
        q.Parameters.AddWithValue("$project", projectId);
        using var r = q.ExecuteReader();
        var items = new List<RebirthEvidence>();
        while (r.Read())
            items.Add(new(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2),
                Enum.Parse<RebirthEvidenceKind>(r.GetString(3)), r.GetString(4), r.GetString(5),
                r.GetInt32(6) != 0, r.GetString(7), r.GetInt64(8), r.GetInt32(9) != 0) { AcceptanceDigest = r.GetString(10) });
        return items;
    }

    private static IReadOnlyList<RebirthDecision> ReadDecisions(SqliteConnection c, string projectId, SqliteTransaction? tx = null)
    {
        using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText = "SELECT decision_id,project_id,ticket_id,kind,actor,target,scope,text,created_utc,status FROM rb_decisions WHERE project_id=$project ORDER BY created_utc,decision_id";
        q.Parameters.AddWithValue("$project", projectId);
        using var r = q.ExecuteReader();
        var items = new List<RebirthDecision>();
        while (r.Read())
            items.Add(new(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2),
                Enum.Parse<RebirthDecisionKind>(r.GetString(3)), r.GetString(4), r.GetString(5),
                r.GetString(6), r.GetString(7), r.GetInt64(8), r.GetString(9)));
        return items;
    }

    private static IReadOnlyList<RebirthInput> ReadInputs(SqliteConnection c, string projectId, SqliteTransaction? tx = null)
    {
        using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText = "SELECT input_id,project_id,ticket_id,actor,kind,text,state,created_utc,delivered_utc,read_utc,applied_utc FROM rb_inputs WHERE project_id=$project ORDER BY created_utc,input_id";
        q.Parameters.AddWithValue("$project", projectId);
        using var r = q.ExecuteReader();
        var items = new List<RebirthInput>();
        while (r.Read())
            items.Add(new(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2),
                r.GetString(3), Enum.Parse<RebirthInputKind>(r.GetString(4)), r.GetString(5),
                Enum.Parse<RebirthInputState>(r.GetString(6)), r.GetInt64(7),
                r.IsDBNull(8) ? null : r.GetInt64(8), r.IsDBNull(9) ? null : r.GetInt64(9),
                r.IsDBNull(10) ? null : r.GetInt64(10)));
        return items;
    }

    private static IReadOnlyList<RebirthAttention> ReadAttention(SqliteConnection c, string projectId, SqliteTransaction? tx = null)
    {
        using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText = "SELECT attention_id,project_id,ticket_id,question,status,created_utc,answered_utc,answer FROM rb_attention WHERE project_id=$project ORDER BY created_utc,attention_id";
        q.Parameters.AddWithValue("$project", projectId);
        using var r = q.ExecuteReader();
        var items = new List<RebirthAttention>();
        while (r.Read())
            items.Add(new(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2),
                r.GetString(3), Enum.Parse<RebirthAttentionStatus>(r.GetString(4)), r.GetInt64(5),
                r.IsDBNull(6) ? null : r.GetInt64(6), r.IsDBNull(7) ? null : r.GetString(7)));
        return items;
    }

    public void Dispose() { }
}
