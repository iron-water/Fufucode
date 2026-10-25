using System.Text.Json;

namespace Fufucode.Core;

/// <summary>Persisted selection and disk status, never a claim about another process's loaded extensions.</summary>
public sealed record ProjectExtensionStatus(string Path, bool Enabled, bool Exists, bool HasRecordedError);
public sealed record ProjectExtensionChange(string Path, bool Enabled, string SessionId);

/// <summary>
/// Shared project extension use case. Uses the same desktop.db preference keys as the App host.
/// A one-shot change verifies a fresh Pi connection, then saves the selection; it does not restart other frontends.
/// </summary>
public static class ProjectExtensionService
{
    private static StringComparer Comparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static string EnabledKey(string projectId) => "app.enabled-extensions:" + projectId;
    private static string AddedKey(string projectId) => "app.added-extensions:" + projectId;
    private static string ErrorsKey(string projectId) => "app.extension-errors:" + projectId;

    public static IReadOnlyList<ProjectExtensionStatus> List(AppDataLayout layout, string projectPath)
    {
        var metadata = new DesktopMetadataStore(layout);
        var project = metadata.GetOrCreateProject(ValidateProjectPath(projectPath));
        var enabled = ReadPaths(metadata.GetPreference(EnabledKey(project.Id)));
        var errors = ReadErrors(metadata.GetPreference(ErrorsKey(project.Id)));
        var known = enabled.Concat(ReadPaths(metadata.GetPreference(AddedKey(project.Id))))
            .Concat(errors.Keys).Concat(Discover(project.Path, layout.AgentDirectory))
            .Distinct(Comparer).OrderBy(path => path, Comparer);
        return known.Select(path => new ProjectExtensionStatus(path, enabled.Contains(path, Comparer),
            Exists(path), errors.ContainsKey(path))).ToArray();
    }

    /// <summary>Apply persisted selection before any CLI session is opened; missing paths remain in preferences.</summary>
    public static IReadOnlyList<string> ApplyToManager(PersistentSessionManager manager, string projectPath)
    {
        ArgumentNullException.ThrowIfNull(manager);
        var project = manager.Metadata.GetOrCreateProject(ValidateProjectPath(projectPath));
        var enabled = ReadPaths(manager.Metadata.GetPreference(EnabledKey(project.Id)));
        manager.UserExtensionPaths = enabled.Where(Exists).ToArray();
        return enabled.Where(path => !Exists(path)).ToArray();
    }

