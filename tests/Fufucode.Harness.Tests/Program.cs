using System.Text.Json;
using Microsoft.Data.Sqlite;
using Fufucode.Core.Automation;
using Fufucode.Core.Automation.Harnesses;

if (args.FirstOrDefault() == "--fake")
{
    var harness = args[1];
    var scenario = args[2];
    var counter = args[3];
    File.AppendAllText(counter, "started\n");
    var prompt = await Console.In.ReadToEndAsync();
    string? Value(string flag) => Array.IndexOf(args, flag) is var i && i >= 0 ? args[i + 1] : null;
    var resume = harness switch { "pi" => Value("--session"), "omp" => Value("--resume"),
        "dsh" => Value("--session-id"), _ => args.Contains("resume") ? args[^2] : null };
    if (harness != "dsh" && Value("--model") != "devin/swe-2") return 17;
    if (harness == "dsh")
    {
        using var patch = JsonDocument.Parse(File.ReadAllText(Value("--patch")!));
        if (patch.RootElement[0].GetProperty("config").GetProperty("model").GetString() != "devin/swe-2") return 18;
    }
    if (prompt == "repair" && resume is null) return 19;
    var id = scenario == "wrong-session" && resume is not null ? "wrong" : "fixture-session";
    if (harness is "pi" or "omp")
    {
        var sessions = Value("--session-dir")!;
        Directory.CreateDirectory(sessions);
        File.WriteAllText(resume ?? Path.Combine(sessions, "session.jsonl"),
            (harness == "omp" ? "{\"type\":\"title\",\"title\":\"fixture\"}\n" : "") +
            JsonSerializer.Serialize(new { type = "session", id }) + "\n");
    }
    void Emit(object record) => Console.WriteLine(JsonSerializer.Serialize(record));
    if (harness == "codex") Emit(new { type = "thread.started", thread_id = id });
    else if (harness == "dsh") Emit(new { type = "session", sessionId = id });
    else Emit(new { type = "session", id });
    await Console.Out.FlushAsync();
    if (scenario == "hang") await Task.Delay(30000);
    if (scenario == "bad-json") { Console.WriteLine("{bad"); return 0; }
    if (scenario == "no-terminal") return 0;
    var output = prompt == "repair" ? "correct" : "needs repair";
    if (harness == "codex")
    {
        Emit(new { type = "item.completed", item = new { type = "agent_message", text = output } });
        Emit(new { type = scenario == "failed" ? "turn.failed" : "turn.completed" });
    }
    else if (harness == "dsh")
    {
        Emit(new { type = "status", phase = "turn_end", reason = new { kind = scenario == "failed" ? "error" : "completed" } });
        Emit(new { type = "final", text = output });
    }
    else
    {
        Emit(new { type = "message_end", message = new { role = "assistant", stopReason = scenario == "failed" ? "error" : "stop",
            content = new[] { new { type = "text", text = output } } } });
        Emit(new { type = "agent_end" });
    }
    return 0;
}

