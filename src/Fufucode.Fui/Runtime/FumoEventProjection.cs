using System.Text;
using System.Threading.Channels;
using Fufucode.App.Abstractions;
using Fufucode.Core;
using Fufucode.Core.Backend;

namespace Fufucode.Fui;

/// <summary>Projects block deltas using stable row ids. History is read once, never on a token callback.</summary>
internal sealed class FumoEventProjection(ChannelWriter<TuiEvent> writer)
{
    private sealed class Draft(MessageRole role, int imageCount)
    {
        public readonly string TextKind = role == MessageRole.User ? "user" : "assistant";
        public readonly int ImageCount = imageCount;
        public readonly Dictionary<int, ToolDraft> Tools = [];
    }

    private sealed record ToolDraft(string Name, StringBuilder Arguments);
    private readonly Dictionary<string, Draft> _messages = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private string? _lastError;
    private int _nextUserImageCount;

    public void ExpectUserImages(int count)
    {
        lock (_gate) _nextUserImageCount = count;
    }

    public static IReadOnlyList<TuiLine> ReadHistory(PersistentSession session, SessionHistory? history = null)
    {
        var lines = new List<TuiLine>();
        // The Pi-shaped entries omit native image references. Join by the persisted native entry identity,
        // without reading the asset, following a path from a transcript, or copying its base64 into display text.
        var imageCounts = (session.Chat as NativeCSharpChatSession)?.GetHistoryMessages()
            .Where(m => m.Role == PureAgentRole.User && m.Images is { Count: > 0 })
            .ToLookup(m => "native_" + m.EntryId, m => m.Images!.Count);
        foreach (var entry in (history ?? session.History).Entries)
        {
            if (!entry.OnCurrentBranch || entry.Message is not { } message) continue;
            var kind = message.Role switch { "user" => "user", "assistant" => "assistant", "toolResult" => "tool", _ => "info" };
            var imageCount = imageCounts?[entry.Id].FirstOrDefault() ?? 0;
            foreach (var block in message.Content)
                switch (block)
                {
                    case PiTextBlock text: lines.Add(new(kind, WithImages(text.Text, imageCount))); imageCount = 0; break;
                    case PiThinkingBlock thinking: lines.Add(new("thinking", thinking.Thinking)); break;
                    case PiToolCallBlock tool: lines.Add(new("tool", $"{tool.Name} {tool.Arguments?.GetRawText()}".TrimEnd())); break;
                }
            if (imageCount > 0) lines.Add(new(kind, WithImages("", imageCount)));
        }
        return lines;
    }

