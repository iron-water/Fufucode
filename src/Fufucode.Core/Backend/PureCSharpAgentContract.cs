using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Fufucode.Core.Backend;

/// <summary>纯 C# 内核使用的角色。</summary>
public enum PureAgentRole
{
    System,
    User,
    Assistant,
    Tool,
}

/// <summary>六级权限天梯模式（对标 Claude Code / Codex）。</summary>
public enum PermissionMode
{
    Default = 0,
    DontAsk = 0,
    AcceptEdits = 1,
    Plan = 2,
    Auto = 3,
    BypassPermissions = 4,
}

/// <summary>工具动作契约定义（名称、说明、参数 JSON Schema）。</summary>
public sealed record PureToolDefinition(string Name, string Description, string ParametersJsonSchema);

/// <summary>一次模型请求（无外部 Node/Python 依赖，纯托管）。</summary>
public sealed record PureModelRequest(
    IReadOnlyList<PureAgentMessage> Messages,
    IReadOnlyList<string> ToolNames,
    string? ModelId = null,
    IReadOnlyList<PureToolDefinition>? ToolDefinitions = null,
    string? ReasoningEffort = null);

/// <summary>图片附件（Base64 编码数据与 MIME 类型）。</summary>
public sealed record PureImageAttachment(string Base64Data, string MimeType);

/// <summary>模型请求/工具调度共用的一条消息。支持工具调用与结果的精确关联。</summary>
public sealed record PureAgentMessage(
    PureAgentRole Role,
    string Content,
    string? ToolCallId = null,
    IReadOnlyList<PureToolCall>? ToolCalls = null,
    IReadOnlyList<PureImageAttachment>? Images = null,
    PureUsage? Usage = null);

/// <summary>一次模型响应的用量（未知字段保持 null，不补零）。</summary>
public sealed record PureUsage(long? InputTokens = null, long? OutputTokens = null, long? TotalTokens = null,
    long? CacheReadTokens = null, long? CacheWriteTokens = null)
{
    public bool IsEmpty => InputTokens is null && OutputTokens is null && TotalTokens is null
        && CacheReadTokens is null && CacheWriteTokens is null;
}

/// <summary>一次模型响应：要么是文本，要么是一个或多个工具调用。</summary>
public sealed record PureModelResponse(string? Text, IReadOnlyList<PureToolCall> ToolCalls,
    string? FinishReason = null, PureUsage? Usage = null)
{
    public static PureModelResponse FromText(string text) => new(text, Array.Empty<PureToolCall>());

    public static PureModelResponse FromToolCalls(IReadOnlyList<PureToolCall> toolCalls) =>
        new(null, toolCalls);
}

/// <summary>模型请求的工具调用。</summary>
public sealed record PureToolCall(string Id, string Name, string ArgumentsJson);

/// <summary>工具调度结果。</summary>
public sealed record PureToolResult(string Id, bool Success, string Content);

/// <summary>工具调度契约。</summary>
public interface IPureToolDispatcher
{
    Task<PureToolResult> DispatchAsync(PureToolCall call, CancellationToken cancellationToken = default);
}

/// <summary>单轮内核运行状态。</summary>
public enum PureAgentTurnStatus
{
    Completed,
    ToolCalls,
    Failed,
}

/// <summary>单轮运行结果（模型响应 + 工具结果 + 最近一次模型用量）。</summary>
public sealed record PureAgentTurnResult(
    PureAgentTurnStatus Status,
    PureModelResponse Response,
    IReadOnlyList<PureToolResult> ToolResults,
    string? Error = null,
    PureUsage? Usage = null);

/// <summary>内核运行状态快照。</summary>
public sealed record PureAgentState(string? ModelId, int MessageCount, bool IsRunning, IReadOnlyList<PureAgentMessage>? History = null);

/// <summary>
/// 纯 C# Agent 内核契约：模型请求、工具调度、状态管理，全程无外部 Node/Python 依赖。
/// </summary>
public interface IPureCSharpAgentEngine
{
    /// <summary>推进一轮：请求模型，执行工具循环，将工具结果回填上下文并再次请求模型，直至终态。</summary>
    Task<PureAgentTurnResult> RunTurnAsync(PureModelRequest request, CancellationToken cancellationToken = default);

    /// <summary>当前内核状态快照（包含当前模型与完整上下文历史）。</summary>
    PureAgentState GetState();

    /// <summary>从持久化历史记录恢复消息上下文。</summary>
    void RestoreHistory(IReadOnlyList<PureAgentMessage> messages);

    /// <summary>清空对话与运行状态。</summary>
    void Reset();
}
