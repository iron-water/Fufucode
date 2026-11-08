using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Fufucode.Core.Automation;

public enum ManagedWorkspaceStatus
{
    Ready, NeedsRepositoryAuthorization, BaselineDirty, CandidateReady, Integrated,
    Cancelled, Failed, Removed
}

public sealed record ManagedWorkspaceManifest(
    string RepositoryPath, string BaseHead, string WorktreePath, string? Branch,
    string PatchPath, string[] ChangedFiles, string PatchHash, long PatchSize,
    ManagedWorkspaceStatus Status, string? Error = null, string? CandidateCommit = null, string? CandidateRef = null);

public sealed record ManagedWorkspaceCreation(
    ManagedWorkspaceStatus Status, ManagedDevelopmentWorkspace? Workspace, string? Error = null);

/// <summary>
/// Controlled Git workspaces, independent of optional execution plugins. This is not a permission
/// boundary: the host MUST obtain approval through its permission workflow before calling creation,
/// integration or cleanup. An approval flag records that decision; user/model text is not authority.
/// No commands supplied by a model are executed. Cancellation never removes a worktree or candidate.
/// Instances serialize their own operations; the host must also exclude concurrent repository writers
/// during creation/integration (Git cannot atomically compare HEAD, worktree contents and apply).
/// </summary>
public sealed class ManagedDevelopmentWorkspace
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string artifactDirectory;
    public ManagedWorkspaceManifest Manifest { get; private set; }
    public string ManifestPath => Path.Combine(artifactDirectory, "manifest.json");

    private ManagedDevelopmentWorkspace(string artifacts, ManagedWorkspaceManifest manifest)
        => (artifactDirectory, Manifest) = (artifacts, manifest);

    /// <summary>
    /// Reopens retained state using read-only Git identity checks. The host must select a manifest
    /// from its trusted application sandbox; manifest contents never grant permission. Does not
    /// apply, capture, save, initialize or delete anything. Integration still needs fresh approval.
    /// Hashes detect corruption, not malicious replacement of both manifest and patch.
    /// </summary>
    public static async Task<ManagedDevelopmentWorkspace> LoadAsync(string manifestPath,
        CancellationToken cancellationToken = default)
    {
        var path = Path.GetFullPath(manifestPath);
        var artifacts = Path.GetDirectoryName(path)!;
        if (!SamePath(path, Path.Combine(artifacts, "manifest.json")))
            throw new InvalidOperationException("Expected the managed manifest.json path.");
        RejectLinks(artifacts);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Manifest cannot be a link.");
        var manifest = JsonSerializer.Deserialize<ManagedWorkspaceManifest>(
            await File.ReadAllTextAsync(path, cancellationToken))
            ?? throw new InvalidOperationException("Manifest is empty.");
        if (!Enum.IsDefined(manifest.Status) || manifest.Status is ManagedWorkspaceStatus.Removed
            or ManagedWorkspaceStatus.NeedsRepositoryAuthorization or ManagedWorkspaceStatus.BaselineDirty)
            throw new InvalidOperationException("Manifest does not describe a retained workspace.");
        if (!Path.IsPathFullyQualified(manifest.RepositoryPath) ||
            !Path.IsPathFullyQualified(manifest.WorktreePath) || !Path.IsPathFullyQualified(manifest.PatchPath) ||
            !SamePath(manifest.WorktreePath, Path.Combine(artifacts, "tree")) ||
            !SamePath(manifest.PatchPath, Path.Combine(artifacts, "candidate.patch")) ||
            IsWithin(artifacts, manifest.RepositoryPath) || IsWithin(manifest.RepositoryPath, artifacts))
            throw new InvalidOperationException("Manifest paths violate managed workspace layout.");
        RejectLinks(manifest.RepositoryPath);
        RejectLinks(manifest.WorktreePath);
        foreach (var directory in new[] { manifest.RepositoryPath, manifest.WorktreePath })
            if (!SamePath((await Git(directory, ["rev-parse", "--show-toplevel"], cancellationToken)).Text.Trim(), directory))
                throw new InvalidOperationException("Manifest Git root identity mismatch.");
        var repoCommon = (await Git(manifest.RepositoryPath,
            ["rev-parse", "--path-format=absolute", "--git-common-dir"], cancellationToken)).Text.Trim();
        var treeCommon = (await Git(manifest.WorktreePath,
            ["rev-parse", "--path-format=absolute", "--git-common-dir"], cancellationToken)).Text.Trim();
        if (!SamePath(repoCommon, treeCommon))
            throw new InvalidOperationException("Worktree belongs to a different repository.");
        var gitDirectory = (await Git(manifest.WorktreePath,
            ["rev-parse", "--absolute-git-dir"], cancellationToken)).Text.Trim();
        var registeredTree = Path.Combine(gitDirectory, "gitdir");
        if (!File.Exists(registeredTree) ||
            !SamePath((await File.ReadAllTextAsync(registeredTree, cancellationToken)).Trim(), Path.Combine(manifest.WorktreePath, ".git")))
            throw new InvalidOperationException("Worktree registration mismatch.");
        if (manifest.BaseHead is null || manifest.BaseHead.Length is not (40 or 64) ||
            !manifest.BaseHead.All(Uri.IsHexDigit) ||
            (await Git(manifest.RepositoryPath, ["rev-parse", "--verify", manifest.BaseHead + "^{commit}"], cancellationToken)).Text.Trim() != manifest.BaseHead)
            throw new InvalidOperationException("Invalid fixed base commit.");
        if ((await Git(manifest.WorktreePath, ["merge-base", "--is-ancestor", manifest.BaseHead, "HEAD"],
            cancellationToken, allowFailure: true)).Code != 0)
            throw new InvalidOperationException("Worktree HEAD is not based on the fixed baseline.");
        var branch = (await Git(manifest.WorktreePath, ["symbolic-ref", "--quiet", "--short", "HEAD"],
            cancellationToken, allowFailure: true));
        if ((manifest.Branch is null && branch.Code == 0) ||
            (manifest.Branch is not null && (branch.Code != 0 || branch.Text.Trim() != manifest.Branch)))
            throw new InvalidOperationException("Worktree branch identity mismatch.");
        if (manifest.ChangedFiles is null || manifest.ChangedFiles.Any(name =>
            string.IsNullOrEmpty(name) || Path.IsPathRooted(name) || name.Split('/','\\').Contains("..")))
            throw new InvalidOperationException("Invalid changed file paths.");
        if (!string.IsNullOrEmpty(manifest.PatchHash))
        {
            if ((File.GetAttributes(manifest.PatchPath) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Candidate cannot be a link.");
            var bytes = await File.ReadAllBytesAsync(manifest.PatchPath, cancellationToken);
            if (manifest.PatchSize != bytes.LongLength || manifest.PatchHash != Hash(bytes))
                throw new InvalidOperationException("Candidate patch integrity mismatch.");
        }
        else if (manifest.PatchSize != 0 || manifest.ChangedFiles.Length != 0 ||
                 manifest.Status is ManagedWorkspaceStatus.CandidateReady or ManagedWorkspaceStatus.Integrated || File.Exists(manifest.PatchPath))
            throw new InvalidOperationException("Manifest candidate metadata is inconsistent.");
        return new ManagedDevelopmentWorkspace(artifacts, manifest);
    }

    /// <summary>Requires an exact Git root and a clean tracked/untracked baseline. Never initializes Git.</summary>
    public static Task<ManagedWorkspaceCreation> CreateAsync(string repositoryPath,
        string applicationSandboxRoot, string? newBranch = null, CancellationToken cancellationToken = default)
        => CreateCoreAsync(repositoryPath, applicationSandboxRoot, null, newBranch, cancellationToken);

    public static Task<ManagedWorkspaceCreation> CreateFromBaseAsync(string repositoryPath,
        string applicationSandboxRoot, string baseCommit, string? newBranch = null, CancellationToken cancellationToken = default)
        => CreateCoreAsync(repositoryPath, applicationSandboxRoot, baseCommit, newBranch, cancellationToken);

    private static async Task<ManagedWorkspaceCreation> CreateCoreAsync(string repositoryPath,
        string applicationSandboxRoot, string? baseCommit, string? newBranch, CancellationToken cancellationToken)
    {
        var repo = Path.GetFullPath(repositoryPath);
        var root = Path.GetFullPath(applicationSandboxRoot);
        if (!Directory.Exists(repo))
            return new(ManagedWorkspaceStatus.NeedsRepositoryAuthorization, null, "Repository directory does not exist; host authorization is required.");
        var probe = await Git(repo, ["rev-parse", "--show-toplevel"], cancellationToken, allowFailure: true);
        if (probe.Code != 0 || !SamePath(probe.Text.Trim(), repo))
            return new(ManagedWorkspaceStatus.NeedsRepositoryAuthorization, null, "An exact existing Git root is required; repository initialization requires separate host authorization.");
        if (baseCommit is null && (await Git(repo, ["status", "--porcelain=v1", "-z", "--untracked-files=all"], cancellationToken)).Bytes.Length != 0)
            return new(ManagedWorkspaceStatus.BaselineDirty, null, "Repository contains user changes; no baseline was created.");
        if (IsWithin(root, repo) || IsWithin(repo, root))
            throw new ArgumentException("Application sandbox and repository must be disjoint.", nameof(applicationSandboxRoot));
        RejectLinks(root);
        if (newBranch is not null)
            await Git(repo, ["check-ref-format", "--branch", newBranch], cancellationToken);
        if (baseCommit is not null && (baseCommit.Length is not (40 or 64) || !baseCommit.All(Uri.IsHexDigit)))
            throw new ArgumentException("An explicit immutable commit SHA is required.", nameof(baseCommit));
        var head = (await Git(repo, ["rev-parse", "--verify", (baseCommit ?? "HEAD") + "^{commit}"], cancellationToken)).Text.Trim();
        var artifacts = Path.Combine(root, "workspace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(artifacts);
        var tree = Path.Combine(artifacts, "tree");
        var workspace = new ManagedDevelopmentWorkspace(artifacts,
            new(repo, head, tree, newBranch, Path.Combine(artifacts, "candidate.patch"), [], "", 0, ManagedWorkspaceStatus.Ready));
        await workspace.Save();
        try
        {
            string[] args = newBranch is null
                ? ["worktree", "add", "--detach", "--", tree, head]
                : ["worktree", "add", "-b", newBranch, "--", tree, head];
            await Git(repo, args, cancellationToken);
            // Refuse a baseline that changed while worktree creation was underway; preserve the tree.
            if (baseCommit is null && ((await Git(repo, ["rev-parse", "HEAD"], cancellationToken)).Text.Trim() != head ||
                (await Git(repo, ["status", "--porcelain=v1", "-z", "--untracked-files=all"], cancellationToken)).Bytes.Length != 0))
                throw new InvalidOperationException("Source baseline changed during creation; isolated worktree retained.");
            return new(ManagedWorkspaceStatus.Ready, workspace);
        }
        catch (Exception ex)
        {
            await workspace.Fail(ex);
            return new(workspace.Manifest.Status, workspace, ex.Message);
        }
    }

    /// <summary>Snapshot HEAD-relative tracked and untracked changes, including binary data, using a private index.</summary>
    public async Task<ManagedWorkspaceManifest> CaptureCandidateAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try { return await Capture(cancellationToken); }
        catch (Exception ex) { await Fail(ex); throw; }
        finally { gate.Release(); }
    }

    private async Task<ManagedWorkspaceManifest> Capture(CancellationToken token)
    {
        EnsurePresent();
        var index = Path.Combine(artifactDirectory, "index-" + Guid.NewGuid().ToString("N"));
        try
        {
            await Git(Manifest.WorktreePath, ["read-tree", Manifest.BaseHead], token, index);
            await Git(Manifest.WorktreePath, ["add", "-A", "--", "."], token, index);
            var patch = await Git(Manifest.WorktreePath,
                ["diff", "--cached", "--binary", "--full-index", "--src-prefix=a/", "--dst-prefix=b/", "--no-ext-diff", "--no-textconv", Manifest.BaseHead, "--"], token, index);
            var names = await Git(Manifest.WorktreePath,
                ["diff", "--cached", "--name-only", "-z", Manifest.BaseHead, "--"], token, index);
            var temporary = Manifest.PatchPath + ".tmp";
            await File.WriteAllBytesAsync(temporary, patch.Bytes, token);
            File.Move(temporary, Manifest.PatchPath, true);
            var tree = (await Git(Manifest.WorktreePath, ["write-tree"], token, index)).Text.Trim();
            var commit = (await Git(Manifest.WorktreePath,
                ["-c", "user.name=AutoDev", "-c", "user.email=autodev@localhost", "commit-tree", tree, "-p", Manifest.BaseHead, "-m", "AutoDev candidate " + Hash(patch.Bytes)], token)).Text.Trim();
            var candidateRef = "refs/autodev/candidates/" + Path.GetFileName(artifactDirectory) + "/" + commit;
            await Git(Manifest.RepositoryPath, ["update-ref", candidateRef, commit], token);
            Manifest = Manifest with { CandidateCommit = commit, CandidateRef = candidateRef, ChangedFiles = names.Text.Split('\0', StringSplitOptions.RemoveEmptyEntries),
                PatchHash = Hash(patch.Bytes), PatchSize = patch.Bytes.LongLength,
                Status = ManagedWorkspaceStatus.CandidateReady, Error = null };
            await Save();
            return Manifest;
        }
        finally
        {
            if (File.Exists(index)) File.Delete(index);
            if (File.Exists(index + ".lock")) File.Delete(index + ".lock");
        }
    }

    /// <summary>
    /// Legacy compatibility entry point. Direct patch application to a user's working tree is forbidden.
    /// Use ManagedGitIntegration to prepare, validate and commit in isolation; publication is separate.
    /// </summary>
    public Task<ManagedWorkspaceManifest> IntegrateAsync(bool hostPermissionGranted,
        string approvedPatchHash, CancellationToken cancellationToken = default)
    {
        if (!hostPermissionGranted) throw new UnauthorizedAccessException("Host permission workflow approval required.");
        if (Manifest.PatchHash != approvedPatchHash)
            throw new InvalidOperationException("Approval must identify the current candidate.");
        throw new InvalidOperationException("Direct integration is disabled. Use ManagedGitIntegration.PrepareAsync, validate its worktree, then CommitAsync; publish separately.");
    }

    /// <summary>
    /// Explicit cleanup only. Refuses dirty (including ignored) files and any commits not contained in
    /// the target. Does not discard delivered changes: callers must first reconcile the tree themselves.
    /// Artifacts and branch are retained. Never uses force or recursive directory deletion.
    /// </summary>
    public async Task<ManagedWorkspaceManifest> RemoveAsync(bool hostPermissionGranted,
        CancellationToken cancellationToken = default)
    {
        if (!hostPermissionGranted) throw new UnauthorizedAccessException("Host cleanup approval required.");
        await gate.WaitAsync(cancellationToken);
        try
        {
            EnsurePresent();
            if ((await Git(Manifest.WorktreePath, ["status", "--porcelain=v1", "-z", "--untracked-files=all", "--ignored"], cancellationToken)).Bytes.Length != 0)
                throw new InvalidOperationException("Worktree has differences or ignored files; retained.");
            var result = await Git(Manifest.RepositoryPath,
                ["merge-base", "--is-ancestor", (await Git(Manifest.WorktreePath, ["rev-parse", "HEAD"], cancellationToken)).Text.Trim(), "HEAD"],
                cancellationToken, allowFailure: true);
            if (result.Code != 0) throw new InvalidOperationException("Worktree has undelivered commits; retained.");
            await Git(Manifest.RepositoryPath, ["worktree", "remove", "--", Manifest.WorktreePath], cancellationToken);
            Manifest = Manifest with { Status = ManagedWorkspaceStatus.Removed, Error = null };
            await Save();
            return Manifest;
        }
        catch (Exception ex) { await Fail(ex); throw; }
        finally { gate.Release(); }
    }

    private void EnsurePresent()
    {
        if (Manifest.Status == ManagedWorkspaceStatus.Removed || !Directory.Exists(Manifest.WorktreePath))
            throw new InvalidOperationException("Managed worktree is not present.");
    }
    private async Task Fail(Exception ex)
    {
        Manifest = Manifest with { Status = ex is OperationCanceledException ? ManagedWorkspaceStatus.Cancelled : ManagedWorkspaceStatus.Failed, Error = ex.Message };
        await Save();
    }
    private async Task Save()
    {
        var temp = ManifestPath + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(Manifest, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, ManifestPath, true);
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static bool SamePath(string a, string b) => string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
        Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static bool IsWithin(string path, string parent) => SamePath(path, parent) ||
        Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)) + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static void RejectLinks(string path)
    {
        for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("Sandbox ancestors cannot be symbolic links or junctions.");
    }
    internal sealed record GitResult(int Code, byte[] Bytes)
    { public string Text => Encoding.UTF8.GetString(Bytes); }

    internal static async Task<GitResult> Git(string cwd, string[] arguments, CancellationToken token,
        string? index = null, bool allowFailure = false, byte[]? input = null)
    {
        token.ThrowIfCancellationRequested();
        var info = new ProcessStartInfo("git") { WorkingDirectory = cwd, UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            RedirectStandardInput = input is not null };
        foreach (var key in info.Environment.Keys.Where(k => k.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray())
            info.Environment.Remove(key);
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        info.Environment["GIT_LITERAL_PATHSPECS"] = "1";
        if (index is not null) info.Environment["GIT_INDEX_FILE"] = index;
        info.ArgumentList.Add("--no-optional-locks");
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new IOException("Could not start Git.");
        using var output = new MemoryStream();
        var read = process.StandardOutput.BaseStream.CopyToAsync(output);
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            if (input is not null)
            {
                await process.StandardInput.BaseStream.WriteAsync(input, token);
                process.StandardInput.Close();
            }
            await process.WaitForExitAsync(token);
            await read;
            var stderr = await error;
            if (process.ExitCode != 0 && !allowFailure) throw new IOException($"Git {arguments[0]} failed ({process.ExitCode}): {stderr.Trim()}");
            return new(process.ExitCode, output.ToArray());
        }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(read, error);
            throw;
        }
    }
}
