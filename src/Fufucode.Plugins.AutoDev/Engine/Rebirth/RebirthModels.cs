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
public enum RebirthDecisionKind { Veto, Constraint, Plan, Stop, Takeover, Acceptance, Replan }
public enum RebirthInputKind { Query, Suggestion, Evidence, Veto, ScopeChange, Stop, Takeover, Answer, NewGoal }
public enum RebirthInputState { Received, Delivered, Read, Applied, Failed, Unknown }
public enum RebirthAttentionStatus { Open, Answered, Withdrawn, Expired }

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
    long UpdatedUtc);

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
    string? Detail);

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
    bool Valid);

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
    IReadOnlyList<RebirthAttention> Attention);

/// <summary>Input state returned by every mutation. A receipt is an acknowledgement, not proof of work.</summary>
public sealed record RebirthReceipt(string CommandId, long ProjectRevision, string ObjectId, string Status);

public sealed record RebirthVetoResult(RebirthDecision Decision, int TicketsReplanned);
