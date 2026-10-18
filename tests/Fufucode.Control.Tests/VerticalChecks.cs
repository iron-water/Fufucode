using System.Diagnostics;
using System.Text.Json;
using Fufucode.Core.Automation;
using Fufucode.Core.Automation.Harnesses;
using Fufucode.Core.Automation.Hosting;
using Microsoft.Data.Sqlite;

internal static class VerticalChecks
{
    // The fixture is a real child process which writes and commits, not a fake Git observer.
    public static async Task Protocol(string[] args)
    {
        var prompt = await Console.In.ReadToEndAsync();
        var modelAt = Array.IndexOf(args, "--model");
        if (modelAt < 0 || args[modelAt + 1] != "devin/swe-2") throw new Exception("Explicit cheapest model required");
        var task = Path.GetFileName(Environment.CurrentDirectory);
        var resume = args.Contains("resume");
        var session = "c4-session-" + task;
        if (resume && !args.Contains(session)) throw new Exception("Original session required");
        if (task == "B" && args.Contains("--hold-second"))
        {
            Console.WriteLine(JsonSerializer.Serialize(new { type = "thread.started", thread_id = session }));
            await Task.Delay(Timeout.Infinite); return;
        }
        if (task == "A")
        {
            if (!resume) File.WriteAllText("kept.txt", "KEEP\n");
            File.WriteAllText("fix.txt", resume ? "AFTER\n" : "BEFORE\n");
            await Git(Environment.CurrentDirectory, "add", "kept.txt", "fix.txt");
        }
        else { File.WriteAllText("other.txt", "SECOND\n"); await Git(Environment.CurrentDirectory, "add", "other.txt"); }
        await Git(Environment.CurrentDirectory, "commit", "-q", "-m", resume ? "C4 bounded repair" : "C4 delivery " + task);
        var sha = (await Git(Environment.CurrentDirectory, "rev-parse", "HEAD")).Trim();
        var card = JsonSerializer.Serialize(new { task, status = "module_ready", head = sha,
            parts = task == "A" ? resume ? new[] { "fix" } : new[] { "kept", "fix" } : new[] { "other" } });
        File.AppendAllText(args[1], JsonSerializer.Serialize(new { task, resume, session, sha, model = args[modelAt + 1], prompt }) + "\n");
        Console.WriteLine(JsonSerializer.Serialize(new { type = "thread.started", thread_id = session }));
        Console.WriteLine(JsonSerializer.Serialize(new { type = "item.completed", item = new { type = "agent_message", text = card } }));
        Console.WriteLine("{\"type\":\"turn.completed\"}");
    }

