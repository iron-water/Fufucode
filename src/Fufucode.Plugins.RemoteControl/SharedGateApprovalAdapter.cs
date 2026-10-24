using Fufucode.Core;
using Fufucode.Core.Approvals;
using Fufucode.Core.RemoteControl;

namespace Fufucode.App.Features.RemoteControl;

/// <summary>
/// 把共享审批 gate 适配为远控线协议的审批入口。
///
/// 与其它入口共用同一个 <see cref="SharedApprovalGate"/>：
/// 手机批准、FUFU 批准、桌面批准最终都落到同一个决定上，先到先得。
/// 不再自己订阅 IExtensionUiSession，避免出现第二个真源。
/// </summary>
public sealed class SharedGateApprovalAdapter : IRemoteToolApprovalGate, IDisposable
{
    private readonly SharedApprovalGate _gate;

    public SharedGateApprovalAdapter(SharedApprovalGate gate)
    {
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _gate.Requested += OnRequested;
        _gate.Decided += OnDecided;
    }

    public event EventHandler<RemoteApprovalRequest>? Requested;
    public event EventHandler<RemoteApprovalResolution>? Resolved;

    public IReadOnlyList<RemoteApprovalRequest> Pending =>
        _gate.Pending.Select(Map).ToArray();

    /// <summary>共享 gate 不接受外部凭空登记请求；请求只能来自真实会话。</summary>
    public RemoteApprovalRequest Request(string toolCallId, string toolName, string argsJson, string? summary = null) =>
        throw new NotSupportedException("审批请求必须由真实会话产生。");

    public async Task<RemoteApprovalResolution> ResolveAsync(
        string approvalId,
        RemoteApprovalDecision decision,
        CancellationToken cancellationToken = default)
    {
        var resolution = await _gate
            .DecideAsync(approvalId, decision == RemoteApprovalDecision.Approve, "jiii", cancellationToken)
            .ConfigureAwait(false);
        return new RemoteApprovalResolution(
            resolution.ApprovalId,
            resolution.ToolCallId,
            decision,
            resolution.Accepted,
            resolution.Message);
    }

    public async Task<RemoteApprovalResolution> WaitAsync(
        string approvalId,
        CancellationToken cancellationToken = default)
    {
        var resolution = await _gate.WaitAsync(approvalId, cancellationToken).ConfigureAwait(false);
        return new RemoteApprovalResolution(
            resolution.ApprovalId,
            resolution.ToolCallId,
            resolution.Approved ? RemoteApprovalDecision.Approve : RemoteApprovalDecision.Deny,
            resolution.Accepted,
            resolution.Message);
    }

    private void OnRequested(SharedApprovalRequest request) =>
        Requested?.Invoke(this, Map(request));

    private void OnDecided(SharedApprovalResolution resolution) =>
        Resolved?.Invoke(this, new RemoteApprovalResolution(
            resolution.ApprovalId,
            resolution.ToolCallId,
            resolution.Approved ? RemoteApprovalDecision.Approve : RemoteApprovalDecision.Deny,
            resolution.Accepted,
            resolution.Message));

    private static RemoteApprovalRequest Map(SharedApprovalRequest request) => new(
        request.ApprovalId,
        request.ToolCallId,
        request.ToolName,
        request.ArgsJson,
        request.Summary,
        request.RequestedAtUtc);

    public void Dispose()
    {
        _gate.Requested -= OnRequested;
        _gate.Decided -= OnDecided;
    }
}
