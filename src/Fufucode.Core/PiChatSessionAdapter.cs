using System.Diagnostics;
using System.Text.Json;
using Fufucode.App.Abstractions;
using Fufucode.Pi;

namespace Fufucode.Core;

/// <summary>Maps the real Pi coordinator onto the UI's frozen, UI-free chat contract.</summary>
public sealed class PiChatSessionAdapter : IChatSession, IBackendChatSession, IExtensionUiSession, IPromptHandledSession, IChatAttachments, IExecutionModeSession, IAsyncDisposable
{
    private sealed record ToolLink(string MessageId, int Index, ToolCallBlock Block);

    private readonly ChatSessionCoordinator _session;
    private readonly Dictionary<string, ToolLink> _tools = [];
    private readonly object _gate = new();
    private string? _activeMessageId;
    private string? _currentModel;
    private bool _supportsImages;
    private long _nextMessageId;
    private SessionSnapshot _snapshot;
    private bool _hasOutcome;
    private bool _terminalPublished;
    private string? _lastError;
    private SessionErrorKind? _lastErrorKind;
    private int _recoveredCount;

    private PiChatSessionAdapter(ChatSessionCoordinator session)
    {
        _session = session;
        _snapshot = session.Snapshot;
        _hasOutcome = _snapshot.LastResult != TaskResult.Unknown;
        session.SnapshotChanged += OnSnapshot;
        session.ProtocolEvent += OnProtocol;
        session.ExtensionUiRequestReceived += OnExtensionUiRequest;
        session.ExtensionUiRequestClosed += OnExtensionUiClosed;
        session.PromptHandled += OnPromptHandled;
        session.ExtensionUiNotification += OnExtensionUiNotification;
        session.ExtensionError += OnExtensionError;
    }

    internal static PiChatSessionAdapter AttachConnected(ChatSessionCoordinator session, JsonElement state)
    {
        var adapter = new PiChatSessionAdapter(session);
        adapter.ReadModel(state);
        return adapter;
    }

    public static async Task<PiChatSessionAdapter> ConnectAsync(PiRpcOptions options,
        CancellationToken cancellationToken = default)
    {
        var adapter = new PiChatSessionAdapter(new ChatSessionCoordinator(options));
        try
        {
            await adapter._session.StartAsync(cancellationToken);
            var state = await adapter._session.QueryStateAsync(cancellationToken);
            if (state.Data is { } data) adapter.ReadModel(data);
            return adapter;
        }
        catch
        {
            await adapter.DisposeAsync();
            throw;
        }
    }

    internal void ReadModel(JsonElement data)
    {
        if (!data.TryGetProperty("model", out var model) || model.ValueKind != JsonValueKind.Object) return;
        var provider = String(model, "provider");
        var id = String(model, "id");
        _currentModel = provider is null ? id : $"{provider}/{id}";
        _supportsImages = model.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Array &&
            input.EnumerateArray().Any(value => value.ValueKind == JsonValueKind.String && value.GetString() == "image");
    }

    public SessionConnection Connection => Map(_session.Snapshot.Connection);
    public string BackendId => BackendCapabilities.StorageId(_session.Backend);
    public string BackendName => _session.Capabilities.Name;
    public SessionActivity Activity => Map(_session.Snapshot.Activity);
    public SessionOutcome? LastOutcome => _hasOutcome ? Map(_session.Snapshot.LastResult) : null;
    public string? CurrentModel => _currentModel;
    public bool CanSend => Connection == SessionConnection.Connected && Activity != SessionActivity.Cancelling &&
        _session.CanSendPrompt;
    public bool CanStop => Connection == SessionConnection.Connected &&
        (Activity != SessionActivity.Idle || QueuedCount > 0);
    public int QueuedCount => _session.Snapshot.QueuedTexts.Count;
    public event EventHandler<SessionEvent>? Event;
    public event Action? PromptHandled;
    private void OnPromptHandled()
    {
        if (PromptHandled is not { } observers) return;
        foreach (Action observer in observers.GetInvocationList())
            try { observer(); } catch (Exception error) { Debug.WriteLine(error); }
    }
    public IReadOnlyList<ExtensionUiRequest> PendingExtensionUiRequests => _session.PendingExtensionUiRequests;
    public ApprovalAvailability ApprovalAvailability => _session.ApprovalAvailability;
    public string? ApprovalDiagnostic => _session.ApprovalDiagnostic;
    public event Action<ExtensionUiRequest>? ExtensionUiRequestReceived;
    public event Action<ExtensionUiRequest>? ExtensionUiRequestClosed;
    public event Action<PiExtensionUiRequestEvent>? ExtensionUiNotification;
    public event Action<PiExtensionErrorEvent>? ExtensionError;
    public Task RespondToExtensionUiAsync(ExtensionUiRequest request, PiExtensionUiResponse response,
        CancellationToken cancellationToken = default) => _session.RespondToExtensionUiAsync(request, response, cancellationToken);

