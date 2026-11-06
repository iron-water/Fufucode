using Fufucode.App.Abstractions.Graph;
using Microsoft.Data.Sqlite;
using static Fufucode.Plugins.AutoDev.Graph.GraphStorage;

namespace Fufucode.Plugins.AutoDev.Graph;

public sealed partial class GraphProjectService
{
    private string? _organizationHostActor;
    private GraphOrganizationSnapshot SendHostOrganizationMessage(string commandId, GraphMessageDraft message)
    {
        lock (_gate)
        {
            var previous = _organizationHostActor;
            _organizationHostActor = "host";
            try { return SendOrganizationMessage(commandId, message); }
            finally { _organizationHostActor = previous; }
        }
    }

    private static readonly HashSet<string> MandatoryMessageKinds = new(StringComparer.OrdinalIgnoreCase)
        { "veto", "stop", "permission", "interface", "invalidated", "ownership" };

    private GraphOrganizationSnapshot Organization(ProjectContext context, SqliteConnection c, SqliteTransaction tx)
    {
        var json = Scalar(c, tx, "SELECT state_json FROM graph_organization WHERE project_id=$p", ("$p", context.AuthorityId)) as string;
        return json is null ? new(context.AuthorityId, 0, [new("project-lead", GraphExecutionRole.ProjectLead, [])], [], [], []) : Deserialize<GraphOrganizationSnapshot>(json);
    }

    public GraphOrganizationSnapshot ReadOrganizationSnapshot()
    {
        lock (_gate) { var context = Current; return context.Kernel.Atomic((c, tx) => Organization(context, c, tx)); }
    }

    public GraphResponsibility? ResolveOrganizationResponsibility(string ticketId, GraphExecutionRole role)
    {
        lock (_gate)
        {
            OrganizationScope(null, ticketId, null);
            var state = ReadOrganizationSnapshot();
            if (role == GraphExecutionRole.ProjectLead) return state.Responsibilities.Single(r => r.Role == role);
            var matches = state.Responsibilities.Where(r => r.TicketId == ticketId && r.Role == role).ToArray();
            if (matches.Length > 1) throw new InvalidOperationException("Ambiguous ticket responsibility; explicit delegation required.");
            return matches.SingleOrDefault();
        }
    }

    private string OrganizationActor(GraphOrganizationSnapshot state, GraphExecutionIdentity? identity, bool lead = false)
    {
        if (identity is null) { HumanOnly(); return _organizationHostActor ?? "human"; }
        var member = state.Responsibilities.SingleOrDefault(r => r.Id == identity.ResponsibilityId)
            ?? throw new InvalidOperationException("Unregistered execution responsibility.");
        if (member.Role != identity.Role || (member.Role is GraphExecutionRole.Executor or GraphExecutionRole.Verifier && member.TicketId != identity.TicketId) ||
            !member.ModuleIds.Order().SequenceEqual(identity.ModuleIds.Order()))
            throw new InvalidOperationException("Execution identity no longer matches organization.");
        if (lead && member.Role is not (GraphExecutionRole.ProjectLead or GraphExecutionRole.ScopeLead))
            throw new InvalidOperationException("Lead responsibility required.");
        if (string.IsNullOrWhiteSpace(identity.RunId)) throw new InvalidOperationException("Host run identity required.");
        var run = Current.Kernel.ReadSnapshot(Current.AuthorityId).Runs.SingleOrDefault(r => r.Id == identity.RunId);
        if (run is null || run.TicketId != identity.TicketId || run.Status.ToString() != "Running")
            throw new InvalidOperationException("Host identity must belong to a live project run.");
        if (member.Role == GraphExecutionRole.ScopeLead)
        {
            var ticket = ReadSnapshot().Tickets.Single(t => t.Id == run.TicketId);
            if (ticket.ModuleIds.Length == 0 || ticket.ModuleIds.Any(m => !member.ModuleIds.Contains(m)))
                throw new InvalidOperationException("ScopeLead run work is outside its module scope.");
        }
        return member.Id;
    }

