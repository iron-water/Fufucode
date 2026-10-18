using Fufucode.Core.Automation;
using Fufucode.Core.Automation.Harnesses;

namespace Fufucode.Core.Features.LocalReview;

/// <summary>
/// Adapter from the narrow review seam onto the verified harness execution backend
/// (HarnessExecutor + registered HarnessRuntime + the U1 project store). It reuses that backend
/// unchanged: durable dispatch before launch, terminal-state checks, replay dedup, and the
/// honest Unknown on cancellation. Two properties are deliberate:
///
/// - The review thread's worktree is a module-owned scratch directory, never the reviewed
///   repository. A batch harness runs with write access to its own worktree, and a review must not
///   be able to modify the user's checkout.
/// - Findings are only accepted through <see cref="LocalReviewFindingsParser"/> against the frozen
///   snapshot. The transport reaching a terminal state is not treated as a completed review.
/// </summary>
public sealed class LocalReviewHarnessExecutor : ILocalReviewExecutor
{
    private readonly Func<AutomationStore> _store;
    private readonly HarnessRuntime _runtime;
    private readonly string _scratchRoot;
    private readonly string _evidenceRoot;
    private readonly string _projectId;
    private readonly string? _provider;
    private long _sequence;

    /// <param name="store">Opens the project automation store (host-injected; the module never
    /// decides where desktop data lives).</param>
    /// <param name="runtime">A registered runtime with absolute executable and verified version.</param>
    /// <param name="dataRoot">Module-owned directory for review scratch and evidence.</param>
    public LocalReviewHarnessExecutor(Func<AutomationStore> store, HarnessRuntime runtime,
        string dataRoot, string projectId, string model, string? provider = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        _store = store;
        _runtime = runtime;
        _scratchRoot = Path.Combine(dataRoot, "scratch");
        _evidenceRoot = Path.Combine(dataRoot, "evidence");
        _projectId = projectId;
        _provider = provider;
        // The harness backend binds one model per thread; the review model is explicit here so the
        // UI and exports can show what actually ran, and probes never inherit a configuration default.
        Model = model;
    }

    public string Model { get; }