    private void OnExtensionUiRequest(ExtensionUiRequest request) => InvokeExtensionObservers(ExtensionUiRequestReceived, request);
    private void OnExtensionUiClosed(ExtensionUiRequest request) => InvokeExtensionObservers(ExtensionUiRequestClosed, request);
    private void OnExtensionUiNotification(PiExtensionUiRequestEvent request)
    {
        if (request.Method == "notify" && request.Raw.TryGetProperty("message", out var text) && text.ValueKind == JsonValueKind.String &&
            text.GetString() == "fufucode-bridge: executionMode=" + ExecutionMode) InvokeExtensionObservers(ExecutionModeChanged, ExecutionMode);
        InvokeExtensionObservers(ExtensionUiNotification, request);
    }
    private void OnExtensionError(PiExtensionErrorEvent error) => InvokeExtensionObservers(ExtensionError, error);

    private static void InvokeExtensionObservers<T>(Action<T>? observers, T value)
    {
        if (observers is null) return;
        foreach (Action<T> observer in observers.GetInvocationList())
            try { observer(value); }
            catch (Exception error) { Debug.WriteLine($"Fufucode extension UI observer failed: {error}"); }
    }

    public async Task SendAsync(string text, CancellationToken cancellationToken = default)
    {
        var delivery = _session.Snapshot.Activity == ActivityState.Idle ? PromptDelivery.Immediate : PromptDelivery.FollowUp;
        await _session.SendPromptAsync(text, delivery, cancellationToken: cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken = default) => _session.StopAsync(cancellationToken);

    public bool CanSendImages => _supportsImages;
    public Task SendWithImagesAsync(string text, IReadOnlyList<ChatImage> images, CancellationToken cancellationToken = default)
    {
        if (!CanSendImages) throw new InvalidOperationException("当前模型未声明图片输入能力");
        if (_session.Snapshot.Activity != ActivityState.Idle) throw new InvalidOperationException("图片附件请等待当前回合结束后发送");
        return _session.SendPromptAsync(text, cancellationToken: cancellationToken, images: images.Select(i => new PiPromptImage(i.Data, i.MimeType)).ToArray());
    }

    public Task SetExecutionModeAsync(string mode, CancellationToken cancellationToken = default)
    {
        if (mode is not ("manual" or "auto" or "full" or "plan")) throw new ArgumentException("Unknown execution mode", nameof(mode));
        if (_session.Snapshot.Activity != ActivityState.Idle) throw new InvalidOperationException("Wait for the current turn to settle");
        return _session.SetExecutionModeAsync(mode, cancellationToken);
    }
    public bool SupportsExecutionModes => _session.SupportsExecutionModes;
    public string ExecutionMode => _session.ExecutionMode;
    public event Action<string>? ExecutionModeChanged;

    private void OnSnapshot(SessionSnapshot next)
    {
        SessionSnapshot previous;
        lock (_gate) { previous = _snapshot; _snapshot = next; }
        if (previous.Activity == ActivityState.Idle && next.Activity == ActivityState.Running)
        {
            _terminalPublished = false;
            _hasOutcome = false;
        }
        if (previous.Connection != next.Connection) Emit(new SessionEvent.ConnectionChanged(Map(next.Connection)));
        if (previous.Activity != next.Activity) Emit(new SessionEvent.ActivityChanged(Map(next.Activity)));
        if (!previous.QueuedTexts.SequenceEqual(next.QueuedTexts))
            Emit(new SessionEvent.QueueChanged(next.QueuedTexts.Count, next.QueuedTexts));
        if (next.RecoverableQueue.Count < _recoveredCount) _recoveredCount = 0;
        if (next.RecoverableQueue.Count > _recoveredCount)
        {
            var added = next.RecoverableQueue.Skip(_recoveredCount).ToArray();
            _recoveredCount = next.RecoverableQueue.Count;
            Emit(new SessionEvent.QueueCleared(added));
        }
        if (next.LastError is null) { _lastError = null; _lastErrorKind = null; }
        if (next.LastError is { } error)
        {
            var modelFailed = next.Messages.LastOrDefault(m => m.Role == "assistant")?.StopReason == "error";
            var kind = error.StartsWith("Pi rejected prompt:", StringComparison.Ordinal) ? SessionErrorKind.CommandRejected :
                next.Connection == ConnectionState.Faulted ? next.FaultKind switch
            {
                SessionFaultKind.ProcessExited => SessionErrorKind.ProcessExited,
                SessionFaultKind.ProtocolError => SessionErrorKind.ProtocolError,
                _ => SessionErrorKind.Unknown
            } :
                modelFailed ? SessionErrorKind.ModelFailed : SessionErrorKind.Unknown;
            var alreadyReportedFatal = next.Connection == ConnectionState.Faulted && kind == _lastErrorKind;
            if (!alreadyReportedFatal && (error != _lastError || kind != _lastErrorKind))
            {
                _lastError = error;
                _lastErrorKind = kind;
                Emit(new SessionEvent.SessionError(kind, error, null));
            }
        }
    }

    private void OnProtocol(PiSessionEvent ev)
    {
        switch (ev)
        {
            case PiMessageEvent { Type: "message_start" } start:
                if (start.Message.Role is not ("user" or "assistant")) break;
                _activeMessageId = $"{ev.Generation:N}-{Interlocked.Increment(ref _nextMessageId)}";
                var role = start.Message.Role == "user" ? MessageRole.User : MessageRole.Assistant;
                var model = String(start.Message.Raw, "model") ?? _currentModel;
                Emit(new SessionEvent.MessageStarted(_activeMessageId, role, model, start.Message.Content.Count));
                break;
            case PiMessageUpdateEvent update when _activeMessageId is not null:
                EmitUpdate(_activeMessageId, update.Update);
                break;
            case PiMessageEvent { Type: "message_end" } end:
                if (_activeMessageId is null) break;
                var id = _activeMessageId;
                _activeMessageId = null;
                var messageRole = end.Message.Role == "user" ? MessageRole.User : MessageRole.Assistant;
                var blocks = end.Message.Content.Select(MapBlock).Where(b => b is not null).Cast<MessageBlock>().ToArray();
                var finalMessage = new ChatMessage(id, messageRole,
                    String(end.Message.Raw, "model") ?? _currentModel, blocks, MapUsage(end.Message.Usage), true);
                Emit(new SessionEvent.MessageFinished(id, finalMessage, finalMessage.CumulativeUsage));
                break;
            case PiToolExecutionEvent tool:
                EmitTool(tool);
                break;
            case PiOtherEvent { Type: "agent_settled" }:
            case PiOtherEvent { Type: "session_settled" } when _session.Backend == Fufucode.Pi.BackendId.Omp:
            case PiOtherEvent { Type: "prompt_result" } result when _session.Backend == Fufucode.Pi.BackendId.Omp &&
                result.Raw.TryGetProperty("sessionSettled", out var settled) && settled.ValueKind == JsonValueKind.True:
                if (_session.Backend == Fufucode.Pi.BackendId.Omp && _terminalPublished) break;
                _terminalPublished = true;
                _hasOutcome = true;
                Emit(new SessionEvent.TurnSettled(Map(_session.Snapshot.LastResult), _session.Snapshot.LastError));
                break;
        }
    }

    private void EmitUpdate(string messageId, PiContentUpdate update)
    {
        if (update.ContentIndex is not { } index) return;
        switch (update.Type)
        {
            case "text_start": Emit(new SessionEvent.BlockStarted(messageId, index, MessageBlockKind.Text, null, null)); break;
            case "thinking_start": Emit(new SessionEvent.BlockStarted(messageId, index, MessageBlockKind.Thinking, null, null)); break;
            case "toolcall_start": Emit(new SessionEvent.BlockStarted(messageId, index, MessageBlockKind.ToolCall, update.ToolCallId, update.ToolName)); break;
            case "text_delta": Emit(new SessionEvent.TextAppended(messageId, index, update.Delta ?? "")); break;
            case "thinking_delta": Emit(new SessionEvent.ThinkingAppended(messageId, index, update.Delta ?? "")); break;
            case "toolcall_delta": Emit(new SessionEvent.ToolCallAppended(messageId, index, update.ToolCallId ?? "", update.Delta ?? "")); break;
            case "text_end" or "thinking_end" when update.CompletedBlock is { } block:
                if (MapBlock(block) is { } final) Emit(new SessionEvent.BlockFinished(messageId, index, final));
                break;
            case "toolcall_end" when update.CompletedBlock is PiToolCallBlock tool:
                var toolBlock = new ToolCallBlock(tool.Id, tool.Name, tool.Arguments?.GetRawText() ?? "{}",
                    ToolCallStatus.Pending, null, null, false);
                _tools[tool.Id] = new ToolLink(messageId, index, toolBlock);
                Emit(new SessionEvent.BlockFinished(messageId, index, toolBlock));
                break;
        }
    }

    private void EmitTool(PiToolExecutionEvent ev)
    {
        if (!_tools.TryGetValue(ev.ToolCallId, out var link)) return;
        var status = ev.Type switch
        {
            "tool_execution_start" => ToolCallStatus.Running,
            "tool_execution_end" when ev.IsError == true => ToolCallStatus.Failed,
            "tool_execution_end" => ToolCallStatus.Completed,
            _ => ToolCallStatus.Running
        };
        var output = ExtractToolText(ev.Result) ?? link.Block.Output;
        var updated = link.Block with { Status = status, Output = output, IsErrorOutput = ev.IsError == true };
        _tools[ev.ToolCallId] = link with { Block = updated };
        Emit(new SessionEvent.BlockFinished(link.MessageId, link.Index, updated));
        Emit(new SessionEvent.ToolCallStatusChanged(link.MessageId, link.Index, ev.ToolCallId, status, null));
    }

    private static string? ExtractToolText(JsonElement? result)
    {
        if (result is not { ValueKind: JsonValueKind.Object } value ||
            !value.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return null;
        var text = content.EnumerateArray().Where(block => block.ValueKind == JsonValueKind.Object)
            .Select(block => String(block, "text")).Where(part => part is not null);
        return string.Join("\n", text);
    }

    private static MessageBlock? MapBlock(PiContentBlock block) => block switch
    {
        PiTextBlock text => new TextBlock(text.Text),
        PiThinkingBlock thought => new ThinkingBlock(thought.Thinking),
        PiToolCallBlock tool => new ToolCallBlock(tool.Id, tool.Name, tool.Arguments?.GetRawText() ?? "{}",
            ToolCallStatus.Pending, null, null, false),
        PiToolCallDraftBlock draft => new ToolCallBlock(draft.Id, draft.Name, draft.ArgumentsText,
            ToolCallStatus.Pending, null, null, false),
        _ => null
    };

    private static UsageInfo? MapUsage(PiUsage? usage)
    {
        if (usage is null) return null;
        decimal? totalCost = null;
        if (usage.Raw.TryGetProperty("cost", out var cost) && cost.ValueKind == JsonValueKind.Object &&
            cost.TryGetProperty("total", out var total) && total.ValueKind == JsonValueKind.Number && total.TryGetDecimal(out var amount))
            totalCost = amount;
        return new UsageInfo { InputTokens = CheckedInt(usage.Input), OutputTokens = CheckedInt(usage.Output),
            CacheReadTokens = CheckedInt(usage.CacheRead), CacheWriteTokens = CheckedInt(usage.CacheWrite), TotalCost = totalCost };
    }

    private static int? CheckedInt(long? value) => value is >= int.MinValue and <= int.MaxValue ? (int)value.Value : null;
    private static string? String(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var field) && field.ValueKind == JsonValueKind.String
            ? field.GetString() : null;
    private static SessionConnection Map(ConnectionState state) => (SessionConnection)(int)state;
    private static SessionActivity Map(ActivityState state) => (SessionActivity)(int)state;
    private static SessionOutcome Map(TaskResult result) => result switch
    {
        TaskResult.Completed => SessionOutcome.Completed,
        TaskResult.Failed => SessionOutcome.Failed,
        TaskResult.Interrupted => SessionOutcome.Interrupted,
        _ => SessionOutcome.Unknown
    };

    private void Emit(SessionEvent ev)
    {
        var handlers = Event;
        if (handlers is null) return;
        foreach (EventHandler<SessionEvent> handler in handlers.GetInvocationList())
        {
            try { handler(this, ev); }
            catch (Exception error) { Debug.WriteLine($"Fufucode UI event observer failed: {error}"); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _session.SnapshotChanged -= OnSnapshot;
        _session.ProtocolEvent -= OnProtocol;
        _session.ExtensionUiRequestReceived -= OnExtensionUiRequest;
        _session.ExtensionUiRequestClosed -= OnExtensionUiClosed;
        _session.PromptHandled -= OnPromptHandled;
        _session.ExtensionUiNotification -= OnExtensionUiNotification;
        _session.ExtensionError -= OnExtensionError;
        await _session.DisposeAsync();
    }
}