var root = Path.Combine(Path.GetTempPath(), "fufucode-harness-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var passed = 0;
void Check(bool value, string name)
{ if (!value) throw new Exception("FAIL " + name); passed++; Console.WriteLine("PASS " + name); }
async Task Reject(Func<Task> action, string name)
{ try { await action(); } catch (Exception) { Check(true, name); return; } throw new Exception("FAIL " + name); }

foreach (var harness in new[] { "pi", "omp", "codex", "dsh" })
{
    var directory = Path.Combine(root, harness);
    Directory.CreateDirectory(directory);
    using var store = new AutomationStore(Path.Combine(directory, "automation.db"));
    store.CreateThread("thread", "project", harness, "devin/swe-2", directory, null, 1);
    store.CreateTask("task", "thread", "fixture", "base", 1);
    store.CreatePart("part", "task", "fixture part", 1);
    var counter = Path.Combine(directory, "starts.log");
    var runtime = new HarnessRuntime("fixture", harness, Environment.ProcessPath!,
        ["--fake", harness, "ok", counter], "fixture-v1");
    var request = new HarnessRequest("first", "thread", "task", "devin/swe-2", "first", "fixture-provider");
    var executor = new HarnessExecutor(store, Path.Combine(directory, "runs"));
    var first = await executor.ExecuteAsync(runtime, request);
    Check(first.Status == HarnessRunStatus.Completed && first.Output == "needs repair", harness + " process/event completion");
    Check(store.GetPart("part").Status == DeliveryPartStatus.Delivered, harness + " transport never auto-accepts");
    store.RecordDelivery("rev1", "part", "first", "head1", "{}", "delivery1", 2);
    store.RecordDecision("review1", "part", AutomationDecision.Rework, "fix", null, "human", "decision1", 3, "first", "head1", 1);
    var second = await executor.ExecuteAsync(runtime, request with { RunId = "second", Prompt = "repair" });
    Check(second.Status == HarnessRunStatus.Completed && second.Output == "correct" && second.SessionId == first.SessionId,
        harness + " rework resumes exact saved session");
    store.RecordDelivery("rev2", "part", "second", "head2", "{}", "delivery2", 4);
    store.RecordDecision("review2", "part", AutomationDecision.Accept, null, null, "human", "decision2", 5, "second", "head2", 2);
    Check(store.GetPart("part").Status == DeliveryPartStatus.Accepted && !store.IsIntegrated("part", 2), harness + " accept is not integration");
    var reopened = new HarnessExecutor(new AutomationStore(store.DatabasePath), Path.Combine(directory, "runs"));
    await reopened.ExecuteAsync(runtime, request);
    Check(File.ReadAllLines(counter).Length == 2, harness + " persisted replay never launches twice");
    await Reject(() => executor.ExecuteAsync(runtime, request with { Prompt = "changed" }), harness + " rejects replay payload change");
    await Reject(() => executor.ExecuteAsync(runtime with { Version = "different" }, request with { RunId = "third" }), harness + " immutable runtime binding");
    var events = store.ListEventsAfter(0);
    Check(events.Any(e => e.Type == "harness.settled"), harness + " persisted event loop");
}

foreach (var scenario in new[] { "failed", "no-terminal", "bad-json", "hang", "wrong-session" })
{
    var directory = Path.Combine(root, scenario);
    Directory.CreateDirectory(directory);
    using var store = new AutomationStore(Path.Combine(directory, "automation.db"));
    store.CreateThread("thread", "project", "codex", "devin/swe-2", directory, null, 0);
    store.CreateTask("task", "thread", "fixture", "base", 0);
    var runtime = new HarnessRuntime("fixture", "codex", Environment.ProcessPath!,
        ["--fake", "codex", scenario, Path.Combine(directory, "starts.log")], "fixture-v1");
    var request = new HarnessRequest("first", "thread", "task", "devin/swe-2", "first");
    var executor = new HarnessExecutor(store, Path.Combine(directory, "runs"));
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(scenario == "hang" ? 1 : 10));
    var first = await executor.ExecuteAsync(runtime, request, timeout.Token);
    if (scenario == "wrong-session") first = await executor.ExecuteAsync(runtime, request with { RunId = "second", Prompt = "repair" });
    Check(first.Status != HarnessRunStatus.Completed, scenario + " cannot report success");
    if (first.Status == HarnessRunStatus.Unknown)
        await Reject(() => executor.ExecuteAsync(runtime, request with { RunId = "third" }), scenario + " quarantines new dispatch");
    await executor.ExecuteAsync(runtime, request);
    Check(File.ReadAllLines(Path.Combine(directory, "starts.log")).Length == (scenario == "wrong-session" ? 2 : 1), scenario + " replay is inert");
}
// Simulate loss of the owning supervisor after its durable claim. Reopening must
// neither replay that request nor dispatch a different run into the same scope.
{
    var directory = Path.Combine(root, "crash");
    Directory.CreateDirectory(directory);
    using var store = new AutomationStore(Path.Combine(directory, "automation.db"));
    store.CreateThread("thread", "project", "codex", "devin/swe-2", directory, null, 0);
    store.CreateTask("task", "thread", "fixture", "base", 0);
    var counter = Path.Combine(directory, "starts.log");
    var runtime = new HarnessRuntime("fixture", "codex", Environment.ProcessPath!,
        ["--fake", "codex", "ok", counter], "fixture-v1");
    var request = new HarnessRequest("first", "thread", "task", "devin/swe-2", "first");
    var executor = new HarnessExecutor(store, Path.Combine(directory, "runs"));
    Check(store.ListEventsAfter(0).Count == 0 && !File.Exists(counter), "idle initialization makes zero calls");
    await executor.ExecuteAsync(runtime, request);
    using (var c = new SqliteConnection($"Data Source={store.DatabasePath};Pooling=False"))
    {
        c.Open();
        using var q = c.CreateCommand();
        q.CommandText = "UPDATE harness_runs SET status='Dispatching',output=NULL WHERE run_id='first'";
        q.ExecuteNonQuery();
    }
    var reopened = new HarnessExecutor(store, Path.Combine(directory, "runs"));
    Check(reopened.Runs.Reconcile("first").Status == HarnessRunStatus.Unknown, "lost owner projects unknown");
    await reopened.ExecuteAsync(runtime, request);
    Check(File.ReadAllLines(counter).Length == 1, "crash claim replay never calls model");
    await Reject(() => reopened.ExecuteAsync(runtime, request with { RunId = "other" }), "uncertain claim blocks other run");
    store.CreateThread("other-thread", "project", "codex", "devin/swe-2", directory, null, 0);
    store.CreateTask("other-task", "other-thread", "fixture", "base", 0);
    await Reject(() => reopened.ExecuteAsync(runtime, request with { RunId = "other", ThreadId = "other-thread", TaskId = "other-task" }),
        "same worktree cannot gain second writer");
}
Console.WriteLine($"PASS {passed} checks. Evidence: {root}");
return 0;
