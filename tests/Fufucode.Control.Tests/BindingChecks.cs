using System.Diagnostics;
using System.Text.Json;
using Fufucode.Core.Automation;
using Fufucode.Core.Automation.Harnesses;
using Fufucode.Core.Automation.Hosting;
using Microsoft.Data.Sqlite;

internal static class BindingChecks
{
    internal static async Task Run(string root, Action<bool, string> check)
    {
        async Task Refused(Func<Task> action, string label)
        {
            try { await action(); }
            catch (InvalidOperationException) { check(true, label); return; }
            throw new Exception("FAIL " + label);
        }
        var worktree = Path.Combine(root, "binding-worktree");
        Directory.CreateDirectory(worktree);
        await Git(worktree, "init", "-q");
        await Git(worktree, "config", "user.email", "fixture@example.invalid");
        await Git(worktree, "config", "user.name", "Fixture");
        await Git(worktree, "config", "core.autocrlf", "false");
        File.WriteAllText(Path.Combine(worktree, "a.txt"), "fixture\n");
        await Git(worktree, "add", "a.txt");
        await Git(worktree, "commit", "-q", "-m", "fixture");
        var head = (await Git(worktree, "rev-parse", "HEAD")).Trim();
        var counter = Path.Combine(root, "binding-writer.log");
        var executable = Environment.ProcessPath!;
        var prefix = new List<string>();
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            prefix.Add(typeof(BindingChecks).Assembly.Location);
        prefix.AddRange(["--protocol", counter]);
        var runtime = new HarnessRuntime("fixture", "codex", executable, prefix.ToArray(), "fixture-v1");
        var db = Path.Combine(root, "binding.db");
        using var store = new AutomationStore(db);
        var queue = new HostJournal(store);
        var consumer = new ControlIntentConsumer(store);
        void Register(string thread, string task, string model = "devin/swe-2", string? saved = null)
        {
            store.CreateThread(thread, "fixture", "codex", model, worktree, saved, 1);
            store.CreateTask(task, thread, task, head, 1);
        }
        WorkPackageSpec Spec(string id, string[]? deps = null) =>
            new(id, id, "Inspect fixture without changing it", ["a.txt"], ["Return a bounded report"],
                "codex", "devin/swe-2", "code", deps ?? []);
        ControlCommand Mode(string id, DevelopmentMode mode) =>
            new(id, store.ReadControlSnapshot().Revision, "human-fixture", ControlAction.SetMode, Mode: mode);
        void DefineApprove(string id, string[]? deps = null)
        {
            store.ApplyControlCommand(new("define-" + id, store.ReadControlSnapshot().Revision, "human-fixture",
                ControlAction.DefinePackage, Package: Spec(id, deps), ExpectedPackageVersion: 0), 2);
            store.ApplyControlCommand(new("approve-" + id, store.ReadControlSnapshot().Revision, "human-fixture",
                ControlAction.ApprovePackage, PackageId: id, ExpectedPackageVersion: 1), 3);
        }
        ControlExecutionBinding Binding(string cmd, string id, string thread, string task) =>
            new(cmd, store.ReadControlSnapshot().Revision, "execute:" + id + ":1", 1, thread, task, runtime);
        Register("thread-a", "task-a");
        Register("wrong-model", "wrong-model-task", "another-model");
        Register("saved", "saved-task", saved: "foreign-session");
        DefineApprove("A");
        await Refused(() => consumer.QueueAsync(Binding("manual-refusal", "A", "thread-a", "task-a"), 4),
            "manual approval cannot be bound or queued");
        store.ApplyControlCommand(Mode("semi-bind", DevelopmentMode.SemiAutomatic), 5);
        await Refused(() => consumer.QueueAsync(Binding("model-refusal", "A", "wrong-model", "wrong-model-task"), 6),
            "binding cannot replace the approved model");
        await Refused(() => consumer.QueueAsync(Binding("session-refusal", "A", "saved", "saved-task"), 6),
            "binding cannot adopt an unimported external session");
        await Refused(() => consumer.QueueAsync(Binding("task-refusal", "A", "thread-a", "wrong-model-task"), 6),
            "binding refuses a task in another thread");
        var before = store.ReadControlSnapshot().Revision;
        var binding = Binding("bind-a", "A", "thread-a", "task-a");
        using (var c = new SqliteConnection("Data Source=" + db))
        {
            c.Open(); using var q = c.CreateCommand();
            q.CommandText = "CREATE TRIGGER fail_binding BEFORE INSERT ON control_bindings BEGIN SELECT RAISE(ABORT,'late bind failure'); END";
            q.ExecuteNonQuery();
        }
        var threw = false;
        try { await consumer.QueueAsync(binding, 7); } catch (SqliteException) { threw = true; }
        check(threw && queue.List().Count == 0 && store.ReadControlSnapshot().Revision == before,
            "late binding failure rolls back queue, intent and control revision together");
        check(store.ReadControlSnapshot().Intents.Single().Status == "awaiting_binding",
            "failed binding leaves no phantom dispatch");
        using (var c = new SqliteConnection("Data Source=" + db))
        {
            c.Open(); using var q = c.CreateCommand(); q.CommandText = "DROP TRIGGER fail_binding"; q.ExecuteNonQuery();
        }
        var receipt = await consumer.QueueAsync(binding, 8);
        check(queue.List().Count == 1 && store.ReadControlSnapshot().Intents.Single().Status == "queued",
            "binding commits exactly one real host queue row");
        check(await consumer.QueueAsync(binding, 9) == receipt && queue.List().Count == 1,
            "lost binding acknowledgment replays the exact durable receipt");
        await Refused(() => consumer.QueueAsync(binding with { Provider = "changed" }, 9),
            "same binding command cannot alter runtime input");
        var payload = HostJournal.ReadPayload(queue.Get(receipt.RunId)!.PayloadJson);
        check(payload.Request.Model == "devin/swe-2" && payload.Request.Prompt.Contains("Inspect fixture") &&
            payload.Request.Prompt.Contains("a.txt"), "queued prompt is derived from approved scope and goal");
        store.ApplyControlCommand(Mode("manual-after-bind", DevelopmentMode.Manual), 10);
        check(!queue.MarkStarted(receipt.RunId, 11) && queue.Get(receipt.RunId)!.State == HostRequestState.Queued,
            "manual mode after enqueue fences actual start");
        check(queue.NextQueued() is null, "suspended control intent does not make the host busy-loop");
        store.ApplyControlCommand(Mode("semi-after-bind", DevelopmentMode.SemiAutomatic), 12);
        check(queue.NextQueued()?.RunId == receipt.RunId, "explicit semi mode releases the same queued identity");
        check(queue.MarkStarted(receipt.RunId, 13), "start is admitted only after a fresh approval/mode check");
        check(!queue.MarkStarted(receipt.RunId, 14), "start transition remains at most once");
        var edit = new ControlCommand("edit-active", store.ReadControlSnapshot().Revision, "human-fixture",
            ControlAction.DefinePackage, Package: Spec("A"), ExpectedPackageVersion: 1);
        await Refused(() => Task.FromResult(store.ApplyControlCommand(edit, 15)),
            "a started writer blocks contract replacement");
        var replayAfterStart = await consumer.QueueAsync(binding, 16);
        check(replayAfterStart == receipt, "original binding retry during execution is only an acknowledgment");
        var runs = new HarnessRunStore(store);
        // Crash in the atomic started transition, before HarnessExecutor.Claim: no process started.
        var recovery = queue.RecoverAfterRestart(runs, 17);
        check(recovery.Requeued.Contains(receipt.RunId) && queue.Get(receipt.RunId)!.State == HostRequestState.Queued,
            "recovery requeues only the proven pre-dispatch intent, retaining the run id");

        DefineApprove("B", ["A"]);
        Register("thread-b", "task-b");
        await Refused(() => consumer.QueueAsync(Binding("dep-before-review", "B", "thread-b", "task-b"), 18),
            "downstream binding cannot replace accepted/integrated dependency evidence with plan approval");
        await using (var host = new BackgroundHost(new(db, Path.Combine(root, "binding-evidence"),
            "fixture-host", Path.Combine(root, "binding-endpoint.txt"))))
        {
            check(await host.StartAsync(), "real background host accepts the bound durable intent");
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (queue.Get(receipt.RunId)!.State != HostRequestState.Settled && DateTime.UtcNow < deadline)
                await Task.Delay(50);
            check(queue.Get(receipt.RunId)!.State == HostRequestState.Settled &&
                host.Executor.Runs.Get(receipt.RunId).Status == HarnessRunStatus.Completed,
                "approved intent reaches an actual subprocess through the existing executor");
            check(File.ReadAllLines(counter).Length == 1, "recovery and binding replay launch exactly one writer");
            check(store.ReadControlSnapshot().Intents.Single(i => i.PackageId == "A").Status == "transport_settled",
                "transport terminal state is displayed without inventing acceptance/integration");
            await host.StopAsync();
        }
        store.CreatePart("part-a", "task-a", "fixture evidence", 19);
        store.RecordDelivery("rev-a", "part-a", receipt.RunId, head, "{}", "delivery-a", 20);
        store.RecordDecision("accept-a", "part-a", AutomationDecision.Accept, null, "{}", "fixture-reviewer",
            "accept-event", 21, receipt.RunId, head, 1);
        await Refused(() => consumer.QueueAsync(Binding("dep-only-accept", "B", "thread-b", "task-b"), 22),
            "code dependency acceptance alone does not authorize downstream execution");
        var integrationCandidate = store.ReadControlIntegrationCandidate("A")!;
        store.ApplyControlIntegration(new("authorize-a", store.ReadControlSnapshot().Revision, "human-fixture",
            ControlIntegrationAction.Authorize, "A", integrationCandidate.Package.Version, integrationCandidate.EvidenceToken), 23);
        store.MarkIntegrated("part-a", 1, head, "integration-event", 23);
        var bindingB = Binding("bind-b", "B", "thread-b", "task-b");
        var receiptB = await consumer.QueueAsync(bindingB, 24);
        check(queue.Get(receiptB.RunId)!.State == HostRequestState.Queued,
            "accepted and integrated upstream evidence unlocks downstream binding");
        store.CreatePart("new-requirement", "task-a", "unaccepted extra scope", 24);
        check(!queue.MarkStarted(receiptB.RunId, 24),
            "a dependency changed after binding is checked again at actual start");
        store.ApplyControlCommand(new("invalidate-b", store.ReadControlSnapshot().Revision, "human-fixture",
            ControlAction.DefinePackage, Package: Spec("B", ["A"]), ExpectedPackageVersion: 1), 25);
        check(queue.Get(receiptB.RunId)!.State == HostRequestState.Abandoned &&
            !queue.MarkStarted(receiptB.RunId, 26), "editing a bound contract abandons its unstarted queue row atomically");
        check(await consumer.QueueAsync(bindingB, 27) == receiptB && File.ReadAllLines(counter).Length == 1,
            "old receipt after cancellation cannot reopen or resend the abandoned run");

        DefineApprove("LateHead");
        Register("late-head-thread", "late-head-task");
        var lateReceipt = await consumer.QueueAsync(Binding("bind-late-head", "LateHead", "late-head-thread", "late-head-task"), 28);
        File.WriteAllText(Path.Combine(worktree, "a.txt"), "changed by another owner\n");
        await Git(worktree, "add", "a.txt");
        await Git(worktree, "commit", "-q", "-m", "external change after binding");
        await using (var host = new BackgroundHost(new(db, Path.Combine(root, "binding-evidence"),
            "fixture-host-late", Path.Combine(root, "binding-endpoint-late.txt"))))
        {
            check(await host.StartAsync(), "host can resume an unstarted bound queue after restart");
            check(queue.SlotState(SystemProcessProbe.Shared) == HostSlotState.Occupied,
                "restarting a released instance clears its old stopped marker");
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (queue.Get(lateReceipt.RunId)!.State == HostRequestState.Queued && DateTime.UtcNow < deadline)
                await Task.Delay(50);
            check(queue.Get(lateReceipt.RunId)!.State == HostRequestState.Abandoned &&
                queue.Get(lateReceipt.RunId)!.Detail!.Contains("HEAD changed"),
                "host reobserves the real baseline before launch and blocks an externally changed worktree");
            check(File.ReadAllLines(counter).Length == 1, "baseline refusal started no second subprocess");
            await host.StopAsync();
        }

        // A queue can be settled after a lost transport while the run remains Unknown.
        // That does not constitute stopped-writer evidence or permit contract replacement.
        using (var c = new SqliteConnection("Data Source=" + db))
        {
            c.Open(); using var q = c.CreateCommand();
            q.CommandText = "UPDATE harness_runs SET status='Unknown' WHERE run_id=$id";
            q.Parameters.AddWithValue("$id", receipt.RunId); q.ExecuteNonQuery();
        }
        await Refused(() => Task.FromResult(store.ApplyControlCommand(new("unknown-edit",
            store.ReadControlSnapshot().Revision, "human-fixture", ControlAction.DefinePackage,
            Package: Spec("A"), ExpectedPackageVersion: 1), 28)),
            "settled queue with unresolved Unknown run still blocks contract replacement");
        check(store.ReadControlSnapshot().Packages.Single(p => p.Spec.Id == "A").Version == 1,
            "Unknown refusal preserves the reviewed contract lineage");
    }

    private static async Task<string> Git(string cwd, params string[] args)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = cwd, UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new Exception(await error);
        return await output;
    }
}
