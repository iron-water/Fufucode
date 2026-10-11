using Fufucode.Pi;

var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
var fake = Path.Combine(root, "tests/Fufucode.FakePi/bin/Debug/net10.0/Fufucode.FakePi.dll");
var dotnet = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet/dotnet.exe");
PiRpcOptions fakeOptions(int maxBytes = 8 * 1024 * 1024) => new(dotnet, [fake], root, MaxRecordBytes: maxBytes);
void Check(bool condition, string name) { if (!condition) throw new Exception(name); Console.WriteLine($"PASS {name}"); }

await using (var client = await PiRpcClient.StartAsync(fakeOptions()))
{
    var first = client.RequestAsync("reverse");
    var second = client.RequestAsync("reverse");
    var results = await Task.WhenAll(first, second);
    Check(results[0].Data!.Value.GetProperty("order").GetInt32() == 1 && results[1].Data!.Value.GetProperty("order").GetInt32() == 2, "out of order IDs");
    Check(results[1].Data!.Value.GetProperty("text").GetString() == "中\u2028文\u2029字", "split UTF-8 and Unicode separators");
    await client.RequestAsync("emit");
    var ev = await client.Events.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
    Check(ev.Type == "mystery" && ev.Record.GetProperty("text").GetString() == "中\u2028文\u2029字" && ev.Generation == client.Generation, "event routing and generation");
    await client.RequestAsync("emit_multi");
    var a = await client.Events.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
    var b = await client.Events.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
    Check(a.Type == "first_event" && b.Type == "second_event", "multiple records in one pipe write");
    await client.RequestAsync("stderr");
    Check(client.StderrTail.Length > 0 && client.StderrTail.Length <= 65536, "stderr drained with bounded diagnostics");
    try { await client.RequestAsync("hang", timeout: TimeSpan.FromMilliseconds(250)); throw new Exception("timeout missed"); }
    catch (PiRpcException error) { Check(error.Message.Contains("timed out"), "deadline ends request"); }
    using (var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(250)))
    {
        try { await client.RequestAsync("hang", cancellationToken: cancel.Token); throw new Exception("cancellation missed"); }
        catch (OperationCanceledException) { Check(true, "caller cancellation ends request wait"); }
    }
    Check((await client.RequestAsync("get_state")).Success, "connection survives request deadline");
}

await using (var client = await PiRpcClient.StartAsync(fakeOptions()))
{
    var pending = client.RequestAsync("hang");
    await Task.Delay(100);
    await client.StopAsync();
    try { await pending.WaitAsync(TimeSpan.FromSeconds(2)); throw new Exception("shutdown wait missed"); }
    catch (PiRpcException) { Check(true, "shutdown ends pending request"); }
}

await using (var client = await PiRpcClient.StartAsync(fakeOptions()))
{
    try { await client.RequestAsync("die"); throw new Exception("process exit missed"); }
    catch (PiRpcException error) { Check(error.InnerException is not null, "process exit ends pending request"); }
}

await using (var client = await PiRpcClient.StartAsync(fakeOptions()))
{
    Check((await client.RequestAsync("respond_exit")).Success, "final response survives immediate process exit");
}

await using (var client = await PiRpcClient.StartAsync(fakeOptions()))
{
    try { await client.RequestAsync("malformed"); throw new Exception("malformed JSON missed"); }
    catch (PiRpcException error) { Check(error.InnerException is not null, "malformed JSON is fatal"); }
}

foreach (var command in new[] { "no_id", "missing_fields", "wrong_command" })
{
    await using var client = await PiRpcClient.StartAsync(fakeOptions());
    try { await client.RequestAsync(command, timeout: TimeSpan.FromSeconds(2)); throw new Exception($"{command} missed"); }
    catch (PiRpcException error) { Check(error.InnerException is not TimeoutException, $"{command} ends pending request"); }
}

await using (var client = await PiRpcClient.StartAsync(fakeOptions(1024)))
{
    try { await client.RequestAsync("large"); throw new Exception("record limit missed"); }
    catch (PiRpcException error) { Check(error.InnerException is not null, "record size limit is explicit"); }
}

var node = Path.Combine(root, "runtime/node/node.exe");
var unobserved = 0;
void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs error) => Interlocked.Increment(ref unobserved);
TaskScheduler.UnobservedTaskException += OnUnobserved;
try
{
    await using (var client = await PiRpcClient.StartAsync(fakeOptions()))
    {
        var requests = Enumerable.Range(0, 16).Select(async _ =>
        {
            try { await client.RequestAsync("hang"); return false; }
            catch (PiRpcException) { return true; }
        }).ToArray();
        await client.StopAsync();
        Check((await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(3))).All(failed => failed),
            "concurrent shutdown preserves request failures");
    }
    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
    Check(unobserved == 0, "concurrent shutdown leaves no unobserved waiter faults");
}
finally { TaskScheduler.UnobservedTaskException -= OnUnobserved; }

var cli = Path.Combine(root, "runtime/pi/node_modules/@earendil-works/pi-coding-agent/dist/bundle/cli.js");
var work = Path.Combine(root, "tmp", "real-pi-smoke-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(work);
var agentDir = Path.Combine(work, "agent");
var extensions = Path.Combine(agentDir, "extensions");
Directory.CreateDirectory(extensions);
await File.WriteAllTextAsync(Path.Combine(extensions, "must-not-load.ts"),
    "throw new Error('R-003 unexpected extension load');");
try
{
    await using var client = await PiRpcClient.StartAsync(new PiRpcOptions(node,
        [cli, "--mode", "rpc", "--no-session", "--no-extensions"], work,
        EnvironmentVariables: new Dictionary<string, string> { ["PI_CODING_AGENT_DIR"] = agentDir }));
    var models = await client.RequestAsync("get_available_models");
    Check(models.Success && models.Command == "get_available_models" && models.Data?.TryGetProperty("models", out _) == true, "real Pi models query");
    var state = await client.RequestAsync("get_state");
    Check(state.Success && state.Data?.TryGetProperty("isStreaming", out _) == true, "real Pi state query");
    Check(!client.StderrTail.Contains("R-003 unexpected extension load", StringComparison.Ordinal),
        "R-003 isolated real Pi ignores a failing auto-discovered extension");
}
finally { Directory.Delete(work, recursive: true); }
