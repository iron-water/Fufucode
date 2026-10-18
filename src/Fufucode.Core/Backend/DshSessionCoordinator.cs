using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Fufucode.App.Abstractions;

namespace Fufucode.Core.Backend;

/// <summary>
/// DeepSeek Harness (DSH) 后端会话协调器：把 DshChatSession 的事件映射为与 Pi / FUMO 同形的 SessionSnapshot，
/// 实现统一的 ISessionCoordinator 契约，使 UI、持久化与自动化层无需感知底层是 DSH CLI。
/// </summary>
public sealed class DshSessionCoordinator : ISessionCoordinator, IExtensionUiSession
{
    private readonly DshChatSession _chat;
    private readonly AppSettingsStore? _settings;
    private readonly DiagnosticLog? _diagnosticLog;
    private readonly object _gate = new();
    private readonly List<PiMessage> _messages = [];
    private readonly List<PiToolCard> _tools = [];
    private readonly List<string> _queuedTexts = [];
    private readonly List<string> _recoverableQueue = [];
    private ConnectionState _connection = ConnectionState.Connected;
    private ActivityState _activity = ActivityState.Idle;
    private TaskResult _result = TaskResult.Unknown;
    private string? _error;
    private bool _idleMaintenance;
    private bool _disposed;

    private long _usageInput = 0;
    private long _usageOutput = 0;
    private long _usageCacheRead = 0;
    private long _usageCacheWrite = 0;
    private int _usageMessages = 0;

    public DshSessionCoordinator(DshChatSession chat, AppSettingsStore? settings = null,
        DiagnosticLog? diagnosticLog = null)
    {
        _chat = chat ?? throw new ArgumentNullException(nameof(chat));
        _settings = settings;
        _diagnosticLog = diagnosticLog;

        _chat.Event += OnSessionEvent;
    }

    public BackendId Backend => BackendId.Dsh;
    public BackendCapabilities Capabilities => BackendCapabilities.For(Backend);
    public SessionSnapshot Snapshot { get { lock (_gate) return MakeSnapshot(); } }
    public event Action<SessionSnapshot>? SnapshotChanged;

    public event Action<ExtensionUiRequest>? ExtensionUiRequestReceived { add { } remove { } }
    public event Action<ExtensionUiRequest>? ExtensionUiRequestClosed { add { } remove { } }
    public event Action<PiExtensionUiRequestEvent>? ExtensionUiNotification { add { } remove { } }
    public event Action<PiExtensionErrorEvent>? ExtensionError { add { } remove { } }

    public ApprovalAvailability ApprovalAvailability => ApprovalAvailability.Verified;
    public string? ApprovalDiagnostic => "DeepSeek Harness 官方 CLI 审批流已适配。";

