using System.Text.Json;
using System.Diagnostics;
using Fufucode.Pi;

namespace Fufucode.Core;

public enum ConnectionState { Stopped, Starting, Connected, Faulted, Stopping }
public enum ActivityState { Idle, Running, Compacting, Retrying, Cancelling }
public enum TaskResult { Unknown, Completed, Failed, Interrupted }
public enum SessionFaultKind { None, ProcessExited, ProtocolError, StartupError }
public enum PromptDelivery { Immediate, Steer, FollowUp }

public sealed record SessionSnapshot(Guid? Generation, ConnectionState Connection, ActivityState Activity,
    TaskResult LastResult, IReadOnlyList<PiMessage> Messages, IReadOnlyList<PiToolCard> Tools,
    PiUsage? CurrentResponseUsage, IReadOnlyList<string> QueuedTexts, IReadOnlyList<string> RecoverableQueue,
    IReadOnlyList<ExtensionUiRequest> PendingUiRequests, string? LastError, SessionFaultKind FaultKind,
    ApprovalAvailability ApprovalAvailability, string? ApprovalDiagnostic);

/// <summary>Owns a single Pi connection. Command acknowledgement and run completion remain separate.</summary>
public sealed class ChatSessionCoordinator : IExtensionUiSession, IAsyncDisposable
{
    private const string BridgeProbeCommand = "fufucode-bridge.status";
    private sealed record PendingUi(ExtensionUiRequest Request, CancellationTokenSource Deadline);
    private static readonly TimeSpan DefaultUiTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MaxUiTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DefaultPromptAcknowledgementTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DefaultExtensionCommandAcknowledgementTimeout = TimeSpan.FromMinutes(5);
    private readonly PiRpcOptions _options;
    private readonly object _stateLock = new();
    private readonly SemaphoreSlim _commands = new(1, 1);
    private readonly SemaphoreSlim _closeGate = new(1, 1);
    private readonly MessageTimeline _timeline = new();
    private readonly List<string> _recoverableQueue = [];
    private readonly List<string> _queuedTexts = [];
    private readonly Dictionary<string, PendingUi> _pendingUi = new(StringComparer.Ordinal);
    private PiRpcClient? _client;
    private Task? _eventPump;
    private ConnectionState _connection = ConnectionState.Stopped;
    private ActivityState _activity = ActivityState.Idle;
    private TaskResult _result = TaskResult.Unknown;
    private string? _error;
    private SessionFaultKind _faultKind;
    private bool _sawAssistantEnd;
    private bool _assistantFailed;
    private bool _assistantInterrupted;
    private bool _stopRequested;
    private long _runEventVersion;
    private ApprovalAvailability _approvalAvailability = ApprovalAvailability.Unverified;
    private string? _approvalDiagnostic;
    private string _executionMode = "manual";
    private bool _supportsExecutionModes;
    public string ExecutionMode { get { lock (_stateLock) return _executionMode; } }
    public bool SupportsExecutionModes { get { lock (_stateLock) return _supportsExecutionModes; } }

    public ChatSessionCoordinator(PiRpcOptions options) => _options = options;
    public BackendId Backend => _options.Backend;
    public BackendCapabilities Capabilities => BackendCapabilities.For(Backend);
    public event Action<SessionSnapshot>? SnapshotChanged;
    public event Action<PiSessionEvent>? ProtocolEvent;
    public event Action<ExtensionUiRequest>? ExtensionUiRequestReceived;
    public event Action<ExtensionUiRequest>? ExtensionUiRequestClosed;
    public event Action? PromptHandled;
    public event Action<PiExtensionUiRequestEvent>? ExtensionUiNotification;
    public event Action<PiExtensionErrorEvent>? ExtensionError;

    public IReadOnlyList<ExtensionUiRequest> PendingExtensionUiRequests => Snapshot.PendingUiRequests;
    public ApprovalAvailability ApprovalAvailability { get { lock (_stateLock) return _approvalAvailability; } }
    /// <summary>Completed messages in the live branch; provisional cumulative usage is excluded.</summary>
    public SessionUsageReport GetUsage() { lock (_stateLock) return _timeline.GetUsage(); }
    public string? ApprovalDiagnostic { get { lock (_stateLock) return _approvalDiagnostic; } }
    public bool CanSendPrompt { get { lock (_stateLock) return !_options.RequireApprovalBridge ||
        _approvalAvailability == ApprovalAvailability.Verified; } }

    public SessionSnapshot Snapshot
    {
        get { lock (_stateLock) return MakeSnapshot(); }
    }

