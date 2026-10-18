using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Fufucode.App.Abstractions;
using Fufucode.Claude;
using Fufucode.Core;
using Fufucode.Core.Backend;

namespace Fufucode.Plugins.Claude;

/// <summary>
/// Claude 后端会话协调器：把 ClaudeChatSession 的事件映射为与 Pi / FUMO 同形的 SessionSnapshot，
/// 实现统一的 ISessionCoordinator 契约，使 UI、持久化与自动化层无需感知底层是 Claude Code CLI。
/// </summary>
public sealed class ClaudeSessionCoordinator : ISessionCoordinator, IExtensionUiSession
{
    private readonly ClaudeChatSession _chat;
    private readonly AppSettingsStore? _settings;
    private readonly DiagnosticLog? _diagnosticLog;
    private readonly object _gate = new();
    private readonly List<PiMessage> _messages = [];
    private readonly List<PiToolCard> _tools = [];
    private readonly List<string> _queuedTexts = [];
    private readonly List<string> _recoverableQueue = [];
    private ConnectionState _connection = ConnectionState.Stopped;
    private ActivityState _activity = ActivityState.Idle;
    private TaskResult _result = TaskResult.Unknown;
    private string? _error;
    private bool _idleMaintenance;
    private bool _disposed;

    private long _usageInput;
    private long _usageOutput;
    private long _usageCacheRead;
    private long _usageCacheWrite;
    private int _usageMessages;

    public ClaudeSessionCoordinator(ClaudeChatSession chat, AppSettingsStore? settings = null,
        DiagnosticLog? diagnosticLog = null)
    {
        _chat = chat ?? throw new ArgumentNullException(nameof(chat));
        _settings = settings;
        _diagnosticLog = diagnosticLog;
        _connection = MapConnection(_chat.Connection);
        _activity = MapActivity(_chat.Activity);

        _chat.Event += OnSessionEvent;
    }

    public BackendId Backend => BackendId.Claude;
    public BackendCapabilities Capabilities => BackendCapabilities.For(Backend);
    public SessionSnapshot Snapshot { get { lock (_gate) return MakeSnapshot(); } }
    public event Action<SessionSnapshot>? SnapshotChanged;

    public event Action<ExtensionUiRequest>? ExtensionUiRequestReceived
    {
        add => _chat.ExtensionUiRequestReceived += value;
        remove => _chat.ExtensionUiRequestReceived -= value;
    }
    public event Action<ExtensionUiRequest>? ExtensionUiRequestClosed
    {
        add => _chat.ExtensionUiRequestClosed += value;
        remove => _chat.ExtensionUiRequestClosed -= value;
    }
    public event Action<PiExtensionUiRequestEvent>? ExtensionUiNotification { add { } remove { } }
    public event Action<PiExtensionErrorEvent>? ExtensionError { add { } remove { } }

    public ApprovalAvailability ApprovalAvailability => ApprovalAvailability.Verified;
    public string? ApprovalDiagnostic => "Claude 官方 stdio 权限审批桥已就绪。";

    public bool IsInIdleMaintenance { get { lock (_gate) return _idleMaintenance; } }
    public bool CanSendPrompt { get { lock (_gate) return !_idleMaintenance && _chat.CanSend; } }

    public RpcProcessExit? LastProcessExit
    {
        get
        {
            var exit = _chat.ProcessExit;
            return exit is null ? null : new RpcProcessExit(exit.Generation, exit.ProcessId, exit.ExitCode, exit.ObservedAtUtc);
        }
    }

    public IReadOnlyList<ExtensionUiRequest> PendingExtensionUiRequests => _chat.PendingExtensionUiRequests;

    public SessionUsageReport GetUsage()
    {
        lock (_gate)
        {
            UsageCounter Counter(long total) => new(total, total, _usageMessages, 0);
            var totals = new UsageTotals(_usageMessages, Counter(_usageInput), Counter(_usageOutput),
                Counter(_usageCacheRead), Counter(_usageCacheWrite), Counter(_usageInput + _usageOutput));
            return new SessionUsageReport(totals, [], 0);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate) _connection = MapConnection(_chat.Connection);
        Publish();
        return Task.CompletedTask;
    }

