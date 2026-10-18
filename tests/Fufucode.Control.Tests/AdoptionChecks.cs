using System.Diagnostics;
using System.Text.Json;
using Fufucode.Core.Automation;
using Fufucode.Core.Automation.Harnesses;
using Fufucode.Core.Automation.Hosting;
using Microsoft.Data.Sqlite;

internal static class AdoptionChecks
{
    public static async Task Protocol(string mode, string marker, string release)
    {
        _ = await Console.In.ReadToEndAsync();
        File.AppendAllText(marker, "started\n");
        Console.WriteLine("{\"type\":\"thread.started\",\"thread_id\":\"adoption-fixture\"}");
        if (mode == "hold")
        {
            var until = DateTime.UtcNow.AddSeconds(60);
            while (!File.Exists(release))
            { if (DateTime.UtcNow > until) throw new TimeoutException("Fixture not released"); await Task.Delay(20); }
        }
        Console.WriteLine("{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\",\"text\":\"fixture result\"}}");
        if (mode != "unknown") Console.WriteLine("{\"type\":\"turn.completed\"}");
    }

    public static async Task Run(string root, Action<bool, string> check)
    {
        var worktree = Path.Combine(root, "adoption 工作区");
        Directory.CreateDirectory(worktree);
        await Git(worktree, "init", "-q"); await Git(worktree, "config", "user.name", "Fixture");
        await Git(worktree, "config", "user.email", "fixture@example.invalid");
        File.WriteAllText(Path.Combine(worktree, "a.txt"), "fixture\n");
        await Git(worktree, "add", "a.txt"); await Git(worktree, "commit", "-q", "-m", "fixture");
        var head = (await Git(worktree, "rev-parse", "HEAD")).Trim();
        var db = Path.Combine(root, "adoption.db");
        using var store = new AutomationStore(db);
        var queue = new HostJournal(store); var consumer = new ControlIntentConsumer(store);
        var sequence = 0; string Cmd() => "adoption-" + ++sequence;
        long Revision() => store.ReadControlSnapshot().Revision;
        WorkPackageVersion Package(string id) => store.ReadControlSnapshot().Packages.Single(p => p.Spec.Id == id);
        void Define(string id, string[]? refs = null, string[]? deps = null)
        {
            var spec = new WorkPackageSpec(id, id, "Inspect one fixture", ["a.txt"], ["Return a report"], "codex", "devin/swe-2", "code", deps ?? [])
            { KnowledgeRefs = refs };
            store.ApplyControlCommand(new(Cmd(), Revision(), "human", ControlAction.DefinePackage, spec, ExpectedPackageVersion: 0), 1);
        }
        void Approve(string id) => store.ApplyControlCommand(new(Cmd(), Revision(), "human", ControlAction.ApprovePackage,
            PackageId: id, ExpectedPackageVersion: Package(id).Version), 2);
        void Publish(string key, string text)
        {
            var previous = store.ReadControlSnapshot().Knowledge.Facts.Where(f => f.Key == key).Select(f => f.Version).DefaultIfEmpty(0).Max();
            var proposal = Cmd();
            store.ApplyControlKnowledge(new(proposal, Revision(), "M", ControlKnowledgeAction.Propose, key, text, "fixture-source", previous), 3);
            store.ApplyControlKnowledge(new(Cmd(), Revision(), "human", ControlKnowledgeAction.Publish,
                ExpectedKnowledgeVersion: previous, ProposalId: proposal), 4);
        }
        ControlKnowledgeCommand Adoption(string id, string key, long factVersion) => new(Cmd(), Revision(), "human", ControlKnowledgeAction.Adopt,
            Key: key, ExpectedKnowledgeVersion: factVersion) { PackageId = id, ExpectedPackageVersion = Package(id).Version };
        void Refuse(Action action, string label)
        {
            try { action(); } catch (InvalidOperationException) { check(true, label); return; }
            throw new Exception("FAIL " + label);
        }
        async Task RefuseAsync(Func<Task> action, string label)
        { try { await action(); } catch (InvalidOperationException) { check(true, label); return; } throw new Exception("FAIL " + label); }
        var executable = Environment.ProcessPath!;
        HarnessRuntime Runtime(string mode, string marker, string release)
        {
            var prefix = new List<string>();
            if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) prefix.Add(typeof(AdoptionChecks).Assembly.Location);
            prefix.AddRange(["--adoption-protocol", mode, marker, release]);
            return new("fixture", "codex", executable, prefix.ToArray(), "fixture-v1");
        }
        ControlExecutionBinding Binding(string id, HarnessRuntime runtime)
        {
            var version = Package(id).Version; var serial = Cmd();
            store.CreateThread("thread-" + serial, "fixture", "codex", "devin/swe-2", worktree, null, 5);
            store.CreateTask("task-" + serial, "thread-" + serial, id, head, 5);
            return new(Cmd(), Revision(), $"execute:{id}:{version}", version, "thread-" + serial, "task-" + serial, runtime);
        }
        void Deliver(ControlQueueReceipt receipt, string part)
        {
            store.CreatePart(part, receipt.TaskId, part, 7);
            store.RecordDelivery(Cmd(), part, receipt.RunId, head, "{}", Cmd(), 7);
            store.RecordDecision(Cmd(), part, AutomationDecision.Accept, null, "{}", "human", Cmd(), 8, receipt.RunId, head, 1);
        }
        async Task Wait(Func<bool> condition)
        {
            var until = DateTime.UtcNow.AddSeconds(30);
            while (!condition()) { if (DateTime.UtcNow > until) throw new TimeoutException("Adoption fixture state not reached"); await Task.Delay(25); }
        }
        var marker = Path.Combine(root, "adoption-marker.txt"); var release = Path.Combine(root, "adoption-release.txt");
        var runtime = Runtime("done", marker, release);
        Publish("route", "fact-v1");
        Define("A", ["route"]); Define("B"); Define("Child", deps: ["A"]);
        Approve("A"); Approve("B"); Approve("Child");
        check(Package("A").AdoptedKnowledge["route"] == 1 && Package("B").AdoptedKnowledge.Count == 0,
            "initial approval binds declared latest facts without broadcasting to unrelated packages");
        check(!JsonSerializer.Serialize(Package("B").Spec).Contains("KnowledgeRefs"),
            "absent knowledge extension preserves the pre-extension specification payload");
        store.ApplyControlCommand(new(Cmd(), Revision(), "human", ControlAction.SetMode, Mode: DevelopmentMode.SemiAutomatic), 6);
        var binding = Binding("A", runtime); var queued = await consumer.QueueAsync(binding, 6);
        check(HostJournal.ReadPayload(queue.Get(queued.RunId)!.PayloadJson).Request.Prompt.Contains("route @ 1 [fixture-source]: fact-v1"),
            "actual queued prompt contains bound fact version and exact source");
        Publish("route", "fact-v2");
        check(store.ReadControlSnapshot().Knowledge.Adoptions.Count(a => a.Status == "pending") == 1 &&
            store.ReadControlSnapshot().Knowledge.Adoptions.Single(a => a.Status == "pending").PackageId == "A",
            "publication requests adoption only for declared approved unfinished packages");
        check(!queue.MarkStarted(queued.RunId, 7), "actual atomic queue start refuses old knowledge without starting a writer");
        check(queue.Get(queued.RunId)!.State == HostRequestState.Queued && !File.Exists(marker),
            "knowledge start refusal preserves the pending row and creates no process");
        Refuse(() => Approve("A"), "repeat plan approval cannot silently replace adopted facts in the same contract");
        var extraBinding = Binding("B", runtime) with { IntentId = "execute:A:1", ThreadId = binding.ThreadId, TaskId = binding.TaskId };
        await RefuseAsync(() => consumer.QueueAsync(extraBinding, 7), "new binding cannot bypass a pending adoption request");
        var adoption = Adoption("A", "route", 2);
        var before = Revision(); var beforeEvents = store.ListEventsAfter(0).Count;
        using (var c = new SqliteConnection("Data Source=" + db))
        { c.Open(); using var q = c.CreateCommand(); q.CommandText = "CREATE TRIGGER adoption_abort BEFORE INSERT ON control_receipts WHEN NEW.command_id='" + adoption.CommandId + "' BEGIN SELECT RAISE(ABORT,'injected failure'); END;"; q.ExecuteNonQuery(); }
        try { store.ApplyControlKnowledge(adoption, 8); throw new Exception("Rollback not injected"); } catch (SqliteException) { }
        check(Package("A").Version == 1 && Package("A").AdoptedKnowledge["route"] == 1 && Revision() == before &&
            store.ListEventsAfter(0).Count == beforeEvents && queue.Get(queued.RunId)!.State == HostRequestState.Queued,
            "late adoption failure rolls back contract, binding versions, events, approval and queue abandonment");
        using (var c = new SqliteConnection("Data Source=" + db))
        { c.Open(); using var q = c.CreateCommand(); q.CommandText = "DROP TRIGGER adoption_abort"; q.ExecuteNonQuery(); }
        var adopted = store.ApplyControlKnowledge(adoption, 8);
        check(adopted.PackageVersion == 2 && Package("A") is { Version: 2, Approved: true } && Package("A").AdoptedKnowledge["route"] == 2,
            "explicit human adoption approves a new semantic package version");
        check(store.ApplyControlKnowledge(adoption, 9) == adopted && Package("A").Version == 2,
            "adoption replay never creates a third contract or run");
        check(queue.Get(queued.RunId)!.State == HostRequestState.Abandoned &&
            store.ReadControlSnapshot().Intents.Single(i => i.Id == "execute:A:1").Status == "superseded",
            "adoption abandons an unstarted old queue and never revives its intent");
        check(store.GetWorkPackageVersion("A", 1).AdoptedKnowledge["route"] == 1 &&
            Package("Child") is { Version: 2, Approved: false } && Package("B").Version == 1,
            "old adopted evidence persists, descendants get fresh contracts, unrelated work stays unchanged");
        var fresh = await consumer.QueueAsync(Binding("A", runtime), 10);
        check(fresh.RunId != queued.RunId && HostJournal.ReadPayload(queue.Get(fresh.RunId)!.PayloadJson).Request.Prompt.Contains("fact-v2"),
            "the newly adopted contract gets a different run bound to the approved fact text");
        await using var host = new BackgroundHost(new(db, Path.Combine(root, "adoption-evidence"), "adoption-fixture-host", Path.Combine(root, "adoption-endpoint.txt")));
        check(await host.StartAsync(), "adopted plan uses the existing real background host");
        await Wait(() => queue.Get(fresh.RunId)!.State == HostRequestState.Settled);
        check(host.Executor.Runs.Get(fresh.RunId).Status == HarnessRunStatus.Completed && File.ReadAllLines(marker).Length == 1,
            "only the newly adopted run actually executes");
        Deliver(fresh, "adopted-part");
        check(store.CanRequestControlIntegration("A") && !store.CanIntegrateControlPackage("A"), "accepted current adopted evidence needs an explicit integration authorization");
        Publish("route", "fact-v3");
        check(!store.CanIntegrateControlPackage("A"), "a later declared fact fences off an already accepted old outcome");
        Refuse(() => store.MarkIntegrated("adopted-part", 1, head, Cmd(), 11),
            "direct U1 integration cannot bypass pending knowledge adoption");
        var changed = store.ApplyControlKnowledge(Adoption("A", "route", 3), 12);
        check(changed.PackageVersion == 3 && store.GetPart("adopted-part").Status == DeliveryPartStatus.Accepted &&
            !store.CanIntegrateControlPackage("A"), "adoption preserves old accepted history without authorizing the new contract");
        var final = await consumer.QueueAsync(Binding("A", runtime), 12);
        await Wait(() => queue.Get(final.RunId)!.State == HostRequestState.Settled);
        Deliver(final, "final-adopted-part");
        var authorizationCandidate = store.ReadControlIntegrationCandidate("A")!;
        store.ApplyControlIntegration(new(Cmd(), Revision(), "human", ControlIntegrationAction.Authorize,
            "A", authorizationCandidate.Package.Version, authorizationCandidate.EvidenceToken), 12);
        store.MarkIntegrated("final-adopted-part", 1, head, Cmd(), 12);

