using System.Diagnostics;
using Fufucode.Core.Automation;
using Fufucode.Core.Automation.Harnesses;

namespace Fufucode.Conv.Tests;

/// <summary>
/// One isolated CONV scenario: a real git repository (both as the project root and as the reviewed
/// worktree), its own SQLite project database and evidence directories. Everything lives under a
/// temp directory; no model is contacted and no user data is touched.
/// </summary>
internal sealed record ConvScenario(string Root, string Worktree, string State, string Database,
    string Runs, string WriterCounter, string WorktreeRoot);

internal static class ConvFixture
{
    public static ConvScenario New(string root, string name)
    {
        var directory = Path.Combine(root, name);
        var state = Path.Combine(directory, "state");
        Directory.CreateDirectory(Path.Combine(directory, "worktree"));
        Directory.CreateDirectory(state);
        return new(directory, Path.Combine(directory, "worktree"), state,
            Path.Combine(state, "automation.db"), Path.Combine(state, "runs"),
            Path.Combine(state, "writer-starts.log"), Path.Combine(directory, "worktrees"));
    }

    public static ConvScenario FromDatabase(string database)
    {
        var state = Path.GetDirectoryName(Path.GetFullPath(database))!;
        var root = Path.GetDirectoryName(state)!;
        return new(root, Path.Combine(root, "worktree"), state, database,
            Path.Combine(state, "runs"), Path.Combine(state, "writer-starts.log"),
            Path.Combine(root, "worktrees"));
    }

    public static AutomationStore Seed(ConvScenario scenario, string threadId = "thread",
        string taskId = "task", string partId = "part")
    {
        var store = new AutomationStore(scenario.Database);
        store.CreateThread(threadId, "project", "codex", "devin/swe-2", scenario.Worktree, null, 1);
        store.CreateTask(taskId, threadId, "fixture", "base", 1);
        store.CreatePart(partId, taskId, "fixture part", 1);
        return store;
    }

    /// <summary>
    /// The fixture executable is the harness runtime; a thread freezes the runtime/model binding, so
    /// behaviour comes from the prompt text and not from the argument array.
    /// </summary>
    public static HarnessRuntime Runtime(ConvScenario scenario) =>
        new("fixture", "codex", Environment.ProcessPath!,
            ["--child", "protocol", scenario.WriterCounter, "--model", "devin/swe-2", "--json"],
            "fixture-v1");

    /// <summary>Supervise one bounded execution in a real child process.</summary>
    public static Process LaunchHost(ConvScenario scenario, string hostId, string runId, string prompt) =>
        Launch("--child", "host", scenario.WriterCounter, scenario.Database, scenario.Runs, hostId,
            runId, prompt);

    public static string GitInit(string worktree)
    {
        Git(worktree, ["init", "-q"]);
        Git(worktree, ["config", "user.email", "fixture@example.invalid"]);
        Git(worktree, ["config", "user.name", "fixture"]);
        Git(worktree, ["config", "core.autocrlf", "false"]);
        File.WriteAllText(Path.Combine(worktree, "tracked.txt"), "initial\n");
        Git(worktree, ["add", "-A"]);
        Git(worktree, ["commit", "-q", "-m", "initial"]);
        return Git(worktree, ["rev-parse", "HEAD"]);
    }

    public static string GitCommit(string worktree, string message)
    {
        Git(worktree, ["add", "-A"]);
        Git(worktree, ["commit", "-q", "--allow-empty", "-m", message]);
        return Git(worktree, ["rev-parse", "HEAD"]);
    }

    public static string Git(string worktree, string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true, WorkingDirectory = worktree
        };
        foreach (var arg in new[] { "--no-optional-locks", "-C", worktree }.Concat(args))
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("git failed");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)}: {error}");
        return output.Trim();
    }

    public static Process Launch(params string[] args)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true, RedirectStandardInput = true
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        var process = Process.Start(start) ?? throw new InvalidOperationException("child failed");
        process.StandardInput.Close();
        return process;
    }

    public static void KillTree(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill(entireProcessTree: true);
            process.WaitForExit(20_000);
        }
        catch (ArgumentException) { /* already gone */ }
    }

    public static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public static int StartCount(string counter) =>
        File.Exists(counter) ? File.ReadAllLines(counter).Length : 0;
}
