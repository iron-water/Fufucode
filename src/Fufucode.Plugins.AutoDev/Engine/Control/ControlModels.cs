using System.Text.Json.Serialization;

namespace Fufucode.Core.Automation;

public enum DevelopmentMode { Manual, SemiAutomatic, Automatic }
public enum ControlAction { DefinePackage, ApprovePackage, SetMode }

/// <summary>
/// The one place that decides whether a mode is allowed to turn an approved plan into real
/// execution. Manual never dispatches. SemiAutomatic dispatches with a human on every
/// review/integration. Automatic dispatches under the project's delegated scope, where the
/// AutoAdvancementEngine makes those same decisions inside the recorded authorization.
/// Dispatch authority itself is therefore identical for the two autonomous modes; only the
/// decision maker differs.
/// </summary>
public static class ControlExecutionMode
{
    public static bool AllowsDispatch(DevelopmentMode mode) => mode != DevelopmentMode.Manual;
    public static bool AllowsDispatch(string persistedMode) =>
        Enum.TryParse<DevelopmentMode>(persistedMode, out var mode) && AllowsDispatch(mode);
}

/// <summary>A versioned plan, not a registered thread, task or proof of execution.</summary>
public sealed record WorkPackageSpec(string Id, string Title, string Goal, string[] Scope,
    string[] Acceptance, string Harness, string Model, string Artifact, string[] DependsOn)
{
    // Omit the absent extension to preserve hashes of persisted pre-extension commands.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? KnowledgeRefs { get; init; }
}

public sealed record WorkPackageVersion(WorkPackageSpec Spec, long Version, bool Approved)
{
    public IReadOnlyDictionary<string, long> AdoptedKnowledge { get; init; } = new Dictionary<string, long>();
    public bool Superseded { get; init; }
}

/// <summary>The authenticated caller supplies a human decision; adapter reports are a separate API.</summary>
public sealed record ControlCommand(string CommandId, long ExpectedRevision, string DecidedBy,
    ControlAction Action, WorkPackageSpec? Package = null, string? PackageId = null,
    long? ExpectedPackageVersion = null, DevelopmentMode? Mode = null);

public sealed record ControlReceipt(string CommandId, long Revision, DevelopmentMode Mode,
    string? PackageId, long? PackageVersion);

/// <summary>Human review of one stopped delivery. Never dispatches work or records integration.</summary>
public sealed record ControlReviewCommand(string CommandId, long ExpectedRevision, string DecidedBy,
    string TaskId, string PartId, long ExpectedPartRevision, string ExpectedRunId,
    string ExpectedHeadSha, string ExpectedEvidenceToken, [property: JsonRequired] AutomationDecision Decision,
    string? Findings, string EvidenceJson);

public sealed record ControlReviewReceipt(string CommandId, long Revision, string TaskId,
    string PartId, long PartRevision, string RunId, string HeadSha, AutomationDecision Decision);

/// <summary>Reconsider unchanged blocked/deferred evidence. Does not retry its producer.</summary>
public sealed record ControlReconsiderCommand(string CommandId, long ExpectedRevision, string DecidedBy,
    string TaskId, string PartId, long ExpectedPartRevision, string ExpectedRunId,
    string ExpectedHeadSha, string ExpectedEvidenceToken, string ExpectedDecisionId, string Reason);

public sealed record ControlReconsiderReceipt(string CommandId, long Revision, string TaskId,
    string PartId, long PartRevision, string PreviousDecisionId);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ControlRepairCommand(string CommandId, long ExpectedRevision, string DecidedBy,
    string TaskId, string PartId, long ExpectedPartRevision, string ExpectedRunId, string ExpectedHeadSha,
    string ExpectedDecisionId, string ExpectedEvidenceToken, string Reason);
public sealed record ControlRepairReceipt(string CommandId, long Revision, string RepairId, string Status);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ControlRepairBinding(string CommandId, long ExpectedRevision, string RepairId);
public sealed record ControlRepairQueueReceipt(string CommandId, long Revision, string RepairId,
    string RunId, string ThreadId, string TaskId, string PartId);
public sealed record ControlRepairState(string Id, string PackageId, long PackageVersion, string TaskId,
    string ThreadId, string PartId, long PartRevision, string DecisionId, string SourceRunId,
    string HeadSha, string RequestedBy, string Reason, string Status, string? RunId)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? BlockingReason { get; init; }
}

/// <summary>Only a planning intent. No run/session/worktree is invented before trusted binding.</summary>
public sealed record ControlIntent(string Id, string PackageId, long PackageVersion,
    string Status, long CreatedRevision);

public sealed record ControlSnapshot(long Revision, DevelopmentMode Mode,
    IReadOnlyList<WorkPackageVersion> Packages, IReadOnlyList<ControlIntent> Intents)
{
    public ControlKnowledgeSnapshot Knowledge { get; init; } = new([], [], []);
    public IReadOnlyList<ControlRestructureState> Restructures { get; init; } = [];
    public IReadOnlyList<ControlIntegrationAuthorization> IntegrationAuthorizations { get; init; } = [];
    public IReadOnlyList<ControlRepairState> Repairs { get; init; } = [];
}