    public Task<PiRpcResponse> QueryStateAsync(CancellationToken cancellationToken = default) =>
        RequireClient().RequestAsync("get_state", null, TimeSpan.FromSeconds(10), cancellationToken);

    public Task<PiRpcResponse> QueryAvailableModelsAsync(CancellationToken cancellationToken = default) =>
        RequireClient().RequestAsync("get_available_models", null, TimeSpan.FromSeconds(10), cancellationToken);

    public Task<PiRpcResponse> SetModelAsync(string provider, string modelId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        return RequireClient().RequestAsync("set_model", JsonSerializer.SerializeToElement(new { provider, modelId }),
            TimeSpan.FromSeconds(10), cancellationToken);
    }

    public Task<PiRpcResponse> SetThinkingLevelAsync(string level, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(level);
        return RequireClient().RequestAsync("set_thinking_level", JsonSerializer.SerializeToElement(new { level }),
            TimeSpan.FromSeconds(10), cancellationToken);
    }

    /// <summary>Levels the current model supports. A successful set_thinking_level may still be clamped.</summary>
    public Task<PiRpcResponse> QueryAvailableThinkingLevelsAsync(CancellationToken cancellationToken = default) =>
        RequireClient().RequestAsync("get_available_thinking_levels", null, TimeSpan.FromSeconds(10), cancellationToken);

    public async Task<ThinkingLevelAvailability> QueryThinkingLevelsAsync(CancellationToken cancellationToken = default) =>
        ThinkingLevelAvailability.Parse(await QueryAvailableThinkingLevelsAsync(cancellationToken));