    public async Task<LocalReviewRunResult> ReviewAsync(LocalReviewScopeResolution scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var finished = DateTimeOffset.UtcNow;
        // Cancellation at any stage (snapshot capture, freshness check, or after dispatch) yields a
        // classified result the UI can show; it never escapes as a bare exception.
        FileDiffSnapshot? snapshot = null;
        string? runId = null;
        string? worktree = null;
        try
        {
        if (scope.Status != LocalReviewScopeStatus.Resolved)
            return Fail(scope, null, scope.Error ?? "scope-not-resolved", finished);
        if (scope.SnapshotCapability != LocalReviewSnapshotCapability.FullSnapshot)
            return Fail(scope, null,
                "shared-snapshot-api-gap: " + LocalReviewScopeResolver.MissingSharedApiFor(scope.Kind), finished);

        snapshot = await LocalReviewScopeResolver.CaptureFixedSnapshotAsync(scope, cancellationToken);
        if (snapshot is null || snapshot.Status != GitDiffSnapshotStatus.Captured)
            return Fail(scope, snapshot,
                snapshot?.Error ?? "snapshot-capture-refused", finished);
        if (!await snapshot.IsFreshAsync(cancellationToken))
            return new LocalReviewRunResult(scope, snapshot, LocalReviewRunStatus.StaleAfterReview,
                [], [], Model, null, "snapshot-stale-before-review", false, DateTimeOffset.UtcNow);

        var payload = LocalReviewPayload.BuildResult(scope, snapshot);
        runId = $"review-{Interlocked.Increment(ref _sequence):D6}-{Guid.NewGuid():N}";
        var threadId = "review-thread-" + runId;
        var taskId = "review-task-" + runId;
        worktree = Directory.CreateDirectory(Path.Combine(_scratchRoot, runId)).FullName;
        Directory.CreateDirectory(Path.Combine(_evidenceRoot, runId));

        using var store = _store();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        store.CreateThread(threadId, _projectId, _runtime.Harness, Model, worktree,
            null, now);
        store.CreateTask(taskId, threadId, "local review of " + scope.Description,
            snapshot.HeadCommit, now);

        var executor = new HarnessExecutor(store, _evidenceRoot, hostId: "local-review");
        var request = new HarnessRequest(runId, threadId, taskId, Model, payload.Text, _provider);
        var cancelled = false;
        HarnessRun run;
        try
        {
            run = await executor.ExecuteAsync(_runtime, request, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            // The backend journals its durable claim before it launches anything, so a missing row
            // proves nothing was dispatched. Reading it through Get() would throw KeyNotFound and
            // crash the page for a cancellation that happened in the pre-dispatch window.
            run = TryReadRun(executor.Runs, runId);
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException
            or DirectoryNotFoundException or FileNotFoundException or NotSupportedException
            or IOException or UnauthorizedAccessException)
        {
            // Identity/schema/binding faults from the durable backend are Failed outcomes, shown
            // with their reason; they never crash the review page.
            // An exception alone does not prove that a launched writer has stopped.
            var bindingScratch = RetainOrRelease(worktree, retain: true);
            return new LocalReviewRunResult(scope, snapshot, LocalReviewRunStatus.Failed, [], [],
                Model, runId, error.Message, false, DateTimeOffset.UtcNow)
            {
                RetainedScratch = bindingScratch,
            };
        }

        var finishedAt = DateTimeOffset.UtcNow;
        // An unknown end state (cancellation after dispatch, crash, missing terminal event) must keep
        // its scratch directory and identity so a human can reconcile it. Deleting the evidence of an
        // uncertain run destroys exactly what the reconciliation needs.
        if (cancelled || run.Status is HarnessRunStatus.Unknown or HarnessRunStatus.Interrupted)
        {
            return new LocalReviewRunResult(scope, snapshot, LocalReviewRunStatus.Cancelled, [], [],
                Model, runId,
                "review run ended in an unknown state; the backend quarantines it and never auto-resends. " +
                "Reconcile before dispatching another review. Scratch retained for inspection.",
                false, finishedAt)
            {
                UnreviewedPaths = payload.UnreviewedPaths,
                RetainedScratch = RetainOrRelease(worktree, retain: true),
            };
        }
        if (run.Status != HarnessRunStatus.Completed)
        {
            // A definite terminal failure: nothing left to reconcile, release the scratch.
            return new LocalReviewRunResult(scope, snapshot, LocalReviewRunStatus.Failed, [], [],
                Model, runId, run.Error ?? "run-failed", false, finishedAt)
            {
                UnreviewedPaths = payload.UnreviewedPaths,
                RetainedScratch = RetainOrRelease(worktree, retain: false),
            };
        }

        var parsed = LocalReviewFindingsParser.Parse(run.Output, snapshot);
        // Freshness is re-checked before any "complete" classification: an empty answer during a
        // concurrent source change is still a stale review, never a clean "no findings".
        var fresh = await snapshot.IsFreshAsync(CancellationToken.None);
        var retained = RetainOrRelease(worktree, retain: false);

        // 1) Structurally unreadable, or every listed finding rejected: invalid response, not a review.
        var wholeFailure = parsed.Rejected.FirstOrDefault(issue =>
            LocalReviewFindingsParser.WholeResponseFailures.Contains(issue.Reason));
        if (parsed.ItemsSeen == 0 && wholeFailure is not null)
            return Classified(scope, snapshot, payload.UnreviewedPaths, retained,
                LocalReviewRunStatus.InvalidResponse, [], parsed.Rejected, runId,
                wholeFailure.Reason, parsed.Truncated, finishedAt);
        if (parsed.AllFindingsInvalid)
            return Classified(scope, snapshot, payload.UnreviewedPaths, retained,
                LocalReviewRunStatus.InvalidResponse, [], parsed.Rejected, runId,
                $"all-{parsed.ItemsSeen}-listed-findings-rejected", parsed.Truncated, finishedAt);

        // 2) Scope not fully sent: the answer cannot certify a scope it never saw.
        if (payload.UnreviewedPaths.Count > 0)
            return Classified(scope, snapshot, payload.UnreviewedPaths, retained,
                LocalReviewRunStatus.IncompleteScope, parsed.Findings, parsed.Rejected, runId,
                "payload-incomplete: " + payload.UnreviewedPaths.Count +
                " file(s) not sent to the reviewer", parsed.Truncated, finishedAt);

        // 3) Repository moved under the snapshot: stale regardless of an empty answer.
        if (!fresh)
            return Classified(scope, snapshot, payload.UnreviewedPaths, retained,
                LocalReviewRunStatus.StaleAfterReview, parsed.Findings, parsed.Rejected, runId,
                "repository changed after the snapshot was fixed", parsed.Truncated, finishedAt);

        // 4) Only now is an empty answer "no findings".
        if (parsed.Findings.Count == 0 && parsed.Rejected.Count == 0)
            return Classified(scope, snapshot, payload.UnreviewedPaths, retained,
                LocalReviewRunStatus.NoFindings, [], parsed.Rejected, runId, null,
                parsed.Truncated, finishedAt);

        return Classified(scope, snapshot, payload.UnreviewedPaths, retained,
            LocalReviewRunStatus.Completed, parsed.Findings, parsed.Rejected, runId,
            parsed.Truncated ? "response exceeded the byte limit" : null, parsed.Truncated, finishedAt);
        }
        catch (OperationCanceledException)
        {
            // Cancelled before/while dispatching: if a scratch already exists and the run identity
            // was journaled, keep it; a dispatch that never happened leaves nothing to reconcile.
            return new LocalReviewRunResult(scope, snapshot, LocalReviewRunStatus.Cancelled, [], [],
                Model, runId, runId is null
                    ? "cancelled before dispatch: nothing was sent to the reviewer"
                    : "review run ended in an unknown state; the backend quarantines it and never " +
                      "auto-resends. Reconcile before dispatching another review. Scratch retained.",
                false, DateTimeOffset.UtcNow)
            {
                RetainedScratch = RetainOrRelease(worktree, retain: runId is not null),
            };
        }
    }

    private LocalReviewRunResult Classified(LocalReviewScopeResolution scope,
        FileDiffSnapshot snapshot, IReadOnlyList<string> unreviewed, string? retained,
        LocalReviewRunStatus status, IReadOnlyList<LocalReviewFinding> findings,
        IReadOnlyList<LocalReviewParseIssue> rejected, string runId, string? error,
        bool truncated, DateTimeOffset finishedAt) =>
        new(scope, snapshot, status, findings, rejected, Model, runId, error,
            truncated, finishedAt)
        {
            UnreviewedPaths = unreviewed,
            RetainedScratch = retained,
        };

    /// <summary>Returns the retained scratch path, or null after releasing it. Retention is the
    /// deliberate choice for runs whose end state cannot be proven.</summary>
    private string? RetainOrRelease(string? worktree, bool retain)
    {
        if (worktree is null) return null;
        if (retain) return worktree;
        TryDeleteDirectory(worktree);
        return null;
    }

    private static LocalReviewRunResult Fail(LocalReviewScopeResolution scope, FileDiffSnapshot? snapshot,
        string error, DateTimeOffset finishedAt) =>
        new(scope, snapshot, LocalReviewRunStatus.Failed, [], [], null, null, error, false, finishedAt);

    /// <summary>Reads the journaled run, or a synthetic never-dispatched row when the cancellation
    /// landed before the backend's durable claim. Absence of the claim is proof, not a guess.</summary>
    private static HarnessRun TryReadRun(HarnessRunStore runs, string runId)
    {
        try { return runs.Get(runId); }
        catch (KeyNotFoundException)
        {
            return new HarnessRun(runId, "", HarnessRunStatus.Failed, null, null, null,
                "cancelled before dispatch: the durable claim was never written");
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (Exception) { /* Scratch cleanup is best effort; evidence stays for inspection. */ }
    }
}
