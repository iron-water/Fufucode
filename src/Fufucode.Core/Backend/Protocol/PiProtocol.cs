using System.Text.Json;

namespace Fufucode.Core;

public abstract record PiContentBlock(string Type, JsonElement Raw);
public sealed record PiTextBlock(string Text, JsonElement Source) : PiContentBlock("text", Source);
public sealed record PiThinkingBlock(string Thinking, JsonElement Source) : PiContentBlock("thinking", Source);
public sealed record PiToolCallBlock(string Id, string Name, JsonElement? Arguments, JsonElement Source) : PiContentBlock("toolCall", Source);
public sealed record PiToolCallDraftBlock(string Id, string Name, string ArgumentsText)
    : PiContentBlock("toolCallDraft", default);
public sealed record PiOtherBlock(string BlockType, JsonElement Source) : PiContentBlock(BlockType, Source);

public sealed record PiUsage(long? Input, long? Output, long? CacheRead, long? CacheWrite,
    long? TotalTokens, JsonElement Raw);

public sealed record PiMessage(string Role, IReadOnlyList<PiContentBlock> Content, string? StopReason,
    string? ErrorMessage, PiUsage? Usage, JsonElement Raw);

public sealed record PiContentUpdate(string Type, int? ContentIndex, string? Delta,
    PiContentBlock? CompletedBlock, string? ToolCallId, string? ToolName, JsonElement Raw);

public abstract record PiSessionEvent(Guid Generation, string Type, JsonElement Raw);
public sealed record PiMessageEvent(Guid Generation, string Type, PiMessage Message, JsonElement Raw)
    : PiSessionEvent(Generation, Type, Raw);
public sealed record PiMessageUpdateEvent(Guid Generation, PiContentUpdate Update, PiUsage? Usage, JsonElement Raw)
    : PiSessionEvent(Generation, "message_update", Raw);
public sealed record PiToolExecutionEvent(Guid Generation, string Type, string ToolCallId, string ToolName,
    JsonElement? Args, JsonElement? Result, bool? IsError, JsonElement Raw)
    : PiSessionEvent(Generation, Type, Raw);
public sealed record PiExtensionUiRequestEvent(Guid Generation, string Id, string Method, JsonElement Raw)
    : PiSessionEvent(Generation, "extension_ui_request", Raw);
public sealed record PiExtensionErrorEvent(Guid Generation, string ExtensionPath, string EventName, string Error, JsonElement Raw)
    : PiSessionEvent(Generation, "extension_error", Raw);
public sealed record PiOtherEvent(Guid Generation, string Type, JsonElement Raw)
    : PiSessionEvent(Generation, Type, Raw);

public static class PiProtocol
{
    public static PiSessionEvent Parse(RpcEvent source)
    {
        var raw = source.Record;
        return source.Type switch
        {
            "message_start" or "message_end" => new PiMessageEvent(source.Generation, source.Type,
                ParseMessage(RequiredObject(raw, "message")), raw),
            "message_update" => new PiMessageUpdateEvent(source.Generation,
                ParseUpdate(RequiredObject(raw, "assistantMessageEvent")),
                OptionalObject(raw, "usage") is { } usage ? ParseUsage(usage) : null, raw),
            "tool_execution_start" or "tool_execution_update" or "tool_execution_end" =>
                new PiToolExecutionEvent(source.Generation, source.Type,
                    RequiredString(raw, "toolCallId"), RequiredString(raw, "toolName"),
                    OptionalAny(raw, "args"), OptionalAny(raw, source.Type == "tool_execution_update" ? "partialResult" : "result"),
                    OptionalBool(raw, "isError"), raw),
            "extension_ui_request" => new PiExtensionUiRequestEvent(source.Generation,
                RequiredString(raw, "id"), RequiredString(raw, "method"), raw),
            "extension_error" => new PiExtensionErrorEvent(source.Generation,
                RequiredString(raw, "extensionPath"), RequiredString(raw, "event"), RequiredString(raw, "error"), raw),
            _ => new PiOtherEvent(source.Generation, source.Type, raw)
        };
    }

