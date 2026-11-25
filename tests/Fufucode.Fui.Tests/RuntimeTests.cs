using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using Fufucode.App.Abstractions;
using Fufucode.Core;
using Fufucode.Core.Backend;
using Fufucode.Core.Security;

namespace Fufucode.Fui.Tests;

internal static class RuntimeTests
{
    public static async Task RunAsync()
    {
        var root = Environment.GetEnvironmentVariable("FUFUCODE_TEST_ROOT") is { Length: > 0 } fixtures
            ? Directory.CreateDirectory(Path.Combine(fixtures, "tui-" + Guid.NewGuid().ToString("N")))
            : Directory.CreateTempSubdirectory("fui-test-");
        var previousRoot = Environment.GetEnvironmentVariable(AppDataLayout.RootEnvironmentVariable);
        var previousJournal = FileChangeJournal.Root;
        var layout = new AppDataLayout(Path.Combine(root.FullName, "data"));
        var credential = "FUFUCODE_TEST_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(AppDataLayout.RootEnvironmentVariable, layout.Root);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var ct = timeout.Token;
            var project = root.CreateSubdirectory("project").FullName;
            var other = root.CreateSubdirectory("other").FullName;
            var metadata = new DesktopMetadataStore(layout);
            var owner = metadata.GetOrCreateProject(project);
            new BackendPreferenceStore(layout).SetDefault(EngineId.Omp);
            string? id;
            Task<FumoTuiSession> Open(string? session = null, bool latest = false, string? path = null) =>
                FumoTuiSession.OpenAsync(path ?? project, layout.Root, session, latest, ct);

            // New ignores a saved OMP preference; --continue is never an implicit create.
            await Reject<InvalidOperationException>(() => Open(latest: true));
            Check(metadata.ListSessions(owner.Id).Count == 0, "missing --continue created a replacement");
            await using (var empty = await Open())
            {
                id = empty.Id;
                Check(metadata.GetSession(id).Backend == BackendId.NativeCSharp && empty.History.Count == 0, "not a new FUMO session");
                while (empty.Events.TryRead(out var initial))
                    Check(initial is not TuiEvent.Message { Id: var key } || !key.StartsWith("plugin:"), "standalone TUI attempted to seed missing optional payloads");
                Check(FileChangeJournal.Root == Path.Combine(layout.Root, "turn-backups"), "journal outside isolated data root");
                await Reject<InvalidOperationException>(() => Open(id)); // live lease
            }
            await using (var empty = await Open(id))
            {
                Check(empty.Id == id && empty.History.Count == 0, "empty session not resumable");
                await empty.SendAsync("unconfigured", ct);
                var run = await Settled(empty, ct);
                Check(run.OfType<TuiEvent.Message>().Any(m => m.Kind == "error" && m.Text.Contains("模型端点")), "model failure hidden");
                Check(run[^1] is TuiEvent.Status { Text: "失败", Busy: false }, "model failure reported as success");
            }
            var foreign = metadata.CreateSession(owner, backend: BackendId.Omp);
            await Reject<InvalidOperationException>(() => Open(foreign.Id));
            await Reject<InvalidOperationException>(() => Open(id, path: other));
            await Reject<KeyNotFoundException>(() => Open("absent"));
            await using (var latest = await Open(latest: true)) Check(latest.Id == id, "latest selected wrong engine");

            File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(other, ".pi")).FullName, "settings.json"), "{}");
            await Reject<ProjectTrustDecisionRequiredException>(() => Open(path: other));
            Check(metadata.ListSessions(metadata.TryGetProject(other)!.Id).Count == 0, "untrusted project started a session");

            await using var endpoint = new Loopback();
            // A random vault name and synthetic environment value exercise the normal resolver, never a user's key.
            var names = new AppSettingsStore(layout);
            Environment.SetEnvironmentVariable(credential, "loopback-fixture");
            names.Update(s => s with { Endpoint = new("fixture", endpoint.Url, "fixture-model", credential) });

            string streamedId;
            await using (var session = await Open())
            {
                streamedId = session.Id;
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                endpoint.When("stream", false, Frame(new { reasoning_content = "think" }) + Frame(new { content = "first" }),
                    release.Task, Frame(new { content = " second" }, "stop"));
                await session.SendAsync("stream", ct);
                var first = await Until(session, e => e is TuiEvent.Message { Kind: "assistant", Text: "first", Append: true }, ct);
                Check(first.OfType<TuiEvent.Message>().Any(m => m.Kind == "thinking" && m.Text == "think"), "missing reasoning delta");
                Check(first.OfType<TuiEvent.Status>().Any(s => s.Busy) && first.OfType<TuiEvent.Status>().All(s => s.Busy), "settled before SSE finished");
                release.SetResult();
                var run = first.Concat(await Settled(session, ct)).ToArray();
                var rows = Rows(run);
                Check(rows.Count(l => l.Kind == "user" && l.Text == "stream") == 1 && rows.Count(l => l.Kind == "assistant" && l.Text == "first second") == 1,
                    "user echoed twice or final text did not replace deltas");
                Check(session.Model == "fixture-model", "session didn't use configured model");

                foreach (var allow in new[] { false, true })
                {
                    var name = allow ? "allowed.txt" : "refused.txt";
                    endpoint.Tool(name, name);
                    endpoint.When(name, true, Frame(new { content = "after tool" }, "stop"));
                    await session.SendAsync(name, ct);
                    var pre = await Until(session, e => e is TuiEvent.Approval, ct, endpoint);
                    var approval = (TuiEvent.Approval)pre[^1];
                    Check(approval.Text.Contains("write_file") && approval.Text.Contains(name), "approval missing arguments");
                    Check(!File.Exists(Path.Combine(project, name)), "unapproved tool already ran");
                    var wrongGeneration = Guid.NewGuid().ToString("N") + approval.Id[approval.Id.IndexOf(':')..];
                    await Reject<InvalidOperationException>(() => session.RespondAsync(wrongGeneration, true, ct));
                    await session.RespondAsync(approval.Id, allow, ct);
                    await Reject<InvalidOperationException>(() => session.RespondAsync(approval.Id, !allow, ct));
                    var post = await Settled(session, ct);
                    Check(post.OfType<TuiEvent.ApprovalClosed>().Any(a => a.Id == approval.Id), "decision did not close matching ID");
                    var tool = Rows(pre.Concat(post)).Single(l => l.Kind == "tool");
                    Check(tool.Text.Contains(name) && tool.Text.Contains(allow ? "[完成]" : "[失败]"), "missing authoritative tool result");
                    Check(allow ? File.ReadAllText(Path.Combine(project, name)) == "fixture" : !File.Exists(Path.Combine(project, name)), "wrong approval side effect");
                }

                const string blocked = "policy-forbidden.txt";
                new SecurityPolicyStore(layout.Root).Update(policy => policy with
                {
                    FileRules = [new(blocked, Write: SecurityAction.Deny), .. policy.FileRules]
                });
                endpoint.Tool("policy", blocked);
                endpoint.When("policy", true, Frame(new { content = "after policy refusal" }, "stop"));
                await session.SendAsync("policy", ct);
                var denied = await Settled(session, ct);
                Check(!denied.OfType<TuiEvent.Approval>().Any() && !File.Exists(Path.Combine(project, blocked)) &&
                    Rows(denied).Any(l => l.Kind == "tool" && l.Text.Contains("[失败]")), "security policy bypassed or became user-overridable");

                endpoint.Tool("stop", "stopped.txt");
                await session.SendAsync("stop", ct);
                var awaiting = (TuiEvent.Approval)(await Until(session, e => e is TuiEvent.Approval, ct))[^1];
                await Reject<InvalidOperationException>(() => session.SendAsync("late ordinary submission", ct));
                Check((await session.ExecuteAsync("queue", "", ct)).Notice == "队列为空", "ordinary send entered the native follow-up queue");
                var stopping = session.StopAsync(ct);
                await Reject<InvalidOperationException>(() => session.RespondAsync(awaiting.Id, true, ct));
                await stopping;
                var stopped = await Settled(session, ct);
                Check(stopped[^1] is TuiEvent.Status { Text: "已中断" }, "stop did not publish Interrupted: " + stopped[^1]);
                var closed = stopped.FindIndex(e => e is TuiEvent.ApprovalClosed a && a.Id == awaiting.Id);
                Check(closed >= 0 && closed < stopped.FindIndex(e => e is TuiEvent.Status { Text: "正在停止" }),
                    "stopping was announced with the cancelled approval still visible");
                Check(!File.Exists(Path.Combine(project, "stopped.txt")), "stop approved a write");
                await Reject<InvalidOperationException>(() => session.RespondAsync(awaiting.Id, true, ct));

                endpoint.Tool("after stop", "next-turn.txt");
                endpoint.When("after stop", true, Frame(new { content = "after new refusal" }, "stop"));
                await session.SendAsync("after stop", ct);
                var next = (TuiEvent.Approval)(await Until(session, e => e is TuiEvent.Approval, ct))[^1];
                await Reject<InvalidOperationException>(() => session.RespondAsync(awaiting.Id, true, ct));
                await session.RespondAsync(next.Id, false, ct);
                await Settled(session, ct);
                Check(!File.Exists(Path.Combine(project, "next-turn.txt")), "a stopped turn's decision affected the next approval");
            }

            await CheckWorkerHandoffAsync(project, layout, endpoint, ct);
            await using (var resumed = await Open(streamedId))
            {
                Check(resumed.History.Any(l => l is { Kind: "assistant", Text: "first second" }) &&
                    resumed.History.Any(l => l is { Kind: "user", Text: "stream" }) &&
                    resumed.History.Any(l => l is { Kind: "thinking", Text: "think" }) &&
                    resumed.History.Any(l => l.Kind == "tool" && l.Text.Contains("allowed.txt")), "native persisted history not restored");
                endpoint.Tool("close", "closed.txt");
                await resumed.SendAsync("close", ct);
                var pending = (TuiEvent.Approval)(await Until(resumed, e => e is TuiEvent.Approval, ct))[^1];
                // A stalled picker may overlap an approval. Closing the terminal owns both lifetimes.
                var catalogStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var releaseCatalog = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                endpoint.HoldModelList = async token =>
                {
                    catalogStarted.TrySetResult();
                    await releaseCatalog.Task.WaitAsync(token);
                    return false;
                };
                var choices = resumed.ChoicesAsync("model", ct);
                try
                {
                    await catalogStarted.Task.WaitAsync(ct);
                    var count = metadata.ListSessions(owner.Id).Count;
                    var queuedAction = resumed.ExecuteAsync("new", "", ct);
                    var close = resumed.DisposeAsync().AsTask();
                    await Reject<ObjectDisposedException>(() => resumed.RespondAsync(pending.Id, true, ct));
                    await close.WaitAsync(TimeSpan.FromSeconds(5), ct);
                    await Reject<OperationCanceledException>(() => choices);
                    await Reject<OperationCanceledException>(() => queuedAction);
                    Check(metadata.ListSessions(owner.Id).Count == count, "a waiting workbench command created a session after exit");
                }
                finally { releaseCatalog.TrySetResult(); endpoint.HoldModelList = null; }
            }
            Check(!File.Exists(Path.Combine(project, "closed.txt")), "exit approved a pending write");
            await using (var released = await Open(streamedId)) Check(released.Id == streamedId, "exit didn't release lease");

            await ImageAttachmentTests.RunAsync(project, layout, credential, ct);

            var damaged = metadata.CreateSession(owner);
            var file = NativeSessionFactory.SessionFilePath(layout, owner.Id, damaged.Id);
            metadata.UpdateSessionFile(damaged.Id, file);
            await Reject<FileNotFoundException>(() => Open(damaged.Id));
            File.WriteAllText(file, "{");
            await Reject<JsonException>(() => Open(latest: true));
            Check(File.ReadAllText(file) == "{", "failed resume changed corrupt history");

            const string brokenPlugin = "chat.interactive-ui";
            var package = Directory.CreateDirectory(layout.PluginDirectory(brokenPlugin));
            File.WriteAllText(Path.Combine(package.FullName, "manifest.json"), "{\"id\":\"wrong-fixture-id\"}");
            await using (var withPlugin = await Open())
            {
                Check(withPlugin.Events.TryRead(out var problem) && problem is TuiEvent.Message { Id: "plugin:" + brokenPlugin, Text.Length: > 0 },
                    "an installed plugin's actual activation failure was hidden");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(credential, null);
            Environment.SetEnvironmentVariable(AppDataLayout.RootEnvironmentVariable, previousRoot);
            FileChangeJournal.Configure(Path.GetDirectoryName(previousJournal)!);
            root.Delete(recursive: true); // Owned, unique test fixture only.
        }
    }

    private static async Task CheckWorkerHandoffAsync(string project, AppDataLayout layout, Loopback endpoint, CancellationToken ct)
    {
        var host = new FumoProjectRuntime(layout);
        try
        {
            var opened = await host.Manager.CreateAsync(project, backend: BackendId.NativeCSharp, cancellationToken: ct);
            await using var session = await FumoTuiSession.AttachAsync(host, opened, true, false, ct);
            using var release = new ManualResetEventSlim();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void HoldWorker() { entered.TrySetResult(); release.Wait(ct); }
            var worker = (IPromptHandledSession)opened.Chat;
            worker.PromptHandled += HoldWorker;
            try
            {
                // Core calls this after TurnSettled, before releasing the old CTS or promoting its queue.
                endpoint.When("worker handoff", false, Frame(new { content = "finished" }, "stop"));
                await session.SendAsync("worker handoff", ct);
                await entered.Task.WaitAsync(ct);
                while (session.Events.TryRead(out var e))
                    Check(e is not TuiEvent.Status { Busy: false }, "FUI announced idle while an ordinary submission would still be queued");
                await Reject<InvalidOperationException>(() => session.SendAsync("must not queue", ct));
                await Reject<InvalidOperationException>(() => session.SendWithImagesAsync("", [ImageAttachmentTests.Png], ct));
                await Reject<InvalidOperationException>(() => session.ExecuteAsync("queue", "missed turn", ct));
                await Reject<InvalidOperationException>(() => session.ExecuteAsync("steer", "missed turn", ct));
                Check((await session.ExecuteAsync("queue", "", ct)).Notice == "队列为空", "submission after turn settlement entered the queue");
            }
            finally { worker.PromptHandled -= HoldWorker; release.Set(); }
            await Settled(session, ct);
            endpoint.When("next ordinary turn", false, Frame(new { content = "next turn ran" }, "stop"));
            await session.SendAsync("next ordinary turn", ct);
            Check((await Settled(session, ct)).OfType<TuiEvent.Message>().Any(m => m.Text == "next turn ran"),
                "ordinary submission failed after native ownership was released");

            var releaseStream = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            endpoint.When("queued handoff", false, Frame(new { content = "streaming" }), releaseStream.Task, Frame(new { content = " done" }, "stop"));
            entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            release.Reset();
            worker.PromptHandled += HoldWorker;
            Task stopping;
            try
            {
                await session.SendAsync("queued handoff", ct);
                await Until(session, e => e is TuiEvent.Message { Text: "streaming" }, ct);
                await session.ExecuteAsync("queue", "cancelled before promotion", ct);
                releaseStream.SetResult();
                await entered.Task.WaitAsync(ct);
                stopping = session.StopAsync(ct);
            }
            finally { worker.PromptHandled -= HoldWorker; release.Set(); releaseStream.TrySetResult(); }
            await stopping.WaitAsync(ct);
            var tail = new List<TuiEvent>();
            while (session.Events.TryRead(out var e)) tail.Add(e);
            Check(tail.LastOrDefault() is TuiEvent.Status { Busy: false }, "cancelling the last queued item during worker handoff left FUI busy");
            Check((await session.ExecuteAsync("queue", "", ct)).Notice!.Contains("已取消：cancelled before promotion") &&
                !endpoint.Choices.Any(r => r.Prompt == "cancelled before promotion"), "the cleared follow-up ran or was lost");
            await session.ExecuteAsync("rename", "idle after queue cancellation", ct);
        }
        finally { await host.ReleaseAsync(); }
    }

    internal static async Task Reject<T>(Func<Task> operation) where T : Exception
    {
        try { await operation(); }
        catch (T) { return; }
        throw new InvalidOperationException($"expected {typeof(T).Name}");
    }

    internal static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    internal static Task<List<TuiEvent>> Settled(ITuiSession session, CancellationToken ct) =>
        Until(session, e => e is TuiEvent.Status { Busy: false }, ct);

    internal static async Task<List<TuiEvent>> Until(ITuiSession session, Predicate<TuiEvent> predicate, CancellationToken ct, Loopback? fixture = null)
    {
        List<TuiEvent> events = [];
        while (true)
        {
            var e = await session.Events.ReadAsync(ct);
            events.Add(e);
            if (predicate(e)) return events;
            if (e is TuiEvent.Status { Busy: false })
                throw new InvalidOperationException("turn ended before the requested event: " + string.Join(" | ", events.Select(value => value.ToString())) +
                    "\nHTTP fixture requests: " + string.Join(" | ", fixture?.Requests.ToArray() ?? []));
        }
    }

    private static ICollection<TuiLine> Rows(IEnumerable<TuiEvent> events)
    {
        var rows = new Dictionary<string, TuiLine>();
        foreach (var m in events.OfType<TuiEvent.Message>())
            rows[m.Id] = new(m.Kind, m.Append && rows.TryGetValue(m.Id, out var row) ? row.Text + m.Text : m.Text);
        return rows.Values;
    }

    internal static string Frame(object delta, string? finish = null) => "data: " + JsonSerializer.Serialize(
        new { choices = new[] { new { index = 0, delta, finish_reason = finish } } }) + "\n\n";

    /// <summary>Real HTTP/SSE on an ephemeral loopback port. No paid model, HTTP.sys URL ACL, or external service.</summary>
    internal sealed class Loopback : IAsyncDisposable
    {
        private sealed record Reply(string First, Task Gate, string Last);
        private readonly ConcurrentDictionary<(string Prompt, bool AfterTool), Reply> _replies = new();
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _server;
        public string Url { get; }
        public ConcurrentQueue<string> Requests { get; } = new();
        public ConcurrentQueue<(string Prompt, string Model, string? Effort)> Choices { get; } = new();
        public ConcurrentQueue<JsonElement> MessageArrays { get; } = new();
        public string Models { get; init; } = "{\"data\":[{\"id\":\"fixture-model\"}]}";
        public Func<CancellationToken, Task<bool>>? HoldModelList { get; set; }

        public Loopback()
        {
            _listener.Start();
            Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/v1";
            _server = ServeAsync(_stop.Token);
        }

        public void When(string prompt, bool afterTool, string first, Task? gate = null, string last = "") =>
            _replies[(prompt, afterTool)] = new(first, gate ?? Task.CompletedTask, last);

        public void Tool(string prompt, string path) => When(prompt, false, Frame(new { tool_calls = new[] { new {
            index = 0, id = Guid.NewGuid().ToString("N"), type = "function", function = new {
                name = "write_file", arguments = JsonSerializer.Serialize(new { path, content = "fixture" }) } } } }, "tool_calls"));

        private async Task ServeAsync(CancellationToken ct)
        {
            var connections = new List<Task>();
            try
            {
                while (!ct.IsCancellationRequested)
                    connections.Add(ReplyAsync(await _listener.AcceptTcpClientAsync(ct), ct));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            finally { await Task.WhenAll(connections); }
        }

        private async Task ReplyAsync(TcpClient client, CancellationToken ct)
        {
            using (client)
            try
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.Latin1, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
                var request = await reader.ReadLineAsync(ct);
                if (request is null) return;
                var length = 0;
                var authorized = false;
                while (await reader.ReadLineAsync(ct) is { Length: > 0 } header)
                {
                    if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(header[15..]);
                    if (header.Equals("Authorization: Bearer loopback-fixture", StringComparison.OrdinalIgnoreCase)) authorized = true;
                }
                var body = new char[length];
                if (length > 0) Check(await reader.ReadBlockAsync(body, ct) == length, "incomplete HTTP request");
                Task Write(string text) => stream.WriteAsync(Encoding.UTF8.GetBytes(text), ct).AsTask();
                if (request.StartsWith("GET /v1/models ", StringComparison.Ordinal))
                {
                    if (HoldModelList is { } hold && !await hold(ct)) return;
                    await Write($"HTTP/1.1 200 OK\r\nConnection: close\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(Models)}\r\n\r\n" + Models);
                    return;
                }
                Check(request.StartsWith("POST /v1/chat/completions ", StringComparison.Ordinal), "unexpected fixture route");
                Check(authorized, "Core credential resolver was not used");
                using var packet = JsonDocument.Parse(Encoding.Latin1.GetBytes(body));
                var array = packet.RootElement.GetProperty("messages");
                MessageArrays.Enqueue(array.Clone());
                var messages = array.EnumerateArray().ToArray();
                var latest = messages.LastOrDefault(m => m.GetProperty("role").GetString() == "user");
                var content = latest.GetProperty("content");
                var prompt = content.ValueKind == JsonValueKind.Array
                    ? string.Concat(content.EnumerateArray().Where(b => b.GetProperty("type").GetString() == "text").Select(b => b.GetProperty("text").GetString()))
                    : content.GetString()!;
                var afterTool = messages[^1].GetProperty("role").GetString() == "tool";
                Choices.Enqueue((prompt, packet.RootElement.GetProperty("model").GetString()!,
                    packet.RootElement.TryGetProperty("reasoning_effort", out var effort) ? effort.GetString() : null));
                Requests.Enqueue($"{prompt[..Math.Min(40, prompt.Length)]}/{messages[^1].GetProperty("role").GetString()}");
                if (!_replies.TryGetValue((prompt, afterTool), out var reply))
                {
                    await Write("HTTP/1.1 400 Unexpected fixture request\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                    return;
                }
                var tail = reply.Last + "data: [DONE]\n\n";
                var bytes = Encoding.UTF8.GetByteCount(reply.First) + Encoding.UTF8.GetByteCount(tail);
                await Write($"HTTP/1.1 200 OK\r\nConnection: close\r\nContent-Type: text/event-stream\r\nContent-Length: {bytes}\r\n\r\n" + reply.First);
                await reply.Gate.WaitAsync(ct);
                await Write(tail);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            try { await _server; }
            finally { _listener.Stop(); _stop.Dispose(); }
        }
    }
}
