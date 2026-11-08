using Fufucode.App.Abstractions.Graph;
using Fufucode.Core.Automation.Rebirth;
using static Fufucode.Plugins.AutoDev.Graph.GraphStorage;

namespace Fufucode.Plugins.AutoDev.Graph;

public sealed partial class GraphProjectService
{
    private static GraphResponsibility? ResolveScopeEventLead(GraphSnapshot graph, GraphOrganizationSnapshot organization, string ticketId)
    {
        var modules = graph.Tickets.SingleOrDefault(t => t.Id == ticketId)?.ModuleIds ?? [];
        if (modules.Length == 0) return null;
        bool Covers(GraphResponsibility lead, string moduleId)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (string? current = moduleId; current is not null && seen.Add(current); current = graph.Modules.FirstOrDefault(m => m.Id == current)?.ParentId)
                if (lead.ModuleIds.Contains(current)) return true;
            return false;
        }
        var candidates = organization.Responsibilities.Where(r => r.Role == GraphExecutionRole.ScopeLead && modules.All(m => Covers(r, m)))
            .Select(r => new { Lead = r, Breadth = graph.Modules.Count(m => Covers(r, m.Id)) }).OrderBy(r => r.Breadth).ToArray();
        return candidates.Length == 0 || candidates.Length > 1 && candidates[0].Breadth == candidates[1].Breadth ? null : candidates[0].Lead;
    }

    /// <summary>Local coalesced fault lanes. Ordinary facts/notifications and coordinator terminal events never wake a model.</summary>
    public void ReconcileScopeCycles(string projectPath) => InProjectScope(projectPath, () =>
    {
        var context = Current;
        Change(context, "Reconciled scoped blocking events", (c, tx) =>
        {
            Execute(c, tx, """
                CREATE TABLE IF NOT EXISTS graph_scope_cycle_events(
                    project_id TEXT NOT NULL, recipient_id TEXT NOT NULL, event_key TEXT NOT NULL,
                    source_ticket TEXT NOT NULL, payload TEXT NOT NULL, coordination_ticket TEXT,
                    PRIMARY KEY(project_id,recipient_id,event_key));
                """);
            var authority = context.Kernel.ReadSnapshot(context.AuthorityId);
            var graph = ReadSnapshot(); var organization = Organization(context, c, tx);
            var options = Options(context, c, tx);
            foreach (var run in authority.Runs.Where(r => r.Status is RebirthRunStatus.Failed or RebirthRunStatus.Interrupted or RebirthRunStatus.Unknown))
            {
                var ticket = authority.Tickets.Single(t => t.Id == run.TicketId);
                if (ticket.Route is "scope-planning" or "project-planning") continue;
                var lead = ResolveScopeEventLead(graph, organization, ticket.Id);
                if (lead is null) continue;
                Execute(c, tx, "INSERT OR IGNORE INTO graph_scope_cycle_events VALUES($p,$recipient,$key,$ticket,$payload,NULL)",
                    ("$p", context.AuthorityId), ("$recipient", lead.Id), ("$key", "run:" + run.Id), ("$ticket", ticket.Id),
                    ("$payload", Serialize(new { run.Id, run.Status, run.Detail })));
            }
            if (!options.Enabled || !options.AutoAdvance || authority.Brief.Brief.Autonomy != RebirthAutonomy.WithinMandate ||
                RemainingExecutionBudget() <= TimeSpan.Zero) return true;
            var pending = Query(c, tx, "SELECT recipient_id,event_key,source_ticket,payload FROM graph_scope_cycle_events WHERE project_id=$p AND coordination_ticket IS NULL",
                r => (Recipient: r.GetString(0), Key: r.GetString(1), Ticket: r.GetString(2), Payload: r.GetString(3)), ("$p", context.AuthorityId));
            foreach (var lane in pending.GroupBy(e => e.Recipient))
            {
                var lead = organization.Responsibilities.SingleOrDefault(r => r.Id == lane.Key && r.Role == GraphExecutionRole.ScopeLead);
                if (lead is null || authority.Tickets.Any(t => t.Route == "scope-planning" && t.Owner == lead.Id && t.Status is RebirthTicketStatus.Active or RebirthTicketStatus.Authorized or RebirthTicketStatus.Proposed)) continue;
                // Unknown effects require deterministic/human reconciliation, not a new speculative executor run.
                var actionable = lane.Where(e => !authority.Runs.Any(r => "run:" + r.Id == e.Key && r.Status == RebirthRunStatus.Unknown)).ToArray();
                if (actionable.Length == 0) continue;
                var plan = context.Kernel.CreateTicket(CommandId(), context.AuthorityId, "Review local blocking work",
                    "Review these scoped, persisted failures and decide the necessary local repair or replan. Delegate ordinary implementation repair to an Executor; preserve original acceptance and unknown effects. Events: " + Serialize(actionable),
                    ["Record a scoped response to the exact blocking event batch without replaying unknown effects."], "scope-planning", lead.Id, Now());
                foreach (var module in lead.ModuleIds)
                    Execute(c, tx, "INSERT OR IGNORE INTO graph_ticket_modules VALUES($ticket,$module)", ("$ticket", plan.Id), ("$module", module));
                foreach (var item in actionable)
                    Execute(c, tx, "UPDATE graph_scope_cycle_events SET coordination_ticket=$plan WHERE project_id=$p AND recipient_id=$recipient AND event_key=$key",
                        ("$plan", plan.Id), ("$p", context.AuthorityId), ("$recipient", lead.Id), ("$key", item.Key));
            }
            return true;
        });
        KickQueue(context);
        return true;
    });
}
