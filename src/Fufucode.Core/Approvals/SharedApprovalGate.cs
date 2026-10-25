using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Fufucode.Core.Approvals;

/// <summary>待审批请求的规范化投影。ApprovalId = generation + 原始 id，不会跨会话复用。</summary>
public sealed record SharedApprovalRequest(
    string ApprovalId,
    string ToolCallId,
    string ToolName,
    string ArgsJson,
    string? Summary,
    DateTimeOffset RequestedAtUtc);

/// <summary>
/// 审批结果。Accepted=false 表示请求不存在、已失效，或已被另一个入口先决定；
/// Message 会说明是谁决定的，便于另一个入口如实展示。
/// </summary>
public sealed record SharedApprovalResolution(
    string ApprovalId,
    string ToolCallId,
    bool Approved,
    bool Accepted,
    string? Message,
    string Source);

/// <summary>
/// 单一审批 gate：一个真实会话只允许一个订阅者与一个最终决定。
///
/// FUFU 与 Jiii 都从这里收请求、从这里回决定；先到先得，后到者得到
/// Accepted=false 与「已由 X 处理」，不会产生第二次 RespondToExtensionUiAsync。
///
/// 不生成第二套状态：Pending 始终读真实会话，请求关闭时如实标记失效。
/// </summary>
public sealed class SharedApprovalGate : IDisposable
{
    private const int MaxDecisions = 128;
    private static readonly StringComparer IdComparer = StringComparer.Ordinal;

    private readonly IExtensionUiSession _session;
    private readonly SemaphoreSlim _decideGate = new(1, 1);
    private readonly object _state = new();
    private readonly Dictionary<string, SharedApprovalResolution> _decided = new(IdComparer);
    private readonly Queue<string> _decidedOrder = new();

    /// <summary>
    /// 正在回传决定的请求。真实会话会在 Respond 内部同步触发 Closed；若不先登记，
    /// 关闭事件会抢先把成功决定覆盖成「已失效」。
    /// </summary>
    private readonly HashSet<string> _deciding = new(IdComparer);
    private bool _disposed;

    public SharedApprovalGate(IExtensionUiSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _session.ExtensionUiRequestReceived += OnRequested;
        _session.ExtensionUiRequestClosed += OnClosed;
    }

    /// <summary>新的待审批请求（来自真实会话）。可在任意线程触发。</summary>
    public event Action<SharedApprovalRequest>? Requested;

    /// <summary>请求被某个入口决定，或因会话关闭而失效。每个 ApprovalId 至多一次。</summary>
    public event Action<SharedApprovalResolution>? Decided;

    public IReadOnlyList<SharedApprovalRequest> Pending =>
        _disposed ? Array.Empty<SharedApprovalRequest>() : MapPending();

    /// <summary>
    /// 由任一入口提交决定。只有第一个调用生效；重复、过期或未知请求返回 Accepted=false。
    /// </summary>
    public async Task<SharedApprovalResolution> DecideAsync(
        string approvalId,
        bool approved,
        string source,
        CancellationToken cancellationToken = default)
    {
        if (_disposed)
            return Rejected(approvalId, source, "审批入口已关闭。");

        await _decideGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_state)
            {
                if (_decided.TryGetValue(approvalId, out var existing))
                {
                    return new SharedApprovalResolution(
                        approvalId,
                        existing.ToolCallId,
                        existing.Approved,
                        Accepted: false,
                        Message: $"该审批已由 {existing.Source} 处理。",
                        Source: source);
                }
            }

            var request = FindPending(approvalId);
            if (request is null)
                return Rejected(approvalId, source, "审批不存在或已失效。");