    private GraphOrganizationSnapshot OrganizationChange(string commandId, object payload, GraphExecutionIdentity? identity,
        Func<GraphOrganizationSnapshot, string, GraphOrganizationSnapshot> mutate, bool lead = false)
    {
        lock (_gate)
        {
            var context = Current;
            return Change(context, "Organization command", (c, tx) =>
            {
                if (identity is not null) EnsureAgentMandate(context, c, tx);
                var before = Organization(context, c, tx);
                var actor = OrganizationActor(before, identity, lead);
                var hash = Hash(Serialize(new { payload, identity }));
                var receipt = Query(c, tx, "SELECT payload_hash,result_json FROM graph_organization_commands WHERE project_id=$p AND command_id=$id",
                    r => (Hash: r.GetString(0), Json: r.GetString(1)), ("$p", context.AuthorityId), ("$id", Required(commandId, nameof(commandId)))).SingleOrDefault();
                if (receipt.Json is not null)
                {
                    if (receipt.Hash != hash) throw new InvalidOperationException("Command content conflict.");
                    return Deserialize<GraphOrganizationSnapshot>(receipt.Json);
                }
                var after = mutate(before, actor) with { Version = checked(before.Version + 1) };
                Execute(c, tx, "INSERT INTO graph_organization VALUES($p,$v,$s) ON CONFLICT(project_id) DO UPDATE SET version=$v,state_json=$s",
                    ("$p", context.AuthorityId), ("$v", after.Version), ("$s", Serialize(after)));
                Execute(c, tx, "INSERT INTO graph_organization_commands VALUES($p,$id,$hash,$result)",
                    ("$p", context.AuthorityId), ("$id", commandId), ("$hash", hash), ("$result", Serialize(after)));
                Entry(context, c, tx, null, null, "organization", Serialize(new { commandId, payload, before, after }), null, actor);
                return after;
            });
        }
    }

    private void OrganizationScope(GraphExecutionIdentity? identity, string? ticketId, string? moduleId)
    {
        var authority = Current.Kernel.ReadSnapshot(Current.AuthorityId);
        if (ticketId is not null && !authority.Tickets.Any(t => t.Id == ticketId)) throw new InvalidOperationException("Unknown project ticket.");
        if (moduleId is not null && !ReadSnapshot().Modules.Any(m => m.Id == moduleId)) throw new InvalidOperationException("Unknown project module.");
        if (identity is null || identity.Role == GraphExecutionRole.ProjectLead) return;
        if (ticketId is not null)
        {
            if (identity.Role == GraphExecutionRole.ScopeLead)
            {
                var modules = ReadSnapshot().Tickets.Single(t => t.Id == ticketId).ModuleIds;
                if (modules.Length == 0 || modules.Any(m => !identity.ModuleIds.Contains(m))) throw new InvalidOperationException("Ticket outside delegated module scope.");
            }
            else if (ticketId != identity.TicketId) throw new InvalidOperationException("Ticket outside execution scope.");
        }
        if (moduleId is not null && !identity.ModuleIds.Contains(moduleId)) throw new InvalidOperationException("Module outside execution scope.");
        if (ticketId is null && moduleId is null) throw new InvalidOperationException("Scoped execution requires an affected object.");
    }

    public GraphOrganizationSnapshot ApplyOrganization(string commandId, long expectedVersion, string reason,
        GraphResponsibility[] responsibilities, string[] affectedIds, GraphExecutionIdentity? hostIdentity = null) =>
        OrganizationChange(commandId, new { expectedVersion, reason, responsibilities, affectedIds }, hostIdentity,
            (state, actor) => ValidateOrganizationChange(state, expectedVersion, reason, responsibilities, affectedIds, hostIdentity), true);

    public GraphOrganizationSnapshot DelegateResponsibility(string commandId, long expectedVersion, string reason,
        GraphResponsibility member, GraphExecutionIdentity? hostIdentity = null) =>
        OrganizationChange(commandId, new { operation = "delegate-responsibility", expectedVersion, reason, member }, hostIdentity,
            (state, actor) => ValidateOrganizationChange(state, expectedVersion, reason,
                state.Responsibilities.Where(r => r.Id != member.Id).Append(member).ToArray(), [member.Id], hostIdentity), true);

