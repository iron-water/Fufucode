namespace Fufucode.App.Abstractions.Graph;

public interface IGraphOrganizationService
{
    GraphOrganizationSnapshot SendOrganizationMessage(string commandId, GraphMessageDraft message, GraphExecutionIdentity? hostIdentity = null);
}

public sealed record GraphValidationProposal(string TicketId, string RunId, string Executable,
    string[] Arguments, int TimeoutSeconds, string[] CoveredCriteria, string Reason);

public sealed record GraphResponsibility(string Id, GraphExecutionRole Role, string[] ModuleIds, string? TicketId = null);
public enum GraphMessageStatus { Received, Delivered, Read, Applied, Rejected }
public sealed record GraphMessageDraft(string RecipientId, bool HumanRecipient, string Kind, string Text, string? TicketId, string? ModuleId, long Revision);
public sealed record GraphOrganizationMessage(long Sequence, string Id, string SenderId, GraphMessageDraft Content, GraphMessageStatus Status, string? Resolution);
public sealed record GraphWorkDependency(string TicketId, string DependsOnTicketId);
public sealed record GraphCompletionBarrier(string TicketId, long Watermark, bool RequiresReview);
public sealed record GraphOrganizationSnapshot(string ProjectId, long Version, GraphResponsibility[] Responsibilities, GraphWorkDependency[] Dependencies, GraphOrganizationMessage[] Messages, GraphCompletionBarrier[] Barriers);
