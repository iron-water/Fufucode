using System.Threading;
using System.Threading.Tasks;
using Fufucode.Core;

namespace Fufucode.App.Integration;

/// <summary>
/// 运行中插入（steer）能力：P2-17。冻结契约 <see cref="Fufucode.App.Abstractions.IChatSession"/>
/// 的 SendAsync 无投递参数（适配器运行中一律 FollowUp），steer 由本接口旁路承载：
/// 经 Core 的 <see cref="ChatSessionCoordinator.SendPromptAsync"/>（PromptDelivery.Steer）
/// 投递，队列事件（QueueChanged）照旧从 coordinator 的事件流到 App。
/// </summary>
public interface IPromptSteering
{
    /// <summary>当前是否可插入（通常要求回合运行中）。</summary>
    bool CanSteer { get; }

    /// <summary>把文本以 steer 语义投递：当前回合工具调用结束后、下一次模型调用前送达。</summary>
    Task SteerAsync(string text, CancellationToken cancellationToken = default);
}

/// <summary>
/// 真实 steer 实现：包裹当前会话的 coordinator。会话重启/切换后由 AttachRealSession 重建。
/// </summary>
public sealed class RealPromptSteering : IPromptSteering
{
    private readonly RealSessionHost? _host;
    private readonly ISessionCoordinator? _directCoordinator;

    public RealPromptSteering(RealSessionHost host)
    {
        _host = host;
    }

    /// <summary>直接包裹 coordinator（自测在无 host 的真实会话上使用）。</summary>
    internal RealPromptSteering(ISessionCoordinator coordinator)
    {
        _directCoordinator = coordinator;
    }

    private ISessionCoordinator Coordinator =>
        _directCoordinator ?? _host!.Current.Coordinator;

    public bool CanSteer =>
        (_host?.HasLiveSession ?? true) && Coordinator.Snapshot.Activity != ActivityState.Idle;

    public Task SteerAsync(string text, CancellationToken cancellationToken = default) =>
        Coordinator.SendPromptAsync(text, PromptDelivery.Steer, cancellationToken: cancellationToken);
}
