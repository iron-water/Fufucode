using System.Collections.Concurrent;
using Fufucode.App.Abstractions;
using Fufucode.Core;
using Fufucode.Pi;

var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
var fake = Path.Combine(root, "tests/Fufucode.FakePi/bin/Debug/net10.0/Fufucode.FakePi.dll");
var dotnet = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet/dotnet.exe");
var node = Path.Combine(root, "runtime/node/node.exe");
var cli = Path.Combine(root, "runtime/pi/node_modules/@earendil-works/pi-coding-agent/dist/bundle/cli.js");
if (args.Contains("--d1-only")) { await D1BackendTests.RunAsync(root); return; }
if (args.Contains("--claude-only")) { await ClaudeBackendIntegrationTests.RunAsync(root); return; }
if (args.Contains("--codex-dsh-only")) { await CodexDshIntegrationTests.RunAsync(root); return; }
if (args.Contains("--d1-regression-only")) { await PersistentTests.RunAsync(root, fake, dotnet); await ExtensionUiTests.RunAsync(root, fake, dotnet); return; }
if (args.Contains("--bridge-only")) { await BridgeIntegrationTests.RunAsync(root, node, cli); return; }
if (args.Contains("--cli-ui-only")) { await CliTests.RunUiAsync(root); return; }
if (args.Contains("--cli-contract-only")) { await CliTests.RunContractsAsync(root); return; }
if (args.Contains("--cli-host-only")) { await CliHostTests.RunAsync(); return; }
if (args.Contains("--cli-only")) { await CliTests.RunAsync(root, fake, dotnet); return; }
if (args.Contains("--idle-maintenance-only")) { await IdleMaintenanceTests.RunAsync(root, fake, dotnet); return; }
if (args.Contains("--process-exit-only")) { await ProcessExitTests.RunAsync(root, fake, dotnet); return; }
if (args.Contains("--a13-only")) { await A13ContractTests.RunAsync(root, fake, dotnet); return; }
if (args.Contains("--crash-repro-only")) { await CrashReproTests.RunAsync(root, node, cli); return; }
if (args.Contains("--worktree-only")) { await ProjectForkTests.RunAsync(root, node, cli); return; }
if (args.Contains("--adapter-stop-only")) { await AdapterStopTests.RunAsync(root, fake, dotnet); return; }
if (args.Contains("--nvx-only")) { NvxProtocolTests.Run(); return; }
PiRpcOptions fakeOptions() => new(dotnet, [fake], root);
void Check(bool condition, string name) { if (!condition) throw new Exception(name); Console.WriteLine($"PASS {name}"); }
HistoryMappingTests.Run();
await AdapterStopTests.RunAsync(root, fake, dotnet);
await IdleMaintenanceTests.RunAsync(root, fake, dotnet);
await ProcessExitTests.RunAsync(root, fake, dotnet);
await CliTests.RunAsync(root, fake, dotnet);

async Task WaitFor(Func<bool> condition)
{
    var until = DateTime.UtcNow + TimeSpan.FromSeconds(5);
    while (!condition())
    {
        if (DateTime.UtcNow > until) throw new TimeoutException("Expected session state was not reached.");
        await Task.Delay(10);
    }
}

foreach (var prompt in new[] { "stream", "early" })
{
    await using var session = PiSessions.Coordinator(fakeOptions());
    var snapshots = new ConcurrentQueue<SessionSnapshot>();
    session.SnapshotChanged += snapshots.Enqueue;
    await session.StartAsync();
    await session.SendPromptAsync(prompt);
    await WaitFor(() => session.Snapshot.LastResult == TaskResult.Completed);
    var final = session.Snapshot;
    Check(final.Connection == ConnectionState.Connected && final.Activity == ActivityState.Idle, $"{prompt}: settled is final idle");
    Check(final.Messages.Count == 1 && final.Messages[0].Content.Count == 3 &&
        final.Messages[0].Content[0] is PiTextBlock { Text: "Final answer" }, $"{prompt}: final message replaces draft once");
    Check(final.Messages[0].Content[1] is PiThinkingBlock { Thinking: "thought-final" } &&
        final.Messages[0].Content[2] is PiToolCallBlock { Id: "call-1" }, $"{prompt}: content blocks stay distinct");
    Check(snapshots.Any(s => s.Messages.Count == 1 && s.Messages[0].Content.Count >= 2 &&
        s.Messages[0].Content[0] is PiTextBlock { Text: "Hel" } &&
        s.Messages[0].Content[1] is PiThinkingBlock { Thinking: "thought" }), $"{prompt}: deltas accumulate by contentIndex");
    Check(snapshots.Any(s => s.Messages.Count == 1 && s.Messages[0].Content.Count == 3 &&
        s.Messages[0].Content[2] is PiToolCallDraftBlock { ArgumentsText: "{\"path\":\"a" }),
        $"{prompt}: incomplete tool arguments remain draft text");
    Check(final.CurrentResponseUsage?.Output == 2 && final.CurrentResponseUsage?.TotalTokens == 12,
        $"{prompt}: usage is latest cumulative value");
    Check(final.Tools.Count == 1 && final.Tools[0].IsFinal &&
        final.Tools[0].DisplayResult?.GetProperty("content")[0].GetProperty("text").GetString() == "complete",
        $"{prompt}: final tool result replaces partial result");
    Check(snapshots.Any(s => s.Messages.Count == 1 && s.Messages[0].Content.Count > 0 &&
        s.Messages[0].Content[0] is PiTextBlock { Text: "Final answer" } &&
        s.Activity == ActivityState.Running && s.LastResult == TaskResult.Unknown),
        $"{prompt}: agent_end does not settle session");
}

