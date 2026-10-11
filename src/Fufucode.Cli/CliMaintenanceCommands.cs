using System.Text.Json;
using Fufucode.Core;

namespace Fufucode.Cli;

/// <summary>One-shot maintenance command adapter. Returns 0; parameter errors throw ArgumentException.</summary>
public static class CliMaintenanceCommands
{
    public static async Task<int> RunExtensionsAsync(AppDataLayout layout, string projectPath,
        IReadOnlyList<string> arguments, bool json, TextWriter output,
        PersistentSessionManager? manager = null, string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(output);
        if (arguments.SequenceEqual(["list"]))
        {
            var extensions = ProjectExtensionService.List(layout, projectPath);
            if (json) await output.WriteLineAsync(JsonSerializer.Serialize(extensions.Select(item => new
            {
                path = item.Path, enabled = item.Enabled, exists = item.Exists,
                hasRecordedError = item.HasRecordedError, loaded = (bool?)null
            })));
            else if (extensions.Count == 0) await output.WriteLineAsync("No configured or discovered extensions for this project.");
            else foreach (var item in extensions)
                await output.WriteLineAsync($"{item.Path}  {(item.Enabled ? "enabled" : "disabled")}" +
                    $"  {(item.Exists ? "exists" : "missing")}" +
                    (item.HasRecordedError ? "  [recorded error]" : "") + "  [load state not queried]");
            return 0;
        }
        if (arguments.Count != 2 || arguments[0] is not ("enable" or "disable"))
            throw new ArgumentException("Usage: extensions list | enable <path> | disable <path>.", nameof(arguments));
        if (manager is null) throw new ArgumentException("Extension changes require a session manager.", nameof(manager));
        if (!string.Equals(layout.Root, manager.Layout.Root, OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("Session manager must use the selected application data root.", nameof(manager));
        var result = await ProjectExtensionService.ChangeAsync(manager, projectPath, arguments[1],
            arguments[0] == "enable", sessionId, cancellationToken);
        if (json) await output.WriteLineAsync(JsonSerializer.Serialize(new
            { path = result.Path, enabled = result.Enabled, sessionId = result.SessionId }));
        else await output.WriteLineAsync($"{(result.Enabled ? "Enabled" : "Disabled")} {result.Path}; verified in session {result.SessionId}.");
        return 0;
    }

    public static async Task<int> RunDiagnosticsAsync(AppDataLayout layout,
        IReadOnlyList<string> arguments, bool json, TextWriter output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(output);
        if (arguments.Count is < 1 or > 2 || arguments[0] != "export")
            throw new ArgumentException("Usage: diag export [<path>].", nameof(arguments));
        layout.EnsureCreated();
        var path = arguments.Count == 2 ? ResolveExportPath(arguments[1])
            : Path.Combine(layout.ExportsDirectory, "diagnostics-" + Guid.NewGuid().ToString("N") + ".jsonl");
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var logRoot = Path.TrimEndingDirectorySeparator(layout.LogsDirectory);
        if (path.Equals(logRoot, comparison) || path.StartsWith(logRoot + Path.DirectorySeparatorChar, comparison))
            throw new ArgumentException("Export destination must be outside the source logs directory.", nameof(arguments));
        if (File.Exists(path) || Directory.Exists(path))
            throw new ArgumentException("Export destination already exists.", nameof(arguments));
        if (!Directory.Exists(Path.GetDirectoryName(path)))
            throw new ArgumentException("Export destination directory does not exist.", nameof(arguments));
        var created = false;
        int records;
        try
        {
            await using var destination = new FileStream(path, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 8192, FileOptions.Asynchronous);
            created = true;
            records = await DiagnosticExport.WriteJsonLinesAsync(layout, destination, cancellationToken);
        }
        catch
        {
            if (created) File.Delete(path);
            throw;
        }
        if (json) await output.WriteLineAsync(JsonSerializer.Serialize(new { path, records }));
        else await output.WriteLineAsync($"Exported {records} diagnostic records to {path}.");
        return 0;
    }

    private static string ResolveExportPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.GetFullPath(path);
    }
}