    private GraphOrganizationSnapshot ValidateOrganizationChange(GraphOrganizationSnapshot state, long expectedVersion,
        string reason, GraphResponsibility[] responsibilities, string[] affectedIds, GraphExecutionIdentity? hostIdentity)
        {
            Required(reason, nameof(reason));
            if (state.Version != expectedVersion) throw new InvalidOperationException("Organization version conflict.");
            if (responsibilities.Select(r => r.Id).Distinct().Count() != responsibilities.Length || responsibilities.Any(r => string.IsNullOrWhiteSpace(r.Id) || !Enum.IsDefined(r.Role)))
                throw new InvalidOperationException("Invalid responsibilities.");
            if (responsibilities.Count(r => r.Role == GraphExecutionRole.ProjectLead) != 1) throw new InvalidOperationException("Exactly one stable ProjectLead is required.");
            var oldLead = state.Responsibilities.SingleOrDefault(r => r.Role == GraphExecutionRole.ProjectLead);
            if (oldLead is not null && !responsibilities.Any(r => r.Id == oldLead.Id && r.Role == oldLead.Role)) throw new InvalidOperationException("ProjectLead identity is stable.");
            var changed = state.Responsibilities.Select(r => r.Id).Union(responsibilities.Select(r => r.Id)).Where(id =>
                Serialize(state.Responsibilities.SingleOrDefault(r => r.Id == id)) != Serialize(responsibilities.SingleOrDefault(r => r.Id == id))).ToArray();
            if (!changed.Order().SequenceEqual(affectedIds.Distinct().Order())) throw new InvalidOperationException("Affected responsibilities must exactly describe the change.");
            foreach (var member in responsibilities)
            {
                foreach (var module in member.ModuleIds) OrganizationScope(hostIdentity, member.TicketId, module);
                if (member.TicketId is not null) OrganizationScope(hostIdentity, member.TicketId, null);
                if (hostIdentity?.Role == GraphExecutionRole.ScopeLead && member.Role == GraphExecutionRole.ProjectLead) {
                    if (!state.Responsibilities.Any(r => Serialize(r) == Serialize(member))) throw new InvalidOperationException("ScopeLead cannot create ProjectLead.");
                }
            }
            foreach (var id in changed)
            {
                if (hostIdentity?.Role == GraphExecutionRole.ScopeLead)
                {
                    var affected = responsibilities.SingleOrDefault(r => r.Id == id) ?? state.Responsibilities.Single(r => r.Id == id);
                    if (affected.Role is GraphExecutionRole.ProjectLead || affected.ModuleIds.Length == 0)
                        throw new InvalidOperationException("ScopeLead cannot change unscoped responsibility.");
                }
                var replacement = responsibilities.SingleOrDefault(r => r.Id == id);
                var oldResponsibility = state.Responsibilities.SingleOrDefault(r => r.Id == id);
                var affectedModules = (replacement?.ModuleIds ?? []).Union(oldResponsibility?.ModuleIds ?? []).ToHashSet();
                var graph = ReadSnapshot();
                if (Current.Kernel.ReadSnapshot(Current.AuthorityId).Runs.Any(run =>
                    (run.Status.ToString() is "Running" or "Queued") && graph.Tickets.Any(t => t.Id == run.TicketId && t.ModuleIds.Any(affectedModules.Contains))))
                    throw new InvalidOperationException("Active run prevents module responsibility changes.");
                if (replacement?.TicketId is not null && Current.Kernel.ReadSnapshot(Current.AuthorityId).Runs.Any(run =>
                    (run.Status.ToString() is "Running" or "Queued") && run.TicketId == replacement.TicketId))
                    throw new InvalidOperationException("Active run prevents assignment changes.");
                var prior = state.Responsibilities.SingleOrDefault(r => r.Id == id);
                if (prior is not null)
                {
                    foreach (var module in prior.ModuleIds) OrganizationScope(hostIdentity, prior.TicketId, module);
                    if (prior.TicketId is not null) OrganizationScope(hostIdentity, prior.TicketId, null);
                    if (Current.Kernel.ReadSnapshot(Current.AuthorityId).Runs.Any(run => run.Status.ToString() is "Running" or "Queued" && (prior.TicketId == run.TicketId || prior.Role == GraphExecutionRole.ProjectLead)))
                        throw new InvalidOperationException("Active run prevents reassignment; stop and reconcile first.");
                }
            }
            return state with { Responsibilities = responsibilities };
        }

