using System.Text.Json;
using Fufucode.App.Abstractions.Automation.Harnesses;

namespace Fufucode.Core.Automation.Hosting;

/// <summary>What the receiving gate observed, returned to the caller instead of a silent bool.</summary>
public sealed record ReceiveOutcome(bool AcceptedForReview, string Code, string Detail,
    string? PartId, long? RevisionNumber, string? ObservedHead);

/// <summary>
/// One candidate delivery as presented by an execution session. Text such as "done" is not a
/// terminal state; the gate only trusts the journal, the real repository and the bound identity.
/// </summary>
public sealed record DeliveryCandidate(string RunId, string ThreadId, string TaskId, string PartId,
    string HeadSha, string CardJson, string RevisionId, string EventId, long NowUtc)
{
    /// <summary>
    /// Card status of this part. The transport can complete without anything being delivered,
    /// so an empty or non-delivery status is rejected rather than accepted as success.
    /// </summary>
    public string? CardStatus { get; init; }
}

/// <summary>
/// C1 receiving gate. Checks run/task/part identity, journaled terminal state, real repository
/// HEAD, worktree binding and an intact journal before a revision becomes reviewable. Acceptance
/// stays an upper-layer decision (AutomationStore.RecordDecision) and is never made here.
/// </summary>
public sealed class DeliveryReceiver(AutomationStore store, HarnessRunStore runs, IGitEvidence git)
{
    public async Task<ReceiveOutcome> ReceiveAsync(DeliveryCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        if (!GitCliEvidence.IsSha(candidate.HeadSha))
            return Reject(candidate, "head-format", "A delivery must name a full commit SHA.");
        if (candidate.CardStatus is not ("module_ready" or "submitted"))
            return Reject(candidate, "card-status",
                $"Card status '{candidate.CardStatus}' is not a deliverable status.");
        HarnessRun run;
        try { run = runs.Get(candidate.RunId); }
        catch (KeyNotFoundException)
        {
            return Reject(candidate, "unknown-run", "The journal has no such run; nothing was dispatched.");
        }
        if (run.Status == HarnessRunStatus.Unknown)
            return Reject(candidate, "unknown-requires-resolution",
                "An unresolved Unknown run can only be settled through RunReconciler.ResolveAsync.");
        // A non-completed terminal run has no deliverable result; a crash cannot be reported as a
        // delivery merely because it left a commit behind.
        if (run.Status != HarnessRunStatus.Completed)
            return Reject(candidate, "not-terminal",
                $"Run '{candidate.RunId}' is {run.Status}; only a Completed journal can be a delivery.");
        if (!string.Equals(run.ThreadId, candidate.ThreadId, StringComparison.Ordinal))
            return Reject(candidate, "thread-mismatch",
                $"The run belongs to thread '{run.ThreadId}', the candidate claims '{candidate.ThreadId}'.");
        // The card's task must be the task this run was actually dispatched for. Comparing only the
        // card against the part's owner would let a completed run of task A deliver task B's card in
        // the same thread, so the journaled dispatch identity is checked here, at claim-time order.
        if (run.TaskId is null)
            return Reject(candidate, "run-task-unknown",
                $"Run '{candidate.RunId}' never journaled the task it was dispatched for (legacy row); " +
                "the identity is never guessed from the request hash or the streamed output. Re-dispatch " +
                "through an executor that journals task identity, or settle this run explicitly.");
        if (!string.Equals(run.TaskId, candidate.TaskId, StringComparison.Ordinal))
            return Reject(candidate, "run-task-mismatch",
                $"Run '{candidate.RunId}' was dispatched for task '{run.TaskId}', the card claims '{candidate.TaskId}'.");
        DeliveryPart part;
        AutomationTask task;
        try
        {
            part = store.GetPart(candidate.PartId);
            task = store.GetTask(part.TaskId);
        }
        catch (KeyNotFoundException)
        {
            return Reject(candidate, "unknown-part", "The delivery part is not in this project.");
        }
        // Belt and braces: the journaled dispatch task and the part's own task must be the same row,
        // so a card cannot pair a legitimate run with a part that has moved under another task.
        if (!string.Equals(run.TaskId, part.TaskId, StringComparison.Ordinal))
            return Reject(candidate, "run-part-mismatch",
                $"Run '{candidate.RunId}' was dispatched for task '{run.TaskId}', part '{candidate.PartId}' belongs to '{part.TaskId}'.");
        if (!string.Equals(task.TaskId, candidate.TaskId, StringComparison.Ordinal))
            return Reject(candidate, "task-mismatch",
                $"Part '{candidate.PartId}' belongs to task '{task.TaskId}', the candidate claims '{candidate.TaskId}'.");
        if (!string.Equals(task.ThreadId, candidate.ThreadId, StringComparison.Ordinal))
            return Reject(candidate, "task-thread-mismatch",
                $"Task '{task.TaskId}' belongs to thread '{task.ThreadId}'.");
        var thread = store.GetThread(candidate.ThreadId);
        if (thread.Status == ThreadStatus.Frozen)
            return Reject(candidate, "thread-frozen",
                $"Thread '{candidate.ThreadId}' is frozen ({thread.FreezeReason}); reconcile first.");
        var runWorktree = Normalize(run.Worktree ?? thread.Worktree);
        var threadWorktree = Normalize(thread.Worktree);
        if (runWorktree != threadWorktree)
            return Reject(candidate, "worktree-replaced",
                $"The run holds '{run.Worktree}' while the thread is bound to '{thread.Worktree}'.");
        // An acknowledgment that was lost is replayed against the recorded revision, so a retry
        // confirms persistence instead of creating a second delivery or tripping the guards below.
        var recorded = store.ListRevisions(candidate.PartId)
            .Where(revision => revision.RunId == candidate.RunId).ToList();
        if (recorded.Any(revision => revision.CardJson == candidate.CardJson &&
                string.Equals(revision.HeadSha, candidate.HeadSha, StringComparison.OrdinalIgnoreCase)))
        {
            var replayed = recorded.First(revision => revision.CardJson == candidate.CardJson)!;
            return new(true, "already-recorded",
                $"Revision {replayed.RevisionNumber} is already persisted.", candidate.PartId,
                replayed.RevisionNumber, candidate.HeadSha);
        }
        if (!store.ControlRepairDeliveryAdmissible(candidate.RunId, candidate.PartId))
            return Reject(candidate, "repair-scope-or-identity", "A repair may deliver only its authorized part through the original managed session.");
        var observedHead = await git.HeadAsync(threadWorktree, cancellationToken).ConfigureAwait(false);
        if (observedHead is null)
            return Reject(candidate, "worktree-unreadable",
                $"Cannot read a repository HEAD in '{thread.Worktree}'.");
        if (!string.Equals(observedHead, candidate.HeadSha, StringComparison.OrdinalIgnoreCase))
            return Reject(candidate, "head-mismatch",
                $"The repository HEAD is {observedHead}, the candidate claims {candidate.HeadSha}.");
        // Provenance: the claimed commit must be the one this run itself left behind. The repository
        // HEAD alone proves only "this is current", and in one shared worktree another run can have
        // moved it forward, so a current HEAD that this run never attested is borrowed work.
        if (run.SettleHead is null)
            return Reject(candidate, "settle-head-unobserved",
                $"Run '{candidate.RunId}' has no journaled repository provenance: the supervising host " +
                "recorded no HEAD when this run's own writer exited (a legacy row, or an executor built " +
                "without an IGitEvidence observer). A delivery cannot be certified from a later reading " +
                "of the worktree; re-dispatch with an observer or settle this run explicitly.");
        if (!string.Equals(run.SettleHead, candidate.HeadSha, StringComparison.OrdinalIgnoreCase))
            return Reject(candidate, "head-not-from-run",
                $"Run '{candidate.RunId}' left HEAD {run.SettleHead} when its writer exited; the card " +
                $"claims {candidate.HeadSha}, which this run never produced.");
        if (run.Status == HarnessRunStatus.Completed)
        {
            if (run.Output is null || run.SessionId is null)
                return Reject(candidate, "incomplete-journal",
                    "A completed run must have journaled output and session identity.");
            if (await git.IsCleanAsync(threadWorktree, cancellationToken).ConfigureAwait(false) != true)
                return Reject(candidate, "dirty-worktree",
                    "Tracked changes exist at the recorded HEAD; the HEAD does not describe the delivery.");
        }
        // A decided revision is immutable: a new delivery needs a rework decision first, otherwise
        // a runner could quietly replace what an auditor already accepted.
        if (part.Status is DeliveryPartStatus.Accepted or DeliveryPartStatus.Blocked or
            DeliveryPartStatus.Deferred)
            return Reject(candidate, "already-decided",
                $"Part '{candidate.PartId}' is {part.Status}; a rework decision must precede a new delivery.");
        try
        {
            var revision = store.RecordDelivery(candidate.RevisionId, candidate.PartId, candidate.RunId,
                observedHead, candidate.CardJson, candidate.EventId, candidate.NowUtc);
            return new(true, "delivered",
                $"Revision {revision.RevisionNumber} of '{candidate.PartId}' is recorded and awaiting review.",
                candidate.PartId, revision.RevisionNumber, observedHead);
        }
        catch (InvalidOperationException error)
        {
            // Same (part, run, HEAD) with different card content: a conflicting re-delivery.
            return Reject(candidate, "conflicting-delivery", error.Message);
        }
    }

