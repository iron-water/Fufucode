using Fufucode.App.Abstractions.Graph;
using Fufucode.Core.Automation.Rebirth;
using static Fufucode.Plugins.AutoDev.Graph.GraphStorage;

namespace Fufucode.Plugins.AutoDev.Graph;

public sealed partial class GraphProjectService
{
    /// <summary>Application composition-root bridge only. Never exposed as an agent tool or a historical JSON receipt.</summary>
    internal void CompleteHostVerifiedWork(string ticketId, string runId, string acceptanceDigest,
        string reference, string producer, string[] coveredCriteria, Func<bool> verifyLiveReceipt, bool internalCompletion,
        string continuation, string continuationReason)
    {
        lock (_gate)
        {
            HumanOnly();
            RequireDeliveryAuthorization(ticketId);
            RequireVersionedDelivery(ticketId);
            var context = Current;
            Required(reference, nameof(reference)); Required(producer, nameof(producer));
            if (continuation is not ("active_goal" or "successor" or "no_followup"))
                throw new ArgumentException("Explicit continuation is required.");
            Required(continuationReason, nameof(continuationReason));
            if (!verifyLiveReceipt()) throw new InvalidOperationException("Host verification is stale or not live.");
            Change(context, "Recorded live host verification", (c, tx) =>
            {
                var ticket = Ticket(context, ticketId);
                if (ticket.Status != RebirthTicketStatus.Review || ticket.AcceptanceDigest != acceptanceDigest)
                    throw new InvalidOperationException("Verification does not match the current reviewing work contract.");
                var run = context.Kernel.ReadSnapshot(context.AuthorityId).Runs.Single(r => r.Id == runId && r.TicketId == ticketId);
                if (run.Status != RebirthRunStatus.Completed) throw new InvalidOperationException("Execution is not settled successfully.");
                if (!verifyLiveReceipt()) throw new InvalidOperationException("Verification changed before durable completion.");
                if (coveredCriteria.Length == 0 || coveredCriteria.Any(criterion => !ticket.Acceptance.Contains(criterion, StringComparer.Ordinal)))
                    throw new InvalidOperationException("Verification must explicitly identify current acceptance criteria.");
                foreach (var criterion in coveredCriteria)
                    context.Kernel.AddEvidence(CommandId(), ticketId, runId, RebirthEvidenceKind.Test,
                        criterion, reference, true, producer, Now());
                Entry(context, c, tx, ticketId, null, "host-verification", Serialize(new
                { runId, acceptanceDigest, reference, producer, internalCompletion, continuation, continuationReason }), reference, "host-verifier");
                if (internalCompletion)
                {
                    CloseOrganizationBarrier(CommandId(), ticketId);
                    context.Kernel.CompleteTicket(CommandId(), ticketId, Now());
                }
                return true;
            });
            SignalProjectLead(context.Path, "run-terminal", runId);
        }
    }
}
