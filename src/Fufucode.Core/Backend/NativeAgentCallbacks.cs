using System;
using System.Threading;
using System.Threading.Tasks;

namespace Fufucode.Core.Backend;

/// <summary>一次调用专用的观察回调；不把 UI 订阅状态保存在共享引擎或模型客户端中。</summary>
public sealed class NativeAgentCallbacks
{
    public ModelStreamCallbacks Stream { get; init; } = ModelStreamCallbacks.None;
    public Action<PureAgentMessage>? OnMessageAppended { get; init; }
    public Action<PureToolCall>? OnToolStarting { get; init; }
    public Action<PureToolCall, PureToolResult>? OnToolCompleted { get; init; }
}

/// <summary>可选观察能力：保留原 IPureCSharpAgentEngine，供会话观察既有引擎路径。</summary>
public interface IObservablePureCSharpAgentEngine : IPureCSharpAgentEngine
{
    bool StreamingEnabled { get; }

    Task<PureAgentTurnResult> RunObservedTurnAsync(
        PureModelRequest request,
        NativeAgentCallbacks callbacks,
        CancellationToken cancellationToken = default);
}
