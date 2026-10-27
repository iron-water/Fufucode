using System.Diagnostics;
using System.Globalization;

namespace Fufucode.Cli;

/// <summary>
/// 无头 UI 控制入口。CLI 不引用 Avalonia；真正的控件树与自动化操作由 App 的
/// --headless-ui 进程执行。业务动作和控件操作一律要求 --allow-action。
/// </summary>
public static class CliUiCommands
{
    private static readonly string[] ValueOptions =
    [
        "--page", "--skin", "--theme", "--settings-category", "--settings-page",
        "--settings-scroll", "--settings-anchor", "--shortcuts-group", "--results"
    ];

    public static async Task<int> RunAsync(IReadOnlyList<string> arguments, TextWriter output, TextWriter errors,
        CancellationToken cancellationToken = default)
    {
        var args = arguments.ToList(); string? app = null;
        TakeOption("--ui-app", value => app = Path.GetFullPath(value));
        int? timeoutSeconds = null;
        TakeOption("--timeout", value =>
        {
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) ||
                seconds is < 1 or > 600)
                throw new ArgumentException("--timeout must be a whole number of seconds between 1 and 600.");
            timeoutSeconds = seconds;
        });

        string? page = null;
        var positionals = new List<string>();
        var options = new List<string>();
        for (var index = 0; index < args.Count; index++)
        {
            var token = args[index];
            if (token is "--allow-action" or "--perf-turn") { options.Add(token); continue; }
            if (ValueOptions.Contains(token))
            {
                if (index + 1 >= args.Count) throw new ArgumentException("Missing value for " + token);
                var value = args[++index];
                if (token == "--page") page = value;
                if (token == "--settings-scroll" &&
                    !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                    throw new ArgumentException("Invalid --settings-scroll value.");
                options.Add(token); options.Add(value);
                continue;
            }
            if (token.StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException("Unknown ui option: " + token);
            positionals.Add(token);
        }

        if (page is not null and not ("chat" or "extensions" or "settings"))
            throw new ArgumentException("Unknown preview page.");
        if (positionals.Count == 0) throw Usage();
        var command = positionals[0];
        var expected = command switch
        {
            "snapshot" => 1,
            "screenshot" or "invoke" or "click" or "focus" or "toggle" => 2,
            "set" => 3,
            _ => -1
        };
        if (positionals.Count != expected) throw Usage();

        if (command == "screenshot")
        {
            var path = Path.GetFullPath(positionals[1]);
            if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Screenshot target must be a new .png file.");
            if (File.Exists(path) || Directory.Exists(path))
                throw new IOException("Screenshot target already exists.");
        }

        var allowAction = options.Contains("--allow-action");
        if (command == "invoke" && !allowAction &&
            positionals[1] is not ("nav.chat" or "nav.extensions" or "nav.settings"))
            throw new ArgumentException(
                "Only nav.chat, nav.extensions and nav.settings can be invoked without --allow-action.");
        if (command is "click" or "focus" or "toggle" or "set" && !allowAction)
            throw new ArgumentException("Control actions require --allow-action.");

        app ??= ResolveApp();
        if (!File.Exists(app)) throw new FileNotFoundException("Headless App was not found. Supply --ui-app PATH.");
        var start = new ProcessStartInfo(app) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--headless-ui");
        // App 的解析器要求命令在第一位；选项可以跟随其后。
        foreach (var value in positionals) start.ArgumentList.Add(value);
        foreach (var value in options) start.ArgumentList.Add(value);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Headless App could not start.");
        var started = Stopwatch.GetTimestamp();
        var stdout = PumpAsync(process.StandardOutput, output);
        var stderr = PumpAsync(process.StandardError, errors);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var limit = timeoutSeconds ?? 60;
        deadline.CancelAfter(TimeSpan.FromSeconds(limit));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(); await Task.WhenAll(stdout, stderr);
            if (!cancellationToken.IsCancellationRequested)
                throw new TimeoutException($"Headless App did not finish within {limit} seconds.");
            throw;
        }
        await Task.WhenAll(stdout, stderr);
        if (process.ExitCode != 0)
            await errors.WriteLineAsync($"Headless App exited with code {process.ExitCode} after " +
                $"{Stopwatch.GetElapsedTime(started).TotalMilliseconds:N0} ms.");
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

    private static ArgumentException Usage() => new(
        "Usage: ui snapshot | screenshot <new.png> | invoke <action> [--allow-action] | " +
        "click|focus|toggle <automation-id> | set <automation-id> <value> " +
        "[--page chat|extensions|settings] [--skin NAME] [--theme NAME] " +
        "[--settings-category ID] [--settings-page ID] [--settings-scroll N] " +
        "[--settings-anchor TEXT] [--shortcuts-group TEXT] [--results DIR] [--timeout SEC].");

    private static async Task PumpAsync(StreamReader source, TextWriter target)
    {
        while (await source.ReadLineAsync() is { } line) { await target.WriteLineAsync(line); await target.FlushAsync(); }
    }

    internal static string ResolveApp()
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
