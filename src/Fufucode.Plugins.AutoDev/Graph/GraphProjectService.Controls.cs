using Fufucode.App.Abstractions.Graph;
using Fufucode.Core.Automation.Rebirth;
using static Fufucode.Plugins.AutoDev.Graph.GraphStorage;

namespace Fufucode.Plugins.AutoDev.Graph;

public sealed partial class GraphProjectService
{
    private void CancelLinkedRepairs(ProjectContext context, string originalTicketId)
    {
        foreach (var entry in ReadSnapshot().Entries.Where(e => e.Kind == "integration-repair" && e.TicketId is not null))
        {
            using var data = System.Text.Json.JsonDocument.Parse(entry.Text);
            var original = data.RootElement.TryGetProperty("OriginalTicketId", out var property) ? property.GetString() :
                data.RootElement.GetProperty("originalTicketId").GetString();
            if (original == originalTicketId && context.Runs.TryGetValue(entry.TicketId!, out var run))
                RequestManagedStop(context, run, "Parent integration delivery authority was stopped or revoked");
        }
    }

    public void AssignHostRepairExecutor(string ticketId)
    {
        lock (_gate)
        {
            HumanOnly();
            var state = ReadOrganizationSnapshot();
            if (state.Responsibilities.Any(r => r.TicketId == ticketId && r.Role == GraphExecutionRole.Executor))
            { KickQueue(Current); return; }
            var ticket = ReadSnapshot().Tickets.Single(t => t.Id == ticketId);
            var previous = _organizationHostActor; _organizationHostActor = "host";
            try
            {
                DelegateResponsibility(CommandId(), state.Version, "Host delegated ordinary integration repair to an Executor",
                    new GraphResponsibility("repair-executor:" + ticketId, GraphExecutionRole.Executor, ticket.ModuleIds, ticketId));
            }
            finally { _organizationHostActor = previous; }
        }
    }

    public void SupersedeInternalRepair(string ticketId, string reason)
    {
        lock (_gate)
        {
            HumanOnly(); var context = Current;
            var snapshot = context.Kernel.ReadSnapshot(context.AuthorityId);
            RejectInFlight(context, snapshot, ticketId);
            var ticket = Ticket(context, ticketId);
            if (ticket.Status is RebirthTicketStatus.Done or RebirthTicketStatus.Cancelled) return;
            if (ticket.Status == RebirthTicketStatus.NeedsReconciliation)
                throw new InvalidOperationException("Unknown repair effects must be reconciled before supersession.");
            Change(context, "Superseded internal integration repair", (c, tx) =>
            {
                context.Kernel.StopTicket(CommandId(), ticketId, reason, "host", Now());
                Entry(context, c, tx, ticketId, null, "repair-superseded", reason, null, "host");
                return true;
            });
        }
    }

    public Action<string>? RecoveryProbe { get; set; }

    public void RecordHostInspection(string ticketId, string details)
    {
        lock (_gate)
        {
            HumanOnly();
            Change(Current, "Recorded deterministic host recovery inspection", (c, tx) =>
            {
                Entry(Current, c, tx, ticketId, null, "recovery-inspection", details, null, "host");
                return true;
            });
        }
    }

    private void RequireVersionedDelivery(string ticketId)
    {
        var snapshot = ReadSnapshot();
        var results = snapshot.Entries.Where(e => e.TicketId == ticketId && e.Kind == "run-result" && e.Actor == "executor");
        var requiresCommit = results.Any(e =>
        {
            using var data = System.Text.Json.JsonDocument.Parse(e.Text);
            return data.RootElement.TryGetProperty("candidateManifestPath", out var path) && path.ValueKind == System.Text.Json.JsonValueKind.String ||
                data.RootElement.TryGetProperty("deliveryStatus", out var status) && status.GetString() is "CandidateReady" or "EmptyCandidate";
        });
        if (requiresCommit && !snapshot.Entries.Any(e => e.TicketId == ticketId && e.Kind == "integration-version" && e.Actor == "host"))
            throw new InvalidOperationException("Repository work requires a host-verified integration commit before Done; candidate completion is not integration.");
    }

