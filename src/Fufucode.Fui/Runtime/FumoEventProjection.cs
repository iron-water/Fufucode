using System.Text;
using System.Threading.Channels;
using Fufucode.App.Abstractions;
using Fufucode.Core;
using Fufucode.Core.Backend;

namespace Fufucode.Fui;

/// <summary>Projects block deltas using stable row ids. History is read once, never on a token callback.</summary>
internal sealed class FumoEventProjection(ChannelWriter<TuiEvent> writer, int initialMessages = 0, int initialTools = 0,
    int initialThinking = 0)
{
    private sealed class Draft(MessageRole role, IReadOnlyList<TuiImage>? images)
    {
        public readonly string TextKind = role == MessageRole.User ? "user" : "assistant";
        public readonly IReadOnlyList<TuiImage>? Images = images;
        public readonly Dictionary<int, ToolDraft> Tools = [];
    }

    private sealed class ToolDraft(string name)
    {
        public string Name { get; set; } = name;
        public StringBuilder Arguments { get; } = new();
        public ToolCallStatus Status { get; set; } = ToolCallStatus.Pending;
        public string? StatusText { get; set; }
        public long? DurationMs { get; set; }
    }
    private readonly Dictionary<string, Draft> _messages = new(StringComparer.Ordinal);
    private readonly FuiAgentActivityTracker _agents = new();
    private readonly object _gate = new();
    private string? _lastError;
    private IReadOnlyList<TuiImage>? _nextUserImages;
    private int _messageCount = initialMessages;
    private int _toolCount = initialTools;
    private int _thinkingCount = initialThinking;
    private int _errorCount;

    public int MessageCount { get { lock (_gate) return _messageCount; } }
    public int ToolCount { get { lock (_gate) return _toolCount; } }
    public int ThinkingCount { get { lock (_gate) return _thinkingCount; } }
    public int ErrorCount { get { lock (_gate) return _errorCount; } }

    public void ExpectUserImages(IReadOnlyList<TuiImage>? images)
    {
        lock (_gate) _nextUserImages = images is { Count: > 0 } ? images : null;
    }

    public static IReadOnlyList<TuiLine> ReadHistory(PersistentSession session, SessionHistory? history = null) =>
        ReadHistoryWithSources(session, history).Lines;

    public static (IReadOnlyList<TuiLine> Lines, IReadOnlyList<string?> Sources) ReadHistoryWithSources(
        PersistentSession session, SessionHistory? history = null)
    {
        var lines = new List<TuiLine>();
        var sources = new List<string?>();
        void Add(TuiLine line, string source) { lines.Add(line); sources.Add(source); }
        // The Pi-shaped entries omit native image references. Join by the persisted native entry identity,
        // without eagerly reading assets, following paths outside this session's .assets folder, or copying base64 into text.
        var assetsRoot = string.IsNullOrWhiteSpace(session.Session.FilePath)
            ? null
            : Path.GetFullPath(session.Session.FilePath + ".assets");
        var imageGroups = (session.Chat as NativeCSharpChatSession)?.GetHistoryMessages()
            .Where(m => m.Role == PureAgentRole.User && m.Images is { Count: > 0 })
            .ToLookup(m => "native_" + m.EntryId, m => ToHistoryImages(m.Images!, assetsRoot));
        foreach (var entry in (history ?? session.History).Entries)
        {
            if (!entry.OnCurrentBranch || entry.Message is not { } message) continue;
            var kind = message.Role switch { "user" => "user", "assistant" => "assistant", "toolResult" => "tool", _ => "info" };
            var images = imageGroups?[entry.Id].FirstOrDefault();
            foreach (var block in message.Content)
                switch (block)
                {
                    case PiTextBlock text:
                        Add(new(kind, WithImages(text.Text, images?.Count ?? 0), Images: images), entry.Id);
                        images = null;
                        break;
                    case PiThinkingBlock thinking: Add(new("thinking", thinking.Thinking), entry.Id); break;
                    case PiToolCallBlock tool:
                    {
                        var arguments = tool.Arguments?.GetRawText() ?? "";
                        Add(new("tool", $"{tool.Name} {arguments}".TrimEnd(),
                            new ToolCallSnapshot(tool.Name, arguments)), entry.Id);
                        break;
                    }
                }
            if (images is { Count: > 0 })
                Add(new(kind, WithImages("", images.Count), Images: images), entry.Id);
        }
        return (lines, sources);
    }

    public void OnEvent(object? sender, SessionEvent e)
    {
        lock (_gate)
        {
            switch (e)
            {
                case SessionEvent.MessageStarted started:
                    _messages[started.MessageId] = new(started.Role,
                        started.Role == MessageRole.User ? _nextUserImages : null);
                    _messageCount++;
                    if (started.Role == MessageRole.User) _nextUserImages = null;
                    break;
                case SessionEvent.BlockStarted { Kind: MessageBlockKind.ToolCall } start when _messages.TryGetValue(start.MessageId, out var message):
                    var name = string.IsNullOrEmpty(start.ToolName) ? start.ToolCallId ?? "工具" : start.ToolName;
                    message.Tools[start.ContentIndex] = new(name);
                    _toolCount++;
                    Row(start.MessageId, start.ContentIndex, "tool", name + " ",
                        snapshot: Snapshot(message.Tools[start.ContentIndex]));
                    break;
                case SessionEvent.BlockStarted { Kind: MessageBlockKind.Thinking }:
                    _thinkingCount++;
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
                    Row(args.MessageId, args.ContentIndex, "tool", args.JsonFragment, append: true,
                        snapshot: Snapshot(tool));
                    break;
                case SessionEvent.ToolCallStatusChanged status when _messages.TryGetValue(status.MessageId, out var message) &&
                    message.Tools.TryGetValue(status.ContentIndex, out var tool):
                    tool.Status = status.Status;
                    tool.StatusText = status.StatusText;
                    tool.DurationMs = status.DurationMs;
                    Row(status.MessageId, status.ContentIndex, "tool",
                        ToolText(tool.Name, tool.Arguments.ToString(), status.Status, status.StatusText),
                        snapshot: Snapshot(tool));
                    break;
                case SessionEvent.BlockFinished { FinalBlock: ToolCallBlock tool } finished when
                    _messages.TryGetValue(finished.MessageId, out var message) &&
                    message.Tools.TryGetValue(finished.ContentIndex, out var draft):
                    draft.Name = tool.ToolName;
                    draft.Status = tool.Status;
                    draft.StatusText = tool.StatusText;
                    draft.DurationMs = tool.DurationMs;
                    Row(finished.MessageId, finished.ContentIndex, "tool",
                        ToolText(tool.ToolName, tool.InputJson, tool.Status, tool.Output ?? tool.StatusText),
                        snapshot: Snapshot(tool));
                    break;
                case SessionEvent.MessageFinished final:
                    // Native BlockFinished immediately precedes this authoritative snapshot. Replace, don't append.
                    var role = final.FinalMessage.Role == MessageRole.User ? "user" : "assistant";
                    var images = _messages.GetValueOrDefault(final.MessageId)?.Images;
                    for (var i = 0; i < final.FinalMessage.Blocks.Count; i++)
                        switch (final.FinalMessage.Blocks[i])
                        {
                            case TextBlock t:
                                Row(final.MessageId, i, role, WithImages(t.Text, images?.Count ?? 0), images: images);
                                images = null;
                                break;
                            case ThinkingBlock t: Row(final.MessageId, i, "thinking", t.Text); break;
                            case ToolCallBlock t:
                                Row(final.MessageId, i, "tool",
                                    ToolText(t.ToolName, t.InputJson, t.Status, t.Output ?? t.StatusText),
                                    snapshot: Snapshot(t)); break;
                        }
                    if (images is { Count: > 0 })
                        Row(final.MessageId, final.FinalMessage.Blocks.Count, role, WithImages("", images.Count), images: images);
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
                    _errorCount++;
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
                    if (queued == 0)
                        writer.TryWrite(new TuiEvent.TurnFinished(settled.Outcome switch
                        {
                            SessionOutcome.Completed => FuiTurnOutcome.Completed,
                            SessionOutcome.Failed => FuiTurnOutcome.Failed,
                            SessionOutcome.Interrupted => FuiTurnOutcome.Interrupted,
                            _ => FuiTurnOutcome.Other
                        }, settled.Summary));
                    break;
                case SessionEvent.QueueChanged { QueuedCount: > 0 } queue:
                    writer.TryWrite(new TuiEvent.Status($"运行中 · 排队 {queue.QueuedCount}", true));
                    break;
                case SessionEvent.QueueCleared queue:
                    foreach (var text in queue.ClearedTexts) Notice("info", "已取消排队：" + text);
                    break;
                case SessionEvent.SubagentUpdated subagent:
                    writer.TryWrite(new TuiEvent.AgentsChanged(_agents.Apply(subagent)));
                    break;
            }
        }
    }

    private void Row(string message, int index, string kind, string text, bool append = false,
        ToolCallSnapshot? snapshot = null, IReadOnlyList<TuiImage>? images = null) =>
        writer.TryWrite(new TuiEvent.Message($"{message}:{index}", kind, text, append, snapshot, images));

    private void Notice(string kind, string text) => writer.TryWrite(new TuiEvent.Message(Guid.NewGuid().ToString("N"), kind, text));

    private static string WithImages(string text, int count) =>
        count == 0 ? text : (text.Length == 0 ? "" : text + "\n") + $"[图片 × {count}]";

    private static IReadOnlyList<TuiImage> ToHistoryImages(IReadOnlyList<PureImageAttachment> attachments, string? assetsRoot)
    {
        var list = new TuiImage[attachments.Count];
        for (var i = 0; i < attachments.Count; i++)
        {
            var att = attachments[i];
            var label = att.FilePath is { Length: > 0 } fp ? Path.GetFileName(fp) : $"图片 {i + 1}";
            list[i] = new TuiImage(label, att.MimeType, LoadBytes: () => LoadPersistedImage(att, assetsRoot), MaxRows: 12);
        }
        return list;
    }

    private static byte[]? LoadPersistedImage(PureImageAttachment att, string? assetsRoot)
    {
        if (!string.IsNullOrEmpty(att.Base64Data))
        {
            var maxBase64 = (long)ProjectFiles.MaxAttachmentBytes * 4 / 3 + 32;
            if (att.Base64Data.Length > maxBase64) return null;
            try { return Convert.FromBase64String(att.Base64Data); }
            catch (FormatException) { return null; }
        }
        if (assetsRoot is null || string.IsNullOrWhiteSpace(att.FilePath)) return null;
        try
        {
            if (!Directory.Exists(assetsRoot) || File.GetAttributes(assetsRoot).HasFlag(FileAttributes.ReparsePoint))
                return null;
            var full = Path.GetFullPath(att.FilePath);
            var parent = Path.GetDirectoryName(full);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (parent is null || !string.Equals(parent, assetsRoot, comparison)) return null;
            if (!File.Exists(full) || File.GetAttributes(full).HasFlag(FileAttributes.ReparsePoint)) return null;
            using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 8192);
            if (stream.Length is <= 0 or > ProjectFiles.MaxAttachmentBytes) return null;
            var bytes = new byte[(int)stream.Length];
            stream.ReadExactly(bytes);
            return bytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static string ToolText(string name, string args, ToolCallStatus state, string? output) =>
        $"{name} [{state switch { ToolCallStatus.Running => "执行中", ToolCallStatus.Completed => "完成", ToolCallStatus.Failed => "失败", ToolCallStatus.Cancelled => "已取消", _ => "待执行" }}] {args}".TrimEnd() +
        (string.IsNullOrEmpty(output) ? "" : "\n" + output);

    private static ToolCallSnapshot Snapshot(ToolDraft tool) =>
        new(tool.Name, tool.Arguments.ToString(), tool.Status, tool.StatusText, null, false, tool.DurationMs);

    private static ToolCallSnapshot Snapshot(ToolCallBlock tool) =>
        new(tool.ToolName, tool.InputJson, tool.Status, tool.StatusText, tool.Output,
            tool.IsErrorOutput, tool.DurationMs);
}
