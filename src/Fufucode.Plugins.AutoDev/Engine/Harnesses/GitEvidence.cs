using System.Diagnostics;
using System.Text;

namespace Fufucode.Core.Automation.Harnesses;

/// <summary>Observed repository facts used by reconciliation and the receiving gate.</summary>
public interface IGitEvidence
{
    Task<string?> HeadAsync(string worktree, CancellationToken cancellationToken = default);
    Task<bool?> IsCleanAsync(string worktree, CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads the real repository. Uses --no-optional-locks so inspection never creates an
/// index lock in a worktree a managed writer may still be using.
/// </summary>
public sealed class GitCliEvidence : IGitEvidence
{
    public static readonly GitCliEvidence Shared = new();

    public async Task<string?> HeadAsync(string worktree, CancellationToken cancellationToken = default)
    {
        var (code, output) = await RunAsync(worktree,
            ["rev-parse", "--verify", "-q", "HEAD"], cancellationToken).ConfigureAwait(false);
        if (code != 0) return null;
        var sha = output.Trim();
        return IsSha(sha) ? sha.ToLowerInvariant() : null;
    }

    public async Task<bool?> IsCleanAsync(string worktree, CancellationToken cancellationToken = default)
    {
        // Ignored and untracked material is reported separately; untracked files are part of a
        // delivery, so only tracked modifications make the recorded HEAD an incomplete claim.
        var (code, output) = await RunAsync(worktree,
            ["status", "--porcelain", "--untracked-files=no"], cancellationToken).ConfigureAwait(false);
        if (code != 0) return null;
        return string.IsNullOrWhiteSpace(output);
    }

    public static bool IsSha(string value) =>
        value.Length is 40 or 64 && value.All(Uri.IsHexDigit);

    private static async Task<(int Code, string Output)> RunAsync(string directory, string[] args,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directory)) return (-1, "");
        var start = new ProcessStartInfo("git")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false)
        };
        foreach (var arg in new[] { "--no-optional-locks", "-C", directory, "-c", "core.fsmonitor=false" }.Concat(args))
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start);
        if (process is null) return (-1, "");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            return (-1, "");
        }
        await output.ConfigureAwait(false);
        return (process.ExitCode, await output.ConfigureAwait(false));
    }
}
