using Fufucode.Core.Approvals;

namespace Fufucode.Plugins.FufuBridge;

/// <summary>
/// “Fufu 连接”的审批入口适配：只把共享 gate 的请求/结果转发给 FUFU 管道。
///
/// 不在本地保存待审批状态，也不自行调用 RespondToExtensionUiAsync；
/// 真正的决定由 <see cref="SharedApprovalGate.DecideAsync"/> 统一串行化，
/// 因此 FUFU、Jiii 与桌面三方不会重复应答。
/// </summary>
public sealed class FufuApprovalBridge : IDisposable
{
    private readonly SharedApprovalGate _gate;
    private bool _disposed;

    public FufuApprovalBridge(SharedApprovalGate gate)
    {
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _gate.Requested += OnRequested;
        _gate.Decided += OnDecided;
    }

    public event Action<SharedApprovalRequest>? Requested;
    public event Action<SharedApprovalResolution>? Resolved;

    public Task<SharedApprovalResolution> ResolveAsync(
        string approvalId,
        bool approved,
        CancellationToken cancellationToken = default) =>
        _gate.DecideAsync(approvalId, approved, "fufu", cancellationToken);

    public IReadOnlyList<SharedApprovalRequest> Pending => _gate.Pending;

    private void OnRequested(SharedApprovalRequest request)
    {
        if (!_disposed) Requested?.Invoke(request);
    }

    private void OnDecided(SharedApprovalResolution resolution)
    {
        if (!_disposed) Resolved?.Invoke(resolution);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gate.Requested -= OnRequested;
        _gate.Decided -= OnDecided;
    }
}
