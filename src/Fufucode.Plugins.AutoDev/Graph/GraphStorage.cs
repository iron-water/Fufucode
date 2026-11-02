using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace Fufucode.Plugins.AutoDev.Graph;

internal static class GraphStorage
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    internal static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);
    internal static T Deserialize<T>(string value) => JsonSerializer.Deserialize<T>(value, Json)
        ?? throw new InvalidDataException("Unreadable Graph data: " + typeof(T).Name);
    internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    internal static string Id(string kind) => kind + "-" + Guid.NewGuid().ToString("N");
    internal static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    internal static string Canonical(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    internal static string PathKey(string path) => OperatingSystem.IsWindows() ? Canonical(path).ToUpperInvariant() : Canonical(path);

    internal static SqliteConnection Open(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var c = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Pooling = false, DefaultTimeout = 15
        }.ToString());
        c.Open();
        Execute(c, null, "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=15000;");
        return c;
    }

    internal static int Execute(SqliteConnection c, SqliteTransaction? tx, string sql, params (string, object?)[] parameters)
    {
        using var q = Command(c, tx, sql, parameters);
        return q.ExecuteNonQuery();
    }

    internal static object? Scalar(SqliteConnection c, SqliteTransaction? tx, string sql, params (string, object?)[] parameters)
    {
        using var q = Command(c, tx, sql, parameters);
        var value = q.ExecuteScalar();
        return value is DBNull ? null : value;
    }

    internal static SqliteCommand Command(SqliteConnection c, SqliteTransaction? tx, string sql, params (string, object?)[] parameters)
    {
        var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText = sql;
        foreach (var (name, value) in parameters) q.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return q;
    }

    internal static List<T> Query<T>(SqliteConnection c, SqliteTransaction? tx, string sql, Func<SqliteDataReader, T> read,
        params (string, object?)[] parameters)
    {
        using var q = Command(c, tx, sql, parameters);
        using var r = q.ExecuteReader();
        var result = new List<T>();
        while (r.Read()) result.Add(read(r));
        return result;
    }

    internal static void Migrate(SqliteConnection c, SqliteTransaction tx) => Execute(c, tx, """
        CREATE TABLE IF NOT EXISTS graph_state (
          project_id TEXT PRIMARY KEY REFERENCES rb_projects(project_id), project_path TEXT NOT NULL,
          revision INTEGER NOT NULL DEFAULT 0, kernel_revision INTEGER NOT NULL DEFAULT 0,
          options_json TEXT NOT NULL, autonomy INTEGER NOT NULL DEFAULT 0, budget_started_utc INTEGER);
        CREATE TABLE IF NOT EXISTS graph_modules (
          project_id TEXT NOT NULL REFERENCES rb_projects(project_id), module_id TEXT NOT NULL,
          parent_id TEXT, module_json TEXT NOT NULL, origin TEXT NOT NULL, actor TEXT NOT NULL,
          version INTEGER NOT NULL, PRIMARY KEY(project_id,module_id));
        CREATE TABLE IF NOT EXISTS graph_relations (
          project_id TEXT NOT NULL REFERENCES rb_projects(project_id), relation_id TEXT NOT NULL,
          relation_json TEXT NOT NULL, origin TEXT NOT NULL, actor TEXT NOT NULL,
          version INTEGER NOT NULL, PRIMARY KEY(project_id,relation_id));
        CREATE TABLE IF NOT EXISTS graph_sources (
          project_id TEXT NOT NULL, entity_id TEXT NOT NULL, source_path TEXT NOT NULL,
          source_kind TEXT NOT NULL, content_hash TEXT NOT NULL, detail TEXT NOT NULL,
          observed_utc INTEGER NOT NULL, version INTEGER NOT NULL,
          PRIMARY KEY(project_id,entity_id,source_path,source_kind));
        CREATE TABLE IF NOT EXISTS graph_ticket_modules (
          ticket_id TEXT NOT NULL REFERENCES rb_tickets(ticket_id), module_id TEXT NOT NULL,
          PRIMARY KEY(ticket_id,module_id));
        CREATE TABLE IF NOT EXISTS graph_ticket_links (
          ticket_id TEXT PRIMARY KEY REFERENCES rb_tickets(ticket_id), session_id TEXT);
        CREATE TABLE IF NOT EXISTS graph_session_links (
          project_id TEXT NOT NULL REFERENCES rb_projects(project_id), session_id TEXT NOT NULL,
          ticket_id TEXT NOT NULL DEFAULT '', PRIMARY KEY(project_id,session_id,ticket_id));
        INSERT OR IGNORE INTO graph_session_links(project_id,session_id,ticket_id)
          SELECT t.project_id,l.session_id,l.ticket_id FROM graph_ticket_links l
          JOIN rb_tickets t ON t.ticket_id=l.ticket_id WHERE l.session_id IS NOT NULL;
        CREATE TABLE IF NOT EXISTS graph_run_links (
          run_id TEXT PRIMARY KEY REFERENCES rb_runs(run_id), session_id TEXT,
          stop_requested INTEGER NOT NULL DEFAULT 0);
        CREATE TABLE IF NOT EXISTS graph_entries (
          entry_id TEXT PRIMARY KEY, project_id TEXT NOT NULL REFERENCES rb_projects(project_id),
          ticket_id TEXT REFERENCES rb_tickets(ticket_id), module_id TEXT, kind TEXT NOT NULL,
          actor TEXT NOT NULL, text TEXT NOT NULL, reference TEXT, created_utc INTEGER NOT NULL, version INTEGER NOT NULL);
        CREATE INDEX IF NOT EXISTS graph_entries_project ON graph_entries(project_id,created_utc);
        CREATE TABLE IF NOT EXISTS graph_revisions (
          project_id TEXT NOT NULL REFERENCES rb_projects(project_id), version INTEGER NOT NULL,
          summary TEXT NOT NULL, created_utc INTEGER NOT NULL, snapshot_json TEXT NOT NULL,
          PRIMARY KEY(project_id,version));
        CREATE TABLE IF NOT EXISTS graph_evidence_sources (
          evidence_id TEXT PRIMARY KEY REFERENCES rb_evidence(evidence_id), file_path TEXT NOT NULL,
          content_hash TEXT NOT NULL, content_size INTEGER NOT NULL, observed_utc INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS graph_receipts (
          project_id TEXT NOT NULL, command_id TEXT NOT NULL, payload_hash TEXT NOT NULL,
          result_json TEXT NOT NULL, PRIMARY KEY(project_id,command_id));
        """);
}