    public GraphOrganizationSnapshot SetWorkDependency(string commandId, string ticketId, string dependsOnTicketId, bool remove, string reason, GraphExecutionIdentity? hostIdentity = null) =>
        OrganizationChange(commandId, new { ticketId, dependsOnTicketId, remove, reason }, hostIdentity, (state, actor) =>
        {
            Required(reason, nameof(reason)); OrganizationScope(hostIdentity, ticketId, null); OrganizationScope(null, dependsOnTicketId, null);
            var edges = state.Dependencies.Where(d => d.TicketId != ticketId || d.DependsOnTicketId != dependsOnTicketId).ToList();
            if (!remove) edges.Add(new(ticketId, dependsOnTicketId));
            bool Reaches(string from, string target, HashSet<string> seen) => from == target || seen.Add(from) && edges.Where(e => e.TicketId == from).Any(e => Reaches(e.DependsOnTicketId, target, seen));
            if (!remove && Reaches(dependsOnTicketId, ticketId, [])) throw new InvalidOperationException("Work dependency cycle.");
            return state with { Dependencies = edges.ToArray() };
        }, true);

    public GraphOrganizationSnapshot SendOrganizationMessage(string commandId, GraphMessageDraft message, GraphExecutionIdentity? hostIdentity = null) =>
        OrganizationChange(commandId, message, hostIdentity, (state, actor) =>
        {
            if (message.Kind != message.Kind.Trim()) throw new InvalidOperationException("Message kind must be canonical.");
            if (hostIdentity is not null && (message.Kind.ToLowerInvariant() is "veto" or "stop" or "permission"))
                throw new InvalidOperationException("Control messages require direct human authority.");
            Required(message.Kind, nameof(message.Kind)); Required(message.Text, nameof(message.Text)); Required(message.RecipientId, nameof(message.RecipientId));
            OrganizationScope(null, message.TicketId, message.ModuleId);
            if (hostIdentity is not null && hostIdentity.Role is GraphExecutionRole.Executor or GraphExecutionRole.Verifier)
            {
                var recipient = state.Responsibilities.SingleOrDefault(r => r.Id == message.RecipientId);
                var related = message.TicketId is null || message.TicketId == hostIdentity.TicketId ||
                    state.Dependencies.Any(d => (d.TicketId == hostIdentity.TicketId && d.DependsOnTicketId == message.TicketId) ||
                        (d.DependsOnTicketId == hostIdentity.TicketId && d.TicketId == message.TicketId));
                if (!related || (!message.HumanRecipient && recipient?.Role != GraphExecutionRole.ProjectLead &&
                    recipient?.TicketId != hostIdentity.TicketId && !state.Dependencies.Any(d =>
                        (d.TicketId == hostIdentity.TicketId && d.DependsOnTicketId == recipient?.TicketId) ||
                        (d.DependsOnTicketId == hostIdentity.TicketId && d.TicketId == recipient?.TicketId))))
                    throw new InvalidOperationException("Direct collaboration requires an explicit related work dependency.");
            }
            else OrganizationScope(hostIdentity, message.TicketId, message.ModuleId);
            if (message.Revision < 0 || message.Revision > ReadSnapshot().Revision) throw new InvalidOperationException("Invalid message revision.");
            if (message.HumanRecipient ? message.RecipientId != "human" : !state.Responsibilities.Any(r => r.Id == message.RecipientId)) throw new InvalidOperationException("Unknown recipient.");
            var sequence = state.Messages.Select(m => m.Sequence).DefaultIfEmpty().Max() + 1;
            var item = new GraphOrganizationMessage(sequence, Id("message"), actor, message, GraphMessageStatus.Received, null);
            var critical = MandatoryMessageKinds.Contains(message.Kind);
            if (critical)
            {
                var authority = Current.Kernel.ReadSnapshot(Current.AuthorityId);
                foreach (var barrier in state.Barriers.Where(b => message.TicketId is null || b.TicketId == message.TicketId))
                {
                    foreach (var evidence in authority.Evidence.Where(e => e.TicketId == barrier.TicketId && e.Valid))
                        Current.Kernel.InvalidateEvidence(CommandId(), evidence.Id, "Mandatory information arrived after completion: " + message.Kind, Now());
                    if (authority.Tickets.Single(t => t.Id == barrier.TicketId).Status.ToString() == "Done")
                        Execute(_connection!, _transaction, "UPDATE rb_tickets SET status='Review',updated_utc=$now WHERE ticket_id=$id", ("$now", Now()), ("$id", barrier.TicketId));
                }
            }
            return state with { Messages = [..state.Messages, item], Barriers = state.Barriers.Select(b => critical && (message.TicketId is null || b.TicketId == message.TicketId) ? b with { RequiresReview = true } : b).ToArray() };
        });

