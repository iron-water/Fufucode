using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Fufucode.Core;

public sealed record ReadOnlySessionReportResult(string? SessionId, SessionHistory History, bool PartialTail);

/// <summary>Reads registered Pi history without starting Pi, taking a session lease, or mutating metadata.</summary>
public static class ReadOnlySessionReport
{
    private const int MaximumHistoryBytes = 32 * 1024 * 1024;

    public static ReadOnlySessionReportResult Load(AppDataLayout layout, string projectPath, string? sessionId)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var project = AppDataLayout.NormalizeProjectPath(projectPath);
        if (sessionId is not null) ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        RejectReparsePoints(layout.DatabasePath);
        if (!File.Exists(layout.DatabasePath)) return Missing(sessionId);

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = layout.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 10
        }.ToString());
        connection.Open();
        using (var version = connection.CreateCommand())
        {
            version.CommandText = "PRAGMA user_version";
            var schema = Convert.ToInt32(version.ExecuteScalar());
            if (schema < DesktopMetadataStore.MinimumReadOnlySchemaVersion)
                throw new InvalidOperationException($"Read-only session reports require metadata schema {DesktopMetadataStore.MinimumReadOnlySchemaVersion} or newer; found {schema}.");
            if (schema > DesktopMetadataStore.SchemaVersion)
                throw new InvalidOperationException($"Metadata schema {schema} is newer than this app.");
        }
        using var query = connection.CreateCommand();
        query.CommandText = """
            SELECT s.id, s.project_id, s.file_path
            FROM sessions AS s JOIN projects AS p ON p.id=s.project_id
            WHERE p.path_key=$key
            """ + (sessionId is null ? " ORDER BY s.last_opened_utc DESC, s.id LIMIT 1" : " AND s.id=$id LIMIT 1");
        query.Parameters.AddWithValue("$key", AppDataLayout.PathKey(project));
        if (sessionId is not null) query.Parameters.AddWithValue("$id", sessionId);
        using var reader = query.ExecuteReader();
        if (!reader.Read()) return Missing(sessionId);
        var selectedId = reader.GetString(0);
        var projectId = reader.GetString(1);
        var filePath = reader.IsDBNull(2) ? null : reader.GetString(2);
        if (filePath is null) return Empty(selectedId);
        if (projectId.Length == 0 || projectId is "." or ".." ||
            projectId.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0 ||
            Path.IsPathRooted(projectId))
            throw new InvalidOperationException("Invalid registered project ID.");
        var directory = layout.ProjectSessionsDirectory(projectId);
        if (!Path.IsPathFullyQualified(filePath) || !AppDataLayout.IsUnderDirectory(filePath, directory) ||
            !string.Equals(Path.GetExtension(filePath), ".jsonl", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Registered session history must be a JSONL file inside its project session directory.");
        RejectReparsePoints(filePath);
        if (!File.Exists(filePath)) return Empty(selectedId);

        byte[] bytes;
        try
        {
            using var file = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (file.Length > MaximumHistoryBytes) throw new InvalidDataException("Session history exceeds the 32 MiB report limit.");
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int count;
            while ((count = file.Read(chunk)) != 0)
            {
                if (buffer.Length + count > MaximumHistoryBytes)
                    throw new InvalidDataException("Session history exceeds the 32 MiB report limit.");
                buffer.Write(chunk, 0, count);
            }
            bytes = buffer.ToArray();
        }
        catch (FileNotFoundException) { return Empty(selectedId); }
        catch (DirectoryNotFoundException) { return Empty(selectedId); }

        var entries = new List<JsonElement>();
        var partialTail = false;
        var start = bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? 3 : 0;
        var line = 0;
        while (start < bytes.Length)
        {
            line++;
            var end = Array.IndexOf(bytes, (byte)'\n', start);
            var complete = end >= 0;
            if (!complete) end = bytes.Length;
            var length = end - start;
            if (length > 0 && bytes[end - 1] == '\r') length--;
            JsonElement value;
            try
            {
                using var document = JsonDocument.Parse(bytes.AsMemory(start, length));
                value = document.RootElement.Clone();
            }
            catch (JsonException error)
            {
                if (!complete) { partialTail = true; break; }
                throw new InvalidDataException($"Invalid JSON on complete session history line {line}.", error);
            }
            if (value.ValueKind != JsonValueKind.Object ||
                !value.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
                throw new InvalidDataException($"Invalid session entry on line {line}.");
            if (type.GetString() != "session") entries.Add(value);
            start = end + 1;
        }
        var history = new SessionHistory();
        string? leafId = null;
        if (entries.Count > 0 && entries[^1].TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
            leafId = id.GetString();
        history.ApplyEntriesResponse(JsonSerializer.SerializeToElement(new { entries, leafId }), replace: true);
        return new(selectedId, history, partialTail);
    }

    private static ReadOnlySessionReportResult Empty(string? sessionId) => new(sessionId, new SessionHistory(), false);

    private static ReadOnlySessionReportResult Missing(string? sessionId)
    {
        if (sessionId is not null) throw new KeyNotFoundException("Session is not registered to the requested project.");
        return Empty(null);
    }

    private static void RejectReparsePoints(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full)!;
        var current = root;
        Check(current);
        foreach (var component in full[root.Length..].Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            Check(current);
        }

        static void Check(string value)
        {
            try
            {
                if ((File.GetAttributes(value) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Read-only session reports refuse reparse points.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}
