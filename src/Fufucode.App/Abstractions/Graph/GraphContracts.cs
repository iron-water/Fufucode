using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Fufucode.App.Abstractions.Graph;

public sealed record GraphModule(string Id, string? ParentId, string Name, string Summary,
    string[] SourcePaths, long UpdatedUtc);
public sealed record GraphRelation(string Id, string SourceId, string TargetId, string Kind, string Label);
public sealed record GraphTicket(string Id, string Title, string Goal, string Status,
    string[] ModuleIds, string[] Acceptance, string? SessionId, long CreatedUtc, long UpdatedUtc);
public sealed record GraphEntry(string Id, string? TicketId, string? ModuleId, string Kind,
    string Actor, string Text, string? Reference, long CreatedUtc);
public sealed record GraphRevision(long Version, string Summary, long CreatedUtc);
public sealed record GraphRun(string Id, string TicketId, string Status, string? SessionId,
    string? Detail, long StartedUtc, long? EndedUtc);
public sealed record GraphSnapshot(string ProjectId, string ProjectPath, string Title, string Purpose,
    string ProjectStatus, long Revision, bool IsHistorical, IReadOnlyList<GraphModule> Modules,
    IReadOnlyList<GraphRelation> Relations, IReadOnlyList<GraphTicket> Tickets,
    IReadOnlyList<GraphEntry> Entries, IReadOnlyList<GraphRevision> Revisions,
    IReadOnlyList<GraphRun> Runs);

public sealed record GraphOptions
{
    public bool Enabled { get; init; } = true;
    public bool AutoAdvance { get; init; }
    public int Concurrency { get; init; } = 1;
    public int BudgetMinutes { get; init; } = 60;
    public double ComputeQuota { get; init; } = 1;
    public string Provider { get; init; } = "";
    public string Model { get; init; } = "";
    public int ScanDepth { get; init; } = 6;
    public int ValidationTimeoutSeconds { get; init; } = 120;
}

public sealed record GraphRunRequest(string RunId, string TicketId, string ProjectId,
    string ProjectPath, string Prompt, GraphOptions Options, string? SessionId = null);
public sealed record GraphRunResult(string Status, string Output, string? Error = null,
    string? SessionId = null);

public interface IGraphRunExecutor
{
    Task<GraphRunResult> ExecuteAsync(GraphRunRequest request, Action<string>? progress,
        CancellationToken cancellationToken);
}

/// <summary>Project authority. Views submit commands and consume versioned snapshots.</summary>
public interface IGraphProjectService : IDisposable
{
    event Action? Changed;
    void SetProject(string projectId, string projectPath);
    GraphSnapshot ReadSnapshot(long? revision = null);
    GraphOptions ReadOptions();
    Task SaveOptionsAsync(GraphOptions options, CancellationToken cancellationToken = default);
    Task ScanAsync(CancellationToken cancellationToken = default);
    void SetPurpose(string purpose);
    GraphModule UpsertModule(string? id, string? parentId, string name, string summary, string[] sourcePaths);
    void RemoveModule(string moduleId);
    GraphRelation UpsertRelation(string? id, string sourceId, string targetId, string kind, string label);
    void RemoveRelation(string relationId);
    GraphTicket CreateTicket(string title, string goal, string[] moduleIds, string[] acceptance);
    void AddEntry(string? ticketId, string? moduleId, string kind, string text, string? reference = null);
    void LinkSession(string ticketId, string sessionId);
    string BuildContext(string? moduleId, string? ticketId, long? revision = null);
    Task RunTicketAsync(string ticketId, CancellationToken cancellationToken = default);
    Task StopTicketAsync(string ticketId, CancellationToken cancellationToken = default);
    Task SetAutonomyAsync(bool running, CancellationToken cancellationToken = default);
    Task StopAllAsync(CancellationToken cancellationToken = default);
    void VetoTicket(string ticketId, string reason);
    void LiftVeto(string ticketId, string reason);
    void ReconcileTicket(string ticketId, string resolution);
    void AcceptTicket(string ticketId, string evidenceReference);
}