/// <summary>Trusted runtime assignment, never a client-supplied prompt or execution report.</summary>
public sealed record ControlExecutionBinding(string CommandId, long ExpectedRevision, string IntentId,
    long PackageVersion, string ThreadId, string TaskId,
    Fufucode.App.Abstractions.Automation.Harnesses.HarnessRuntime Runtime, string? Provider = null);

/// <summary>A durable queue acknowledgment. It proves neither execution nor delivery.</summary>
public sealed record ControlQueueReceipt(string CommandId, long Revision, string IntentId,
    string RunId, string ThreadId, string TaskId);

/// <summary>Explicit history capture; no legacy run or decision is promoted to execution authority.</summary>
public sealed record LegacyControlArchiveCommand(string CommandId, long ExpectedRevision, string DecidedBy,
    string SourceId, long SourceRevision, string ContentSha256, string Json);

public sealed record LegacyControlArchive(string SourceId, long SourceRevision, string ContentSha256,
    string Json, string DecidedBy, long CapturedUtc);

public sealed record ControlArchiveReceipt(string CommandId, long Revision, string SourceId,
    long SourceRevision, string ContentSha256, string Status);

/// <summary>Current control plan, U1 event cursor and separately labelled legacy claims in one read transaction.</summary>
public sealed record ControlShadowExport(string Format, ControlSnapshot Control, long EventCursor,
    IReadOnlyList<LegacyControlArchive> LegacyHistory)
{
    public IReadOnlyList<ControlGroupState> Groups { get; init; } = [];
}

public enum ControlRelation { Independent, Competitive, Complementary, Pipeline }
public enum ControlRelationAction { DefineGroup, SelectWinner }
public sealed record ControlGroupSpec(string Id, ControlRelation Relation, string[] Members,
    string? Baseline = null, string[]? Criteria = null);
public sealed record ControlRelationCommand(string CommandId, long ExpectedRevision, string DecidedBy,
    ControlRelationAction Action, ControlGroupSpec? Group = null, string? GroupId = null,
    string? Winner = null, string? ExpectedEvidenceToken = null);
public sealed record ControlRelationReceipt(string CommandId, long Revision, string GroupId, string? Winner);
public sealed record ControlPackageOutcome(string PackageId, long Version, bool Approved, string? TaskId,
    string EvidenceToken, bool Accepted, bool Integrated);
public sealed record ControlGroupState(ControlGroupSpec Spec, string? Winner, bool SelectionCurrent,
    IReadOnlyList<ControlPackageOutcome> Members)
{
    public bool Superseded { get; init; }
}

public enum ControlKnowledgeAction { Propose, Publish, RejectProposal, Ask, Answer, Adopt }
/// <summary>Human-recorded knowledge decision or directed question; adoption versions a stopped contract, never executes it.</summary>
public sealed record ControlKnowledgeCommand(string CommandId, long ExpectedRevision, string DecidedBy,
    ControlKnowledgeAction Action, string? Key = null, string? Text = null, string? Source = null,
    long? ExpectedKnowledgeVersion = null, string? ProposalId = null, string? Reason = null,
    string? FromPackage = null, string? ToPackage = null, long? ExpectedFromVersion = null,
    long? ExpectedToVersion = null, string? QuestionId = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? PackageId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public long? ExpectedPackageVersion { get; init; }
}
public sealed record ControlKnowledgeReceipt(string CommandId, long Revision, string ObjectId,
    string Status, long? KnowledgeVersion)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public long? PackageVersion { get; init; }
}
public sealed record ControlKnowledgeProposal(string Id, string Key, long PreviousVersion,
    string Text, string Source, string ProposedBy, string Status, string? DecidedBy, string? Reason);
public sealed record ControlKnowledgeFact(string Key, long Version, string Text, string Source,
    string ProposalId, string PublishedBy);
public sealed record ControlQuestionState(string Id, string FromPackage, long FromVersion,
    string ToPackage, long ToVersion, string Text, string AskedBy, string? Answer,
    string? AnsweredBy, bool ContractCurrent);
/// <summary>Approved facts remain separate from plans; publication never changes a running prompt.</summary>
public sealed record ControlKnowledgeSnapshot(IReadOnlyList<ControlKnowledgeProposal> Proposals,
    IReadOnlyList<ControlKnowledgeFact> Facts, IReadOnlyList<ControlQuestionState> Questions)
{
    public IReadOnlyList<ControlKnowledgeAdoption> Adoptions { get; init; } = [];
}
public sealed record ControlKnowledgeAdoption(string PackageId, long PackageVersion, string Key,
    long KnowledgeVersion, string Status, string? DecidedBy, string? BlockedReason);

