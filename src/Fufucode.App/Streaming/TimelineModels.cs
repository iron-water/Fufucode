using System;
using System.Collections.Generic;
using Fufucode.App.Abstractions;

namespace Fufucode.App.Streaming;

/// <summary>时间线条目种类。</summary>
public enum TimelineEntryKind
{
    UserMessage,
    AssistantMessage,
    Error,
}

/// <summary>时间线中的错误条目内容。</summary>
public sealed record ErrorEntry(SessionErrorKind Kind, string Message, string? RequestId);

/// <summary>
/// 时间线条目（不可变）。同一条消息在流式期间会被多次替换：消费者按 <see cref="Id"/>
/// 定位条目并用新快照整体替换展示状态（计划 §6.6：最终消息整体替换，不重复追加）。
/// </summary>
public sealed record TimelineEntry
{
    public required string Id { get; init; }
    public required TimelineEntryKind Kind { get; init; }
    public DateTimeOffset Timestamp { get; init; }

    /// <summary>用户/助手条目携带的消息快照。</summary>
    public ChatMessage? Message { get; init; }

    /// <summary>错误条目携带的错误内容。</summary>
    public ErrorEntry? Error { get; init; }
}

/// <summary>一次合并刷新提交的增量。</summary>
public sealed record TimelineBatch(IReadOnlyList<TimelineEntry> AddedEntries, IReadOnlyList<string> UpdatedEntryIds);
