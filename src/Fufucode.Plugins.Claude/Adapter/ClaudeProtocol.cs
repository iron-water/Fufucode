using System.Text.Json;

namespace Fufucode.Claude;

/// <summary>
/// Claude Code CLI `--output-format stream-json` 的帧分类。
/// 分类基于 2026-10-16 对 claude 2.1.296 的真实抓取，不基于源码推测。
/// </summary>
public enum ClaudeFrameKind
{
    /// <summary>system 帧（init / hook_started / hook_response / commands_changed / permission_denied / thinking_tokens ...）。</summary>
    System,

    /// <summary>assistant 帧：message.content[] 含 text / thinking / tool_use 块。</summary>
    Assistant,

    /// <summary>user 帧：message.content[] 含 tool_result 块（工具结果回灌）。</summary>
    User,

    /// <summary>result 帧：一轮终止，含 usage / total_cost_usd / stop_reason / num_turns。</summary>
    Result,

    /// <summary>stream_event 帧：仅在 --include-partial-messages 下出现的增量事件。</summary>
    StreamEvent,

    /// <summary>CLI 发起的控制请求（can_use_tool 审批询问、或对客户端请求的响应）。</summary>
    ControlRequest,

    /// <summary>CLI 对客户端控制请求的应答。</summary>
    ControlResponse,

    /// <summary>控制请求取消。</summary>
    ControlCancelRequest,

    /// <summary>未识别的帧类型；宽容保留原始载荷，不抛异常。</summary>
    Unknown,
}

/// <summary>一轮终止信息（result 帧）。</summary>
public sealed record ClaudeResultInfo(
    string? Subtype,
    bool IsError,
    string? StopReason,
    decimal? TotalCostUsd,
    int? NumTurns,
    long? DurationMs,
    string? ResultText,
    ClaudeUsage? Usage,
    JsonElement Raw);

/// <summary>用量快照；未知字段保持 null，不补零。</summary>
public sealed record ClaudeUsage(long? InputTokens, long? OutputTokens, long? CacheReadTokens, long? CacheWriteTokens);

/// <summary>init 帧暴露的会话事实。工具清单来自 CLI 自报，未经本类推断。</summary>
public sealed record ClaudeInitInfo(
    string SessionId,
    string? Cwd,
    string? Model,
    string? PermissionMode,
    string? CliVersion,
    IReadOnlyList<string> Tools,
    IReadOnlyList<string> Capabilities,
    JsonElement Raw);

/// <summary>assistant 帧里的一个内容块。</summary>
public abstract record ClaudeContentBlock;

/// <summary>普通文本块。</summary>
public sealed record ClaudeTextBlock(string Text) : ClaudeContentBlock;

/// <summary>思考块；signature 为空时仍按文本处理。</summary>
public sealed record ClaudeThinkingBlock(string Text) : ClaudeContentBlock;

/// <summary>工具调用块。</summary>
public sealed record ClaudeToolUseBlock(string Id, string Name, string InputJson) : ClaudeContentBlock;

/// <summary>工具结果块（来自 user 帧）。</summary>
public sealed record ClaudeToolResultBlock(
    string ToolUseId,
    bool IsError,
    string? Text,
    string? PermissionDecision,
    string? PermissionSource,
    string? NonExecutionKind) : ClaudeContentBlock;

/// <summary>未识别的内容块。</summary>
public sealed record ClaudeOtherBlock(string Type, JsonElement Raw) : ClaudeContentBlock;

/// <summary>assistant 帧。</summary>
public sealed record ClaudeAssistantFrame(
    string? SessionId,
    string? MessageId,
    string? Model,
    IReadOnlyList<ClaudeContentBlock> Blocks,
    JsonElement Raw);

/// <summary>
/// CLI 发起的 can_use_tool 审批询问。请求 ID 是回灌令牌，必须原样回填。
/// </summary>
public sealed record ClaudePermissionRequest(
    string RequestId,
    string ToolName,
    string? ToolUseId,
    string? DisplayName,
    string? Description,
    string InputJson,
    JsonElement Raw);

/// <summary>本类的出站审批结论。</summary>
public enum ClaudePermissionDecision
{
    Allow,
    Deny,
}

/// <summary>stream_event 的增量事件（仅在 --include-partial-messages 下出现）。</summary>
public sealed record ClaudeTextDelta(int Index, string Delta, string Kind);

/// <summary>
/// 传输层暴露给上层适配器的统一事件。
/// </summary>
public abstract record ClaudeStreamEvent(Guid Generation, ClaudeFrameKind Kind, JsonElement Raw);

/// <summary>init 握手完成；此后可提交用户消息。</summary>
public sealed record ClaudeInitEvent(Guid Generation, ClaudeInitInfo Init, JsonElement Raw)
    : ClaudeStreamEvent(Generation, ClaudeFrameKind.System, Raw);