    public bool IsInIdleMaintenance { get { lock (_gate) return _idleMaintenance; } }
    public bool CanSendPrompt { get { lock (_gate) return !_idleMaintenance && _chat.CanSend; } }
    public RpcProcessExit? LastProcessExit => null;
    public IReadOnlyList<ExtensionUiRequest> PendingExtensionUiRequests => [];

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
                model = new { provider = "deepseek", id = _chat.CurrentModel ?? "dsh" },
                activity = _activity.ToString().ToLowerInvariant(),
            };
            return Task.FromResult(new RpcResponse("dsh", "get_state", true,
                JsonSerializer.SerializeToElement(data), null));
        }
    }

    public Task<RpcResponse> QueryAvailableModelsAsync(CancellationToken cancellationToken = default)
    {
        var models = new[]
        {
            new { id = "deepseek-coder", name = "DeepSeek Coder" },
            new { id = "deepseek-chat", name = "DeepSeek Chat" },
            new { id = "deepseek-reasoner", name = "DeepSeek Reasoner" },
        };
        return Task.FromResult(new RpcResponse("dsh", "get_available_models", true,
            JsonSerializer.SerializeToElement(new { models }), null));
    }

    public Task<RpcResponse> SetModelAsync(string provider, string modelId, CancellationToken cancellationToken = default) =>
        Task.FromResult(new RpcResponse("dsh", "set_model", true,
            JsonSerializer.SerializeToElement(new { model = new { provider, id = modelId } }), null));

    public Task<RpcResponse> SetThinkingLevelAsync(string level, CancellationToken cancellationToken = default) =>
        Task.FromResult(new RpcResponse("dsh", "set_thinking_level", true,
            JsonSerializer.SerializeToElement(new { thinkingLevel = level }), null));

    public Task<ThinkingLevelAvailability> QueryThinkingLevelsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new ThinkingLevelAvailability(["off", "low", "medium", "high"], "off", _chat.CurrentModel));

    public Task<RpcResponse> QueryCommandsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new RpcResponse("dsh", "get_commands", true,
            JsonSerializer.SerializeToElement(new { commands = Array.Empty<object>() }), null));

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
            entryList.Add(new { id, parentId = lastId, type = "message", message = new { role, content } });
            lastId = id;
        }

        return Task.FromResult(new RpcResponse("dsh", "get_entries", true,
            JsonSerializer.SerializeToElement(new { entries = entryList, leafId = lastId }), null));
    }

    public Task<RpcResponse> SetSessionNameAsync(string name, CancellationToken cancellationToken = default) =>
        Task.FromResult(new RpcResponse("dsh", "set_session_name", true, null, null));

    public Task<RpcResponse> ForkAsync(string entryId, CancellationToken cancellationToken = default) =>
        Task.FromResult(new RpcResponse("dsh", "fork", false, null, "DSH backend fork is managed via CLI."));

    public Task SetExecutionModeAsync(string mode, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task RespondToExtensionUiAsync(ExtensionUiRequest request, PiExtensionUiResponse response,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public void RestoreHistory(SessionHistory history, bool previousRunInterrupted)
    {
        lock (_gate)
        {
            _messages.Clear();
            _messages.AddRange(history.CurrentBranchMessages);
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

    private sealed class MaintenanceToken(DshSessionCoordinator coordinator) : IDisposable
    {
        private DshSessionCoordinator? _coordinator = coordinator;
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
                case SessionEvent.ActivityChanged act:
                    _activity = act.Activity switch
                    {
                        SessionActivity.Running => ActivityState.Running,
                        SessionActivity.Cancelling => ActivityState.Cancelling,
                        _ => ActivityState.Idle,
                    };
                    break;
                case SessionEvent.TurnSettled settled:
                    _activity = ActivityState.Idle;
                    _result = settled.Outcome == SessionOutcome.Completed ? TaskResult.Completed : TaskResult.Failed;
                    break;
                case SessionEvent.SessionError err:
                    _error = err.Message;
                    break;
                case SessionEvent.MessageFinished finished:
                    _messages.Add(new PiMessage(
                        finished.FinalMessage.Role == MessageRole.User ? "user" : "assistant",
                        finished.FinalMessage.Blocks.Select(b => b switch
                        {
                            TextBlock t => (PiContentBlock)new PiTextBlock(t.Text, default),
                            _ => new PiOtherBlock("other", default),
                        }).ToArray(),
                        null, null, null, default));
                    break;
            }
        }
        Publish();
    }

    private void Publish() => SnapshotChanged?.Invoke(Snapshot);

    private SessionSnapshot MakeSnapshot() => new(
        Guid.NewGuid(),
        _connection,
        _activity,
        _result,
        _messages.ToArray(),
        _tools.ToArray(),
        null,
        _queuedTexts.ToArray(),
        _recoverableQueue.ToArray(),
        [],
        _error,
        SessionFaultKind.None,
        ApprovalAvailability.Verified,
        "DeepSeek Harness 官方 CLI 审批流已适配。");

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _chat.Event -= OnSessionEvent;
        await _chat.DisposeAsync().ConfigureAwait(false);
    }
}