    public void OnEvent(object? sender, SessionEvent e)
    {
        lock (_gate)
        {
            switch (e)
            {
                case SessionEvent.MessageStarted started:
                    _messages[started.MessageId] = new(started.Role, started.Role == MessageRole.User ? _nextUserImageCount : 0);
                    if (started.Role == MessageRole.User) _nextUserImageCount = 0;
                    break;
                case SessionEvent.BlockStarted { Kind: MessageBlockKind.ToolCall } start when _messages.TryGetValue(start.MessageId, out var message):
                    var name = string.IsNullOrEmpty(start.ToolName) ? start.ToolCallId ?? "工具" : start.ToolName;
                    message.Tools[start.ContentIndex] = new(name, new());
                    Row(start.MessageId, start.ContentIndex, "tool", name + " ");
                    break;
                case SessionEvent.TextAppended text when _messages.TryGetValue(text.MessageId, out var message):
                    Row(text.MessageId, text.ContentIndex, message.TextKind, text.Delta, append: true);
                    break;
                case SessionEvent.ThinkingAppended thinking:
                    Row(thinking.MessageId, thinking.ContentIndex, "thinking", thinking.Delta, append: true);
                    break;
                case SessionEvent.ToolCallAppended args when _messages.TryGetValue(args.MessageId, out var message) &&
                    message.Tools.TryGetValue(args.ContentIndex, out var tool):
                    tool.Arguments.Append(args.JsonFragment);
                    Row(args.MessageId, args.ContentIndex, "tool", args.JsonFragment, append: true);
                    break;
                case SessionEvent.ToolCallStatusChanged status when _messages.TryGetValue(status.MessageId, out var message) &&
                    message.Tools.TryGetValue(status.ContentIndex, out var tool):
                    Row(status.MessageId, status.ContentIndex, "tool", ToolText(tool.Name, tool.Arguments.ToString(), status.Status, status.StatusText));
                    break;
                case SessionEvent.MessageFinished final:
                    // Native BlockFinished immediately precedes this authoritative snapshot. Replace, don't append.
                    var role = final.FinalMessage.Role == MessageRole.User ? "user" : "assistant";
                    var count = _messages.GetValueOrDefault(final.MessageId)?.ImageCount ?? 0;
                    for (var i = 0; i < final.FinalMessage.Blocks.Count; i++)
                        switch (final.FinalMessage.Blocks[i])
                        {
                            case TextBlock t: Row(final.MessageId, i, role, WithImages(t.Text, count)); count = 0; break;
                            case ThinkingBlock t: Row(final.MessageId, i, "thinking", t.Text); break;
                            case ToolCallBlock t:
                                Row(final.MessageId, i, "tool", ToolText(t.ToolName, t.InputJson, t.Status, t.Output ?? t.StatusText)); break;
                        }
                    _messages.Remove(final.MessageId);
                    break;
                case SessionEvent.ActivityChanged { Activity: not SessionActivity.Idle } activity:
                    if (activity.Activity == SessionActivity.Running) _lastError = null;
                    writer.TryWrite(new TuiEvent.Status(activity.Activity switch
                    {
                        SessionActivity.Cancelling => "正在停止", SessionActivity.Compacting => "正在压缩上下文",
                        SessionActivity.Retrying => "正在重试", _ => "运行中"
                    }, true));
                    break;
                case SessionEvent.SessionError error:
                    _lastError = error.Message;
                    Notice("error", error.Message);
                    break;
                case SessionEvent.TurnSettled settled:
                    if (settled.Outcome == SessionOutcome.Failed && settled.Summary is { Length: > 0 } reason && reason != _lastError)
                        Notice("error", reason);
                    var queued = sender is IChatSession chat ? chat.QueuedCount : 0;
                    writer.TryWrite(new TuiEvent.Status(queued > 0 ? $"运行中 · 排队 {queued}" : settled.Outcome switch
                    {
                        SessionOutcome.Completed => "已完成", SessionOutcome.Failed => "失败", SessionOutcome.Interrupted => "已中断",
                        SessionOutcome.Truncated => settled.Summary ?? "结果不完整", _ => settled.Summary ?? "结果未知"
                    }, queued > 0));
                    break;
                case SessionEvent.QueueChanged { QueuedCount: > 0 } queue:
                    writer.TryWrite(new TuiEvent.Status($"运行中 · 排队 {queue.QueuedCount}", true));
                    break;
                case SessionEvent.QueueCleared queue:
                    foreach (var text in queue.ClearedTexts) Notice("info", "已取消排队：" + text);
                    break;
            }
        }
    }

    private void Row(string message, int index, string kind, string text, bool append = false) =>
        writer.TryWrite(new TuiEvent.Message($"{message}:{index}", kind, text, append));

    private void Notice(string kind, string text) => writer.TryWrite(new TuiEvent.Message(Guid.NewGuid().ToString("N"), kind, text));

    private static string WithImages(string text, int count) =>
        count == 0 ? text : (text.Length == 0 ? "" : text + "\n") + $"[图片 × {count}]";

    private static string ToolText(string name, string args, ToolCallStatus state, string? output) =>
        $"{name} [{state switch { ToolCallStatus.Running => "执行中", ToolCallStatus.Completed => "完成", ToolCallStatus.Failed => "失败", ToolCallStatus.Cancelled => "已取消", _ => "待执行" }}] {args}".TrimEnd() +
        (string.IsNullOrEmpty(output) ? "" : "\n" + output);
}
