using Fufucode.App.Abstractions.Graph;
using Fufucode.Core.Automation.Rebirth;
using static Fufucode.Plugins.AutoDev.Graph.GraphStorage;

namespace Fufucode.Plugins.AutoDev.Graph;

public sealed partial class GraphProjectService
{
    public void RequireDeliveryAuthorization(string ticketId)
    {
        lock (_gate)
        {
            var context = Current;
            var authority = context.Kernel.ReadSnapshot(context.AuthorityId);
            var ticket = authority.Tickets.Single(t => t.Id == ticketId);
            if (ticket.Status is not (RebirthTicketStatus.Review or RebirthTicketStatus.Done) ||
                authority.Project.Status != RebirthProjectStatus.Active ||
                authority.Decisions.Any(d => d.Status == "Active" && d.Kind == RebirthDecisionKind.Veto &&
                    (d.TicketId == ticketId || d.Target == "ticket:" + ticketId || d.Target == "project:" + context.AuthorityId)))
                throw new UnauthorizedAccessException("Delivery blocked by current ticket or project control.");
        }
    }

    public void RecordIntegratedVersion(string ticketId, string runId, string baseCommit,
        string candidateCommit, string integrationCommit, string journalPath, string verificationHash)
    {
        lock (_gate)
        {
            HumanOnly(); RequireDeliveryAuthorization(ticketId);
            Change(Current, "Recorded host integration version", (c, tx) =>
            {
                Entry(Current, c, tx, ticketId, null, "integration-version", Serialize(new
                { ticketId, runId, baseCommit, candidateCommit, integrationCommit, journalPath, verificationHash }), integrationCommit, "host");
                return true;
            });
        }
    }

    /// <summary>Live host gate, not a prompt or an inbox acknowledgement. Rechecked at tool/integration boundaries.</summary>
    public bool IsRunAuthorized(string projectPath, string ticketId, string runId)
    {
        lock (_gate)
        {
            if (_disposed || _disposing) return false;
            return InProjectScope(projectPath, () =>
            {
                var context = Current;
                return context.Kernel.Atomic((c, tx) =>
                {
                    var options = Options(context, c, tx);
                    if (!options.Enabled || options.BudgetMinutes <= 0 || options.ComputeQuota <= 0) return false;
                    var authority = context.Kernel.ReadSnapshot(context.AuthorityId);
                    if (authority.Project.Status != RebirthProjectStatus.Active) return false;
                    var ticket = authority.Tickets.SingleOrDefault(t => t.Id == ticketId);
                    var run = authority.Runs.SingleOrDefault(r => r.Id == runId && r.TicketId == ticketId);
                    if (ticket?.Status != RebirthTicketStatus.Active || run?.Status != RebirthRunStatus.Running) return false;
                    if (authority.Decisions.Any(d => d.Status == "Active" && d.Kind == RebirthDecisionKind.Veto &&
                        (d.TicketId == ticketId || d.Target == "ticket:" + ticketId || d.Target == "project:" + context.AuthorityId))) return false;
                    var start = Scalar(c, tx, "SELECT budget_started_utc FROM graph_state WHERE project_id=$p", ("$p", context.AuthorityId));
                    return start is not null && Now() - Convert.ToInt64(start) < options.BudgetMinutes * 60_000L;
                });
            });
        }
    }
}
