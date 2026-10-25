using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Fufucode.App.Abstractions;

/// <summary>连接器运行状态。Degraded 表示传输可用但入站/出站语义尚未完全映射。</summary>
public enum CompanionConnectorState
{
    Stopped,
    Starting,
    Running,
    Degraded,
    Faulted,
}

/// <summary>连接器声明的能力。不做能力推断，由连接器自己如实申报。</summary>
public sealed record CompanionCapabilities(
    bool PublishesFacts,
    bool PublishesEvents,
    bool AcceptsIntents,
    bool HandlesApproval,
    bool TransfersFiles);

/// <summary>
/// 规范事实：某个聚合在某一版本的权威状态。
/// 只有 Owner 能发布；其他端持有的都是投影。
/// </summary>
public sealed record CanonicalFact(
    string FactId,
    string Owner,
    string Aggregate,
    long Revision,
    DateTimeOffset ObservedAt,
    int StaleAfterMs,
    string Confidence,
    string PayloadJson);

/// <summary>规范事件：事实变化的不可变通知。Event 不是 Fact。</summary>
public sealed record CanonicalEvent(
    string EventId,
    string Owner,
    string Aggregate,
    long Revision,
    string Type,
    DateTimeOffset OccurredAt,
    string? CorrelationId,
    string Sensitivity,
    string PayloadJson);

/// <summary>
/// 规范意图：请求目标域改变状态或执行动作。
/// 意图不是命令成功；目标端负责鉴权、确认、幂等。
/// </summary>
public sealed record CanonicalIntent(
    string IntentId,
    string IdempotencyKey,
    string Source,
    string Target,
    string Type,
    DateTimeOffset? ExpiresAt,
    bool RequiresConfirmation,
    string PayloadJson);

/// <summary>规范回执：目标端给出的最终结果。Accepted 不等于 Succeeded。</summary>
public static class CompanionReceiptStatus
{
    public const string Accepted = "accepted";
    public const string Executing = "executing";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Rejected = "rejected";
    public const string Expired = "expired";
    public const string Duplicate = "duplicate";
    public const string Unknown = "unknown";
}

public sealed record CanonicalReceipt(
    string ReceiptId,
    string? IntentId,
    string Target,
    string Status,
    DateTimeOffset At,
    string? EvidenceJson = null,
    string? ErrorCode = null);

/// <summary>连接器状态投影（核心对外暴露）。</summary>
public sealed record CompanionConnectorStatus(
    string ConnectorId,
    CompanionConnectorState State,
    CompanionCapabilities Capabilities,
    long ReceivedEvents,
    long SentEvents,
    string? LastError);

/// <summary>
/// 一个传输连接器。只负责协议适配：把本端协议映射为 Canonical，把 Canonical 交给传输。
/// 不得自己保存业务状态，也不得绕过 <see cref="ICompanionCore"/> 直接调用会话。
/// </summary>
public interface ICompanionConnector
{
    string ConnectorId { get; }

    CompanionCapabilities Capabilities { get; }

    CompanionConnectorState State { get; }

    /// <summary>核心把其它连接器发布的事件投递到这里。实现方不得假定线程。</summary>
    Task DeliverAsync(CanonicalEvent evt, CancellationToken cancellationToken = default);
}

/// <summary>可选：连接器能接收意图并返回回执。</summary>
public interface ICompanionIntentSink
{
    Task<CanonicalReceipt> HandleIntentAsync(CanonicalIntent intent, CancellationToken cancellationToken = default);
}

/// <summary>
/// 协同核心：唯一的路由、去重与审计点。
/// 不拥有业务真源，不替目标端做领域决定。
/// </summary>
public interface ICompanionCore
{
    void RegisterConnector(ICompanionConnector connector);

    void UnregisterConnector(string connectorId);

    /// <summary>发布事实。只有事实的 Owner 可以发布。</summary>
    void Publish(CanonicalFact fact);

    /// <summary>发布事件。按 EventId 去重，投递给除 Owner 外的连接器。</summary>
    void Publish(CanonicalEvent evt);

    /// <summary>提交意图。按幂等键去重，交给目标连接器。</summary>
    Task<CanonicalReceipt> SubmitIntentAsync(CanonicalIntent intent, CancellationToken cancellationToken = default);

    IReadOnlyList<CompanionConnectorStatus> Status { get; }
}