    public GraphOrganizationMessage[] ReadOrganizationInbox(string recipientId, bool human, GraphExecutionIdentity? hostIdentity = null)
    {
        lock (_gate)
        {
            var state = ReadOrganizationSnapshot(); var actor = OrganizationActor(state, hostIdentity);
            if (actor != recipientId || human != (actor == "human")) throw new InvalidOperationException("Inbox identity mismatch.");
            return state.Messages.Where(m => m.Content.RecipientId == recipientId && m.Content.HumanRecipient == human).ToArray();
        }
    }

    public GraphOrganizationSnapshot AdvanceOrganizationMessage(string commandId, string messageId, GraphMessageStatus status, string reason, GraphExecutionIdentity? hostIdentity = null) =>
        OrganizationChange(commandId, new { messageId, status, reason }, hostIdentity, (state, actor) =>
        {
            var message = state.Messages.Single(m => m.Id == messageId);
            if (message.Content.RecipientId != actor || message.Content.HumanRecipient != (actor == "human")) throw new InvalidOperationException("Only the recipient acknowledges delivery and consumption.");
            var valid = message.Status switch {
                GraphMessageStatus.Received => status == GraphMessageStatus.Delivered,
                GraphMessageStatus.Delivered => status == GraphMessageStatus.Read,
                GraphMessageStatus.Read => status is GraphMessageStatus.Applied or GraphMessageStatus.Rejected,
                GraphMessageStatus.Rejected => hostIdentity is null && status == GraphMessageStatus.Applied &&
                    (message.Content.Kind.ToLowerInvariant() is "veto" or "stop" or "permission"),
                _ => false };
            if (!valid) throw new InvalidOperationException("Invalid message lifecycle transition.");
            if (status is GraphMessageStatus.Applied or GraphMessageStatus.Rejected) Required(reason, nameof(reason));
            if (hostIdentity is not null && (message.Content.Kind.ToLowerInvariant() is "veto" or "stop" or "permission") && status == GraphMessageStatus.Applied)
                throw new InvalidOperationException("Only direct human authority can resolve control messages.");
            return state with { Messages = state.Messages.Select(m => m.Id == messageId ? m with { Status = status, Resolution = reason } : m).ToArray() };
        });

    public GraphOrganizationSnapshot CloseOrganizationBarrier(string commandId, string ticketId, GraphExecutionIdentity? hostIdentity = null) =>
        OrganizationChange(commandId, new { ticketId }, hostIdentity, (state, actor) =>
        {
            OrganizationScope(hostIdentity, ticketId, null);
            var relevant = state.Messages.Where(m => m.Content.TicketId is null || m.Content.TicketId == ticketId).ToArray();
            if (relevant.Any(m => MandatoryMessageKinds.Contains(m.Content.Kind) &&
                (m.Status is not (GraphMessageStatus.Applied or GraphMessageStatus.Rejected) ||
                 (m.Content.Kind.ToLowerInvariant() is "veto" or "stop" or "permission") && m.Status != GraphMessageStatus.Applied)))
                throw new InvalidOperationException("Mandatory messages must be consumed before closing.");
            var barrier = new GraphCompletionBarrier(ticketId, state.Messages.Select(m => m.Sequence).DefaultIfEmpty().Max(), false);
            return state with { Barriers = [..state.Barriers.Where(b => b.TicketId != ticketId), barrier] };
        }, true);
}
