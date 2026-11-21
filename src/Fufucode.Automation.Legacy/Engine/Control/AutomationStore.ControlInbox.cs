using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Fufucode.Core.Automation;

public sealed partial class AutomationStore
{
    /// <summary>A coherent read of actual planning, U1 parts, knowledge and integration decisions. Never mutates state.</summary>
    public ControlInboxSnapshot ReadControlInbox()
    {
        using var c = Open(); using var tx = c.BeginTransaction();
        var result = ReadControlInbox(c, tx); tx.Commit(); return result;
    }

    private ControlInboxSnapshot ReadControlInbox(SqliteConnection c, SqliteTransaction tx)
    {
        var state = ReadControlSnapshot(c, tx); var items = new List<ControlDecisionItem>();
        void Add(string id, string kind, string obj, long? version, bool actionable, string? blocked,
            string[] options, string command, object context, string? token = null)
        {
            var json = context is string text ? text : JsonSerializer.Serialize(context);
            items.Add(new(id, kind, obj, version, token ?? Sha(json), actionable, blocked, options, command, json));
        }
        foreach (var package in state.Packages.Where(p => !p.Superseded))
        {
            if (!package.Approved)
                Add($"plan:{package.Spec.Id}:{package.Version}", "plan", package.Spec.Id, package.Version, true, null,
                    ["approve"], "control.apply", new { package, knowledge = state.Knowledge.Facts.Where(f => (package.Spec.KnowledgeRefs ?? []).Contains(f.Key, StringComparer.Ordinal)).ToArray() });
            var outcome = ReadControlOutcome(c, tx, package.Spec.Id);
            if (outcome.TaskId is not null)
            {
                // Cards are opaque evidence, not a source of decision keywords or code to execute.
                var stopped = ControlReviewWriterStopped(c, tx, package.Spec.Id);
                var threadReady = ControlReviewThreadReady(c, tx, outcome.TaskId);
                var reviewable = stopped && package.Approved && threadReady;
                foreach (var part in ReadControlDecisionParts(c, tx, outcome.TaskId, stopped).Where(p => p.Status == DeliveryPartStatus.Delivered && p.Revision > 0 && p.RunId is not null && p.HeadSha is not null))
                    Add($"review:{part.PartId}:{part.Revision}", "review", part.PartId, part.Revision, reviewable, !stopped ? "writer_not_settled" : !package.Approved ? "contract_not_approved" : !threadReady ? "thread_frozen" : null,
                        reviewable ? ["accept", "rework", "blocked", "defer"] : [], "control.review", new { package, outcome.TaskId, part });
                foreach (var part in ReadControlDecisionParts(c, tx, outcome.TaskId, stopped).Where(p => p.Status is DeliveryPartStatus.Blocked or DeliveryPartStatus.Deferred))
                    Add($"reconsider:{part.PartId}:{part.Revision}:{part.ReviewId}", "reconsider", part.PartId, part.Revision, reviewable,
                        !stopped ? "writer_not_settled" : !package.Approved ? "contract_not_approved" : !threadReady ? "thread_frozen" : null,
                        reviewable ? ["reopen"] : [], "control.reconsider", new { package, outcome.TaskId, part });
                foreach (var part in ReadControlDecisionParts(c, tx, outcome.TaskId, stopped).Where(p => p.Status == DeliveryPartStatus.ReworkRequested &&
                    !state.Repairs.Any(r => r.DecisionId == p.ReviewId)))
                {
                    // Repair authorization follows the same dispatch rule as initial binding: a manual
                    // project never dispatches, and the two autonomous modes both may. AutoAdvancementEngine
                    // is the automatic-mode decision maker; in semi-automatic a human still confirms each one.
                    var allowed = reviewable && ControlExecutionMode.AllowsDispatch(state.Mode);
                    Add($"repair:{part.PartId}:{part.ReviewId}", "repair", part.PartId, part.Revision, allowed,
                        !reviewable ? "writer_or_contract_not_ready" : !allowed ? "manual_mode" : null,
                        allowed ? ["request_repair"] : [], "control.repair", new { package, outcome.TaskId, part });
                }
            }
            var candidate = ReadControlIntegrationCandidate(c, tx, package.Spec.Id);
            if (candidate is not null && outcome.Accepted && !candidate.Integrated)
                if (!candidate.Authorized)
                    Add($"integration:{package.Spec.Id}:{package.Version}:{candidate.EvidenceToken}", "integration", package.Spec.Id, package.Version,
                        candidate.Eligible, candidate.Eligible ? null : "integration_requirements_not_ready",
                        candidate.Eligible ? ["authorize"] : [], "control.integration", candidate.ContextJson, candidate.EvidenceToken);
        }
        foreach (var group in ReadControlGroups(c, tx).Where(g => !g.Superseded && g.Spec.Relation == ControlRelation.Competitive && !g.SelectionCurrent))
        {
            var integrated = ControlGroupHasIntegration(c, tx, group.Spec.Id);
            var selectable = !integrated && group.Members.Any(m => m.Approved && m.Accepted && m.TaskId is not null && ControlGroupBaselineSatisfied(c, tx, group.Spec, m.TaskId));
            Add("choice:" + group.Spec.Id, "choice", group.Spec.Id, null, selectable,
                integrated ? "already_integrated_group" : selectable ? null : "no_eligible_accepted_candidate", selectable ? ["select"] : [], "control.relation", group);
        }
        foreach (var proposal in state.Knowledge.Proposals.Where(p => p.Status == "pending"))
        {
            var current = proposal.PreviousVersion == CurrentKnowledgeVersion(c, tx, proposal.Key);
            Add("knowledge:" + proposal.Id, "knowledge", proposal.Id, proposal.PreviousVersion, true, current ? null : "obsolete_fact_version",
                current ? ["publish", "reject"] : ["reject"], "control.knowledge", proposal);
        }
        foreach (var adoption in state.Knowledge.Adoptions.Where(a => a.Status == "pending"))
            Add($"adoption:{adoption.PackageId}:{adoption.PackageVersion}:{adoption.Key}:{adoption.KnowledgeVersion}", "adoption", adoption.PackageId,
                adoption.PackageVersion, adoption.BlockedReason is null, adoption.BlockedReason, adoption.BlockedReason is null ? ["adopt"] : [], "control.knowledge",
                new { adoption, package = state.Packages.Single(p => p.Spec.Id == adoption.PackageId), fact = state.Knowledge.Facts.Single(f => f.Key == adoption.Key && f.Version == adoption.KnowledgeVersion) });
        foreach (var restructure in state.Restructures.Where(p => p.Status == "pending"))
            Add("restructure:" + restructure.Id, "restructure", restructure.Id, null, true,
                !restructure.ContractCurrent ? "obsolete_contract" : restructure.BlockedReason,
                restructure.ContractCurrent && restructure.BlockedReason is null ? ["approve", "reject"] : ["reject"], "control.restructure", restructure);
        foreach (var question in state.Knowledge.Questions.Where(q => q.Answer is null))
            Add("question:" + question.Id, "question", question.Id, null, question.ContractCurrent, question.ContractCurrent ? null : "obsolete_contract",
                question.ContractCurrent ? ["answer"] : [], "control.knowledge", question);
        foreach (var authorization in state.IntegrationAuthorizations.Where(a => a.Status == "authorized"))
        {
            var candidate = ReadControlIntegrationCandidate(c, tx, authorization.PackageId);
            if (candidate is { Integrated: true } && candidate.Package.Version == authorization.PackageVersion && candidate.TaskId == authorization.TaskId) continue;
            Add("authorization:" + authorization.Id, "authorization", authorization.Id, authorization.PackageVersion, true,
                authorization.ContractCurrent ? null : "obsolete_contract", ["revoke"], "control.integration", authorization, authorization.EvidenceToken);
        }
        using var cursor = ControlSql(c, tx, "SELECT COALESCE(MAX(cursor),0) FROM automation_events");
        var watermark = Convert.ToInt64(cursor.ExecuteScalar());
        return new(state.Revision, watermark, state.Mode, items.OrderBy(i => i.Id, StringComparer.Ordinal).ToArray());
    }