            lock (_state) _deciding.Add(approvalId);
            try
            {
                // 原始请求对象与 generation 原样回传，不重建、不替换。
                await _session.RespondToExtensionUiAsync(
                        request,
                        PiExtensionUiResponse.Confirm(approved),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                lock (_state) _deciding.Remove(approvalId);
                return Rejected(approvalId, source, error.Message);
            }
            lock (_state) _deciding.Remove(approvalId);

            var resolution = new SharedApprovalResolution(
                approvalId, request.Id, approved, Accepted: true, Message: null, Source: source);
            Record(resolution);
            return resolution;
        }
        finally
        {
            _decideGate.Release();
        }
    }

    /// <summary>等待某个审批的决定；已决定则立即返回，未知则返回失效。</summary>
    public async Task<SharedApprovalResolution> WaitAsync(
        string approvalId,
        CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            lock (_state)
            {
                if (_decided.TryGetValue(approvalId, out var decided)) return decided;
            }
            if (_disposed || FindPending(approvalId) is null)
                return Rejected(approvalId, "wait", "审批不存在或已失效。");
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
        return Rejected(approvalId, "wait", "等待审批超时。");
    }

    private void OnRequested(ExtensionUiRequest request)
    {
        if (_disposed || request.Method != "confirm") return;
        Requested?.Invoke(Map(request));
    }

    private void OnClosed(ExtensionUiRequest request)
    {
        if (_disposed || request.Method != "confirm") return;
        var id = IdOf(request);
        lock (_state)
        {
            // 已决定或正在回传的请求不再广播失效：避免把成功决定覆盖成「已失效」。
            if (_decided.ContainsKey(id) || _deciding.Contains(id)) return;
        }

        var resolution = new SharedApprovalResolution(
            id, request.Id, Approved: false, Accepted: false,
            Message: "请求已结束或失效。", Source: "session");
        Record(resolution);
    }

    private ExtensionUiRequest? FindPending(string approvalId)
    {
        if (_disposed) return null;
        foreach (var request in _session.PendingExtensionUiRequests)
        {
            if (request.Method == "confirm" && string.Equals(IdOf(request), approvalId, StringComparison.Ordinal))
                return request;
        }
        return null;
    }

    private IReadOnlyList<SharedApprovalRequest> MapPending()
    {
        var pending = new List<SharedApprovalRequest>();
        foreach (var request in _session.PendingExtensionUiRequests)
        {
            if (request.Method == "confirm") pending.Add(Map(request));
        }
        return pending;
    }

    private void Record(SharedApprovalResolution resolution)
    {
        lock (_state)
        {
            if (!_decided.ContainsKey(resolution.ApprovalId))
            {
                _decided[resolution.ApprovalId] = resolution;
                _decidedOrder.Enqueue(resolution.ApprovalId);
                while (_decidedOrder.Count > MaxDecisions)
                    _decided.Remove(_decidedOrder.Dequeue());
            }
        }
        Decided?.Invoke(resolution);
    }

    private SharedApprovalResolution Rejected(string approvalId, string source, string message)
    {
        var toolCallId = string.Empty;
        try
        {
            toolCallId = FindPending(approvalId)?.Id ?? string.Empty;
        }
        catch (ObjectDisposedException)
        {
            // 会话已释放：保持空 toolCallId，由调用方按失效处理。
        }
        return new SharedApprovalResolution(
            approvalId, toolCallId, Approved: false, Accepted: false, Message: message, Source: source);
    }

    private static string IdOf(ExtensionUiRequest request) => $"{request.Generation:N}:{request.Id}";

    private static SharedApprovalRequest Map(ExtensionUiRequest request) => new(
        IdOf(request),
        request.Id,
        ReadString(request.Payload, "toolName") ?? request.Method,
        request.Payload.GetRawText(),
        ReadString(request.Payload, "title") ?? ReadString(request.Payload, "message"),
        request.ReceivedAt);

    private static string? ReadString(JsonElement payload, string name) =>
        payload.ValueKind == JsonValueKind.Object &&
        payload.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _session.ExtensionUiRequestReceived -= OnRequested;
        _session.ExtensionUiRequestClosed -= OnClosed;
    }
}

/// <summary>
/// 按真实会话共享的 gate 表。两个连接插件拿到的必须是同一个实例，
/// 否则「先到先得」不成立。会话释放后自动回收。
/// </summary>
public static class SharedApprovalRegistry
{
    private static readonly ConditionalWeakTable<IExtensionUiSession, SharedApprovalGate> Gates = new();

    public static SharedApprovalGate For(IExtensionUiSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return Gates.GetValue(session, static value => new SharedApprovalGate(value));
    }
}
