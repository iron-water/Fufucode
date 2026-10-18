using System.Text;
using System.Text.Json;

namespace Fufucode.Core.Features.LocalReview;

/// <summary>
/// Module-local review history. Entries are the export documents (scope, frozen commits, findings,
/// staleness) written as one JSON file per run inside a module-owned directory injected by the
/// host. The module never opens or writes the user's desktop.db, and history survives an app
/// restart because it is on disk; a reopened page reads exactly what was written.
/// </summary>
public sealed class LocalReviewHistory
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _directory;

    public LocalReviewHistory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(_directory);
    }

    public string Root => _directory;

    public string Save(LocalReviewRunResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var document = LocalReviewExport.ToDocument(result);
        // The extension stays outside SafeId: SafeId replaces dots, and a name without .json
        // would vanish from ListRecent's enumeration.
        var fileName = SafeId(result.Scope.Kind + "-" +
            (result.Snapshot?.HeadCommit is { Length: >= 8 } head ? head[..8] : "unknown") +
            "-" + result.FinishedAtUtc.ToString("yyyyMMdd-HHmmssfff")) + ".json";
        var path = Path.Combine(_directory, fileName);
        File.WriteAllText(path, JsonSerializer.Serialize(document, Json), new UTF8Encoding(false));
        return fileName;
    }

    public LocalReviewExport.ReviewDocument? Load(string fileName)
    {
        var path = Resolve(fileName);
        if (path is null || !File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<LocalReviewExport.ReviewDocument>(File.ReadAllText(path)); }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        { return null; }
    }

    public IReadOnlyList<LocalReviewExport.ReviewDocument> ListRecent(int limit = 50)
    {
        var files = Array.Empty<string>();
        try { files = Directory.GetFiles(_directory, "*.json"); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return []; }
        // Names end with a sortable timestamp; newest first without trusting file mtimes.
        return files.OrderByDescending(path => path, StringComparer.Ordinal)
            .Take(Math.Max(1, limit))
            .Select(path => Load(Path.GetFileName(path)))
            .OfType<LocalReviewExport.ReviewDocument>()
            .ToList();
    }

    /// <summary>Only files inside the module directory can be read back; path material is never
    /// resolved from a caller-supplied name.</summary>
    private string? Resolve(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        var candidate = Path.GetFullPath(Path.Combine(_directory, fileName));
        return AppDataLayout.IsUnderDirectory(candidate, _directory) ? candidate : null;
    }

    private static string SafeId(string value)
    {
        var builder = new StringBuilder();
        foreach (var c in value)
            builder.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-');
        return builder.Length == 0 ? "review" : builder.ToString();
    }
}
