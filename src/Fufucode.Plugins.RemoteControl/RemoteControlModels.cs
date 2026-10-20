using System.Text.Json.Serialization;

namespace Fufucode.Core.RemoteControl;

/// <summary>手机远控：消息角色（错误条目也以助手侧呈现）。</summary>
public enum RemoteEntryRole
{
    User,
    Assistant,
}

/// <summary>手机远控：工具调用状态，额外区分「等待审批」。</summary>
public enum RemoteToolStatus
{
    Pending,
    AwaitingApproval,
    Running,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>手机远控：审批决定。</summary>
public enum RemoteApprovalDecision
{
    Approve,
    Deny,
}

/// <summary>消息内容块（与桌面时间线同构的传输投影）。</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(RemoteTextBlock), "text")]
[JsonDerivedType(typeof(RemoteThinkingBlock), "thinking")]
[JsonDerivedType(typeof(RemoteToolBlock), "tool")]
[JsonDerivedType(typeof(RemoteErrorBlock), "error")]
public abstract record RemoteBlock;

public sealed record RemoteTextBlock(string Text) : RemoteBlock;

public sealed record RemoteThinkingBlock(string Text) : RemoteBlock;

public sealed record RemoteToolBlock(RemoteToolCall Tool) : RemoteBlock;

public sealed record RemoteErrorBlock(string Message) : RemoteBlock;

/// <summary>工具调用投影：参数、状态、输出。</summary>
public sealed record RemoteToolCall(
    string ToolCallId,
    string ToolName,
    string ArgsJson,
    RemoteToolStatus Status,
    string? StatusText,
    string? Output,
    bool IsErrorOutput);

/// <summary>一条时间线条目（一条消息或一个错误项）。</summary>
public sealed record RemoteTimelineEntry(
    string Id,
    RemoteEntryRole Role,
    bool IsStreaming,
    string? Model,
    IReadOnlyList<RemoteBlock> Blocks);

/// <summary>待审批工具调用请求。</summary>
public sealed record RemoteApprovalRequest(
    string ApprovalId,
    string ToolCallId,
    string ToolName,
    string ArgsJson,
    string? Summary,
    DateTimeOffset RequestedAtUtc);

/// <summary>审批决定的结果；Accepted 为 false 表示该审批不存在或已失效。</summary>
public sealed record RemoteApprovalResolution(
    string ApprovalId,
    string ToolCallId,
    RemoteApprovalDecision Decision,
    bool Accepted,
    string? Message);

/// <summary>会话状态与时间线的完整快照；手机首次连接或重连补齐时使用。</summary>
public sealed record RemoteSessionSnapshot(
    string SessionId,
    string Title,
    string Activity,
    string Connection,
    string? Model,
    bool CanSend,
    bool CanStop,
    int QueuedCount,
    IReadOnlyList<RemoteTimelineEntry> Entries,
    IReadOnlyList<RemoteApprovalRequest> PendingApprovals);

/// <summary>命令（发送/停止/审批）的受理结果。受理不等于执行完成。</summary>
