using System.Text.Json;
using Fufucode.Core.Automation.Harnesses;
using Fufucode.Core.Automation.Hosting;
using Microsoft.Data.Sqlite;

namespace Fufucode.Core.Automation;

/// <summary>
/// The delegated scope of one automatic project. It is persisted separately from the control plan so
/// enabling Automatic is an explicit, auditable decision rather than an implicit default. The engine
/// refuses to advance a project whose scope was never recorded: an unknown budget or permission is
/// never treated as unlimited.
/// </summary>
public sealed record AutomationDelegation(long Concurrency, bool AutoReview, bool AutoIntegrate,
    long MaxDispatches, string Scope, string Model, long RecordedUtc, string RecordedBy)
{
    public string[] AllowedPaths { get; init; } = [];
}

/// <summary>Why the engine stopped and what it did in this pass. Never a claim that work happened.</summary>
public enum AutoAdvancementOutcome
{
    /// <summary>The project is not in Automatic mode; nothing was decided.</summary>
    NotAutomatic,
    /// <summary>Automatic is on but no delegation scope was recorded; a human must record it.</summary>
    NeedsDelegation,
    /// <summary>Live or settled-unknown workers exist; the engine only observes.</summary>
    WorkersActive,
    /// <summary>At least one decision (review, integration, dispatch) was applied.</summary>
    Advanced,
    /// <summary>Every approved target is accepted and integrated; nothing is left to do.</summary>
    ProjectCompleted,
    /// <summary>Everything left is blocked, unverifiable or over budget; the reason is preserved.</summary>
    Stalled
}

public sealed record AutoAdvancementReceipt(AutoAdvancementOutcome Outcome, string Detail,
    int ActiveWorkers, IReadOnlyList<string> Actions, int ReviewsAccepted, int IntegrationsAuthorized,
    int DispatchesQueued, int DispatchesRemaining)
{
    public bool AdvancedPastIdle => Outcome == AutoAdvancementOutcome.Advanced;
}

/// <summary>
/// The idle-reconciliation advancement state machine. Given a project with no active worker it walks
/// the same ordered decisions the coordination loop defines: settle reviews, authorize integration of
/// accepted work, and dispatch ready tasks while a concurrency slot is free. It never calls a model
/// itself, never retries an uncertain side effect, and stops with a real reason instead of spinning.
/// It only acts in Automatic mode under a recorded delegation scope.
/// </summary>
public sealed class AutoAdvancementEngine
{
    private readonly AutomationStore _store;
    private readonly ControlIntentConsumer _consumer;
    private readonly HarnessRunStore _runs;
    private readonly IProcessProbe _probe;

    public AutoAdvancementEngine(AutomationStore store, ControlIntentConsumer? consumer = null,
        IProcessProbe? probe = null)
    {
        _store = store;
        _consumer = consumer ?? new ControlIntentConsumer(store, probe);
        _runs = new HarnessRunStore(store);
        _probe = probe ?? SystemProcessProbe.Shared;
        Migrate();
    }

