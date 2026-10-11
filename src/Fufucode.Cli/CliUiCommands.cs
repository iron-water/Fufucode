using System.Diagnostics;

namespace Fufucode.Cli;

/// <summary>CLI stays independent of Avalonia; the existing App owns headless controls.</summary>
public static class CliUiCommands
{
    public static async Task<int> RunAsync(IReadOnlyList<string> arguments, TextWriter output, TextWriter errors,
        CancellationToken cancellationToken = default)
    {
        var args = arguments.ToList(); string? app = null;
        TakeOption("--ui-app", value => app = Path.GetFullPath(value));
        string? page = null;
        TakeOption("--page", value => page = value);
        if (page is not null and not ("chat" or "extensions" or "settings")) throw new ArgumentException("Unknown preview page.");
        if (!(args.Count == 1 && args[0] == "snapshot" || args.Count == 2 && args[0] is "invoke" or "screenshot"))
            throw new ArgumentException("Usage: ui snapshot | screenshot <new.png> | invoke nav.<page> [--page <page>] [--ui-app PATH].");
        if (args[0] == "invoke" && args[1] is not ("nav.chat" or "nav.extensions" or "nav.settings"))
            throw new ArgumentException("Only nav.chat, nav.extensions and nav.settings can be invoked in preview.");
        if (args[0] == "screenshot")
        {
            args[1] = Path.GetFullPath(args[1]);
            if (!args[1].EndsWith(".png", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Screenshot target must be a new .png file.");
            if (File.Exists(args[1]) || Directory.Exists(args[1])) throw new IOException("Screenshot target already exists.");
        }
        app ??= ResolveApp();
        if (!File.Exists(app)) throw new FileNotFoundException("Headless App was not found. Supply --ui-app PATH.");
        var start = new ProcessStartInfo(app) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--headless-ui");
        foreach (var arg in args) start.ArgumentList.Add(arg);
        if (page is not null) { start.ArgumentList.Add("--page"); start.ArgumentList.Add(page); }
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Headless App could not start.");
        var stdout = PumpAsync(process.StandardOutput, output);
        var stderr = PumpAsync(process.StandardError, errors);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(); await Task.WhenAll(stdout, stderr);
            if (!cancellationToken.IsCancellationRequested) throw new TimeoutException("Headless App did not finish within 30 seconds.");
            throw;
        }
        await Task.WhenAll(stdout, stderr);
        return process.ExitCode;

        void TakeOption(string name, Action<string> take)
        {
            var index = args.IndexOf(name);
            if (index < 0) return;
            if (index + 1 >= args.Count) throw new ArgumentException("Missing value for " + name);
            take(args[index + 1]); args.RemoveRange(index, 2);
            if (args.Contains(name)) throw new ArgumentException("Duplicate option: " + name);
        }
    }

    private static async Task PumpAsync(StreamReader source, TextWriter target)
    {
        while (await source.ReadLineAsync() is { } line) { await target.WriteLineAsync(line); await target.FlushAsync(); }
    }

    private static string ResolveApp()
    {
        var name = OperatingSystem.IsWindows() ? "Fufucode.App.exe" : "Fufucode.App";
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "app", name);
            if (File.Exists(path)) return path;
        }
        // Development binaries share the source root, configuration and TFM.
        var binary = new DirectoryInfo(AppContext.BaseDirectory);
        var configuration = binary.Parent?.Name;
        var source = binary.Parent?.Parent?.Parent?.Parent;
        if (source is not null && configuration is not null)
        {
            var path = Path.Combine(source.FullName, "Fufucode.App", "bin", configuration, binary.Name, name);
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException("Headless App was not found. Supply --ui-app PATH.");
    }
}