/// <summary>assistant 输出帧。</summary>
public sealed record ClaudeAssistantEvent(Guid Generation, ClaudeAssistantFrame Frame, JsonElement Raw)
    : ClaudeStreamEvent(Generation, ClaudeFrameKind.Assistant, Raw);

/// <summary>工具结果回灌帧。</summary>
public sealed record ClaudeToolResultEvent(Guid Generation, IReadOnlyList<ClaudeToolResultBlock> Results, JsonElement Raw)
    : ClaudeStreamEvent(Generation, ClaudeFrameKind.User, Raw);

/// <summary>一轮终止帧。</summary>
public sealed record ClaudeResultEvent(Guid Generation, ClaudeResultInfo Result, JsonElement Raw)
    : ClaudeStreamEvent(Generation, ClaudeFrameKind.Result, Raw);

/// <summary>审批询问；上层必须应答，否则 CLI 会一直等待。</summary>
public sealed record ClaudePermissionRequestEvent(Guid Generation, ClaudePermissionRequest Request, JsonElement Raw)
    : ClaudeStreamEvent(Generation, ClaudeFrameKind.ControlRequest, Raw);

/// <summary>CLI 对客户端控制请求（initialize / interrupt）的应答。</summary>
public sealed record ClaudeControlResponseEvent(Guid Generation, string? RequestId, bool Success, JsonElement Raw)
    : ClaudeStreamEvent(Generation, ClaudeFrameKind.ControlResponse, Raw);

/// <summary>权限被拒（system/permission_denied）。拒绝路径不得静默吞掉。</summary>
public sealed record ClaudePermissionDeniedEvent(Guid Generation, JsonElement Raw)
    : ClaudeStreamEvent(Generation, ClaudeFrameKind.System, Raw);

/// <summary>文本增量。</summary>
public sealed record ClaudeTextDeltaEvent(Guid Generation, ClaudeTextDelta Delta, JsonElement Raw)
    : ClaudeStreamEvent(Generation, ClaudeFrameKind.StreamEvent, Raw);

/// <summary>已宽容接收但不影响会话语义的帧（hook_*、commands_changed、thinking_tokens 等）。</summary>
public sealed record ClaudeIgnoredEvent(Guid Generation, string Subtype, JsonElement Raw)
    : ClaudeStreamEvent(Generation, ClaudeFrameKind.Unknown, Raw);

/// <summary>
/// stream-json 帧编解码。所有解析对未知/多余字段宽容：环境钩子帧与新版字段
/// 不得导致会话失败，但缺失必需字段时也不得伪造内容。
/// </summary>
public static class ClaudeProtocol
{
    public const string PermissionPromptToolStdio = "stdio";

