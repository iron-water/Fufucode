using System.Text.Json;
using Fufucode.App.Abstractions;

namespace Fufucode.Core;

/// <summary>Maps Pi's persisted transcript messages onto the UI-free chat contract.</summary>
public static class MessageBlockMapper
{
    /// <summary>Maps user and assistant messages, skipping Pi history entries with other roles.</summary>
    public static IReadOnlyList<ChatMessage> MapTranscript(IEnumerable<PiMessage> messages, string? currentModel)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var result = new List<ChatMessage>();
        var index = 0;
        foreach (var message in messages)
        {
            var mapped = MapMessage(message, currentModel, $"history-{index + 1}");
            if (mapped is null) continue;
            index++;
            result.Add(mapped with { Id = $"history-{index}" });
        }

        return result;
    }

    /// <summary>Maps one persisted Pi message; toolResult/custom and unknown roles are omitted.</summary>
    public static ChatMessage? MapMessage(PiMessage message, string? currentModel, string id)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var role = message.Role switch
        {
            "user" => MessageRole.User,
            "assistant" => MessageRole.Assistant,
            _ => (MessageRole?)null,
        };
        if (role is null) return null;

        var blocks = message.Content.Select(block => MapBlock(block, message.StopReason))
            .Where(block => block is not null).Cast<MessageBlock>().ToArray();
        var model = role == MessageRole.Assistant ? StringValue(message.Raw, "model") ?? currentModel : null;
        return new ChatMessage(id, role.Value, model, blocks, MapUsage(message.Usage), true);
    }

    private static MessageBlock? MapBlock(PiContentBlock block, string? stopReason) => block switch
    {
        PiTextBlock text => new TextBlock(text.Text),
        PiThinkingBlock thinking => new ThinkingBlock(thinking.Thinking),
        PiToolCallBlock tool => new ToolCallBlock(tool.Id, tool.Name,
            tool.Arguments?.GetRawText() ?? "{}", MapToolStatus(stopReason), null, null, false),
        PiToolCallDraftBlock draft => new ToolCallBlock(draft.Id, draft.Name, draft.ArgumentsText,
            MapToolStatus(stopReason), null, null, false),
        _ => null,
    };

    // Persisted history has no tool execution result; message stopReason is the available evidence.
    private static ToolCallStatus MapToolStatus(string? stopReason) => stopReason switch
    {
        "aborted" => ToolCallStatus.Cancelled,
        "error" => ToolCallStatus.Failed,
        _ => ToolCallStatus.Completed,
    };

    private static UsageInfo? MapUsage(PiUsage? usage)
    {
        if (usage is null) return null;
        decimal? totalCost = null;
        if (usage.Raw.ValueKind == JsonValueKind.Object &&
            usage.Raw.TryGetProperty("cost", out var cost) && cost.ValueKind == JsonValueKind.Object &&
            cost.TryGetProperty("total", out var total) && total.ValueKind == JsonValueKind.Number &&
            total.TryGetDecimal(out var amount))
            totalCost = amount;

        return new UsageInfo
        {
            InputTokens = CheckedInt(usage.Input),
            OutputTokens = CheckedInt(usage.Output),
            CacheReadTokens = CheckedInt(usage.CacheRead),
            CacheWriteTokens = CheckedInt(usage.CacheWrite),
            TotalCost = totalCost,
        };
    }

    private static int? CheckedInt(long? value) =>
        value is >= int.MinValue and <= int.MaxValue ? (int)value.Value : null;

    private static string? StringValue(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var field) &&
        field.ValueKind == JsonValueKind.String
            ? field.GetString()
            : null;
}