    public static async Task Run(string root, Action<bool, string> check, HarnessRuntime? live = null, bool timeoutFixture = false)
    {
        if (live is not null && live.Harness != "codex") throw new ArgumentException("C4 live gate currently supports the verified Codex batch protocol only");
        Directory.CreateDirectory(root);
        Console.WriteLine("C4 evidence: " + root);
        var main = Path.Combine(root, "integration"); Directory.CreateDirectory(main);
        await Git(main, "init", "-q"); await Git(main, "config", "user.name", "C4 Fixture");
        await Git(main, "config", "user.email", "c4@example.invalid");
        await Git(main, "config", "core.autocrlf", "false");
        File.WriteAllText(Path.Combine(main, "baseline.txt"), "BASE\n");
        await Git(main, "add", "baseline.txt"); await Git(main, "commit", "-q", "-m", "C4 isolated baseline");
        var baseline = (await Git(main, "rev-parse", "HEAD")).Trim();
        var db = Path.Combine(root, "control.db"); var counter = Path.Combine(root, "writers.jsonl");
        using var store = new AutomationStore(db); var consumer = new ControlIntentConsumer(store); var journal = new HostJournal(store);
        var n = 0; string Id() => "c4-command-" + ++n; long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long Rev() => store.ReadControlSnapshot().Revision;
        var executable = Environment.ProcessPath!; var prefix = new List<string>();
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) prefix.Add(typeof(VerticalChecks).Assembly.Location);
        prefix.AddRange(["--c4-protocol", counter]);
        if (timeoutFixture) prefix.Add("--hold-second");
        var runtime = live ?? new HarnessRuntime("c4-fixture", "codex", executable, prefix.ToArray(), "fixture-v1");
        var adapter = new CodexBatchAdapter(); var probeRequest = new HarnessRequest("args", "thread-A", "task-A", "devin/swe-2", "unused");
        var defaults = runtime with { CodexSandbox = null };
        var defaultArgs = adapter.Prepare(defaults, probeRequest, root, root, null).ArgumentList;
        check(defaultArgs[defaultArgs.IndexOf("--sandbox") + 1] == "workspace-write" && !JsonSerializer.Serialize(defaults).Contains("CodexSandbox"),
            "default sandbox and legacy deployment serialization remain unchanged");
        foreach (var policy in new[] { "read-only", "workspace-write", "danger-full-access" })
        {
            var configured = JsonSerializer.Deserialize<HarnessRuntime>(JsonSerializer.Serialize(runtime with { CodexSandbox = policy }))!;
            var actualArgs = adapter.Prepare(configured, probeRequest, root, root, null).ArgumentList;
            check(actualArgs[actualArgs.IndexOf("--sandbox") + 1] == policy, "explicit trusted deployment policy survives persistence: " + policy);
        }
        try { adapter.Prepare(runtime with { CodexSandbox = "invented-policy" }, probeRequest, root, root, null); throw new Exception("FAIL invalid sandbox accepted"); }
        catch (ArgumentException) { check(true, "invalid deployment policy is rejected before launching a child"); }
        var queued = new Dictionary<string, ControlQueueReceipt>(); var bindings = new Dictionary<string, ControlExecutionBinding>();
        foreach (var id in new[] { "A", "B" })
        {
            var tree = Path.Combine(root, id); await Git(root, "clone", "-c", "core.autocrlf=false", "-q", main, tree);
            await Git(tree, "config", "user.name", "C4 Fixture"); await Git(tree, "config", "user.email", "c4@example.invalid");
            var work = id == "A" ? "Create kept.txt containing exactly KEEP followed by a newline, and fix.txt containing exactly BEFORE followed by a newline." :
                "Create other.txt containing exactly SECOND followed by a newline.";
            var parts = id == "A" ? "[\"kept\",\"fix\"]" : "[\"other\"]";
            var goal = work + " Commit only these files in the current repository. Do not delegate, read other directories, change configuration, or access the network. " +
                "Use local file and git tools only. Finish with exactly one JSON object, no markdown: " +
                "{\"task\":\"" + id + "\",\"status\":\"module_ready\",\"head\":\"<actual full git HEAD>\",\"parts\":" + parts + "}. " +
                "On a later bounded repair, follow its findings, leave kept.txt unchanged and report only parts:[\"fix\"].";
            store.ApplyControlCommand(new(Id(), Rev(), "human-fixture", ControlAction.DefinePackage,
                new(id, "C4 " + id, goal, id == "A" ? ["kept.txt", "fix.txt"] : ["other.txt"],
                    ["Exact file contents, clean Git commit, JSON card and unchanged baseline.txt"], "codex", "devin/swe-2", "code", []), ExpectedPackageVersion: 0), Now());
            store.ApplyControlCommand(new(Id(), Rev(), "human-fixture", ControlAction.ApprovePackage, PackageId: id, ExpectedPackageVersion: 1), Now());
            store.CreateThread("thread-" + id, "c4", "codex", "devin/swe-2", tree, null, Now());
            store.CreateTask("task-" + id, "thread-" + id, "C4 " + id, baseline, Now());
        }
        store.ApplyControlCommand(new(Id(), Rev(), "human-fixture", ControlAction.SetMode, Mode: DevelopmentMode.SemiAutomatic), Now());
        foreach (var id in new[] { "A", "B" })
        {
            var binding = new ControlExecutionBinding(Id(), Rev(), "execute:" + id + ":1", 1, "thread-" + id, "task-" + id, runtime);
            bindings.Add(id, binding); queued.Add(id, await consumer.QueueAsync(binding, Now()));
        }
        check(journal.List().Count == 2 && journal.List().All(q => q.State == HostRequestState.Queued), "two approved tasks persist before any host or model starts");
        var hostOptions = new HostOptions(db, Path.Combine(root, "runs"), "c4-host", Path.Combine(root, "endpoint"));
        async Task Drain(params string[] runs)
        {
            await using var host = new BackgroundHost(hostOptions);
            check(await host.StartAsync(), "C4 uses the sole actual background host");
            try
            {
                var until = DateTime.UtcNow.AddSeconds(timeoutFixture ? 5 : live is null ? 30 : 180 * runs.Length);
                while (runs.Any(r => journal.Get(r)!.State is HostRequestState.Queued or HostRequestState.Started))
                { if (DateTime.UtcNow > until) throw new TimeoutException("C4 bounded host wait expired; preserve evidence, do not resend"); await Task.Delay(100); }
                foreach (var run in runs)
                {
                    var state = host.Executor.Runs.Get(run);
                    check(state.Status == HarnessRunStatus.Completed, "stopped completed transport: " + run);
                }
            }
            catch (TimeoutException)
            {
                // Stop new dispatch first; terminate only a PID/start-time proven to be our test writer.
                var stopping = host.StopAsync();
                foreach (var runId in runs)
                {
                    var run = host.Executor.Runs.Get(runId);
                    // OwnedHere intentionally refuses live writers; validate this test supervisor directly.
                    using var self = Process.GetCurrentProcess();
                    if (run.HostId != host.HostId || run.SupervisorPid != Environment.ProcessId ||
                        run.SupervisorStartUtc != new DateTimeOffset(self.StartTime.ToUniversalTime()).ToUnixTimeMilliseconds()) continue;
                    if (run.WriterPid is not { } pid || run.WriterStartUtc is not { } started) continue;
                    if (SystemProcessProbe.Shared.Probe(pid).Liveness != ProcessLiveness.Alive) continue;
                    using var writer = Process.GetProcessById(pid);
                    if (!writer.HasExited && new DateTimeOffset(writer.StartTime.ToUniversalTime()).ToUnixTimeMilliseconds() == started)
                        writer.Kill(entireProcessTree: true);
                }
                await stopping.WaitAsync(TimeSpan.FromSeconds(15));
                throw;
            }
            finally { await host.StopAsync(); }
        }
        if (timeoutFixture)
        {
            var timedOut = false;
            try { await Drain(queued.Values.Select(q => q.RunId).ToArray()); }
            catch (TimeoutException) { timedOut = true; }
            check(timedOut, "deliberately held second writer triggers a bounded test timeout");
            var savedRuns = new HarnessRunStore(store);
            check(queued.Values.All(q => journal.Get(q.RunId)!.State is not HostRequestState.Started) &&
                queued.Values.All(q => SystemProcessProbe.Shared.Probe(savedRuns.Get(q.RunId).WriterPid!.Value).Liveness == ProcessLiveness.Dead),
                "timeout cleanup leaves no active owned test writer or started request");
            check(store.ListParts("task-A").Count == 0 && store.ListParts("task-B").Count == 0,
                "timeout cannot turn an early commit or unfinished output into a delivery");
            return;
        }
        await Drain(queued.Values.Select(q => q.RunId).ToArray());
        var runStore = new HarnessRunStore(store); var receiver = new DeliveryReceiver(store, runStore, GitCliEvidence.Shared);
        async Task Deliver(string id, string runId, string[] expectedParts)
        {
            var run = runStore.Get(runId);
            var audit = await new RunReconciler(store, runStore, SystemProcessProbe.Shared, GitCliEvidence.Shared, "c4-host").AuditAsync(runId);
            check(audit.WriterStopped && audit.Verdict == OwnerVerdict.ConfirmedFinished, "ownership independently confirms stopped writer: " + id);
            using var card = JsonDocument.Parse(run.Output!);
            check(card.RootElement.GetProperty("task").GetString() == id && card.RootElement.GetProperty("status").GetString() == "module_ready" &&
                card.RootElement.GetProperty("parts").EnumerateArray().Select(p => p.GetString()).SequenceEqual(expectedParts), "producer supplies exact structured part card: " + id);
            var sha = card.RootElement.GetProperty("head").GetString()!;
            check(sha == run.SettleHead && sha == (await Git(Path.Combine(root, id), "rev-parse", "HEAD")).Trim() && sha != baseline &&
                await GitCliEvidence.Shared.IsCleanAsync(Path.Combine(root, id)) == true, "card names own committed clean settle HEAD: " + id);
            var changed = (await Git(Path.Combine(root, id), "diff", "--name-only", baseline + "..HEAD"))
                .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(f => f.Trim()).Order().ToArray();
            check(changed.SequenceEqual(id == "A" ? new[] { "fix.txt", "kept.txt" } : new[] { "other.txt" }),
                "independent commit diff stays inside the approved file scope: " + id);
            foreach (var part in expectedParts)
            {
                if (!store.ListParts("task-" + id).Any(p => p.PartId == part)) store.CreatePart(part, "task-" + id, part, Now());
                var outcome = await receiver.ReceiveAsync(new(runId, "thread-" + id, "task-" + id, part, sha, run.Output!, Id(), Id(), Now()) { CardStatus = "module_ready" });
                check(outcome.AcceptedForReview, "real run/card/HEAD receiving gate admits: " + part);
            }
        }
        await Deliver("A", queued["A"].RunId, ["kept", "fix"]); await Deliver("B", queued["B"].RunId, ["other"]);
        check(store.ReadControlInbox().Items.Count(i => i.Kind == "review") == 3, "two stopped task deliveries accumulate without automatic review");
        void Content(string tree, string file, string text) => check(File.ReadAllText(Path.Combine(root, tree, file)) == text, tree + " independent contents: " + file);
        Content("A", "kept.txt", "KEEP\n"); Content("A", "fix.txt", "BEFORE\n"); Content("B", "other.txt", "SECOND\n");
        foreach (var tree in new[] { "A", "B" }) Content(tree, "baseline.txt", "BASE\n");
        ControlReviewCommand Review(string part, AutomationDecision decision, string? findings = null)
        {
            var item = store.ReadControlInbox().Items.Single(i => i.Kind == "review" && i.ObjectId == part);
            var rev = store.ListRevisions(part).Last(); var task = store.GetPart(part).TaskId;
            return new(Id(), Rev(), "human-fixture", task, part, rev.RevisionNumber, rev.RunId, rev.HeadSha, item.EvidenceToken,
                decision, findings, "{\"checks\":[\"independent exact file, clean Git and stopped writer\"]}");
        }
        var staleB = Review("other", AutomationDecision.Accept);
        var keptCommand = Review("kept", AutomationDecision.Accept); var accepted = store.ApplyControlReview(keptCommand, Now());
        check(store.ApplyControlReview(keptCommand, Now()) == accepted, "lost review receipt replay does not append another decision");
        try { store.ApplyControlReview(staleB, Now()); throw new Exception("FAIL stale parallel review accepted"); }
        catch (InvalidOperationException) { check(store.GetPart("other").Status == DeliveryPartStatus.Delivered, "stale second reviewer cannot overwrite serial human decision revision"); }
        store.ApplyControlReview(Review("fix", AutomationDecision.Rework, "Change only fix.txt to exactly AFTER followed by newline, commit it, preserve kept.txt. Report task A, parts [fix], new full HEAD in the same JSON card."), Now());
        var keptRevision = store.ListRevisions("kept").Single(); var keptDecision = store.ListDecisions("kept").Single(); var otherRevision = store.ListRevisions("other").Single();
        var repairItem = store.ReadControlInbox().Items.Single(i => i.Kind == "repair" && i.ObjectId == "fix");
        var source = store.ListRevisions("fix").Single(); var sourceSession = runStore.Get(source.RunId).SessionId;
        var authorized = store.ApplyControlRepair(new(Id(), Rev(), "human-fixture", "task-A", "fix", 1, source.RunId, source.HeadSha,
            store.ListDecisions("fix").Single().DecisionId, repairItem.EvidenceToken, "Explicit fixture human approves only named correction"), Now());
        var repairBinding = new ControlRepairBinding(Id(), Rev(), authorized.RepairId);
        var repair = await consumer.QueueRepairAsync(repairBinding, Now());
        check(await consumer.QueueRepairAsync(repairBinding, Now()) == repair && journal.List().Count == 3, "repair consumer lost acknowledgment returns original run without duplication");
        await Drain(repair.RunId);
        check(runStore.Get(repair.RunId).SessionId == sourceSession && runStore.Get(repair.RunId).TaskId == "task-A" &&
            HostJournal.ReadPayload(journal.Get(repair.RunId)!.PayloadJson).Request.Model == "devin/swe-2", "actual repair resumes original session/model/task");
        await Deliver("A", repair.RunId, ["fix"]);
        Content("A", "kept.txt", "KEEP\n"); Content("A", "fix.txt", "AFTER\n");
        check(store.ListRevisions("kept").Single() == keptRevision && store.ListDecisions("kept").Single() == keptDecision &&
            store.ListRevisions("other").Single() == otherRevision && store.ListDecisions("other").Count == 0, "local repair preserves accepted sibling and other queued-for-review task exactly");
        var replay = await receiver.ReceiveAsync(new(keptRevision.RunId, "thread-A", "task-A", "kept", keptRevision.HeadSha,
            keptRevision.CardJson, Id(), Id(), Now()) { CardStatus = "module_ready" });
        check(replay.AcceptedForReview && replay.Code == "already-recorded" && store.ListRevisions("kept").Count == 1,
            "lost original delivery acknowledgment replays after repair advanced HEAD without replacing accepted evidence");
        store.ApplyControlReview(Review("fix", AutomationDecision.Accept), Now());
        check(store.GetPart("fix").CurrentRevision == 2 && Scalar(db, "SELECT COUNT(*) FROM integration_marks") == 0,
            "new revision has independent acceptance with zero integration marks");
        var mainShas = new Dictionary<string, string>();
        foreach (var id in new[] { "A", "B" })
        {
            if (id == "B") store.ApplyControlReview(Review("other", AutomationDecision.Accept), Now());
            var candidate = store.ReadControlIntegrationCandidate(id)!;
            store.ApplyControlIntegration(new(Id(), Rev(), "human-fixture", ControlIntegrationAction.Authorize, id, 1, candidate.EvidenceToken), Now());
            var tree = Path.Combine(root, id); await Git(main, "fetch", "-q", tree, "HEAD");
            var commits = (await Git(tree, "rev-list", "--reverse", baseline + "..HEAD")).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            foreach (var commit in commits) await Git(main, "cherry-pick", commit.Trim());
            Content("integration", "baseline.txt", "BASE\n"); Content("integration", "kept.txt", "KEEP\n"); Content("integration", "fix.txt", "AFTER\n");
            if (id == "B") Content("integration", "other.txt", "SECOND\n");
            check(await GitCliEvidence.Shared.IsCleanAsync(main) == true, "independent post-integration repository check: " + id);
            var sha = (await Git(main, "rev-parse", "HEAD")).Trim(); mainShas.Add(id, sha);
            foreach (var part in store.ListParts("task-" + id)) store.MarkIntegrated(part.PartId, part.CurrentRevision, sha, Id(), Now());
            check(store.ReadControlIntegrationCandidate(id)!.Integrated, "integration marked only after explicit authorization and actual Git verification: " + id);
        }
        check(mainShas["A"] != baseline && mainShas["B"] != mainShas["A"] && Scalar(db, "SELECT COUNT(*) FROM integration_marks") == 3,
            "two real integration commits have distinct SHA and exact accepted part marks");
        var rows = journal.List().Count; var events = store.ListEventsAfter(0).Count;
        foreach (var id in new[] { "A", "B" }) check(await consumer.QueueAsync(bindings[id], Now()) == queued[id], "settled original execution receipt replay: " + id);
        await using (var idle = new BackgroundHost(hostOptions))
        {
            check(await idle.StartAsync(), "empty host restarts against durable completed history");
            for (var i = 0; i < 5; i++) { _ = store.ReadControlClientSnapshot(); await Task.Delay(100); }
            check(journal.List().Count == rows && runStore.ListWorktreeRuns(Path.Combine(root, "A")).Count == 2 &&
                runStore.ListWorktreeRuns(Path.Combine(root, "B")).Count == 1, "idle reads and host polling create zero additional harness/model runs");
            await idle.StopAsync();
        }
        using (var reopened = new AutomationStore(db))
            check(reopened.GetPart("fix").CurrentRevision == 2 && reopened.ListDecisions("fix").Count == 2 &&
                reopened.ReadControlIntegrationCandidate("A")!.Integrated && reopened.ReadControlIntegrationCandidate("B")!.Integrated,
                "reopening the authoritative store retains both integration outcomes and original repair decision history");
        File.WriteAllText(Path.Combine(root, "result.json"), JsonSerializer.Serialize(new { model = "devin/swe-2", live = live is not null, baseline, mainShas,
            runs = journal.List().Select(q => new { q.RunId, q.State }), reviewedParts = 3, eventsBeforeIdle = events, status = "passed" }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static long Scalar(string db, string sql)
    { using var c = new SqliteConnection("Data Source=" + db); c.Open(); using var q = c.CreateCommand(); q.CommandText = sql; return Convert.ToInt64(q.ExecuteScalar()); }
    private static async Task<string> Git(string tree, params string[] args)
    {
        var s = new ProcessStartInfo("git") { WorkingDirectory = tree, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) s.ArgumentList.Add(arg);
        using var p = Process.Start(s)!; var output = p.StandardOutput.ReadToEndAsync(); var error = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); var text = await output; var detail = await error;
        if (p.ExitCode != 0) throw new Exception("git " + args[0] + ": " + detail); return text;
    }
}
