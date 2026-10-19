using System;
using System.Collections.Generic;
using System.IO;
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
/// Claude 会话门面：包装 ClaudeChatSessionAdapter，实现统一聊天契约 IChatSession、
/// IBackendChatSession 与 IExtensionUiSession 审批桥，并维护本地会话历史持久化。
/// </summary>
public sealed class ClaudeChatSession : IChatSession, IBackendChatSession, IExtensionUiSession, IPresentationProfiled, IAsyncDisposable, IDisposable
{
    private readonly ClaudeChatSessionAdapter _adapter;
    private readonly string? _persistFilePath;
    private readonly object _gate = new();
    private readonly List<PureAgentMessage> _history = [];
    private readonly Dictionary<string, ExtensionUiRequest> _pendingUi = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ClaudePermissionRequest> _pendingClaudeRequests = new(StringComparer.Ordinal);
    private string? _lastSessionId;
    private bool _disposed;

    public event EventHandler<SessionEvent>? Event;
    public event Action<ExtensionUiRequest>? ExtensionUiRequestReceived;
    public event Action<ExtensionUiRequest>? ExtensionUiRequestClosed;
    public event Action<PiExtensionUiRequestEvent>? ExtensionUiNotification { add { } remove { } }
    public event Action<PiExtensionErrorEvent>? ExtensionError { add { } remove { } }
    public event Action<string>? SessionIdChanged;

    public ClaudeChatSession(ClaudeChatSessionAdapter adapter, string? persistFilePath = null)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _persistFilePath = persistFilePath;
        _lastSessionId = adapter.SessionId;

