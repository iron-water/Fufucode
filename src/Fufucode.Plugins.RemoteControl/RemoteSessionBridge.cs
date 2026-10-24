using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Fufucode.App.Abstractions;
using Fufucode.Core.RemoteControl;

namespace Fufucode.App.Features.RemoteControl;

/// <summary>
/// 把真实 <see cref="IChatSession"/> 投影为手机远控传输模型：
/// 复用桌面同一套消息重建语义（消息整体替换、块按 contentIndex 增量、工具状态变化），
/// 只在投影层做「等待审批」的补充标记。本类不依赖 UI 框架，可在任意线程引发事件。
/// </summary>
public sealed class RemoteSessionAdapter : IRemoteSessionBridge, IDisposable
{
    private IChatSession _session;
    private readonly object _gate = new();
    private readonly Dictionary<string, EntryBuilder> _entries = new(StringComparer.Ordinal);
    private readonly List<string> _order = new();
    private string _title;
    private readonly DelegatingToolApprovalGate _approvals;
    private string _activity = nameof(SessionActivity.Idle);
    private string _connection = nameof(SessionConnection.Stopped);
    private string? _model;
    private bool _canSend;
    private bool _canStop;
    private int _queuedCount;
    private bool _disposed;

    public RemoteSessionAdapter(IChatSession session, string title, IRemoteToolApprovalGate? approvals = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        _title = title;
        _approvals = new DelegatingToolApprovalGate(approvals ?? (session is Fufucode.Core.IExtensionUiSession ui
            ? (IRemoteToolApprovalGate)new SessionApprovalGate(ui) : new InMemoryToolApprovalGate()));
        Approvals = _approvals;
        _session.Event += OnSessionEvent;
    }

    public IRemoteToolApprovalGate Approvals { get; }

    public event EventHandler<RemoteUpdate>? Updated;

    public void LoadHistory(IReadOnlyList<ChatMessage> messages)
    {
        lock (_gate)
        {
            if (_disposed) return;
            var historyIds = new List<string>();
            foreach (var message in messages)
            {
                if (!_entries.ContainsKey(message.Id))
                {
                    var entry = new EntryBuilder(message.Id, MapRole(message.Role), message.Model);
                    entry.ApplyFinalMessage(message);
                    _entries.Add(message.Id, entry);
                    historyIds.Add(message.Id);
                }
            }
            _order.InsertRange(0, historyIds);
        }
        Raise(new RemoteSnapshotUpdate(Snapshot()));
    }

    /// <summary>
    /// 切换底层会话但保持远控服务、配对令牌与审批订阅不变。调用方随后仍可调用
    /// <see cref="LoadHistory"/> 补齐当前会话历史；旧会话的条目与审批不会泄漏到新快照。
    /// </summary>
    public void Rebind(IChatSession session, string title, IRemoteToolApprovalGate? approvals = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        lock (_gate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(RemoteSessionAdapter));
            _session.Event -= OnSessionEvent;
            _session = session;
            _title = title;
            _entries.Clear();
            _order.Clear();
            _activity = nameof(SessionActivity.Idle);
            _connection = nameof(SessionConnection.Stopped);
            _model = null;
            _canSend = false;
            _canStop = false;
            _queuedCount = 0;
            var nextApprovals = approvals ?? (session is Fufucode.Core.IExtensionUiSession ui
                ? (IRemoteToolApprovalGate)new SessionApprovalGate(ui) : new InMemoryToolApprovalGate());
            _approvals.Replace(nextApprovals);
            _session.Event += OnSessionEvent;
            RefreshStatusLocked();
        }

