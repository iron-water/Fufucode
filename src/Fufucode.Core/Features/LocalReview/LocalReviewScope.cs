using System.Text.Json.Serialization;

namespace Fufucode.Core.Features.LocalReview;

public enum LocalReviewScopeKind
{
    /// <summary>Working-tree differences against HEAD (tracked edits plus untracked files).</summary>
    Workspace,
    /// <summary>Staged (index) differences against HEAD.</summary>
    Staged,
    /// <summary>One explicit commit against its first parent (root commits use the empty tree).</summary>
    Commit,
    /// <summary>An explicit base..target commit range.</summary>
    Range,
}

public enum LocalReviewScopeStatus
{
    Resolved,
    NotGitWorktree,
    GitUnavailable,
    InvalidRevision,
    MergeCommitUnsupported,
    EmptyScope,
    CommandFailed,
}

/// <summary>
/// Whether the shared source-specific snapshot can freeze this scope. PendingSharedSnapshotApi
/// is retained for old result compatibility; all four supported kinds now use the correct source.
/// </summary>
public enum LocalReviewSnapshotCapability
{
    FullSnapshot,
    PendingSharedSnapshotApi,
}

public sealed record LocalReviewScopedFile(string Path, string Kind, int? Added, int? Removed);

public sealed record LocalReviewScopeResolution(
    LocalReviewScopeKind Kind,
    LocalReviewScopeStatus Status,
    string RepositoryRoot,
    string ProjectPath,
    string? HeadCommit,
    string? BaseCommit,
    string? TargetCommit,
    LocalReviewSnapshotCapability SnapshotCapability,
    IReadOnlyList<LocalReviewScopedFile> Files,
    string? Error)
{
    [JsonIgnore] public bool IsValid => Status == LocalReviewScopeStatus.Resolved;
    [JsonIgnore] public string Description => Kind switch
    {
        LocalReviewScopeKind.Workspace => "工作区差异（相对 HEAD）",
        LocalReviewScopeKind.Staged => "暂存差异（相对 HEAD）",
        LocalReviewScopeKind.Commit => $"提交 {Short(TargetCommit)}（相对其第一父提交）",
        LocalReviewScopeKind.Range => $"{Short(BaseCommit)}..{Short(TargetCommit)}",
        _ => Kind.ToString(),
    };

    private static string Short(string? sha) =>
        sha is { Length: >= 8 } ? sha[..8] : sha ?? "?";
}

