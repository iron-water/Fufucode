using System.Diagnostics;
using System.Text.Json;
using Fufucode.Core.Automation;
using Fufucode.Core.Automation.Harnesses;
using Fufucode.Core.Automation.Hosting;
using Microsoft.Data.Sqlite;

internal static class RepairChecks
{
    public static async Task Run(string root, Action<bool, string> check)
    {
        var tree = Path.Combine(root, "repair 工作树"); Directory.CreateDirectory(tree);
        await Git(tree, "init", "-q"); await Git(tree, "config", "user.name", "Fixture"); await Git(tree, "config", "user.email", "fixture@example.invalid");
        File.WriteAllText(Path.Combine(tree, "a.txt"), "fixture\n"); await Git(tree, "add", "a.txt"); await Git(tree, "commit", "-q", "-m", "fixture");
        var head = (await Git(tree, "rev-parse", "HEAD")).Trim().ToUpperInvariant(); var db = Path.Combine(root, "repair.db");
        var counter = Path.Combine(root, "repair-writers.jsonl"); var gate = Path.Combine(root, "repair-release");
        using var store = new AutomationStore(db); var queue = new HostJournal(store); var consumer = new ControlIntentConsumer(store);
        var n = 0; string Id() => "repair-fixture-" + ++n; long Revision() => store.ReadControlSnapshot().Revision;
        void Mode(DevelopmentMode mode) => store.ApplyControlCommand(new(Id(), Revision(), "human", ControlAction.SetMode, Mode: mode), 1);
        void Refuse<T>(Action action, string label) where T : Exception
        { try { action(); } catch (T) { check(true, label); return; } throw new Exception("FAIL " + label); }
        async Task RefuseAsync(Func<Task> action, string label)
        { try { await action(); } catch (InvalidOperationException) { check(true, label); return; } throw new Exception("FAIL " + label); }
        void Sql(string sql)
        { using var c = new SqliteConnection("Data Source=" + db); c.Open(); using var q = c.CreateCommand(); q.CommandText = sql; q.ExecuteNonQuery(); }
        var spec = new WorkPackageSpec("Repair", "Repair", "Keep accepted evidence while fixing a named part", ["a.txt"], ["Return independent evidence"], "codex", "devin/swe-2", "code", []);
        store.ApplyControlCommand(new(Id(), Revision(), "human", ControlAction.DefinePackage, spec, ExpectedPackageVersion: 0), 1);
        store.ApplyControlCommand(new(Id(), Revision(), "human", ControlAction.ApprovePackage, PackageId: spec.Id, ExpectedPackageVersion: 1), 1);
        Mode(DevelopmentMode.SemiAutomatic);
        store.CreateThread("repair-thread", "fixture", "codex", "devin/swe-2", tree, null, 1); store.CreateTask("repair-task", "repair-thread", "Repair", head, 1);
        var prefix = new List<string>(); var executable = Environment.ProcessPath!;
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) prefix.Add(typeof(RepairChecks).Assembly.Location);
        prefix.AddRange(["--repair-protocol", counter, gate]);
        var runtime = new HarnessRuntime("fixture", "codex", executable, prefix.ToArray(), "fixture-v1", new() { ["FUFUCODE_FIXTURE_SECRET"] = "fixture-private-runtime-value" });
        var original = await consumer.QueueAsync(new(Id(), Revision(), "execute:Repair:1", 1, "repair-thread", "repair-task", runtime), 2);
        await using (var sourceHost = new BackgroundHost(new(db, Path.Combine(root, "repair-evidence"), "repair-source-host", Path.Combine(root, "repair-endpoint"))))
        {
            check(await sourceHost.StartAsync(), "repair source uses an actual uniquely claimed background host");
            await Until(() => queue.Get(original.RunId)!.State == HostRequestState.Settled);
            check(sourceHost.Executor.Runs.Get(original.RunId).Status == HarnessRunStatus.Completed, "source produces real completed journal/session/settle HEAD");
            await sourceHost.StopAsync();
        }
        var runs = new HarnessRunStore(store); var receiver = new DeliveryReceiver(store, runs, GitCliEvidence.Shared);
        foreach (var part in new[] { "kept", "fix", "pending" })
        {
            store.CreatePart(part, "repair-task", part, 3);
            var received = await receiver.ReceiveAsync(new(original.RunId, "repair-thread", "repair-task", part, head, "{\"source\":\"real local protocol fixture\"}", Id(), Id(), 3) { CardStatus = "module_ready" });
            check(received.AcceptedForReview, "original actual-run delivery passes the receiving gate: " + part);
            head = store.ListRevisions(part).Single().HeadSha; // Review exactly the receiver's persisted canonical SHA.
            if (part != "pending") store.RecordDecision(Id(), part, part == "kept" ? AutomationDecision.Accept : AutomationDecision.Rework,
                part == "fix" ? "Fix only this part; keep accepted sibling" : null, "{\"checks\":[\"fixture\"]}", "human", Id(), 4, original.RunId, head, 1);
        }
        var kept = store.ListRevisions("kept").Single(); var keptDecision = store.ListDecisions("kept").Single();
        store.CreatePart("planned-only", "repair-task", "No delivery yet", 4);
        check(!store.ReadControlInbox().Items.Any(i => i.Kind == "review" && i.ObjectId == "planned-only"),
            "a planned part without any delivery cannot offer review actions");
        var item = store.ReadControlInbox().Items.Single(i => i.Kind == "repair" && i.ObjectId == "fix");
        var command = new ControlRepairCommand(Id(), Revision(), "human", "repair-task", "fix", 1, original.RunId, head,
            store.ListDecisions("fix").Single().DecisionId, item.EvidenceToken, "Authorize bounded correction through the original session");
        Refuse<ArgumentException>(() => store.ApplyControlRepair(command with { Reason = " " }, 5), "repair authorization requires an explicit reason");
        foreach (var bad in new[] { command with { ExpectedRevision = Revision() - 1 }, command with { ExpectedRunId = "invented" },
            command with { ExpectedHeadSha = new string('f', 40) }, command with { ExpectedDecisionId = "invented" }, command with { ExpectedEvidenceToken = "invented" },
            command with { PartId = "kept" }, command with { TaskId = "wrong" }, command with { ExpectedPartRevision = 2 } })
            Refuse<InvalidOperationException>(() => store.ApplyControlRepair(bad, 5), "repair intent refuses stale or unbound identity");
        var node = JsonSerializer.SerializeToNode(command)!; node["Model"] = "unapproved-model";
        Refuse<JsonException>(() => JsonSerializer.Deserialize<ControlRepairCommand>(node.ToJsonString()), "repair client cannot submit an unapproved model/runtime field");
        Mode(DevelopmentMode.Manual);
        check(!store.ReadControlInbox().Items.Single(i => i.Kind == "repair").Actionable, "manual mode does not offer an automatic repair intent");
        Refuse<InvalidOperationException>(() => store.ApplyControlRepair(command with { ExpectedRevision = Revision() }, 5), "manual mode refuses repair intent creation");
        Mode(DevelopmentMode.SemiAutomatic); command = command with { ExpectedRevision = Revision() };
        Sql("CREATE TRIGGER repair_request_abort BEFORE INSERT ON control_receipts WHEN NEW.command_id='" + command.CommandId + "' BEGIN SELECT RAISE(ABORT,'late repair request'); END;");
        var before = Revision(); var events = store.ListEventsAfter(0).Count;
        Refuse<SqliteException>(() => store.ApplyControlRepair(command, 5), "late repair request failure is reported");
        check(Revision() == before && store.ReadControlSnapshot().Repairs.Count == 0 && store.ListEventsAfter(0).Count == events && queue.List().Count == 1,
            "repair request rollback leaves no phantom intent, queue, event or version");
        Sql("DROP TRIGGER repair_request_abort;");
        var receipt = store.ApplyControlRepair(command, 6);
        check(store.GetThread("repair-thread").Status == ThreadStatus.Active, "explicit repair authorization reactivates a completed controlled thread");
        check(store.ApplyControlRepair(command, 7) == receipt && queue.List().Count == 1 && store.ReadControlSnapshot().Repairs.Single().Status == "awaiting_consumer",
            "human repair approval is one durable intent, not execution or another queue row");
        Refuse<InvalidOperationException>(() => store.ApplyControlRepair(command with { Reason = "changed" }, 7), "replayed repair authorization cannot change its reason");
        check(!store.ReadControlInbox().Items.Any(i => i.Kind == "repair" && i.ObjectId == "fix") &&
            !JsonSerializer.Serialize(store.ReadControlSnapshot()).Contains("fixture-private-runtime-value"), "requested repair is not offered twice and public projection hides private runtime environment");
        var binding = new ControlRepairBinding(Id(), Revision(), receipt.RepairId);
        File.WriteAllText(Path.Combine(tree, "a.txt"), "inherited pending edit\n");
        await RefuseAsync(() => consumer.QueueRepairAsync(binding, 7), "repair consumer preserves and refuses uncommitted work");
        File.WriteAllText(Path.Combine(tree, "a.txt"), "fixture\n");
        Sql("UPDATE harness_bindings SET session_locator='another-session' WHERE thread_id='repair-thread';");
        await RefuseAsync(() => consumer.QueueRepairAsync(binding, 7), "repair cannot attach another saved session");
        Sql("UPDATE harness_bindings SET session_locator='fixture-managed-session' WHERE thread_id='repair-thread'; UPDATE harness_runs SET status='Unknown' WHERE run_id='" + original.RunId + "';");
        await RefuseAsync(() => consumer.QueueRepairAsync(binding, 7), "repair consumer refuses an unresolved source writer");
        Sql("UPDATE harness_runs SET status='Completed' WHERE run_id='" + original.RunId + "';");
        Sql("CREATE TRIGGER repair_bind_abort BEFORE INSERT ON control_repair_bindings BEGIN SELECT RAISE(ABORT,'late repair binding'); END;");
        var threw = false; before = Revision();
        try { await consumer.QueueRepairAsync(binding, 7); } catch (SqliteException) { threw = true; }
        check(threw && Revision() == before && queue.List().Count == 1 && store.ReadControlSnapshot().Repairs.Single().Status == "awaiting_consumer",
            "late repair binding rolls back queue, binding, status and receipt together");
        Sql("DROP TRIGGER repair_bind_abort;");
        var file = Path.Combine(root, "repair-consumer.json"); File.WriteAllText(file, JsonSerializer.Serialize(binding));
        async Task<string> Cli()
        {
            var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in new[] { typeof(Fufucode.Cli.CliOptions).Assembly.Location, "control", "consume-repair", db, file }) start.ArgumentList.Add(arg);
            using var p = Process.Start(start)!; var output = p.StandardOutput.ReadToEndAsync(); var error = p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); var text = await output; var detail = await error;
            if (p.ExitCode != 0) throw new Exception(detail); return text;
        }
        var response = await Cli(); check(response == await Cli(), "actual CLI consumer retry returns the same bound queue receipt");
        var bound = store.ReadControlSnapshot().Repairs.Single(); var repairRun = bound.RunId!;
        var payload = HostJournal.ReadPayload(queue.Get(repairRun)!.PayloadJson);
        check(payload.Runtime.Version == runtime.Version && payload.Runtime.Executable == runtime.Executable && payload.Request.Model == "devin/swe-2" &&
            payload.Request.ThreadId == "repair-thread" && payload.Request.TaskId == "repair-task" && payload.Request.Prompt.Contains("Fix only this part") &&
            payload.Request.Prompt.Contains("Preserve other accepted parts"), "repair payload derives original runtime/model/thread/task and only the human-reviewed correction");
        Sql("UPDATE control_repairs SET run_id=NULL WHERE repair_id='" + bound.Id + "';");
        check(!queue.MarkStarted(repairRun, 8) && queue.NextQueued() is null, "damaged repair authority cannot fall back to an unguarded legacy queue");
        Refuse<InvalidOperationException>(() => store.RecordDelivery(Id(), "kept", repairRun, head, "{}", Id(), 8), "damaged repair authority cannot bypass direct delivery scope");
        Sql("UPDATE control_repairs SET run_id='" + repairRun + "' WHERE repair_id='" + bound.Id + "'; UPDATE host_requests SET request_hash='damaged' WHERE run_id='" + repairRun + "';");
        check(!queue.MarkStarted(repairRun, 8), "repair start refuses a queue payload hash that differs from its trusted binding");
        Sql("UPDATE host_requests SET request_hash='" + HostJournal.Hash(queue.Get(repairRun)!.PayloadJson) + "' WHERE run_id='" + repairRun + "';");
        Mode(DevelopmentMode.Manual);
        check(!queue.MarkStarted(repairRun, 8) && queue.NextQueued() is null, "manual switch after repair enqueue fences actual start");
        Mode(DevelopmentMode.SemiAutomatic);
        check(queue.NextQueued()?.RunId == repairRun, "semi-automatic mode releases the same repair queue identity");
        check(!store.ReadControlInbox().Items.Any(i => i.Kind == "review" && i.ObjectId == "kept") && store.ListRevisions("kept").Single() == kept,
            "queued repair does not redeliver an accepted sibling");
        File.WriteAllText(gate + ".hold", "hold");
        await using (var host = new BackgroundHost(new(db, Path.Combine(root, "repair-evidence"), "repair-followup-host", Path.Combine(root, "repair-endpoint"))))
        {
            check(await host.StartAsync(), "original managed session repair resumes through the unique host");
            try
            {
                await Until(() => runs.ListWorktreeRuns(tree).Any(r => r.RunId == repairRun && r.Status == HarnessRunStatus.Running));
                check(store.ReadControlSnapshot().Repairs.Single().Status == "dispatched", "repair projection records actual dispatch separately from its approval");
                var pending = store.ReadControlInbox().Items.Single(i => i.Kind == "review" && i.ObjectId == "pending");
                using (var context = JsonDocument.Parse(pending.ContextJson))
                    check(!pending.Actionable && context.RootElement.GetProperty("part").GetProperty("CardJson").ValueKind == JsonValueKind.Null,
                        "actual repair writer hides sibling pending card bodies and review actions");
                Refuse<InvalidOperationException>(() => store.ApplyControlCommand(new(Id(), Revision(), "human", ControlAction.DefinePackage, spec, ExpectedPackageVersion: 1), 9),
                    "active repair writer blocks contract replacement through the common writer projection");
                var early = await receiver.ReceiveAsync(new(repairRun, "repair-thread", "repair-task", "fix", head, "{}", Id(), Id(), 9) { CardStatus = "module_ready" });
                check(!early.AcceptedForReview && early.Code == "not-terminal", "running repair cannot deliver a premature card");
                check(await consumer.QueueRepairAsync(binding, 9) == JsonSerializer.Deserialize<ControlRepairQueueReceipt>(response,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }), "binding replay during running repair does not launch another writer");
            }
            finally { File.WriteAllText(gate, "release"); }
            await Until(() => queue.Get(repairRun)!.State == HostRequestState.Settled);
            check(runs.Get(repairRun).Status == HarnessRunStatus.Completed && runs.Get(repairRun).SessionId == runs.Get(original.RunId).SessionId,
                "repair finishes in the exact original native protocol session");
            await host.StopAsync();
        }
        var lines = File.ReadAllLines(counter).Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();
        check(lines.Length == 2 && !lines[0].GetProperty("resume").GetBoolean() && lines[1].GetProperty("resume").GetBoolean() &&
            lines.All(l => l.GetProperty("model").GetString() == "devin/swe-2"), "actual process arguments execute exactly initial + original-session resume, with explicit low-cost model");
        var foreign = await receiver.ReceiveAsync(new(repairRun, "repair-thread", "repair-task", "kept", head, "{}", Id(), Id(), 10) { CardStatus = "module_ready" });
        check(!foreign.AcceptedForReview && foreign.Code == "repair-scope-or-identity", "repair receiving gate refuses a different accepted part in the same task");
        Refuse<InvalidOperationException>(() => store.RecordDelivery(Id(), "kept", repairRun, head, "{}", Id(), 10), "direct U1 cannot bypass authorized repair-part scope");
        var candidate = new DeliveryCandidate(repairRun, "repair-thread", "repair-task", "fix", head, "{\"checks\":[\"repaired local fixture\"]}", Id(), Id(), 10) { CardStatus = "module_ready" };
        var delivery = await receiver.ReceiveAsync(candidate);
        check(delivery.AcceptedForReview && delivery.RevisionNumber == 2 && (await receiver.ReceiveAsync(candidate)).Code == "already-recorded",
            "finished original-session repair creates one new part revision and replay confirms that exact revision");
        check(store.ListRevisions("kept").Single() == kept && store.ListDecisions("kept").Single() == keptDecision &&
            store.GetPart("kept").Status == DeliveryPartStatus.Accepted && store.ListDecisions("fix").Single().Decision == AutomationDecision.Rework,
            "partial repair preserves sibling delivery/acceptance and original rework history");
        item = store.ReadControlInbox().Items.Single(i => i.Kind == "review" && i.ObjectId == "fix");
        store.ApplyControlReview(new(Id(), Revision(), "human-final", "repair-task", "fix", 2, repairRun, head, item.EvidenceToken,
            AutomationDecision.Accept, null, "{\"checks\":[\"independent final fixture\"]}"), 11);
        check(store.GetPart("fix").Status == DeliveryPartStatus.Accepted && store.ListDecisions("fix").Count == 2 && !store.IsIntegrated("fix", 2),
            "new repair revision returns to separate human review; acceptance does not manufacture integration");
        using var reopened = new AutomationStore(db);
        check(reopened.ApplyControlRepair(command, 12) == receipt && await new ControlIntentConsumer(reopened).QueueRepairAsync(binding, 12) ==
            JsonSerializer.Deserialize<ControlRepairQueueReceipt>(response, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) && File.ReadAllLines(counter).Length == 2,
            "lost acknowledgments after reopen return original approvals/bindings without another execution");
        store.CreatePart("dirty-start", "repair-task", "Dirty start counterexample", 13);
        check((await receiver.ReceiveAsync(new(original.RunId, "repair-thread", "repair-task", "dirty-start", head, "{}", Id(), Id(), 13) { CardStatus = "module_ready" })).AcceptedForReview,
            "dirty-start case is bound to an actual original completed delivery");
        store.RecordDecision(Id(), "dirty-start", AutomationDecision.Rework, "Do not overwrite later pending edits", "{}", "human", Id(), 13, original.RunId, head, 1);
        item = store.ReadControlInbox().Items.Single(i => i.Kind == "repair" && i.ObjectId == "dirty-start");
        var dirty = store.ApplyControlRepair(new(Id(), Revision(), "human", "repair-task", "dirty-start", 1, original.RunId, head,
            store.ListDecisions("dirty-start").Single().DecisionId, item.EvidenceToken, "Bound correction"), 13);
        var dirtyQueued = await consumer.QueueRepairAsync(new(Id(), Revision(), dirty.RepairId), 13);
        File.WriteAllText(Path.Combine(tree, "a.txt"), "new edit after approved enqueue\n");
        try
        {
            await using var host = new BackgroundHost(new(db, Path.Combine(root, "repair-evidence"), "repair-dirty-host", Path.Combine(root, "repair-endpoint")));
            check(await host.StartAsync(), "dirty-start counterexample uses the real background start boundary");
            await Until(() => queue.Get(dirtyQueued.RunId)!.State is HostRequestState.Settled or HostRequestState.Abandoned);
            check(queue.Get(dirtyQueued.RunId)!.State == HostRequestState.Abandoned && !runs.ListWorktreeRuns(tree).Any(r => r.RunId == dirtyQueued.RunId) &&
                File.ReadAllLines(counter).Length == 2 && File.ReadAllText(Path.Combine(tree, "a.txt")) == "new edit after approved enqueue\n",
                "uncommitted edit appearing after repair enqueue blocks actual launch and preserves the new edit");
            check(store.ReadControlSnapshot().Repairs.Single(r => r.Id == dirty.RepairId).BlockingReason!.Contains("pending edits"),
                "blocked repair exposes the actual safe start reason without claiming execution");
            await host.StopAsync();
        }
        finally { File.WriteAllText(Path.Combine(tree, "a.txt"), "fixture\n"); }
    }

    private static async Task Until(Func<bool> predicate)
    { var until = DateTime.UtcNow.AddSeconds(30); while (!predicate()) { if (DateTime.UtcNow > until) throw new TimeoutException("Repair fixture did not settle"); await Task.Delay(25); } }
    private static async Task<string> Git(string dir, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in args) start.ArgumentList.Add(arg); using var p = Process.Start(start)!;
        var output = p.StandardOutput.ReadToEndAsync(); var error = p.StandardError.ReadToEndAsync(); await p.WaitForExitAsync(); var text = await output;
        if (p.ExitCode != 0) throw new Exception(await error); return text;
    }
}
