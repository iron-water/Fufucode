using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fufucode.Core;
using Fufucode.Pi;
using Microsoft.Data.Sqlite;

internal static class D1BackendTests
{
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        Console.WriteLine("PASS " + name);
    }

    private static async Task Wait(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("D1 expected state not reached.");
            await Task.Delay(20);
        }
    }

    public static async Task RunAsync(string root)
    {
        var scratch = Path.Combine(root, "tmp", "d1-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        Console.WriteLine("EVIDENCE " + scratch);
        var node = Environment.GetEnvironmentVariable("D1_PI_NODE") ?? Path.Combine(root, "runtime/node/node.exe");
        var cli = Environment.GetEnvironmentVariable("D1_PI_CLI") ?? Path.Combine(root, "runtime/pi/node_modules/@earendil-works/pi-coding-agent/dist/bundle/cli.js");
        var omp = Environment.GetEnvironmentVariable("D1_OMP_EXE") ?? Path.Combine(root, "runtime/omp/omp.exe");
        var layout = new AppDataLayout(Path.Combine(scratch, "data"));
        var metadata = new DesktopMetadataStore(layout);
        var old = metadata.CreateSession(metadata.GetOrCreateProject(scratch), "old Pi");
        // Recreate the previous schema so migration is exercised against a real existing database.
        using (var connection = new SqliteConnection("Data Source=" + metadata.DatabasePath))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE sessions DROP COLUMN backend_id; ALTER TABLE sessions DROP COLUMN backend_session_id; PRAGMA user_version=3;";
            command.ExecuteNonQuery();
        }
        metadata = new DesktopMetadataStore(layout);
        Check(metadata.GetSession(old.Id).Backend == BackendId.Pi && Directory.GetFiles(layout.Root, "desktop.db.before-v4-*.bak").Length == 1,
            "v3 migration defaults old chats to Pi and backs up database");
        await using var server = new ModelServer();
        Environment.SetEnvironmentVariable("D1_FIXTURE_KEY", "local-fixture-only");
        var manager = new PersistentSessionManager(node, cli, layout,
            approvalBridgePath: Path.Combine(root, "extensions/fufucode-bridge"),
            credentialResolver: new EnvironmentCredentialResolver(), ompExecutablePath: omp);
        manager.Settings.Update(_ => new AppSettings(SelectedModel: "d1/d1-fixture",
            Endpoint: new CompatibleEndpoint("d1", server.BaseUrl, "d1-fixture", "D1_FIXTURE_KEY")));
        string? piFile = null, piHash = null;
        foreach (var backend in new[] { BackendId.Pi, BackendId.Omp })
        {
            string savedId, engineId, file;
            await using (var session = await manager.CreateAsync(scratch, backend.ToString(), backend: backend))
            {
                savedId = session.Session.Id;
                engineId = session.Session.BackendSessionId!;
                Check(session.Session.Backend == backend && session.Coordinator.ApprovalAvailability == ApprovalAvailability.Verified,
                    backend + " real create and approval bridge discovery");
                var uiEvents = new ConcurrentQueue<string>();
                session.Chat.Event += (_, ev) => uiEvents.Enqueue(ev.GetType().Name);
                await session.Coordinator.SendPromptAsync("Reply with fixture text.");
                await Wait(() => session.Coordinator.Snapshot.LastResult == TaskResult.Completed);
                Check(session.Coordinator.Snapshot.Messages.Any(m => m.Content.OfType<PiTextBlock>().Any(t => t.Text == "D1 fixture answer")) &&
                    uiEvents.Count(e => e == "TurnSettled") == 1, backend + " actual protocol text and single UI settled event");
                var allow = true;
                session.Coordinator.ExtensionUiRequestReceived += request =>
                    _ = session.Coordinator.RespondToExtensionUiAsync(request, PiExtensionUiResponse.Confirm(allow));
                foreach (var permitted in new[] { true, false })
                {
                    allow = permitted;
                    var output = Path.Combine(scratch, backend + (permitted ? "-allowed.txt" : "-denied.txt"));
                    server.Steps.Enqueue((output, 0));
                    server.Steps.Enqueue((null, 0));
                    await session.Coordinator.SendPromptAsync("Execute the requested write tool.");
                    await Wait(() => session.Coordinator.Snapshot.LastResult == TaskResult.Completed);
                    Check(File.Exists(output) == permitted && (!permitted || File.ReadAllText(output) == "D1 approved"),
                        backend + (permitted ? " approval allows actual write" : " rejection prevents write"));
                }
                await session.Coordinator.SetExecutionModeAsync("manual");
                await Wait(() => session.Coordinator.Snapshot.Activity == ActivityState.Idle);
                server.Steps.Enqueue((null, 15000));
                var before = server.Requests;
                await session.Coordinator.SendPromptAsync("Hold this turn.");
                await Wait(() => server.Requests > before);
                await session.Coordinator.SendPromptAsync("Never execute queued text.", PromptDelivery.FollowUp);
                await session.Coordinator.StopAsync();
                Check(session.Coordinator.Snapshot.LastResult == TaskResult.Interrupted &&
                    session.Coordinator.Snapshot.RecoverableQueue.Contains("Never execute queued text."), backend + " stop withdraws queue");
                await session.RefreshHistoryAsync();
                file = manager.Metadata.GetSession(savedId).FilePath!;
                Check(File.Exists(file), backend + " history file recorded");
                try { await manager.ResumeAsync(savedId); throw new Exception("Duplicate writer allowed"); }
                catch (InvalidOperationException e) when (e.Message.Contains("already owned")) { Check(true, backend + " excludes second writer"); }
                manager.Metadata.SetDraft(savedId, "draft " + backend);
            }
            await using (var resumed = await manager.ResumeAsync(savedId))
            {
                Check(resumed.Session.Backend == backend && resumed.Session.BackendSessionId == engineId &&
                    resumed.History.CurrentBranchMessages.Count >= 2 && manager.Metadata.GetDraft(savedId) == "draft " + backend,
                    backend + " close/reopen preserves engine identity, history and draft");
                await resumed.Coordinator.SendPromptAsync("Continue the same chat.");
                await Wait(() => resumed.Coordinator.Snapshot.LastResult == TaskResult.Completed);
            }
            // Real engine exits while a command is pending. Hold the same desktop lease.
            var engineArgs = new List<string>();
            if (backend == BackendId.Pi) engineArgs.Add(cli);
            engineArgs.AddRange(["--mode", "rpc", "--model", "d1/d1-fixture", "--no-extensions", "--no-skills",
                backend == BackendId.Pi ? "--session" : "--resume", file,
                "-e", Path.Combine(root, "extensions/fufucode-bridge/index.ts"),
                "-e", Path.Combine(root, "tests/fixtures/d1-crash-extension.ts")]);
            if (backend == BackendId.Omp) engineArgs.AddRange(["--no-title", "--no-rules", "--no-lsp"]);
            using (var lease = SessionLease.Acquire(Path.GetDirectoryName(file)!, savedId))
            await using (var crashed = new ChatSessionCoordinator(new PiRpcOptions(backend == BackendId.Pi ? node : omp,
                engineArgs, scratch, EnvironmentVariables: new Dictionary<string, string>
                {
                    ["PI_CODING_AGENT_DIR"] = backend == BackendId.Pi ? layout.AgentDirectory : Path.Combine(layout.Root, "omp-agent"),
                    ["D1_FIXTURE_KEY"] = "local-fixture-only"
                }, RequireApprovalBridge: true, Backend: backend)))
            {
                await crashed.StartAsync();
                try { await crashed.SendPromptAsync("/d1.crash"); throw new Exception("Crash command unexpectedly returned"); }
                catch (PiRpcException) { }
                await Wait(() => crashed.Snapshot.Connection == ConnectionState.Faulted);
                Check(crashed.Snapshot.LastResult != TaskResult.Completed, backend + " exit faults pending command without false success");
            }
            var requestCount = server.Requests;
            await using (var recovered = await manager.ResumeAsync(savedId))
                Check(recovered.Session.BackendSessionId == engineId && server.Requests == requestCount,
                    backend + " process-exit recovery does not replay a prompt");
            manager.Settings.Update(s => s with { SelectedModel = "d1/model-that-does-not-exist" });
            try { await manager.CreateAsync(scratch, backend: backend); throw new Exception("Invalid model accepted"); }
            catch (Exception e) when (e is PiRpcException or InvalidOperationException)
            { Check(true, backend + " model error refuses chat creation"); }
            manager.Settings.Update(s => s with { SelectedModel = "d1/d1-fixture" });
            await using (var recovered = await manager.ResumeAsync(savedId))
                Check(recovered.Chat.CanSend, backend + " valid model/session remains usable after model error");
            if (backend == BackendId.Pi) { piFile = file; piHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))); }
            else Check(piHash == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(piFile!))), "OMP operations preserve Pi history bytes");
        }
        var missing = new PersistentSessionManager(node, cli, layout, ompExecutablePath: Path.Combine(scratch, "missing.exe"));
        var ompSession = manager.Metadata.ListSessions(manager.Metadata.GetOrCreateProject(scratch).Id).First(s => s.Backend == BackendId.Omp);
        try { await missing.ResumeAsync(ompSession.Id); throw new Exception("Missing OMP silently substituted"); }
        catch (FileNotFoundException) { Check(manager.Metadata.GetSession(ompSession.Id).Backend == BackendId.Omp, "missing OMP refuses substitution and preserves metadata"); }
        try { manager.Metadata.BindBackendSession(ompSession.Id, "different-id"); throw new Exception("Engine identity changed"); }
        catch (InvalidOperationException) { Check(true, "engine identity cannot be rebound"); }
    }

    private sealed class ModelServer : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        public readonly ConcurrentQueue<(string? Path, int Delay)> Steps = new();
        private int _requests;
        public int Requests => Volatile.Read(ref _requests);
        public string BaseUrl { get; }
        public ModelServer()
        {
            var socket = new TcpListener(IPAddress.Loopback, 0); socket.Start();
            var port = ((IPEndPoint)socket.LocalEndpoint).Port; socket.Stop();
            BaseUrl = "http://127.0.0.1:" + port;
            _listener.Prefixes.Add(BaseUrl + "/"); _listener.Start(); _ = Run();
        }
        private async Task Run()
        {
            try { while (_listener.IsListening) { var context = await _listener.GetContextAsync(); _ = Respond(context); } }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException) { }
        }
        private async Task Respond(HttpListenerContext context)
        {
            try
            {
                using var reader = new StreamReader(context.Request.InputStream);
                var body = await reader.ReadToEndAsync();
                using var json = JsonDocument.Parse(body);
                if (json.RootElement.GetProperty("model").GetString() != "d1-fixture") throw new Exception("Unexpected model");
                Interlocked.Increment(ref _requests);
                Steps.TryDequeue(out var step);
                if (step.Delay > 0) await Task.Delay(step.Delay);
                context.Response.ContentType = "text/event-stream";
                object delta = step.Path is null ? new { role = "assistant", content = "D1 fixture answer" } :
                    new { role = "assistant", tool_calls = new[] { new { index = 0, id = "call_" + Guid.NewGuid().ToString("N"),
                        type = "function", function = new { name = "write", arguments = JsonSerializer.Serialize(new { path = step.Path, content = "D1 approved" }) } } } };
                string Frame(object value, string? finish) => "data: " + JsonSerializer.Serialize(new
                {
                    id = "d1", @object = "chat.completion.chunk", created = 1, model = "d1-fixture",
                    choices = new[] { new { index = 0, delta = value, finish_reason = finish } },
                    usage = new { prompt_tokens = 10, completion_tokens = 5, total_tokens = 15 }
                }) + "\n\n";
                var bytes = Encoding.UTF8.GetBytes(Frame(delta, null) + Frame(new { }, step.Path is null ? "stop" : "tool_calls") + "data: [DONE]\n\n");
                await context.Response.OutputStream.WriteAsync(bytes);
                context.Response.Close();
            }
            catch (Exception e) when (e is HttpListenerException or IOException or ObjectDisposedException) { }
        }
        public ValueTask DisposeAsync() { _listener.Close(); return ValueTask.CompletedTask; }
    }
}
