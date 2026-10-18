using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Fufucode.App.Abstractions;
using Fufucode.Core.RemoteControl;

namespace Fufucode.App.RemoteControl.Tests;

/// <summary>断言失败时抛出，供独立测试入口统一统计通过/失败。</summary>
internal sealed class CheckFailed(string name) : Exception("FAIL " + name);

/// <summary>最小的测试驱动会话：脚本化事件流，不访问任何真实进程。</summary>
internal class FakeChatSession : IChatSession
{
    private readonly object _gate = new();
    private readonly List<string> _queue = new();
    private SessionConnection _connection = SessionConnection.Connected;
    private SessionActivity _activity = SessionActivity.Idle;

    public SessionConnection Connection => _connection;
    public SessionActivity Activity => _activity;
    public SessionOutcome? LastOutcome { get; private set; }
    public string? CurrentModel { get; set; } = "测试模型";
    public bool CanSend => _connection == SessionConnection.Connected && _activity != SessionActivity.Cancelling;
    public bool CanStop => _activity == SessionActivity.Running || _queue.Count > 0;
    public int QueuedCount => _queue.Count;
    public List<string> Sent { get; } = new();
    public int StopCount { get; private set; }
    public TaskCompletionSource<string> NextSend { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public event EventHandler<SessionEvent>? Event;

    public Task SendAsync(string text, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!CanSend) throw new InvalidOperationException("会话未连接，无法提交（测试夹具）");
            Sent.Add(text);
            NextSend.TrySetResult(text);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            StopCount++;
            var cleared = _queue.ToArray();
            _queue.Clear();
            if (cleared.Length > 0) RaiseLocked(new SessionEvent.QueueCleared(cleared));
            RaiseLocked(new SessionEvent.QueueChanged(0, Array.Empty<string>()));
            _activity = SessionActivity.Idle;
            RaiseLocked(new SessionEvent.ActivityChanged(_activity));
        }

        return Task.CompletedTask;
    }

    public void SimulateQueue(int count)
    {
        lock (_gate)
        {
            _queue.Clear();
            for (var i = 0; i < count; i++) _queue.Add("queued-" + i);
            RaiseLocked(new SessionEvent.QueueChanged(_queue.Count, _queue.ToArray()));
        }
    }

    public void SimulateActivity(SessionActivity activity)
    {
        lock (_gate)
        {
            _activity = activity;
            RaiseLocked(new SessionEvent.ActivityChanged(activity));
        }
    }

    public void SimulateConnection(SessionConnection connection)
    {
        lock (_gate)
        {
            _connection = connection;
            RaiseLocked(new SessionEvent.ConnectionChanged(connection));
        }
    }

    public void SimulateUserMessage(string id, string text)
    {
        lock (_gate)
        {
            RaiseLocked(new SessionEvent.MessageStarted(id, MessageRole.User, null, 1));
            RaiseLocked(new SessionEvent.BlockStarted(id, 0, MessageBlockKind.Text, null, null));
            RaiseLocked(new SessionEvent.TextAppended(id, 0, text));
            RaiseLocked(new SessionEvent.BlockFinished(id, 0, new TextBlock(text)));
            RaiseLocked(new SessionEvent.MessageFinished(id,
                new ChatMessage(id, MessageRole.User, null, new MessageBlock[] { new TextBlock(text) }, null, true),
                null));
            LastOutcome = SessionOutcome.Completed;
        }
    }

    /// <summary>流式助手消息：思考 → 文本增量 → 工具调用（参数分片）→ 工具执行完成。</summary>
    public void SimulateAssistantStream(string id, string toolCallId, string toolName)
    {
        lock (_gate)
        {
            RaiseLocked(new SessionEvent.MessageStarted(id, MessageRole.Assistant, CurrentModel, 3));
            RaiseLocked(new SessionEvent.BlockStarted(id, 0, MessageBlockKind.Thinking, null, null));
            RaiseLocked(new SessionEvent.ThinkingAppended(id, 0, "先确认目录结构。"));
            RaiseLocked(new SessionEvent.BlockFinished(id, 0, new ThinkingBlock("先确认目录结构。")));
            RaiseLocked(new SessionEvent.BlockStarted(id, 1, MessageBlockKind.Text, null, null));
            RaiseLocked(new SessionEvent.TextAppended(id, 1, "正在读取 README"));
            RaiseLocked(new SessionEvent.TextAppended(id, 1, "的说明。"));
            RaiseLocked(new SessionEvent.BlockFinished(id, 1, new TextBlock("正在读取 README的说明。")));
            RaiseLocked(new SessionEvent.BlockStarted(id, 2, MessageBlockKind.ToolCall, toolCallId, toolName));
            RaiseLocked(new SessionEvent.ToolCallAppended(id, 2, toolCallId, "{\"path\":\"READM"));
            RaiseLocked(new SessionEvent.ToolCallAppended(id, 2, toolCallId, "E.md\"}"));
            RaiseLocked(new SessionEvent.ToolCallStatusChanged(id, 2, toolCallId, ToolCallStatus.Running, "读取 README.md"));
            RaiseLocked(new SessionEvent.ToolCallStatusChanged(id, 2, toolCallId, ToolCallStatus.Completed, "已读取 7 行"));
            var tool = new ToolCallBlock(toolCallId, toolName, "{\"path\":\"README.md\"}",
                ToolCallStatus.Completed, "已读取 7 行", "内容", false);
            RaiseLocked(new SessionEvent.BlockFinished(id, 2, tool));
            RaiseLocked(new SessionEvent.MessageFinished(id,
                new ChatMessage(id, MessageRole.Assistant, CurrentModel,
                    new MessageBlock[]
                    {
                        new ThinkingBlock("先确认目录结构。"),
                        new TextBlock("正在读取 README的说明。"),
                        tool,
                    },
                    new UsageInfo { InputTokens = 128, OutputTokens = 256 },
                    true),
                new UsageInfo { InputTokens = 128, OutputTokens = 256 }));
            LastOutcome = SessionOutcome.Completed;
            RaiseLocked(new SessionEvent.TurnSettled(SessionOutcome.Completed, null));
        }
    }

    public void SimulateError(SessionErrorKind kind, string message)
    {
        lock (_gate) RaiseLocked(new SessionEvent.SessionError(kind, message, null));
    }

    private void RaiseLocked(SessionEvent e) => Event?.Invoke(this, e);
}

