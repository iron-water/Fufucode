using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Fufucode.App.Features.Automation;
using Fufucode.Core.Automation;
using Fufucode.App.Abstractions.Automation.Harnesses;
using Fufucode.Core.Automation.Harnesses;
using Fufucode.Core.Automation.Hosting;
using Microsoft.Data.Sqlite;

namespace Fufucode.App.SelfTest;

internal static partial class AutomationControlSelfTest
{
    private static async Task RunStopResolutionAsync(AutomationStore store, AutomationControlViewModel model,
        AutomationControlPageView view, Window window, string root, Action<bool, string> check)
    {
        const string runId = "native-cancel-run";
        long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var runStore = new HarnessRunStore(store);
        var original = runStore.Get(runId);
        var originalThreadStatus = store.GetThread(original.ThreadId).Status;
        var events = store.ListEventsAfter(0).Count;
        var audit = await store.ReadControlStopReconciliationAsync(runId);
        check(audit.CanInterrupt && original.Status == HarnessRunStatus.Unknown && audit.ThreadId == original.ThreadId &&
            audit.TaskId == original.TaskId && events == store.ListEventsAfter(0).Count,
            "read-only cancellation audit uses actual stopped writer and current Git HEAD without recording a decision");
        var command = new ControlStopResolutionCommand("resolution-negative", audit.Revision, "fixture", runId,
            audit.ThreadId, audit.TaskId, audit.StopCommandId, audit.EvidenceToken, audit.ObservedHead!, "Preserve cancelled work", "Inspected actual writer and files");
        foreach (var scenario in new[] { "pending", "queue", "owner", "worktree" })
        {
            var path = Path.Combine(root, "stop-resolution-" + scenario + ".db");
            await BackupControlFixtureAsync(store.DatabasePath, path);
            using var isolated = AutomationStore.AttachControlClient(path);
            using var sql = new SqliteConnection("Data Source=" + path); sql.Open(); using var q = sql.CreateCommand();
            q.CommandText = scenario switch
            {
                "pending" => "UPDATE control_run_stops SET status='requested' WHERE run_id='native-cancel-run'",
                "queue" => "UPDATE host_requests SET state=1 WHERE run_id='native-cancel-run'",
                "owner" => "UPDATE harness_runs SET supervisor_start_utc=supervisor_start_utc+60000 WHERE run_id='native-cancel-run'",
                _ => "UPDATE harness_runs SET worktree='C:/wrong-authority' WHERE run_id='native-cancel-run'"
            };
            q.ExecuteNonQuery();
            var refused = false;
            try { await isolated.ApplyControlStopResolutionAsync(command with { CommandId = "refuse-" + scenario }, Now()); }
            catch (InvalidOperationException) { refused = true; }
            check(refused && new HarnessRunStore(isolated).Get(runId).Status == HarnessRunStatus.Unknown &&
                isolated.ReadControlSnapshot().Revision == audit.Revision,
                "snapshot counterexample preserves Unknown and refuses changed cancellation ownership/state: " + scenario);
        }
        var rollbackPath = Path.Combine(root, "stop-resolution-rollback.db");
        await BackupControlFixtureAsync(store.DatabasePath, rollbackPath);
        using (var isolated = AutomationStore.AttachControlClient(rollbackPath))
        {
            using var sql = new SqliteConnection("Data Source=" + rollbackPath); sql.Open();
            using var q = sql.CreateCommand();
            q.CommandText = "CREATE TRIGGER refuse_resolution BEFORE INSERT ON harness_resolutions BEGIN SELECT RAISE(ABORT,'fixture resolution journal failure'); END"; q.ExecuteNonQuery();
            var failed = false;
            try { await isolated.ApplyControlStopResolutionAsync(command with { CommandId = "rollback-resolution" }, Now()); }
            catch (SqliteException) { failed = true; }
            check(failed && new HarnessRunStore(isolated).Get(runId).Status == HarnessRunStatus.Unknown &&
                isolated.ReadControlClientSnapshot(includeGraph: true).RunStops!.Single(s => s.RunId == runId).Status == "attempt_finished_reconciliation_required" &&
                isolated.ReadControlSnapshot().Revision == audit.Revision && isolated.ListEventsAfter(0).Count == events &&
                isolated.ListUnresolvedClaims().Any(c => c.ClaimId == "cancel-resolution-claim"),
                "actual resolution journal failure rolls back run, cancellation, revision, event and pending claim together");
            q.CommandText = "DROP TRIGGER refuse_resolution"; q.ExecuteNonQuery();
            var isolatedCommand = command with { CommandId = "isolated-resolution" };
            var receipt = await isolated.ApplyControlStopResolutionAsync(isolatedCommand, Now());
            var isolatedEvents = isolated.ListEventsAfter(0).Count;
            check(await isolated.ApplyControlStopResolutionAsync(isolatedCommand, Now()) == receipt && isolated.ListEventsAfter(0).Count == isolatedEvents,
                "exact resolution ID and payload replay the immutable receipt after run state changed");
            var changed = false; try { await isolated.ApplyControlStopResolutionAsync(isolatedCommand with { Evidence = "different evidence" }, Now()); }
            catch (InvalidOperationException) { changed = true; }
            check(changed && isolated.ListEventsAfter(0).Count == isolatedEvents, "same resolution ID with changed human evidence cannot overwrite the original receipt");
        }
        check(runStore.Get(runId).Status == HarnessRunStatus.Unknown && store.ListEventsAfter(0).Count == events,
            "destructive resolution counterexamples stay in a real SQLite backup, leaving the actual cancelled run unresolved");
        async Task Refused(ControlStopResolutionCommand input, string label)
        {
            var refused = false;
            try { await store.ApplyControlStopResolutionAsync(input, Now()); }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException) { refused = true; }
            check(refused && runStore.Get(runId).Status == HarnessRunStatus.Unknown && store.ListEventsAfter(0).Count == events, label);
        }
        await Refused(command with { ThreadId = "wrong-thread" }, "wrong resolution thread identity is refused without changing Unknown");
        await Refused(command with { TaskId = "wrong-task" }, "wrong resolution task identity is refused");
        await Refused(command with { StopCommandId = "wrong-stop" }, "resolution binds the immutable original cancellation request");
        await Refused(command with { ExpectedEvidenceToken = "wrong-token" }, "resolution refuses unrelated process and repository evidence");
        await Refused(command with { ExpectedHead = new string('a', 40) }, "resolution refuses a different full repository HEAD");
        await Refused(command with { ExpectedRevision = audit.Revision - 1 }, "resolution refuses stale control revision without rebasing");
        await Refused(command with { Reason = " " }, "resolution requires explicit human reason");
        await Refused(command with { Evidence = " " }, "resolution requires independent human evidence");
        await Refused(command with { Evidence = new string('x', 256 * 1024 + 1) }, "oversized resolution evidence is refused before writing");
        var completedRefused = false;
        try { await store.ReadControlStopReconciliationAsync("after-cancel-run"); }
        catch (InvalidOperationException) { completedRefused = true; }
        check(completedRefused && runStore.Get("after-cancel-run").Status == HarnessRunStatus.Completed,
            "independent completed execution cannot be repurposed as a cancelled Unknown run");
        await Until(() => !model.IsBusy); await model.RefreshAsync(); model.SelectTask("cancel-task"); await Paint(window);
        var selector = view.FindControl<ComboBox>("CancelledRunInput")!;
        selector.SelectedItem = model.CancelledRuns.Single(stop => stop.RunId == runId); await Paint(window);
        check(model.SelectedCancelledRun?.RunId == runId && !model.ResolveCancelledRunCommand.CanExecute(null),
            "native exact-run selection cannot resolve before a fresh audit");
        await Until(() => !model.IsBusy); await model.InspectCancelledRunCommand.ExecuteAsync(null); await Paint(window);
        check(model.ReconciliationText.Contains(audit.ObservedHead!) && model.ReconciliationText.Contains(audit.Worktree!) && model.ReconciliationText.Contains("不接受输出") &&
            !model.ResolveCancelledRunCommand.CanExecute(null), "native audit displays the actual HEAD and requires separate reason and evidence");
        var reason = view.FindControl<TextBox>("ResolutionReason")!;
        var evidence = view.FindControl<TextBox>("ResolutionEvidence")!;
        reason.Text = "Cancelled execution explicitly reconciled 中文😀"; evidence.Text = "Reviewed retained local files and actual process audit"; await Paint(window);
        await Until(() => !model.IsBusy);
        check(model.ResolveCancelledRunCommand.CanExecute(null), $"compiled native reconciliation inputs enable the real Interrupted command; busy={model.IsBusy}; reasonBound={model.ResolutionReason == reason.Text}; evidenceBound={model.ResolutionEvidence == evidence.Text}");
        // Real repository changes after opening the form must not be silently substituted into an edited decision.
        var tree = original.Worktree!;
        await Git(tree, "-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "commit", "--allow-empty", "-q", "-m", "external HEAD change");
        await Until(() => !model.IsBusy); await model.ResolveCancelledRunCommand.ExecuteAsync(null);
        check(runStore.Get(runId).Status == HarnessRunStatus.Unknown && model.StatusText.Contains("操作未完成") &&
            model.ResolutionReason.Contains("中文😀"), "external Git HEAD change refuses the native old audit and preserves human input");
        var tracked = (await Git(tree, "ls-files")).Split('\n', StringSplitOptions.RemoveEmptyEntries).First().Trim();
        var retained = Path.Combine(tree, tracked); File.AppendAllText(retained, "\nRetained cancellation changes\n");
        var bytes = File.ReadAllBytes(retained);
        await Until(() => !model.IsBusy); await model.InspectCancelledRunCommand.ExecuteAsync(null); await Paint(window);
        check(model.ReconciliationText.Contains("仍有修改") && reason.Text == "" && evidence.Text == "",
            "explicit re-audit reports dirty tracked files and requires new human confirmation");
        reason.Text = "Keep cancellation changes 中文😀"; evidence.Text = "Inspected retained dirty files, current HEAD and exited writer"; await Paint(window);
        await Until(() => !model.IsBusy);
        var button = view.FindControl<Button>("ResolveCancelledRun")!; button.BringIntoView(); await Paint(window); window.Activate();
        var focused = button.IsEnabled && button.Focus();
        check(focused && button.IsFocused && model.ResolveCancelledRunCommand.CanExecute(null),
            $"actual reconciliation input targets enabled focused button; enabled={button.IsEnabled}; focused={button.IsFocused}; busy={model.IsBusy}");
        if (!focused || !button.IsFocused) throw new InvalidOperationException("Actual reconciliation button could not receive keyboard focus.");
        var prior = model.ResolveCancelledRunCommand.ExecutionTask; window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\r"); window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\r");
        try { await Until(() => model.ResolveCancelledRunCommand.ExecutionTask is { } task && !ReferenceEquals(task, prior)); }
        catch (TimeoutException error)
        {
            throw new InvalidOperationException($"Actual reconciliation key did not start command; enabled={button.IsEnabled}; focused={button.IsFocused}; busy={model.IsBusy}; canExecute={model.ResolveCancelledRunCommand.CanExecute(null)}; status={model.StatusText}; runStatus={runStore.Get(runId).Status}", error);
        }
        await model.ResolveCancelledRunCommand.ExecutionTask!;
        var settled = runStore.Get(runId);
        check(settled.Status == HarnessRunStatus.Interrupted && settled.SettleHead is null && settled.SessionId == original.SessionId &&
            settled.TaskId == original.TaskId && settled.WriterPid == original.WriterPid,
            "focused native Enter records Interrupted without inventing successful provenance or changing original session identity");
        check(File.ReadAllBytes(retained).SequenceEqual(bytes), "reconciliation preserves actual dirty worktree file bytes");
        check(!store.ListUnresolvedClaims().Any(c => c.RunId == runId) &&
            store.ListUnresolvedClaims().Any(c => c.ClaimId == "independent-resolution-claim"),
            "Interrupted decision resolves only this run's Unknown claim and preserves independent pending claims");
        check(store.GetThread(original.ThreadId).Status == originalThreadStatus,
            "explicit Interrupted settlement does not silently unfreeze or restart the original thread");
        check(runStore.ListResolutions().Count(row => row.RunId == runId) == 1 &&
            store.ReadControlClientSnapshot(includeGraph: true).RunStops!.Single(stop => stop.RunId == runId).Status == "reconciled_interrupted",
            "one immutable resolution and cancellation outcome are recorded together");
        var count = store.ListEventsAfter(0).Count;
        await Until(() => !model.IsBusy); await model.RetryReceiptAsync();
        check(store.ListEventsAfter(0).Count == count && runStore.ListResolutions().Count(row => row.RunId == runId) == 1,
            "replaying the original native resolution returns its receipt without duplicate resolution or event");
        var journal = runStore.ListResolutions().Single(row => row.RunId == runId);
        check(journal.DecidedBy == "desktop-user" && journal.EvidenceJson.Contains("Keep cancellation changes") &&
            journal.EvidenceJson.Contains("Inspected retained dirty files"), "resolution journal preserves explicit human actor, reason and evidence");
        check((await store.ReadControlStopReconciliationAsync(runId)).CanInterrupt == false,
            "resolved Interrupted execution is not offered for another resolution");
        await Until(() => !model.IsBusy); await model.RefreshAsync(); model.SelectTask("cancel-task");
        check(model.DetailText.Contains("人类已记录为中断") && !model.CancelExecutionCommand.CanExecute(null),
            "native task history distinguishes reconciled interruption from attempted cancellation");
        using var reopened = AutomationStore.AttachControlClient(store.DatabasePath);
        check(reopened.ReadControlClientSnapshot(includeGraph: true).RunStops!.Single(stop => stop.RunId == runId).Status == "reconciled_interrupted",
            "reopening the existing authority observes the same durable Interrupted decision");
        var delivery = await new DeliveryReceiver(store, runStore, GitCliEvidence.Shared).ReceiveAsync(new(runId, original.ThreadId,
            original.TaskId!, "cancel-no-delivery", (await Git(tree, "rev-parse", "HEAD")).Trim(), "{}", "resolved-fake-card", "resolved-fake-event", Now()));
        check(!delivery.AcceptedForReview && new HostJournal(store).Get(runId)!.State == HostRequestState.Settled,
            "Interrupted reconciliation neither certifies a card nor requeues the original execution");
    }
}
