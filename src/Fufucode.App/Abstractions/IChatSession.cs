using System;
using System.Threading;
using System.Threading.Tasks;

namespace Fufucode.App.Abstractions;

/// <summary>
/// 聊天会话契约：发送、停止与事件流。这是轨道 C 外壳与轨道 A 的 Core（真实 Pi 适配）之间的对接点。
/// 本接口已冻结（见 docs/decisions.md [轨道C] 条目），改动需 A、C 双方同步。
/// </summary>
/// <remarks>
/// 线程约定：<see cref="Event"/> 可在任意线程引发（真实实现通常在 RPC 读取泵线程上引发）；
/// 订阅者自行封送到 UI 线程，接口本身不依赖任何 UI 框架。
/// <see cref="SendAsync"/> 在请求被接受时完成（对应 prompt 被接收或处理），不代表 Agent 运行结束。
/// </remarks>
public interface IChatSession
{
    /// <summary>连接状态（计划 §7.1）。</summary>
    SessionConnection Connection { get; }

    /// <summary>活动状态（计划 §7.1）。</summary>
    SessionActivity Activity { get; }

    /// <summary>最近一次任务的终态；尚未完成任何任务时为 null。</summary>
    SessionOutcome? LastOutcome { get; }

    /// <summary>当前使用的模型显示名；未知时为 null。模型与思考等级从 Pi 查询，不由客户端写死。</summary>
    string? CurrentModel { get; }

    /// <summary>是否可以提交新 prompt。</summary>
    bool CanSend { get; }

    /// <summary>停止按钮是否可用（运行中或队列非空时为 true）。</summary>
    bool CanStop { get; }

    /// <summary>当前排队等待执行的文本数量。</summary>
    int QueuedCount { get; }

    /// <summary>
    /// 事件流：消息重建分块事件、错误、状态与队列变化。事件可能在非 UI 线程引发。
    /// </summary>
    event EventHandler<SessionEvent>? Event;

    /// <summary>
    /// 提交一条用户消息。请求被接受时返回（排队也视为接受）。
    /// 调用方不应假定本方法返回时消息已经出现在时间线——用户消息同样通过
    /// <see cref="Event"/> 事件流回放（MessageStarted/MessageFinished，Role 为 User）。
    /// </summary>
    Task SendAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>
    /// 停止：先清空队列，再中止进行中的一轮（计划 §6.5）。
    /// 被清空的队列内容通过 <see cref="SessionEvent.QueueCleared"/> 事件回传，
    /// 订阅者将其保存为待恢复草稿文本，且不得覆盖用户已有草稿。
    /// 停止过程中应阻止新的提交竞态；本方法在停止流程收敛后返回。
    /// </summary>
    Task StopAsync(CancellationToken cancellationToken = default);
}
