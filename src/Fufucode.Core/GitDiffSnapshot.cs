using System.Diagnostics;
using System.Text;

namespace Fufucode.Core;

public enum GitDiffSnapshotStatus
{
    Captured,
    NotGitWorktree,
    NoPathsCaptured,
}

public enum GitDiffSnapshotSource { Worktree, StagedIndex, TargetTree }

/// <summary>Snapshot of one explicitly requested file at capture time. This is a read-only copy
/// of the file bytes plus the unified diff result; it is not an atomic Git snapshot.</summary>
public sealed record GitDiffSnapshotEntry(string RelativePath,
    bool ExistedAtCapture, bool TrackedAtCapture, bool Stale,
    string? Sha256, byte[]? Bytes, FileDiff Diff, string? Error);

/// <summary>Read-only snapshot for a fixed list of files, intended for later review consumers (O4).
/// It records the actual HEAD and repository root, copies file bytes only (never mutating git), and
/// marks entries stale when the source or HEAD changed after capture rather than claiming atomicity.</summary>
public sealed record FileDiffSnapshot(string RepositoryRoot, string HeadCommit,
    DateTimeOffset CapturedAtUtc, string ProjectPath,
    IReadOnlyList<GitDiffSnapshotEntry> Entries, GitDiffSnapshotStatus Status,
    string? Error)
{
    public GitDiffSnapshotSource Source { get; init; } = GitDiffSnapshotSource.Worktree;
    public string? BaseCommit { get; init; }
    public string? TargetCommit { get; init; }
    public string? IndexFingerprint { get; init; }
    /// <summary>True when the captured source (worktree/index/target tree) and observed HEAD remain valid.</summary>
    public async Task<bool> IsFreshAsync(CancellationToken cancellationToken = default) =>
        await GitDiffSnapshot.CheckFreshAsync(this, cancellationToken);

    /// <summary>Whether frozen lines can safely locate current worktree content. Independent of source freshness.</summary>
    public Task<bool> MatchesWorkingTreeAsync(CancellationToken cancellationToken = default) =>
        GitDiffSnapshot.CheckWorkingTreeBytesAsync(this, cancellationToken);
}

public static partial class GitDiffSnapshot
{
    public const int MaximumFileBytes = 8 * 1024 * 1024;

    public static async Task<FileDiffSnapshot> CaptureAsync(string projectPath,
        IEnumerable<string> relativePaths, CancellationToken cancellationToken = default)
    {
        var project = AppDataLayout.NormalizeProjectPath(projectPath);
        var paths = (relativePaths ?? throw new ArgumentNullException(nameof(relativePaths)))
            .Select(p => p?.Replace('\\', '/') ?? "")
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        try
        {
            var top = await GitAsync(project, ["rev-parse", "--show-toplevel"], cancellationToken);
            if (top.Code != 0)
                return new(project, "", DateTimeOffset.UtcNow, project, [],
                    GitDiffSnapshotStatus.NotGitWorktree, "not-a-git-worktree");
            var root = Path.GetFullPath(top.Output.Trim());
            var headResult = await GitAsync(root, ["rev-parse", "--verify", "HEAD"], cancellationToken);
            var head = headResult.Code == 0 ? headResult.Output.Trim() : "";

            var stateBefore = await CaptureStateAsync(root, project, paths, cancellationToken);
            var diffs = new List<FileDiff>();
            foreach (var path in paths)
                diffs.Add(await DiffQuery.FileAsync(project, path, cancellationToken: cancellationToken));
            var stateAfter = await CaptureStateAsync(root, project, paths, cancellationToken);
            var headAfter = (await GitAsync(root, ["rev-parse", "--verify", "HEAD"], cancellationToken)).Output.Trim();

            var entries = new List<GitDiffSnapshotEntry>();
            for (var i = 0; i < paths.Length; i++)
            {
                var path = paths[i];
                var diff = diffs[i];
                var before = stateBefore.TryGetValue(path, out var b) ? b : null;
                var after = stateAfter.TryGetValue(path, out var a) ? a : null;
                var stale = !(before?.Equals(after) ?? after is null) || head != headAfter;
                var full = Resolve(root, project, path);
                byte[]? bytes = null;
                string? sha = null;
                string? error = diff.Error;
                var tracked = after?.Tracked ?? false;
                var existed = after?.Exists ?? false;
                if (full is not null && File.Exists(full))
                {
                    var info = new FileInfo(full);
                    if (info.Length > MaximumFileBytes)
                    {
                        error = "file-too-large";
                    }
                    else
                    {
                        var read = await ReadBoundedFileAsync(full, cancellationToken);
                        if (read is null) { error = "file-too-large"; stale = true; }
                        else
                        {
                            bytes = read;
                            sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
                            var post = await CaptureStateAsync(root, project, [path], cancellationToken);
                            var current = post.TryGetValue(path, out var p) ? p : null;
                            if (!(current?.Equals(after) ?? after is null)) stale = true;
                        }
                    }
                }
                entries.Add(new(path, existed, tracked, stale, sha, bytes, diff, error));
            }
            var status = entries.Count == 0 ? GitDiffSnapshotStatus.NoPathsCaptured
                : GitDiffSnapshotStatus.Captured;
            return new(root, head, DateTimeOffset.UtcNow, project, entries, status,
                entries.Any(e => e.Stale) ? "concurrent-changes-detected" : null);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return new(project, "", DateTimeOffset.UtcNow, project, [],
                GitDiffSnapshotStatus.NotGitWorktree, "git-unavailable");
        }
    }