public enum ControlRestructureAction { Propose, Approve, Reject }
/// <summary>Explicit goal provenance; a successor does not inherit execution, acceptance or integration.</summary>
public sealed record ControlGoalMapping(string SourceId, string[] SuccessorIds, string Reason);
public sealed record ControlGroupMapping(string SourceGroupId, string[] SuccessorGroupIds, string Reason);
public sealed record ControlRestructureSpec(IReadOnlyDictionary<string, long> Sources,
    WorkPackageSpec[] Replacements, ControlGoalMapping[] GoalMappings,
    ControlGroupSpec[] Groups, ControlGroupMapping[] GroupMappings, string Reason);
public sealed record ControlRestructureCommand(string CommandId, long ExpectedRevision, string DecidedBy,
    ControlRestructureAction Action, ControlRestructureSpec? Spec = null, string? ProposalId = null, string? Reason = null);
public sealed record ControlRestructureReceipt(string CommandId, long Revision, string ProposalId, string Status);
public sealed record ControlRestructureState(string Id, ControlRestructureSpec Spec,
    IReadOnlyList<WorkPackageVersion> SourceContracts, string Status, string ProposedBy, string? DecidedBy,
    string? DecisionReason, bool ContractCurrent, string? BlockedReason);

public enum ControlIntegrationAction { Authorize, Revoke }
public sealed record ControlIntegrationCommand(string CommandId, long ExpectedRevision, string DecidedBy,
    ControlIntegrationAction Action, string PackageId, long ExpectedPackageVersion, string ExpectedEvidenceToken,
    string? AuthorizationId = null, string? Reason = null);
public sealed record ControlIntegrationReceipt(string CommandId, long Revision, string AuthorizationId, string Status);
public sealed record ControlIntegrationAuthorization(string Id, string PackageId, long PackageVersion,
    string TaskId, string EvidenceToken, string ContextJson, string AuthorizedBy, string Status,
    string? RevokedBy, string? Reason, bool ContractCurrent);
public sealed record ControlIntegrationCandidate(WorkPackageVersion Package, string TaskId, string EvidenceToken,
    string ContextJson, bool Eligible, bool Authorized, bool Integrated);
/// <summary>Read-only projection, not another decision store. Context is evidence data, never executable instructions.</summary>
public sealed record ControlDecisionItem(string Id, string Kind, string ObjectId, long? Version,
    string EvidenceToken, bool Actionable, string? BlockedReason, string[] Options, string CommandType, string ContextJson);
public sealed record ControlInboxSnapshot(long Revision, long EventCursor, DevelopmentMode Mode,
    IReadOnlyList<ControlDecisionItem> Items);

public sealed record ControlClientSnapshot(string Format, ControlShadowExport State, ControlInboxSnapshot Inbox)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AuthorityIdentity { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ControlGraphSnapshot? Graph { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ControlRunningExecution>? RunningExecutions { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ControlRunStopState>? RunStops { get; init; }
}
public sealed record ControlRunStopState(string CommandId, string RunId, string ThreadId, string TaskId,
    string RequestedBy, string Reason, string Status);

public sealed record ControlStopReconciliation(long Revision, string RunId, string ThreadId, string TaskId,
    string StopCommandId, string EvidenceToken, string? ObservedHead, bool? TrackedClean,
    bool CanInterrupt, string Detail)
{
    public string? Worktree { get; init; }
}
public sealed record ControlStopResolutionCommand(string CommandId, long ExpectedRevision, string DecidedBy,
    string RunId, string ThreadId, string TaskId, string StopCommandId, string ExpectedEvidenceToken,
    string ExpectedHead, string Reason, string Evidence);
public sealed record ControlStopResolutionReceipt(string CommandId, long Revision, string RunId, string Status);
public sealed record ControlStopRunCommand(string CommandId, long ExpectedRevision, string DecidedBy,
    string RunId, string ThreadId, string TaskId, string ExpectedEvidenceToken, string Reason);
public sealed record ControlStopRunReceipt(string CommandId, long Revision, string RunId, string Status);
public sealed record ControlRunningExecution(string RunId, string ThreadId, string TaskId, string HostId,
    int SupervisorPid, long SupervisorStartUtc, int WriterPid, long WriterStartUtc, string EvidenceToken,
    string? StopCommandId, string? StopStatus);
public sealed record ControlGraphSnapshot(IReadOnlyList<ControlThreadNode> Threads, IReadOnlyList<ControlDependencyEdge> Edges);
public sealed record ControlThreadNode(string ThreadId, string ProjectId, string Harness, string Model, ThreadStatus Status,
    bool Running, IReadOnlyList<ControlTaskNode> Tasks);
public sealed record ControlTaskNode(string TaskId, string Title, AutomationTaskStatus Status, IReadOnlyList<ControlPartNode> Parts);
public sealed record ControlPartNode(string PartId, string Title, DeliveryPartStatus Status, long Revision, string? RunId, string? HeadSha, bool Integrated);
public sealed record ControlDependencyEdge(string SourceTaskId, string TargetTaskId, string? SourcePartId, bool RequiresIntegration);
