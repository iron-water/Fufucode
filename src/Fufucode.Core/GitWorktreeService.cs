using System.Diagnostics;
using System.Text;

namespace Fufucode.Core;

public sealed record CreatedGitWorktree(string Path, string Branch, string SourceCommit);

/// <summary>One managed checkout as Git itself lists it. <c>Detached</c> means the entry has no
/// branch; <c>Prunable</c> is Git's own "working tree missing" marker, never inferred.</summary>
public sealed record GitWorktreeInfo(string Path, string HeadCommit, string? Branch,
    bool Detached, bool Bare, bool Prunable, string? LockReason);

/// <summary>Read-only inspection result for one worktree. Every failure is a machine-readable
/// reason (<see cref="GitWorktreeInspectReason"/>), never a silently empty success.</summary>
public sealed record GitWorktreeStatus(string Path, GitWorktreeInspectReason Reason,
    string? HeadCommit, string? Branch, bool Detached, IReadOnlyList<string> ChangedPaths,
    string? Error);

public enum GitWorktreeInspectReason
{
    Ok,
    NotGitWorktree,
    PathMissing,
    GitUnavailable,
    CommandFailed,
}

public static class GitWorktreeService
{
    /// <summary>Read-only repository facts used by the local review module: it lists managed
    /// checkouts and one worktree's status without staging, locking, creating, or removing
    /// anything, and never writes to the repository.</summary>
    public static async Task<CreatedGitWorktree> CreateAsync(string projectPath, string destination,
        CancellationToken cancellationToken = default)
    {
        projectPath = AppDataLayout.NormalizeProjectPath(projectPath);
        destination = Path.GetFullPath(destination);
        if (Directory.Exists(destination) || File.Exists(destination))
            throw new InvalidOperationException("The worktree destination already exists.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        var root = (await GitAsync(projectPath, ["rev-parse", "--show-toplevel"], timeout.Token)).Trim();
        var commit = (await GitAsync(root, ["rev-parse", "--verify", "HEAD"], timeout.Token)).Trim();
        var branch = "codex/chat-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await GitAsync(root, ["worktree", "add", "-b", branch, destination, commit], timeout.Token);
        return new(destination, branch, commit);
    }

    /// <summary>Lists the managed checkouts of the repository containing <paramref name="projectPath"/>.
    /// Paths are read raw (<c>--porcelain -z</c>), so spaces, Unicode, and newlines inside a path
    /// survive without shell quoting. A non-Git directory reports NotGitWorktree; a missing git
    /// binary reports GitUnavailable.</summary>
    public static async Task<GitWorktreeListResult> ListAsync(string projectPath,
        CancellationToken cancellationToken = default)
    {
        var project = TryNormalize(projectPath);
        if (project is null) return new([], GitWorktreeInspectReason.PathMissing, "project-path-missing");
        var (code, output, error) = await RunAsync(project,
            ["worktree", "list", "--porcelain", "-z"], cancellationToken);
        if (code == -1) return new([], GitWorktreeInspectReason.GitUnavailable, "git-unavailable");
        if (code != 0) return new([], GitWorktreeInspectReason.CommandFailed, Truncate(error));
        // With -z, Git separates porcelain fields with NUL and records with an empty field.
        var records = output.Split("\0\0", StringSplitOptions.RemoveEmptyEntries);
        var items = new List<GitWorktreeInfo>(records.Length);
        foreach (var record in records)
        {
            string? path = null, head = null, branch = null, lockReason = null;
            var detached = false; var bare = false; var prunable = false;
            foreach (var field in record.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                if (field.StartsWith("worktree ", StringComparison.Ordinal)) path = field["worktree ".Length..];
                else if (field.StartsWith("HEAD ", StringComparison.Ordinal)) head = field["HEAD ".Length..].Trim();
                else if (field.StartsWith("branch ", StringComparison.Ordinal))
                {
                    var full = field["branch ".Length..].Trim();
                    branch = full.StartsWith("refs/heads/", StringComparison.Ordinal)
                        ? full["refs/heads/".Length..] : full;
                }
                else if (field == "detached") detached = true;
                else if (field == "bare") bare = true;
                else if (field == "prunable") prunable = true;
                else if (field.StartsWith("prunable ", StringComparison.Ordinal))
                { prunable = true; lockReason ??= field["prunable ".Length..]; }
                else if (field.StartsWith("locked ", StringComparison.Ordinal)) lockReason = field["locked ".Length..];
            }
            if (path is null || head is null) continue;
            items.Add(new(Path.GetFullPath(path), head, branch, detached, bare, prunable, lockReason));
        }
        if (items.Count == 0) return new([], GitWorktreeInspectReason.NotGitWorktree, "empty-worktree-list");
        return new(items, GitWorktreeInspectReason.Ok, null);
    }

    /// <summary>Read-only status of one worktree: HEAD/branch plus the changed-path list from
    /// <c>status --porcelain=v1 -z</c> (raw paths, rename source folded away). Production
    /// worktrees are inspected, never removed or cleaned here.</summary>
    public static async Task<GitWorktreeStatus> StatusAsync(string worktreePath,
        CancellationToken cancellationToken = default)
    {
        var worktree = TryNormalize(worktreePath);
        if (worktree is null)
            return new(TrimEnding(worktreePath), GitWorktreeInspectReason.PathMissing,
                null, null, false, [], "path-missing");
        var top = await RunAsync(worktree, ["rev-parse", "--show-toplevel"], cancellationToken);
        if (top.Code == -1)
            return new(worktree, GitWorktreeInspectReason.GitUnavailable, null, null, false, [], "git-unavailable");
        if (top.Code != 0)
            return new(worktree, GitWorktreeInspectReason.NotGitWorktree, null, null, false, [], "not-a-git-worktree");
        var root = Path.GetFullPath(top.Output.Trim());
        var head = await RunAsync(root, ["rev-parse", "--verify", "-q", "HEAD"], cancellationToken);
        var headCommit = head.Code == 0 ? head.Output.Trim() : null;
        var branch = await RunAsync(root, ["rev-parse", "--abbrev-ref", "HEAD"], cancellationToken);
        var branchName = branch.Code == 0 ? branch.Output.Trim() : null;
        if (branchName == "HEAD") branchName = null; // detached
        var status = await RunAsync(root,
            ["status", "--porcelain=v1", "-z", "--untracked-files=all"], cancellationToken);
        if (status.Code != 0)
            return new(worktree, GitWorktreeInspectReason.CommandFailed, headCommit, branchName,
                branchName is null, [], Truncate(status.Error));
        var changed = new List<string>();
        var parts = status.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            var item = parts[i];
            if (item.Length < 4) continue;
            var path = item[3..];
            changed.Add(path);
            // R/C records are followed by their source path as a separate NUL field.
            if ((item[0] is 'R' or 'C' || item[1] is 'R' or 'C') && i + 1 < parts.Length) i++;
        }
        return new(worktree, GitWorktreeInspectReason.Ok, headCommit, branchName,
            branchName is null, changed, null);
    }

