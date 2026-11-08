using System.Text.Json;
using Fufucode.App.Abstractions.Graph;
using Fufucode.Core.Automation.Rebirth;
using Microsoft.Data.Sqlite;
using static Fufucode.Plugins.AutoDev.Graph.GraphStorage;

namespace Fufucode.Plugins.AutoDev.Graph;

public sealed partial class GraphProjectService
{
    private static string Required(string value, string name)
    { ArgumentException.ThrowIfNullOrWhiteSpace(value, name); return value.Trim(); }
    private static string[] Clean(string[] values) => values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).Distinct(StringComparer.Ordinal).ToArray();
    private static string CommandId() => Id("graph-command");
    private void HumanOnly()
    { if (_actor != "human") throw new InvalidOperationException("This action requires a human operation."); }

    public void SetPurpose(string purpose)
    {
        lock (_gate)
        {
            HumanOnly(); var context = Current; purpose = Required(purpose, nameof(purpose));
            Change(context, "Human updated project purpose", (c, tx) =>
            {
                var snapshot = context.Kernel.ReadSnapshot(context.AuthorityId);
                var options = Options(context, c, tx);
                var prior = snapshot.Brief.Brief;
                var brief = prior with { Statement = purpose, Objectives = [purpose], Acceptance = [purpose],
                    AllowedPaths = prior.AllowedPaths.Length == 0 ? [context.Path] : prior.AllowedPaths,
                    BudgetMinutes = Math.Max(1, options.BudgetMinutes), ComputeQuota = options.ComputeQuota };
                context.Kernel.UpdateBrief(CommandId(), context.AuthorityId, snapshot.Project.BriefVersion, brief, "human", Now());
                Entry(context, c, tx, null, null, "purpose", Serialize(new { before = prior.Statement, after = purpose, beforeVersion = snapshot.Project.BriefVersion, afterVersion = snapshot.Project.BriefVersion + 1 }));
                foreach (var ticket in snapshot.Tickets.Where(t => t.Status is not (RebirthTicketStatus.Done or RebirthTicketStatus.Cancelled)))
                    if (prior.Statement.Length > 0 && prior.Statement != purpose)
                        Entry(context, c, tx, ticket.Id, null, "conflict", "Human purpose changed; the ticket retains its original mandate version. Review its scope against the new purpose.", null, "system");
                return true;
            });
        }
    }

    public async Task SaveOptionsAsync(GraphOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Concurrency is < 1 or > 16 || options.BudgetMinutes is < 0 or > 1440
            || !double.IsFinite(options.ComputeQuota) || options.ComputeQuota is < 0 or > 1
            || !Enum.IsDefined(options.Supervision)
            || options.ScanDepth is < 0 or > 20 || options.ValidationTimeoutSeconds is < 1 or > 86400)
            throw new ArgumentOutOfRangeException(nameof(options));
        ProjectContext context;
        bool stop;
        lock (_gate)
        {
            HumanOnly(); context = Current; cancellationToken.ThrowIfCancellationRequested();
            var priorOptions = context.Kernel.Atomic((c, tx) => Options(context, c, tx));
            stop = !options.Enabled || priorOptions.AutoAdvance && !options.AutoAdvance || options.BudgetMinutes == 0 || options.ComputeQuota == 0;
            Change(context, "Human updated Graph options", (c, tx) =>
            {
                var before = Options(context, c, tx);
                Execute(c, tx, "UPDATE graph_state SET options_json=$json WHERE project_id=$id", ("$json", Serialize(options)), ("$id", context.AuthorityId));
                if (options.BudgetMinutes != before.BudgetMinutes && context.Runs.Count == 0)
                    Execute(c, tx, "UPDATE graph_state SET budget_started_utc=NULL WHERE project_id=$id", ("$id", context.AuthorityId));
                var snapshot = context.Kernel.ReadSnapshot(context.AuthorityId);
                if (!string.IsNullOrWhiteSpace(snapshot.Brief.Brief.Statement))
                    context.Kernel.UpdateBrief(CommandId(), context.AuthorityId, snapshot.Project.BriefVersion,
                        snapshot.Brief.Brief with { BudgetMinutes = Math.Max(1, options.BudgetMinutes), ComputeQuota = options.ComputeQuota }, "human", Now());
                if (stop) Execute(c, tx, "UPDATE graph_state SET autonomy=0 WHERE project_id=$id", ("$id", context.AuthorityId));
                Entry(context, c, tx, null, null, "options", Serialize(new { before, after = options }));
                return true;
            });
            if (stop) context.AutonomyCancellation?.Cancel();
            else if (options.BudgetMinutes != priorOptions.BudgetMinutes && context.Runs.Count > 0)
            {
                var started = context.Kernel.Atomic((c, tx) => Scalar(c, tx, "SELECT budget_started_utc FROM graph_state WHERE project_id=$id", ("$id", context.AuthorityId)));
                var remaining = options.BudgetMinutes * 60_000L - (Now() - Convert.ToInt64(started));
                foreach (var run in context.Runs.Values)
                    if (remaining <= 0) run.Cancellation.Cancel();
                    else run.Cancellation.CancelAfter(TimeSpan.FromMilliseconds(remaining));
            }
        }
        if (stop) await StopContextAsync(context, cancellationToken).ConfigureAwait(false);
        else if (options.AutoAdvance)
        {
            lock (_gate)
            {
                var brief = context.Kernel.ReadSnapshot(context.AuthorityId).Brief.Brief;
                if (!string.IsNullOrWhiteSpace(brief.Statement)) StartAutonomy(context);
            }
        }
    }

    private static GraphModule Module(ProjectContext context, SqliteConnection c, SqliteTransaction tx, string id) =>
        Deserialize<GraphModule>(Scalar(c, tx, "SELECT module_json FROM graph_modules WHERE project_id=$project AND module_id=$id",
            ("$project", context.AuthorityId), ("$id", id)) as string ?? throw new KeyNotFoundException("Unknown module: " + id));

    private static RebirthTicket Ticket(ProjectContext context, string id) => context.Kernel.ReadSnapshot(context.AuthorityId).Tickets
        .SingleOrDefault(t => t.Id == id) ?? throw new KeyNotFoundException("Unknown ticket in this project: " + id);

    public GraphModule UpsertModule(string? id, string? parentId, string name, string summary, string[] sourcePaths)
    {
        lock (_gate)
        {
            var context = Current; name = Required(name, nameof(name)); var moduleId = string.IsNullOrWhiteSpace(id) ? Id("module") : id.Trim();
            return Change(context, "Updated module: " + name, (c, tx) =>
            {
                if (parentId is not null)
                {
                    var seen = new HashSet<string>(StringComparer.Ordinal) { moduleId };
                    for (var parent = parentId; parent is not null; parent = Module(context, c, tx, parent).ParentId)
                        if (!seen.Add(parent)) throw new InvalidOperationException("A module hierarchy cannot contain a cycle.");
                }
                var paths = Clean(sourcePaths).Select(p => NormalizeSourcePath(context, p)).Distinct(StringComparer.Ordinal).ToArray();
                var before = Scalar(c, tx, "SELECT module_json FROM graph_modules WHERE project_id=$project AND module_id=$id",
                    ("$project", context.AuthorityId), ("$id", moduleId)) as string;
                var module = new GraphModule(moduleId, parentId, name, summary ?? "", paths, Now());
                Execute(c, tx, """
                    INSERT INTO graph_modules VALUES($project,$id,$parent,$json,'manual',$actor,$version)
                    ON CONFLICT(project_id,module_id) DO UPDATE SET parent_id=$parent,module_json=$json,origin='manual',actor=$actor,version=$version
                    """, ("$project", context.AuthorityId), ("$id", moduleId), ("$parent", parentId), ("$json", Serialize(module)),
                    ("$actor", _actor), ("$version", Revision(context, c, tx) + 1));
                Entry(context, c, tx, null, moduleId, "module", Serialize(new { before = before is null ? null : Deserialize<GraphModule>(before), after = module }));
                Execute(c, tx, "DELETE FROM graph_sources WHERE project_id=$project AND entity_id=$id AND source_kind='declared'", ("$project", context.AuthorityId), ("$id", moduleId));
                foreach (var path in paths)
                    Source(context, c, tx, new(moduleId, path, "declared", "", "Source path declared by " + _actor));
                return module;
            });
        }
    }

    private static string NormalizeSourcePath(ProjectContext context, string value)
    {
        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(context.Path, value.Replace('\\', System.IO.Path.DirectorySeparatorChar)));
        var relative = System.IO.Path.GetRelativePath(context.Path, full);
        if (relative == ".." || relative.StartsWith(".." + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal) || System.IO.Path.IsPathRooted(relative))
            throw new ArgumentException("A module source must be inside its project.");
        return relative.Replace('\\', '/');
    }

    public void RemoveModule(string moduleId)
    {
        lock (_gate)
        {
            var context = Current;
            Change(context, "Removed module", (c, tx) =>
            {
                var before = Module(context, c, tx, moduleId);
                if (Convert.ToInt64(Scalar(c, tx, "SELECT COUNT(*) FROM graph_modules WHERE project_id=$project AND parent_id=$id",
                    ("$project", context.AuthorityId), ("$id", moduleId))) != 0)
                    throw new InvalidOperationException("Remove or reparent child modules before removing their parent.");
                if (Convert.ToInt64(Scalar(c, tx, "SELECT COUNT(*) FROM graph_ticket_modules WHERE module_id=$id", ("$id", moduleId))) != 0)
                    throw new InvalidOperationException("This module is referenced by tickets; preserve it or update its definition.");
                foreach (var relation in Query(c, tx, "SELECT relation_json FROM graph_relations WHERE project_id=$project", r => Deserialize<GraphRelation>(r.GetString(0)), ("$project", context.AuthorityId))
                    .Where(r => r.SourceId == moduleId || r.TargetId == moduleId))
                    RemoveRelation(relation.Id);
                Execute(c, tx, "DELETE FROM graph_modules WHERE project_id=$project AND module_id=$id", ("$project", context.AuthorityId), ("$id", moduleId));
                Execute(c, tx, "DELETE FROM graph_sources WHERE project_id=$project AND entity_id=$id", ("$project", context.AuthorityId), ("$id", moduleId));
                Entry(context, c, tx, null, moduleId, "module-removed", Serialize(before));
                return true;
            });
        }
    }

    public GraphRelation UpsertRelation(string? id, string sourceId, string targetId, string kind, string label)
    {
        lock (_gate)
        {
            var context = Current; kind = Required(kind, nameof(kind));
            var relationId = string.IsNullOrWhiteSpace(id) ? "relation-" + Hash(sourceId + ":" + targetId + ":" + kind)[..24] : id.Trim();
            return Change(context, "Updated module relation", (c, tx) =>
            {
                Module(context, c, tx, sourceId); Module(context, c, tx, targetId);
                if (sourceId == targetId) throw new ArgumentException("A relation needs two distinct modules.");
                var before = Scalar(c, tx, "SELECT relation_json FROM graph_relations WHERE project_id=$project AND relation_id=$id", ("$project", context.AuthorityId), ("$id", relationId)) as string;
                var relation = new GraphRelation(relationId, sourceId, targetId, kind, label ?? "");
                Execute(c, tx, """
                    INSERT INTO graph_relations VALUES($project,$id,$json,'manual',$actor,$version)
                    ON CONFLICT(project_id,relation_id) DO UPDATE SET relation_json=$json,origin='manual',actor=$actor,version=$version
                    """, ("$project", context.AuthorityId), ("$id", relationId), ("$json", Serialize(relation)), ("$actor", _actor), ("$version", Revision(context, c, tx) + 1));
                Entry(context, c, tx, null, null, "relation", Serialize(new { before, after = relation }));
                return relation;
            });
        }
    }

    public void RemoveRelation(string relationId)
    {
        lock (_gate)
        {
            var context = Current;
            Change(context, "Removed module relation", (c, tx) =>
            {
                var before = Scalar(c, tx, "SELECT relation_json FROM graph_relations WHERE project_id=$project AND relation_id=$id", ("$project", context.AuthorityId), ("$id", relationId)) as string
                    ?? throw new KeyNotFoundException("Unknown relation: " + relationId);
                Execute(c, tx, "DELETE FROM graph_relations WHERE project_id=$project AND relation_id=$id", ("$project", context.AuthorityId), ("$id", relationId));
                Execute(c, tx, "DELETE FROM graph_sources WHERE project_id=$project AND entity_id=$id", ("$project", context.AuthorityId), ("$id", relationId));
                Entry(context, c, tx, null, null, "relation-removed", before);
                return true;
            });
        }
    }

    public GraphTicket CreateTicket(string title, string goal, string[] moduleIds, string[] acceptance)
    {
        lock (_gate)
        {
            var context = Current; var modules = Clean(moduleIds); var criteria = Clean(acceptance);
            var created = Change(context, "Created work ticket", (c, tx) =>
            {
                foreach (var moduleId in modules) Module(context, c, tx, moduleId);
                var authority = context.Kernel.ReadSnapshot(context.AuthorityId);
                var ticket = context.Kernel.CreateTicket(CommandId(), context.AuthorityId, title, goal, criteria,
                    "graph", "graph", Now(), planVersion: authority.Project.BriefVersion,
                    ticketId: _agentCommand is null ? Id("ticket") : "ticket-" + Hash(_agentCommand)[..24],
                    mandateJson: Serialize(new { purposeVersion = authority.Project.BriefVersion, graphRevision = Revision(context, c, tx), actor = _actor, moduleIds = modules }));
                foreach (var moduleId in modules)
                    Execute(c, tx, "INSERT INTO graph_ticket_modules VALUES($ticket,$module)", ("$ticket", ticket.Id), ("$module", moduleId));
                Entry(context, c, tx, ticket.Id, null, "ticket-created", Serialize(new { title, goal, moduleIds = modules, acceptance = criteria }));
                return new GraphTicket(ticket.Id, ticket.Title, ticket.Goal, ticket.Status.ToString(), modules, ticket.Acceptance, null, ticket.CreatedUtc, ticket.UpdatedUtc);
            });
            if (_agentCommand is null) KickQueue(context);
            return created;
        }
    }

    public void AddEntry(string? ticketId, string? moduleId, string kind, string text, string? reference = null)
    {
        lock (_gate)
        {
            var context = Current; kind = Required(kind, nameof(kind)); text = Required(text, nameof(text));
            if (kind.Equals("human", StringComparison.OrdinalIgnoreCase) || kind.Equals("acceptance", StringComparison.OrdinalIgnoreCase)
                || kind.Equals("gate", StringComparison.OrdinalIgnoreCase) || kind.Equals("veto", StringComparison.OrdinalIgnoreCase)
                || kind.Equals("purpose", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Use the dedicated human command for protected decisions.");
            Change(context, "Added project correspondence", (c, tx) =>
            {
                if (ticketId is not null) Ticket(context, ticketId);
                if (moduleId is not null) Module(context, c, tx, moduleId);
                if (kind.Equals("evidence", StringComparison.OrdinalIgnoreCase))
                {
                    if (ticketId is null) throw new ArgumentException("Evidence needs a ticket.");
                    var ticket = Ticket(context, ticketId);
                    var criterion = ticket.Acceptance.Length == 1 ? ticket.Acceptance[0] : text;
                    var passed = _actor == "human";
                    try
                    {
                        using var parsed = JsonDocument.Parse(text);
                        if (parsed.RootElement.TryGetProperty("criterion", out var field)) criterion = field.GetString() ?? criterion;
                        if (parsed.RootElement.TryGetProperty("passed", out var pass)) passed = pass.GetBoolean();
                    }
                    catch (JsonException) { }
                    AddEvidenceCore(context, c, tx, ticketId, null, "artifact", criterion,
                        Required(reference ?? "", nameof(reference)), passed);
                }
                Entry(context, c, tx, ticketId, moduleId, kind, text, reference);
                return true;
            });
        }
    }

    internal object RecordAgentEvidence(string ticketId, string? runId, string kind, string criterion, string reference, bool passed)
    {
        lock (_gate)
        {
            if (_actor != "agent") throw new InvalidOperationException("Use the model command scope.");
            var context = Current;
            return Change(context, "Recorded model evidence claim", (c, tx) => AddEvidenceCore(context, c, tx, ticketId, runId, kind, criterion, reference, passed));
        }
    }

    private object AddEvidenceCore(ProjectContext context, SqliteConnection c, SqliteTransaction tx,
        string ticketId, string? runId, string kind, string criterion, string reference, bool passed)
    {
        var ticket = Ticket(context, ticketId);
        if (!ticket.Acceptance.Contains(criterion, StringComparer.Ordinal)) throw new ArgumentException("Evidence criterion must match a current ticket acceptance condition.");
        var evidenceKind = kind.ToLowerInvariant() switch
        {
            "artifact" => RebirthEvidenceKind.Artifact, "test" => RebirthEvidenceKind.Test,
            "review" => RebirthEvidenceKind.Review, "note" => RebirthEvidenceKind.Note,
            _ => throw new ArgumentException("Model evidence cannot claim human acceptance.")
        };
        EvidenceFile(context, Required(reference, nameof(reference)));
        // In-run tool claims are recorded without pretending the still-running execution ended.
        // The authority allows evidence without a run; provenance keeps the active run in correspondence.
        if (runId is not null)
        {
            var run = context.Kernel.ReadSnapshot(context.AuthorityId).Runs.SingleOrDefault(r => r.Id == runId && r.TicketId == ticketId)
                ?? throw new ArgumentException("Evidence run must belong to this ticket.");
            if (run.Status is RebirthRunStatus.Running or RebirthRunStatus.Queued)
            {
                Entry(context, c, tx, ticketId, null, "evidence-claim", Serialize(new { runId, kind, criterion, reference, passed }));
                return new { status = "claim-recorded", runId, criterion };
            }
        }
        var evidence = context.Kernel.AddEvidence(CommandId(), ticketId, runId, evidenceKind, criterion,
            Required(reference, nameof(reference)), passed, _actor, Now());
        FingerprintEvidence(context, c, tx, evidence, _actor);
        return evidence;
    }

    private static string? EvidenceFile(ProjectContext context, string reference)
    {
        string? file = null;
        if (System.IO.Path.IsPathRooted(reference)) file = System.IO.Path.GetFullPath(reference);
        else if (Uri.TryCreate(reference, UriKind.Absolute, out var uri))
        {
            if (uri.IsFile) file = uri.LocalPath;
            else if (uri.Scheme is "http" or "https") return null;
            else throw new ArgumentException("Evidence needs an existing local file or an explicit http/https reference.");
        }
        else file = System.IO.Path.GetFullPath(System.IO.Path.Combine(context.Path, reference));
        if (!File.Exists(file)) throw new FileNotFoundException("Evidence file does not exist.", file);
        return file;
    }

    private void FingerprintEvidence(ProjectContext context, SqliteConnection c, SqliteTransaction tx, RebirthEvidence evidence, string actor)
    {
        var file = EvidenceFile(context, evidence.Reference);
        if (file is null)
        {
            Entry(context, c, tx, evidence.TicketId, null, "evidence-source", Serialize(new
                { evidence.Id, producer = actor, verification = "External reference recorded; not fetched by Graph" }), evidence.Reference, actor);
            return;
        }
        using var stream = File.OpenRead(file);
        var size = stream.Length;
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant();
        Execute(c, tx, "INSERT INTO graph_evidence_sources VALUES($id,$file,$hash,$size,$now)",
            ("$id", evidence.Id), ("$file", file), ("$hash", hash), ("$size", size), ("$now", Now()));
        Entry(context, c, tx, evidence.TicketId, null, "evidence-source", Serialize(new
            { evidence.Id, contentHash = hash, contentSize = size, producer = actor }), file, actor);
    }

    public void LinkSession(string ticketId, string sessionId)
    {
        lock (_gate)
        {
            HumanOnly(); var context = Current; sessionId = Required(sessionId, nameof(sessionId));
            Change(context, "Linked execution session", (c, tx) =>
            {
                Ticket(context, ticketId);
                Execute(c, tx, "INSERT INTO graph_ticket_links VALUES($id,$session) ON CONFLICT(ticket_id) DO UPDATE SET session_id=$session", ("$id", ticketId), ("$session", sessionId));
                Execute(c, tx, "INSERT OR IGNORE INTO graph_session_links VALUES($project,$session,$ticket)", ("$project", context.AuthorityId), ("$session", sessionId), ("$ticket", ticketId));
                Entry(context, c, tx, ticketId, null, "session", "Linked session", sessionId);
                return true;
            });
        }
    }

    public string BuildContext(string? moduleId, string? ticketId, long? revision = null)
    {
        var snapshot = ReadSnapshot(revision);
        return GraphFocusedContext.Build(snapshot with
        {
            Organization = snapshot.Organization is { } organization ? organization with { Messages = [] } : null,
            Entries = snapshot.Entries.Where(e => e.Kind != "organization").ToArray()
        }, moduleId, ticketId);
    }

    public void AcceptTicket(string ticketId, string evidenceReference)
    {
        lock (_gate)
        {
            HumanOnly(); var context = Current;
            var accepted = Change(context, "Human reviewed ticket evidence", (c, tx) =>
            {
                Ticket(context, ticketId);
                var sources = Query(c, tx, """
                    SELECT s.evidence_id,s.file_path,s.content_hash,s.content_size FROM graph_evidence_sources s
                    JOIN rb_evidence e ON e.evidence_id=s.evidence_id WHERE e.ticket_id=$id AND e.valid=1
                    """, r => (Id: r.GetString(0), File: r.GetString(1), Hash: r.GetString(2), Size: r.GetInt64(3)), ("$id", ticketId));
                var stale = false;
                foreach (var source in sources)
                {
                    var changed = !File.Exists(source.File);
                    if (!changed)
                    {
                        using var stream = File.OpenRead(source.File);
                        changed = stream.Length != source.Size || Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant() != source.Hash;
                    }
                    if (!changed) continue;
                    stale = true;
                    context.Kernel.InvalidateEvidence(CommandId(), source.Id, "Evidence file changed or disappeared after recording", Now());
                    Entry(context, c, tx, ticketId, null, "conflict", "Evidence content changed; previous passing evidence invalidated", source.File, "system");
                }
                if (stale) return false;
                var barrier = CloseOrganizationBarrier(CommandId(), ticketId);
                if (barrier.Barriers.Any(b => b.TicketId == ticketId && b.RequiresReview))
                    throw new InvalidOperationException("New mandatory information requires review before acceptance.");
                context.Kernel.AcceptTicket(CommandId(), ticketId, Required(evidenceReference, nameof(evidenceReference)), Now());
                return true;
            });
            if (!accepted) throw new InvalidOperationException("Evidence changed or disappeared; record fresh evidence before accepting this ticket.");
        }
    }

    public void VetoTicket(string ticketId, string reason)
    {
        lock (_gate)
        {
            HumanOnly(); var context = Current;
            Change(context, "Human vetoed ticket", (c, tx) =>
            {
                Ticket(context, ticketId);
                context.Kernel.RecordVeto(CommandId(), context.AuthorityId, ticketId, "ticket:" + ticketId, "ticket", reason, "human", Now());
                if (context.Runs.TryGetValue(ticketId, out var run))
                {
                    run.StopRequested = true;
                    Execute(c, tx, "UPDATE graph_run_links SET stop_requested=1 WHERE run_id=$id", ("$id", run.Id));
                }
                return true;
            });
            var organization = ReadOrganizationSnapshot();
            var recipient = organization.Responsibilities.FirstOrDefault(r => r.TicketId == ticketId && r.Role == GraphExecutionRole.Executor)?.Id ?? "project-lead";
            SendOrganizationMessage(CommandId(), new GraphMessageDraft(recipient, false, "veto", reason, ticketId, null, ReadSnapshot().Revision));
            if (context.Runs.TryGetValue(ticketId, out var managed)) managed.Cancellation.Cancel();
        }
    }

    public void LiftVeto(string ticketId, string reason)
    {
        lock (_gate)
        {
            HumanOnly(); var context = Current; Required(reason, nameof(reason));
            Change(context, "Human lifted ticket veto", (c, tx) =>
            {
                var authority = context.Kernel.ReadSnapshot(context.AuthorityId);
                Ticket(context, ticketId); RejectInFlight(context, authority, ticketId);
                var vetoes = authority.Decisions.Where(d => d.Kind == RebirthDecisionKind.Veto && d.Status == "Active"
                    && d.Target == "ticket:" + ticketId && (d.TicketId is null || d.TicketId == ticketId)).ToArray();
                if (vetoes.Length == 0) throw new InvalidOperationException("This ticket has no active ticket-scoped veto to lift.");
                foreach (var veto in vetoes)
                    context.Kernel.LiftVeto(CommandId(), context.AuthorityId, veto.Id, "human", Now());
                Entry(context, c, tx, ticketId, null, "veto-lifted", Serialize(new { reason, decisionIds = vetoes.Select(d => d.Id).ToArray() }), null, "human");
                return true;
            });
        }
    }

    public void ReconcileTicket(string ticketId, string resolution)
    {
        lock (_gate)
        {
            HumanOnly(); var context = Current;
            var receipt = ReadSnapshot().Entries.Where(e => e.TicketId == ticketId && e.Kind == "run-result" && e.Actor == "executor")
                .OrderByDescending(e => e.CreatedUtc).FirstOrDefault();
            if (receipt is null) throw new InvalidOperationException("缺少可信执行回执；保留未知状态。可停止此任务并创建明确的新任务，不能盲目重放。");
            using var receiptData = JsonDocument.Parse(receipt.Text);
            if (!receiptData.RootElement.TryGetProperty("noSideEffectsDispatched", out var safe) || safe.ValueKind != JsonValueKind.True)
                throw new InvalidOperationException("执行曾派发或副作用无法确认；已保留现场与未知状态。请选择停止该任务，或针对具体外部操作进行恢复，不能直接重试。");
            resolution = "Kernel verified the host receipt: no side effects were dispatched; no active run may be replayed.";
            Change(context, "Kernel reconciled unknown ticket outcome", (c, tx) =>
            {
                var authority = context.Kernel.ReadSnapshot(context.AuthorityId);
                var ticket = Ticket(context, ticketId); RejectInFlight(context, authority, ticketId);
                if (ticket.Status != RebirthTicketStatus.NeedsReconciliation)
                    throw new InvalidOperationException("Only an unknown-outcome ticket can be reconciled.");
                context.Kernel.ReconcileTicket(CommandId(), ticketId, resolution, Now());
                Entry(context, c, tx, ticketId, null, "reconciliation", resolution, null, "human");
                return true;
            });
            // Reconciliation records knowledge, not permission to repeat effects. No Run or KickQueue.
        }
    }

    private static void RejectInFlight(ProjectContext context, RebirthSnapshot authority, string ticketId)
    {
        if (context.Runs.ContainsKey(ticketId) || authority.Runs.Any(r => r.TicketId == ticketId && r.Status is RebirthRunStatus.Queued or RebirthRunStatus.Running))
            throw new InvalidOperationException("Wait for the actual execution terminal receipt before changing its recovery decisions.");
    }

    private static void Source(ProjectContext context, SqliteConnection c, SqliteTransaction tx, GraphSource source) =>
        Execute(c, tx, """
            INSERT INTO graph_sources VALUES($project,$entity,$path,$kind,$hash,$detail,$now,$version)
            ON CONFLICT(project_id,entity_id,source_path,source_kind) DO UPDATE SET content_hash=$hash,detail=$detail,observed_utc=$now,version=$version
            """, ("$project", context.AuthorityId), ("$entity", source.EntityId), ("$path", source.Path), ("$kind", source.Kind),
            ("$hash", source.Hash), ("$detail", source.Detail), ("$now", Now()), ("$version", Revision(context, c, tx) + 1));

    public Task ScanAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            HumanOnly(); var context = Current;
            if (context.Scan is { IsCompleted: false }) return context.Scan.WaitAsync(cancellationToken);
            var options = context.Kernel.Atomic((c, tx) => Options(context, c, tx));
            if (!options.Enabled) throw new InvalidOperationException("Graph is disabled.");
            var task = ScanProjectAsync(context, options.ScanDepth, cancellationToken); context.Scan = task; return task;
        }
    }

    private async Task ScanProjectAsync(ProjectContext context, int depth, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, context.Lifetime.Token);
        context.ScanCancellation = linked;
        var acquired = false;
        try
        {
            await context.ScanGate.WaitAsync(linked.Token).ConfigureAwait(false); acquired = true;
            var scan = await Task.Run(() => GraphProjectScanner.Scan(context.Path, depth, linked.Token), linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (_disposed) return;
                Change(context, "Scanned real project sources", (c, tx) =>
                {
                    foreach (var module in scan.Modules)
                    {
                        var prior = Scalar(c, tx, "SELECT module_json FROM graph_modules WHERE project_id=$project AND module_id=$id AND origin='manual'", ("$project", context.AuthorityId), ("$id", module.Id)) as string;
                        if (prior is not null)
                        {
                            var manual = Deserialize<GraphModule>(prior);
                            if (manual.Name != module.Name || manual.ParentId != module.ParentId || !manual.SourcePaths.SequenceEqual(module.SourcePaths))
                                Entry(context, c, tx, null, module.Id, "conflict", Serialize(new { resolution = "preserved-manual", before = manual, observed = module }), null, "scanner");
                            continue;
                        }
                        Execute(c, tx, """
                            INSERT INTO graph_modules VALUES($project,$id,$parent,$json,'scan','scanner',$version)
                            ON CONFLICT(project_id,module_id) DO UPDATE SET parent_id=$parent,module_json=$json,version=$version
                            """, ("$project", context.AuthorityId), ("$id", module.Id), ("$parent", module.ParentId), ("$json", Serialize(module)), ("$version", Revision(context, c, tx) + 1));
                    }
                    foreach (var relation in scan.Relations)
                    {
                        var prior = Scalar(c, tx, "SELECT relation_json FROM graph_relations WHERE project_id=$project AND relation_id=$id AND origin='manual'", ("$project", context.AuthorityId), ("$id", relation.Id)) as string;
                        if (prior is not null)
                        {
                            if (Deserialize<GraphRelation>(prior) != relation)
                                Entry(context, c, tx, null, null, "conflict", Serialize(new { resolution = "preserved-manual", before = prior, observed = relation }), null, "scanner");
                            continue;
                        }
                        Execute(c, tx, """
                            INSERT INTO graph_relations VALUES($project,$id,$json,'scan','scanner',$version)
                            ON CONFLICT(project_id,relation_id) DO UPDATE SET relation_json=$json,version=$version
                            """, ("$project", context.AuthorityId), ("$id", relation.Id), ("$json", Serialize(relation)), ("$version", Revision(context, c, tx) + 1));
                    }
                    // Only a complete observation can establish deletion. Shrinking scan depth also
                    // does not prove that a deeper source disappeared.
                    var liveRelations = scan.Relations.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
                    if (scan.Notices.Count == 0)
                        foreach (var old in Query(c, tx, "SELECT relation_id,relation_json FROM graph_relations WHERE project_id=$project AND origin='scan'", r => (Id: r.GetString(0), Json: r.GetString(1)), ("$project", context.AuthorityId)))
                            if (!liveRelations.Contains(old.Id))
                            {
                                var relation = Deserialize<GraphRelation>(old.Json);
                                if (!scan.Modules.Any(m => m.Id == relation.SourceId)
                                    && !Module(context, c, tx, relation.SourceId).SourcePaths.Any(GraphProjectScanner.IsExcludedSourcePath)) continue;
                                Execute(c, tx, "DELETE FROM graph_relations WHERE project_id=$project AND relation_id=$id", ("$project", context.AuthorityId), ("$id", old.Id));
                                Execute(c, tx, "DELETE FROM graph_sources WHERE project_id=$project AND entity_id=$id", ("$project", context.AuthorityId), ("$id", old.Id));
                                Entry(context, c, tx, null, null, "source-removed", old.Json, null, "scanner");
                            }
                    var observed = scan.Modules.Select(m => m.Id).Concat(scan.Relations.Select(r => r.Id)).ToHashSet(StringComparer.Ordinal);
                    if (scan.Notices.Count == 0)
                    {
                        var oldModules = Query(c, tx, "SELECT module_json FROM graph_modules WHERE project_id=$project AND origin='scan'",
                            r => Deserialize<GraphModule>(r.GetString(0)), ("$project", context.AuthorityId));
                        foreach (var old in oldModules.OrderByDescending(m => m.SourcePaths.Select(p => p.Count(ch => ch == '/')).DefaultIfEmpty(0).Max()))
                        {
                            var excluded = old.SourcePaths.Any(GraphProjectScanner.IsExcludedSourcePath);
                            if (observed.Contains(old.Id) || !excluded && old.SourcePaths.Any(p => File.Exists(System.IO.Path.Combine(context.Path, p)) || Directory.Exists(System.IO.Path.Combine(context.Path, p)))) continue;
                            var referenced = Convert.ToInt64(Scalar(c, tx, "SELECT COUNT(*) FROM graph_ticket_modules WHERE module_id=$id", ("$id", old.Id))) != 0
                                || Convert.ToInt64(Scalar(c, tx, "SELECT COUNT(*) FROM graph_modules WHERE project_id=$project AND parent_id=$id", ("$project", context.AuthorityId), ("$id", old.Id))) != 0;
                            var attached = Query(c, tx, "SELECT relation_id,relation_json,origin FROM graph_relations WHERE project_id=$project",
                                r => (Id: r.GetString(0), Relation: Deserialize<GraphRelation>(r.GetString(1)), Origin: r.GetString(2)), ("$project", context.AuthorityId))
                                .Where(r => r.Relation.SourceId == old.Id || r.Relation.TargetId == old.Id).ToArray();
                            referenced |= attached.Any(r => r.Origin == "manual");
                            if (referenced)
                            {
                                if (!old.Summary.StartsWith("Missing source: ", StringComparison.Ordinal))
                                {
                                    Execute(c, tx, "UPDATE graph_modules SET module_json=$json,version=$version WHERE project_id=$project AND module_id=$id",
                                        ("$json", Serialize(old with { Summary = "Missing source: " + old.Summary, UpdatedUtc = Now() })), ("$version", Revision(context, c, tx) + 1), ("$project", context.AuthorityId), ("$id", old.Id));
                                    Entry(context, c, tx, null, old.Id, "conflict", "Source disappeared; identity retained because tickets, child modules or manual relations still reference it.", null, "scanner");
                                }
                                continue;
                            }
                            foreach (var relation in attached)
                            {
                                Execute(c, tx, "DELETE FROM graph_relations WHERE project_id=$project AND relation_id=$id", ("$project", context.AuthorityId), ("$id", relation.Id));
                                Execute(c, tx, "DELETE FROM graph_sources WHERE project_id=$project AND entity_id=$id", ("$project", context.AuthorityId), ("$id", relation.Id));
                            }
                            Execute(c, tx, "DELETE FROM graph_modules WHERE project_id=$project AND module_id=$id", ("$project", context.AuthorityId), ("$id", old.Id));
                            Execute(c, tx, "DELETE FROM graph_sources WHERE project_id=$project AND entity_id=$id", ("$project", context.AuthorityId), ("$id", old.Id));
                            Entry(context, c, tx, null, old.Id, "module-removed", Serialize(old), null, "scanner");
                        }
                    }
                    foreach (var entity in observed)
                        Execute(c, tx, "DELETE FROM graph_sources WHERE project_id=$project AND entity_id=$id AND source_kind<>'declared'", ("$project", context.AuthorityId), ("$id", entity));
                    foreach (var source in scan.Sources) Source(context, c, tx, source);
                    foreach (var notice in scan.Notices) Entry(context, c, tx, null, null, "scan-notice", notice, null, "scanner");
                    Entry(context, c, tx, null, null, "scan", Serialize(new { path = context.Path, depth, modules = scan.Modules.Count, relations = scan.Relations.Count, notices = scan.Notices.Count }), context.Path, "scanner");
                    return true;
                });
            }
        }
        finally
        {
            if (acquired) context.ScanGate.Release();
            lock (_gate) if (ReferenceEquals(context.ScanCancellation, linked)) context.ScanCancellation = null;
        }
    }
}