    /// <summary>Branches at an entry. Pi rebinds the same process to a new session file on success.</summary>
    public Task<PiRpcResponse> ForkAsync(string entryId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entryId);
        return RequireClient().RequestAsync("fork", JsonSerializer.SerializeToElement(new { entryId }),
            TimeSpan.FromSeconds(30), cancellationToken);
    }

    public Task<PiRpcResponse> QueryCommandsAsync(CancellationToken cancellationToken = default) =>
        RequireClient().RequestAsync(CommandsQuery, null, TimeSpan.FromSeconds(10), cancellationToken);

    private string CommandsQuery => Backend == BackendId.Omp ? "get_available_commands" : "get_commands";

    public Task<PiRpcResponse> QueryEntriesAsync(string? since = null, CancellationToken cancellationToken = default) =>
        RequireClient().RequestAsync("get_entries", since is null ? null : JsonSerializer.SerializeToElement(new { since }),
            TimeSpan.FromSeconds(30), cancellationToken);

    public Task<PiRpcResponse> SetSessionNameAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return RequireClient().RequestAsync("set_session_name", JsonSerializer.SerializeToElement(new { name }),
            TimeSpan.FromSeconds(10), cancellationToken);
    }

    public void RestoreHistory(SessionHistory history, bool previousRunInterrupted)
    {
        lock (_stateLock)
        {
            if (_connection != ConnectionState.Connected || _activity != ActivityState.Idle)
                throw new InvalidOperationException("Session must be idle before history is restored.");
            _timeline.Restore(history.CurrentBranchMessages);
            if (previousRunInterrupted) _result = TaskResult.Interrupted;
        }
        Publish();
    }

    /// <summary>Bounded extension-UI audit trail: method, dialog id and how the dialog was answered.</summary>
    private void RecordUi(string eventType, string? requestId, string? method, string? outcome, long? durationMs = null)
    {
        try { _options.DiagnosticSink?.Record(new PiDiagnosticEvent(DateTimeOffset.UtcNow, eventType,
            requestId, method, durationMs, null, null, outcome)); }
        catch { /* Diagnostics must never interrupt protocol transport. */ }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_stateLock)
        {
            if (_connection != ConnectionState.Stopped) throw new InvalidOperationException("Session has already started.");
            _connection = ConnectionState.Starting;
        }
        Publish();
        PiRpcClient? client = null;
        try
        {
            client = await PiRpcClient.StartAsync(_options, cancellationToken);
            lock (_stateLock)
            {
                _client = client;
                _connection = ConnectionState.Connected;
                _activity = ActivityState.Idle;
                _approvalAvailability = ApprovalAvailability.Unverified;
                _executionMode = "manual";
                _supportsExecutionModes = false;
                _approvalDiagnostic = "Approval bridge has not been verified for this connection.";
            }
            _eventPump = PumpEventsAsync(client);
            Publish();
            await ProbeApprovalBridgeAsync(client, cancellationToken);
        }
        catch (Exception error)
        {
            if (client is not null)
            {
                try
                {
                    await client.DisposeAsync();
                    if (_eventPump is not null) await _eventPump;
                }
                catch (Exception cleanupError) { Debug.WriteLine($"Pi startup cleanup failed: {cleanupError}"); }
            }
            lock (_stateLock)
            {
                _client = null;
                _connection = ConnectionState.Faulted;
                _error = error.Message;
                _faultKind = SessionFaultKind.StartupError;
                _approvalAvailability = ApprovalAvailability.Unavailable;
                _approvalDiagnostic = $"Approval unavailable: Pi startup failed ({error.GetBaseException().Message}).";
            }
            Publish();
            throw;
        }
    }

    private async Task ProbeApprovalBridgeAsync(PiRpcClient client, CancellationToken cancellationToken)
    {
        string? diagnostic = null;
        bool verified = false;
        bool supportsModes = false;
        try
        {
            var response = await client.RequestAsync(CommandsQuery, null, TimeSpan.FromSeconds(10), cancellationToken);
            if (!response.Success)
                diagnostic = $"Approval unavailable: get_commands failed ({response.Error}).";
            else if (response.Data is not { ValueKind: JsonValueKind.Object } data ||
                     !data.TryGetProperty("commands", out var commands) || commands.ValueKind != JsonValueKind.Array)
                diagnostic = "Approval unavailable: get_commands returned no command list.";
            else
            {
                verified = commands.EnumerateArray().Any(command =>
                    command.ValueKind == JsonValueKind.Object &&
                    command.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String &&
                    name.GetString() == BridgeProbeCommand &&
                    command.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.String &&
                    source.GetString() == "extension");
                if (!verified) diagnostic = $"Approval unavailable: {BridgeProbeCommand} was not registered by an extension.";
                supportsModes = verified && commands.EnumerateArray().Any(command =>
                    command.ValueKind == JsonValueKind.Object && command.TryGetProperty("name", out var name) &&
                    name.GetString() == "fufucode-bridge.mode");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            if (client.FailureReason is not null) throw;
            diagnostic = $"Approval unavailable: get_commands probe failed ({error.Message}).";
        }
        lock (_stateLock)
        {
            if (_client != client || _connection != ConnectionState.Connected ||
                _approvalAvailability != ApprovalAvailability.Unverified) return;
            _approvalAvailability = verified ? ApprovalAvailability.Verified : ApprovalAvailability.Unavailable;
            _approvalDiagnostic = verified ? null : diagnostic;
            _supportsExecutionModes = supportsModes;
        }
        Publish();
    }

    /// <summary>Returns on command acknowledgement. It does not wait for a model run to settle.</summary>
    public async Task SetExecutionModeAsync(string mode, CancellationToken cancellationToken = default)
    {
        if (mode is not ("manual" or "auto" or "full" or "plan")) throw new ArgumentException("Unknown execution mode", nameof(mode));
        if (!SupportsExecutionModes) throw new InvalidOperationException("The loaded bridge does not provide execution modes");
        var confirmed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnNotification(PiExtensionUiRequestEvent e)
        {
            if (e.Method == "notify" && e.Raw.TryGetProperty("message", out var text) && text.GetString() == "fufucode-bridge: executionMode=" + mode)
                confirmed.TrySetResult();
        }
        ExtensionUiNotification += OnNotification;
        try
        {
            await SendPromptAsync("/fufucode-bridge.mode " + mode, acknowledgementTimeout: TimeSpan.FromSeconds(10), cancellationToken: cancellationToken);
            await confirmed.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        }
        finally { ExtensionUiNotification -= OnNotification; }
    }

    /// <summary>Returns on command acknowledgement. It does not wait for a model run to settle.</summary>
    public async Task SendPromptAsync(string message, PromptDelivery delivery = PromptDelivery.Immediate,
        TimeSpan? acknowledgementTimeout = null, CancellationToken cancellationToken = default,
        IReadOnlyList<PiPromptImage>? images = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        await _commands.WaitAsync(cancellationToken);
        try
        {
            PiRpcClient client;
            long priorEvents;
            lock (_stateLock)
            {
                client = RequireClient();
                if (_options.RequireApprovalBridge && _approvalAvailability != ApprovalAvailability.Verified)
                    throw new InvalidOperationException(_approvalDiagnostic ?? "Approval bridge is unavailable; prompt submission is disabled.");
                if (_activity == ActivityState.Cancelling) throw new InvalidOperationException("Stop is in progress.");
                if (_activity != ActivityState.Idle && delivery == PromptDelivery.Immediate)
                    throw new InvalidOperationException("A run is active; choose steer or follow-up.");
                if (_activity == ActivityState.Idle)
                {
                    // A new submission supersedes text already handed back to the UI after Stop.
                    _recoverableQueue.Clear();
                    _result = TaskResult.Unknown;
                    _error = null;
                    _sawAssistantEnd = _assistantFailed = _assistantInterrupted = _stopRequested = false;
                    _activity = ActivityState.Running;
                }
                priorEvents = _runEventVersion;
            }
            Publish();
            var behavior = delivery switch
            {
                PromptDelivery.Steer => "steer",
                PromptDelivery.FollowUp => "followUp",
                _ => null
            };
            var fields = JsonSerializer.SerializeToElement(new PiPromptFields(message, behavior, images), PiRpcJsonContext.Default.PiPromptFields);
            try
            {
                // Pi acknowledges slash commands only after the extension handler returns. A dialog
                // can outlive the ordinary prompt acknowledgement window while stdout remains live.
                var timeout = acknowledgementTimeout ?? (message.StartsWith("/", StringComparison.Ordinal)
                    ? _options.ExtensionCommandAcknowledgementTimeout ?? DefaultExtensionCommandAcknowledgementTimeout
                    : _options.PromptAcknowledgementTimeout ?? DefaultPromptAcknowledgementTimeout);
                var response = await client.RequestAsync("prompt", fields, timeout, cancellationToken);
                if (!response.Success) throw new PiRpcException($"Pi rejected prompt: {response.Error}");
                if (delivery != PromptDelivery.Immediate)
                {
                    lock (_stateLock) _queuedTexts.Add(message);
                    Publish();
                }
            }
            catch (Exception error)
            {
                lock (_stateLock)
                {
                    if (_connection != ConnectionState.Faulted)
                    {
                        if (client.FailureReason is { } failure)
                        {
                            _connection = ConnectionState.Faulted;
                            _faultKind = client.ExitCode is null ? SessionFaultKind.ProtocolError : SessionFaultKind.ProcessExited;
                            _error = failure.Message;
                        }
                        else _error = error.Message;
                        _result = TaskResult.Unknown;
                        if (_activity == ActivityState.Running && priorEvents == _runEventVersion)
                            _activity = ActivityState.Idle;
                    }
                }
                Publish();
                throw; // A timed-out prompt is never automatically resent.
            }
            // A successful command response means accepted, not settled. Refresh separately so
            // a handled extension command can return to Idle without delaying SendAsync.
            _ = ReconcileAcceptedPromptAsync(client, priorEvents);
        }
        finally { _commands.Release(); }
    }

    private async Task ReconcileAcceptedPromptAsync(PiRpcClient client, long priorEvents)
    {
        // OMP acknowledges before dispatch; an idle query can race the accepted run.
        // Only its prompt_result/session_settled frames can finish accepted work.
        if (Backend == BackendId.Omp) return;
        try
        {
            var state = await client.RequestAsync("get_state", null, TimeSpan.FromSeconds(5));
            if (state.Success && state.Data is { } data && IsIdleState(data))
            {
                var handled = false;
                lock (_stateLock)
                    if (_client == client && _connection == ConnectionState.Connected && priorEvents == _runEventVersion &&
                        _activity == ActivityState.Running && _pendingUi.Count == 0)
                    {
                        _activity = ActivityState.Idle;
                        handled = true;
                    }
                Publish();
                if (handled && PromptHandled is { } observers)
                    foreach (Action observer in observers.GetInvocationList())
                        try { observer(); } catch (Exception error) { System.Diagnostics.Debug.WriteLine(error); }
            }
        }
        catch (Exception error)
        {
            lock (_stateLock)
            {
                if (_client != client || _connection != ConnectionState.Connected) return;
                if (client.FailureReason is { } failure)
                {
                    _connection = ConnectionState.Faulted;
                    _faultKind = client.ExitCode is null ? SessionFaultKind.ProtocolError : SessionFaultKind.ProcessExited;
                    _error = failure.Message;
                }
                else
                {
                    _error = $"State refresh after accepted prompt failed: {error.Message}";
                }
            }
            Publish();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        // An extension command may withhold its prompt acknowledgement until the dialog is answered.
        // Reject dialogs before waiting for the prompt command gate.
        PiRpcClient? pendingClient;
        IReadOnlyList<ExtensionUiRequest> rejected;
        lock (_stateLock)
        {
            pendingClient = _client;
            rejected = ClearPendingUiLocked();
        }
        if (rejected.Count > 0)
        {
            Publish();
            foreach (var request in rejected) InvokeObservers(ExtensionUiRequestClosed, request);
            await CancelDialogsAsync(pendingClient, rejected);
        }
        await _commands.WaitAsync(cancellationToken);
        try
        {
            PiRpcClient client;
            lock (_stateLock)
            {
                client = RequireClient();
                _activity = ActivityState.Cancelling;
                _stopRequested = true;
            }
            Publish();
            try
            {
                var cleared = await client.RequestAsync(Backend == BackendId.Omp ? "abort_and_restore_queue" : "clear_queue",
                    null, TimeSpan.FromSeconds(15), cancellationToken);
                if (!cleared.Success) throw new PiRpcException($"Pi refused clear_queue: {cleared.Error}");
                var recovered = ParseClearedQueue(cleared.Data, Backend);
                lock (_stateLock)
                {
                    _recoverableQueue.AddRange(recovered);
                    _queuedTexts.Clear();
                }
                Publish();
                if (Backend == BackendId.Pi)
                {
                    var aborted = await client.RequestAsync("abort", null, TimeSpan.FromSeconds(15), cancellationToken);
                    if (!aborted.Success) throw new PiRpcException($"Pi refused abort: {aborted.Error}");
                }
                lock (_stateLock)
                {
                    _activity = ActivityState.Idle;
                    _result = TaskResult.Interrupted;
                }
                Publish();
            }
            catch (Exception error)
            {
                lock (_stateLock) { _error = error.Message; _result = TaskResult.Unknown; }
                Publish();
                throw; // Never report stopped if the queue may still run.
            }
        }
        finally { _commands.Release(); }
    }

    public async Task CloseAsync()
    {
        await _closeGate.WaitAsync();
        try
        {
            PiRpcClient? client;
            IReadOnlyList<ExtensionUiRequest> closed;
            lock (_stateLock)
            {
                client = _client;
                _connection = ConnectionState.Stopping;
                _approvalAvailability = ApprovalAvailability.Unavailable;
                _approvalDiagnostic = "Approval unavailable: Pi connection is closing.";
                closed = ClearPendingUiLocked();
            }
            Publish();
            foreach (var request in closed) InvokeObservers(ExtensionUiRequestClosed, request);
            await CancelDialogsAsync(client, closed);
            if (client is not null) await client.DisposeAsync();
            if (_eventPump is not null) await _eventPump;
            await _commands.WaitAsync();
            try
            {
                lock (_stateLock) { _client = null; _connection = ConnectionState.Stopped; _activity = ActivityState.Idle; }
                Publish();
            }
            finally { _commands.Release(); }
        }
        finally { _closeGate.Release(); }
    }

    private async Task CancelDialogsAsync(PiRpcClient? client, IReadOnlyList<ExtensionUiRequest> requests)
    {
        if (client is null) return;
        foreach (var request in requests)
        {
            // Stop and close abandon a dialog that nobody can answer any more; record it as such.
            RecordUi("extension_ui_response", request.Id, request.Method, "closed",
                (long)(DateTimeOffset.UtcNow - request.ReceivedAt).TotalMilliseconds);
            try { await client.SendExtensionUiResponseAsync(request.Id, PiExtensionUiResponse.Cancel()); }
            catch (Exception) { break; } // A dead connection is closed by the caller or event pump.
        }
    }

    private async Task PumpEventsAsync(PiRpcClient client)
    {
        try
        {
            await foreach (var raw in client.Events.ReadAllAsync())
            {
                if (raw.Generation != client.Generation) continue;
                var ev = PiProtocol.Parse(raw);
                ExtensionUiRequest? dialog = null;
                PiExtensionUiRequestEvent? notification = null;
                PiExtensionErrorEvent? extensionError = null;
                bool handled = false;
                lock (_stateLock)
                {
                    if (ev.Type is "agent_start" or "agent_end" or "agent_settled" or "prompt_result" or "session_settled" or
                        "message_start" or "message_update" or "message_end" or
                        "turn_start" or "turn_end" or "auto_retry_start" or "auto_retry_end" or
                        "compaction_start" or "compaction_end") _runEventVersion++;
                    _timeline.Apply(ev);
                    switch (ev.Type)
                    {
                        case "prompt_result" when Backend == BackendId.Omp:
                            var outcome = ev.Raw.GetProperty("status").GetString();
                            _assistantFailed |= outcome == "error";
                            _assistantInterrupted |= outcome == "aborted";
                            if (ev.Raw.TryGetProperty("error", out var promptError) &&
                                promptError.TryGetProperty("message", out var errorMessage)) _error = errorMessage.GetString();
                            if (ev.Raw.TryGetProperty("agentInvoked", out var invoked) && invoked.ValueKind == JsonValueKind.False)
                            {
                                handled = outcome == "completed";
                            }
                            if (!ev.Raw.TryGetProperty("sessionSettled", out var settled) || settled.ValueKind != JsonValueKind.True) break;
                            SettleLocked();
                            break;
                        case "session_settled" when Backend == BackendId.Omp:
                            SettleLocked();
                            break;
                        case "agent_start": _activity = ActivityState.Running; break;
                        case "compaction_start" when _activity is ActivityState.Running or ActivityState.Retrying:
                            _activity = ActivityState.Compacting; break;
                        case "compaction_end" when _activity == ActivityState.Compacting:
                            _activity = ActivityState.Running; break;
                        case "auto_retry_start" when _activity is ActivityState.Running or ActivityState.Compacting:
                            _activity = ActivityState.Retrying; break;
                        case "auto_retry_end" when _activity == ActivityState.Retrying:
                            _activity = ActivityState.Running; break;
                        case "agent_settled":
                            _activity = ActivityState.Idle;
                            _queuedTexts.Clear();
                            _result = _stopRequested || _assistantInterrupted ? TaskResult.Interrupted :
                                _assistantFailed ? TaskResult.Failed : _sawAssistantEnd ? TaskResult.Completed : TaskResult.Unknown;
                            break;
                    }
                    if (ev is PiMessageEvent { Type: "message_end", Message.Role: "assistant" } ended)
                    {
                        _sawAssistantEnd = true;
                        _assistantFailed |= ended.Message.StopReason == "error";
                        _assistantInterrupted |= ended.Message.StopReason == "aborted";
                        // Pi may attach "request aborted" as errorMessage to a cancelled assistant.
                        // Cancellation is represented by Interrupted, not a new error notification.
                        if (ended.Message.StopReason != "aborted" && ended.Message.ErrorMessage is { Length: > 0 })
                            _error = ended.Message.ErrorMessage;
                    }
                    if (ev is PiMessageEvent { Type: "message_start", Message.Role: "user" } user &&
                        user.Message.Content.OfType<PiTextBlock>().FirstOrDefault() is { } userText)
                    {
                        var queuedIndex = _queuedTexts.IndexOf(userText.Text);
                        if (queuedIndex >= 0) _queuedTexts.RemoveAt(queuedIndex);
                    }
                    if (ev is PiOtherEvent { Type: "auto_retry_end" } retry &&
                        retry.Raw.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False)
                        _assistantFailed = true;
                    if (ev is PiExtensionUiRequestEvent ui && _connection == ConnectionState.Connected)
                    {
                        if (ui.Method is "select" or "confirm" or "input" or "editor")
                        {
                            if (_pendingUi.ContainsKey(ui.Id)) throw new PiRpcException($"Duplicate extension UI request ID: {ui.Id}.");
                            var timeout = UiTimeout(ui.Raw);
                            dialog = new ExtensionUiRequest(ui.Generation, ui.Id, ui.Method, ui.Raw.Clone(),
                                DateTimeOffset.UtcNow, timeout);
                            var deadline = new CancellationTokenSource();
                            _pendingUi.Add(ui.Id, new PendingUi(dialog, deadline));
                            _ = ExpireUiRequestAsync(dialog, deadline.Token);
                        }
                        else if (ui.Method is "notify" or "setStatus" or "setWidget" or "setTitle" or "set_editor_text")
                        {
                            notification = ui;
                            if (ui.Method == "notify" && ui.Raw.TryGetProperty("message", out var modeText) && modeText.ValueKind == JsonValueKind.String)
                            {
                                var text = modeText.GetString();
                                const string prefix = "fufucode-bridge: executionMode=";
                                if (text?.StartsWith(prefix, StringComparison.Ordinal) == true && text[prefix.Length..] is "manual" or "auto" or "full" or "plan")
                                    _executionMode = text[prefix.Length..];
                            }
                        }
                        else _error = $"Unknown extension UI method ignored: {ui.Method}.";
                        // A write tool approval is the only audit gap we cannot reconstruct afterwards,
                        // so every dialog, notification and ignored method leaves a trace here.
                        RecordUi("extension_ui_request", ui.Id, ui.Method,
                            dialog is not null ? null : notification is not null ? "notified" : "ignored");
                    }
                    if (ev is PiExtensionErrorEvent failed)
                    {
                        extensionError = failed;
                        _approvalAvailability = ApprovalAvailability.Unavailable;
                        _approvalDiagnostic = $"Approval unavailable: extension {failed.ExtensionPath} failed in {failed.EventName}: {failed.Error}";
                    }
                }
                Publish();
                if (handled) PromptHandled?.Invoke();
                InvokeObservers(ProtocolEvent, ev);
                if (dialog is not null) InvokeObservers(ExtensionUiRequestReceived, dialog);
                if (notification is not null) InvokeObservers(ExtensionUiNotification, notification);
                if (extensionError is not null) InvokeObservers(ExtensionError, extensionError);
            }
        }
        catch (Exception error)
        {
            IReadOnlyList<ExtensionUiRequest> closed;
            lock (_stateLock)
            {
                closed = ClearPendingUiLocked();
                if (_connection == ConnectionState.Connected)
                {
                    _connection = ConnectionState.Faulted;
                    _activity = ActivityState.Idle;
                    _result = TaskResult.Unknown;
                    _error = error.Message;
                    _faultKind = client.ExitCode is null ? SessionFaultKind.ProtocolError : SessionFaultKind.ProcessExited;
                }
                _approvalAvailability = ApprovalAvailability.Unavailable;
                _approvalDiagnostic = "Approval unavailable: Pi connection was lost.";
            }
            Publish();
            foreach (var request in closed)
            {
                RecordUi("extension_ui_response", request.Id, request.Method, "closed",
                    (long)(DateTimeOffset.UtcNow - request.ReceivedAt).TotalMilliseconds);
                InvokeObservers(ExtensionUiRequestClosed, request);
            }
        }
    }

    public async Task RespondToExtensionUiAsync(ExtensionUiRequest request, PiExtensionUiResponse response,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(response);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Method == "confirm" && response.Kind == PiExtensionUiResponseKind.Value ||
            request.Method != "confirm" && response.Kind == PiExtensionUiResponseKind.Confirmation)
            throw new ArgumentException("Response does not match the dialog method.", nameof(response));
        if (request.Method == "select" && response.Kind == PiExtensionUiResponseKind.Value &&
            (!request.Payload.TryGetProperty("options", out var options) || options.ValueKind != JsonValueKind.Array ||
             !options.EnumerateArray().Any(option => option.ValueKind == JsonValueKind.String && option.GetString() == response.Value)))
            throw new ArgumentException("Selected value was not offered by Pi.", nameof(response));

        await CompleteUiRequestAsync(request, response, OutcomeOf(response));
    }

    private static string OutcomeOf(PiExtensionUiResponse response) => response.Kind switch
    {
        PiExtensionUiResponseKind.Value => "value",
        PiExtensionUiResponseKind.Confirmation => response.Confirmed == true ? "confirmed" : "declined",
        _ => "cancelled"
    };

    /// <summary>Records who answered a dialog before the reply leaves the process.</summary>
    private async Task CompleteUiRequestAsync(ExtensionUiRequest request, PiExtensionUiResponse response, string outcome)
    {
        PiRpcClient client;
        lock (_stateLock)
        {
            client = RequireClient();
            if (request.Generation != client.Generation || !_pendingUi.TryGetValue(request.Id, out var pending) ||
                !ReferenceEquals(pending.Request, request))
                throw new InvalidOperationException("Extension UI request is stale or has already been answered.");
            _pendingUi.Remove(request.Id);
            pending.Deadline.Cancel();
            pending.Deadline.Dispose();
        }
        Publish();
        InvokeObservers(ExtensionUiRequestClosed, request);
        try
        {
            await client.SendExtensionUiResponseAsync(request.Id, response);
            RecordUi("extension_ui_response", request.Id, request.Method, outcome,
                (long)(DateTimeOffset.UtcNow - request.ReceivedAt).TotalMilliseconds);
        }
        catch (Exception error)
        {
            lock (_stateLock) if (_client == client) _error = $"Extension UI reply failed: {error.Message}";
            Publish();
            throw;
        }
    }

    private async Task ExpireUiRequestAsync(ExtensionUiRequest request, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(request.Timeout, cancellationToken);
            await CompleteUiRequestAsync(request, PiExtensionUiResponse.Cancel(), "expired");
        }
        catch (OperationCanceledException) { }
        catch (InvalidOperationException) { } // A reply or connection close won the race.
        catch (Exception error)
        {
            lock (_stateLock) if (_client?.Generation == request.Generation) _error = $"Extension UI cancellation failed: {error.Message}";
            Publish();
        }
    }

    private TimeSpan UiTimeout(JsonElement request)
    {
        if (!request.TryGetProperty("timeout", out var timeout))
        {
            var fallback = _options.UiDefaultTimeout ?? DefaultUiTimeout;
            if (fallback <= TimeSpan.Zero) throw new PiRpcException("Extension UI default timeout must be positive.");
            return fallback < MaxUiTimeout ? fallback : MaxUiTimeout;
        }
        if (timeout.ValueKind != JsonValueKind.Number || !timeout.TryGetDouble(out var milliseconds) ||
            !double.IsFinite(milliseconds) || milliseconds < 0)
            throw new PiRpcException("Extension UI timeout is invalid.");
        return TimeSpan.FromMilliseconds(Math.Min(milliseconds, MaxUiTimeout.TotalMilliseconds));
    }

    private IReadOnlyList<ExtensionUiRequest> ClearPendingUiLocked()
    {
        var closed = _pendingUi.Values.Select(p => p.Request).ToArray();
        foreach (var pending in _pendingUi.Values)
        {
            pending.Deadline.Cancel();
            pending.Deadline.Dispose();
        }
        _pendingUi.Clear();
        return closed;
    }

    private void SettleLocked()
    {
        _activity = ActivityState.Idle;
        _queuedTexts.Clear();
        _result = _stopRequested || _assistantInterrupted ? TaskResult.Interrupted :
            _assistantFailed ? TaskResult.Failed : _sawAssistantEnd ? TaskResult.Completed : TaskResult.Unknown;
    }

    private static bool IsIdleState(JsonElement data) =>
        data.ValueKind == JsonValueKind.Object &&
        data.TryGetProperty("isStreaming", out var streaming) && streaming.ValueKind == JsonValueKind.False &&
        data.TryGetProperty("pendingMessageCount", out var pending) && pending.ValueKind == JsonValueKind.Number && pending.GetInt32() == 0;

    private static IReadOnlyList<string> ParseClearedQueue(JsonElement? data, BackendId backend)
    {
        if (data is not { ValueKind: JsonValueKind.Object } value) throw new PiRpcException("clear_queue lacks data.");
        var result = new List<string>();
        if (backend == BackendId.Omp && value.TryGetProperty("truncated", out var truncated) && truncated.ValueKind == JsonValueKind.True)
            throw new PiRpcException("OMP stopped, but the recovered queue was truncated; inspect session history before resubmitting.");
        foreach (var name in new[] { "steering", "followUp" })
        {
            if (!value.TryGetProperty(name, out var list) || list.ValueKind != JsonValueKind.Array)
                throw new PiRpcException($"clear_queue lacks {name} array.");
            foreach (var item in list.EnumerateArray())
            {
                var text = backend == BackendId.Omp && item.ValueKind == JsonValueKind.Object && item.TryGetProperty("text", out var content) ? content : item;
                if (text.ValueKind != JsonValueKind.String) throw new PiRpcException("clear_queue contains non-text item.");
                result.Add(text.GetString()!);
            }
        }
        return result;
    }

    private PiRpcClient RequireClient()
    {
        lock (_stateLock)
            return _connection == ConnectionState.Connected && _client is { } client
                ? client : throw new InvalidOperationException("Pi session is not connected.");
    }

    private SessionSnapshot MakeSnapshot() => new(_client?.Generation, _connection, _activity, _result,
        _timeline.Messages, _timeline.Tools, _timeline.CurrentResponseUsage,
        _queuedTexts.ToArray(), _recoverableQueue.ToArray(), _pendingUi.Values.Select(p => p.Request).ToArray(),
        _error, _faultKind, _approvalAvailability, _approvalDiagnostic);

    private void Publish()
    {
        SessionSnapshot snapshot;
        lock (_stateLock) snapshot = MakeSnapshot();
        InvokeObservers(SnapshotChanged, snapshot);
    }

    private static void InvokeObservers<T>(Action<T>? observers, T value)
    {
        if (observers is null) return;
        foreach (Action<T> observer in observers.GetInvocationList())
        {
            try { observer(value); }
            catch (Exception error) { Debug.WriteLine($"Fufucode observer failed: {error}"); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync();
        _commands.Dispose();
        _closeGate.Dispose();
    }
}
