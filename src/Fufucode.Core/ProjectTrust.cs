namespace Fufucode.Core;

public enum ProjectTrustDecision { Trust, DoNotTrust }

/// <summary>Application-owned trust decision for Pi's protected project resources.</summary>
public sealed record ProjectTrustStatus(string ProjectPath, ProjectTrustDecision? Decision,
    IReadOnlyList<string> ProtectedResources)
{
    public bool RequiresDecision => Decision is null && ProtectedResources.Count > 0;
}

public sealed class ProjectTrustDecisionRequiredException : InvalidOperationException
{
    public ProjectTrustDecisionRequiredException(ProjectTrustStatus status)
        : base($"Project trust must be decided before opening {status.ProjectPath}.") => Status = status;

    public ProjectTrustStatus Status { get; }
}

/// <summary>A denied project cannot explicitly load executable extension code from inside itself.</summary>
public sealed class ProjectExtensionNotTrustedException : InvalidOperationException
{
    public ProjectExtensionNotTrustedException(ProjectTrustStatus status, string extensionPath)
        : base($"Project-local extension requires a trusted project: {extensionPath}.")
    {
        Status = status;
        ExtensionPath = extensionPath;
    }

    public ProjectTrustStatus Status { get; }
    public string ExtensionPath { get; }
}

internal static class ProjectTrustResources
{
    private static readonly string[] PiEntries =
        ["settings.json", "extensions", "skills", "prompts", "themes", "SYSTEM.md", "APPEND_SYSTEM.md"];

    public static IReadOnlyList<string> Find(string projectPath)
    {
        var result = new List<string>();
        var piDirectory = Path.Combine(projectPath, ".pi");
        foreach (var entry in PiEntries)
        {
            var candidate = Path.Combine(piDirectory, entry);
            if (Exists(candidate)) result.Add(candidate);
        }

        var home = Environment.GetEnvironmentVariable("HOME");
        if (string.IsNullOrWhiteSpace(home)) home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var userSkills = Path.GetFullPath(Path.Combine(home, ".agents", "skills"));
        for (var directory = projectPath; directory is not null; directory = Directory.GetParent(directory)?.FullName)
        {
            var skills = Path.Combine(directory, ".agents", "skills");
            if (!PathKey(skills).Equals(PathKey(userSkills), StringComparison.Ordinal) && Exists(skills))
                result.Add(skills);
        }
        return result.ToArray();
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
    private static string PathKey(string path) => AppDataLayout.PathKey(Path.GetFullPath(path));
}
