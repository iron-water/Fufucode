using System.Collections.Generic;

namespace Fufucode.App.Abstractions;

/// <summary>
/// 用量快照。顶层流式 usage 是累计值（计划 §6.6），消费者直接覆盖，不得逐条相加；
/// 压缩后未知用量为 null，不得显示为零消耗。
/// </summary>
public sealed record UsageInfo
{
    public int? InputTokens { get; init; }
    public int? OutputTokens { get; init; }
    public int? CacheReadTokens { get; init; }
    public int? CacheWriteTokens { get; init; }

    /// <summary>费用，单位由实现约定；无法获知时为 null。</summary>
    public decimal? TotalCost { get; init; }

    public bool IsUnknown => InputTokens is null && OutputTokens is null;
}

/// <summary>消息内容块（计划 §6.6：文本/思考/工具调用分块维护， delta 只追加到对应块）。</summary>
public abstract record MessageBlock
{
    protected MessageBlock(MessageBlockKind kind) => Kind = kind;

    public MessageBlockKind Kind { get; }
}

/// <summary>普通文本块。</summary>
public sealed record TextBlock(string Text) : MessageBlock(MessageBlockKind.Text);

/// <summary>思考块。只展示 Pi 实际返回的思考内容，没有数据时不伪造占位推理文本。</summary>
public sealed record ThinkingBlock(string Text) : MessageBlock(MessageBlockKind.Thinking);

/// <summary>
/// 工具调用块。<paramref name="InputJson"/> 为最终解析出的完整参数 JSON；
/// 流式期间尚未组装完成的分片由 <see cref="SessionEvent.ToolCallAppended"/> 携带，
/// 展示为临时文本，不得当成可执行参数。
/// </summary>
public sealed record ToolCallBlock(
    string ToolCallId,
    string ToolName,
    string InputJson,
    ToolCallStatus Status,
    string? StatusText,
    string? Output,
    bool IsErrorOutput,
    /// <summary>后端报告的工具耗时（毫秒）；未知时为 null。</summary>
    long? DurationMs = null) : MessageBlock(MessageBlockKind.ToolCall);

/// <summary>
/// 一条消息的快照。<see cref="Blocks"/> 按 contentIndex 有序；
/// <see cref="IsComplete"/> 为 true 表示终态（ message_end 整体替换之后）。
/// </summary>
public sealed record ChatMessage(
    string Id,
    MessageRole Role,
    string? Model,
    IReadOnlyList<MessageBlock> Blocks,
    UsageInfo? CumulativeUsage,
    bool IsComplete);