await using (var session = PiSessions.Coordinator(fakeOptions()))
{
    await session.StartAsync();
    await session.SendPromptAsync("hold");
    await WaitFor(() => session.Snapshot.Activity == ActivityState.Running);
    await session.SendPromptAsync("queued", PromptDelivery.FollowUp);
    await session.StopAsync();
    var state = session.Snapshot;
    Check(state.RecoverableQueue.SequenceEqual(["queued"]) && state.LastResult == TaskResult.Interrupted,
        "stop restores cleared queue and marks interrupted");
    var raw = (await session.QueryStateAsync()).Data!.Value;
    var order = raw.GetProperty("commandOrder").EnumerateArray().Select(x => x.GetString()).ToArray();
    Check(Array.IndexOf(order, "clear_queue") < Array.IndexOf(order, "abort") &&
        raw.GetProperty("pendingMessageCount").GetInt32() == 0, "clear_queue precedes abort and prevents queued work");
}

await using (var session = PiSessions.Coordinator(fakeOptions()))
{
    await session.StartAsync();
    try { await session.SendPromptAsync("ack_timeout", acknowledgementTimeout: TimeSpan.FromMilliseconds(250)); throw new Exception("timeout missed"); }
    catch (RpcException error) { Check(error.Message.Contains("timed out"), "prompt acknowledgement timeout is explicit"); }
    var state = (await session.QueryStateAsync()).Data!.Value;
    Check(state.GetProperty("promptCount").GetInt32() == 1 && session.Snapshot.LastResult == TaskResult.Unknown,
        "timed-out side-effect command is not resent");
}

await using (var session = PiSessions.Coordinator(fakeOptions()))
{
    var events = new ConcurrentQueue<PiSessionEvent>();
    session.ProtocolEvent += events.Enqueue;
    await session.StartAsync();
    await session.SendPromptAsync("late_events");
    await WaitFor(() => events.Count(e => e.Type == "auto_retry_end") == 1);
    Check(session.Snapshot.Activity == ActivityState.Idle,
        "late compaction and retry events cannot reopen a settled run");
    await session.SendPromptAsync("handled");
    Check(session.Snapshot.Connection == ConnectionState.Connected,
        "immediate prompt remains available after late lifecycle events");
}

await using (var session = PiSessions.Coordinator(fakeOptions() with
{
    PromptAcknowledgementTimeout = TimeSpan.FromMilliseconds(100),
    ExtensionCommandAcknowledgementTimeout = TimeSpan.FromSeconds(2)
}))
{
    await session.StartAsync();
    var prompt = session.SendPromptAsync("/slow.ui");
    await WaitFor(() => session.Snapshot.PendingUiRequests.Count == 1);
    await Task.Delay(250);
    Check(!prompt.IsCompleted, "slash command waits beyond ordinary prompt acknowledgement timeout");
    await session.RespondToExtensionUiAsync(session.Snapshot.PendingUiRequests.Single(), PiExtensionUiResponse.Confirm(true));
    await prompt;
    await WaitFor(() => session.Snapshot.Activity == ActivityState.Idle);
    Check(session.Snapshot.LastError is null, "slash command acknowledgement succeeds after dialog response");
    try { await session.SendPromptAsync("ack_timeout"); throw new Exception("ordinary prompt timeout missed"); }
    catch (RpcException error)
    {
        Check(error.Message.Contains("timed out"), "ordinary prompt keeps configured short acknowledgement timeout");
    }
}

await using (var session = PiSessions.Coordinator(fakeOptions()))
{
    await session.StartAsync();
    await session.SendPromptAsync("handled");
    await WaitFor(() => session.Snapshot.Activity == ActivityState.Idle);
    Check(session.Snapshot.Activity == ActivityState.Idle && session.Snapshot.LastResult == TaskResult.Unknown,
        "handled command does not wait for nonexistent agent_settled");
    await session.SendPromptAsync("error");
    await WaitFor(() => session.Snapshot.LastResult == TaskResult.Failed);
    Check(session.Snapshot.LastError == "provider failed" && session.Snapshot.Activity == ActivityState.Idle,
        "model failure is distinct from command acknowledgement");
}

