using System.Diagnostics;
using System.Text.Json;
using Fufucode.Core.Automation;
using Fufucode.Core.Automation.Harnesses;
using Fufucode.Core.Automation.Hosting;
using Microsoft.Data.Sqlite;

internal static class RelationChecks
{
    internal static async Task Run(string root, Action<bool, string> check)
    {
        void Refused<T>(Action action, string label) where T : Exception
        {
            try { action(); } catch (T) { check(true, label); return; }
            throw new Exception("FAIL " + label);
        }
        async Task RefusedAsync(Func<Task> action, string label)
        {
            try { await action(); } catch (InvalidOperationException) { check(true, label); return; }
            throw new Exception("FAIL " + label);
        }
        var worktree = Path.Combine(root, "relation 工作区");
        Directory.CreateDirectory(worktree);
        await Git(worktree, "init", "-q");
        await Git(worktree, "config", "user.name", "Fixture");
        await Git(worktree, "config", "user.email", "fixture@example.invalid");
        File.WriteAllText(Path.Combine(worktree, "a.txt"), "fixture\n");
        await Git(worktree, "add", "a.txt"); await Git(worktree, "commit", "-q", "-m", "fixture");
        var head = (await Git(worktree, "rev-parse", "HEAD")).Trim();
        var db = Path.Combine(root, "relations.db");
        using var store = new AutomationStore(db);
        var queue = new HostJournal(store);
        var consumer = new ControlIntentConsumer(store);
        long sequence = 0;
        string Cmd() => "relation-test-" + ++sequence;
        long Revision() => store.ReadControlSnapshot().Revision;
        WorkPackageSpec Spec(string id, string artifact = "code", string[]? deps = null) =>
            new(id, id, "Inspect the bounded local fixture", ["a.txt"], ["Return a report"], "codex", "devin/swe-2", artifact, deps ?? []);
        void Define(string id, string artifact = "code", string[]? deps = null) =>
            store.ApplyControlCommand(new(Cmd(), Revision(), "human-fixture", ControlAction.DefinePackage,
                Package: Spec(id, artifact, deps), ExpectedPackageVersion: 0), sequence);
        void Approve(string id)
        {
            var version = store.ReadControlSnapshot().Packages.Single(p => p.Spec.Id == id).Version;
            store.ApplyControlCommand(new(Cmd(), Revision(), "human-fixture", ControlAction.ApprovePackage,
                PackageId: id, ExpectedPackageVersion: version), sequence);
        }
        ControlRelationCommand Group(string id, ControlRelation relation, params string[] ids) =>
            new(Cmd(), Revision(), "human-fixture", ControlRelationAction.DefineGroup,
                new(id, relation, ids, relation == ControlRelation.Competitive ? head : null,
                    relation == ControlRelation.Competitive ? ["correctness", "cost"] : null));
        ControlRelationCommand Choice(string group, string winner) =>
            new(Cmd(), Revision(), "human-fixture", ControlRelationAction.SelectWinner, GroupId: group, Winner: winner,
                ExpectedEvidenceToken: store.ReadControlGroups().Single(g => g.Spec.Id == group).Members.Single(m => m.PackageId == winner).EvidenceToken);

        Define("A"); Define("B"); Define("C");
        var competition = Group("competition", ControlRelation.Competitive, "A", "B");
        var receipt = store.ApplyControlRelation(competition, 1);
        check(store.ApplyControlRelation(competition, 2) == receipt && store.ReadControlGroups().Count == 1,
            "relation replay keeps one group and the original receipt");
        Refused<InvalidOperationException>(() => store.ApplyControlRelation(competition with { DecidedBy = "another" }, 3),
            "relation replay cannot change its attribution");
        Refused<InvalidOperationException>(() => store.ApplyControlRelation(Group("overlap", ControlRelation.Competitive, "B", "C"), 3),
            "one package cannot join two competing groups");
        Refused<ArgumentException>(() => store.ApplyControlRelation(Group("bad-members", ControlRelation.Independent, "C", "C"), 3),
            "duplicate group members do not create a relation");
        Refused<InvalidOperationException>(() => store.ApplyControlRelation(Choice("competition", "A"), 3),
            "an unreviewed proposal cannot win a competition");
        Approve("A"); Approve("B"); Approve("C");
        Refused<InvalidOperationException>(() => store.ApplyControlRelation(Group("too-late", ControlRelation.Independent, "A", "C"), 4),
            "relations cannot be silently attached after approval");
        store.ApplyControlCommand(new(Cmd(), Revision(), "human-fixture", ControlAction.SetMode, Mode: DevelopmentMode.SemiAutomatic), 4);
        var executable = Environment.ProcessPath!;
        var prefix = new List<string>();
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            prefix.Add(typeof(RelationChecks).Assembly.Location);
        prefix.AddRange(["--protocol", Path.Combine(root, "relation-writer.log")]);
        var runtime = new HarnessRuntime("fixture", "codex", executable, prefix.ToArray(), "fixture-v1");
        await using var host = new BackgroundHost(new(db, Path.Combine(root, "relation-evidence"),
            "relation-fixture-host", Path.Combine(root, "relation-endpoint.txt")));
        check(await host.StartAsync(), "relation gates share the actual persistent background host");
        ControlExecutionBinding Binding(string id)
        {
            store.CreateThread("thread-" + id, "fixture", "codex", "devin/swe-2", worktree, null, 5);
            store.CreateTask("task-" + id, "thread-" + id, id, head, 5);
            var version = store.ReadControlSnapshot().Packages.Single(p => p.Spec.Id == id).Version;
            return new(Cmd(), Revision(), $"execute:{id}:{version}", version, "thread-" + id, "task-" + id, runtime);
        }
        async Task<ControlQueueReceipt> Execute(ControlExecutionBinding binding)
        {
            var queued = await consumer.QueueAsync(binding, 6);
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (queue.Get(queued.RunId)!.State != HostRequestState.Settled && DateTime.UtcNow < deadline) await Task.Delay(40);
            if (queue.Get(queued.RunId)!.State != HostRequestState.Settled || host.Executor.Runs.Get(queued.RunId).Status != HarnessRunStatus.Completed)
                throw new Exception("Relation fixture did not complete: " + queued.RunId);
            return queued;
        }
        void Deliver(string id, string run, string part, AutomationDecision? decision = AutomationDecision.Accept)
        {
            if (!store.ListParts("task-" + id).Any(p => p.PartId == part)) store.CreatePart(part, "task-" + id, part, 7);
            var revision = store.RecordDelivery(Cmd(), part, run, head, "{}", Cmd(), 7);
            if (decision is not null)
                store.RecordDecision(Cmd(), part, decision.Value, decision == AutomationDecision.Rework ? "repair this part" : null,
                    "{}", "human-fixture", Cmd(), 8, run, head, revision.RevisionNumber);
        }
        void Authorize(string id)
        {
            var candidate = store.ReadControlIntegrationCandidate(id)!;
            store.ApplyControlIntegration(new(Cmd(), Revision(), "human-fixture", ControlIntegrationAction.Authorize,
                id, candidate.Package.Version, candidate.EvidenceToken), 9);
        }
        var runA = await Execute(Binding("A")); var runB = await Execute(Binding("B"));
        Deliver("A", runA.RunId, "part-a"); Deliver("B", runB.RunId, "part-b");
        check(!store.CanIntegrateControlPackage("A") && !store.CanIntegrateControlPackage("B"),
            "acceptance does not integrate all competing candidates");
        Refused<InvalidOperationException>(() => store.MarkIntegrated("part-a", 1, head, Cmd(), 9),
            "direct U1 integration cannot bypass the competition choice");
        var select = Choice("competition", "B"); store.ApplyControlRelation(select, 10);
        check(store.CanRequestControlIntegration("B") && !store.CanRequestControlIntegration("A") && !store.CanIntegrateControlPackage("B") && !store.IsIntegrated("part-b", 1),
            "human selection admits only the winner for an explicit integration authorization and creates no mark");
        store.CreatePart("b-extra", "task-B", "late required evidence", 10);
        check(!store.ReadControlGroups().Single(g => g.Spec.Id == "competition").SelectionCurrent && !store.CanIntegrateControlPackage("B"),
            "a later required part invalidates an already selected outcome");
        Refused<InvalidOperationException>(() => store.ApplyControlRelation(select with { CommandId = Cmd(), ExpectedRevision = Revision() }, 11),
            "fresh command cannot reuse a stale outcome token");
        Deliver("B", runB.RunId, "b-extra");
        check(!store.CanIntegrateControlPackage("B"), "accepting new evidence still requires a fresh selection");
        store.ApplyControlRelation(Choice("competition", "B"), 12);
        Authorize("B");
        store.MarkIntegrated("part-b", 1, head, Cmd(), 13); store.MarkIntegrated("b-extra", 1, head, Cmd(), 13);
        check(store.IsIntegrated("b-extra", 1) && store.ReadControlGroups().Single(g => g.Spec.Id == "competition").SelectionCurrent,
            "one pinned winner permits all current parts while integration progresses");
        Refused<InvalidOperationException>(() => store.ApplyControlRelation(Choice("competition", "A"), 14),
            "later selection cannot replace an already integrated winner");
        check(store.GetPart("part-a").Status == DeliveryPartStatus.Accepted, "losing accepted candidate and its history stay inspectable");

        Define("Version1"); Define("Version2");
        store.ApplyControlRelation(Group("versioned-choice", ControlRelation.Competitive, "Version1", "Version2"), 14);
        Approve("Version1"); Approve("Version2");
        var versioned = await Execute(Binding("Version1")); Deliver("Version1", versioned.RunId, "versioned-part");
        store.ApplyControlRelation(Choice("versioned-choice", "Version1"), 14);
        store.ApplyControlCommand(new(Cmd(), Revision(), "human-fixture", ControlAction.DefinePackage,
            Package: Spec("Version2") with { Goal = "Revised alternative" }, ExpectedPackageVersion: 1), 14);
        check(!store.ReadControlGroups().Single(g => g.Spec.Id == "versioned-choice").SelectionCurrent &&
            !store.CanIntegrateControlPackage("Version1"), "changing a competing contract invalidates the pinned comparison versions");
        Refused<InvalidOperationException>(() => store.MarkIntegrated("versioned-part", 1, head, Cmd(), 14),
            "an obsolete comparison cannot be reused at the integration boundary");

        Define("Code"); Define("Evidence", "evidence");
        store.ApplyControlRelation(Group("bundle", ControlRelation.Complementary, "Code", "Evidence"), 15);
        Approve("Code"); Approve("Evidence");
        var code = await Execute(Binding("Code")); var evidence = await Execute(Binding("Evidence"));
        Deliver("Code", code.RunId, "code");
        check(!store.CanIntegrateControlPackage("Code"), "complementary code waits for its evidence member");
        Refused<InvalidOperationException>(() => store.MarkIntegrated("code", 1, head, Cmd(), 16),
            "direct integration cannot bypass an incomplete complementary bundle");
        Deliver("Evidence", evidence.RunId, "proof");
        check(store.CanRequestControlIntegration("Code") && !store.CanIntegrateControlPackage("Code"), "accepted evidence unlocks a complementary code authorization request");
        check(!store.CanIntegrateControlPackage("Evidence"), "an evidence artifact is consumed as proof and is not a code integration candidate");
        Refused<InvalidOperationException>(() => store.MarkIntegrated("proof", 1, head, Cmd(), 17),
            "direct integration cannot fabricate a code mark for an evidence artifact");
        store.CreatePart("new-proof", "task-Evidence", "new requirement", 17);
        check(!store.CanIntegrateControlPackage("Code"), "new missing bundle evidence is rechecked at integration time");
        Deliver("Evidence", evidence.RunId, "new-proof"); Authorize("Code"); store.MarkIntegrated("code", 1, head, Cmd(), 18);
        check(!store.IsIntegrated("proof", 1), "complementary evidence acceptance never invents a code integration mark");

        Define("First"); Define("Next");
        store.ApplyControlRelation(Group("pipeline", ControlRelation.Pipeline, "First", "Next"), 19);
        check(store.GetWorkPackageVersion("Next", 2).Spec.DependsOn.SequenceEqual(["First"]) &&
            store.GetWorkPackageVersion("Next", 1).Spec.DependsOn.Length == 0,
            "pipeline adds a versioned predecessor edge and preserves the old plan");
        Approve("First"); Approve("Next");
        var nextBinding = Binding("Next");
        await RefusedAsync(() => consumer.QueueAsync(nextBinding, 20), "pipeline cannot start from plan approval alone");
        var first = await Execute(Binding("First")); Deliver("First", first.RunId, "first");
        await RefusedAsync(() => consumer.QueueAsync(nextBinding with { ExpectedRevision = Revision() }, 21),
            "pipeline code predecessor acceptance still requires real integration");
        Authorize("First"); store.MarkIntegrated("first", 1, head, Cmd(), 22);
        var next = await Execute(nextBinding with { ExpectedRevision = Revision() });
        check(queue.Get(next.RunId)!.State == HostRequestState.Settled, "integration unlocks downstream through the actual queue and subprocess");
        Refused<InvalidOperationException>(() => store.ApplyControlCommand(new(Cmd(), Revision(), "human-fixture", ControlAction.DefinePackage,
            Package: Spec("Next"), ExpectedPackageVersion: 2), 23), "ordinary package edit cannot remove a pipeline relation");

        Define("ProofFirst", "evidence"); Define("AfterProof");
        store.ApplyControlRelation(Group("evidence-pipeline", ControlRelation.Pipeline, "ProofFirst", "AfterProof"), 24);
        Approve("ProofFirst"); Approve("AfterProof");
        var proofFirst = await Execute(Binding("ProofFirst")); Deliver("ProofFirst", proofFirst.RunId, "proof-first");
        var afterProof = await Execute(Binding("AfterProof"));
        check(queue.Get(afterProof.RunId)!.State == HostRequestState.Settled && !store.IsIntegrated("proof-first", 1),
            "evidence pipeline unlocks on accepted evidence without a invented integration");

        Define("Independent1"); Define("Independent2");
        store.ApplyControlRelation(Group("independent", ControlRelation.Independent, "Independent1", "Independent2"), 25);
        Approve("Independent1"); Approve("Independent2");
        var independent = await Execute(Binding("Independent1")); Deliver("Independent1", independent.RunId, "independent-part");
        check(store.CanRequestControlIntegration("Independent1"), "independent grouping does not wait for another unrelated outcome");
        Authorize("Independent1");
        Deliver("Independent1", "repair-run", "independent-part", AutomationDecision.Accept);
        Refused<InvalidOperationException>(() => store.MarkIntegrated("independent-part", 1, head, Cmd(), 26),
            "controlled integration cannot mark an older accepted revision after a replacement");
        Authorize("Independent1"); store.MarkIntegrated("independent-part", 2, head, Cmd(), 26);

        Define("Wrong1"); Define("Wrong2");
        var wrong = Group("wrong-baseline", ControlRelation.Competitive, "Wrong1", "Wrong2");
        store.ApplyControlRelation(wrong with { Group = wrong.Group! with { Baseline = new string('a', 40) } }, 27);
        Approve("Wrong1"); Approve("Wrong2");
        await RefusedAsync(() => consumer.QueueAsync(Binding("Wrong1"), 28), "competitive binding rejects a different shared baseline before dispatch");

        Define("Cycle1", deps: []); Define("Cycle2", deps: ["Cycle1"]);
        var before = Revision(); var events = store.ListEventsAfter(0).Count;
        Refused<ArgumentException>(() => store.ApplyControlRelation(Group("cyclic-pipeline", ControlRelation.Pipeline, "Cycle2", "Cycle1"), 29),
            "pipeline cycle rolls back instead of creating a partial relation");
        check(Revision() == before && store.ListEventsAfter(0).Count == events && !store.ReadControlGroups().Any(g => g.Spec.Id == "cyclic-pipeline"),
            "cycle refusal leaves no group, plan version, receipt or event");

        Define("Rollback1"); Define("Rollback2");
        using (var c = new SqliteConnection("Data Source=" + db))
        {
            c.Open(); using var q = c.CreateCommand();
            q.CommandText = "CREATE TRIGGER relation_abort BEFORE INSERT ON control_receipts BEGIN SELECT RAISE(ABORT,'late relation failure'); END";
            q.ExecuteNonQuery();
        }
        before = Revision(); events = store.ListEventsAfter(0).Count;
        var rollback = Group("rollback", ControlRelation.Pipeline, "Rollback1", "Rollback2");
        Refused<SqliteException>(() => store.ApplyControlRelation(rollback, 30), "late receipt failure aborts the full relation transaction");
        check(Revision() == before && store.ListEventsAfter(0).Count == events &&
            store.ReadControlSnapshot().Packages.Single(p => p.Spec.Id == "Rollback2").Version == 1 &&
            !store.ReadControlGroups().Any(g => g.Spec.Id == "rollback"),
            "late failure leaves neither group nor pipeline edge nor orphan event");
        using (var c = new SqliteConnection("Data Source=" + db))
        {
            c.Open(); using var q = c.CreateCommand(); q.CommandText = "DROP TRIGGER relation_abort"; q.ExecuteNonQuery();
        }
        var cliFile = Path.Combine(root, "relation command 关系.json");
        File.WriteAllText(cliFile, JsonSerializer.Serialize(Group("cli-group", ControlRelation.Independent, "Rollback1", "Rollback2")));
        var cliReceipt = await ArchiveChecks.Cli("control", "relation", db, cliFile);
        check(cliReceipt.Exit == 0 && JsonDocument.Parse(cliReceipt.Output).RootElement.GetProperty("groupId").GetString() == "cli-group",
            "actual CLI relation client writes the same versioned store with a Unicode command path");
        var cliGroups = await ArchiveChecks.Cli("control", "groups", db);
        check(cliGroups.Exit == 0 && JsonDocument.Parse(cliGroups.Output).RootElement.EnumerateArray()
            .Any(g => g.GetProperty("spec").GetProperty("id").GetString() == "cli-group"),
            "another CLI process reads the persisted relation and current outcome evidence");
        using var reopened = new AutomationStore(db);
        var shadow = reopened.ExportControlShadow();
        check(shadow.Groups.Single(g => g.Spec.Id == "competition").Winner == "B" &&
            shadow.Groups.Single(g => g.Spec.Id == "competition").SelectionCurrent && shadow.LegacyHistory.Count == 0,
            "reopen and coherent export preserve real selection separately from legacy claims");
        await host.StopAsync();
    }

    private static async Task<string> Git(string cwd, params string[] args)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = cwd, UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(); if (process.ExitCode != 0) throw new Exception(await error); return await output;
    }
}