    /// <summary>
    /// 官方执行命令（2026-10-16 实测 claude 2.1.296）：
    /// `claude -p --input-format stream-json --output-format stream-json --verbose ...`
    /// 审批走 `--permission-prompt-tool stdio`，由本应用的审批流程应答；
    /// 不使用 `--dangerously-skip-permissions` 等绕过权限模型的开关。
    /// </summary>
    public static IReadOnlyList<string> BuildArguments(string? model, string? resumeSessionId = null,
        string permissionMode = "manual", bool includePartialMessages = false)
    {
        var arguments = new List<string>
        {
            "-p",
            "--input-format", "stream-json",
            "--output-format", "stream-json",
            "--verbose",
            "--permission-prompt-tool", PermissionPromptToolStdio,
            "--permission-mode", permissionMode,
        };
        if (!string.IsNullOrWhiteSpace(model)) arguments.AddRange(["--model", model]);
        if (includePartialMessages) arguments.Add("--include-partial-messages");
        if (!string.IsNullOrWhiteSpace(resumeSessionId))
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(resumeSessionId);
            arguments.AddRange(["--resume", resumeSessionId]);
        }
        return arguments;
    }

    public static ClaudeFrameKind Classify(JsonElement frame)
    {
        var type = OptionalString(frame, "type");
        return type switch
        {
            "system" => ClaudeFrameKind.System,
            "assistant" => ClaudeFrameKind.Assistant,
            "user" => ClaudeFrameKind.User,
            "result" => ClaudeFrameKind.Result,
            "stream_event" => ClaudeFrameKind.StreamEvent,
            "control_request" => ClaudeFrameKind.ControlRequest,
            "control_response" => ClaudeFrameKind.ControlResponse,
            "control_cancel_request" => ClaudeFrameKind.ControlCancelRequest,
            _ => ClaudeFrameKind.Unknown,
        };
    }

    /// <summary>解析 system/init 帧；不是 init 时返回 null。</summary>
    public static ClaudeInitInfo? ParseInit(JsonElement frame)
    {
        if (OptionalString(frame, "type") != "system" || OptionalString(frame, "subtype") != "init") return null;
        var sessionId = OptionalString(frame, "session_id");
        if (string.IsNullOrWhiteSpace(sessionId)) return null;
        return new ClaudeInitInfo(
            sessionId,
            OptionalString(frame, "cwd"),
            OptionalString(frame, "model"),
            OptionalString(frame, "permissionMode"),
            OptionalString(frame, "claude_code_version"),
            StringArray(frame, "tools"),
            StringArray(frame, "capabilities"),
            frame);
    }

    public static ClaudeAssistantFrame ParseAssistant(JsonElement frame)
    {
        var message = frame.ValueKind == JsonValueKind.Object && frame.TryGetProperty("message", out var found)
            ? found : default;
        var blocks = new List<ClaudeContentBlock>();
        if (message.ValueKind == JsonValueKind.Object &&
            message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray()) blocks.Add(ParseBlock(block));
        }
        return new ClaudeAssistantFrame(OptionalString(frame, "session_id"),
            message.ValueKind == JsonValueKind.Object ? OptionalString(message, "id") : null,
            message.ValueKind == JsonValueKind.Object ? OptionalString(message, "model") : null,
            blocks, frame);
    }

    public static ClaudeContentBlock ParseBlock(JsonElement block)
    {
        if (block.ValueKind != JsonValueKind.Object) return new ClaudeOtherBlock("invalid", block);
        return OptionalString(block, "type") switch
        {
            "text" => new ClaudeTextBlock(OptionalString(block, "text") ?? ""),
            "thinking" => new ClaudeThinkingBlock(OptionalString(block, "thinking") ?? ""),
            "tool_use" => new ClaudeToolUseBlock(OptionalString(block, "id") ?? "",
                OptionalString(block, "name") ?? "", RawText(block, "input")),
            "tool_result" => ParseToolResult(block),
            var other => new ClaudeOtherBlock(other ?? "unknown", block),
        };
    }

    private static ClaudeToolResultBlock ParseToolResult(JsonElement block)
    {
        return new ClaudeToolResultBlock(OptionalString(block, "tool_use_id") ?? "",
            OptionalBool(block, "is_error") == true, ToolResultText(block), null, null, null);
    }

    /// <summary>
    /// 真实帧里 permission_decision 位于帧级 tool_result_meta[]（按 tool_use_id 关联），
    /// 而不是 tool_result 块内；这里按 id 关联后补齐，缺失时保持 null 而不伪造。
    /// </summary>
    private static ClaudeToolResultBlock WithFrameMeta(ClaudeToolResultBlock result, JsonElement frame)
    {
        if (result.PermissionDecision is not null || result.NonExecutionKind is not null) return result;
        if (frame.ValueKind != JsonValueKind.Object || !frame.TryGetProperty("tool_result_meta", out var meta)) return result;
        var entries = meta.ValueKind == JsonValueKind.Array ? meta.EnumerateArray() : new[] { meta }.AsEnumerable();
        foreach (var entry in entries)
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;
            if (OptionalString(entry, "id") is { } id && id != result.ToolUseId) continue;
            var nonExecutionKind = OptionalString(entry, "non_execution_kind");
            string? decision = null, source = null;
            if (entry.TryGetProperty("permission_decision", out var permission) && permission.ValueKind == JsonValueKind.Object)
            {
                decision = OptionalString(permission, "decision");
                source = OptionalString(permission, "source");
            }
            return result with { PermissionDecision = decision, PermissionSource = source, NonExecutionKind = nonExecutionKind };
        }
        return result;
    }

    private static string? ToolResultText(JsonElement block)
    {
        if (block.ValueKind != JsonValueKind.Object || !block.TryGetProperty("content", out var content)) return null;
        if (content.ValueKind == JsonValueKind.String) return content.GetString();
        if (content.ValueKind != JsonValueKind.Array) return null;
        var parts = content.EnumerateArray()
            .Where(part => part.ValueKind == JsonValueKind.Object && OptionalString(part, "type") == "text")
            .Select(part => OptionalString(part, "text") ?? "").Where(text => text.Length > 0);
        return string.Join("\n", parts);
    }

    public static IReadOnlyList<ClaudeToolResultBlock> ParseToolResults(JsonElement frame)
    {
        if (OptionalString(frame, "type") != "user") return [];
        if (frame.ValueKind != JsonValueKind.Object ||
            !frame.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
            return [];
        if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return [];
        return content.EnumerateArray().Select(ParseBlock).OfType<ClaudeToolResultBlock>()
            .Select(result => WithFrameMeta(result, frame)).ToArray();
    }

    public static ClaudeResultInfo? ParseResult(JsonElement frame)
    {
        if (OptionalString(frame, "type") != "result") return null;
        return new ClaudeResultInfo(OptionalString(frame, "subtype"), OptionalBool(frame, "is_error") == true,
            OptionalString(frame, "stop_reason"), OptionalDecimal(frame, "total_cost_usd"),
            OptionalInt(frame, "num_turns"), OptionalLong(frame, "duration_ms"),
            OptionalString(frame, "result"), ParseUsage(frame), frame);
    }

    public static ClaudeUsage? ParseUsage(JsonElement frame)
    {
        if (frame.ValueKind != JsonValueKind.Object ||
            !frame.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) return null;
        return new ClaudeUsage(OptionalLong(usage, "input_tokens"), OptionalLong(usage, "output_tokens"),
            OptionalLong(usage, "cache_read_input_tokens"), OptionalLong(usage, "cache_creation_input_tokens"));
    }

    /// <summary>解析 control_request；仅 can_use_tool 返回请求对象。</summary>
    public static ClaudePermissionRequest? ParsePermissionRequest(JsonElement frame)
    {
        if (OptionalString(frame, "type") != "control_request") return null;
        var requestId = OptionalString(frame, "request_id");
        if (string.IsNullOrWhiteSpace(requestId)) return null;
        if (!frame.TryGetProperty("request", out var request) || request.ValueKind != JsonValueKind.Object) return null;
        if (OptionalString(request, "subtype") != "can_use_tool") return null;
        return new ClaudePermissionRequest(requestId, OptionalString(request, "tool_name") ?? "",
            OptionalString(request, "tool_use_id"), OptionalString(request, "display_name"),
            OptionalString(request, "description"), RawText(request, "input"), frame);
    }

    /// <summary>解析 --include-partial-messages 的文本增量。</summary>
    public static ClaudeTextDelta? ParseTextDelta(JsonElement frame)
    {
        if (OptionalString(frame, "type") != "stream_event") return null;
        if (frame.ValueKind != JsonValueKind.Object ||
            !frame.TryGetProperty("event", out var ev) || ev.ValueKind != JsonValueKind.Object) return null;
        var eventType = OptionalString(ev, "type");
        if (eventType != "content_block_delta") return null;
        if (!ev.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object) return null;
        var deltaType = OptionalString(delta, "type");
        return deltaType switch
        {
            "text_delta" => new ClaudeTextDelta(OptionalInt(ev, "index") ?? 0, OptionalString(delta, "text") ?? "text", "text"),
            "thinking_delta" => new ClaudeTextDelta(OptionalInt(ev, "index") ?? 0,
                OptionalString(delta, "thinking") ?? "", "thinking"),
            _ => null,
        };
    }

    /// <summary>构造出站用户消息帧（text 块）。</summary>
    public static string BuildUserMessage(string text) => JsonSerializer.Serialize(new
    {
        type = "user",
        message = new { role = "user", content = new object[] { new { type = "text", text } } },
    });

    /// <summary>构造 initialize 控制请求；hooks 为 null 表示不注册回调。</summary>
    public static string BuildInitialize(string requestId) => JsonSerializer.Serialize(new
    {
        type = "control_request",
        request_id = requestId,
        request = new { subtype = "initialize", hooks = (object?)null, sdkMcpServers = Array.Empty<string>(), jsonSchema = (object?)null },
    });

    /// <summary>构造 interrupt 控制请求。</summary>
    public static string BuildInterrupt(string requestId) => JsonSerializer.Serialize(new
    {
        type = "control_request",
        request_id = requestId,
        request = new { subtype = "interrupt" },
    });

    /// <summary>
    /// 构造审批结论。允许时保留 CLI 给出的原始 input（不改写参数）；
    /// 拒绝时把理由作为 message 回灌，CLI 会把它放进 tool_result。
    /// </summary>
    public static string BuildPermissionResponse(string requestId, ClaudePermissionDecision decision,
        string? message, JsonElement? allowInput)
    {
        object payload;
        if (decision == ClaudePermissionDecision.Allow)
        {
            payload = allowInput is { ValueKind: JsonValueKind.Object } input
                ? new { behavior = "allow", updatedInput = input }
                : new { behavior = "allow" };
        }
        else
        {
            payload = new { behavior = "deny", message = string.IsNullOrWhiteSpace(message) ? "Denied." : message };
        }
        return JsonSerializer.Serialize(new
        {
            type = "control_response",
            response = new { subtype = "success", request_id = requestId, response = payload },
        });
    }

    private static string RawText(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) ? value.GetRawText() : "{}";

    private static IReadOnlyList<string> StringArray(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array) return [];
        return value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString() ?? "").Where(item => item.Length > 0).ToArray();
    }

    public static string? OptionalString(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static bool? OptionalBool(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

    private static int? OptionalInt(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var result) ? result : null;

    private static long? OptionalLong(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var result) ? result : null;

    private static decimal? OptionalDecimal(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var result) ? result : null;
}