    private static bool ControlReviewAdmissible(SqliteConnection c, SqliteTransaction tx, string task)
    {
        using var q = ControlSql(c, tx, "SELECT i.package_id FROM control_bindings b JOIN control_intents i ON i.intent_id=b.intent_id WHERE b.task_id=$task", ("$task", task));
        if (q.ExecuteScalar() is not string id) return true; // Explicit legacy U1 task keeps its existing protocol.
        var current = CurrentControlPackage(c, tx, id);
        return !current.Superseded && current.Approved && ReadControlOutcome(c, tx, id).TaskId == task && ControlReviewWriterStopped(c, tx, id) && ControlReviewThreadReady(c, tx, task);
    }

    private static bool ControlReviewThreadReady(SqliteConnection c, SqliteTransaction tx, string task)
    {
        using var q = ControlSql(c, tx, "SELECT t.status FROM threads t JOIN tasks a ON a.thread_id=t.thread_id WHERE a.task_id=$task", ("$task", task));
        return q.ExecuteScalar() is long status && status != (int)ThreadStatus.Frozen;
    }

    private static bool ControlReviewWriterStopped(SqliteConnection c, SqliteTransaction tx, string id)
    {
        using var q = ControlSql(c, tx, """
            SELECT NOT EXISTS(SELECT 1 FROM control_writer_bindings b JOIN control_intents i ON i.intent_id=b.intent_id
              LEFT JOIN host_requests q ON q.run_id=b.run_id LEFT JOIN harness_runs h ON h.run_id=b.run_id
              WHERE i.package_id=$id AND (q.state IS NULL OR q.state IN (0,1) OR (q.state=2 AND h.status IS NULL)
                OR h.status IN ('Dispatching','Running','Unknown')))
              AND NOT EXISTS(SELECT 1 FROM control_writer_bindings b JOIN control_intents i ON i.intent_id=b.intent_id
                JOIN threads owner ON owner.thread_id=b.thread_id JOIN harness_runs h
                  ON h.thread_id=b.thread_id OR
                    rtrim(replace(upper(h.worktree),'/',char(92)),char(92))=rtrim(replace(upper(owner.worktree),'/',char(92)),char(92))
                WHERE i.package_id=$id AND h.status IN ('Dispatching','Running','Unknown'))
              AND NOT EXISTS(SELECT 1 FROM control_writer_bindings b JOIN control_intents i ON i.intent_id=b.intent_id
                JOIN threads owner ON owner.thread_id=b.thread_id JOIN host_requests q JOIN threads pending ON pending.thread_id=q.thread_id
                WHERE i.package_id=$id AND q.state IN (0,1) AND (q.thread_id=b.thread_id OR
                  rtrim(replace(upper(pending.worktree),'/',char(92)),char(92))=rtrim(replace(upper(owner.worktree),'/',char(92)),char(92))))
            """, ("$id", id));
        return Convert.ToInt64(q.ExecuteScalar()) != 0;
    }
}