    public async Task SendPromptAsync(string message, PromptDelivery delivery = PromptDelivery.Immediate,
        TimeSpan? acknowledgementTimeout = null, CancellationToken cancellationToken = default,
        IReadOnlyList<PiPromptImage>? images = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_idleMaintenance) throw new InvalidOperationException("会话正在安全维护，未发送新请求。");
            _activity = ActivityState.Running;
            _result = TaskResult.Unknown;
            _error = null;
        }
        Publish();

        await _chat.SendAsync(message, cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_activity != ActivityState.Idle) _activity = ActivityState.Cancelling;
        }
        Publish();
        await _chat.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task CloseAsync() => DisposeAsync().AsTask();

    public Task<RpcResponse> QueryStateAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var data = new
            {
                sessionId = _chat.SessionId ?? "",
                sessionFile = "",
                model = new { provider = "anthropic", id = _chat.CurrentModel ?? "claude" },
                activity = _activity.ToString().ToLowerInvariant(),
            };
            return Task.FromResult(new RpcResponse("claude", "get_state", true,
                JsonSerializer.SerializeToElement(data), null));
        }
    }

    public Task<RpcResponse> QueryAvailableModelsAsync(CancellationToken cancellationToken = default)
    {
        var models = new[]
        {
            new { id = "claude-sonnet-5-5", name = "Claude Sonnet 5.5" },
            new { id = "claude-opus-5-5", name = "Claude Opus 5.5" },
            new { id = "claude-haiku-5-5", name = "Claude Haiku 5.5" },
        };
        return Task.FromResult(new RpcResponse("claude", "get_available_models", true,
            JsonSerializer.SerializeToElement(new { models }), null));
    }

    public Task<RpcResponse> SetModelAsync(string provider, string modelId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new RpcResponse("claude", "set_model", true,
            JsonSerializer.SerializeToElement(new { model = new { provider, id = modelId } }), null));
    }

    public Task<RpcResponse> SetThinkingLevelAsync(string level, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new RpcResponse("claude", "set_thinking_level", true,
            JsonSerializer.SerializeToElement(new { thinkingLevel = level }), null));
    }

    public Task<ThinkingLevelAvailability> QueryThinkingLevelsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new ThinkingLevelAvailability(["off", "low", "medium", "high", "max"], "off", _chat.CurrentModel));

    public Task<RpcResponse> QueryCommandsAsync(CancellationToken cancellationToken = default)
    {
        var commands = new[]
        {
            new { name = "Bash", description = "Executes bash command", source = "claude" },
            new { name = "Read", description = "Reads file content", source = "claude" },
            new { name = "Write", description = "Writes file content", source = "claude" },
            new { name = "Edit", description = "Edits file content", source = "claude" },
            new { name = "Glob", description = "Pattern matches file paths", source = "claude" },
            new { name = "Grep", description = "Searches text in files", source = "claude" },
        };
        return Task.FromResult(new RpcResponse("claude", "get_commands", true,
            JsonSerializer.SerializeToElement(new { commands }), null));
    }

    public Task<RpcResponse> QueryEntriesAsync(string? since = null, CancellationToken cancellationToken = default)
    {
        var history = _chat.GetHistoryMessages();
        var entryList = new List<object>();
        string? lastId = null;
        for (var i = 0; i < history.Count; i++)
        {
            var id = $"entry_{i}";
            var message = history[i];
            var role = message.Role switch
            {
                PureAgentRole.User => "user",
                PureAgentRole.Assistant => "assistant",
                PureAgentRole.Tool => "toolResult",
                _ => "system",
            };
            var content = new List<object>();
            if (!string.IsNullOrEmpty(message.Content)) content.Add(new { type = "text", text = message.Content });
            if (message.ToolCalls is { Count: > 0 } calls)
            {
                foreach (var call in calls)
                {
                    JsonElement? arguments = null;
                    try
                    {
                        using var parsed = JsonDocument.Parse(call.ArgumentsJson);
                        arguments = JsonSerializer.SerializeToElement(parsed.RootElement);
                    }
                    catch (JsonException) { }
                    content.Add(new { type = "toolCall", id = call.Id, name = call.Name, arguments });
                }
            }
            entryList.Add(new { id, parentId = lastId, type = "message", message = new { role, content } });
            lastId = id;
        }

        return Task.FromResult(new RpcResponse("claude", "get_entries", true,
            JsonSerializer.SerializeToElement(new { entries = entryList, leafId = lastId }), null));
    }

    public Task<RpcResponse> SetSessionNameAsync(string name, CancellationToken cancellationToken = default) =>
        Task.FromResult(new RpcResponse("claude", "set_session_name", true, null, null));

    public Task<RpcResponse> ForkAsync(string entryId, CancellationToken cancellationToken = default) =>
        Task.FromResult(new RpcResponse("claude", "fork", false, null, "Claude backend does not support fork."));

    public Task SetExecutionModeAsync(string mode, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task RespondToExtensionUiAsync(ExtensionUiRequest request, PiExtensionUiResponse response,
        CancellationToken cancellationToken = default) =>
        _chat.RespondToExtensionUiAsync(request, response, cancellationToken);

    public void RestoreHistory(SessionHistory history, bool previousRunInterrupted)
    {
        lock (_gate)
        {
            _messages.Clear();
            _messages.AddRange(history.CurrentBranchMessages);
            _tools.Clear();
            _usageInput = _usageOutput = _usageCacheRead = _usageCacheWrite = 0;
            _usageMessages = 0;
            foreach (var message in _messages)
            {
                if (message.Usage is not { } usage) continue;
                _usageMessages++;
                _usageInput += usage.Input ?? 0;
                _usageOutput += usage.Output ?? 0;
                _usageCacheRead += usage.CacheRead ?? 0;
                _usageCacheWrite += usage.CacheWrite ?? 0;
            }
            if (previousRunInterrupted) _result = TaskResult.Interrupted;
        }
        Publish();
    }

    public IDisposable BeginIdleMaintenance()
    {
        lock (_gate)
        {
            if (_idleMaintenance) throw new InvalidOperationException("会话已在安全维护。");
            _idleMaintenance = true;
        }
        Publish();
        return new MaintenanceToken(this);
    }

    private sealed class MaintenanceToken(ClaudeSessionCoordinator coordinator) : IDisposable
    {
        private ClaudeSessionCoordinator? _coordinator = coordinator;
        public void Dispose()
        {
            var c = Interlocked.Exchange(ref _coordinator, null);
            if (c is null) return;
            lock (c._gate) c._idleMaintenance = false;
            c.Publish();
        }
    }

    private void OnSessionEvent(object? sender, SessionEvent ev)
    {
        lock (_gate)
        {
            switch (ev)
            {
                case SessionEvent.ConnectionChanged conn:
                    _connection = MapConnection(conn.Connection);
                    break;
                case SessionEvent.ActivityChanged act:
                    _activity = MapActivity(act.Activity);
                    break;
                case SessionEvent.TurnSettled settled:
                    _activity = ActivityState.Idle;
                    _result = settled.Outcome == SessionOutcome.Completed ? TaskResult.Completed : TaskResult.Failed;
                    break;
                case SessionEvent.SessionError err:
                    _error = err.Message;
                    break;
                case SessionEvent.MessageFinished finished:
                    _messages.Add(ToWire(finished.FinalMessage));
                    foreach (var block in finished.FinalMessage.Blocks.OfType<ToolCallBlock>())
                    {
                        var index = _tools.FindIndex(t => t.ToolCallId == block.ToolCallId);
                        var card = new PiToolCard(block.ToolCallId, block.ToolName, null,
                            ToJson(block.Output), block.Status is ToolCallStatus.Completed or ToolCallStatus.Failed,
                            block.IsErrorOutput);
                        if (index >= 0) _tools[index] = card;
                        else _tools.Add(card);
                    }
                    break;
            }
        }
        Publish();
    }

    private static PiMessage ToWire(ChatMessage message) => new(
        message.Role == MessageRole.User ? "user" : "assistant",
        message.Blocks.Select(block => block switch
        {
            TextBlock text => (PiContentBlock)new PiTextBlock(text.Text, default),
            ThinkingBlock thinking => new PiThinkingBlock(thinking.Text, default),
            ToolCallBlock tool => new PiToolCallBlock(tool.ToolCallId, tool.ToolName, null, default),
            _ => new PiOtherBlock("unknown", default),
        }).ToArray(),
        null, null, null, default);

    private static JsonElement? ToJson(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        try
        {
            using var doc = JsonDocument.Parse(value);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return JsonSerializer.SerializeToElement(value);
        }
    }

    private void Publish() => SnapshotChanged?.Invoke(Snapshot);

    private SessionSnapshot MakeSnapshot() => new(
        _chat.InnerAdapter.Client.Generation,
        _connection,
        _activity,
        _result,
        _messages.ToArray(),
        _tools.ToArray(),
        null,
        _queuedTexts.ToArray(),
        _recoverableQueue.ToArray(),
        _chat.PendingExtensionUiRequests,
        _error,
        SessionFaultKind.None,
        ApprovalAvailability.Verified,
        "Claude 官方 stdio 权限审批桥已就绪。");

    private static ConnectionState MapConnection(SessionConnection connection) => connection switch
    {
        SessionConnection.Starting => ConnectionState.Starting,
        SessionConnection.Connected => ConnectionState.Connected,
        SessionConnection.Faulted => ConnectionState.Faulted,
        _ => ConnectionState.Stopped,
    };

    private static ActivityState MapActivity(SessionActivity activity) => activity switch
    {
        SessionActivity.Running => ActivityState.Running,
        SessionActivity.Cancelling => ActivityState.Cancelling,
        _ => ActivityState.Idle,
    };

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _chat.Event -= OnSessionEvent;
        await _chat.DisposeAsync().ConfigureAwait(false);
    }
}
