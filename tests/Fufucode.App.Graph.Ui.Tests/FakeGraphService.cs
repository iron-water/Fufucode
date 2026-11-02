using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Fufucode.App.Abstractions.Graph;

namespace Fufucode.App.Graph.Ui.Tests;

internal sealed class FakeGraphService : IGraphProjectService
{
    private Action? _changed;
    internal int Subscriptions, Mutations, AcceptCalls, RunCalls, StopCalls, ReadCalls, ContextCalls;
    internal bool Disposed, Reject, AsyncReject;
    internal int LiftVetoCalls, ReconcileCalls, AutonomyCalls;
    internal string? RecoveryTicketId, LiftVetoReason, RecoveryResolution;
    internal TaskCompletionSource? PendingRun;
    internal CancellationToken RunToken;
    internal GraphSnapshot Current;
    internal readonly Dictionary<long, GraphSnapshot> History = new();
    internal long? ContextRevision;
    internal string? ContextModule, ContextTicket;
    internal readonly List<GraphEntry> AddedEntries = new();
    internal FakeGraphService()
    {
        const long time = 1793577600000;
        var modules = new[]
        {
            new GraphModule("root", null, "Workspace", "project", new[] { "src/Workspace" }, time),
            new GraphModule("leaf", null, "External", "leaf root", Array.Empty<string>(), time),
            new GraphModule("a", "root", "Alpha", "module a", new[] { "src/Alpha.cs" }, time),
            new GraphModule("b", "root", "Beta", "module b", Array.Empty<string>(), time),
            new GraphModule("a1", "a", "Alpha child", "nested module", Array.Empty<string>(), time)
        };
        var ticket = new GraphTicket("t1", "First request", "Goal one", "Review", new[] { "a", "b" },
            new[] { "build", "review" }, null, time, time + 10);
        var second = new GraphTicket("t2", "Second request", "Goal two", "Authorized", new[] { "leaf" },
            new[] { "proof" }, null, time, time + 20);
        var revisions = new[] { new GraphRevision(1, "Created", time), new GraphRevision(2, "Added Alpha", time + 1),
            new GraphRevision(3, "Current", time + 2) };
        Current = new("project", "C:/fake-project", "Project title", "Project purpose", "Authorized", 3, false,
            modules, new[] { new GraphRelation("r1", "root", "leaf", "dependency", "parent edge"),
                new GraphRelation("r2", "a", "leaf", "dataflow", "shared input"),
                new GraphRelation("r3", "a", "b", "Import", "real kind") },
            new[] { ticket, second }, new[]
            { new GraphEntry("e1", "t1", "a", "message", "owner", "Please review", null, time + 1),
                new GraphEntry("e2", "t1", "a", "evidence", "executor", "Build log", "build.log", time + 2) },
            revisions, Array.Empty<GraphRun>());
        History[1] = Current with { Revision = 1, IsHistorical = true, Title = "Old project", Purpose = "Old purpose",
            Modules = new[] { modules[0] with { Name = "Old workspace" }, modules[1] },
            Relations = new[] { Current.Relations[0] }, Tickets = Array.Empty<GraphTicket>(), Entries = Array.Empty<GraphEntry>() };
        History[2] = Current with { Revision = 2, IsHistorical = true, Title = "Middle project",
            Modules = modules.Select(m => m.Id == "a" ? m with { Name = "Old Alpha" } : m).ToArray() };
    }
    public event Action? Changed
    { add { _changed += value; Subscriptions++; } remove { _changed -= value; Subscriptions--; } }
    internal void RaiseChanged() => _changed?.Invoke();
    internal void Replace(GraphSnapshot snapshot) { Current = snapshot; RaiseChanged(); }
    public void SetProject(string projectId, string projectPath) =>
        Replace(Current with { ProjectId = projectId, ProjectPath = projectPath });
    public GraphSnapshot ReadSnapshot(long? revision = null)
    { ReadCalls++; return revision is { } value ? History[value] : Current; }
    public GraphOptions ReadOptions() => new();
    private void Mutation()
    { if (Reject) throw new InvalidOperationException("authority rejected"); Mutations++; }
    public Task SaveOptionsAsync(GraphOptions options, CancellationToken cancellationToken = default)
    { Mutation(); RaiseChanged(); return Task.CompletedTask; }
    public Task ScanAsync(CancellationToken cancellationToken = default)
    { Mutation(); RaiseChanged(); return AsyncReject ? Task.FromException(new InvalidOperationException("scan failed")) : Task.CompletedTask; }
    public void SetPurpose(string purpose) { Mutation(); Replace(Current with { Purpose = purpose }); }
    public GraphModule UpsertModule(string? id, string? parentId, string name, string summary, string[] sourcePaths)
    {
        Mutation(); var module = new GraphModule(id ?? "created", parentId, name, summary, sourcePaths, 0);
        Replace(Current with { Modules = Current.Modules.Where(m => m.Id != module.Id).Append(module).ToArray() }); return module;
    }
    public void RemoveModule(string moduleId)
    { Mutation(); Replace(Current with { Modules = Current.Modules.Where(m => m.Id != moduleId).ToArray() }); }
    public GraphRelation UpsertRelation(string? id, string sourceId, string targetId, string kind, string label)
    {
        Mutation(); var relation = new GraphRelation(id ?? "new-relation", sourceId, targetId, kind, label);
        Replace(Current with { Relations = Current.Relations.Where(r => r.Id != relation.Id).Append(relation).ToArray() }); return relation;
    }
    public void RemoveRelation(string relationId)
    { Mutation(); Replace(Current with { Relations = Current.Relations.Where(r => r.Id != relationId).ToArray() }); }
    public GraphTicket CreateTicket(string title, string goal, string[] moduleIds, string[] acceptance)
    {
        Mutation(); var ticket = new GraphTicket("created-ticket", title, goal, "Authorized", moduleIds, acceptance, null, 0, 0);
        Replace(Current with { Tickets = Current.Tickets.Append(ticket).ToArray() }); return ticket;
    }
    public void AddEntry(string? ticketId, string? moduleId, string kind, string text, string? reference = null)
    {
        Mutation(); var entry = new GraphEntry("new-" + AddedEntries.Count, ticketId, moduleId, kind, "user", text, reference, 0);
        AddedEntries.Add(entry); Replace(Current with { Entries = Current.Entries.Append(entry).ToArray() });
    }
    public void LinkSession(string ticketId, string sessionId)
    { Mutation(); Replace(Current with { Tickets = Current.Tickets.Select(t => t.Id == ticketId ? t with { SessionId = sessionId } : t).ToArray() }); }
    public string BuildContext(string? moduleId, string? ticketId, long? revision = null)
    { ContextCalls++; ContextRevision = revision; ContextModule = moduleId; ContextTicket = ticketId; return "snapshot context"; }
    public Task RunTicketAsync(string ticketId, CancellationToken cancellationToken = default)
    {
        Mutation(); RunCalls++; RunToken = cancellationToken; RaiseChanged();
        return PendingRun != null ? PendingRun.Task.WaitAsync(cancellationToken) :
            AsyncReject ? Task.FromException(new InvalidOperationException("run failed")) : Task.CompletedTask;
    }
    public Task StopTicketAsync(string ticketId, CancellationToken cancellationToken = default)
    { Mutation(); StopCalls++; RaiseChanged(); return Task.CompletedTask; }
    public Task SetAutonomyAsync(bool running, CancellationToken cancellationToken = default)
    { Mutation(); AutonomyCalls++; RaiseChanged(); return Task.CompletedTask; }
    public Task StopAllAsync(CancellationToken cancellationToken = default) { Mutation(); RaiseChanged(); return Task.CompletedTask; }
    public void VetoTicket(string ticketId, string reason)
    { Mutation(); Replace(Current with { Tickets = Current.Tickets.Select(t => t.Id == ticketId ? t with { Status = "Cancelled" } : t).ToArray() }); }
    public void LiftVeto(string ticketId, string reason)
    {
        Mutation(); LiftVetoCalls++; RecoveryTicketId = ticketId; LiftVetoReason = reason;
        Replace(Current with { Entries = Current.Entries.Select(e => e.Kind == "Veto" && e.TicketId == ticketId ? e with
            { Text = System.Text.Json.JsonSerializer.Serialize(new { status = "Lifted", target = "ticket:" + ticketId }) } : e).ToArray() });
    }
    public void ReconcileTicket(string ticketId, string resolution)
    {
        Mutation(); ReconcileCalls++; RecoveryTicketId = ticketId; RecoveryResolution = resolution;
        Replace(Current with { Tickets = Current.Tickets.Select(t => t.Id == ticketId ? t with { Status = "Authorized" } : t).ToArray() });
    }
    public void AcceptTicket(string ticketId, string evidenceReference)
    { Mutation(); AcceptCalls++; Replace(Current with { Tickets = Current.Tickets.Select(t => t.Id == ticketId ? t with { Status = "Done" } : t).ToArray() }); }
    public void Dispose() => Disposed = true;
}
