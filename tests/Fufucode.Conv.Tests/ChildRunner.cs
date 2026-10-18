using System.Text.Json;
using Fufucode.Core.Automation;
using Fufucode.Core.Automation.Harnesses;

namespace Fufucode.Conv.Tests;

/// <summary>
/// Deterministic local protocol emitter and process-role launcher. Every check observes a real child
/// process, a real worktree and a real repository HEAD. No model is contacted.
///
/// prompt "commit"        write a file into the worktree, commit it, then deliver
/// prompt "echo:&lt;text&gt;"   deliver the text
/// prompt "fail"          emit a native failure terminal
/// prompt "truncate"      emit a session identity and exit without any terminal record
/// prompt "hang:&lt;seconds&gt;" emit a session, wait, then deliver
///
/// Roles:
///   protocol &lt;writerCounter&gt;
///   host &lt;counter&gt; &lt;db&gt; &lt;runs&gt; &lt;hostId&gt; &lt;runId&gt; &lt;prompt&gt; supervise one bounded execution
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
            case "host":
                {
                    using var store = new AutomationStore(args[3]);
                    var executor = new HarnessExecutor(store, args[4], args[5], GitCliEvidence.Shared);
                    var scenario = ConvFixture.FromDatabase(args[3]);
                    try
                    {
                        var result = await executor.ExecuteAsync(ConvFixture.Runtime(scenario),
                            new HarnessRequest(args[6], "thread", "task", "devin/swe-2", args[7]));
                        Console.Error.WriteLine(result.Status.ToString());
                        await Console.Error.FlushAsync();
                        return result.Status == HarnessRunStatus.Completed ? 0 : 1;
                    }
                    catch (Exception error) when (error is InvalidOperationException
                        or Microsoft.Data.Sqlite.SqliteException)
                    {
                        Console.Error.WriteLine(error.GetType().Name);
                        await Console.Error.FlushAsync();
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
                    ConvFixture.GitCommit(worktree, "fixture delivery");
                    Deliver("committed " + Path.GetFileName(file));
                    return 0;
                }
            default:
                Deliver(prompt.StartsWith("echo:") ? prompt[5..] : prompt);
                return 0;
        }
    }
}
