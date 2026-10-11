using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Fufucode.App.Abstractions;

namespace Fufucode.App.Streaming;

/// <summary>
/// 消息重建器：把 <see cref="SessionEvent"/> 事件流按计划 §6.6 重建为有序时间线。
/// 规则：临时消息在 <see cref="SessionEvent.MessageStarted"/> 建立；文本/思考/工具调用按
/// contentIndex 分块维护；delta 只追加到对应块；<see cref="SessionEvent.BlockFinished"/>
/// 替换临时块；<see cref="SessionEvent.MessageFinished"/> 是整条消息的最终依据（整体替换）；
/// 顶层 usage 是累计快照（覆盖不累加）。
/// 本类不依赖 UI 线程，也不自行通知；调用方负责按节拍调用 <see cref="Flush"/> 合并刷新。
/// </summary>
public sealed class MessageRebuilder
{
    private readonly List<TimelineEntry> _entries = new();
    private readonly Dictionary<string, MessageBuilder> _messages = new();
    private readonly Dictionary<string, int> _entryIndexById = new();

    private readonly List<TimelineEntry> _pendingAdded = new();
    private readonly HashSet<string> _pendingUpdated = new();

    public IReadOnlyList<TimelineEntry> Timeline => _entries;

    /// <summary>按条目 Id 取最新快照（不存在返回 null）。</summary>
    public TimelineEntry? GetEntry(string id)
    {
        return _entryIndexById.TryGetValue(id, out var index) && index < _entries.Count
            ? _entries[index]
            : null;
    }

    /// <summary>应用一个事件，更新内部状态（不产生通知）。</summary>
    public void Apply(SessionEvent e)
    {
        switch (e)
        {
            case SessionEvent.MessageStarted started:
                ApplyMessageStarted(started);
                break;
            case SessionEvent.BlockStarted block:
                EnsureBlock(block.MessageId, block.ContentIndex, block.Kind, block.ToolCallId, block.ToolName);
                break;
            case SessionEvent.TextAppended text:
                AppendToBlock(text.MessageId, text.ContentIndex, text.Delta);
                break;
            case SessionEvent.ThinkingAppended thinking:
                AppendToBlock(thinking.MessageId, thinking.ContentIndex, thinking.Delta);
                break;
            case SessionEvent.ToolCallAppended tool:
                AppendToBlock(tool.MessageId, tool.ContentIndex, tool.JsonFragment);
                break;
            case SessionEvent.ToolCallStatusChanged status:
                ApplyToolStatus(status);
                break;
            case SessionEvent.BlockFinished finished:
                ApplyBlockFinished(finished);
                break;
            case SessionEvent.MessageFinished finished:
                ApplyMessageFinished(finished);
                break;
            case SessionEvent.SessionError error:
                ApplySessionError(error);
                break;
            // 状态/队列事件不影响时间线内容，由上层分别消费
        }
    }

    /// <summary>提交累积的变更并返回本次批次，随后清空脏标记。</summary>
    public TimelineBatch Flush()
    {
        var batch = new TimelineBatch(_pendingAdded.ToArray(), _pendingUpdated.ToArray());
        _pendingAdded.Clear();
        _pendingUpdated.Clear();
        return batch;
    }

    /// <summary>清空全部状态（新建会话时使用）。</summary>
    public void Reset()
    {
        _entries.Clear();
        _messages.Clear();
        _entryIndexById.Clear();
        _pendingAdded.Clear();
        _pendingUpdated.Clear();
    }

    private void ApplyMessageStarted(SessionEvent.MessageStarted e)
    {
        if (_messages.ContainsKey(e.MessageId))
        {
            return;
        }

        var builder = new MessageBuilder(e.MessageId, e.Role, e.Model);
        _messages.Add(e.MessageId, builder);

        var entry = new TimelineEntry
        {
            Id = e.MessageId,
            Kind = e.Role == MessageRole.User ? TimelineEntryKind.UserMessage : TimelineEntryKind.AssistantMessage,
            Timestamp = DateTimeOffset.Now,
            Message = builder.ToSnapshot(),
        };
        _entryIndexById[e.MessageId] = _entries.Count;
        _entries.Add(entry);
        _pendingAdded.Add(entry);
    }

    private MessageBuilder GetMessage(string messageId)
    {
        if (!_messages.TryGetValue(messageId, out var builder))
        {
            // 防御性：事件先于 MessageStarted 到达时按助手临时消息兜底，不丢弃数据
            builder = new MessageBuilder(messageId, MessageRole.Assistant, null);
            _messages.Add(messageId, builder);
        }
        return builder;
    }

    private BlockBuilder EnsureBlock(string messageId, int contentIndex, MessageBlockKind kind, string? toolCallId, string? toolName)
    {
        var message = GetMessage(messageId);
        if (!message.Blocks.TryGetValue(contentIndex, out var block))
        {
            block = new BlockBuilder(contentIndex, kind, toolCallId, toolName);
            message.Blocks[contentIndex] = block;
        }
        MarkUpdated(messageId);
        return block;
    }

    private void AppendToBlock(string messageId, int contentIndex, string delta)
    {
        var message = GetMessage(messageId);
        if (!message.Blocks.TryGetValue(contentIndex, out var block))
        {
            // 未知块种类时无法安全归类，记为文本块（最保守的展示方式）
            block = new BlockBuilder(contentIndex, MessageBlockKind.Text, null, null);
            message.Blocks[contentIndex] = block;
        }
        block.AppendText(delta);
        MarkUpdated(messageId);
    }

