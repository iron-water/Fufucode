using System.Text;

namespace Fufucode.Core.Features.LocalReview;

/// <summary>
/// Read-only git subprocess runner shared by this module. Same discipline as the integrated
/// snapshot contract: argument lists (never a shell string built from input), no optional index
/// locks, no fsmonitor, raw (unquoted) paths, and a machine-readable failure code when git is
/// unavailable or times out instead of a hung or fake-successful call.
/// </summary>
internal static class ReviewGit
{
    public const int GitMissing = -1;
    public const int GitTimeout = -2;

    public static async Task<(int Code, string Output, string Error)> RunAsync(string directory,
        IEnumerable<string> arguments, CancellationToken cancellationToken, int timeoutSeconds = 30)
    {
        var start = new System.Diagnostics.ProcessStartInfo("git")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
        };
        foreach (var arg in new[] { "--no-optional-locks", "-C", directory,
            "-c", "core.fsmonitor=false", "-c", "core.quotepath=false" }.Concat(arguments))
            start.ArgumentList.Add(arg);
        try
        {
            using var process = System.Diagnostics.Process.Start(start)
                ?? throw new IOException("Could not start git.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                // A caller cancellation propagates untouched; our own timeout is a command failure.
                if (cancellationToken.IsCancellationRequested) throw;
                return (GitTimeout, "", "git-timeout");
            }
            var outText = await output;
            var errText = await error;
            return (process.ExitCode, outText, errText);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (GitMissing, "", "git-unavailable");
        }
    }

    public static string? NormalizeOrNone(string path)
    {
        try { return AppDataLayout.NormalizeProjectPath(path); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException
            or DirectoryNotFoundException or System.Security.SecurityException or PathTooLongException)
        { return null; }
    }

    public static string Truncate(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "command-failed" :
        value.Length <= 400 ? value.ReplaceLineEndings(" ") : value[..400];
}
