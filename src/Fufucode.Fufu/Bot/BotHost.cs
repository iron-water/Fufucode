using Fufucode.Fufu.Pet;

namespace Fufucode.Fufu.Bot;

/// <summary>
/// Agent 生命周期事件种类（任务书 §五）：Bot 宿主把 Agent/任务事件翻译为宠物状态。
/// FUFU-01 只有桌面渠道；FUFU-03 接入 FUMO 会话、FUFU-04 接入 Fufucode 会话事件。
/// </summary>
public enum AgentEventKind
{
    TaskStarted,
    TaskCompleted,
    TaskFailed,
    AttentionRequired,
    ModelThinking,
    Idle,
}

/// <summary>
/// Bot 宿主：事件路由 + 行为状态驱动。不依赖宠物窗口可见——
/// 窗口隐藏到托盘时宿主仍处理授权范围内的事件与任务（任务书 §三.2）。
/// FUFU-01 先落事件→状态的确定性映射与会话/渠道预留点；FUMO 与桥接随后接入。
/// </summary>
public sealed class BotHost
{
    private readonly PetBehavior _behavior;

    public BotHost(PetBehavior behavior) => _behavior = behavior;

    /// <summary>当前行为状态（供渠道/托盘展示与将来 IPC 状态上报）。</summary>
    public PetState CurrentState => _behavior.State;

    /// <summary>上报 Agent 事件 → 驱动确定性行为状态。</summary>
    public void NotifyAgentEvent(AgentEventKind kind)
    {
        var state = kind switch
        {
            AgentEventKind.TaskStarted => PetState.Working,
            AgentEventKind.TaskCompleted => PetState.Success,
            AgentEventKind.TaskFailed => PetState.Error,
            AgentEventKind.AttentionRequired => PetState.Attention,
            AgentEventKind.ModelThinking => PetState.Thinking,
            _ => PetState.Idle,
        };
        _behavior.Notify(state);
    }
}