await using (var session = PiSessions.Coordinator(fakeOptions()))
{
    await session.StartAsync();
    try { await session.SendPromptAsync("crash"); throw new Exception("crash missed"); }
    catch (RpcException) { }
    await WaitFor(() => session.Snapshot.Connection == ConnectionState.Faulted);
    Check(session.Snapshot.LastResult == TaskResult.Unknown, "process fault is distinct from model failure");
}

await using (var adapter = await PiSessions.ConnectAsync(fakeOptions()))
{
    var events = new ConcurrentQueue<SessionEvent>();
    adapter.Event += (_, ev) => events.Enqueue(ev);
    await adapter.SendAsync("stream");
    await WaitFor(() => adapter.LastOutcome == SessionOutcome.Completed);
    Check(adapter.Connection == SessionConnection.Connected && adapter.Activity == SessionActivity.Idle,
        "IChatSession maps connection and activity");
    Check(events.OfType<SessionEvent.TextAppended>().Any(e => e.Delta == "Hel") &&
        events.OfType<SessionEvent.ThinkingAppended>().Any(e => e.Delta == "thought") &&
        events.OfType<SessionEvent.ToolCallAppended>().Any(), "IChatSession emits distinct content deltas");
    Check(events.OfType<SessionEvent.MessageFinished>().Count() == 1 &&
        events.OfType<SessionEvent.MessageFinished>().Single().FinalMessage.Blocks[0] is TextBlock { Text: "Final answer" } &&
        events.OfType<SessionEvent.MessageFinished>().Single().CumulativeUsage?.OutputTokens == 2,
        "IChatSession final message and usage are authoritative");
    Check(events.OfType<SessionEvent.ToolCallStatusChanged>().Any(e => e.Status == ToolCallStatus.Completed) &&
        events.OfType<SessionEvent.TurnSettled>().Single().Outcome == SessionOutcome.Completed,
        "IChatSession maps tool completion and settled outcome");
}

await using (var adapter = await PiSessions.ConnectAsync(fakeOptions()))
{
    var events = new ConcurrentQueue<SessionEvent>();
    adapter.Event += (_, ev) => events.Enqueue(ev);
    await adapter.SendAsync("hold");
    await adapter.SendAsync("queued");
    Check(adapter.QueuedCount == 1 && adapter.CanStop, "IChatSession exposes accepted queue");
    await adapter.StopAsync();
    Check(events.OfType<SessionEvent.QueueCleared>().Any(e => e.ClearedTexts.SequenceEqual(["queued"])) &&
        adapter.QueuedCount == 0 && adapter.LastOutcome == SessionOutcome.Interrupted,
        "IChatSession returns cleared queue without replacing draft");
}

await using (var adapter = await PiSessions.ConnectAsync(fakeOptions()))
{
    var events = new ConcurrentQueue<SessionEvent>();
    adapter.Event += (_, ev) => events.Enqueue(ev);
    await adapter.SendAsync("error");
    await WaitFor(() => adapter.LastOutcome == SessionOutcome.Failed);
    Check(events.OfType<SessionEvent.SessionError>().Any(e => e.Kind == SessionErrorKind.ModelFailed) &&
        events.OfType<SessionEvent.TurnSettled>().Single().Outcome == SessionOutcome.Failed,
        "IChatSession maps model error separately from process fault");
}

foreach (var (request, expectedKind) in new[]
{
    ("bad_event", SessionErrorKind.ProtocolError),
    ("crash", SessionErrorKind.ProcessExited)
})
{
    await using var adapter = await PiSessions.ConnectAsync(fakeOptions());
    var events = new ConcurrentQueue<SessionEvent>();
    adapter.Event += (_, ev) => events.Enqueue(ev);
    try { await adapter.SendAsync(request); } catch (RpcException) { }
    await WaitFor(() => adapter.Connection == SessionConnection.Faulted &&
        events.OfType<SessionEvent.SessionError>().Any(e => e.Kind == expectedKind));
    var errorKinds = string.Join(",", events.OfType<SessionEvent.SessionError>().Select(e => e.Kind));
    Check(events.OfType<SessionEvent.SessionError>().Any(e => e.Kind == expectedKind),
        $"IChatSession classifies {request} as {expectedKind}; observed {errorKinds}");
}

