using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace Fufucode.App.Features.Library;

/// <summary>
/// 资料库自有 SQLite 存储（&lt;libraryRoot&gt;/library.db）。
/// 与 DesktopMetadataStore 完全分离：不读写 desktop.db，不改现有元数据表。
/// 副本内容存放在 &lt;libraryRoot&gt;/copies/&lt;entryId&gt;/v&lt;n&gt;/&lt;文件名&gt;，库内只记相对路径。
/// </summary>
internal sealed class LibraryStore : IDisposable
{
    public const int SchemaVersion = 1;

    private readonly string _root;
    private readonly string _path;
    private bool _disposed;

    public LibraryStore(string libraryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);
        _root = Path.GetFullPath(libraryRoot);
        Directory.CreateDirectory(_root);
        _path = Path.Combine(_root, "library.db");
        Migrate();
    }

    public string Root => _root;
    public string DatabasePath => _path;
    public string CopiesDirectory => Path.Combine(_root, "copies");
    public string CopyVersionDirectory(string entryId, int version) =>
        Path.Combine(CopiesDirectory, entryId, "v" + version.ToString(CultureInfo.InvariantCulture));

    public string? ToAbsolute(string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) return null;
        return Path.IsPathRooted(relative) ? relative : Path.Combine(_root, relative);
    }

    public string? ToRelative(string? absolute)
    {
        if (string.IsNullOrWhiteSpace(absolute)) return null;
        var full = Path.GetFullPath(absolute);
        var prefix = Path.TrimEndingDirectorySeparator(_root) + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? full[prefix.Length..] : full;
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = 10,
        }.ToString());
        connection.Open();
        using var foreignKeys = connection.CreateCommand();
        foreignKeys.CommandText = "PRAGMA foreign_keys=ON";
        foreignKeys.ExecuteNonQuery();
        return connection;
    }

    private void Migrate()
    {
        using var connection = Open();
        using var versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version";
        var version = Convert.ToInt32(versionCommand.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (version > SchemaVersion) throw new InvalidOperationException($"Library schema {version} is newer than this app.");
        if (version == SchemaVersion) return;
        using var tx = connection.BeginTransaction();
        if (version < 1)
        {
            Execute(connection, tx, """
                CREATE TABLE entries (
                  id TEXT PRIMARY KEY,
                  title TEXT NOT NULL,
                  kind INTEGER NOT NULL,
                  storage_mode INTEGER NOT NULL,
                  source_path TEXT,
                  source_url TEXT,
                  stored_path TEXT,
                  content_hash TEXT,
                  source_size INTEGER NOT NULL DEFAULT 0,
                  source_modified_utc TEXT,
                  added_utc TEXT NOT NULL,
                  updated_utc TEXT NOT NULL,
                  status INTEGER NOT NULL DEFAULT 0,
                  last_checked_utc TEXT,
                  note TEXT,
                  text_state INTEGER NOT NULL DEFAULT 0,
                  text_error TEXT
                );
                CREATE INDEX ix_entries_hash ON entries(content_hash);
                CREATE TABLE entry_tags (
                  entry_id TEXT NOT NULL REFERENCES entries(id) ON DELETE CASCADE,
                  tag TEXT NOT NULL,
                  PRIMARY KEY (entry_id, tag)
                );
                CREATE INDEX ix_entry_tags_tag ON entry_tags(tag);
                CREATE TABLE entry_text (
                  entry_id TEXT PRIMARY KEY REFERENCES entries(id) ON DELETE CASCADE,
                  text TEXT NOT NULL
                );
                CREATE TABLE index_tokens (
                  entry_id TEXT NOT NULL REFERENCES entries(id) ON DELETE CASCADE,
                  token TEXT NOT NULL
                );
                CREATE INDEX ix_index_tokens_token ON index_tokens(token);
                CREATE TABLE entry_versions (
                  entry_id TEXT NOT NULL REFERENCES entries(id) ON DELETE CASCADE,
                  version_no INTEGER NOT NULL,
                  content_hash TEXT NOT NULL,
                  size INTEGER NOT NULL,
                  source_modified_utc TEXT,
                  captured_utc TEXT NOT NULL,
                  stored_path TEXT,
                  origin TEXT NOT NULL,
                  PRIMARY KEY (entry_id, version_no)
                );
                """);
        }
        using var stamp = connection.CreateCommand();
        stamp.Transaction = tx;
        stamp.CommandText = $"PRAGMA user_version={SchemaVersion}";
        stamp.ExecuteNonQuery();
        tx.Commit();
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction tx, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void Bind(SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static string? Format(DateTimeOffset? value) => value is { } stamp ? Format(stamp) : null;

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static DateTimeOffset? ParseNullable(string? value) =>
        string.IsNullOrEmpty(value) ? null : Parse(value);

    public IReadOnlyList<LibraryEntry> ListEntries()
    {
        using var connection = Open();
        var entries = ReadEntries(connection, null);
        var tags = ReadTags(connection);
        var versions = ReadVersionCounts(connection);
        return entries.Select(entry => entry with
        {
            Tags = tags.TryGetValue(entry.Id, out var list) ? list : Array.Empty<string>(),
            VersionCount = versions.TryGetValue(entry.Id, out var count) ? count : 0,
        }).ToList();
    }

    public LibraryEntry? GetEntry(string id)
    {
        using var connection = Open();
        var entry = ReadEntries(connection, id).FirstOrDefault();
        if (entry is null) return null;
        var tags = ReadTags(connection).TryGetValue(id, out var list) ? list : Array.Empty<string>();
        var count = ReadVersionCounts(connection).TryGetValue(id, out var value) ? value : 0;
        return entry with { Tags = tags, VersionCount = count };
    }

    private List<LibraryEntry> ReadEntries(SqliteConnection connection, string? id)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, title, kind, storage_mode, source_path, source_url, stored_path, content_hash,
                   source_size, source_modified_utc, added_utc, updated_utc, status, last_checked_utc,
                   note, text_state, text_error
            FROM entries
            """ + (id is null ? " ORDER BY added_utc DESC" : " WHERE id = $id");
        if (id is not null) Bind(command, "$id", id);
        var results = new List<LibraryEntry>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new LibraryEntry(
                reader.GetString(0),
                reader.GetString(1),
                (LibraryEntryKind)reader.GetInt32(2),
                (LibraryStorageMode)reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                ToAbsolute(reader.IsDBNull(6) ? null : reader.GetString(6)),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.GetInt64(8),
                ParseNullable(reader.IsDBNull(9) ? null : reader.GetString(9)),
                Parse(reader.GetString(10)),
                Parse(reader.GetString(11)),
                (LibraryEntryStatus)reader.GetInt32(12),
                ParseNullable(reader.IsDBNull(13) ? null : reader.GetString(13)),
                reader.IsDBNull(14) ? null : reader.GetString(14),
                (LibraryTextState)reader.GetInt32(15),
                reader.IsDBNull(16) ? null : reader.GetString(16),
                Array.Empty<string>(),
                0));
        }
        return results;
    }

    private static Dictionary<string, IReadOnlyList<string>> ReadTags(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT entry_id, tag FROM entry_tags ORDER BY tag";
        var results = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetString(0);
            if (!results.TryGetValue(id, out var list)) results[id] = list = new List<string>();
            list.Add(reader.GetString(1));
        }
        return results.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value, StringComparer.Ordinal);
    }

    private static Dictionary<string, int> ReadVersionCounts(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT entry_id, COUNT(*) FROM entry_versions GROUP BY entry_id";
        var results = new Dictionary<string, int>(StringComparer.Ordinal);
        using var reader = command.ExecuteReader();
        while (reader.Read()) results[reader.GetString(0)] = reader.GetInt32(1);
        return results;
    }

    public void InsertEntry(LibraryEntry entry)
    {
        using var connection = Open();
        using var tx = connection.BeginTransaction();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = tx;
            command.CommandText = """
                INSERT INTO entries (id, title, kind, storage_mode, source_path, source_url, stored_path,
                    content_hash, source_size, source_modified_utc, added_utc, updated_utc, status,
                    last_checked_utc, note, text_state, text_error)
                VALUES ($id, $title, $kind, $mode, $source, $url, $stored, $hash, $size, $mtime,
                    $added, $updated, $status, $checked, $note, $text, $error)
                """;
            Bind(command, "$id", entry.Id);
            Bind(command, "$title", entry.Title);
            Bind(command, "$kind", (int)entry.Kind);
            Bind(command, "$mode", (int)entry.StorageMode);
            Bind(command, "$source", entry.SourcePath);
            Bind(command, "$url", entry.SourceUrl);
            Bind(command, "$stored", ToRelative(entry.StoredPath));
            Bind(command, "$hash", entry.ContentHash);
            Bind(command, "$size", entry.SourceSize);
            Bind(command, "$mtime", Format(entry.SourceModifiedUtc));
            Bind(command, "$added", Format(entry.AddedUtc));
            Bind(command, "$updated", Format(entry.UpdatedUtc));
            Bind(command, "$status", (int)entry.Status);
            Bind(command, "$checked", Format(entry.LastCheckedUtc));
            Bind(command, "$note", entry.Note);
            Bind(command, "$text", (int)entry.TextState);
            Bind(command, "$error", entry.TextError);
            command.ExecuteNonQuery();
        }
        foreach (var tag in entry.Tags.Distinct(StringComparer.Ordinal))
            InsertTag(connection, tx, entry.Id, tag);
        tx.Commit();
    }

    private static void InsertTag(SqliteConnection connection, SqliteTransaction tx, string id, string tag)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "INSERT OR IGNORE INTO entry_tags (entry_id, tag) VALUES ($id, $tag)";
        Bind(command, "$id", id);
        Bind(command, "$tag", tag);
        command.ExecuteNonQuery();
    }

    /// <summary>刷新来源检查与内容版本后写回条目状态。title 为 null 时保持原标题。</summary>
    public void UpdateEntryState(string id, string? title, string? note, string? contentHash, long size,
        DateTimeOffset? sourceModifiedUtc, string? storedPath, LibraryEntryStatus status,
        DateTimeOffset lastCheckedUtc, LibraryTextState textState, string? textError)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE entries SET
              title = COALESCE($title, title),
              note = $note,
              content_hash = $hash,
              source_size = $size,
              source_modified_utc = $mtime,
              stored_path = COALESCE($stored, stored_path),
              status = $status,
              last_checked_utc = $checked,
              updated_utc = $updated,
              text_state = $text,
              text_error = $error
            WHERE id = $id
            """;
        Bind(command, "$title", title);
        Bind(command, "$note", note);
        Bind(command, "$hash", contentHash);
        Bind(command, "$size", size);
        Bind(command, "$mtime", Format(sourceModifiedUtc));
        Bind(command, "$stored", ToRelative(storedPath));
        Bind(command, "$status", (int)status);
        Bind(command, "$checked", Format(lastCheckedUtc));
        Bind(command, "$updated", Format(lastCheckedUtc));
        Bind(command, "$text", (int)textState);
        Bind(command, "$error", textError);
        Bind(command, "$id", id);
        command.ExecuteNonQuery();
    }

    public void SetTags(string id, IEnumerable<string> tags)
    {
        using var connection = Open();
        using var tx = connection.BeginTransaction();
        using (var clear = connection.CreateCommand())
        {
            clear.Transaction = tx;
            clear.CommandText = "DELETE FROM entry_tags WHERE entry_id = $id";
            Bind(clear, "$id", id);
            clear.ExecuteNonQuery();
        }
        foreach (var tag in tags.Where(tag => !string.IsNullOrWhiteSpace(tag)).Select(tag => tag.Trim()).Distinct(StringComparer.Ordinal))
            InsertTag(connection, tx, id, tag);
        tx.Commit();
    }

    public void AddTag(string id, string tag)
    {
        using var connection = Open();
        using var tx = connection.BeginTransaction();
        InsertTag(connection, tx, id, tag.Trim());
        tx.Commit();
    }

    public void RemoveTag(string id, string tag)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM entry_tags WHERE entry_id = $id AND tag = $tag";
        Bind(command, "$id", id);
        Bind(command, "$tag", tag.Trim());
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<LibraryTagCount> ListTags()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT tag, COUNT(*) FROM entry_tags GROUP BY tag ORDER BY tag";
        var results = new List<LibraryTagCount>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) results.Add(new LibraryTagCount(reader.GetString(0), reader.GetInt32(1)));
        return results;
    }

    public void SetEntryText(string id, string text)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO entry_text (entry_id, text) VALUES ($id, $text) ON CONFLICT(entry_id) DO UPDATE SET text = excluded.text";
        Bind(command, "$id", id);
        Bind(command, "$text", text);
        command.ExecuteNonQuery();
    }

    public string? GetEntryText(string id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT text FROM entry_text WHERE entry_id = $id";
        Bind(command, "$id", id);
        return command.ExecuteScalar() as string;
    }

    public void ReplaceEntryTokens(string id, IReadOnlyList<string> tokens)
    {
        using var connection = Open();
        using var tx = connection.BeginTransaction();
        using (var clear = connection.CreateCommand())
        {
            clear.Transaction = tx;
            clear.CommandText = "DELETE FROM index_tokens WHERE entry_id = $id";
            Bind(clear, "$id", id);
            clear.ExecuteNonQuery();
        }
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = tx;
            insert.CommandText = "INSERT INTO index_tokens (entry_id, token) VALUES ($id, $token)";
            var idParameter = insert.Parameters.Add("$id", SqliteType.Text);
            var tokenParameter = insert.Parameters.Add("$token", SqliteType.Text);
            idParameter.Value = id;
            foreach (var token in tokens)
            {
                tokenParameter.Value = token;
                insert.ExecuteNonQuery();
            }
        }
        tx.Commit();
    }

    /// <summary>按 token 命中数排序返回条目 ID（命中越多越相关）。</summary>
    public IReadOnlyList<(string EntryId, int Score)> SearchTokens(IReadOnlyList<string> tokens, int limit)
    {
        if (tokens.Count == 0) return Array.Empty<(string, int)>();
        using var connection = Open();
        using var command = connection.CreateCommand();
        var names = new List<string>();
        for (var index = 0; index < tokens.Count; index++)
        {
            var name = "$t" + index.ToString(CultureInfo.InvariantCulture);
            names.Add(name);
            Bind(command, name, tokens[index]);
        }
        command.CommandText =
            "SELECT entry_id, COUNT(*) AS score FROM index_tokens WHERE token IN (" + string.Join(",", names) +
            ") GROUP BY entry_id ORDER BY score DESC, entry_id ASC LIMIT $limit";
        Bind(command, "$limit", limit);
        var results = new List<(string, int)>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) results.Add((reader.GetString(0), reader.GetInt32(1)));
        return results;
    }

    public void AddVersion(string entryId, LibraryVersion version)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO entry_versions (entry_id, version_no, content_hash, size, source_modified_utc, captured_utc, stored_path, origin)
            VALUES ($id, $no, $hash, $size, $mtime, $captured, $stored, $origin)
            """;
        Bind(command, "$id", entryId);
        Bind(command, "$no", version.Number);
        Bind(command, "$hash", version.ContentHash);
        Bind(command, "$size", version.Size);
        Bind(command, "$mtime", Format(version.SourceModifiedUtc));
        Bind(command, "$captured", Format(version.CapturedUtc));
        Bind(command, "$stored", ToRelative(version.StoredPath));
        Bind(command, "$origin", version.Origin);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<LibraryVersion> ListVersions(string id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT version_no, content_hash, size, source_modified_utc, captured_utc, stored_path, origin
            FROM entry_versions WHERE entry_id = $id ORDER BY version_no
            """;
        Bind(command, "$id", id);
        var results = new List<LibraryVersion>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new LibraryVersion(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetInt64(2),
                ParseNullable(reader.IsDBNull(3) ? null : reader.GetString(3)),
                Parse(reader.GetString(4)),
                ToAbsolute(reader.IsDBNull(5) ? null : reader.GetString(5)),
                reader.GetString(6)));
        }
        return results;
    }

    public string? FindEntryIdByHash(string hash)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM entries WHERE content_hash = $hash ORDER BY added_utc LIMIT 1";
        Bind(command, "$hash", hash);
        return command.ExecuteScalar() as string;
    }

    public bool DeleteEntry(string id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM entries WHERE id = $id";
        Bind(command, "$id", id);
        return command.ExecuteNonQuery() > 0;
    }

    public int CountEntries()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM entries";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>需要（重新）索引的条目：文本状态为 Pending。</summary>
    public IReadOnlyList<(string Id, string Title)> PendingIndexEntries()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, title FROM entries WHERE text_state = $state ORDER BY added_utc";
        Bind(command, "$state", (int)LibraryTextState.Pending);
        var results = new List<(string, string)>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) results.Add((reader.GetString(0), reader.GetString(1)));
        return results;
    }

    public IReadOnlyList<(string Id, string Title)> AllEntryIds()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, title FROM entries ORDER BY added_utc";
        var results = new List<(string, string)>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) results.Add((reader.GetString(0), reader.GetString(1)));
        return results;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }
}
