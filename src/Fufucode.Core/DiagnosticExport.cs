using System.Text.Json;
using System.Text;
using System.IO.Compression;

namespace Fufucode.Core;

/// <summary>Copies only bounded diagnostic metadata into a caller-owned export stream.</summary>
public static class DiagnosticExport
{
    public static async Task<IReadOnlyList<PiDiagnosticEvent>> ReadRecentAsync(string path,
        int limit = 500, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 5000) throw new ArgumentOutOfRangeException(nameof(limit));
        var entries = new Queue<PiDiagnosticEvent>();
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length > 4096) continue;
            PiDiagnosticEvent? entry;
            try { entry = JsonSerializer.Deserialize(line, DiagnosticJsonContext.Default.PiDiagnosticEvent); }
            catch (JsonException) { continue; }
            if (entry is null) continue;
            if (entries.Count == limit) entries.Dequeue();
            entries.Enqueue(DiagnosticLog.Sanitize(entry));
        }
        return entries.ToArray();
    }
    public static async Task<int> WriteArchiveAsync(AppDataLayout layout, Stream destination,
        string applicationVersion, CancellationToken cancellationToken = default)
    {
        using var archive = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);
        var summary = archive.CreateEntry("system.json");
        await using (var stream = summary.Open())
        {
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                applicationVersion, dotnet = Environment.Version.ToString(),
                os = Environment.OSVersion.VersionString, exportedAt = DateTimeOffset.UtcNow,
                exclusions = new[] { "credentials", "environment values", "conversation contents", "raw configuration" }
            }));
            await stream.WriteAsync(bytes, cancellationToken);
        }
        await using var logs = archive.CreateEntry("diagnostics.jsonl").Open();
        return await WriteJsonLinesAsync(layout, logs, cancellationToken);
    }

    public static async Task<int> WriteJsonLinesAsync(AppDataLayout layout, Stream destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite) throw new ArgumentException("Destination must be writable.", nameof(destination));
        if (!Directory.Exists(layout.LogsDirectory)) return 0;
        var count = 0;
        await using var writer = new StreamWriter(destination, new UTF8Encoding(false), 8192, leaveOpen: true);
        foreach (var path in Directory.EnumerateFiles(layout.LogsDirectory, "app-*.jsonl"))
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                // Invalid or oversized records are excluded. Never copy raw log text to an export.
                if (line.Length > 4096) continue;
                PiDiagnosticEvent? entry;
                try { entry = JsonSerializer.Deserialize(line, DiagnosticJsonContext.Default.PiDiagnosticEvent); }
                catch (JsonException) { continue; }
                if (entry is null) continue;
                var safe = DiagnosticLog.Sanitize(entry);
                await writer.WriteLineAsync(JsonSerializer.Serialize(safe, DiagnosticJsonContext.Default.PiDiagnosticEvent));
                count++;
            }
        }
        await writer.FlushAsync(cancellationToken);
        return count;
    }
}
