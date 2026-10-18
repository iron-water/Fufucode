using Fufucode.Core;
using Fufucode.Core.RemoteControl;

namespace Fufucode.App.Features.RemoteControl;

/// <summary>手机确认复用真实会话的原始请求及代次；不生成第二套批准状态。</summary>
public sealed class SessionApprovalGate : IRemoteToolApprovalGate, IDisposable
{
    private readonly IExtensionUiSession _session;
    private readonly SemaphoreSlim _responses = new(1, 1);
    private bool _disposed;

    public SessionApprovalGate(IExtensionUiSession session)
    {
        _session = session;
        session.ExtensionUiRequestReceived += Received;
        session.ExtensionUiRequestClosed += Closed;
    }

    private static string Id(ExtensionUiRequest r) => $"{r.Generation:N}:{r.Id}";
    private static RemoteApprovalRequest Map(ExtensionUiRequest r) => new(
        Id(r), r.Id, "会话确认", r.Payload.GetRawText(),
        r.Payload.TryGetProperty("title", out var t) && t.ValueKind == System.Text.Json.JsonValueKind.String ? t.GetString() : null,
        r.ReceivedAt);
    public IReadOnlyList<RemoteApprovalRequest> Pending => _disposed ? [] :
        _session.PendingExtensionUiRequests.Where(r => r.Method == "confirm").Select(Map).ToArray();
    public event EventHandler<RemoteApprovalRequest>? Requested;
    public event EventHandler<RemoteApprovalResolution>? Resolved;
    private void Received(ExtensionUiRequest r) { if (!_disposed && r.Method == "confirm") Requested?.Invoke(this, Map(r)); }
    private void Closed(ExtensionUiRequest r)
    {
        if (!_disposed && r.Method == "confirm")
            Resolved?.Invoke(this, new(Id(r), r.Id, RemoteApprovalDecision.Deny, false, "请求已结束或失效。"));
    }
    public RemoteApprovalRequest Request(string toolCallId, string toolName, string argsJson, string? summary = null) =>
        throw new NotSupportedException("必须由真实会话创建审批请求。");
    public Task<RemoteApprovalResolution> WaitAsync(string approvalId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("审批等待由真实会话持有。");

    public async Task<RemoteApprovalResolution> ResolveAsync(string approvalId, RemoteApprovalDecision decision,
        CancellationToken cancellationToken = default)
    {
        await _responses.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var request = _disposed ? null : _session.PendingExtensionUiRequests
                .FirstOrDefault(r => r.Method == "confirm" && Id(r) == approvalId);
            if (request is null || !Enum.IsDefined(decision))
                return new(approvalId, "", decision, false, "审批不存在或已失效。");
            try
            {
                await _session.RespondToExtensionUiAsync(request,
                    PiExtensionUiResponse.Confirm(decision == RemoteApprovalDecision.Approve), cancellationToken)
                    .ConfigureAwait(false);
                return new(approvalId, request.Id, decision, true, null);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            { return new(approvalId, request.Id, decision, false, error.Message); }
        }
        finally { _responses.Release(); }
    }
    public void Dispose()
    {
        _disposed = true;
        _session.ExtensionUiRequestReceived -= Received;
        _session.ExtensionUiRequestClosed -= Closed;
    }
}