    private void Migrate()
    {
        using var c = _store.OpenConv();
        using var tx = c.BeginTransaction();
        using (var q = c.CreateCommand())
        {
            q.Transaction = tx;
            q.CommandText = """
                CREATE TABLE IF NOT EXISTS control_delegation (
                  project_id TEXT PRIMARY KEY, concurrency INTEGER NOT NULL, auto_review INTEGER NOT NULL,
                  auto_integrate INTEGER NOT NULL, max_dispatches INTEGER NOT NULL, scope TEXT NOT NULL,
                  model TEXT NOT NULL, allowed_paths TEXT NOT NULL, recorded_utc INTEGER NOT NULL,
                  recorded_by TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS control_advancement_runs (
                  advancement_id TEXT PRIMARY KEY, project_id TEXT NOT NULL, outcome TEXT NOT NULL,
                  detail TEXT NOT NULL, active_workers INTEGER NOT NULL, dispatches_queued INTEGER NOT NULL,
                  reviews_accepted INTEGER NOT NULL, integrations_authorized INTEGER NOT NULL,
                  occurred_utc INTEGER NOT NULL);
                """;
            q.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>
    /// Record the delegated scope that Automatic runs under. A project id already recorded keeps its
    /// exact original scope: a re-record must go through the project owner, never a silent overwrite.
    /// </summary>
    public AutomationDelegation RecordDelegation(string projectId, AutomationDelegation delegation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        if (delegation.Concurrency is < 1 or > 8)
            throw new ArgumentOutOfRangeException(nameof(delegation), "Concurrency must be within 1..8.");
        if (delegation.MaxDispatches < 1)
            throw new ArgumentOutOfRangeException(nameof(delegation), "A dispatch budget is required; unlimited is not a default.");
        ArgumentException.ThrowIfNullOrWhiteSpace(delegation.Scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(delegation.Model);
        using var c = _store.OpenConv();
        using var tx = c.BeginTransaction();
        using (var existing = AutomationStore.ConvSql(c, tx,
            "SELECT concurrency,auto_review,auto_integrate,max_dispatches,scope,model,allowed_paths,recorded_utc,recorded_by FROM control_delegation WHERE project_id=$p",
            ("$p", projectId)))
        using (var r = existing.ExecuteReader())
        {
            if (r.Read())
            {
                var current = new AutomationDelegation(r.GetInt64(0), r.GetInt64(1) != 0, r.GetInt64(2) != 0,
                    r.GetInt64(3), r.GetString(4), r.GetString(5), r.GetInt64(7), r.GetString(8))
                { AllowedPaths = JsonSerializer.Deserialize<string[]>(r.GetString(6)) ?? [] };
                if (current != delegation)
                    throw new InvalidOperationException(
                        "The automatic delegation scope is already recorded with different content; change it explicitly.");
                tx.Commit();
                return current;
            }
        }
        using (var insert = AutomationStore.ConvSql(c, tx, """
            INSERT INTO control_delegation(project_id,concurrency,auto_review,auto_integrate,max_dispatches,
                                           scope,model,allowed_paths,recorded_utc,recorded_by)
            VALUES($p,$c,$r,$i,$m,$scope,$model,$paths,$now,$by)
            """, ("$p", projectId), ("$c", delegation.Concurrency), ("$r", delegation.AutoReview ? 1 : 0),
            ("$i", delegation.AutoIntegrate ? 1 : 0), ("$m", delegation.MaxDispatches),
            ("$scope", delegation.Scope), ("$model", delegation.Model),
            ("$paths", JsonSerializer.Serialize(delegation.AllowedPaths)), ("$now", delegation.RecordedUtc),
            ("$by", delegation.RecordedBy)))
        {
            insert.ExecuteNonQuery();
            _store.AppendConvEvent(c, tx, "conv:delegation:" + projectId, "control.delegation.recorded",
                "", null, null, null,
                JsonSerializer.Serialize(new { projectId, delegation.MaxDispatches, delegation.Concurrency,
                    delegation.AutoReview, delegation.AutoIntegrate }), delegation.RecordedUtc);
        }
        tx.Commit();
        return delegation;
    }

    public AutomationDelegation? ReadDelegation(string projectId)
    {
        using var c = _store.OpenConv();
        using var q = c.CreateCommand();
        q.CommandText = "SELECT concurrency,auto_review,auto_integrate,max_dispatches,scope,model,allowed_paths,recorded_utc,recorded_by FROM control_delegation WHERE project_id=$p";
        q.Parameters.AddWithValue("$p", projectId);
        using var r = q.ExecuteReader();
        if (!r.Read()) return null;
        return new(r.GetInt64(0), r.GetInt64(1) != 0, r.GetInt64(2) != 0, r.GetInt64(3), r.GetString(4),
            r.GetString(5), r.GetInt64(7), r.GetString(8))
        { AllowedPaths = JsonSerializer.Deserialize<string[]>(r.GetString(6)) ?? [] };
    }

    /// <summary>
    /// One ordered advancement pass. It is idempotent: re-running it never repeats a side effect,
    /// because every action it takes is backed by a durable receipt or unique row in the store.
    /// </summary>
    public async Task<AutoAdvancementReceipt> AdvanceAsync(string projectId, long nowUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        var snapshot = _store.ReadControlSnapshot();
        if (snapshot.Mode != DevelopmentMode.Automatic)
            return Receipt(AutoAdvancementOutcome.NotAutomatic, "项目不是自动开发模式；本引擎不做任何决定。", 0, [], 0, 0, 0, 0);
        if (ReadDelegation(projectId) is not { } delegation)
            return Receipt(AutoAdvancementOutcome.NeedsDelegation,
                "自动开发尚未登记委托范围（并发/预算/审核集成授权）；未登记的权限不被当成无限。", 0, [], 0, 0, 0, 0);

        var active = CountActiveWorkers();
        if (active > 0)
            return Receipt(AutoAdvancementOutcome.WorkersActive,
                $"仍有 {active} 个在途执行者；本引擎只观察，不并发派工。", active, [], 0, 0, 0, 0);

        var actions = new List<string>();
        var reviews = 0;
        var integrations = 0;
        var dispatches = 0;
        var dispatchesUsed = CountControlDispatches();
        var budgetLeft = (int)Math.Max(0, delegation.MaxDispatches - dispatchesUsed);

        // 1. Settle pending reviews inside the delegated scope. Only a current, stopped, ready delivery
        //    with a recorded card is acceptable; anything else is left for a human.
        if (delegation.AutoReview)
        {
            foreach (var item in AutoReviewCandidates())
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    _store.ApplyControlReview(new ControlReviewCommand(
                        "auto-review:" + item.PartId + ":" + item.Revision, _store.ReadControlSnapshot().Revision,
                        "auto-advancement", item.TaskId, item.PartId, item.Revision, item.RunId, item.HeadSha,
                        item.EvidenceToken, AutomationDecision.Accept, null,
                        JsonSerializer.Serialize(new { authority = item.EvidenceToken, channel = "auto",
                            project = projectId, reason = "delivery settled inside the recorded delegation scope" })), nowUtc);
                    reviews++;
                    actions.Add("review:accepted:" + item.PartId + "@" + item.Revision);
                }
                catch (Exception error) when (error is InvalidOperationException or KeyNotFoundException)
                {
                    actions.Add("review:deferred:" + item.PartId + ":" + error.GetType().Name);
                }
            }
        }

        // 2. Authorize integration of accepted, eligible candidates. This never merges Git; it records
        //    the authorization the integrator consumes, so no side effect is duplicated by a retry.
        if (delegation.AutoIntegrate)
        {
            foreach (var package in snapshot.Packages.Where(p => !p.Superseded && p.Approved))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var candidate = _store.ReadControlIntegrationCandidate(package.Spec.Id);
                if (candidate is not { Eligible: true, Integrated: false, Authorized: false } current) continue;
                try
                {
                    _store.ApplyControlIntegration(new ControlIntegrationCommand(
                        "auto-integration:" + current.Package.Spec.Id + ":" + current.Package.Version + ":" + current.EvidenceToken,
                        _store.ReadControlSnapshot().Revision, "auto-advancement", ControlIntegrationAction.Authorize,
                        current.Package.Spec.Id, current.Package.Version, current.EvidenceToken,
                        Reason: "accepted evidence inside the recorded delegation scope"), nowUtc);
                    integrations++;
                    actions.Add("integration:authorized:" + current.Package.Spec.Id);
                }
                catch (Exception error) when (error is InvalidOperationException or KeyNotFoundException)
                {
                    actions.Add("integration:deferred:" + current.Package.Spec.Id + ":" + error.GetType().Name);
                }
            }
        }

        // 3. Dispatch ready, unbound intents while a concurrency slot and the dispatch budget allow it.
        var slots = (int)Math.Max(0, delegation.Concurrency - CountCommittedControls());
        foreach (var intent in ReadyIntents())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (dispatches >= slots || dispatches >= budgetLeft) break;
            var prepared = PrepareDispatch(projectId, intent, delegation);
            if (prepared is null) continue;
            try
            {
                await _consumer.QueueAsync(prepared, nowUtc, cancellationToken).ConfigureAwait(false);
                dispatches++;
                actions.Add("dispatch:queued:" + intent.PackageId + "@" + intent.PackageVersion);
            }
            catch (Exception error) when (error is InvalidOperationException or FileNotFoundException
                or DirectoryNotFoundException)
            {
                actions.Add("dispatch:deferred:" + intent.PackageId + ":" + error.GetType().Name);
            }
        }