/// <summary>
/// Resolves and validates an explicitly requested review scope with read-only git calls.
/// Revisions are verified through <c>rev-parse --verify --quiet &lt;rev&gt;^{commit}</c>; nothing is
/// interpolated into a shell. A leading '-', embedded whitespace, or '..' inside a single
/// revision field is refused as option/path injection.
/// </summary>
public static class LocalReviewScopeResolver
{
    public static async Task<LocalReviewScopeResolution> ResolveAsync(string projectPath,
        LocalReviewScopeKind kind, string? revision = null, string? baseRevision = null,
        CancellationToken cancellationToken = default)
    {
        var project = ReviewGit.NormalizeOrNone(projectPath);
        if (project is null)
            return Fail(kind, projectPath, LocalReviewScopeStatus.NotGitWorktree, "project-path-missing");

        var top = await ReviewGit.RunAsync(project, ["rev-parse", "--show-toplevel"], cancellationToken);
        if (top.Code == ReviewGit.GitMissing)
            return Fail(kind, project, LocalReviewScopeStatus.GitUnavailable, "git-unavailable");
        if (top.Code != 0)
            return Fail(kind, project, LocalReviewScopeStatus.NotGitWorktree, "not-a-git-worktree");
        var root = Path.GetFullPath(top.Output.Trim());

        var head = await ReviewGit.RunAsync(root, ["rev-parse", "--verify", "-q", "HEAD"], cancellationToken);
        var headCommit = head.Code == 0 ? head.Output.Trim() : null;
        if (headCommit is null)
            return new(kind, LocalReviewScopeStatus.EmptyScope, root, project, null, null, null,
                CapabilityFor(kind), [], "repository-has-no-commits");

        string? basis = null, target = null;
        // Enumerated as: <subcommand> <format flag> <rest>. The format flag is spliced per call.
        string subcommand = "diff";
        List<string> rest;
        switch (kind)
        {
            case LocalReviewScopeKind.Workspace:
            case LocalReviewScopeKind.Staged:
            {
                rest = ["-z", "--no-renames", "--no-ext-diff", "--no-textconv", "--no-color"];
                if (kind == LocalReviewScopeKind.Staged) rest.Add("--cached");
                rest.Add("HEAD");
                break;
            }
            case LocalReviewScopeKind.Commit:
            {
                var resolved = await ResolveCommitAsync(kind, root, project, headCommit, revision, cancellationToken);
                if (resolved.Failure is not null) return resolved.Failure;
                target = resolved.Commit;
                var parents = await ReviewGit.RunAsync(root,
                    ["rev-list", "--parents", "-n", "1", target!], cancellationToken);
                var parts = parents.Output.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parents.Code != 0 || parts.Length == 0 || parts[0].Length != target!.Length)
                    return Fail(kind, project, LocalReviewScopeStatus.CommandFailed,
                        "revision-verify-failed", headCommit);
                // rev-list --parents emits: self firstParent secondParent... (one line, one commit).
                if (parts.Length > 2)
                    return new(kind, LocalReviewScopeStatus.MergeCommitUnsupported, root, project, headCommit,
                        null, target, CapabilityFor(kind), [],
                        "merge-commit-unsupported: review the range base..target explicitly instead");
                basis = parts.Length == 2 ? parts[1] : null; // null: root commit, first-parent absent
                rest = basis is null
                    // Root commit: no first parent, so enumerate against the empty root tree.
                    ? ["-r", "-z", "--no-commit-id", "--no-renames", "--no-ext-diff", "--no-textconv",
                        "--root", target!]
                    : ["-z", "--no-renames", "--no-ext-diff", "--no-textconv", "--no-color",
                        $"{basis}..{target}"];
                if (basis is null) subcommand = "diff-tree";
                break;
            }
            case LocalReviewScopeKind.Range:
            {
                var from = await ResolveCommitAsync(kind, root, project, headCommit, baseRevision, cancellationToken);
                if (from.Failure is not null) return from.Failure;
                var to = await ResolveCommitAsync(kind, root, project, headCommit, revision, cancellationToken);
                if (to.Failure is not null) return to.Failure;
                basis = from.Commit; target = to.Commit;
                rest = ["-z", "--no-renames", "--no-ext-diff", "--no-textconv", "--no-color",
                    $"{basis}..{target}"];
                break;
            }
            default:
                return Fail(kind, project, LocalReviewScopeStatus.InvalidRevision, "unsupported-scope-kind", headCommit);
        }

