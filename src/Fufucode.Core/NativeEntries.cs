using System.Text.Json;
using Fufucode.Core.Backend;

namespace Fufucode.Core;

/// <summary>
/// 把 FUMO/协议插件的 <see cref="PureAgentMessage"/> 历史映射为与 Pi get_entries 同形的条目，
/// 供桌面时间线回放。工具参数（<see cref="PureToolCall.ArgumentsJson"/>）已经以 JSON 文本形式持有，
/// 校验后直接写入原始 JSON（不建 JsonDocument、不做序列化往返），非法 JSON 保持原有语义写入 null。
/// </summary>
internal static class NativeEntries
{
    public static List<JsonElement> Build(IReadOnlyList<PureAgentMessage> messages, bool stableIds = false)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var entries = new List<JsonElement>(messages.Count);
        string? parent = null;
        for (var i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            var id = stableIds ? StableId(message) : $"entry_{i}";
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteString("id", id);
                if (parent is null) writer.WriteNull("parentId");
                else writer.WriteString("parentId", parent);
                writer.WriteString("type", "message");
                writer.WriteStartObject("message");
                writer.WriteString("role", message.Role switch
                {
                    PureAgentRole.User => "user",
                    PureAgentRole.Assistant => "assistant",
                    PureAgentRole.Tool => "toolResult",
                    _ => "system",
                });
                writer.WriteStartArray("content");
                if (!string.IsNullOrEmpty(message.Content))
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", "text");
                    writer.WriteString("text", message.Content);
                    writer.WriteEndObject();
                }
                if (message.ToolCalls is { Count: > 0 } calls)
                {
                    foreach (var call in calls)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("type", "toolCall");
                        writer.WriteString("id", call.Id);
                        writer.WriteString("name", call.Name);
                        writer.WritePropertyName("arguments");
                        WriteArguments(writer, call.ArgumentsJson);
                        writer.WriteEndObject();
                    }
                }
                writer.WriteEndArray();
                if (message.Usage is { } known)
                {
                    writer.WriteStartObject("usage");
                    WriteNullableNumber(writer, "input", known.InputTokens);
                    WriteNullableNumber(writer, "output", known.OutputTokens);
                    WriteNullableNumber(writer, "totalTokens", known.TotalTokens);
                    WriteNullableNumber(writer, "cacheRead", known.CacheReadTokens);
                    WriteNullableNumber(writer, "cacheWrite", known.CacheWriteTokens);
                    writer.WriteEndObject();
                }
                else
                {
                    writer.WriteNull("usage");
                }
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
            using var document = JsonDocument.Parse(buffer.ToArray());
            entries.Add(document.RootElement.Clone());
            parent = id;
        }
        return entries;
    }

    /// <summary>最后一条消息的条目 id（空历史时为 null）。</summary>
    public static JsonElement Leaf(IReadOnlyList<PureAgentMessage> messages, bool stableIds = false) => messages.Count == 0
        ? JsonSerializer.SerializeToElement((string?)null)
        : JsonSerializer.SerializeToElement(stableIds ? StableId(messages[^1]) : $"entry_{messages.Count - 1}");

    private static string StableId(PureAgentMessage message) => "native_" + message.EntryId;

    public static int ResolveIndex(IReadOnlyList<PureAgentMessage> messages, string entryId)
    {
        // Legacy positional IDs must never silently select a different message after compaction.
        if (!entryId.StartsWith("native_", StringComparison.Ordinal)) return -1;
        var found = -1;
        for (var i = 0; i < messages.Count; i++)
        {
            if (!string.Equals(StableId(messages[i]), entryId, StringComparison.Ordinal)) continue;
            if (found >= 0) return -1; // Ambiguous persisted identities are not valid selections.
            found = i;
        }
        return found;
    }

    private static void WriteArguments(Utf8JsonWriter writer, string? argumentsJson)
    {
        // 与旧实现一致：空或非法参数写成 null，而不是伪造空对象。
        if (!string.IsNullOrWhiteSpace(argumentsJson))
        {
            try
            {
                // 校验后直接写原始 JSON：不分配 JsonDocument，也不做序列化往返。
                writer.WriteRawValue(argumentsJson, skipInputValidation: false);
                return;
            }
            catch (JsonException) { }
        }
        writer.WriteNullValue();
    }

    private static void WriteNullableNumber(Utf8JsonWriter writer, string name, long? value)
    {
        if (value is { } number) writer.WriteNumber(name, number);
        else writer.WriteNull(name);
    }
}
