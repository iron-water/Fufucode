using Fufucode.App.Abstractions.Automation.Harnesses;
using Fufucode.Core.Automation.Hosting;

namespace Fufucode.Core.Automation;

/// <summary>
/// Trusted service-side binding of a human-approved plan to registered execution identities.
/// Never adopts a foreign session, generates a new thread or starts a process itself.
/// </summary>
public sealed class ControlIntentConsumer
{
    private readonly AutomationStore _store;
    private readonly HarnessRunStore _runs;
    private readonly RunReconciler _audit;
    private readonly IGitEvidence _git;

    public ControlIntentConsumer(AutomationStore store, IProcessProbe? probe = null, IGitEvidence? git = null)
    {
        _store = store;
        _runs = new HarnessRunStore(store);
        _ = new HostJournal(store);
        _git = git ?? GitCliEvidence.Shared;
        _audit = new RunReconciler(store, _runs, probe ?? SystemProcessProbe.Shared,
            _git, "control-binder");
    }

    public async Task<ControlQueueReceipt> QueueAsync(ControlExecutionBinding binding, long nowUtc,
        CancellationToken cancellationToken = default)
    {
        // A lost acknowledgment can be replayed while the original run is active. This is a
        // receipt query, never another dispatch or an ownership claim.
        if (_store.ReplayControlBinding(binding) is { } receipt) return receipt;
        cancellationToken.ThrowIfCancellationRequested();
        var thread = _store.GetThread(binding.ThreadId);
        var task = _store.GetTask(binding.TaskId);
        if (!Directory.Exists(thread.Worktree)) throw new DirectoryNotFoundException(thread.Worktree);
        if (!Path.IsPathFullyQualified(binding.Runtime.Executable) || !File.Exists(binding.Runtime.Executable))
            throw new FileNotFoundException("The verified harness runtime is unavailable.");
        var head = await _git.HeadAsync(thread.Worktree, cancellationToken).ConfigureAwait(false);
        if (!GitCliEvidence.IsSha(task.BaseSha) || !string.Equals(head, task.BaseSha, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The registered task baseline is not the observed worktree HEAD.");
        foreach (var run in _runs.ListWorktreeRuns(thread.Worktree))
        {
            var audit = await _audit.AuditAsync(run.RunId, cancellationToken).ConfigureAwait(false);
            if (!audit.WriterStopped || audit.Verdict != OwnerVerdict.ConfirmedFinished)
                throw new InvalidOperationException($"Worktree ownership is active or unverifiable; reconcile before binding. Run={run.RunId}; verdict={audit.Verdict}; {audit.Reason}");
        }
        cancellationToken.ThrowIfCancellationRequested();
        return _store.QueueControlExecution(binding, nowUtc);
    }

    public async Task<ControlRepairQueueReceipt> QueueRepairAsync(ControlRepairBinding binding, long nowUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(binding.CommandId);
        ArgumentException.ThrowIfNullOrWhiteSpace(binding.RepairId);
        if (_store.ReplayRepairBinding(binding) is { } replay) return replay;
        cancellationToken.ThrowIfCancellationRequested();
        var execution = _store.ReadRepairExecution(binding.RepairId);
        var thread = _store.GetThread(execution.State.ThreadId);
        if (!Path.IsPathFullyQualified(execution.Runtime.Executable) || !File.Exists(execution.Runtime.Executable))
            throw new FileNotFoundException("The original repair runtime is unavailable.");
        var head = await _git.HeadAsync(thread.Worktree, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(head, execution.State.HeadSha, StringComparison.OrdinalIgnoreCase) ||
            await _git.IsCleanAsync(thread.Worktree, cancellationToken).ConfigureAwait(false) != true)
            throw new InvalidOperationException("The repair baseline changed or has uncommitted work; reconcile before resuming.");
        foreach (var run in _runs.ListWorktreeRuns(thread.Worktree))
        {
            var audit = await _audit.AuditAsync(run.RunId, cancellationToken).ConfigureAwait(false);
            if (!audit.WriterStopped || audit.Verdict != OwnerVerdict.ConfirmedFinished)
                throw new InvalidOperationException("Repair ownership is active or unverifiable; reconcile before resuming.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        return _store.QueueControlRepair(binding, nowUtc);
    }
}
