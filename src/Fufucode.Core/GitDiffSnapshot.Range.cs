using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Fufucode.Core;

public static partial class GitDiffSnapshot
{
    private sealed record GitSourceEntry(string? Blob, string? Mode, string? Error);

    /// <summary>Freeze index or target-tree bytes and their diff against immutable base blobs.
    /// Refs must already be resolved full commit IDs. No write-tree, checkout, staging or source writes.</summary>
    public static async Task<FileDiffSnapshot> CaptureRangeAsync(string projectPath, IEnumerable<string> relativePaths,
        GitDiffSnapshotSource source, string? baseCommit = null, string? targetCommit = null,
        string? expectedHeadCommit = null, CancellationToken cancellationToken = default)
    {
        if (source == GitDiffSnapshotSource.Worktree)
        {
            var snapshot = await CaptureAsync(projectPath, relativePaths, cancellationToken);
            return expectedHeadCommit is not null && !string.Equals(expectedHeadCommit, snapshot.HeadCommit, StringComparison.OrdinalIgnoreCase)
                ? snapshot with { Entries = [], Status = GitDiffSnapshotStatus.NoPathsCaptured, Error = "head-changed-since-scope-resolution" }
                : snapshot;
        }
        if (!Enum.IsDefined(source)) throw new ArgumentOutOfRangeException(nameof(source));
        if ((baseCommit is not null && !CommitId(baseCommit)) ||
            (source == GitDiffSnapshotSource.TargetTree && !CommitId(targetCommit ?? "")))
            throw new ArgumentException("Use resolved full commit IDs for the snapshot range.");
        var project = AppDataLayout.NormalizeProjectPath(projectPath);
        var paths = (relativePaths ?? throw new ArgumentNullException(nameof(relativePaths)))
            .Select(p => p?.Replace('\\', '/') ?? "").Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToArray();
        try
        {
            var top = await GitAsync(project, ["rev-parse", "--show-toplevel"], cancellationToken);
            if (top.Code != 0) return new(project, "", DateTimeOffset.UtcNow, project, [], GitDiffSnapshotStatus.NotGitWorktree, "not-a-git-worktree");
            var root = Path.GetFullPath(top.Output.Trim());
            var head = await ObservedHead(root, cancellationToken);
            if (expectedHeadCommit is not null && !string.Equals(expectedHeadCommit, head, StringComparison.OrdinalIgnoreCase))
                return new(root, head, DateTimeOffset.UtcNow, project, [], GitDiffSnapshotStatus.NoPathsCaptured, "head-changed-since-scope-resolution");
            if (source == GitDiffSnapshotSource.StagedIndex)
            {
                if (baseCommit is not null && !string.Equals(baseCommit, head, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Staged snapshots compare the index to the observed HEAD.");
                baseCommit = head.Length == 0 ? null : head;
            }
            if (baseCommit is not null && !await IsCommit(root, baseCommit, cancellationToken) ||
                source == GitDiffSnapshotSource.TargetTree && !await IsCommit(root, targetCommit!, cancellationToken))
                return new(root, head, DateTimeOffset.UtcNow, project, [], GitDiffSnapshotStatus.NoPathsCaptured, "invalid-snapshot-commit");
            if (source == GitDiffSnapshotSource.TargetTree && baseCommit is null)
            {
                var parents = await GitAsync(root, ["rev-list", "--parents", "-n", "1", targetCommit!], cancellationToken);
                if (parents.Code != 0 || parents.Output.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).Length != 1)
                    throw new ArgumentException("Only a root commit may omit its explicit base commit.");
            }
            var indexBefore = source == GitDiffSnapshotSource.StagedIndex ? await IndexFingerprint(root, project, cancellationToken) : null;
            var entries = new List<GitDiffSnapshotEntry>();
            foreach (var path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var full = Resolve(root, project, path);
                if (full is null || !AppDataLayout.IsUnderDirectory(full, project))
                {
                    entries.Add(new(path, false, false, false, null, null, new(path, null, null, false, [], "path-or-reparse-refused"), "path-or-reparse-refused"));
                    continue;
                }
                var relative = Path.GetRelativePath(root, full).Replace('\\', '/');
                var before = baseCommit is null ? new GitSourceEntry(null, null, null)
                    : await ReadSourceEntry(root, relative, baseCommit, cancellationToken);
                var after = await ReadSourceEntry(root, relative, source == GitDiffSnapshotSource.StagedIndex ? null : targetCommit, cancellationToken);
                var error = before.Error ?? after.Error;
                byte[]? oldBytes = null, bytes = null;
                if (error is null)
                {
                    var old = await ReadBlob(root, before.Blob, cancellationToken);
                    var next = await ReadBlob(root, after.Blob, cancellationToken);
                    oldBytes = old.Bytes; bytes = next.Bytes; error = old.Error ?? next.Error;
                }
                var diff = error is null ? await DiffCapturedBytes(path, oldBytes, bytes, cancellationToken)
                    : new FileDiff(path, null, null, false, [], error);
                diff = diff with { OldMode = before.Mode, NewMode = after.Mode };
                error ??= diff.Error;
                var stale = false;
                if (source == GitDiffSnapshotSource.StagedIndex)
                    stale = after != await ReadSourceEntry(root, relative, null, cancellationToken);
                entries.Add(new(path, after.Blob is not null, after.Blob is not null, stale,
                    bytes is null ? null : Convert.ToHexString(SHA256.HashData(bytes)), bytes, diff, error));
            }
            var indexAfter = source == GitDiffSnapshotSource.StagedIndex ? await IndexFingerprint(root, project, cancellationToken) : null;
            var changed = head != await ObservedHead(root, cancellationToken) || indexBefore != indexAfter;
            if (changed) entries = entries.Select(e => e with { Stale = true }).ToList();
            return new(root, head, DateTimeOffset.UtcNow, project, entries,
                entries.Count == 0 ? GitDiffSnapshotStatus.NoPathsCaptured : GitDiffSnapshotStatus.Captured,
                entries.Any(e => e.Stale) ? "concurrent-changes-detected" : null)
            { Source = source, BaseCommit = baseCommit, TargetCommit = targetCommit, IndexFingerprint = indexAfter };
        }
        catch (System.ComponentModel.Win32Exception)
        { return new(project, "", DateTimeOffset.UtcNow, project, [], GitDiffSnapshotStatus.NotGitWorktree, "git-unavailable"); }
    }

    private static bool CommitId(string value) => value.Length is 40 or 64 && value.All(Uri.IsHexDigit);
    private static async Task<string> ObservedHead(string root, CancellationToken ct) =>
        (await GitAsync(root, ["rev-parse", "--verify", "HEAD"], ct)).Output.Trim();
    private static async Task<bool> IsCommit(string root, string sha, CancellationToken ct)
    {
        var result = await GitAsync(root, ["rev-parse", "--verify", "--quiet", sha + "^{commit}"], ct);
        return result.Code == 0 && string.Equals(result.Output.Trim(), sha, StringComparison.OrdinalIgnoreCase);
    }
    private static async Task<string?> IndexFingerprint(string root, string project, CancellationToken ct)
    {
        var scope = Path.GetRelativePath(root, project).Replace('\\', '/');
        var result = await GitAsync(root, ["--literal-pathspecs", "ls-files", "--stage", "-z", "--", scope], ct);
        return result.Code == 0 ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(result.Output))) : null;
    }
    private static async Task<GitSourceEntry> ReadSourceEntry(string root, string path, string? tree, CancellationToken ct)
    {
        var result = await GitAsync(root, tree is null
            ? ["--literal-pathspecs", "ls-files", "--stage", "-z", "--", path]
            : ["--literal-pathspecs", "ls-tree", "-z", tree, "--", path], ct);
        if (result.Code != 0) return new(null, null, "git-source-read-failed");
        var rows = result.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (rows.Length == 0) return new(null, null, null);
        if (rows.Length != 1) return new(null, null, "unmerged-or-ambiguous-git-entry");
        var tab = rows[0].IndexOf('\t');
        if (tab < 0 || rows[0][(tab + 1)..] != path) return new(null, null, "ambiguous-git-entry");
        var fields = rows[0][..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 3 || fields[0] is not ("100644" or "100755") ||
            (tree is null ? fields[2] != "0" : fields[1] != "blob")) return new(null, null, "non-regular-or-unmerged-git-entry");
        var blob = fields[tree is null ? 1 : 2];
        return CommitId(blob) ? new(blob, fields[0], null) : new(null, null, "invalid-git-object-id");
    }
    private static async Task<(byte[]? Bytes, string? Error)> ReadBlob(string root, string? blob, CancellationToken ct)
    {
        if (blob is null) return (null, null);
        var size = await GitAsync(root, ["cat-file", "-s", blob], ct);
        if (size.Code != 0 || !long.TryParse(size.Output.Trim(), out var length)) return (null, "git-blob-read-failed");
        if (length > MaximumFileBytes) return (null, "file-too-large");
        var start = new ProcessStartInfo("git") { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "--no-optional-locks", "cat-file", "blob", blob }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        using var cancel = ct.Register(() => { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } });
        var error = process.StandardError.ReadToEndAsync(ct);
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await process.StandardOutput.BaseStream.ReadAsync(buffer, ct)) != 0)
        {
            if (bytes.Length + read > MaximumFileBytes) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(ct); return (null, "file-too-large"); }
            bytes.Write(buffer, 0, read);
        }
        await process.WaitForExitAsync(ct); _ = await error;
        return process.ExitCode == 0 && bytes.Length == length ? (bytes.ToArray(), null) : (null, "git-blob-read-failed");
    }
    private static async Task<FileDiff> DiffCapturedBytes(string path, byte[]? before, byte[]? after, CancellationToken ct)
    {
        if (before is null && after is null) return new(path, null, null, false, [], null);
        var temporary = Directory.CreateTempSubdirectory("fufucode-range-diff-").FullName;
        var empty = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        var oldFile = Path.Combine(temporary, "before"); var newFile = Path.Combine(temporary, "after");
        try
        {
            if (before is not null) await File.WriteAllBytesAsync(oldFile, before, ct);
            if (after is not null) await File.WriteAllBytesAsync(newFile, after, ct);
            var result = await GitAsync(temporary, ["diff", "--no-index", "--no-ext-diff", "--no-textconv", "--no-color", "-U3", "--",
                before is null ? empty : oldFile, after is null ? empty : newFile], ct);
            return result.Code is 0 or 1 ? DiffQuery.Parse(path, result.Output, DiffQuery.MaximumDiffLines)
                : new(path, null, null, false, [], "git-diff-failed");
        }
        finally
        {
            try { File.Delete(oldFile); File.Delete(newFile); Directory.Delete(temporary); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
    private static async Task<bool> CheckGitSourceFreshAsync(FileDiffSnapshot snapshot, CancellationToken ct)
    {
        if (snapshot.Status != GitDiffSnapshotStatus.Captured || snapshot.Entries.Count == 0 ||
            snapshot.Entries.Any(e => e.Stale || e.Error is not null) ||
            snapshot.HeadCommit != await ObservedHead(snapshot.RepositoryRoot, ct)) return false;
        return snapshot.Source switch
        {
            GitDiffSnapshotSource.StagedIndex => snapshot.IndexFingerprint is not null &&
                snapshot.IndexFingerprint == await IndexFingerprint(snapshot.RepositoryRoot, snapshot.ProjectPath, ct),
            GitDiffSnapshotSource.TargetTree => snapshot.TargetCommit is not null &&
                await IsCommit(snapshot.RepositoryRoot, snapshot.TargetCommit, ct),
            _ => false
        };
    }
    internal static async Task<bool> CheckWorkingTreeBytesAsync(FileDiffSnapshot snapshot, CancellationToken ct)
    {
        if (snapshot.Status != GitDiffSnapshotStatus.Captured || snapshot.Entries.Count == 0) return false;
        foreach (var entry in snapshot.Entries)
        {
            if (entry.Stale || entry.Error is not null) return false;
            var full = Resolve(snapshot.RepositoryRoot, snapshot.ProjectPath, entry.RelativePath);
            if (full is null || File.Exists(full) != entry.ExistedAtCapture) return false;
            if (entry.ExistedAtCapture)
            {
                if (entry.Sha256 is null || new FileInfo(full).Length > MaximumFileBytes) return false;
                var bytes = await ReadBoundedFileAsync(full, ct);
                if (bytes is null) return false;
                if (Convert.ToHexString(SHA256.HashData(bytes)) != entry.Sha256) return false;
            }
        }
        return true;
    }
}