    private void ApplyToolStatus(SessionEvent.ToolCallStatusChanged e)
    {
        var message = GetMessage(e.MessageId);
        if (message.Blocks.TryGetValue(e.ContentIndex, out var block) && block.Kind == MessageBlockKind.ToolCall)
        {
            block.Status = e.Status;
            block.StatusText = e.StatusText;
            MarkUpdated(e.MessageId);
        }
    }

    private void ApplyBlockFinished(SessionEvent.BlockFinished e)
    {
        var message = GetMessage(e.MessageId);
        var tool = e.FinalBlock as ToolCallBlock;
        var block = EnsureBlock(e.MessageId, e.ContentIndex, e.FinalBlock.Kind,
            tool?.ToolCallId, tool?.ToolName);
        block.ApplyFinal(e.FinalBlock);
        MarkUpdated(e.MessageId);
    }

    private void ApplyMessageFinished(SessionEvent.MessageFinished e)
    {
        // 整体替换：以终态消息为准重建分块，不沿用任何临时累积，避免重复追加
        if (!_messages.TryGetValue(e.MessageId, out var message))
        {
            message = new MessageBuilder(e.MessageId, e.FinalMessage.Role, e.FinalMessage.Model);
            _messages.Add(e.MessageId, message);
        }

        message.ApplyFinal(e.FinalMessage);
        message.Usage = e.CumulativeUsage ?? message.Usage;
        MarkUpdated(e.MessageId);
    }

    private void ApplySessionError(SessionEvent.SessionError e)
    {
        var entry = new TimelineEntry
        {
            Id = "err-" + Guid.NewGuid().ToString("N"),
            Kind = TimelineEntryKind.Error,
            Timestamp = DateTimeOffset.Now,
            Error = new ErrorEntry(e.Kind, e.Message, e.RequestId),
        };
        _entries.Add(entry);
        _pendingAdded.Add(entry);
    }

    private void MarkUpdated(string messageId)
    {
        if (!_entryIndexById.TryGetValue(messageId, out var index) || index >= _entries.Count)
        {
            return;
        }

        var existing = _entries[index];
        if (existing.Message is null || !_messages.TryGetValue(messageId, out var builder))
        {
            return;
        }

        // 整体替换展示快照：终态消息与流式累积都以最新快照为准
        _entries[index] = existing with { Message = builder.ToSnapshot() };
        _pendingUpdated.Add(messageId);
    }

    private sealed class MessageBuilder
    {
        public MessageBuilder(string id, MessageRole role, string? model)
        {
            Id = id;
            Role = role;
            Model = model;
        }

        public string Id { get; }
        public MessageRole Role { get; set; }
        public string? Model { get; set; }
        public Dictionary<int, BlockBuilder> Blocks { get; } = new();
        public UsageInfo? Usage { get; set; }
        public bool IsComplete { get; set; }

        public ChatMessage ToSnapshot()
        {
            var indices = new List<int>(Blocks.Keys);
            indices.Sort();
            var blocks = new List<MessageBlock>(indices.Count);
            foreach (var index in indices)
            {
                blocks.Add(Blocks[index].ToBlock());
            }
            return new ChatMessage(Id, Role, Model, blocks, Usage, IsComplete);
        }

        /// <summary>整条消息整体替换：以 message_end 的终态消息为准，丢弃全部临时累积。</summary>
        public void ApplyFinal(ChatMessage final)
        {
            Role = final.Role;
            Model = final.Model ?? Model;
            IsComplete = true;
            Blocks.Clear();
            for (var index = 0; index < final.Blocks.Count; index++)
            {
                var block = final.Blocks[index];
                var tool = block as ToolCallBlock;
                var builder = new BlockBuilder(index, block.Kind, tool?.ToolCallId, tool?.ToolName);
                builder.ApplyFinal(block);
                Blocks[index] = builder;
            }
        }
    }

    private sealed class BlockBuilder
    {
        public BlockBuilder(int contentIndex, MessageBlockKind kind, string? toolCallId, string? toolName)
        {
            ContentIndex = contentIndex;
            Kind = kind;
            ToolCallId = toolCallId;
            ToolName = toolName;
        }

        public int ContentIndex { get; }
        public MessageBlockKind Kind { get; }
        public string? ToolCallId { get; }
        public string? ToolName { get; }
        public StringBuilder Buffer { get; } = new();
        public ToolCallStatus Status { get; set; } = ToolCallStatus.Pending;
        public string? StatusText { get; set; }
        public string? Output { get; set; }
        public bool IsErrorOutput { get; set; }

        public void AppendText(string delta) => Buffer.Append(delta);

        public void ApplyFinal(MessageBlock final)
        {
            Buffer.Clear();
            switch (final)
            {
                case TextBlock text:
                    Buffer.Append(text.Text);
                    break;
                case ThinkingBlock thinking:
                    Buffer.Append(thinking.Text);
                    break;
                case ToolCallBlock tool:
                    Buffer.Append(tool.InputJson);
                    Status = tool.Status;
                    StatusText = tool.StatusText;
                    Output = tool.Output;
                    IsErrorOutput = tool.IsErrorOutput;
                    break;
            }
        }

        public MessageBlock ToBlock()
        {
            var text = Buffer.ToString();
            return Kind switch
            {
                MessageBlockKind.Thinking => (MessageBlock)new ThinkingBlock(text),
                MessageBlockKind.ToolCall => new ToolCallBlock(
                    ToolCallId ?? string.Empty,
                    ToolName ?? string.Empty,
                    text,
                    Status,
                    StatusText,
                    Output,
                    IsErrorOutput),
                _ => new TextBlock(text),
            };
        }
    }
}
