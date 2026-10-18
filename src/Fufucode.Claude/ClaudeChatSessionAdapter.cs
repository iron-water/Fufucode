using System.Diagnostics;
using System.Text.Json;
using Fufucode.App.Abstractions;

namespace Fufucode.Claude;

/// <summary>
/// 把官方 Claude Code CLI 的 stream-json 会话映射到 Fufucode 冻结的 UI 无依赖聊天契约。
/// 说明：
/// - assistant 文本块映射为打字机文本流（含 --include-partial-messages 的增量）；
/// - assistant tool_use 块映射为统一工具调用，审批通过 control_request/can_use_tool 往返；
/// - init 帧的 session_id 被保存，重开时通过 --resume &lt;session_id&gt; 续接；
/// - hook_* 与未知帧宽容跳过，不进入时间线。
/// </summary>
public sealed class ClaudeChatSessionAdapter : IChatSession, IBackendChatSession, IAsyncDisposable
{
    private sealed record ToolLink(string MessageId, int Index, ToolCallBlock Block);

    private readonly ClaudeStreamClient _client;
    private readonly Task _pump;
    private readonly object _gate = new();
    private readonly Dictionary<string, ToolLink> _tools = [];
    private readonly Dictionary<string, ClaudePermissionRequest> _pendingPermissions = [];
    private readonly TaskCompletionSource<bool> _init = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _handshake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private SessionConnection _connection = SessionConnection.Starting;
    private SessionActivity _activity = SessionActivity.Idle;
    private SessionOutcome? _outcome;
    private string? _lastError;
    private string? _activeMessageId;
    private string? _currentModel;
    private string? _sessionId;
    private int _blockCount;
    private long _nextMessageId;
    private bool _disposed;

    private ClaudeChatSessionAdapter(ClaudeStreamClient client)
    {
        _client = client;
        _pump = PumpAsync();
    }

    /// <summary>
    /// 启动子进程并等待 initialize 控制请求被 CLI 应答。
    /// 实测（claude 2.1.296）：init 帧在首条用户消息时才发出，因此握手以控制应答为准，
    /// session_id 在 init 到达后通过 <see cref="SessionId"/> 暴露。
    /// </summary>
    public static async Task<ClaudeChatSessionAdapter> ConnectAsync(ClaudeStreamOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var client = ClaudeStreamClient.Start(options);
        var adapter = new ClaudeChatSessionAdapter(client);
        try
        {
            await client.SendInitializeAsync(cancellationToken);
            var timeout = options.StartupTimeout ?? TimeSpan.FromSeconds(30);
            await adapter._handshake.Task.WaitAsync(timeout, cancellationToken);
            return adapter;
        }
        catch
        {
            await adapter.DisposeAsync();
            throw;
        }
    }

    /// <summary>init 帧返回的会话标识；未握手时为 null。</summary>
    public string? SessionId { get { lock (_gate) return _sessionId; } }