    public TimeSpan RemainingExecutionBudget()
    {
        lock (_gate)
        {
            var context = Current;
            return context.Kernel.Atomic((c, tx) =>
            {
                var options = Options(context, c, tx);
                var started = Scalar(c, tx, "SELECT budget_started_utc FROM graph_state WHERE project_id=$p", ("$p", context.AuthorityId));
                return TimeSpan.FromMilliseconds(Math.Max(0, options.BudgetMinutes * 60_000L -
                    (started is null ? 0 : Now() - Convert.ToInt64(started))));
            });
        }
    }

    public void RequireDeliveryAuthorization(string ticketId)
    {
        lock (_gate)
        {
            var context = Current;
            var authority = context.Kernel.ReadSnapshot(context.AuthorityId);
            var ticket = authority.Tickets.Single(t => t.Id == ticketId);
            var lastRun = authority.Runs.Where(r => r.TicketId == ticketId).OrderByDescending(r => r.StartedUtc).FirstOrDefault();
            if (authority.Decisions.Any(d => d.Kind == RebirthDecisionKind.Stop && d.Status == "Requested" &&
                d.TicketId == ticketId && (lastRun is null || d.CreatedUtc >= lastRun.StartedUtc)))
                throw new UnauthorizedAccessException("Delivery blocked by a persisted stop request.");
            var options = ReadOptions();
            if (!options.Enabled || options.BudgetMinutes <= 0 || options.ComputeQuota <= 0 || RemainingExecutionBudget() <= TimeSpan.Zero)
                throw new UnauthorizedAccessException("Graph execution/delivery authorization is paused or disabled.");
            if (ticket.Status is not (RebirthTicketStatus.Review or RebirthTicketStatus.Done) ||
                authority.Project.Status != RebirthProjectStatus.Active ||
                authority.Decisions.Any(d => d.Status == "Active" && d.Kind == RebirthDecisionKind.Veto &&
                    (d.TicketId == ticketId || d.Target == "ticket:" + ticketId || d.Target == "route:" + ticket.Route || d.Target == "project:" + context.AuthorityId)))
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
                    var repairEntry = ReadSnapshot().Entries.LastOrDefault(e => e.TicketId == ticketId && e.Kind == "integration-repair");
                    if (repairEntry is not null)
                    {
                        using var repair = System.Text.Json.JsonDocument.Parse(repairEntry.Text);
                        var original = repair.RootElement.TryGetProperty("OriginalTicketId", out var property) ? property.GetString() :
                            repair.RootElement.GetProperty("originalTicketId").GetString();
                        try { RequireDeliveryAuthorization(original!); } catch (UnauthorizedAccessException) { return false; }
                    }
                    var run = authority.Runs.SingleOrDefault(r => r.Id == runId && r.TicketId == ticketId);
                    if (ticket?.Status != RebirthTicketStatus.Active || run?.Status != RebirthRunStatus.Running) return false;
                    if (authority.Decisions.Any(d => d.Kind == RebirthDecisionKind.Stop && d.Status == "Requested" &&
                        d.TicketId == ticketId && d.CreatedUtc >= run.StartedUtc)) return false;
                    if (authority.Decisions.Any(d => d.Status == "Active" && d.Kind == RebirthDecisionKind.Veto &&
                        (d.TicketId == ticketId || d.Target == "ticket:" + ticketId || d.Target == "route:" + ticket.Route || d.Target == "project:" + context.AuthorityId))) return false;
                    var start = Scalar(c, tx, "SELECT budget_started_utc FROM graph_state WHERE project_id=$p", ("$p", context.AuthorityId));
                    return start is not null && Now() - Convert.ToInt64(start) < options.BudgetMinutes * 60_000L;
                });
            });
        }
    }
}
