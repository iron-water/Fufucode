namespace Fufucode.Core.Automation;

/// <summary>
/// U1 persistent gatekeeping domain model. A thread is a graph node holding many tasks;
/// each task owns many delivery parts, and each part has versioned revisions.
/// Decisions are accept/rework/blocked/defer, recorded decisions are never overwritten,
/// and a partial rework never changes other parts of the same thread.
/// </summary>
public enum AutomationDecision
{
    Accept,
    Rework,
    Blocked,
    Defer
}

public enum DeliveryPartStatus
{
    Delivered,
    Accepted,
    ReworkRequested,
    Blocked,
    Deferred,
    /// <summary>This revision lineage was replaced by a newer revision after rework.</summary>
    Superseded
}

public enum AutomationTaskStatus
{
    Planned,
    Running,
    Delivered,
    PartiallyAccepted,
    Accepted,
    Rework,
    Blocked,
    Deferred
}

public enum ThreadStatus
{
    Active,
    Completed,
    /// <summary>Frozen when shared-M state or a delivery outcome is unknown; never auto-redeliver.</summary>
    Frozen
}

public enum ClaimState
{
    Claimed,
    /// <summary>Claim persisted but start/delivery outcome is unknown: crash, disconnect or timeout.</summary>
    Unknown,
    Resolved
}

public enum WriteLeaseKind
{
    TaskWriter,
    Integrator
}

public sealed record AutomationThread(
    string ThreadId,
    string ProjectId,
    string Harness,
    string Model,
    string Worktree,
    string? SavedSessionId,
    ThreadStatus Status,
    long CreatedUtc,
    string? FreezeReason);

public sealed record AutomationTask(
    string TaskId,
    string ThreadId,
    string Title,
    AutomationTaskStatus Status,
    string BaseSha,
    string? HeadSha,
    string? RunId,
    long CreatedUtc);

public sealed record DeliveryPart(
    string PartId,
    string TaskId,
    string Title,
    DeliveryPartStatus Status,
    long CurrentRevision,
    string? LastDecisionBy,
    long UpdatedUtc);

public sealed record DeliveryRevision(
    string RevisionId,
    string PartId,
    long RevisionNumber,
    string RunId,
    string HeadSha,
    string CardJson,
    long ReceivedUtc);

public sealed record PartDecision(
    string DecisionId,
    string PartId,
    long RevisionNumber,
    AutomationDecision Decision,
    string? Findings,
    string? EvidenceJson,
    string DecidedBy,
    long DecidedUtc);

public sealed record AutomationEvent(
    long Cursor,
    string EventId,
    string Type,
    string ThreadId,
    string? TaskId,
    string? PartId,
    string? RunId,
    string PayloadJson,
    long OccurredUtc);

public sealed record AutomationClaim(
    string ClaimId,
    string TaskId,
    string PartId,
    string RunId,
    ClaimState State,
    long ClaimedUtc,
    long? ResolvedUtc);

public sealed record WriteLease(
    string LeaseId,
    WriteLeaseKind Kind,
    string ScopeId,
    string HolderId,
    long AcquiredUtc,
    long ExpiresUtc)
{
    public bool IsExpired(long nowUtc) => nowUtc >= ExpiresUtc;
}

public sealed record AutomationCommand(
    string CommandId,
    string Type,
    string ThreadId,
    string? TaskId,
    string? PartId,
    string PayloadJson,
    long IssuedUtc);

public sealed record CommandResult(
    string CommandId,
    bool Applied,
    string? RejectReason);

/// <summary>
/// Versioned DAG edge. Unlocks depend on concrete parts and integration evidence,
/// never on a thread merely "looking done".
/// </summary>
public sealed record TaskDependency(
    string TaskId,
    string DependsOnTaskId,
    /// <summary>Required upstream part; null means all parts of the upstream task.</summary>
    string? DependsOnPartId,
    /// <summary>Only unlock after the upstream part is wired into shared integration.</summary>
    bool RequiresIntegration);
