using System.Text.Json;
using Fufucode.App.Abstractions;
using Fufucode.Core.Plugins;

namespace Fufucode.Plugins.FufuBridge;

/// <summary>
/// “Fufu 连接”插件对协同核心的适配器。
///
/// 只做协议映射：
/// - 把 Fufu 侧的连接/审批状态映射为 Canonical 事实与事件；
/// - 接收其它连接器的事件，包装成 FUFU 能显示的 notice；
/// - 不保存业务状态，不直接向会话提交文本。
///
/// 注意：FUFU 管道 v1 没有通用事件类型；需要真实投递时必须扩展协议，
/// 这里不伪造投递成功。
/// </summary>
public sealed class FufuConnector : ICompanionConnector, IDisposable
{
    public const string Id = "service.fufu-bridge";

    private readonly FufuBridgeHost _host;
    private long _revision;
    private long _lastConnected = -1;
    private bool _disposed;

    public FufuConnector(FufuBridgeHost host) => _host = host;

    public string ConnectorId => Id;

    public CompanionCapabilities Capabilities => new(
        PublishesFacts: true,
        PublishesEvents: true,
        AcceptsIntents: false,
        HandlesApproval: false,
        TransfersFiles: false);

    public CompanionConnectorState State => _disposed
        ? CompanionConnectorState.Stopped
        : _host.IsConnected ? CompanionConnectorState.Running : CompanionConnectorState.Degraded;

    /// <summary>由宿主定时或状态回调调用；只在连接状态变化时发布事实。</summary>
    public void ObserveConnection()
    {
        if (_disposed) return;
        var connected = _host.IsConnected;
        var code = connected ? 1L : 0L;
        if (Interlocked.Exchange(ref _lastConnected, code) == code) return;

        var core = PluginPackageManager.CompanionCore;
        core.Publish(new CanonicalFact(
            FactId: $"{Id}:presence",
            Owner: Id,
            Aggregate: "fufu.presence",
            Revision: Interlocked.Increment(ref _revision),
            ObservedAt: DateTimeOffset.UtcNow,
            StaleAfterMs: 15_000,
            Confidence: "observed",
            PayloadJson: JsonSerializer.Serialize(new
            {
                connected,
                pipe = "fufu.fufucode.v1",
            })));

        core.Publish(new CanonicalEvent(
            EventId: $"{Id}:presence:{Guid.NewGuid():N}",
            Owner: Id,
            Aggregate: "fufu.presence",
            Revision: Volatile.Read(ref _revision),
            Type: connected ? "fufu.presence.connected" : "fufu.presence.disconnected",
            OccurredAt: DateTimeOffset.UtcNow,
            CorrelationId: null,
            Sensitivity: "public",
            PayloadJson: "{}"));
    }

    public Task DeliverAsync(CanonicalEvent evt, CancellationToken cancellationToken = default)
    {
        // FUFU 管道 v1 只能接收 submit/select/approve，没有通用 notice 帧。
        // 这里明确拒绝，避免核心以为提醒已经送达。
        throw new NotSupportedException(
            $"FUFU 管道 v1 不支持入站事件类型 {evt.Type}；需先扩展协议。");
    }

    public void Dispose() => _disposed = true;
}