    public static PiMessage ParseMessage(JsonElement message)
    {
        var role = RequiredString(message, "role");
        var content = new List<PiContentBlock>();
        if (message.TryGetProperty("content", out var value))
        {
            if (value.ValueKind == JsonValueKind.String)
                content.Add(new PiTextBlock(value.GetString()!, value.Clone()));
            else if (value.ValueKind == JsonValueKind.Array)
                foreach (var block in value.EnumerateArray()) content.Add(ParseBlock(block));
            else throw new RpcException("Message content is neither a string nor an array.");
        }
        return new PiMessage(role, content, OptionalString(message, "stopReason"),
            OptionalString(message, "errorMessage"),
            OptionalObject(message, "usage") is { } usage ? ParseUsage(usage) : null, message.Clone());
    }

    public static PiContentBlock ParseBlock(JsonElement block)
    {
        if (block.ValueKind != JsonValueKind.Object) throw new RpcException("Message content block is not an object.");
        var type = RequiredString(block, "type");
        var raw = block.Clone();
        return type switch
        {
            "text" => new PiTextBlock(RequiredString(block, "text"), raw),
            "thinking" => new PiThinkingBlock(OptionalString(block, "thinking") ?? "", raw),
            "toolCall" => new PiToolCallBlock(RequiredString(block, "id"), RequiredString(block, "name"), OptionalAny(block, "arguments"), raw),
            _ => new PiOtherBlock(type, raw)
        };
    }

    public static PiUsage ParseUsage(JsonElement usage) => new(
        OptionalLong(usage, "input"), OptionalLong(usage, "output"),
        OptionalLong(usage, "cacheRead"), OptionalLong(usage, "cacheWrite"),
        OptionalLong(usage, "totalTokens"), usage.Clone());

    private static PiContentUpdate ParseUpdate(JsonElement update)
    {
        var type = RequiredString(update, "type");
        var index = OptionalInt(update, "contentIndex");
        if (type is "text_start" or "text_delta" or "text_end" or "thinking_start" or "thinking_delta" or "thinking_end" or
            "toolcall_start" or "toolcall_delta" or "toolcall_end" && index is null)
            throw new RpcException($"{type} lacks contentIndex.");
        PiContentBlock? completed = null;
        if (type == "text_end") completed = ParseCompletedText(update, "text");
        if (type == "thinking_end") completed = ParseCompletedText(update, "thinking");
        if (type == "toolcall_end") completed = ParseBlock(RequiredObject(update, "toolCall"));
        return new PiContentUpdate(type, index, OptionalString(update, "delta"), completed,
            OptionalString(update, "id"), OptionalString(update, "toolName"), update.Clone());
    }

    private static PiContentBlock ParseCompletedText(JsonElement update, string type)
    {
        if (!update.TryGetProperty("content", out var content)) throw new RpcException($"{type}_end lacks content.");
        if (content.ValueKind == JsonValueKind.Object) return ParseBlock(content); // Tolerate older fixture shapes.
        if (content.ValueKind != JsonValueKind.String) throw new RpcException($"{type}_end content is not text.");
        return type == "text" ? new PiTextBlock(content.GetString()!, content.Clone()) :
            new PiThinkingBlock(content.GetString()!, content.Clone());
    }

    private static JsonElement RequiredObject(JsonElement obj, string name)
    {
        if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object) return value;
        throw new RpcException($"RPC record lacks object {name}.");
    }
    private static string RequiredString(JsonElement obj, string name) =>
        OptionalString(obj, name) ?? throw new RpcException($"RPC record lacks string {name}.");
    private static string? OptionalString(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
    private static JsonElement? OptionalObject(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
            ? value.Clone() : null;
    private static JsonElement? OptionalAny(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var value) ? value.Clone() : null;
    private static bool? OptionalBool(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean() : null;
    private static int? OptionalInt(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var result)
            ? result : null;
    private static long? OptionalLong(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var result)
            ? result : null;
}
