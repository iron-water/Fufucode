using System.Text;
using System.Text.Json;
using Fufucode.Pi;

namespace Fufucode.Core;

public sealed record PiToolCard(string ToolCallId, string ToolName, JsonElement? Args,
    JsonElement? DisplayResult, bool IsFinal, bool? IsError);

/// <summary>Rebuilds the display timeline from Pi's delta-only wire events.</summary>
public sealed class MessageTimeline
{
    private sealed class DraftBlock
    {
        public string Type = "";
        public readonly StringBuilder Text = new();
        public string Id = "";
        public string Name = "";
        public PiContentBlock? Final;
    }

    private readonly List<PiMessage> _messages = [];
    private readonly Dictionary<string, PiToolCard> _tools = [];
    private readonly SortedDictionary<int, DraftBlock> _blocks = [];
    private int? _activeIndex;
    private PiMessage? _activeBase;
    private PiUsage? _currentUsage;

    public IReadOnlyList<PiMessage> Messages => _messages.ToArray();
    public IReadOnlyList<PiToolCard> Tools => _tools.Values.ToArray();
    public PiUsage? CurrentResponseUsage => _currentUsage;

    /// <summary>Final-message usage projection; an active streamed assistant is explicitly excluded.</summary>
    public SessionUsageReport GetUsage() => SessionUsageQuery.Query(_messages, _activeIndex);

    public void Restore(IReadOnlyList<PiMessage> messages)
    {
        if (_activeIndex is not null) throw new InvalidOperationException("Cannot restore during a message stream.");
        _messages.Clear();
        _messages.AddRange(messages);
        _tools.Clear();
        _blocks.Clear();
        _activeBase = null;
        _currentUsage = messages.LastOrDefault(m => m.Role == "assistant")?.Usage;
    }

    public void Apply(PiSessionEvent ev)
    {
        switch (ev)
        {
            case PiMessageEvent { Type: "message_start" } start:
                if (_activeIndex is not null) throw new PiRpcException("A message started before the previous message ended.");
                _activeIndex = _messages.Count;
                _activeBase = start.Message;
                _currentUsage = start.Message.Usage;
                _blocks.Clear();
                for (var i = 0; i < start.Message.Content.Count; i++)
                    _blocks[i] = FromBlock(start.Message.Content[i]);
                _messages.Add(start.Message);
                break;
            case PiMessageUpdateEvent update:
                ApplyUpdate(update);
                break;
            case PiMessageEvent { Type: "message_end" } end:
                if (_activeIndex is { } index)
                    _messages[index] = end.Message; // Authoritative final message, never append reconstructed text.
                else _messages.Add(end.Message);
                _activeIndex = null;
                _activeBase = null;
                _currentUsage = end.Message.Usage;
                _blocks.Clear();
                break;
            case PiToolExecutionEvent tool:
                ApplyTool(tool);
                break;
        }
    }

    private void ApplyUpdate(PiMessageUpdateEvent ev)
    {
        if (_activeIndex is not { } index || _activeBase is null || _activeBase.Role != "assistant")
            throw new PiRpcException("Assistant content update arrived without an active assistant message.");
        if (ev.Usage is not null) _currentUsage = ev.Usage; // Provider reports cumulative usage; it is never summed.
        var update = ev.Update;
        if (update.ContentIndex is { } contentIndex)
        {
            if (contentIndex < 0 || contentIndex > 10000) throw new PiRpcException("Invalid contentIndex.");
            if (!_blocks.TryGetValue(contentIndex, out var block)) _blocks[contentIndex] = block = new DraftBlock();
            var expected = update.Type.StartsWith("text_", StringComparison.Ordinal) ? "text" :
                update.Type.StartsWith("thinking_", StringComparison.Ordinal) ? "thinking" :
                update.Type.StartsWith("toolcall_", StringComparison.Ordinal) ? "toolCall" : "";
            if (expected.Length != 0)
            {
                if (block.Type.Length != 0 && block.Type != expected) throw new PiRpcException("Content block type changed at one index.");
                block.Type = expected;
            }
            if (update.Type.EndsWith("_start", StringComparison.Ordinal))
            {
                block.Id = update.ToolCallId ?? block.Id;
                block.Name = update.ToolName ?? block.Name;
            }
            if (update.Type.EndsWith("_delta", StringComparison.Ordinal))
                block.Text.Append(update.Delta ?? throw new PiRpcException("Content delta lacks text."));
            if (update.Type.EndsWith("_end", StringComparison.Ordinal))
                block.Final = update.CompletedBlock ?? throw new PiRpcException("Content end lacks a final block.");
        }
        _messages[index] = _activeBase with { Content = _blocks.Values.Select(ToBlock).ToArray(), Usage = _currentUsage };
    }

    private static DraftBlock FromBlock(PiContentBlock block)
    {
        var draft = new DraftBlock { Type = block.Type, Final = block };
        return draft;
    }

    private static PiContentBlock ToBlock(DraftBlock block)
    {
        if (block.Final is { } final) return final;
        return block.Type switch
        {
            "text" => new PiTextBlock(block.Text.ToString(), default),
            "thinking" => new PiThinkingBlock(block.Text.ToString(), default),
            "toolCall" => new PiToolCallDraftBlock(block.Id, block.Name, block.Text.ToString()),
            _ => new PiOtherBlock(block.Type, default)
        };
    }

    private void ApplyTool(PiToolExecutionEvent ev)
    {
        _tools.TryGetValue(ev.ToolCallId, out var previous);
        _tools[ev.ToolCallId] = new PiToolCard(ev.ToolCallId, ev.ToolName,
            ev.Args ?? previous?.Args,
            ev.Result ?? previous?.DisplayResult, // Partial result is latest state, not universal append-only text.
            ev.Type == "tool_execution_end", ev.IsError ?? previous?.IsError);
    }
}