    /// <summary>Compares the recorded state to a fresh read of the same paths and HEAD.</summary>
    public static async Task<bool> CheckFreshAsync(FileDiffSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        if (snapshot.Source != GitDiffSnapshotSource.Worktree)
            return await CheckGitSourceFreshAsync(snapshot, cancellationToken);
        if (snapshot.Status != GitDiffSnapshotStatus.Captured || snapshot.Entries.Count == 0)
            return false;
        var head = await GitAsync(snapshot.RepositoryRoot, ["rev-parse", "--verify", "HEAD"], cancellationToken);
        if (head.Code != 0 || head.Output.Trim() != snapshot.HeadCommit) return false;
        var current = await CaptureStateAsync(snapshot.RepositoryRoot, snapshot.ProjectPath,
            snapshot.Entries.Select(e => e.RelativePath), cancellationToken);
        foreach (var entry in snapshot.Entries)
        {
            if (entry.Stale || entry.Error is not null) return false;
            if (!current.TryGetValue(entry.RelativePath, out var now)) return false;
            if (now.Exists != entry.ExistedAtCapture || now.Tracked != entry.TrackedAtCapture)
                return false;
            if (now.Exists && entry.Sha256 is not null)
            {
                if (now.Length > MaximumFileBytes || now.Sha256 != entry.Sha256) return false;
                var full = Resolve(snapshot.RepositoryRoot, snapshot.ProjectPath, entry.RelativePath);
                if (full is null || !File.Exists(full)) return false;
                var bytes = await ReadBoundedFileAsync(full, cancellationToken);
                if (bytes is null) return false;
                var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
                if (!string.Equals(sha, entry.Sha256, StringComparison.Ordinal)) return false;
            }
        }
        return true;
    }

    private sealed record FileState(bool Exists, bool Tracked, string? Sha256, long Length);

    private static async Task<Dictionary<string, FileState>> CaptureStateAsync(
        string root, string project, IEnumerable<string> relativePaths, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, FileState>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in relativePaths)
        {
            var full = Resolve(root, project, path);
            var exists = full is not null && File.Exists(full);
            string? sha = null;
            long length = 0;
            // Index identity is independent of the file existing on disk: a deleted tracked file is still tracked.
            var tracked = false;
            if (full is not null)
            {
                var relative = Path.GetRelativePath(root, full).Replace('\\', '/');
                var trackedResult = await GitAsync(root, ["ls-files", "--error-unmatch", "--", relative], cancellationToken);
                tracked = trackedResult.Code == 0;
            }
            if (exists && full is not null)
            {
                var info = new FileInfo(full);
                length = info.Length;
                if (length <= MaximumFileBytes)
                {
                    var bytes = await ReadBoundedFileAsync(full, cancellationToken);
                    if (bytes is null) length = MaximumFileBytes + 1L;
                    else sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
                }
            }
            result[path] = new(exists, tracked, sha, length);
        }
        return result;
    }

    private static async Task<byte[]?> ReadBoundedFileAsync(string path, CancellationToken ct)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var content = new MemoryStream();
        var buffer = new byte[8192]; int count;
        while ((count = await file.ReadAsync(buffer, ct)) > 0)
        {
            if (content.Length + count > MaximumFileBytes) return null;
            content.Write(buffer, 0, count);
        }
        return content.ToArray();
    }

    private static string? Resolve(string root, string project, string relativePath)
    {
        try
        {
            var full = Path.GetFullPath(Path.Combine(project, relativePath));
            if (!AppDataLayout.IsUnderDirectory(full, root)) return null;
            if (HasReparsePoint(full, root)) return null;
            return full;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static bool HasReparsePoint(string file, string root)
    {
        try
        {
            for (var current = file; current is not null; current = Path.GetDirectoryName(current))
            {
                if ((File.Exists(current) || Directory.Exists(current)) &&
                    (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
                if (string.Equals(Path.GetFullPath(current), root, StringComparison.OrdinalIgnoreCase)) break;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return true; }
        return false;
    }

    private static async Task<(int Code, string Output)> GitAsync(string directory, IEnumerable<string> args, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("git")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var arg in new[] { "--no-optional-locks", "-C", directory, "-c", "core.fsmonitor=false" }.Concat(args))
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("Could not start git.");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        try { await process.WaitForExitAsync(cancellationToken); }
        catch (OperationCanceledException) { if (!process.HasExited) process.Kill(true); throw; }
        await error;
        return (process.ExitCode, await output);
    }
}