    /// <summary>
    /// Replay after a lost acknowledgment: identical identity and card content returns the recorded
    /// revision, so the client can confirm persistence without creating a second delivery.
    /// </summary>
    public async Task<ReceiveOutcome> ConfirmAsync(string partId, string runId, string headSha,
        CancellationToken cancellationToken = default)
    {
        // A confirmation must re-check the journaled dispatch identity, not just find a row: the
        // stored revision already passed the gate, but this entry is also called directly.
        HarnessRun confirmingRun;
        try { confirmingRun = runs.Get(runId); }
        catch (KeyNotFoundException)
        {
            return new(false, "not-recorded", $"No run '{runId}' in the dispatch journal.", partId,
                null, null);
        }
        var matching = store.ListRevisions(partId)
            .FirstOrDefault(revision => revision.RunId == runId &&
                string.Equals(revision.HeadSha, headSha, StringComparison.OrdinalIgnoreCase));
        if (matching is null)
            return new(false, "not-recorded", $"No revision for part '{partId}' run '{runId}'.",
                partId, null, null);
        var ownerPart = store.GetPart(partId);
        if (confirmingRun.TaskId is null || confirmingRun.TaskId != ownerPart.TaskId)
            return new(false, "run-task-mismatch",
                $"Run '{runId}' is not journaled as a dispatch of task '{ownerPart.TaskId}'.",
                partId, null, null);
        var worktree = Normalize(store.GetThread(store.GetTask(store.GetPart(partId).TaskId).ThreadId).Worktree);
        var observedHead = await git.HeadAsync(worktree, cancellationToken).ConfigureAwait(false);
        return new(true, "already-recorded",
            $"Revision {matching.RevisionNumber} is persisted for HEAD {matching.HeadSha}.",
            partId, matching.RevisionNumber, observedHead);
    }

    private static ReceiveOutcome Reject(DeliveryCandidate candidate, string code, string detail) =>
        new(false, code, detail, candidate.PartId, null, null);

    internal static string Normalize(string path) =>
        string.IsNullOrWhiteSpace(path) ? ""
        : (OperatingSystem.IsWindows()
            ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)).ToUpperInvariant()
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)));
}