        Define("Live", ["route"]); Approve("Live");
        var liveMarker = Path.Combine(root, "adoption-live-marker.txt"); var liveRelease = Path.Combine(root, "adoption-live-release.txt");
        var live = await consumer.QueueAsync(Binding("Live", Runtime("hold", liveMarker, liveRelease)), 13);
        await Wait(() => File.Exists(liveMarker));
        Publish("route", "fact-v4");
        check(!store.ReadControlSnapshot().Knowledge.Adoptions.Any(a => a.PackageId == "A" && a.PackageVersion == 3 && a.Status == "pending") &&
            Package("A").AdoptedKnowledge["route"] == 3 && store.IsIntegrated("final-adopted-part", 1),
            "later facts do not rewrite an already integrated package or fabricate another adoption");
        var liveAdopt = Adoption("Live", "route", 4); var liveRevision = Revision();
        check(store.ReadControlSnapshot().Knowledge.Adoptions.Single(a => a.PackageId == "Live" && a.Status == "pending").BlockedReason!.StartsWith("waiting_stop:"),
            "live writer creates a visible wait-for-stop adoption condition");
        Refuse(() => store.ApplyControlKnowledge(liveAdopt, 14), "knowledge adoption cannot rewrite a live writer contract");
        check(Revision() == liveRevision && Package("Live").Version == 1 &&
            HostJournal.ReadPayload(queue.Get(live.RunId)!.PayloadJson).Request.Prompt.Contains("fact-v3"),
            "running prompt retains its bound version and refusal mutates nothing");
        File.WriteAllText(liveRelease, "release");
        await Wait(() => queue.Get(live.RunId)!.State == HostRequestState.Settled);
        store.ApplyControlKnowledge(liveAdopt with { ExpectedRevision = Revision() }, 15);
        check(Package("Live").Version == 2 && Package("Live").AdoptedKnowledge["route"] == 4,
            "confirmed settlement permits a fresh adoption decision without a new dispatch");