await using (var session = PiSessions.Coordinator(fakeOptions()))
{
    await session.StartAsync();
    await session.SendPromptAsync("state_fail");
    await Task.Delay(150);
    Check(session.Snapshot.Activity == ActivityState.Running && session.Snapshot.LastResult == TaskResult.Unknown,
        "P2-4 failed get_state does not falsely settle an accepted prompt");
    await session.StopAsync();
}

await using (var adapter = await PiSessions.ConnectAsync(fakeOptions()))
{
    var cleared = new ConcurrentQueue<SessionEvent.QueueCleared>();
    adapter.Event += (_, ev) => { if (ev is SessionEvent.QueueCleared item) cleared.Enqueue(item); };
    await adapter.SendAsync("hold");
    await WaitFor(() => adapter.Activity == SessionActivity.Running);
    await adapter.SendAsync("first queued");
    await adapter.StopAsync();
    Check(cleared.Count == 1, "P2-7 first recovered queue is emitted once");
    await adapter.SendAsync("hold");
    await WaitFor(() => adapter.Activity == SessionActivity.Running);
    await adapter.SendAsync("second queued");
    await adapter.StopAsync();
    Check(cleared.Count == 2 && cleared.Last().ClearedTexts.SequenceEqual(["second queued"]),
        "P2-7 next stop reports only newly recovered text");
}

await using (var adapter = await PiSessions.ConnectAsync(fakeOptions()))
{
    var events = new ConcurrentQueue<SessionEvent>();
    adapter.Event += (_, ev) => events.Enqueue(ev);
    await adapter.SendAsync("hold_aborted");
    await WaitFor(() => adapter.Activity == SessionActivity.Running);
    await adapter.StopAsync();
    await WaitFor(() => events.OfType<SessionEvent.TurnSettled>().Any());
    Check(events.OfType<SessionEvent.TurnSettled>().Last().Outcome == SessionOutcome.Interrupted &&
        !events.OfType<SessionEvent.SessionError>().Any(),
        "F05 user abort with request aborted message ends neutrally");
}

await A13ContractTests.RunAsync(root, fake, dotnet);
await PersistentTests.RunAsync(root, fake, dotnet);
await UserExtensionTests.RunFakeAsync(root, fake, dotnet);
await SettingsAndDiagnosticsTests.RunAsync(root, fake, dotnet);
await ExtensionUiTests.RunAsync(root, fake, dotnet);

await ExtensionUiTests.RunRealAsync(root, node, cli);
await BridgeIntegrationTests.RunAsync(root, node, cli);
await PersistentTests.RunRealAsync(root, node, cli);
await ProjectForkTests.RunAsync(root, node, cli);
await UserExtensionTests.RunRealAsync(root, node, cli);
var work = Path.Combine(root, "tmp", "real-core-smoke");
Directory.CreateDirectory(work);
await using (var session = PiSessions.Coordinator(new PiRpcOptions(node,
    [cli, "--mode", "rpc", "--no-session", "--no-extensions"], work,
    EnvironmentVariables: new Dictionary<string, string>
    {
        ["PI_CODING_AGENT_DIR"] = Path.Combine(work, "agent-" + Guid.NewGuid().ToString("N"))
    })))
{
    await session.StartAsync();
    var state = await session.QueryStateAsync();
    var models = await session.QueryAvailableModelsAsync();
    Check(state.Success && models.Success && session.Snapshot.Connection == ConnectionState.Connected,
        "real Pi Core connection and harmless queries");
    await session.StopAsync();
    Check(session.Snapshot.Activity == ActivityState.Idle && session.Snapshot.LastResult == TaskResult.Interrupted,
        "real Pi clear_queue response passes required steering/followUp array parsing before abort");
    var configured = state.Data is { } data && data.TryGetProperty("model", out var model) && model.ValueKind == System.Text.Json.JsonValueKind.Object;
    Console.WriteLine($"Real model configured: {configured}");
    if (Environment.GetEnvironmentVariable("FUFUCODE_REAL_MODEL_TEST") == "1")
    {
        if (!configured) Console.WriteLine("SKIP real model dialogue: no model selected in Pi state");
        else
        {
            foreach (var request in new[] { "Reply with exactly: ONE", "Reply with exactly: TWO" })
            {
                var assistantsBefore = session.Snapshot.Messages.Count(m => m.Role == "assistant");
                await session.SendPromptAsync(request);
                var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(2);
                while (session.Snapshot.LastResult == TaskResult.Unknown && DateTime.UtcNow < deadline)
                    await Task.Delay(100);
                var result = session.Snapshot;
                Check(result.LastResult == TaskResult.Completed &&
                    result.Messages.Count(m => m.Role == "assistant") == assistantsBefore + 1 &&
                    result.Messages.Last(m => m.Role == "assistant").Content.OfType<PiTextBlock>().Any(b => b.Text.Length > 0),
                    $"real model turn completed: {result.LastError}");
            }
        }
    }
}