    /// <summary>等待 init 帧到达（首条用户消息后），超时返回 false 而不伪造会话标识。</summary>
    public async Task<bool> WaitForInitAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        try { return await _init.Task.WaitAsync(timeout, cancellationToken); }
        catch (TimeoutException) { return false; }
    }

    public string BackendId => "claude";

    public string BackendName => "Claude Code";

    public ClaudeStreamClient Client => _client;

    public ClaudeProcessExit? ProcessExit => _client.ProcessExit;

    public SessionConnection Connection { get { lock (_gate) return _connection; } }

    public SessionActivity Activity { get { lock (_gate) return _activity; } }

    public SessionOutcome? LastOutcome { get { lock (_gate) return _outcome; } }

    public string? CurrentModel { get { lock (_gate) return _currentModel; } }

    /// <summary>最近一次会话级错误或权限拒绝的消息；正常时为 null。拒绝路径不静默吞掉。</summary>
    public string? LastError { get { lock (_gate) return _lastError; } }

    public bool CanSend
    {
        get
        {
            lock (_gate) return _connection == SessionConnection.Connected && _activity != SessionActivity.Cancelling;
        }
    }

    public bool CanStop
    {
        get
        {
            lock (_gate) return _connection == SessionConnection.Connected && _activity != SessionActivity.Idle;
        }
    }

    public int QueuedCount => 0;

    /// <summary>等待用户审批的权限请求快照（用于 UI 展示与测试断言）。</summary>
    public IReadOnlyList<ClaudePermissionRequest> PendingPermissionRequests
    {
        get { lock (_gate) return _pendingPermissions.Values.ToArray(); }
    }

    public event EventHandler<SessionEvent>? Event;

    /// <summary>审批询问到达；订阅者必须调用 <see cref="RespondToPermissionAsync"/> 应答。</summary>
    public event Action<ClaudePermissionRequest>? PermissionRequested;

    public async Task SendAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        lock (_gate)
        {
            if (_connection != SessionConnection.Connected)
                throw new InvalidOperationException("Claude 会话尚未连接。");
            if (_activity != SessionActivity.Idle)
                throw new InvalidOperationException("当前回合仍在进行，Claude stream-json 不接受并发回合。");
        }
        await _client.SendUserMessageAsync(text, cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_connection != SessionConnection.Connected) return;
            if (_activity == SessionActivity.Idle) return;
            _activity = SessionActivity.Cancelling;
        }
        Emit(new SessionEvent.ActivityChanged(SessionActivity.Cancelling));
        await _client.SendInterruptAsync(cancellationToken);
    }

    /// <summary>
    /// 应答一条审批请求。允许时原样回填 CLI 给出的 input；拒绝时把理由作为 message 回灌，
    /// 拒绝路径随后的 tool_result 会以 is_error 呈现，不静默吞掉。
    /// </summary>
    public async Task RespondToPermissionAsync(ClaudePermissionRequest request, ClaudePermissionDecision decision,
        string? message = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            if (!_pendingPermissions.TryGetValue(request.RequestId, out var pending) ||
                !ReferenceEquals(pending, request))
                throw new InvalidOperationException("该审批请求已应答或已过期。");
            _pendingPermissions.Remove(request.RequestId);
        }
        await _client.SendPermissionResponseAsync(request, decision, message, cancellationToken);
    }

    private async Task PumpAsync()
    {
        try
        {
            await foreach (var ev in _client.Events.ReadAllAsync())
            {
                switch (ev)
                {
                    case ClaudeInitEvent init: OnInit(init.Init); break;
                    case ClaudeControlResponseEvent response: OnControlResponse(response); break;
                    case ClaudeAssistantEvent assistant: OnAssistant(assistant); break;
                    case ClaudeToolResultEvent toolResult: OnToolResult(toolResult); break;
                    case ClaudePermissionRequestEvent permission: OnPermissionRequest(permission.Request); break;
                    case ClaudePermissionDeniedEvent denied: OnPermissionDenied(denied); break;
                    case ClaudeTextDeltaEvent delta: OnTextDelta(delta.Delta); break;
                    case ClaudeResultEvent result: OnResult(result.Result); break;
                }
            }
        }
        catch (Exception error) { Fail(error); }
    }

    private void OnInit(ClaudeInitInfo init)
    {
        lock (_gate)
        {
            _sessionId = init.SessionId;
            _currentModel ??= init.Model;
            if (_connection == SessionConnection.Starting) _connection = SessionConnection.Connected;
        }
        Emit(new SessionEvent.ConnectionChanged(SessionConnection.Connected));
        _init.TrySetResult(true);
    }

    private void OnControlResponse(ClaudeControlResponseEvent response)
    {
        if (!response.Success)
        {
            Fail(new ClaudeStreamException($"Claude Code 拒绝了控制请求 {response.RequestId ?? "(unknown)"}。"));
            return;
        }
        lock (_gate)
        {
            if (_connection == SessionConnection.Starting) _connection = SessionConnection.Connected;
        }
        _handshake.TrySetResult(true);
        Emit(new SessionEvent.ConnectionChanged(SessionConnection.Connected));
    }

    private void OnAssistant(ClaudeAssistantEvent message)
    {
        var frame = message.Frame;
        string? messageId;
        string? model;
        int blockCount;
        bool startedActivity;
        bool beginMessage;
        lock (_gate)
        {
            beginMessage = _activeMessageId is null;
            if (beginMessage)
            {
                _activeMessageId = $"{_client.Generation:N}-{Interlocked.Increment(ref _nextMessageId)}";
                _blockCount = CountSemanticBlocks(frame);
                _currentModel ??= frame.Model;
            }
            messageId = _activeMessageId;
            blockCount = _blockCount;
            model = _currentModel;
            startedActivity = _activity == SessionActivity.Idle;
            if (startedActivity) _activity = SessionActivity.Running;
        }
        if (beginMessage) Emit(new SessionEvent.MessageStarted(messageId!, MessageRole.Assistant, model, blockCount));
        if (startedActivity) Emit(new SessionEvent.ActivityChanged(SessionActivity.Running));
        var index = -1;
        foreach (var block in frame.Blocks)
        {
            index++;
            switch (block)
            {
                case ClaudeTextBlock text when text.Text.Length > 0:
                    Emit(new SessionEvent.BlockFinished(messageId!, index, new TextBlock(text.Text)));
                    break;
                case ClaudeThinkingBlock thinking when thinking.Text.Length > 0:
                    Emit(new SessionEvent.BlockFinished(messageId!, index, new ThinkingBlock(thinking.Text)));
                    break;
                case ClaudeToolUseBlock tool:
                    var toolBlock = new ToolCallBlock(tool.Id, tool.Name, tool.InputJson,
                        ToolCallStatus.Running, null, null, false);
                    lock (_gate) _tools[tool.Id] = new ToolLink(messageId!, index, toolBlock);
                    Emit(new SessionEvent.BlockFinished(messageId!, index, toolBlock));
                    break;
            }
        }
        FinishMessage(messageId!, frame);
    }

    private void FinishMessage(string messageId, ClaudeAssistantFrame frame)
    {
        var blocks = new List<MessageBlock>();
        foreach (var block in frame.Blocks)
        {
            switch (block)
            {
                case ClaudeTextBlock text when text.Text.Length > 0: blocks.Add(new TextBlock(text.Text)); break;
                case ClaudeThinkingBlock thinking when thinking.Text.Length > 0: blocks.Add(new ThinkingBlock(thinking.Text)); break;
                case ClaudeToolUseBlock tool:
                    ToolCallBlock current;
                    lock (_gate)
                    {
                        if (_tools.TryGetValue(tool.Id, out var link)) current = link.Block;
                        else current = new ToolCallBlock(tool.Id, tool.Name, tool.InputJson, ToolCallStatus.Running, null, null, false);
                    }
                    blocks.Add(current);
                    break;
            }
        }
        lock (_gate) _activeMessageId = null;
        Emit(new SessionEvent.MessageFinished(messageId,
            new ChatMessage(messageId, MessageRole.Assistant, frame.Model, blocks, null, true), null));
    }

    private static int CountSemanticBlocks(ClaudeAssistantFrame frame) =>
        frame.Blocks.Count(block => block is ClaudeTextBlock { Text.Length: > 0 } or
            ClaudeThinkingBlock { Text.Length: > 0 } or ClaudeToolUseBlock);

    private void OnToolResult(ClaudeToolResultEvent result)
    {
        foreach (var toolResult in result.Results)
        {
            ToolLink? link;
            lock (_gate) _tools.TryGetValue(toolResult.ToolUseId, out link);
            if (link is null) continue;
            var status = toolResult.IsError ? ToolCallStatus.Failed : ToolCallStatus.Completed;
            var updated = link.Block with
            {
                Status = status,
                Output = toolResult.Text,
                IsErrorOutput = toolResult.IsError,
                StatusText = DescribePermission(toolResult),
            };
            lock (_gate) _tools[toolResult.ToolUseId] = link with { Block = updated };
            Emit(new SessionEvent.BlockFinished(link.MessageId, link.Index, updated));
            Emit(new SessionEvent.ToolCallStatusChanged(link.MessageId, link.Index, toolResult.ToolUseId, status, updated.StatusText));
        }
    }

    private static string? DescribePermission(ClaudeToolResultBlock result) =>
        result.PermissionDecision is { Length: > 0 } decision ? $"{decision} ({result.PermissionSource ?? "unknown"})" : null;

    private void OnPermissionRequest(ClaudePermissionRequest request)
    {
        lock (_gate) _pendingPermissions[request.RequestId] = request;
        var observers = PermissionRequested;
        if (observers is not null)
            foreach (Action<ClaudePermissionRequest> observer in observers.GetInvocationList())
                try { observer(request); } catch (Exception error) { Debug.WriteLine(error); }
    }

    private void OnPermissionDenied(ClaudePermissionDeniedEvent denied)
    {
        var message = ClaudeProtocol.OptionalString(denied.Raw, "message") ?? "Claude Code 拒绝了工具权限。";
        lock (_gate) _lastError = message;
        Emit(new SessionEvent.SessionError(SessionErrorKind.CommandRejected, message, null));
    }

    private void OnTextDelta(ClaudeTextDelta delta)
    {
        string messageId;
        string? model;
        bool beginMessage;
        lock (_gate)
        {
            beginMessage = _activeMessageId is null;
            if (beginMessage)
            {
                _activeMessageId = $"{_client.Generation:N}-{Interlocked.Increment(ref _nextMessageId)}";
            }
            messageId = _activeMessageId!;
            model = _currentModel;
        }
        if (beginMessage) Emit(new SessionEvent.MessageStarted(messageId, MessageRole.Assistant, model, 0));
        if (delta.Kind == "text") Emit(new SessionEvent.TextAppended(messageId, delta.Index, delta.Delta));
        else Emit(new SessionEvent.ThinkingAppended(messageId, delta.Index, delta.Delta));
    }

    private void OnResult(ClaudeResultInfo result)
    {
        lock (_gate)
        {
            _activity = SessionActivity.Idle;
            _outcome = result.IsError ? SessionOutcome.Failed : SessionOutcome.Completed;
            if (result.IsError && result.ResultText is { Length: > 0 })
                _lastError = result.ResultText;
        }
        Emit(new SessionEvent.ActivityChanged(SessionActivity.Idle));
        Emit(new SessionEvent.TurnSettled(result.IsError ? SessionOutcome.Failed : SessionOutcome.Completed,
            result.ResultText ?? result.Subtype));
    }

    private void Fail(Exception error)
    {
        if (_disposed) return;
        lock (_gate)
        {
            _connection = SessionConnection.Faulted;
            _activity = SessionActivity.Idle;
            _lastError = error.Message;
        }
        _init.TrySetException(error);
        _handshake.TrySetException(error);
        Emit(new SessionEvent.ConnectionChanged(SessionConnection.Faulted));
        Emit(new SessionEvent.SessionError(SessionErrorKind.ProcessExited, error.Message, null));
    }

    private void Emit(SessionEvent ev)
    {
        var handlers = Event;
        if (handlers is null) return;
        foreach (EventHandler<SessionEvent> handler in handlers.GetInvocationList())
            try { handler(this, ev); } catch (Exception error) { Debug.WriteLine($"Claude session observer failed: {error}"); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try { await _client.StopAsync(); } catch (Exception) { /* best effort */ }
        try { await _pump; } catch (Exception) { /* observed via events */ }
        await _client.DisposeAsync();
    }
}
