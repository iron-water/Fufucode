using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Fufucode.Core.Automation;
using Fufucode.Core.Automation.Harnesses;
using Fufucode.Core.Automation.Hosting;

namespace Fufucode.Hosting.Tests;

/// <summary>
/// One isolated scenario: a real git repository as the worktree plus its own project database and
/// evidence directories. Everything lives under a temp directory; nothing touches user data or the
/// production queue.
/// </summary>
internal sealed record Scenario(string Root, string Worktree, string State, string Database,
    string Runs, string WriterCounter, string HostCounter);

internal static class Fixture
{
    public static Scenario New(string root, string name)
    {
        var directory = Path.Combine(root, name);
        var state = Path.Combine(directory, "state");
        Directory.CreateDirectory(Path.Combine(directory, "worktree"));
        Directory.CreateDirectory(state);
        return new(directory, Path.Combine(directory, "worktree"), state,
            Path.Combine(state, "automation.db"), Path.Combine(state, "runs"),
            Path.Combine(state, "writer-starts.log"), Path.Combine(state, "host-starts.log"));
    }

    /// <summary>Used by role children, which only know the database path.</summary>
    public static Scenario FromDatabase(string database)
    {
        var state = Path.GetDirectoryName(Path.GetFullPath(database))!;
        var root = Path.GetDirectoryName(state)!;
        return new(root, Path.Combine(root, "worktree"), state, database,
            Path.Combine(state, "runs"), Path.Combine(state, "writer-starts.log"),
            Path.Combine(state, "host-starts.log"));
    }

    public static AutomationStore Seed(Scenario scenario, string threadId = "thread",
        string taskId = "task", string partId = "part")
    {
        var store = new AutomationStore(scenario.Database);
        store.CreateThread(threadId, "project", "codex", "devin/swe-2", scenario.Worktree, null, 1);
        store.CreateTask(taskId, threadId, "fixture", "base", 1);
        store.CreatePart(partId, taskId, "fixture part", 1);
        return store;
    }

    /// <summary>
    /// Add a second real task and part to the same thread, as an upper layer would. Both are in the
    /// project from the start, so a cross-task delivery attempt is a legitimate-looking card, not a
    /// typo pointing at a row that does not exist.
    /// </summary>
    public static void SeedSecondTask(AutomationStore store, string taskId = "task-b",
        string partId = "part-b")
    {
        store.CreateTask(taskId, "thread", "second fixture task", "base", 2);
        store.CreatePart(partId, taskId, "second fixture part", 2);
    }

    /// <summary>
    /// The fixture executable is the harness runtime and its working directory is the scenario's
    /// real worktree, so every run is a real child process. The argument array is always identical:
    /// a thread freezes its runtime binding, behavior comes from the prompt.
    /// </summary>
    public static HarnessRuntime Runtime(Scenario scenario) =>
        new("fixture", "codex", Environment.ProcessPath!,
            ["--child", "protocol", scenario.WriterCounter, "--model", "devin/swe-2", "--json"],
            "fixture-v1");

    /// <summary>Launch a real host process supervising one bounded execution.</summary>
    public static Process LaunchHost(Scenario scenario, string hostId, string runId, string prompt,
        string role = "host") =>
        Launch("--child", role, scenario.HostCounter, scenario.Database, scenario.Runs, hostId,
            runId, prompt);

    /// <summary>Launch a writer child that outlives its own parent process.</summary>
    public static (int Pid, long StartUtc) ExitSpawner(Scenario scenario, string pidFile,
        string seconds) =>
        ExitIdentity("--child", "spawn", scenario.WriterCounter, pidFile, seconds,
            Path.Combine(scenario.State, "survivor-child.log"));

    /// <summary>Where the running host publishes its loopback port; deleted on a clean stop.</summary>
    public static string EndpointFile(Scenario scenario) => Path.Combine(scenario.State, "host.endpoint");

    /// <summary>Launch the real C2 background host as a separate process.</summary>
    public static Process LaunchBackgroundHost(Scenario scenario, string hostId) =>
        Launch("--child", "backgroundhost", scenario.HostCounter, scenario.Database, scenario.Runs,
            hostId, EndpointFile(scenario));

    /// <summary>Wait until the host process has published its port, so every check uses a real socket.</summary>
    public static async Task<int> AwaitPort(Scenario scenario, int timeoutMs = 30_000)
    {
        var deadline = Now() + timeoutMs;
        while (true)
        {
            var port = HostClient.ReadEndpoint(EndpointFile(scenario));
            if (port is > 0) return port.Value;
            if (Now() > deadline) throw new TimeoutException("The host never published an endpoint.");
            await Task.Delay(50);
        }
    }