        Define("Future", ["future-fact"]); Approve("Future");
        check(Package("Future").AdoptedKnowledge.Count == 0, "declared future knowledge invents no unpublished fact version");
        Publish("future-fact", "future-v1");
        var obsolete = Adoption("Future", "future-fact", 1);
        Publish("future-fact", "future-v2");
        check(store.ReadControlSnapshot().Knowledge.Adoptions.Single(a => a.PackageId == "Future" && a.KnowledgeVersion == 1).Status == "superseded" &&
            store.ReadControlSnapshot().Knowledge.Adoptions.Single(a => a.PackageId == "Future" && a.KnowledgeVersion == 2).Status == "pending",
            "a newer fact supersedes only its obsolete adoption request while preserving history");
        Refuse(() => store.ApplyControlKnowledge(obsolete with { ExpectedRevision = Revision() }, 15),
            "fresh global revision cannot approve an obsolete fact version");
        var latestAdoption = Adoption("Future", "future-fact", 2);
        store.ApplyControlKnowledge(latestAdoption, 15);
        Refuse(() => store.ApplyControlKnowledge(latestAdoption with { ExpectedKnowledgeVersion = 1 }, 15),
            "adoption command identity cannot change its chosen fact version");

        Define("Unknown", ["route"]); Approve("Unknown");
        var uncertain = await consumer.QueueAsync(Binding("Unknown", Runtime("unknown", Path.Combine(root, "unknown-marker.txt"), release)), 16);
        await Wait(() => queue.Get(uncertain.RunId)!.State == HostRequestState.Settled);
        check(host.Executor.Runs.Get(uncertain.RunId).Status == HarnessRunStatus.Unknown,
            "real process exit without a terminal event remains Unknown");
        Publish("route", "fact-v5");
        check(store.ReadControlSnapshot().Knowledge.Adoptions.Single(a => a.PackageId == "Unknown" && a.Status == "pending").BlockedReason!.StartsWith("waiting_reconciliation:"),
            "uncertain stopped transport still requires ownership reconciliation");
        Refuse(() => store.ApplyControlKnowledge(Adoption("Unknown", "route", 5), 17),
            "unknown execution cannot be bypassed by human knowledge adoption");
        await host.StopAsync();
        check((await Git(worktree, "rev-parse", "HEAD")).Trim() == head && File.ReadAllText(Path.Combine(worktree, "a.txt")) == "fixture\n",
            "knowledge publication and adoption never change the reviewed repository");
    }

    private static async Task<string> Git(string root, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!; var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(); if (process.ExitCode != 0) throw new Exception(await error); return await output;
    }
}