        var remaining = Math.Max(0, budgetLeft - dispatches);
        if (reviews > 0 || integrations > 0 || dispatches > 0)
            return Persist(projectId, AutoAdvancementOutcome.Advanced,
                $"推进：接受 {reviews} 项审核、授权 {integrations} 项集成、派发 {dispatches} 个任务；剩余派发预算 {remaining}。",
                active, actions, reviews, integrations, dispatches, remaining, nowUtc);

        // 4. No progress this pass: classify the terminal reason honestly, never an endless "running".
        var approved = snapshot.Packages.Where(p => !p.Superseded && p.Approved).ToArray();
        var integrated = approved.Count(p => _store.ReadControlIntegrationCandidate(p.Spec.Id) is { Integrated: true });
        if (approved.Length > 0 && integrated == approved.Length)
            return Persist(projectId, AutoAdvancementOutcome.ProjectCompleted,
                $"全部 {approved.Length} 个已批准目标均已接受且整合；项目进入完成终态。", active, actions,
                reviews, integrations, dispatches, remaining, nowUtc);

        var reason = budgetLeft == 0 ? "派发预算已用尽，停止新的付费动作；在途执行按委托处理。"
            : AutoReviewCandidates().Count > 0 ? "存在可审交付但本通道未启用自动审核，保留给人工决定。"
            : _store.ReadControlInbox().Items.Any(i => i.Kind == "review") ? "存在证据不完整或执行者未停写的交付，不自动接受。"
            : ReadyIntents().Count > 0 ? "有就绪任务但无可用并发空位或工作树未就绪。"
            : "没有可推进的动作；等待新交付、依赖解锁或人工决定。";
        return Persist(projectId, AutoAdvancementOutcome.Stalled, reason, active, actions,
            reviews, integrations, dispatches, remaining, nowUtc);
    }

    // ------------------------------------------------------------------ observations

    /// <summary>Live or unknown-ownership workers plus queued-but-not-started control work.</summary>
    public int CountActiveWorkers()
    {
        using var c = _store.OpenConv();
        using var q = c.CreateCommand();
        q.CommandText = """
            SELECT (SELECT COUNT(*) FROM harness_runs WHERE status IN ('Dispatching','Running','Unknown'))
                 + (SELECT COUNT(*) FROM host_requests WHERE state IN (0,1))
            """;
        return Convert.ToInt32(q.ExecuteScalar());
    }

    /// <summary>Distinct control packages that already consumed a dispatch, for the budget.</summary>
    public int CountControlDispatches()
    {
        using var c = _store.OpenConv();
        using var q = c.CreateCommand();
        q.CommandText = "SELECT COUNT(*) FROM control_bindings";
        return Convert.ToInt32(q.ExecuteScalar());
    }

    /// <summary>Control work occupying a concurrency slot right now (queued, started or unknown).</summary>
    private int CountCommittedControls()
    {
        using var c = _store.OpenConv();
        using var q = c.CreateCommand();
        q.CommandText = """
            SELECT COUNT(*) FROM control_bindings b JOIN host_requests q ON q.run_id=b.run_id
            WHERE q.state IN (0,1)
            UNION ALL
            SELECT COUNT(*) FROM control_bindings b JOIN harness_runs h ON h.run_id=b.run_id
            WHERE h.status IN ('Dispatching','Running','Unknown')
            """;
        using var r = q.ExecuteReader();
        var total = 0;
        while (r.Read()) total += r.GetInt32(0);
        return total;
    }

    /// <summary>Approved packages whose execution intent is still awaiting trusted binding.</summary>
    public IReadOnlyList<ControlIntent> ReadyIntents()
    {
        var snapshot = _store.ReadControlSnapshot();
        return snapshot.Intents
            .Where(i => i.Status == "awaiting_binding")
            .Where(i => snapshot.Packages.Any(p => p.Spec.Id == i.PackageId && p.Version == i.PackageVersion && p.Approved))
            .ToArray();
    }

    /// <summary>One reviewable delivery: the inbox item's exact evidence plus its revision identity.</summary>
    public sealed record AutoReviewCandidate(string PartId, string TaskId, long Revision, string RunId,
        string HeadSha, string EvidenceToken);

    /// <summary>
    /// Actionable review items that also carry a concrete stopped revision. An item without a real
    /// run/HEAD is not auto-acceptable evidence, so it stays for a human.
    /// </summary>
    public IReadOnlyList<AutoReviewCandidate> AutoReviewCandidates()
    {
        var candidates = new List<AutoReviewCandidate>();
        foreach (var item in _store.ReadControlInbox().Items.Where(i => i.Kind == "review" && i.Actionable))
        {
            var part = _store.GetPart(item.ObjectId);
            if (part.CurrentRevision <= 0) continue;
            var revision = _store.ListRevisions(item.ObjectId)
                .LastOrDefault(r => r.RevisionNumber == part.CurrentRevision);
            if (revision is null || string.IsNullOrWhiteSpace(revision.RunId) ||
                string.IsNullOrWhiteSpace(revision.HeadSha)) continue;
            candidates.Add(new(item.ObjectId, part.TaskId, part.CurrentRevision, revision.RunId,
                revision.HeadSha, item.EvidenceToken));
        }
        return candidates;
    }

    /// <summary>
    /// Resolve one ready intent to a real registered thread/task with a created worktree. Returns null
    /// when the plan has no matching execution identity yet: the engine never invents a thread, task
    /// or worktree, so a missing identity is a human wiring gap, not something to fabricate.
    /// </summary>
    private ControlExecutionBinding? PrepareDispatch(string projectId, ControlIntent intent,
        AutomationDelegation delegation)
    {
        var snapshot = _store.ReadControlSnapshot();
        var package = snapshot.Packages.SingleOrDefault(p => p.Spec.Id == intent.PackageId &&
            p.Version == intent.PackageVersion && p.Approved);
        if (package is null) return null;
        if (package.Spec.Model != delegation.Model) return null;
        var thread = _store.ListThreads().FirstOrDefault(t => t.ProjectId == projectId &&
                t.Model == package.Spec.Model && t.Harness == package.Spec.Harness);
        if (thread is null || thread.Status != ThreadStatus.Active) return null;
        var task = _store.ListTasks(thread.ThreadId).FirstOrDefault(t => t.Status == AutomationTaskStatus.Planned);
        if (task is null) return null;
        if (!Path.IsPathFullyQualified(thread.Worktree) || !Directory.Exists(thread.Worktree)) return null;
        if (delegation.AllowedPaths.Length > 0 && !delegation.AllowedPaths.Any(p =>
                thread.Worktree.StartsWith(Path.GetFullPath(p), StringComparison.OrdinalIgnoreCase))) return null;
        var runtime = new HarnessRuntime("auto-" + intent.PackageId, package.Spec.Harness,
            ResolveExecutable(package.Spec.Harness), [], "auto-v1");
        return new ControlExecutionBinding("auto-bind:" + intent.Id, snapshot.Revision, intent.Id,
            intent.PackageVersion, thread.ThreadId, task.TaskId, runtime, null);
    }

    /// <summary>
    /// A registered runtime is required before dispatch. The engine does not guess a provider: an
    /// unresolved harness is reported as a wiring gap by returning an unlaunchable path, which the
    /// consumer rejects, instead of silently substituting a different backend.
    /// </summary>
    private static string ResolveExecutable(string harness)
    {
        var registered = Environment.GetEnvironmentVariable("FUFUCODE_" + harness.ToUpperInvariant() + "_RUNTIME");
        return string.IsNullOrWhiteSpace(registered) ? "<unregistered-" + harness + "-runtime>" : registered;
    }

    private AutoAdvancementReceipt Persist(string projectId, AutoAdvancementOutcome outcome, string detail,
        int active, IReadOnlyList<string> actions, int reviews, int integrations, int dispatches,
        int remaining, long nowUtc)
    {
        using (var c = _store.OpenConv())
        using (var tx = c.BeginTransaction())
        {
            using var q = AutomationStore.ConvSql(c, tx, """
                INSERT INTO control_advancement_runs(advancement_id,project_id,outcome,detail,active_workers,
                  dispatches_queued,reviews_accepted,integrations_authorized,occurred_utc)
                VALUES($id,$p,$outcome,$detail,$active,$d,$r,$i,$now)
                """, ("$id", "adv-" + Guid.NewGuid().ToString("N")), ("$p", projectId),
                ("$outcome", outcome.ToString()), ("$detail", detail), ("$active", active),
                ("$d", dispatches), ("$r", reviews), ("$i", integrations), ("$now", nowUtc));
            q.ExecuteNonQuery();
            _store.AppendConvEvent(c, tx, "conv:advancement:" + projectId + ":" + nowUtc + ":" + outcome,
                "control.advancement", "", null, null, null,
                JsonSerializer.Serialize(new { projectId, outcome = outcome.ToString(), detail, actions }),
                nowUtc);
            tx.Commit();
        }
        return Receipt(outcome, detail, active, actions, reviews, integrations, dispatches, remaining);
    }

    private static AutoAdvancementReceipt Receipt(AutoAdvancementOutcome outcome, string detail, int active,
        IReadOnlyList<string> actions, int reviews, int integrations, int dispatches, int remaining) =>
        new(outcome, detail, active, actions, reviews, integrations, dispatches, remaining);
}