    private static string? TryNormalize(string path)
    {
        try { return AppDataLayout.NormalizeProjectPath(path); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException
            or DirectoryNotFoundException or System.Security.SecurityException or PathTooLongException)
        { return null; }
    }

    private static string TrimEnding(string path)
    {
        try { return Path.TrimEndingDirectorySeparator(path); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException) { return path; }
    }

    private static string Truncate(string? value) =>
        string.IsNullOrEmpty(value) ? "command-failed" :
        value.Length <= 400 ? value : value[..400] + "…";

    private static async Task<(int Code, string Output, string Error)> RunAsync(string directory,
        string[] arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("git")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
        };
        // Same read-only discipline as GitDiffSnapshot: no optional index locks, no fsmonitor.
        foreach (var arg in new[] { "--no-optional-locks", "-C", directory,
            "-c", "core.fsmonitor=false", "-c", "core.quotepath=false" }.Concat(arguments))
            start.ArgumentList.Add(arg);
        try
        {
            using var process = Process.Start(start) ?? throw new IOException("Could not start git.");
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = process.StandardError.ReadToEndAsync(cancellationToken);
            try { await process.WaitForExitAsync(cancellationToken); }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                throw;
            }
            var text = await output;
            var detail = await error;
            return (process.ExitCode, text, detail);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (-1, "", "git-unavailable");
        }
    }

    /// <summary>List outcome: items are only meaningful when <see cref="Reason"/> is Ok.</summary>
    public sealed record GitWorktreeListResult(IReadOnlyList<GitWorktreeInfo> Worktrees,
        GitWorktreeInspectReason Reason, string? Error);

    private static async Task<string> GitAsync(string directory, string[] arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Git did not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(cancellationToken); }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
        var text = await output;
        var detail = await error;
        if (process.ExitCode != 0) throw new InvalidOperationException("Git worktree failed: " + detail.Trim());
        return text;
    }
}
