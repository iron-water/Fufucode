using Fufucode.App.Abstractions.Automation;

namespace Fufucode.Core.Features.LocalReview;

/// <summary>
/// Adapter from the narrow review seam onto the host's execution dispatch
/// (<see cref="ExecutionBaseLibrary"/>). It owns no protocol and no worktree: the registered
/// execution owner (the auto-development plugin) runs the review in a module-owned scratch
/// worktree, journals the run, and reports its real terminal state. Two properties are
/// deliberate:
///
/// - Without a registered owner the review reports Failed with the installation reason; the host
///   never falls back to a second executor of its own (LocalReviewHarnessExecutor was removed).
/// - Findings are accepted only when the owner reports Completed. An Unknown or Interrupted end
///   state is shown as cancelled with its retained worktree; the transport reaching a terminal
///   state is not treated as a completed review.
/// </summary>
public sealed class LocalReviewHarnessExecutor : ILocalReviewExecutor
{
    private readonly string _dataRoot;
    private readonly string _projectId;
    private readonly string? _provider;
    private readonly Fufucode.App.Abstractions.Automation.Harnesses.HarnessRuntime? _runtime;

    /// <param name="dataRoot">Module-owned directory for review scratch and evidence.</param>
    /// <param name="runtime">宿主已解析的部署；为空时由执行所有者按环境变量解析。</param>
    public LocalReviewHarnessExecutor(string dataRoot, string projectId, string model, string? provider = null,
        Fufucode.App.Abstractions.Automation.Harnesses.HarnessRuntime? runtime = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        _dataRoot = dataRoot;
        _projectId = projectId;
        _provider = provider;
        _runtime = runtime;
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
        try
        {
            if (scope.Status != LocalReviewScopeStatus.Resolved)
                return Fail(scope, null, scope.Error ?? "scope-not-resolved", finished);
            if (scope.SnapshotCapability != LocalReviewSnapshotCapability.FullSnapshot)
                return Fail(scope, null,
                    "shared-snapshot-api-gap: " + LocalReviewScopeResolver.MissingSharedApiFor(scope.Kind), finished);

            var owner = ExecutionBaseLibrary.Current;
            if (owner is null)
                return Fail(scope, null,
                    "审查执行后端不可用：请在插件市场中安装并启用自动开发工作台（automation.auto-dev）。", finished);
            if (_runtime is null && !owner.SupportsHarness("pi"))
                return Fail(scope, null,
                    $"审查执行后端 '{owner.OwnerId}' 未登记 pi harness，无法执行本地审查。", finished);

            snapshot = await LocalReviewScopeResolver.CaptureFixedSnapshotAsync(scope, cancellationToken);
            if (snapshot is null || snapshot.Status != GitDiffSnapshotStatus.Captured)
                return Fail(scope, snapshot, snapshot?.Error ?? "snapshot-capture-refused", finished);
            if (!await snapshot.IsFreshAsync(cancellationToken))
                return new LocalReviewRunResult(scope, snapshot, LocalReviewRunStatus.StaleAfterReview,
                    [], [], Model, null, "snapshot-stale-before-review", false, DateTimeOffset.UtcNow);

            var payload = LocalReviewPayload.BuildResult(scope, snapshot);
            var outcome = await owner.ExecuteAsync(new ExecutionRequest(
                _dataRoot, _projectId, "pi", Model, payload.Text, _provider,
                BaseSha: snapshot.HeadCommit, Runtime: _runtime), cancellationToken);

            var completedAt = DateTimeOffset.UtcNow;
            if (outcome.Status is ExecutionStatus.Unknown or ExecutionStatus.Interrupted)
                return new LocalReviewRunResult(scope, snapshot, LocalReviewRunStatus.Cancelled, [], [],
                    Model, outcome.RunId,
                    "review run ended in an unknown state; the backend quarantines it and never auto-resends. " +
                    "Reconcile before dispatching another review. Scratch retained for inspection.",
                    false, completedAt)
                {
                    UnreviewedPaths = payload.UnreviewedPaths,
                    RetainedScratch = outcome.Worktree,
                };
            if (outcome.Status != ExecutionStatus.Completed)
                return new LocalReviewRunResult(scope, snapshot, LocalReviewRunStatus.Failed, [], [],
                    Model, outcome.RunId, outcome.Error ?? "run-failed", false, completedAt)
                {
                    UnreviewedPaths = payload.UnreviewedPaths,
                };

            var parsed = LocalReviewFindingsParser.Parse(outcome.Output ?? "", snapshot);
            // Freshness is re-checked before any "complete" classification: an empty answer during a
            // concurrent source change is still a stale review, never a clean "no findings".
            var fresh = await snapshot.IsFreshAsync(CancellationToken.None);

            // 1) Structurally unreadable, or every listed finding rejected: invalid response, not a review.
            var wholeFailure = parsed.Rejected.FirstOrDefault(issue =>
                LocalReviewFindingsParser.WholeResponseFailures.Contains(issue.Reason));
            if (parsed.ItemsSeen == 0 && wholeFailure is not null)
                return Classified(scope, snapshot, payload.UnreviewedPaths, null,
                    LocalReviewRunStatus.InvalidResponse, [], parsed.Rejected, outcome.RunId,
                    wholeFailure.Reason, parsed.Truncated, completedAt);
            if (parsed.AllFindingsInvalid)
                return Classified(scope, snapshot, payload.UnreviewedPaths, null,
                    LocalReviewRunStatus.InvalidResponse, [], parsed.Rejected, outcome.RunId,
                    $"all-{parsed.ItemsSeen}-listed-findings-rejected", parsed.Truncated, completedAt);

            // 2) Scope not fully sent: the answer cannot certify a scope it never saw.
            if (payload.UnreviewedPaths.Count > 0)
                return Classified(scope, snapshot, payload.UnreviewedPaths, null,
                    LocalReviewRunStatus.IncompleteScope, parsed.Findings, parsed.Rejected, outcome.RunId,
                    "payload-incomplete: " + payload.UnreviewedPaths.Count +
                    " file(s) not sent to the reviewer", parsed.Truncated, completedAt);

            // 3) Repository moved under the snapshot: stale regardless of an empty answer.
            if (!fresh)
                return Classified(scope, snapshot, payload.UnreviewedPaths, null,
                    LocalReviewRunStatus.StaleAfterReview, parsed.Findings, parsed.Rejected, outcome.RunId,
                    "repository changed after the snapshot was fixed", parsed.Truncated, completedAt);

            // 4) Only now is an empty answer "no findings".
            if (parsed.Findings.Count == 0 && parsed.Rejected.Count == 0)
                return Classified(scope, snapshot, payload.UnreviewedPaths, null,
                    LocalReviewRunStatus.NoFindings, [], parsed.Rejected, outcome.RunId, null,
                    parsed.Truncated, completedAt);

            return Classified(scope, snapshot, payload.UnreviewedPaths, null,
                LocalReviewRunStatus.Completed, parsed.Findings, parsed.Rejected, outcome.RunId,
                parsed.Truncated ? "response exceeded the byte limit" : null, parsed.Truncated, completedAt);
        }
        catch (OperationCanceledException)
        {
            // Cancelled before/while dispatching. The execution owner owns any dispatched run and
            // reports its worktree through the unknown-state path; here nothing was accepted as done.
            return new LocalReviewRunResult(scope, snapshot, LocalReviewRunStatus.Cancelled, [], [],
                Model, null, "cancelled before the review completed; nothing was accepted as a review",
                false, DateTimeOffset.UtcNow);
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

    private static LocalReviewRunResult Fail(LocalReviewScopeResolution scope, FileDiffSnapshot? snapshot,
        string error, DateTimeOffset finishedAt) =>
        new(scope, snapshot, LocalReviewRunStatus.Failed, [], [], null, null, error, false, finishedAt);
}