        _adapter.Event += OnAdapterEvent;
        _adapter.PermissionRequested += OnPermissionRequested;
    }

    public string BackendId => "claude";
    public BackendPresentationProfile Presentation => BackendPresentationProfile.Claude;
    public string BackendName => "Claude Code";
    public SessionConnection Connection => _adapter.Connection;
    public SessionActivity Activity => _adapter.Activity;
    public SessionOutcome? LastOutcome => _adapter.LastOutcome;
    public string? CurrentModel => _adapter.CurrentModel;
    public string? LastError => _adapter.LastError;
    public bool CanSend => _adapter.CanSend;
    public bool CanStop => _adapter.CanStop;
    public int QueuedCount => _adapter.QueuedCount;

    public string? SessionId => _adapter.SessionId;
    public ClaudeProcessExit? ProcessExit => _adapter.ProcessExit;
    public ClaudeChatSessionAdapter InnerAdapter => _adapter;

    public IReadOnlyList<ExtensionUiRequest> PendingExtensionUiRequests
    {
        get { lock (_gate) return _pendingUi.Values.ToArray(); }
    }

    public ApprovalAvailability ApprovalAvailability => ApprovalAvailability.Verified;
    public string? ApprovalDiagnostic => "Claude 官方 stdio 权限审批桥已就绪。";

    public async Task SendAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        RecordMessage(new PureAgentMessage(PureAgentRole.User, text));
        await _adapter.SendAsync(text, cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _adapter.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RespondToExtensionUiAsync(ExtensionUiRequest request, PiExtensionUiResponse response,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ClaudePermissionRequest? claudeReq;
        lock (_gate)
        {
            _pendingUi.Remove(request.Id);
            _pendingClaudeRequests.Remove(request.Id, out claudeReq);
        }
        ExtensionUiRequestClosed?.Invoke(request);
        if (claudeReq is not null)
        {
            var allowed = response.Kind switch
            {
                PiExtensionUiResponseKind.Confirmation => response.Confirmed == true,
                _ => true,
            };
            var decision = allowed ? ClaudePermissionDecision.Allow : ClaudePermissionDecision.Deny;
            var message = allowed ? null : (response.Confirmed == false ? "User rejected tool execution" : null);
            await _adapter.RespondToPermissionAsync(claudeReq, decision, message, cancellationToken).ConfigureAwait(false);
        }
    }

    private void OnPermissionRequested(ClaudePermissionRequest request)
    {
        var title = $"工具权限审批：{request.ToolName}";
        var desc = string.IsNullOrWhiteSpace(request.Description) ? "" : request.Description + "\n";
        var payload = JsonSerializer.SerializeToElement(new
        {
            method = "confirm",
            title,
            message = $"智能体请求执行工具【{request.ToolName}】。\n{desc}参数：\n{request.InputJson}",
            confirmLabel = "允许执行",
            cancelLabel = "拒绝",
        });
        var uiReq = new ExtensionUiRequest(Guid.NewGuid(), request.RequestId, "confirm", payload,
            DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));
        lock (_gate)
        {
            _pendingUi[request.RequestId] = uiReq;
            _pendingClaudeRequests[request.RequestId] = request;
        }
        ExtensionUiRequestReceived?.Invoke(uiReq);
    }

    private void OnAdapterEvent(object? sender, SessionEvent ev)
    {
        // 监测 SessionId 变化并通知
        var currentId = _adapter.SessionId;
        if (!string.IsNullOrEmpty(currentId) && currentId != _lastSessionId)
        {
            _lastSessionId = currentId;
            SessionIdChanged?.Invoke(currentId);
        }

        // 记录助手消息
        if (ev is SessionEvent.MessageFinished msgFinished && msgFinished.FinalMessage.Role == MessageRole.Assistant)
        {
            var content = string.Join("\n", msgFinished.FinalMessage.Blocks.OfType<TextBlock>().Select(t => t.Text));
            var toolCalls = msgFinished.FinalMessage.Blocks.OfType<ToolCallBlock>().Select(t =>
                new PureToolCall(t.ToolCallId, t.ToolName, t.InputJson ?? "{}")).ToArray();
            RecordMessage(new PureAgentMessage(PureAgentRole.Assistant, content, ToolCalls: toolCalls));
        }
        else if (ev is SessionEvent.BlockFinished blockFinished && blockFinished.FinalBlock is ToolCallBlock toolBlock &&
                 toolBlock.Status is ToolCallStatus.Completed or ToolCallStatus.Failed)
        {
            RecordMessage(new PureAgentMessage(PureAgentRole.Tool, toolBlock.Output ?? "",
                ToolCallId: toolBlock.ToolCallId));
        }

        Event?.Invoke(this, ev);
    }

    private void RecordMessage(PureAgentMessage message)
    {
        lock (_gate)
        {
            _history.Add(message);
            PersistHistory();
        }
    }

    public void RestoreHistory(IReadOnlyList<PureAgentMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        lock (_gate)
        {
            _history.Clear();
            _history.AddRange(messages);
        }
    }

    public IReadOnlyList<PureAgentMessage> GetHistoryMessages()
    {
        lock (_gate) return _history.ToArray();
    }

    private void PersistHistory()
    {
        if (string.IsNullOrEmpty(_persistFilePath)) return;
        try
        {
            var dir = Path.GetDirectoryName(_persistFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(new
            {
                version = 2,
                messages = _history,
                modelId = _adapter.CurrentModel,
                sessionId = _adapter.SessionId
            }, new JsonSerializerOptions { WriteIndented = true });

            var temp = _persistFilePath + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
            File.WriteAllText(temp, json);
            File.Move(temp, _persistFilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning("Failed to persist Claude session: {0}", ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _adapter.Event -= OnAdapterEvent;
        _adapter.PermissionRequested -= OnPermissionRequested;

        // 清理未关闭的 UI 弹窗
        List<ExtensionUiRequest> pending;
        lock (_gate)
        {
            pending = _pendingUi.Values.ToList();
            _pendingUi.Clear();
            _pendingClaudeRequests.Clear();
        }
        foreach (var req in pending)
        {
            ExtensionUiRequestClosed?.Invoke(req);
        }

        await _adapter.DisposeAsync().ConfigureAwait(false);
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
