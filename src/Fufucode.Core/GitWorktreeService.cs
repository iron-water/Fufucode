using System.Diagnostics;

namespace Fufucode.Core;

public sealed record CreatedGitWorktree(string Path, string Branch, string SourceCommit);

public static class GitWorktreeService
{
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
