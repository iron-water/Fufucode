using System.Text.Json;
using Fufucode.Core.Automation;
using Fufucode.Core.Automation.Harnesses;
using Fufucode.Core.Automation.Hosting;

namespace Fufucode.Hosting.Tests;

/// <summary>
/// Deterministic local protocol emitter and process-role launcher used by the lifecycle tests, so
/// every check observes a real process, a real worktree and a real repository HEAD. No model is
/// contacted and no credentials are used.
///
/// One protocol runtime only, because a thread freezes its runtime binding: behavior comes from the
/// prompt text the executor writes to stdin.
///   prompt "commit"        write a file into the worktree, commit it, then deliver
///   prompt "echo:<text>"   deliver the text
///   prompt "fail"          emit a native failure terminal
///   prompt "truncate"      emit a session identity and exit without any terminal record
///   prompt "hang:<seconds>" emit a session, wait, then deliver
///
/// Roles:
///   protocol &lt;writerCounter&gt;
///   hold &lt;counter&gt; &lt;seconds&gt;                       a silent process that can still write
///   spawn &lt;counter&gt; &lt;pidFile&gt; &lt;seconds&gt; &lt;holdCounter&gt;   exits, leaving a real hold child alive
///   backgroundhost &lt;counter&gt; &lt;db&gt; &lt;runs&gt; &lt;hostId&gt; &lt;endpointFile&gt;
///                                   runs the real C2 host as this process: claims the slot, serves
///                                   the line protocol, drains the durable queue, and exits only when
///                                   explicitly stopped (4 when another live host owns the slot)
///   host &lt;counter&gt; &lt;db&gt; &lt;runs&gt; &lt;hostId&gt; &lt;runId&gt; &lt;prompt&gt;      supervises one bounded execution
///   compete &lt;same args&gt;                                same code path, expected to be refused
/// </summary>
internal static class ChildRunner
{
    public static async Task<int> Run(string[] args)
    {
        var role = args[1];
        var counter = args[2];
        File.AppendAllText(counter, "started\n");

        switch (role)
        {
            case "protocol":
                return await Protocol(args);
            case "hold":
                await Task.Delay(TimeSpan.FromSeconds(int.Parse(args[4])));
                return 0;
            case "spawn":
                {
                    using (var survivor = Fixture.Launch("--child", "hold", args[5], args[4]))
                        File.WriteAllText(args[3], survivor.Id.ToString());
                    return 0;
                }
            case "backgroundhost":
                {
                    // args: counter db runs hostId endpointFile
                    var options = new HostOptions(args[3], args[4], args[5], args[6]);
                    await using var host = new BackgroundHost(options);
                    if (!await host.StartAsync())
                    {
                        Console.Error.WriteLine("refused: another live host owns the slot");
                        return 4;
                    }
                    // Exit only after the run in flight finished: the drain loop stops taking new
                    // work at once, and the slot is released as a clean stop by the host itself.
                    await host.DrainTask;
                    await host.AcceptTask;
                    return 0;
                }
            case "host" or "compete":
                {
                    using var store = new AutomationStore(args[3]);
                    var executor = new HarnessExecutor(store, args[4], args[5],
                        GitCliEvidence.Shared);
                    var scenario = Fixture.FromDatabase(args[3]);
                    try
                    {
                        var result = await executor.ExecuteAsync(Fixture.Runtime(scenario),
                            new HarnessRequest(args[6], "thread", "task", "devin/swe-2", args[7]));
                        Console.Error.WriteLine(result.Status.ToString());
                        return result.Status == HarnessRunStatus.Completed ? 0 : 1;
                    }
                    catch (Exception error) when (error is InvalidOperationException
                        or Microsoft.Data.Sqlite.SqliteException)
                    {
                        // A competitor must be refused before its writer process ever starts.
                        Console.Error.WriteLine(error.GetType().Name);
                        return 3;
                    }
                }
            default:
                return 64;
        }
    }

    private static async Task<int> Protocol(string[] args)
    {
        string? Value(string flag) => Array.IndexOf(args, flag) is var i && i >= 0 && i + 1 < args.Length
            ? args[i + 1] : null;
        if (Value("--model") != "devin/swe-2") return 17;
        var prompt = (await Console.In.ReadToEndAsync()).Trim();
        var worktree = Directory.GetCurrentDirectory();

        void Emit(object record) => Console.WriteLine(JsonSerializer.Serialize(record));
        void Session()
        {
            Emit(new { type = "thread.started", thread_id = "fixture-session" });
            Console.Out.Flush();
        }
        void Deliver(string text)
        {
            Emit(new { type = "item.completed", item = new { type = "agent_message", text } });
            Emit(new { type = "turn.completed" });
            Console.Out.Flush();
        }

        Session();
        if (prompt.StartsWith("hang:"))
            await Task.Delay(TimeSpan.FromSeconds(int.Parse(prompt[5..])));
        switch (prompt)
        {
            case "truncate":
                return 0; // no terminal record: the outcome is unknown
            case "fail":
                Emit(new { type = "turn.failed" });
                Console.Out.Flush();
                return 0;
            case "commit":
                {
                    var file = Path.Combine(worktree, $"change-{Guid.NewGuid():N}.txt");
                    File.WriteAllText(file, "fixture delivery\n");
                    Fixture.GitCommit(worktree, "fixture delivery");
                    Deliver("committed " + Path.GetFileName(file));
                    return 0;
                }
            default:
                Deliver(prompt.StartsWith("echo:") ? prompt[5..] : prompt);
                return 0;
        }
    }
}
