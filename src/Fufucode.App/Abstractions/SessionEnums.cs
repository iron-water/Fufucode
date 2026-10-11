namespace Fufucode.App.Abstractions;

/// <summary>消息角色。用户提交的文本也通过 <see cref="SessionEvent"/> 事件流回放为 User 消息。</summary>
public enum MessageRole
{
    User,
    Assistant,
}

/// <summary>消息内容块种类，对应计划 §6.6 的分块重建模型。</summary>
public enum MessageBlockKind
{
    /// <summary>普通文本。</summary>
    Text,

    /// <summary>模型思考内容（可折叠展示，只展示 Pi 实际返回的部分）。</summary>
    Thinking,

    /// <summary>工具调用（输入参数、状态、输出）。</summary>
    ToolCall,
}

/// <summary>工具调用的执行状态。</summary>
public enum ToolCallStatus
{
    /// <summary>参数尚未组装完成（JSON 分片仍在到达）。</summary>
    Pending,

    /// <summary>已派发执行，等待结果。</summary>
    Running,

    /// <summary>执行成功完成。</summary>
    Completed,

    /// <summary>执行返回失败（含非零退出码等）。</summary>
    Failed,

    /// <summary>被用户停止或取消。</summary>
    Cancelled,
}

/// <summary>连接状态（计划 §7.1：与活动状态分离）。</summary>
public enum SessionConnection
{
    Stopped,
    Starting,
    Connected,
    Faulted,
    Stopping,
}

/// <summary>活动状态（计划 §7.1：不与连接状态混成一个 IsRunning）。</summary>
public enum SessionActivity
{
    Idle,
    Running,
    Compacting,
    Retrying,
    Cancelling,
}

/// <summary>最近一次任务的终态判定（agent_settled 之后结合消息与错误事件得到）。</summary>
public enum SessionOutcome
{
    Completed,
    Failed,
    Interrupted,
    Unknown,
}

/// <summary>错误分类（计划 §6.3：区分命令拒绝、模型失败、用户取消、进程退出与协议错误）。</summary>
public enum SessionErrorKind
{
    CommandRejected,
    ModelFailed,
    UserCancelled,
    ProcessExited,
    ProtocolError,
    Unknown,
}