    /// <summary>
    /// Validate under Core trust/lease rules using the requested or most recent project session, or create one.
    /// Save only after that fresh connection closes successfully. Any failure retains previous preferences
    /// and restores the manager's previous extension snapshot. No trust decision is made here.
    /// </summary>
    public static async Task<ProjectExtensionChange> ChangeAsync(PersistentSessionManager manager,
        string projectPath, string extensionPath, bool enabled, string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manager);
        projectPath = ValidateProjectPath(projectPath);
        var path = NormalizeExtensionPath(extensionPath, projectPath);
        if (enabled && !IsValidExtensionPath(path))
            throw new ArgumentException("Extension must be an existing .ts/.js file or directory with a Pi extension entry.", nameof(extensionPath));
        if (sessionId is not null && (string.IsNullOrWhiteSpace(sessionId) ||
            sessionId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '_' and not '-')))
            throw new ArgumentException("Invalid session ID.", nameof(sessionId));

        var metadata = manager.Metadata;
        var project = metadata.GetOrCreateProject(projectPath);
        using var changeLease = SessionLease.Acquire(manager.Layout.ProjectSessionsDirectory(project.Id),
            "extension-preferences");
        if (sessionId is not null)
        {
            SessionRecord requested;
            try { requested = metadata.GetSession(sessionId); }
            catch (KeyNotFoundException error) { throw new ArgumentException("Unknown session ID.", nameof(sessionId), error); }
            if (requested.ProjectId != project.Id)
                throw new ArgumentException("Session does not belong to the selected project.", nameof(sessionId));
        }
        else sessionId = metadata.ListSessions(project.Id).FirstOrDefault()?.Id;

        var oldSelection = ReadPaths(metadata.GetPreference(EnabledKey(project.Id)));
        var nextSelection = enabled ? oldSelection.Append(path).Distinct(Comparer).ToArray()
            : oldSelection.Where(item => !Comparer.Equals(item, path)).ToArray();
        var previousManagerPaths = manager.UserExtensionPaths;
        try
        {
            var active = nextSelection.Where(Exists).ToArray();
            manager.UserExtensionPaths = active;
            var trust = manager.GetProjectTrustStatus(projectPath);
            if (trust.RequiresDecision) throw new ProjectTrustDecisionRequiredException(trust);
            // 会话后端跟随全局默认（缺省 FUMO），不再写死某个二等插件。
            // Core will reject explicitly selected local extensions when the project is denied.
            await using (var session = sessionId is null
                ? await manager.CreateAsync(projectPath, cancellationToken: cancellationToken, backend: manager.PreferredBackend)
                : await manager.ResumeAsync(sessionId, cancellationToken))
            {
                if (!session.Coordinator.CanSendPrompt)
                    throw new InvalidOperationException("Approval bridge is unavailable for the verification session.");
                sessionId = session.Session.Id;
            }
            cancellationToken.ThrowIfCancellationRequested();
            var oldAddedRaw = metadata.GetPreference(AddedKey(project.Id));
            var oldErrorsRaw = metadata.GetPreference(ErrorsKey(project.Id));
            var errors = ReadErrors(oldErrorsRaw);
            var clearedErrors = false;
            foreach (var loadedPath in active) clearedErrors |= errors.Remove(loadedPath);
            var added = ReadPaths(oldAddedRaw).Append(path).Distinct(Comparer).ToArray();
            // The complete intended selection includes missing paths, matching App persistence semantics.
            metadata.SetPreference(AddedKey(project.Id), JsonSerializer.Serialize(added));
            try
            {
                if (clearedErrors) metadata.SetPreference(ErrorsKey(project.Id), JsonSerializer.Serialize(errors));
                // Save selection last so a failure clearing errors cannot publish an uncommitted selection.
                metadata.SetPreference(EnabledKey(project.Id), JsonSerializer.Serialize(nextSelection));
            }
            catch
            {
                if (clearedErrors) metadata.SetPreference(ErrorsKey(project.Id), oldErrorsRaw ?? "{}");
                metadata.SetPreference(AddedKey(project.Id), oldAddedRaw ?? "[]");
                throw;
            }
            return new ProjectExtensionChange(path, enabled, sessionId!);
        }
        catch (Exception error)
        {
            manager.UserExtensionPaths = previousManagerPaths;
            if (error is UserExtensionLoadException load)
            {
                // Only Core's path-specific classification proves an extension failure. Never persist stderr or exception text.
                var errors = ReadErrors(metadata.GetPreference(ErrorsKey(project.Id)));
                errors[load.ExtensionPath] = load.Kind == UserExtensionLoadFailureKind.MissingPath
                    ? "missing_path" : "pi_startup_failure";
                metadata.SetPreference(ErrorsKey(project.Id), JsonSerializer.Serialize(errors));
            }
            throw;
        }
    }

    public static string ValidateProjectPath(string projectPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectPath));
        if (!Directory.Exists(path)) throw new ArgumentException("Project directory does not exist.", nameof(projectPath));
        return path;
    }

    private static string NormalizeExtensionPath(string path, string projectPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path, projectPath));
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    /// <summary>Read-only mirror of App ExtensionCatalog discovery; discovery never changes selection.</summary>
    private static IReadOnlyList<string> Discover(string projectPath, string agentDirectory)
    {
        var found = new List<string>();
        foreach (var directory in new[] { Path.Combine(projectPath, ".pi", "extensions"),
                     Path.Combine(agentDirectory, "extensions") })
        {
            if (!Directory.Exists(directory)) continue;
            try
            {
                foreach (var file in Directory.EnumerateFiles(directory))
                    if (IsValidExtensionPath(file)) found.Add(file);
                foreach (var child in Directory.EnumerateDirectories(directory))
                    if (IsValidExtensionPath(child)) found.Add(child);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // As in the App catalog, unreadable discovery directories preserve partial results.
            }
        }
        return found;
    }

    // Mirrors App ExtensionCatalog's entry validation. Pi validates package declaration values on startup.
    private static bool IsValidExtensionPath(string path)
    {
        if (File.Exists(path)) return Path.GetExtension(path).ToLowerInvariant() is ".ts" or ".js";
        if (!Directory.Exists(path)) return false;
        if (File.Exists(Path.Combine(path, "index.ts")) || File.Exists(Path.Combine(path, "index.js"))) return true;
        var package = Path.Combine(path, "package.json");
        if (!File.Exists(package)) return false;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(package));
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("pi", out var pi) && pi.ValueKind == JsonValueKind.Object &&
                pi.TryGetProperty("extensions", out _);
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException) { return false; }
    }

    private static string[] ReadPaths(string? raw)
    {
        if (raw is null) return [];
        try
        {
            var paths = JsonSerializer.Deserialize<string[]>(raw) ?? [];
            return paths.Where(path => !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path))
                .Select(path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))).Distinct(Comparer).ToArray();
        }
        catch (JsonException) { return []; }
    }

    private static Dictionary<string, string> ReadErrors(string? raw)
    {
        if (raw is null) return new Dictionary<string, string>(Comparer);
        try { return new Dictionary<string, string>(JsonSerializer.Deserialize<Dictionary<string, string>>(raw) ?? [], Comparer); }
        catch (JsonException) { return new Dictionary<string, string>(Comparer); }
    }
}