        var (nameStatus, numstat, failure) = await EnumerateAsync(kind, root, project, headCommit,
            subcommand, rest, cancellationToken);
        if (failure is not null) return failure;
        var files = Merge(nameStatus, numstat, root, project);
        // Workspace diffs never report untracked files; the review scope must, because those
        // changes exist and the shared snapshot contract reviews them as added content.
        if (kind == LocalReviewScopeKind.Workspace)
            files = files.Concat(await UntrackedFilesAsync(root, project, cancellationToken)).
                GroupBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First()).OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
        if (files.Count == 0)
            return new(kind, LocalReviewScopeStatus.EmptyScope, root, project, headCommit, basis, target,
                CapabilityFor(kind), [], null);
        return new(kind, LocalReviewScopeStatus.Resolved, root, project, headCommit, basis, target,
            CapabilityFor(kind), files, null);
    }

    /// <summary>True when the shared snapshot contract can freeze the resolved scope for precise location.</summary>
    public static bool CanFreezeWithSharedSnapshot(LocalReviewScopeResolution scope) =>
        scope.SnapshotCapability == LocalReviewSnapshotCapability.FullSnapshot;

    /// <summary>Freeze each resolved scope from its actual worktree, index or immutable target tree.</summary>
    public static async Task<FileDiffSnapshot?> CaptureFixedSnapshotAsync(
        LocalReviewScopeResolution scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (scope.Status != LocalReviewScopeStatus.Resolved ||
            scope.SnapshotCapability != LocalReviewSnapshotCapability.FullSnapshot) return null;
        var source = scope.Kind switch
        {
            LocalReviewScopeKind.Workspace => GitDiffSnapshotSource.Worktree,
            LocalReviewScopeKind.Staged => GitDiffSnapshotSource.StagedIndex,
            LocalReviewScopeKind.Commit or LocalReviewScopeKind.Range => GitDiffSnapshotSource.TargetTree,
            _ => throw new ArgumentOutOfRangeException(nameof(scope)),
        };
        return await GitDiffSnapshot.CaptureRangeAsync(scope.ProjectPath,
            scope.Files.Select(file => file.Path), source, scope.BaseCommit, scope.TargetCommit,
            scope.HeadCommit, cancellationToken);
    }

    /// <summary>The precise shared-API gap, in machine-readable form for the coordination card.</summary>
    public static string MissingSharedApiFor(LocalReviewScopeKind kind) => kind switch
    {
        LocalReviewScopeKind.Staged =>
            "GitDiffSnapshot needs a staged-vs-HEAD patch mode (capture index content, not worktree bytes).",
        LocalReviewScopeKind.Commit =>
            "GitDiffSnapshot needs a commit-vs-first-parent patch mode (frozen bytes from the commit tree).",
        LocalReviewScopeKind.Range =>
            "GitDiffSnapshot needs a base..target patch mode (frozen bytes from the target tree).",
        _ => "none",
    };

    private static async Task<(List<(string Status, string Path)> Names,
        List<(string Path, int? Added, int? Removed)> Stats, LocalReviewScopeResolution? Failure)>
        EnumerateAsync(LocalReviewScopeKind kind, string root, string project, string? headCommit,
            string subcommand, List<string> rest, CancellationToken cancellationToken)
    {
        var name = await ReviewGit.RunAsync(root,
            new[] { subcommand, "--name-status" }.Concat(rest), cancellationToken);
        if (name.Code != 0)
            return ([], [], Fail(kind, project, LocalReviewScopeStatus.CommandFailed, ReviewGit.Truncate(name.Error), headCommit));
        var stat = await ReviewGit.RunAsync(root,
            new[] { subcommand, "--numstat" }.Concat(rest), cancellationToken);
        if (stat.Code != 0)
            return ([], [], Fail(kind, project, LocalReviewScopeStatus.CommandFailed, ReviewGit.Truncate(stat.Error), headCommit));
        return (ParseNameStatusZ(name.Output), ParseNumstatZ(stat.Output), null);
    }

    /// <summary>git emits <c>--name-status -z</c> as separate NUL fields: status, path (and an
    /// extra source field for R/C). Verified against git 2.55 output shapes.</summary>
    internal static List<(string Status, string Path)> ParseNameStatusZ(string output)
    {
        var result = new List<(string, string)>();
        var fields = output.Split('\0');
        for (var i = 0; i < fields.Length; i++)
        {
            var status = fields[i];
            if (status.Length == 0 || i + 1 >= fields.Length) continue;
            if (!char.IsAsciiLetter(status[0])) continue;
            var path = fields[++i];
            if (status.StartsWith('R') || status.StartsWith('C')) i++; // extra source field
            result.Add((status, path));
        }
        return result;
    }

    /// <summary><c>--numstat -z</c> emits one field per record: "added\tremoved\tpath".</summary>
    internal static List<(string Path, int? Added, int? Removed)> ParseNumstatZ(string output)
    {
        var result = new List<(string, int?, int?)>();
        foreach (var record in output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var first = record.IndexOf('\t');
            var second = first < 0 ? -1 : record.IndexOf('\t', first + 1);
            if (second < 0) continue;
            var addedText = record[..first];
            var removedText = record[(first + 1)..second];
            var path = record[(second + 1)..];
            result.Add((path,
                addedText == "-" ? null : int.TryParse(addedText, out var a) ? a : null,
                removedText == "-" ? null : int.TryParse(removedText, out var r) ? r : null));
        }
        return result;
    }

    /// <summary>Untracked files for the workspace scope, via ls-files -z (raw paths, no quoting).</summary>
    private static async Task<List<LocalReviewScopedFile>> UntrackedFilesAsync(string root,
        string project, CancellationToken cancellationToken)
    {
        var listing = await ReviewGit.RunAsync(root,
            ["ls-files", "-z", "--others", "--exclude-standard"], cancellationToken);
        var files = new List<LocalReviewScopedFile>();
        if (listing.Code != 0) return files;
        foreach (var path in listing.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var relative = ToProjectRelative(path, root, project);
            if (relative is null) continue;
            files.Add(new LocalReviewScopedFile(relative, "untracked", null, null));
        }
        return files;
    }

    private static List<LocalReviewScopedFile> Merge(List<(string Status, string Path)> names,
        List<(string Path, int? Added, int? Removed)> stats, string root, string project)
    {
        var byPath = new Dictionary<string, (string Status, int? Added, int? Removed)>(StringComparer.Ordinal);
        foreach (var (status, path) in names)
        {
            var relative = ToProjectRelative(path, root, project);
            if (relative is null) continue;
            byPath[relative] = (status, null, null);
        }
        foreach (var (path, added, removed) in stats)
        {
            var relative = ToProjectRelative(path, root, project);
            if (relative is null) continue;
            var status = byPath.TryGetValue(relative, out var existing) ? existing.Status : "M";
            byPath[relative] = (status, added, removed);
        }
        return byPath.Select(pair => new LocalReviewScopedFile(pair.Key,
                KindName(pair.Value.Status), pair.Value.Added, pair.Value.Removed))
            .OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
    }

    private static string KindName(string status) => status.Length switch
    {
        0 => "modified",
        _ => status[^1] switch
        {
            'A' => "added", 'D' => "deleted", 'M' => "modified", 'R' => "renamed",
            'C' => "copied", 'T' => "type-changed", 'U' => "unmerged", _ => "modified",
        },
    };

    /// <summary>Maps a repository-root-relative path to project-relative; anything outside the
    /// selected project directory is excluded from the review scope.</summary>
    private static string? ToProjectRelative(string rootRelativePath, string root, string project)
    {
        if (string.IsNullOrWhiteSpace(rootRelativePath)) return null;
        try
        {
            var full = Path.GetFullPath(Path.Combine(root, rootRelativePath.Replace('\\', '/')));
            if (!AppDataLayout.IsUnderDirectory(full, project)) return null;
            return Path.GetRelativePath(project, full).Replace('\\', '/');
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static async Task<(string? Commit, LocalReviewScopeResolution? Failure)> ResolveCommitAsync(
        LocalReviewScopeKind kind, string root, string project, string? headCommit,
        string? revision, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(revision))
            return (null, Fail(kind, project, LocalReviewScopeStatus.InvalidRevision, "revision-required", headCommit));
        var trimmed = revision.Trim();
        if (trimmed.StartsWith('-') || trimmed.Any(char.IsWhiteSpace) || trimmed.Contains("..", StringComparison.Ordinal))
            return (null, Fail(kind, project, LocalReviewScopeStatus.InvalidRevision,
                "revision-must-be-a-single-ref", headCommit));
        var verify = await ReviewGit.RunAsync(root,
            ["rev-parse", "--verify", "--quiet", trimmed + "^{commit}"], cancellationToken);
        if (verify.Code == ReviewGit.GitMissing)
            return (null, Fail(kind, project, LocalReviewScopeStatus.GitUnavailable, "git-unavailable", headCommit));
        var sha = verify.Output.Trim();
        if (verify.Code != 0 || sha.Length is not (40 or 64) || !sha.All(Uri.IsHexDigit))
            return (null, Fail(kind, project, LocalReviewScopeStatus.InvalidRevision,
                "revision-not-a-commit", headCommit));
        return (sha.ToLowerInvariant(), null);
    }

    private static LocalReviewSnapshotCapability CapabilityFor(LocalReviewScopeKind kind) =>
        Enum.IsDefined(kind) ? LocalReviewSnapshotCapability.FullSnapshot
            : LocalReviewSnapshotCapability.PendingSharedSnapshotApi;

    private static LocalReviewScopeResolution Fail(LocalReviewScopeKind kind, string project,
        LocalReviewScopeStatus status, string error, string? head = null) =>
        new(kind, status, "", project, head, null, null, CapabilityFor(kind), [], error);
}