        Raise(new RemoteSnapshotUpdate(Snapshot()));
    }

    public RemoteSessionSnapshot Snapshot()
    {
        lock (_gate)
        {
            RefreshStatusLocked();
            return new RemoteSessionSnapshot(
                _session.GetHashCode().ToString("x8"),
                _title,
                _activity,
                _connection,
                _model,
                _canSend,
                _canStop,
                _queuedCount,
                _order.Select(id => _entries[id].ToEntry()).ToArray(),
                Approvals.Pending.ToArray());
        }
    }

    public async Task<RemoteCommandResult> SendAsync(string text, CancellationToken cancellationToken = default)
    {
        if (_disposed) return new RemoteCommandResult(false, "会话已解绑，请重新配对。");
        if (string.IsNullOrWhiteSpace(text)) return new RemoteCommandResult(false, "消息文本为空。");
        lock (_gate) RefreshStatusLocked();
        if (!_session.CanSend) return new RemoteCommandResult(false, "当前会话不可发送（可能在停止中或未连接）。");
        try
        {
            await _session.SendAsync(text, cancellationToken).ConfigureAwait(false);
            return new RemoteCommandResult(true, null);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return new RemoteCommandResult(false, error.Message);
        }
    }

    public async Task<RemoteCommandResult> StopAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed) return new RemoteCommandResult(false, "会话已解绑，请重新配对。");
        lock (_gate) RefreshStatusLocked();
        if (!_session.CanStop) return new RemoteCommandResult(false, "当前没有可停止的在途任务。");
        try
        {
            await _session.StopAsync(cancellationToken).ConfigureAwait(false);
            return new RemoteCommandResult(true, null);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return new RemoteCommandResult(false, error.Message);
        }
    }

    /// <summary>
    /// 登记一个待审批工具调用：把对应工具块的手机端状态置为「等待审批」并推送。
    /// 审批结果由手机端经 <see cref="Approvals"/> 回传，宿主据此决定是否放行工具执行。
    /// </summary>
    public RemoteApprovalRequest RequestApproval(string toolCallId, string toolName, string argsJson,
        string? summary = null)
    {
        var request = Approvals.Request(toolCallId, toolName, argsJson, summary);
        MarkAwaitingApproval(toolCallId);
        return request;
    }

    private void MarkAwaitingApproval(string toolCallId)
    {
        RemoteTimelineEntry? updated = null;
        lock (_gate)
        {
            foreach (var id in _order)
            {
                var builder = _entries[id];
                if (!builder.MarkToolAwaitingApproval(toolCallId)) continue;
                updated = builder.ToEntry();
                break;
            }
        }

        if (updated is not null) Raise(new RemoteEntryUpdate(updated));
    }

    private void OnSessionEvent(object? sender, SessionEvent e)
    {
        var updates = new List<RemoteUpdate>();
        lock (_gate)
        {
            // Rebind 后旧会话可能仍有排队事件；不得把它们写入新会话时间线。
            if (!ReferenceEquals(sender, _session)) return;
            switch (e)
            {
                case SessionEvent.MessageStarted started:
                    {
                        var builder = new EntryBuilder(started.MessageId,
                            started.Role == MessageRole.User ? RemoteEntryRole.User : RemoteEntryRole.Assistant,
                            started.Model);
                        if (!_entries.ContainsKey(started.MessageId))
                        {
                            _entries[started.MessageId] = builder;
                            _order.Add(started.MessageId);
                        }
                        else
                        {
                            _entries[started.MessageId] = builder;
                        }

                        updates.Add(new RemoteEntryUpdate(builder.ToEntry()));
                        break;
                    }
                case SessionEvent.BlockStarted block:
                    ApplyToEntry(block.MessageId, builder =>
                    {
                        builder.EnsureBlock(block.ContentIndex, block.Kind, block.ToolCallId, block.ToolName);
                        updates.Add(new RemoteEntryUpdate(builder.ToEntry()));
                    });
                    break;
                case SessionEvent.TextAppended text:
                    ApplyToEntry(text.MessageId, builder =>
                    {
                        builder.AppendText(text.ContentIndex, text.Delta, thinking: false);
                        updates.Add(new RemoteEntryUpdate(builder.ToEntry()));
                    });
                    break;
                case SessionEvent.ThinkingAppended thinking:
                    ApplyToEntry(thinking.MessageId, builder =>
                    {
                        builder.AppendText(thinking.ContentIndex, thinking.Delta, thinking: true);
                        updates.Add(new RemoteEntryUpdate(builder.ToEntry()));
                    });
                    break;
                case SessionEvent.ToolCallAppended tool:
                    ApplyToEntry(tool.MessageId, builder =>
                    {
                        builder.AppendToolArgs(tool.ContentIndex, tool.ToolCallId, tool.JsonFragment);
                        updates.Add(new RemoteEntryUpdate(builder.ToEntry()));
                    });
                    break;
                case SessionEvent.ToolCallStatusChanged status:
                    ApplyToEntry(status.MessageId, builder =>
                    {
                        builder.ApplyToolStatus(status.ContentIndex, status.Status, status.StatusText);
                        updates.Add(new RemoteEntryUpdate(builder.ToEntry()));
                    });
                    break;
                case SessionEvent.BlockFinished finished:
                    ApplyToEntry(finished.MessageId, builder =>
                    {
                        builder.ApplyFinalBlock(finished.ContentIndex, finished.FinalBlock);
                        updates.Add(new RemoteEntryUpdate(builder.ToEntry()));
                    });
                    break;
                case SessionEvent.MessageFinished finished:
                    {
                        if (!_entries.TryGetValue(finished.MessageId, out var builder))
                        {
                            builder = new EntryBuilder(finished.MessageId, MapRole(finished.FinalMessage.Role),
                                finished.FinalMessage.Model);
                            _entries[finished.MessageId] = builder;
                            _order.Add(finished.MessageId);
                        }

                        builder.ApplyFinalMessage(finished.FinalMessage);
                        updates.Add(new RemoteEntryUpdate(builder.ToEntry()));
                        break;
                    }
                case SessionEvent.SessionError error:
                    {
                        var id = "err-" + Guid.NewGuid().ToString("N")[..8];
                        _entries[id] = EntryBuilder.FromError(id, error.Message);
                        _order.Add(id);
                        updates.Add(new RemoteEntryUpdate(_entries[id].ToEntry()));
                        break;
                    }
                case SessionEvent.ActivityChanged activity:
                    _activity = activity.Activity.ToString();
                    RefreshStatusLocked();
                    updates.Add(StatusUpdateLocked());
                    break;
                case SessionEvent.ConnectionChanged connection:
                    _connection = connection.Connection.ToString();
                    RefreshStatusLocked();
                    updates.Add(StatusUpdateLocked());
                    break;
                case SessionEvent.QueueChanged queue:
                    _queuedCount = queue.QueuedCount;
                    RefreshStatusLocked();
                    updates.Add(StatusUpdateLocked());
                    break;
                case SessionEvent.QueueCleared cleared:
                    if (cleared.ClearedTexts.Count > 0)
                        updates.Add(new RemoteNoticeUpdate($"停止清空了 {cleared.ClearedTexts.Count} 条排队消息。"));
                    break;
                case SessionEvent.TurnSettled settled:
                    updates.Add(new RemoteNoticeUpdate(settled.Outcome switch
                    {
                        SessionOutcome.Completed => "本轮任务已完成。",
                        SessionOutcome.Failed => "本轮任务失败" + (settled.Summary is null ? "。" : "：" + settled.Summary),
                        SessionOutcome.Interrupted => "本轮任务已被停止。",
                        _ => "本轮任务已结束（结果未知）。",
                    }));
                    break;
            }
        }

        foreach (var update in updates) Raise(update);
    }

    private void ApplyToEntry(string messageId, Action<EntryBuilder> mutate)
    {
        if (!_entries.TryGetValue(messageId, out var builder))
        {
            // 事件先于 MessageStarted 到达时建立兜底条目，不丢弃远端数据。
            builder = new EntryBuilder(messageId, RemoteEntryRole.Assistant, null);
            _entries[messageId] = builder;
            _order.Add(messageId);
        }

        mutate(builder);
    }

    private void RefreshStatusLocked()
    {
        _activity = _session.Activity.ToString();
        _connection = _session.Connection.ToString();
        _model = _session.CurrentModel;
        _queuedCount = _session.QueuedCount;
        _canSend = !_disposed && _session.CanSend;
        _canStop = !_disposed && _session.CanStop;
    }

    private RemoteStatusUpdate StatusUpdateLocked() =>
        new(_activity, _connection, _canSend, _canStop, _queuedCount);

    private void Raise(RemoteUpdate update) => Updated?.Invoke(this, update);

    private static RemoteEntryRole MapRole(MessageRole role) =>
        role == MessageRole.User ? RemoteEntryRole.User : RemoteEntryRole.Assistant;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _session.Event -= OnSessionEvent;
        _approvals.Dispose();
    }

    /// <summary>一条消息的增量投影（与桌面消息重建同语义）。</summary>
    private sealed class EntryBuilder
    {
        private readonly Dictionary<int, BlockBuilder> _blocks = new();
        private RemoteEntryRole _role;
        private string? _model;
        private bool _streaming = true;
        private string? _error;

        public EntryBuilder(string id, RemoteEntryRole role, string? model)
        {
            Id = id;
            _role = role;
            _model = model;
        }

        public string Id { get; }

        public static EntryBuilder FromError(string id, string message) =>
            new(id, RemoteEntryRole.Assistant, null) { _error = message, _streaming = false };

        public void EnsureBlock(int contentIndex, MessageBlockKind kind, string? toolCallId, string? toolName)
        {
            if (!_blocks.TryGetValue(contentIndex, out var block))
            {
                block = new BlockBuilder(kind, toolCallId, toolName);
                _blocks[contentIndex] = block;
            }
        }

        public void AppendText(int contentIndex, string delta, bool thinking)
        {
            var kind = thinking ? MessageBlockKind.Thinking : MessageBlockKind.Text;
            if (!_blocks.TryGetValue(contentIndex, out var block))
            {
                block = new BlockBuilder(kind, null, null);
                _blocks[contentIndex] = block;
            }

            block.Buffer.Append(delta);
        }

        public void AppendToolArgs(int contentIndex, string toolCallId, string fragment)
        {
            if (!_blocks.TryGetValue(contentIndex, out var block))
            {
                block = new BlockBuilder(MessageBlockKind.ToolCall, toolCallId, string.Empty);
                _blocks[contentIndex] = block;
            }

            block.ToolCallId = toolCallId;
            block.Buffer.Append(fragment);
        }

        public void ApplyToolStatus(int contentIndex, ToolCallStatus status, string? statusText)
        {
            if (!_blocks.TryGetValue(contentIndex, out var block) || block.Kind != MessageBlockKind.ToolCall) return;
            block.Status = MapStatus(status);
            block.StatusText = statusText;
        }

        public void ApplyFinalBlock(int contentIndex, MessageBlock block)
        {
            EnsureBlock(contentIndex, block.Kind, null, null);
            var target = _blocks[contentIndex];
            target.ApplyFinal(block);
        }

        public void ApplyFinalMessage(ChatMessage message)
        {
            _role = MapRole(message.Role);
            _model = message.Model ?? _model;
            _streaming = false;
            _blocks.Clear();
            for (var index = 0; index < message.Blocks.Count; index++)
            {
                var block = new BlockBuilder(message.Blocks[index].Kind, null, null);
                block.ApplyFinal(message.Blocks[index]);
                _blocks[index] = block;
            }
        }

        public bool MarkToolAwaitingApproval(string toolCallId)
        {
            foreach (var block in _blocks.Values)
            {
                if (block.Kind != MessageBlockKind.ToolCall) continue;
                if (!string.Equals(block.ToolCallId, toolCallId, StringComparison.Ordinal)) continue;
                block.Status = RemoteToolStatus.AwaitingApproval;
                block.StatusText = "等待手机端审批";
                return true;
            }

            return false;
        }

        public RemoteTimelineEntry ToEntry()
        {
            var indices = _blocks.Keys.ToList();
            indices.Sort();
            var blocks = indices.Select(index => _blocks[index].ToBlock()).ToArray();
            if (_error is not null) blocks = [new RemoteErrorBlock(_error)];
            return new RemoteTimelineEntry(Id, _role, _streaming, _model, blocks);
        }

        private static RemoteToolStatus MapStatus(ToolCallStatus status) => status switch
        {
            ToolCallStatus.Pending => RemoteToolStatus.Pending,
            ToolCallStatus.Running => RemoteToolStatus.Running,
            ToolCallStatus.Completed => RemoteToolStatus.Completed,
            ToolCallStatus.Failed => RemoteToolStatus.Failed,
            ToolCallStatus.Cancelled => RemoteToolStatus.Cancelled,
            _ => RemoteToolStatus.Pending,
        };

        private sealed class BlockBuilder(MessageBlockKind kind, string? toolCallId, string? toolName)
        {
            public MessageBlockKind Kind { get; } = kind;
            public StringBuilder Buffer { get; } = new();
            public string? ToolCallId { get; set; } = toolCallId;
            public string? ToolName { get; set; } = toolName;
            public RemoteToolStatus Status { get; set; } = RemoteToolStatus.Pending;
            public string? StatusText { get; set; }
            public string? Output { get; private set; }
            public bool IsErrorOutput { get; private set; }

            public void ApplyFinal(MessageBlock block)
            {
                Buffer.Clear();
                switch (block)
                {
                    case TextBlock text:
                        Buffer.Append(text.Text);
                        break;
                    case ThinkingBlock thinking:
                        Buffer.Append(thinking.Text);
                        break;
                    case ToolCallBlock tool:
                        // 工具身份必须来自终态块本身：message_end 整体替换时不沿用临时块，
                        // 否则最终卡片会丢失工具名与调用 ID。
                        ToolCallId = tool.ToolCallId;
                        ToolName = tool.ToolName;
                        Buffer.Append(tool.InputJson);
                        Status = MapStatus(tool.Status);
                        StatusText = tool.StatusText;
                        Output = tool.Output;
                        IsErrorOutput = tool.IsErrorOutput;
                        break;
                }
            }

            public RemoteBlock ToBlock()
            {
                var text = Buffer.ToString();
                return Kind switch
                {
                    MessageBlockKind.Thinking => new RemoteThinkingBlock(text),
                    MessageBlockKind.ToolCall => new RemoteToolBlock(new RemoteToolCall(
                        ToolCallId ?? string.Empty, ToolName ?? string.Empty, text, Status, StatusText, Output,
                        IsErrorOutput)),
                    _ => new RemoteTextBlock(text),
                };
            }
        }
    }
}
