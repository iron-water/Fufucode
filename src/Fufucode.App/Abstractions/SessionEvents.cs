using System.Collections.Generic;

namespace Fufucode.App.Abstractions;

/// <summary>
/// 会话事件流。所有事件共享同一体系，按计划 §6.6 的语义传递：
/// 用 <see cref="MessageStarted"/> 建立临时消息，按 contentIndex 维护文本/思考/工具调用块，
/// delta 只追加到对应块，<see cref="BlockFinished"/> 用块结束内容替换临时块，
/// <see cref="MessageFinished"/> 是整条消息的最终依据（整体替换，不重复追加）。
/// </summary>
public abstract record SessionEvent
{
    private SessionEvent() { }

    /// <summary>建立临时消息（消息开始）。BlockCount 为本消息预期的内容块数量，未知时为 0。</summary>
    public sealed record MessageStarted(string MessageId, MessageRole Role, string? Model, int BlockCount) : SessionEvent;

    /// <summary>某个内容块开始（按 contentIndex 对齐）。</summary>
    public sealed record BlockStarted(string MessageId, int ContentIndex, MessageBlockKind Kind, string? ToolCallId, string? ToolName) : SessionEvent;

    /// <summary>文本增量，追加到对应块。</summary>
    public sealed record TextAppended(string MessageId, int ContentIndex, string Delta) : SessionEvent;

    /// <summary>思考增量，追加到对应块。</summary>
    public sealed record ThinkingAppended(string MessageId, int ContentIndex, string Delta) : SessionEvent;

    /// <summary>工具调用参数 JSON 分片（可能不完整），追加到对应块的待组装缓冲。</summary>
    public sealed record ToolCallAppended(string MessageId, int ContentIndex, string ToolCallId, string JsonFragment) : SessionEvent;

    /// <summary>块结束：用最终内容替换该块的临时累积。</summary>
    public sealed record BlockFinished(string MessageId, int ContentIndex, MessageBlock FinalBlock) : SessionEvent;

    /// <summary>工具执行状态变化（状态徽标与状态文本）。</summary>
    public sealed record ToolCallStatusChanged(string MessageId, int ContentIndex, string ToolCallId, ToolCallStatus Status, string? StatusText) : SessionEvent;

    /// <summary>消息结束：整条消息的最终依据，整体替换临时消息；携带累计用量快照。</summary>
    public sealed record MessageFinished(string MessageId, ChatMessage FinalMessage, UsageInfo? CumulativeUsage) : SessionEvent;

    /// <summary>一轮自动工作结束（agent_settled 语义：不再自动继续，最终成败须结合消息与错误事件判定）。</summary>
    public sealed record TurnSettled(SessionOutcome Outcome, string? Summary) : SessionEvent;

    /// <summary>会话级错误，对应时间线中的错误条目。</summary>
    public sealed record SessionError(SessionErrorKind Kind, string Message, string? RequestId) : SessionEvent;

    /// <summary>活动状态变化。</summary>
    public sealed record ActivityChanged(SessionActivity Activity) : SessionEvent;

    /// <summary>连接状态变化。</summary>
    public sealed record ConnectionChanged(SessionConnection Connection) : SessionEvent;

    /// <summary>队列变化。QueuedTexts 为当前排队文本的有序只读快照（队列接收成功不等于已执行）。</summary>
    public sealed record QueueChanged(int QueuedCount, IReadOnlyList<string> QueuedTexts) : SessionEvent;

    /// <summary>停止时被清空的队列内容。订阅者将其保存为待恢复草稿文本，不得覆盖用户已有草稿。</summary>
    public sealed record QueueCleared(IReadOnlyList<string> ClearedTexts) : SessionEvent;
}
