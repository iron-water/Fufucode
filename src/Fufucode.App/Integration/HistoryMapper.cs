using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Fufucode.App.Abstractions;
using Fufucode.Pi;

namespace Fufucode.App.Integration;

/// <summary>
/// 把 Core 恢复的持久化历史（Pi get_entries 当前分支消息）映射为冻结契约的终态消息，
/// 供时间线一次性回放。映射规则与 PiChatSessionAdapter 的流式映射保持一致；
/// 若 Core 后续公开等价 helper，这里应替换为 Core 版本。
/// 已知限制（M2）：toolResult/custom 条目不单独回放，工具卡片不恢复输出文本，
/// 状态按所属助手消息的持久化 stopReason 标注，不虚构执行结果。
/// </summary>
public static class HistoryMapper
{
    public static IReadOnlyList<ChatMessage> MapTranscript(IEnumerable<PiMessage> messages, string? currentModel)
    {
        var result = new List<ChatMessage>();
        var index = 0;
        foreach (var message in messages)
        {
            MessageRole? role = message.Role switch
            {
                "user" => MessageRole.User,
                "assistant" => MessageRole.Assistant,
                _ => null,
            };
            if (role is null)
            {
                continue;
            }

            index++;
            var blocks = message.Content.Select(block => MapBlock(block, message.StopReason))
                .Where(block => block is not null).Cast<MessageBlock>().ToArray();
            var model = role == MessageRole.Assistant ? StringValue(message.Raw, "model") ?? currentModel : null;
            result.Add(new ChatMessage($"history-{index}", role.Value, model, blocks, MapUsage(message.Usage), true));
        }

        return result;
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

    // 持久化历史不含工具执行结果；按消息自身的 stopReason 标注，中断/失败不标成成功。
    private static ToolCallStatus MapToolStatus(string? stopReason) => stopReason switch
    {
        "aborted" => ToolCallStatus.Cancelled,
        "error" => ToolCallStatus.Failed,
        _ => ToolCallStatus.Completed,
    };

    private static UsageInfo? MapUsage(PiUsage? usage)
    {
        if (usage is null)
        {
            return null;
        }

        decimal? totalCost = null;
        if (usage.Raw.TryGetProperty("cost", out var cost) && cost.ValueKind == JsonValueKind.Object &&
            cost.TryGetProperty("total", out var total) && total.ValueKind == JsonValueKind.Number &&
            total.TryGetDecimal(out var amount))
        {
            totalCost = amount;
        }

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