/// <summary>HTTP 客户端封装：自带令牌头，便于断言未授权响应。</summary>
internal sealed class RemoteHttpClient : IDisposable
{
    private readonly string _baseUrl;

    public RemoteHttpClient(HttpClient client, string baseUrl)
    {
        Raw = client;
        _baseUrl = baseUrl;
        BaseUrl = baseUrl;
    }

    public HttpClient Raw { get; }

    /// <summary>本客户端指向的服务基地址（未设置 BaseAddress 时为 null，故单独暴露）。</summary>
    public string BaseUrl { get; }

    public string? Token { get; set; }

    public static RemoteHttpClient Create(int port, string? host = null)
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var address = host ?? "127.0.0.1";
        return new RemoteHttpClient(client, $"http://{address}:{port}");
    }

    public async Task<(HttpStatusCode Status, string Body)> GetAsync(string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _baseUrl + path);
        Authorize(request);
        using var response = await Raw.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    public async Task<(HttpStatusCode Status, string Body)> PostAsync(string path, object payload)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _baseUrl + path)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        Authorize(request);
        using var response = await Raw.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    public async Task<RemotePairResult> PairAsync(string pin)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/api/pair")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { pin }), Encoding.UTF8, "application/json"),
        };
        using var response = await Raw.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<RemotePairResult>(body, RemoteWire.Options) ??
               new RemotePairResult(false, null, "空响应", false);
    }

    public async Task<RemoteSessionSnapshot> SnapshotAsync()
    {
        var (status, body) = await GetAsync("/api/snapshot");
        if (status != HttpStatusCode.OK) throw new CheckFailed($"/api/snapshot 期望 200，实际 {(int)status}");
        return JsonSerializer.Deserialize<RemoteSessionSnapshot>(body, RemoteWire.Options)
               ?? throw new CheckFailed("/api/snapshot 反序列化失败");
    }

    public async Task<RemoteCommandResult> SendMessageAsync(string text)
    {
        var (_, body) = await PostAsync("/api/message", new { text });
        return JsonSerializer.Deserialize<RemoteCommandResult>(body, RemoteWire.Options)
               ?? new RemoteCommandResult(false, "空响应");
    }

    public async Task<RemoteCommandResult> StopAsync()
    {
        var (_, body) = await PostAsync("/api/stop", new { });
        return JsonSerializer.Deserialize<RemoteCommandResult>(body, RemoteWire.Options)
               ?? new RemoteCommandResult(false, "空响应");
    }

    public async Task<(HttpStatusCode Status, RemoteApprovalResolution? Resolution)> ResolveApprovalAsync(
        string approvalId, string decision)
    {
        var (status, body) = await PostAsync("/api/approval", new { approvalId, decision });
        return (status, JsonSerializer.Deserialize<RemoteApprovalResolution>(body, RemoteWire.Options));
    }

    /// <summary>连接事件流并读取首帧快照。</summary>
    public async Task<(ClientWebSocket Socket, RemoteUpdate? First)> OpenEventsAsync()
    {
        var socket = new ClientWebSocket();
        var scheme = _baseUrl.Replace("http://", "ws://", StringComparison.Ordinal);
        await socket.ConnectAsync(new Uri($"{scheme}/api/events?token={Uri.EscapeDataString(Token ?? string.Empty)}"),
            CancellationToken.None);
        var first = await ReadUpdateAsync(socket, TimeSpan.FromSeconds(10));
        return (socket, first);
    }

    public static async Task<RemoteUpdate?> ReadUpdateAsync(ClientWebSocket socket, TimeSpan timeout)
    {
        var buffer = new byte[64 * 1024];
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            var result = await socket.ReceiveAsync(buffer, cts.Token);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
            return JsonSerializer.Deserialize<RemoteUpdate>(text, RemoteWire.Options);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>在给定时限内等待满足条件的更新（不满足时返回 null，不伪造成功）。</summary>
    public static async Task<TUpdate?> WaitForAsync<TUpdate>(ClientWebSocket socket, Func<TUpdate, bool> predicate,
        TimeSpan timeout) where TUpdate : RemoteUpdate
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var remaining = deadline - DateTime.UtcNow;
            var update = await ReadUpdateAsync(socket, remaining);
            if (update is null) return null;
            if (update is TUpdate typed && predicate(typed)) return typed;
        }

        return null;
    }

    private void Authorize(HttpRequestMessage request)
    {
        if (Token is not null) request.Headers.Authorization = new("Bearer", Token);
    }

    public void Dispose() => Raw.Dispose();
}