    public static string GitInit(string worktree)
    {
        Git(worktree, ["init", "-q"]);
        Git(worktree, ["config", "user.email", "fixture@example.invalid"]);
        Git(worktree, ["config", "user.name", "fixture"]);
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
        if (process.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)}: {error}");
        return output.Trim();
    }

    /// <summary>
    /// Journal mutations that simulate what a crash or a recycled pid leaves behind. Fixtures only:
    /// the reconciler never exposes a raw journal edit as an interface.
    /// </summary>
    public static void SetOwner(string database, string runId, int? supervisorPid, long? supervisorStart,
        int? writerPid, long? writerStart, string? status = null, string? hostId = null)
    {
        using var c = Open(database);
        using var q = c.CreateCommand();
        q.CommandText = """
            UPDATE harness_runs SET supervisor_pid=$sup, supervisor_start_utc=$supStart,
              writer_pid=$wri, writer_start_utc=$wriStart,
              status=COALESCE($status,status), host_id=COALESCE($host,host_id) WHERE run_id=$id
            """;
        q.Parameters.AddWithValue("$sup", (object?)supervisorPid ?? DBNull.Value);
        q.Parameters.AddWithValue("$supStart", (object?)supervisorStart ?? DBNull.Value);
        q.Parameters.AddWithValue("$wri", (object?)writerPid ?? DBNull.Value);
        q.Parameters.AddWithValue("$wriStart", (object?)writerStart ?? DBNull.Value);
        q.Parameters.AddWithValue("$status", (object?)status ?? DBNull.Value);
        q.Parameters.AddWithValue("$host", (object?)hostId ?? DBNull.Value);
        q.Parameters.AddWithValue("$id", runId);
        if (q.ExecuteNonQuery() == 0) throw new KeyNotFoundException(runId);
    }

    public static void SetWorktree(string database, string runId, string worktree) =>
        Update(database, runId, "worktree", worktree);

    public static string GetStatus(string database, string runId)
    {
        using var c = Open(database);
        using var q = c.CreateCommand();
        q.CommandText = "SELECT status FROM harness_runs WHERE run_id=$id";
        q.Parameters.AddWithValue("$id", runId);
        return q.ExecuteScalar() as string ?? throw new KeyNotFoundException(runId);
    }

    /// <summary>An open claim left behind by a host that died before journaling an outcome.</summary>
    public static void SeedRunning(string database, string runId, string threadId, string worktree)
    {
        SeedClaim(database, runId, threadId, worktree, "Running");
    }

    /// <summary>A dispatch record whose outcome is already journaled; only one open claim per thread is allowed.</summary>
    public static void SeedClosed(string database, string runId, string threadId, string worktree,
        string status) => SeedClaim(database, runId, threadId, worktree, status);

    /// <summary>
    /// The shape a journal written before a column existed has: identity columns filled, the new
    /// column NULL. Used to prove fail-closed behaviour on legacy rows without inventing data.
    /// </summary>
    public static void SeedLegacy(string database, string runId, string threadId, string taskId,
        string worktree, string status, string sessionId, string output)
    {
        using var c = Open(database);
        using var q = c.CreateCommand();
        q.CommandText = """
            INSERT INTO harness_runs(run_id,thread_id,task_id,worktree,request_hash,status,
                                     session_id,output)
            VALUES($id,$thread,$task,$worktree,$hash,$status,$session,$output)
            """;
        q.Parameters.AddWithValue("$id", runId);
        q.Parameters.AddWithValue("$thread", threadId);
        q.Parameters.AddWithValue("$task", taskId);
        q.Parameters.AddWithValue("$worktree", worktree);
        q.Parameters.AddWithValue("$hash", HarnessRunStore.Hash(runId + "legacy"));
        q.Parameters.AddWithValue("$status", status);
        q.Parameters.AddWithValue("$session", sessionId);
        q.Parameters.AddWithValue("$output", output);
        q.ExecuteNonQuery();
    }

    private static void SeedClaim(string database, string runId, string threadId, string worktree,
        string status)
    {
        using var c = Open(database);
        using var q = c.CreateCommand();
        q.CommandText = """
            INSERT INTO harness_runs(run_id,thread_id,worktree,request_hash,status)
            VALUES($id,$thread,$worktree,$hash,$status)
            """;
        q.Parameters.AddWithValue("$id", runId);
        q.Parameters.AddWithValue("$thread", threadId);
        q.Parameters.AddWithValue("$worktree", worktree);
        q.Parameters.AddWithValue("$hash", HarnessRunStore.Hash(runId + "fixture"));
        q.Parameters.AddWithValue("$status", status);
        q.ExecuteNonQuery();
    }

    private static void Update(string database, string runId, string column, string value)
    {
        using var c = Open(database);
        using var q = c.CreateCommand();
        q.CommandText = $"UPDATE harness_runs SET {column}=$value WHERE run_id=$id";
        q.Parameters.AddWithValue("$value", value);
        q.Parameters.AddWithValue("$id", runId);
        if (q.ExecuteNonQuery() == 0) throw new KeyNotFoundException(runId);
    }

    private static SqliteConnection Open(string database)
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = database, Pooling = false, DefaultTimeout = 10 }.ToString());
        c.Open();
        return c;
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

    public static (int Pid, long StartUtc) ExitIdentity(params string[] args)
    {
        using var process = Launch(args);
        var start = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        if (!process.WaitForExit(120_000)) throw new InvalidOperationException("fixture child hung.");
        return (process.Id, start.ToUnixTimeMilliseconds());
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

    /// <summary>Ask the live host for an explicit stop over its own protocol.</summary>
    public static async Task<HostFrame> StopHostAsync(int port)
    {
        await using var client = await HostClient.ConnectAsync(port);
        return await client.StopAsync();
    }

    public static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public static int StartCount(string counter) =>
        File.Exists(counter) ? File.ReadAllLines(counter).Length : 0;
}
