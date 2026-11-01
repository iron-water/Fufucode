namespace Fufucode.Core.Automation.Rebirth;

/// <summary>Project-level autonomy. Observe records intent, Propose requires a human to authorize work,
/// WithinMandate lets the model create and run tickets inside the brief, Restricted only executes existing tickets.</summary>
public enum RebirthAutonomy { Observe, Propose, WithinMandate, Restricted }

public enum RebirthProjectStatus { Active, Paused, Completed, Archived }
public enum RebirthTicketStatus
{
    Proposed, Authorized, Active, WaitingPerson, WaitingDependency, WaitingResource,
    Review, ReplanRequired, NeedsReconciliation, HumanOwned, Done, Cancelled, Failed
}

public enum RebirthRunStatus { Queued, Running, Completed, Failed, Interrupted, Unknown }
public enum RebirthRunOutcome { Completed, Failed, Interrupted, Unknown }
public enum RebirthEvidenceKind { Artifact, Test, Review, HumanAcceptance, Note }
public enum RebirthDecisionKind { Veto, Constraint, Plan, Stop, Takeover, Acceptance, Replan, Gate }
public enum RebirthInputKind { Query, Suggestion, Evidence, Veto, ScopeChange, Stop, Takeover, Answer, NewGoal }
public enum RebirthInputState { Received, Delivered, Read, Applied, Failed, Unknown }
public enum RebirthAttentionStatus { Open, Answered, Withdrawn, Expired }

public enum RebirthTaskClass { AdvancementTask, ContinuousMonitor, UserGate, UserAction, Blocker }
public enum RebirthPriority { P0, P1, P2, P3, P4 }
public enum RebirthTurnRoute { ReadyForHost, UserActionRequired, Wait, Blocked, ReplanRequired, RepairRequired, ContractError }
public enum RebirthTurnResultKind { Progress, ValidatedProgress, ValidatedCompletion, RepairRequired, ReplanRequired, UserActionRequired, Wait, Failed, Unknown }
public enum RebirthLeaseMode { SoftClaim, HardLease }
public enum RebirthGateDecision { Approve, Reject, Defer }

/// <summary>The human-owned, versioned basis of a project. It is never edited in place.</summary>
public sealed record RebirthBrief(
    string Statement,
    string[] Objectives,
    string[] NonGoals,
    string[] Constraints,
    string[] Acceptance,
    string[] AllowedPaths,
    int BudgetMinutes,
    RebirthAutonomy Autonomy)
{
    /// <summary>0..1 automatic compute duty cycle. Zero is a project-level hard pause.</summary>
    public double ComputeQuota { get; init; } = 1.0;
    public static RebirthBrief Empty { get; } = new("", [], [], [], [], [], 0, RebirthAutonomy.Propose);
}

public sealed record RebirthBriefVersion(long Version, RebirthBrief Brief, string CreatedBy, long CreatedUtc);

public sealed record RebirthProject(
    string Id,
    string Title,
    RebirthProjectStatus Status,
    long BriefVersion,
    long Revision,
    long CreatedUtc,
    long UpdatedUtc);

public sealed record RebirthTicket(
    string Id,
    string ProjectId,
    string Title,
    string Goal,
    string[] Acceptance,
    string Route,
    RebirthTicketStatus Status,
    string Owner,
    long PlanVersion,
    string? MandateJson,
    long CreatedUtc,
    long UpdatedUtc)
{
    public RebirthTaskClass TaskClass { get; init; } = RebirthTaskClass.AdvancementTask;
    public RebirthPriority Priority { get; init; } = RebirthPriority.P1;
    public string? ResumeWhen { get; init; }
    public string? ValidationCommand { get; init; }
    public string AcceptanceDigest { get; init; } = "";
}

public sealed record RebirthRun(
    string Id,
    string TicketId,
    string Executor,
    string Model,
    string Workspace,
    RebirthRunStatus Status,
    long StartedUtc,
    long? EndedUtc,
    string? TerminalCode,
    string? Detail)
{
    public RebirthTurnResultKind? ResultKind { get; init; }
    public bool Settled { get; init; }
}

public sealed record RebirthEvidence(
    string Id,
    string TicketId,
    string? RunId,
    RebirthEvidenceKind Kind,
    string Criterion,
    string Reference,
    bool Passed,
    string Verifier,
    long CreatedUtc,
    bool Valid)
{
    public string AcceptanceDigest { get; init; } = "";
}

public sealed record RebirthDecision(
    string Id,
    string ProjectId,
    string? TicketId,
    RebirthDecisionKind Kind,
    string Actor,
    string Target,
    string Scope,
    string Text,
    long CreatedUtc,
    string Status);

public sealed record RebirthInput(
    string Id,
    string ProjectId,
    string? TicketId,
    string Actor,
    RebirthInputKind Kind,
    string Text,
    RebirthInputState State,
    long CreatedUtc,
    long? DeliveredUtc,
    long? ReadUtc,
    long? AppliedUtc);

public sealed record RebirthAttention(
    string Id,
    string ProjectId,
    string? TicketId,
    string Question,
    RebirthAttentionStatus Status,
    long CreatedUtc,
    long? AnsweredUtc,
    string? Answer);

public sealed record RebirthSnapshot(
    RebirthProject Project,
    RebirthBriefVersion Brief,
    IReadOnlyList<RebirthTicket> Tickets,
    IReadOnlyList<RebirthRun> Runs,
    IReadOnlyList<RebirthEvidence> Evidence,
    IReadOnlyList<RebirthDecision> Decisions,
    IReadOnlyList<RebirthInput> Inputs,
    IReadOnlyList<RebirthAttention> Attention)
{
    public RebirthQuotaState Quota { get; init; } = new(1.0, 0, false);
    public IReadOnlyList<RebirthLease> Leases { get; init; } = [];
}

public sealed record RebirthLease(
    string Id, string TicketId, string Owner, RebirthLeaseMode Mode,
    long Epoch, long ExpiresUtc, string Status);

public sealed record RebirthQuotaState(double ComputeQuota, long SpentSlots, bool Paused);

/// <summary>Deterministic admission for one ticket. It is a decision, not execution.</summary>
public sealed record RebirthTurnAdmission(
    bool ShouldRun, RebirthTurnRoute Route, string Reason,
    string? LeaseId = null, long? LeaseExpiresUtc = null);

public sealed record RebirthGateResult(RebirthDecision Decision, RebirthTicketStatus TicketStatus);

/// <summary>Input state returned by every mutation. A receipt is an acknowledgement, not proof of work.</summary>
public sealed record RebirthReceipt(string CommandId, long ProjectRevision, string ObjectId, string Status);

public sealed record RebirthVetoResult(RebirthDecision Decision, int TicketsReplanned);