/// <summary>Host identity aliases the existing kernel instead of cloning its tickets under a new id.</summary>
internal static class GraphProjectIdentity
{
    internal sealed record Binding(string Database, string AuthorityId, string ProjectPath);

    internal static Binding Resolve(string dataRoot, string hostId, string projectPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        var canonical = GraphStorage.Canonical(projectPath);
        var key = GraphStorage.PathKey(canonical);
        var index = Path.Combine(dataRoot, "auto-dev", "graph", "projects.db");
        using var c = GraphStorage.Open(index);
        GraphStorage.Execute(c, null, """
            CREATE TABLE IF NOT EXISTS graph_project_paths (
              path_key TEXT PRIMARY KEY, project_path TEXT NOT NULL, database_path TEXT NOT NULL, authority_id TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS graph_project_aliases (
              host_id TEXT PRIMARY KEY, path_key TEXT NOT NULL REFERENCES graph_project_paths(path_key));
            """);
        using var tx = c.BeginTransaction();
        var prior = GraphStorage.Scalar(c, tx, "SELECT path_key FROM graph_project_aliases WHERE host_id=$host", ("$host", hostId)) as string;
        if (prior is not null && prior != key)
            throw new InvalidOperationException("Host project id already belongs to another project path.");
        var binding = GraphStorage.Query(c, tx,
            "SELECT database_path,authority_id,project_path FROM graph_project_paths WHERE path_key=$path",
            r => new Binding(r.GetString(0), r.GetString(1), r.GetString(2)), ("$path", key)).SingleOrDefault();
        if (binding is null)
        {
            // Legacy hashes used the original string, so probe exact/slash/case variants first,
            // then inspect only local kernel metadata for an existing matching path identity.
            var variants = new[] { projectPath, canonical, canonical.Replace('\\', '/'), canonical.Replace('/', '\\') }
                .SelectMany(p => new[] { p, p.ToLowerInvariant(), p.ToUpperInvariant() }).Distinct().ToArray();
            var candidates = variants.Select(p => RebirthPaths.Database(dataRoot, p)).Distinct().Where(File.Exists).ToList();
            var rebirthRoot = Path.Combine(dataRoot, "auto-dev", "rebirth");
            if (Directory.Exists(rebirthRoot))
                candidates.AddRange(Directory.EnumerateDirectories(rebirthRoot).Select(p => Path.Combine(p, "kernel.db"))
                    .Where(File.Exists).Where(p => !candidates.Contains(p, StringComparer.OrdinalIgnoreCase)));
            var matches = new List<Binding>();
            foreach (var database in candidates)
            {
                using var legacy = new SqliteConnection(new SqliteConnectionStringBuilder
                    { DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
                legacy.Open();
                var ids = GraphStorage.Query(legacy, null, "SELECT project_id FROM rb_projects", r => r.GetString(0));
                var legacyIds = variants.Select(RebirthPaths.ProjectId).Append(hostId).ToHashSet(StringComparer.Ordinal);
                var found = ids.Where(legacyIds.Contains).ToArray();
                if (found.Length > 1) throw new InvalidOperationException("Multiple kernel identities match this path; reconcile them explicitly.");
                if (found.Length == 1) matches.Add(new Binding(database, found[0], canonical));
            }
            if (matches.Count > 1) throw new InvalidOperationException("Multiple existing kernels match this project; reconcile them before opening Graph.");
            binding = matches.SingleOrDefault() ?? new Binding(RebirthPaths.Database(dataRoot, canonical), hostId, canonical);
            GraphStorage.Execute(c, tx, "INSERT INTO graph_project_paths VALUES($key,$path,$db,$authority)",
                ("$key", key), ("$path", canonical), ("$db", binding.Database), ("$authority", binding.AuthorityId));
        }
        GraphStorage.Execute(c, tx, "INSERT OR IGNORE INTO graph_project_aliases VALUES($host,$path)", ("$host", hostId), ("$path", key));
        tx.Commit();
        return binding;
    }
}
