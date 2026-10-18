using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Fufucode.App.Features.Automation;
using Fufucode.Core.Automation;
using Fufucode.Core.Automation.Harnesses;
using Fufucode.Core.Automation.Hosting;
using Microsoft.Data.Sqlite;

namespace Fufucode.App.SelfTest;

internal static partial class AutomationControlSelfTest
{
    private static async Task RunStopAsync(AutomationStore store, AutomationControlViewModel model, AutomationControlPageView view,
        Window window, string root, Action<bool, string> check)
    {
        long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long Rev() => store.ReadControlSnapshot().Revision;
        var executable = Environment.ProcessPath!;
        var gate = Path.Combine(root, "stop-gate");
        var prefix = Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? new[] { typeof(Program).Assembly.Location, "--automation-protocol", gate } : new[] { "--automation-protocol", gate };
        var runtime = new HarnessRuntime("stop-protocol", "codex", executable, prefix, "fixture-v1");
        var nextTree = Path.Combine(root, "stop-next-tree"); Directory.CreateDirectory(nextTree);
        await Git(nextTree, "init", "-q"); await Git(nextTree, "config", "user.name", "Fixture"); await Git(nextTree, "config", "user.email", "fixture@example.invalid");
        File.WriteAllText(Path.Combine(nextTree, "fixture.txt"), "test\n"); await Git(nextTree, "add", "fixture.txt"); await Git(nextTree, "commit", "-q", "-m", "fixture");
        store.CreateThread("cancel-thread", "project", "codex", "devin/swe-2", Path.Combine(root, "tree-a"), null, Now());
        store.CreateTask("cancel-task", "cancel-thread", "same", (await Git(Path.Combine(root, "tree-a"), "rev-parse", "HEAD")).Trim(), Now());
        store.CreateThread("after-cancel-thread", "project", "codex", "devin/swe-2", nextTree, null, Now());
        store.CreateTask("after-cancel-task", "after-cancel-thread", "same", (await Git(nextTree, "rev-parse", "HEAD")).Trim(), Now());
        var queue = new HostJournal(store); var endpoint = Path.Combine(root, "stop-endpoint");
        store.CreatePart("cancel-claimed-part", "cancel-task", "same", Now());
        store.CreateClaim("cancel-resolution-claim", "cancel-task", "cancel-claimed-part", "native-cancel-run", Now());
        store.CreateClaim("independent-resolution-claim", "after-cancel-task", "independent-part", "after-cancel-run", Now());
        queue.Enqueue("native-cancel-run", "cancel-thread", "cancel-task", HostJournal.Payload(runtime,
            new("native-cancel-run", "cancel-thread", "cancel-task", "devin/swe-2", "hold")), Now());
        queue.Enqueue("after-cancel-run", "after-cancel-thread", "after-cancel-task", HostJournal.Payload(runtime,
            new("after-cancel-run", "after-cancel-thread", "after-cancel-task", "devin/swe-2", "Only independent next task")), Now());
        await using var host = new BackgroundHost(new(store.DatabasePath, Path.Combine(root, "stop-runs"), "native-stop-host", endpoint));
        check(await host.StartAsync(), "cancellation fixture runs through the sole actual host");
        try
        {
            await Until(() =>
            {
                try { return host.Executor.Runs.Get("native-cancel-run") is { Status: HarnessRunStatus.Running, WriterPid: not null }; }
                catch (System.Collections.Generic.KeyNotFoundException) { return false; }
            });
            await Until(() => !model.IsBusy); await model.RefreshAsync();
            var execution = model.Snapshot!.RunningExecutions!.Single(run => run.RunId == "native-cancel-run");
            check(execution is { HostId: "native-stop-host", ThreadId: "cancel-thread", TaskId: "cancel-task" } && model.Snapshot.RunningExecutions!.All(r => r.RunId != "after-cancel-run"),
                "native cancellation metadata contains actual owned run identity and excludes queued work");
            await using (var client = await HostClient.ConnectAsync(host.Port)) { await client.ByeAsync(); }
            window.Content = null; await Task.Delay(30); window.Content = view; await Paint(window);
            check(host.Executor.Runs.Get(execution.RunId).Status == HarnessRunStatus.Running && host.Slot == HostSlotState.Occupied,
                "closing observation clients or temporarily detaching the page does not cancel the execution");
            var command = new ControlStopRunCommand("stop-negative", Rev(), "fixture", execution.RunId, execution.ThreadId, execution.TaskId, execution.EvidenceToken, "Stop only this owned run");
            void Refused(ControlStopRunCommand input, string label)
            {
                var refused = false; try { store.ApplyControlStopRun(input, Now()); } catch (Exception e) when (e is ArgumentException or InvalidOperationException) { refused = true; }
                check(refused && host.Executor.Runs.Get(execution.RunId).Status == HarnessRunStatus.Running, label);
            }
            Refused(command with { ThreadId = "after-cancel-thread" }, "wrong stop thread cannot cancel the similarly titled task");
            Refused(command with { TaskId = "after-cancel-task" }, "wrong stop task identity is refused");
            Refused(command with { ExpectedEvidenceToken = "unrelated-owner" }, "wrong stop owner/writer evidence is refused");
            Refused(command with { Reason = " " }, "stop requires an explicit human reason");
            Refused(command with { Reason = new string('x', 256 * 1024 + 1) }, "oversized stop reason is refused without writing a request");
            Refused(command with { ExpectedRevision = Rev() - 1 }, "stale stop revision is refused without rebase");
            var brokenIdentity = Path.Combine(root, "stop-writer-birth.db");
            await BackupControlFixtureAsync(store.DatabasePath, brokenIdentity);
            using (var target = new SqliteConnection("Data Source=" + brokenIdentity))
            {
                target.Open(); using var q = target.CreateCommand();
                q.CommandText = "UPDATE harness_runs SET writer_start_utc=writer_start_utc+60000 WHERE run_id='native-cancel-run'"; q.ExecuteNonQuery();
            }
            using (var clone = AutomationStore.AttachControlClient(brokenIdentity))
            {
                var staleBirth = clone.ReadControlClientSnapshot(includeGraph: true).RunningExecutions!.Single(run => run.RunId == execution.RunId);
                var refused = false;
                try { clone.ApplyControlStopRun(command with { CommandId = "wrong-birth", ExpectedEvidenceToken = staleBirth.EvidenceToken }, Now()); }
                catch (InvalidOperationException) { refused = true; }
                check(refused && clone.ReadControlClientSnapshot(includeGraph: true).RunStops!.Count == 0,
                    "even a fresh metadata token cannot authorize cancellation with a mismatched actual writer birth identity");
            }
            model.SelectTask("cancel-task"); await Paint(window);
            var reason = view.FindControl<TextBox>("CancellationReason")!;
            var button = view.FindControl<Button>("CancelExecution")!;
            check(model.HasSelectedExecution && reason.IsVisible && !model.CancelExecutionCommand.CanExecute(null), "native cancel form displays the selected run and requires its own reason");
            reason.Text = "Cancel fixture execution only 中文😀"; await Paint(window); button.BringIntoView(); await Paint(window); await Until(() => !model.IsBusy);
            check(model.ReasonText == reason.Text && button.IsEnabled, "compiled cancellation reason preserves Unicode and enables only the bound run");
            var previous = model.CancelExecutionCommand.ExecutionTask;
            window.Activate(); button.Focus(); window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            await Until(() => model.CancelExecutionCommand.ExecutionTask != previous); await model.CancelExecutionCommand.ExecutionTask!;
            await Until(() => queue.Get(execution.RunId)!.State == HostRequestState.Settled);
            await Until(() => queue.Get("after-cancel-run")!.State == HostRequestState.Settled);
            await model.RefreshAsync();
            var run = host.Executor.Runs.Get(execution.RunId);
            var stop = model.Snapshot!.RunStops!.Single(s => s.RunId == execution.RunId);
            var facts = SystemProcessProbe.Shared.Probe(execution.WriterPid);
            check(facts.Liveness == ProcessLiveness.Dead || facts.Liveness == ProcessLiveness.Alive && !facts.SameIdentityAs(DateTimeOffset.FromUnixTimeMilliseconds(execution.WriterStartUtc)),
                "actual original writer process identity has exited after native cancellation");
            check(run.Status == HarnessRunStatus.Unknown && run.SettleHead is null && run.Error!.Contains("Cancelled"),
                "cancelled execution retains Unknown and has no fabricated delivery HEAD");
            check(stop is { Status: "attempt_finished_reconciliation_required", RequestedBy: "desktop-user", Reason: "Cancel fixture execution only 中文😀" },
                "durable cancellation outcome records attempted cancellation and still requires reconciliation");
            store.MarkClaimUnknown("cancel-resolution-claim");
            check(store.ListEventsAfter(0).Count(e => e.RunId == execution.RunId && e.Type == "control.run.stop_requested") == 1 &&
                store.ListEventsAfter(0).Count(e => e.RunId == execution.RunId && e.Type == "control.run.stop_attempt_finished") == 1,
                "stop request and attempt outcome each publish exactly one durable identity-bound event");
            check(host.Executor.Runs.Get("after-cancel-run").Status == HarnessRunStatus.Completed && host.Slot == HostSlotState.Occupied,
                "cancelling one run keeps the host alive and allows an independent queued task to finish");
            var eventCount = store.ListEventsAfter(0).Count; await model.RetryReceiptAsync();
            check(store.ListEventsAfter(0).Count == eventCount, "native original stop receipt replay neither cancels again nor creates another event");
            var duplicate = command with { CommandId = "new-stop-after-unknown", ExpectedRevision = Rev() };
            var unknownRefused = false; try { store.ApplyControlStopRun(duplicate, Now()); } catch (InvalidOperationException) { unknownRefused = true; }
            check(unknownRefused && host.Executor.Runs.Get(execution.RunId).Status == HarnessRunStatus.Unknown, "unknown cancelled run cannot be cancelled or resumed through a new stop identity");
            model.SelectTask("cancel-task");
            check(!model.HasSelectedExecution && model.DetailText.Contains("仍需对账") && !model.CancelExecutionCommand.CanExecute(null), "native cancelled task shows durable reconciliation status without a fake stop button");
            using var reopened = AutomationStore.AttachControlClient(store.DatabasePath);
            check(reopened.ReadControlClientSnapshot(includeGraph: true).RunStops!.Single(s => s.RunId == execution.RunId) == stop,
                "reopened native client sees the same immutable cancellation identity and recorded result");
            store.CreatePart("cancel-no-delivery", "cancel-task", "same", Now());
            var received = await new DeliveryReceiver(store, host.Executor.Runs, GitCliEvidence.Shared).ReceiveAsync(new(execution.RunId, execution.ThreadId,
                execution.TaskId, "cancel-no-delivery", (await Git(Path.Combine(root, "tree-a"), "rev-parse", "HEAD")).Trim(), "{}", "cancel-fake-card", "cancel-fake-event", Now()));
            check(!received.AcceptedForReview, "cancelled Unknown execution cannot be promoted to an accepted delivery card");
            store.CreateThread("finish-stop-thread", "project", "codex", "devin/swe-2", nextTree, null, Now());
            store.CreateTask("finish-stop-task", "finish-stop-thread", "same", (await Git(nextTree, "rev-parse", "HEAD")).Trim(), Now());
            queue.Enqueue("finish-stop-run", "finish-stop-thread", "finish-stop-task", HostJournal.Payload(runtime,
                new("finish-stop-run", "finish-stop-thread", "finish-stop-task", "devin/swe-2", "hold")), Now());
            await Until(() =>
            {
                try { return host.Executor.Runs.Get("finish-stop-run") is { Status: HarnessRunStatus.Running, WriterPid: not null }; }
                catch (System.Collections.Generic.KeyNotFoundException) { return false; }
            });
            var finishing = store.ReadControlClientSnapshot(includeGraph: true).RunningExecutions!.Single(r => r.RunId == "finish-stop-run");
            var finishCommand = new ControlStopRunCommand("finish-stop-request", Rev(), "fixture", finishing.RunId, finishing.ThreadId, finishing.TaskId,
                finishing.EvidenceToken, "Race explicit cancellation against natural completion");
            store.ApplyControlStopRun(finishCommand, Now()); File.WriteAllText(gate + ".release", "natural completion");
            await Until(() => queue.Get(finishing.RunId)!.State == HostRequestState.Settled);
            await Until(() => store.ReadControlClientSnapshot(includeGraph: true).RunStops!.Single(s => s.RunId == finishing.RunId).Status != "requested");
            var finishRun = host.Executor.Runs.Get(finishing.RunId);
            var finishStop = store.ReadControlClientSnapshot(includeGraph: true).RunStops!.Single(s => s.RunId == finishing.RunId);
            check(finishRun.Status == HarnessRunStatus.Completed ? finishStop.Status == "finished_before_cancel" :
                finishRun.Status == HarnessRunStatus.Unknown && finishStop.Status == "attempt_finished_reconciliation_required",
                "natural completion racing cancellation converges to the actual outcome without leaving a false pending request");
            var finishEvents = store.ListEventsAfter(0).Count; var replay = store.ApplyControlStopRun(finishCommand, Now());
            check(replay.Status == "requested" && store.ListEventsAfter(0).Count == finishEvents,
                "race outcome remains separate from its immutable original requested receipt");
        }
        finally { await host.StopAsync(); }
        check(host.Slot == HostSlotState.Stopped, "original graceful host STOP remains distinct from single-run cancellation");
        await RunStopResolutionAsync(store, model, view, window, root, check);
    }
}
