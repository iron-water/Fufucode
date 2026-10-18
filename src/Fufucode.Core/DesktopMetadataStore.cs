using Microsoft.Data.Sqlite;
using System.Globalization;

namespace Fufucode.Core;

public sealed record ProjectRecord(string Id, string Path);
public sealed record SessionRecord(string Id, string ProjectId, string? FilePath, string? Name,
    bool WasActive, DateTimeOffset LastOpened, string? ParentSessionId = null, string? ForkEntryId = null,
    DateTimeOffset? ForkedAt = null, BackendId Backend = BackendId.Pi, string? BackendSessionId = null)
{
    /// <summary>True when this row was created by forking another managed session.</summary>
    public bool IsFork => ParentSessionId is not null;
}

/// <summary>Only desktop metadata lives here. Pi JSONL remains the sole conversation history.</summary>
public sealed class DesktopMetadataStore
{
    public const int SchemaVersion = 4;
    /// <summary>Read-only reports tolerate every schema that still has the session/project tables.</summary>
    public const int MinimumReadOnlySchemaVersion = 2;
    private readonly string _path;

    public DesktopMetadataStore(AppDataLayout layout)
    {
        layout.EnsureCreated();
        _path = layout.DatabasePath;
        Migrate();
    }

    public string DatabasePath => _path;

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false,
            DefaultTimeout = 10
        }.ToString());
        connection.Open();
        using var foreignKeys = connection.CreateCommand();
        foreignKeys.CommandText = "PRAGMA foreign_keys=ON";
        foreignKeys.ExecuteNonQuery();
        return connection;
    }

    private void Migrate()
    {
        var existed = File.Exists(_path) && new FileInfo(_path).Length > 0;
        using var connection = Open();
        using var versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version";
        var version = Convert.ToInt32(versionCommand.ExecuteScalar());
        if (version > SchemaVersion) throw new InvalidOperationException($"Metadata schema {version} is newer than this app.");
        if (version == SchemaVersion) return;
        if (existed)
        {
            var backup = _path + ".before-v" + SchemaVersion + "-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff") + ".bak";
            using var destination = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = backup, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
            destination.Open();
            connection.BackupDatabase(destination);
        }
        using var tx = connection.BeginTransaction();
        if (version < 1)
        {
            Execute(connection, tx, """
                CREATE TABLE projects (
                  id TEXT PRIMARY KEY, path TEXT NOT NULL, path_key TEXT NOT NULL UNIQUE
                );
                CREATE TABLE sessions (
                  id TEXT PRIMARY KEY, project_id TEXT NOT NULL REFERENCES projects(id),
                  file_path TEXT UNIQUE, name TEXT, was_active INTEGER NOT NULL DEFAULT 0
                );
                CREATE TABLE drafts (
                  session_id TEXT PRIMARY KEY REFERENCES sessions(id) ON DELETE CASCADE,
                  text TEXT NOT NULL
                );
                CREATE TABLE preferences (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                """);
        }
        // A column may already exist while user_version still names an older schema, so every
        // step is applied only when it is actually missing.
        if (version < 2)
            AddColumn(connection, tx, "sessions", "last_opened_utc",
                "ALTER TABLE sessions ADD COLUMN last_opened_utc TEXT NOT NULL DEFAULT '1970-01-01T00:00:00+00:00';");
        if (version < 3)
        {
            // Fork lineage: a forked session keeps the parent row and records the entry it branched from.
            // No REFERENCES clause is added; validity is enforced in code so the migration stays portable.
            AddColumn(connection, tx, "sessions", "parent_session_id",
                "ALTER TABLE sessions ADD COLUMN parent_session_id TEXT;");
            AddColumn(connection, tx, "sessions", "fork_entry_id",
                "ALTER TABLE sessions ADD COLUMN fork_entry_id TEXT;");
            AddColumn(connection, tx, "sessions", "forked_at_utc",
                "ALTER TABLE sessions ADD COLUMN forked_at_utc TEXT;");
        }
        if (version < 4)
        {
            AddColumn(connection, tx, "sessions", "backend_id", "ALTER TABLE sessions ADD COLUMN backend_id TEXT NOT NULL DEFAULT 'pi';");
            AddColumn(connection, tx, "sessions", "backend_session_id", "ALTER TABLE sessions ADD COLUMN backend_session_id TEXT;");
        }
        Execute(connection, tx, $"PRAGMA user_version={SchemaVersion}");
        tx.Commit();
    }

    private static void AddColumn(SqliteConnection connection, SqliteTransaction tx, string table,
        string column, string sql)
    {
        using var probe = connection.CreateCommand();
        probe.Transaction = tx;
        probe.CommandText = $"PRAGMA table_info({table})";
        using var reader = probe.ExecuteReader();
        while (reader.Read())
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return;
        Execute(connection, tx, sql);
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction? tx, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public ProjectRecord GetOrCreateProject(string projectPath)
    {
        var full = AppDataLayout.NormalizeProjectPath(projectPath);
        var key = AppDataLayout.PathKey(full);
        using var connection = Open();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "INSERT OR IGNORE INTO projects(id,path,path_key) VALUES($id,$path,$key)";
            command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
            command.Parameters.AddWithValue("$path", full);
            command.Parameters.AddWithValue("$key", key);
            command.ExecuteNonQuery();
        }
        using var select = connection.CreateCommand();
        select.CommandText = "SELECT id,path FROM projects WHERE path_key=$key";
        select.Parameters.AddWithValue("$key", key);
        using var reader = select.ExecuteReader();
        if (!reader.Read()) throw new InvalidOperationException("Project insertion failed.");
        return new ProjectRecord(reader.GetString(0), reader.GetString(1));
    }

    /// <summary>Registers the child of a fork. The parent row is kept so both branches stay reachable.</summary>
    public SessionRecord CreateForkSession(ProjectRecord project, string parentSessionId, string entryId, string? piSessionId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentSessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(entryId);
        var id = piSessionId ?? Guid.NewGuid().ToString("N");
        var opened = DateTimeOffset.UtcNow;
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sessions(id,project_id,name,last_opened_utc,parent_session_id,fork_entry_id,forked_at_utc,backend_id)
            SELECT $id,$project,s.name,$opened,$parent,$entry,$opened,s.backend_id FROM sessions AS s WHERE s.id=$parent
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$project", project.Id);
        command.Parameters.AddWithValue("$opened", opened.ToString("O"));
        command.Parameters.AddWithValue("$parent", parentSessionId);
        command.Parameters.AddWithValue("$entry", entryId);
        if (command.ExecuteNonQuery() != 1) throw new KeyNotFoundException($"Unknown parent session {parentSessionId}.");
        return new SessionRecord(id, project.Id, null, GetSession(parentSessionId).Name, false, opened,
            parentSessionId, entryId, opened, GetSession(parentSessionId).Backend);
    }

    /// <summary>Read-only project lookup. Listing sessions must never register a project as a side effect.</summary>
    public ProjectRecord? TryGetProject(string projectPath)
    {
        var full = AppDataLayout.NormalizeProjectPath(projectPath);
        using var connection = Open();
        using var select = connection.CreateCommand();
        select.CommandText = "SELECT id,path FROM projects WHERE path_key=$key";
        select.Parameters.AddWithValue("$key", AppDataLayout.PathKey(full));
        using var reader = select.ExecuteReader();
        return reader.Read() ? new ProjectRecord(reader.GetString(0), reader.GetString(1)) : null;
    }

    public SessionRecord CreateSession(ProjectRecord project, string? name = null, BackendId backend = BackendId.Pi)
    {
        var id = Guid.NewGuid().ToString("N");
        var opened = DateTimeOffset.UtcNow;
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO sessions(id,project_id,name,last_opened_utc,backend_id) VALUES($id,$project,$name,$opened,$backend)";
        command.Parameters.AddWithValue("$backend", BackendCapabilities.StorageId(backend));
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$project", project.Id);
        command.Parameters.AddWithValue("$name", (object?)name ?? DBNull.Value);
        command.Parameters.AddWithValue("$opened", opened.ToString("O"));
        command.ExecuteNonQuery();
        return new SessionRecord(id, project.Id, null, name, false, opened, Backend: backend);
    }

    public ProjectRecord GetProject(string projectId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,path FROM projects WHERE id=$id";
        command.Parameters.AddWithValue("$id", projectId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new KeyNotFoundException($"Unknown project {projectId}.");
        return new ProjectRecord(reader.GetString(0), reader.GetString(1));
    }

    public IReadOnlyList<ProjectRecord> ListProjects()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,path FROM projects ORDER BY path COLLATE NOCASE";
        using var reader = command.ExecuteReader();
        var projects = new List<ProjectRecord>();
        while (reader.Read()) projects.Add(new(reader.GetString(0), reader.GetString(1)));
        return projects;
    }

    public SessionRecord GetSession(string sessionId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = SelectSessions + " WHERE id=$id";
        command.Parameters.AddWithValue("$id", sessionId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new KeyNotFoundException($"Unknown session {sessionId}.");
        return ReadSession(reader);
    }

    public IReadOnlyList<SessionRecord> ListSessions(string projectId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = SelectSessions + " WHERE project_id=$project ORDER BY last_opened_utc DESC";
        command.Parameters.AddWithValue("$project", projectId);
        using var reader = command.ExecuteReader();
        var sessions = new List<SessionRecord>();
        while (reader.Read()) sessions.Add(ReadSession(reader));
        return sessions;
    }

    private const string SelectSessions =
        "SELECT id,project_id,file_path,name,was_active,last_opened_utc,parent_session_id,fork_entry_id,forked_at_utc,backend_id,backend_session_id FROM sessions";

    private static SessionRecord ReadSession(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetInt32(4) != 0,
        DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind),
        reader.IsDBNull(6) ? null : reader.GetString(6),
        reader.IsDBNull(7) ? null : reader.GetString(7),
        reader.IsDBNull(8) ? null : DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind), BackendCapabilities.Parse(reader.GetString(9)),
        reader.IsDBNull(10) ? null : reader.GetString(10));

    public void BindBackendSession(string sessionId, string backendSessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backendSessionId);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE sessions SET backend_session_id=$backend WHERE id=$id AND (backend_session_id IS NULL OR backend_session_id=$backend)";
        command.Parameters.AddWithValue("$backend", backendSessionId);
        command.Parameters.AddWithValue("$id", sessionId);
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("Backend session identity cannot change.");
    }

    public void UpdateSessionFile(string sessionId, string path)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE sessions SET file_path=$path,last_opened_utc=$opened WHERE id=$id";
        command.Parameters.AddWithValue("$path", Path.GetFullPath(path));
        command.Parameters.AddWithValue("$opened", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", sessionId);
        if (command.ExecuteNonQuery() != 1) throw new KeyNotFoundException(sessionId);
    }

    public void SetSessionActive(string sessionId, bool active)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE sessions SET was_active=$active WHERE id=$id";
        command.Parameters.AddWithValue("$active", active ? 1 : 0);
        command.Parameters.AddWithValue("$id", sessionId);
        if (command.ExecuteNonQuery() != 1) throw new KeyNotFoundException(sessionId);
    }

    public void SetSessionName(string sessionId, string name)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE sessions SET name=$name WHERE id=$id";
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$id", sessionId);
        if (command.ExecuteNonQuery() != 1) throw new KeyNotFoundException(sessionId);
    }

    // Only SessionDeletion may remove metadata; it holds the session lease and validates its file first.
    internal void DeleteSession(SessionRecord expected)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM sessions WHERE id=$id AND project_id=$project AND file_path IS $path";
        command.Parameters.AddWithValue("$id", expected.Id);
        command.Parameters.AddWithValue("$project", expected.ProjectId);
        command.Parameters.AddWithValue("$path", (object?)expected.FilePath ?? DBNull.Value);
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("Session metadata changed during deletion.");
    }

    public void SetDraft(string sessionId, string text)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO drafts(session_id,text) VALUES($id,$text) ON CONFLICT(session_id) DO UPDATE SET text=excluded.text";
        command.Parameters.AddWithValue("$id", sessionId);
        command.Parameters.AddWithValue("$text", text);
        command.ExecuteNonQuery();
    }

    public string? GetDraft(string sessionId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT text FROM drafts WHERE session_id=$id";
        command.Parameters.AddWithValue("$id", sessionId);
        return command.ExecuteScalar() as string;
    }

    public void SetPreference(string key, string value)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO preferences(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    public string? GetPreference(string key)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM preferences WHERE key=$key";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }
}
