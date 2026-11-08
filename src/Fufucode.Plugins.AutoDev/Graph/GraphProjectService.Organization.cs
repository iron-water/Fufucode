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

    private GraphOrganizationSnapshot Organization(ProjectContext context, SqliteConnection c, SqliteTransaction tx)
    {
        var json = Scalar(c, tx, "SELECT state_json FROM graph_organization WHERE project_id=$p", ("$p", context.AuthorityId)) as string;
        GraphOrganizationSnapshot state = json is null ? new(context.AuthorityId, 0, [new("project-lead", GraphExecutionRole.ProjectLead, [])], [], [], []) : GraphOrganizationStorage.ReadSnapshot(json);
        var decisions = context.Kernel.ReadSnapshot(context.AuthorityId).Decisions;
        return state with { Controls = state.Controls.Select(control => control with
        {
            Status = decisions.Any(d => d.Id == control.DecisionId && d.Status == "Active")
                ? GraphControlStatus.Effective : GraphControlStatus.Revoked
        }).ToArray() };
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
            var result = Change(context, "Organization command", (c, tx) =>
            {
                if (identity is not null) EnsureAgentMandate(context, c, tx);
                var before = Organization(context, c, tx);
                var actor = OrganizationActor(before, identity, lead);
                var hash = Hash(Serialize(new { payload, identity }));
                var receipt = Query(c, tx, "SELECT payload_hash,result_json FROM graph_organization_commands WHERE project_id=$p AND command_id=$id",
                    r => (Hash: r.GetString(0), Json: r.GetString(1)), ("$p", context.AuthorityId), ("$id", Required(commandId, nameof(commandId)))).SingleOrDefault();
                if (receipt.Json is not null)
                {
                    var legacyHash = payload is GraphMessageDraft { Communication: GraphCommunicationKind.Notify, Address: null } draft
                        ? Hash(Serialize(new { payload = new { draft.RecipientId, draft.HumanRecipient, draft.Kind, draft.Text,
                            draft.TicketId, draft.ModuleId, draft.Revision }, identity })) : null;
                    if (receipt.Hash != hash && receipt.Hash != legacyHash) throw new InvalidOperationException("Command content conflict.");
                    return GraphOrganizationStorage.ReadSnapshot(receipt.Json);
                }
                var after = mutate(before, actor) with { Version = checked(before.Version + 1) };
                Execute(c, tx, "INSERT INTO graph_organization VALUES($p,$v,$s) ON CONFLICT(project_id) DO UPDATE SET version=$v,state_json=$s",
                    ("$p", context.AuthorityId), ("$v", after.Version), ("$s", Serialize(after)));
                Execute(c, tx, "INSERT INTO graph_organization_commands VALUES($p,$id,$hash,$result)",
                    ("$p", context.AuthorityId), ("$id", commandId), ("$hash", hash), ("$result", Serialize(after)));
                Entry(context, c, tx, null, null, "organization", Serialize(new { commandId, payload, before, after }), null, actor);
                return after;
            });
            // Only actual organization/dependency changes can make runnable work available.
            // Persisting correspondence never schedules or wakes a model.
            if (lead) KickQueue(context);
            return result;
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
            Required(message.Kind, nameof(message.Kind)); Required(message.Text, nameof(message.Text));
            if (message.Kind != message.Kind.Trim()) throw new InvalidOperationException("Message kind must be canonical.");
            if (!Enum.IsDefined(message.Communication) || message.Communication == GraphCommunicationKind.Control)
                throw new InvalidOperationException("Control is a Kernel decision, not a message. Use IssueOrganizationControl.");
            OrganizationScope(null, message.TicketId, message.ModuleId);
            ResolveMessageRecipients(state, message, validate: true); // Validate project entities, not edit permissions or DAG edges.
            if (message.Revision < 0 || message.Revision > ReadSnapshot().Revision) throw new InvalidOperationException("Invalid message revision.");
            var sequence = checked(state.Messages.Select(m => m.Sequence).DefaultIfEmpty().Max() + 1);
            var item = new GraphOrganizationMessage(sequence, Id("message"), actor, message, GraphMessageStatus.Received, null)
            {
                RequestStatus = message.Communication == GraphCommunicationKind.Request ? GraphRequestStatus.Open : null
            };
            // Claims and requests neither invalidate evidence nor schedule a model turn.
            return state with { Messages = [..state.Messages, item] };
        });

    private string[] ResolveMessageRecipients(GraphOrganizationSnapshot state, GraphMessageDraft message, bool validate = false)
    {
        if (message.Address is null)
        {
            Required(message.RecipientId, nameof(message.RecipientId));
            if (validate && (message.HumanRecipient ? message.RecipientId != "human" : !state.Responsibilities.Any(r => r.Id == message.RecipientId)))
                throw new InvalidOperationException("Unknown recipient.");
            return [message.RecipientId];
        }
        var address = message.Address;
        if (message.HumanRecipient || !string.IsNullOrEmpty(message.RecipientId))
            throw new InvalidOperationException("Use either an explicit recipient or a semantic address.");
        if (address.TicketId is null && address.ModuleId is null && address.Role is null)
            throw new InvalidOperationException("Semantic address requires ticket, module or role.");
        if (address.Role.HasValue && !Enum.IsDefined(address.Role.Value)) throw new InvalidOperationException("Unknown role.");
        if (validate) OrganizationScope(null, address.TicketId, address.ModuleId);
        return state.Responsibilities.Where(r =>
            (address.TicketId is null || r.TicketId == address.TicketId) &&
            (address.ModuleId is null || r.ModuleIds.Contains(address.ModuleId)) &&
            (address.Role is null || r.Role == address.Role)).Select(r => r.Id).Order(StringComparer.Ordinal).ToArray();
    }

    public string[] ResolveOrganizationRecipients(GraphMessageAddress address)
    {
        lock (_gate)
        {
            return ResolveMessageRecipients(ReadOrganizationSnapshot(), new("", false, "resolve", "resolve", null, null, 0) { Address = address }, validate: true);
        }
    }

    public GraphOrganizationMessage[] ReadOrganizationInbox(string recipientId, bool human, GraphExecutionIdentity? hostIdentity = null)
    {
        lock (_gate)
        {
            var state = ReadOrganizationSnapshot(); var actor = OrganizationActor(state, hostIdentity);
            if (actor != recipientId || human != (actor == "human")) throw new InvalidOperationException("Inbox identity mismatch.");
            return state.Messages.Where(m => m.Content.HumanRecipient == human && ResolveMessageRecipients(state, m.Content).Contains(recipientId)).ToArray();
        }
    }

    public GraphOrganizationSnapshot AdvanceOrganizationMessage(string commandId, string messageId, GraphMessageStatus status, string reason, GraphExecutionIdentity? hostIdentity = null) =>
        OrganizationChange(commandId, new { messageId, status, reason }, hostIdentity, (state, actor) =>
        {
            var message = state.Messages.Single(m => m.Id == messageId);
            if (message.Content.Communication != GraphCommunicationKind.Request)
                throw new InvalidOperationException("Notify has no acknowledgement lifecycle; controls are resolved through Kernel decisions.");
            return ResolveOrganizationRequestCore(state, message, actor, hostIdentity,
                status == GraphMessageStatus.Applied ? GraphRequestStatus.Resolved :
                status == GraphMessageStatus.Rejected ? GraphRequestStatus.Declined :
                throw new InvalidOperationException("Use the Request lifecycle: Open -> Resolved/Declined/Cancelled."), reason);
        });

    public GraphOrganizationSnapshot ResolveOrganizationRequest(string commandId, string messageId, GraphRequestStatus status,
        string reason, GraphExecutionIdentity? hostIdentity = null) =>
        OrganizationChange(commandId, new { messageId, status, reason }, hostIdentity, (state, actor) =>
            ResolveOrganizationRequestCore(state, state.Messages.Single(m => m.Id == messageId), actor, hostIdentity, status, reason));

    private GraphOrganizationSnapshot ResolveOrganizationRequestCore(GraphOrganizationSnapshot state, GraphOrganizationMessage message,
        string actor, GraphExecutionIdentity? identity, GraphRequestStatus status, string reason)
    {
        if (message.Content.Communication != GraphCommunicationKind.Request ||
            message.RequestStatus is not (null or GraphRequestStatus.Open) ||
            status is not (GraphRequestStatus.Resolved or GraphRequestStatus.Declined or GraphRequestStatus.Cancelled))
            throw new InvalidOperationException("Invalid request lifecycle transition.");
        var authorized = status == GraphRequestStatus.Cancelled ? actor == message.SenderId :
            message.Content.HumanRecipient == (identity is null && actor == "human") && ResolveMessageRecipients(state, message.Content).Contains(actor);
        if (!authorized) throw new InvalidOperationException("Only the recipient resolves/declines; only the sender cancels.");
        Required(reason, nameof(reason));
        return state with { Messages = state.Messages.Select(m => m.Id == message.Id ? m with
        {
            RequestStatus = status, Resolution = reason,
            Status = status == GraphRequestStatus.Resolved ? GraphMessageStatus.Applied : GraphMessageStatus.Rejected
        } : m).ToArray() };
    }

    public GraphOrganizationSnapshot IssueOrganizationControl(string commandId, string ticketId, GraphControlKind kind, string reason)
    {
        lock (_gate)
        {
            HumanOnly();
            var result = OrganizationChange(commandId, new { operation = "control", ticketId, kind, reason }, null, (state, actor) =>
            {
                OrganizationScope(null, ticketId, null); Required(reason, nameof(reason));
                if (!Enum.IsDefined(kind)) throw new InvalidOperationException("Unknown control kind.");
                var decision = Current.Kernel.RecordVeto(commandId + ":kernel", Current.AuthorityId, ticketId,
                    "ticket:" + ticketId, "ticket", reason, "human", Now()).Decision;
                if (kind == GraphControlKind.Stop)
                    Current.Kernel.RequestStop(commandId + ":stop-request", ticketId, reason, Now(), "human");
                if (Current.Runs.TryGetValue(ticketId, out var run))
                {
                    run.StopRequested = true;
                    Execute(_connection!, _transaction, "UPDATE graph_run_links SET stop_requested=1 WHERE run_id=$id", ("$id", run.Id));
                }
                return state with { Controls = [..state.Controls, new GraphOrganizationControl(Id("control"), decision.Id,
                    ticketId, kind, reason, GraphControlStatus.Effective)] };
            });
            var issued = result.Controls.Last();
            CancelLinkedRepairs(Current, ticketId);
            if (Current.Kernel.ReadSnapshot(Current.AuthorityId).Decisions.Any(d => d.Id == issued.DecisionId && d.Status == "Active") &&
                Current.Runs.TryGetValue(ticketId, out var managed)) managed.Cancellation.Cancel();
            return result;
        }
    }

    public GraphOrganizationSnapshot RevokeOrganizationControl(string commandId, string controlId, string reason) =>
        OrganizationChange(commandId, new { operation = "revoke-control", controlId, reason }, null, (state, actor) =>
        {
            Required(reason, nameof(reason));
            var control = state.Controls.Single(c => c.Id == controlId);
            if (control.Status != GraphControlStatus.Effective) throw new InvalidOperationException("Control is not effective.");
            Current.Kernel.LiftVeto(commandId + ":kernel", Current.AuthorityId, control.DecisionId, "human", Now());
            return state with { Controls = state.Controls.Select(c => c.Id == controlId ? c with { Status = GraphControlStatus.Revoked } : c).ToArray() };
        });

    public GraphOrganizationSnapshot CloseOrganizationBarrier(string commandId, string ticketId, GraphExecutionIdentity? hostIdentity = null) =>
        OrganizationChange(commandId, new { ticketId }, hostIdentity, (state, actor) =>
        {
            OrganizationScope(hostIdentity, ticketId, null);
            // Compatibility checkpoint only. Message topics, unread claims and open requests cannot block completion.
            // The Kernel owns the actual veto guard, independent of delivery/read state.
            var authority = Current.Kernel.ReadSnapshot(Current.AuthorityId);
            var route = authority.Tickets.Single(t => t.Id == ticketId).Route;
            if (authority.Decisions.Any(d => d.Kind.ToString() == "Veto" && d.Status == "Active" &&
                (d.TicketId == ticketId || d.Target == "ticket:" + ticketId || d.Target == "project:" + Current.AuthorityId || d.Target == "route:" + route)))
                throw new InvalidOperationException("Active Kernel control prevents completion.");
            var barrier = new GraphCompletionBarrier(ticketId, state.Messages.Select(m => m.Sequence).DefaultIfEmpty().Max(), false);
            return state with { Barriers = [..state.Barriers.Where(b => b.TicketId != ticketId), barrier] };
        }, true);
}
