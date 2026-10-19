using System.Diagnostics;
using System.Text.Json;
using Fufucode.Core.Automation;
using Fufucode.Core.Automation.Harnesses;
using Fufucode.Core.Automation.Hosting;
using Microsoft.Data.Sqlite;

internal static class RestructureChecks
{
    public static async Task Run(string root, Action<bool, string> check)
    {
        var git = Path.Combine(root, "successor 工作区"); Directory.CreateDirectory(git);
        await Git(git, "init", "-q"); await Git(git, "config", "user.name", "Fixture");
        await Git(git, "config", "user.email", "fixture@example.invalid");
        await File.WriteAllTextAsync(Path.Combine(git, "a.txt"), "fixture\n");
        await Git(git, "add", "a.txt"); await Git(git, "commit", "-q", "-m", "fixture");
        var head = (await Git(git, "rev-parse", "HEAD")).Trim();
        var db = Path.Combine(root, "restructure.db");
        using var store = new AutomationStore(db); var queue = new HostJournal(store); var consumer = new ControlIntentConsumer(store);
        var serial = 0; string Id() => "restructure-" + ++serial;
        long Revision() => store.ReadControlSnapshot().Revision;
        WorkPackageVersion Package(string id) => store.ReadControlSnapshot().Packages.Single(p => p.Spec.Id == id);
        WorkPackageSpec Spec(string id, string[]? deps = null) => new(id, id, "Preserve the original fixture requirement", ["a.txt"],
            ["Return bounded evidence"], "codex", "devin/swe-2", "code", deps ?? []);
        void Define(string id, string[]? deps = null, long version = 0) => store.ApplyControlCommand(new(Id(), Revision(), "human",
            ControlAction.DefinePackage, Spec(id, deps), ExpectedPackageVersion: version), 1);
        void Approve(string id) => store.ApplyControlCommand(new(Id(), Revision(), "human", ControlAction.ApprovePackage,
            PackageId: id, ExpectedPackageVersion: Package(id).Version), 2);
        ControlRestructureSpec Plan(string[] sources, params WorkPackageSpec[] replacements) => new(
            sources.ToDictionary(id => id, id => Package(id).Version), replacements,
            sources.Select(id => new ControlGoalMapping(id, replacements.Select(p => p.Id).ToArray(), "Original goal carried by these successor contracts")).ToArray(),
            [], [], "Explicit bounded successor allocation");
        ControlRestructureReceipt Propose(ControlRestructureSpec spec) => store.ApplyControlRestructure(new(Id(), Revision(), "M", ControlRestructureAction.Propose, spec), 3);
        ControlRestructureCommand Decision(string id) => new(Id(), Revision(), "human", ControlRestructureAction.Approve, ProposalId: id);
        void Refuse<T>(Action action, string label) where T : Exception
        { try { action(); } catch (T) { check(true, label); return; } throw new Exception("FAIL " + label); }
        async Task RefuseAsync(Func<Task> action, string label)
        { try { await action(); } catch (InvalidOperationException) { check(true, label); return; } throw new Exception("FAIL " + label); }
        var executable = Environment.ProcessPath!;
        HarnessRuntime Runtime(string mode, string marker, string release)
        {
            var args = new List<string>();
            if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) args.Add(typeof(RestructureChecks).Assembly.Location);
            args.AddRange(["--adoption-protocol", mode, marker, release]);
            return new("fixture", "codex", executable, args.ToArray(), "fixture-v1");
        }
        ControlExecutionBinding Binding(string id, HarnessRuntime runtime)
        {
            var suffix = Id();
            store.CreateThread("thread-" + suffix, "fixture", "codex", "devin/swe-2", git, null, 4);
            store.CreateTask("task-" + suffix, "thread-" + suffix, id, head, 4);
            return new(Id(), Revision(), $"execute:{id}:{Package(id).Version}", Package(id).Version,
                "thread-" + suffix, "task-" + suffix, runtime);
        }
        async Task Wait(Func<bool> predicate)
        {
            var until = DateTime.UtcNow.AddSeconds(30);
            while (!predicate()) { if (DateTime.UtcNow > until) throw new TimeoutException("Successor fixture did not settle"); await Task.Delay(25); }
        }
        void Accept(ControlQueueReceipt run, string part) => store.RecordDecision(Id(), part, AutomationDecision.Accept,
            null, "{}", "human", Id(), 6, run.RunId, head, 1);
        void Deliver(ControlQueueReceipt run, string part)
        { store.CreatePart(part, run.TaskId, part, 5); store.RecordDelivery(Id(), part, run.RunId, head, "{}", Id(), 5); }
        void Authorize(string id)
        {
            var candidate = store.ReadControlIntegrationCandidate(id)!;
            store.ApplyControlIntegration(new(Id(), Revision(), "human", ControlIntegrationAction.Authorize,
                id, candidate.Package.Version, candidate.EvidenceToken), 6);
        }

        Define("A"); Define("B", ["A"]); Define("C", ["B"]); Define("unrelated");
        foreach (var id in new[] { "A", "B", "C", "unrelated" }) Approve(id);
        store.ApplyControlCommand(new(Id(), Revision(), "human", ControlAction.SetMode, Mode: DevelopmentMode.SemiAutomatic), 2);
        var oldMarker = Path.Combine(root, "successor-old-started");
        var oldBinding = Binding("A", Runtime("done", oldMarker, "unused"));
        var oldQueue = await consumer.QueueAsync(oldBinding, 4);
        var askId = Id();
        store.ApplyControlKnowledge(new(askId, Revision(), "human", ControlKnowledgeAction.Ask,
            Text: "Keep original requirement?", FromPackage: "A", ToPackage: "unrelated", ExpectedFromVersion: 1, ExpectedToVersion: 1), 4);
        var split = Plan(["A"], Spec("A1"), Spec("A2"));
        var originalSource = Package("A");
        Refuse<ArgumentException>(() => Propose(split with { GoalMappings = [] }), "successor proposal cannot silently drop source goals");
        Refuse<ArgumentException>(() => Propose(split with { GoalMappings = [new("A", ["absent"], "invalid")] }), "goal mapping cannot name a nonexistent successor");
        Refuse<InvalidOperationException>(() => Propose(Plan(["A"], Spec("A"))), "successors cannot overwrite original identities");
        Refuse<ArgumentException>(() => Propose(Plan(["A"], Spec("A1", ["A2"]), Spec("A2", ["A1"]))), "cyclic successor graph is rejected before proposal persistence");
        Refuse<ArgumentException>(() => Propose(Plan(["A"], Spec("A1", ["A"]))), "a replacement cannot depend on the superseded source");
        var before = Revision(); var proposal = Propose(split);
        check(proposal.Status == "pending" && Revision() == before + 1 && !Package("A").Superseded && queue.Get(oldQueue.RunId)!.State == HostRequestState.Queued,
            "proposing successors changes no contract, approval or real queued execution");
        var pending = store.ReadControlSnapshot().Restructures.Single();
        check(pending.ContractCurrent && pending.SourceContracts.Single().Spec.Goal == Package("A").Spec.Goal && pending.SourceContracts.Single().Spec.Acceptance.SequenceEqual(Package("A").Spec.Acceptance),
            "human decision reads immutable source goal and acceptance provenance");
        var decision = Decision(proposal.ProposalId); var cursor = store.ListEventsAfter(0).Count;
        using (var c = new SqliteConnection("Data Source=" + db))
        { c.Open(); using var q = c.CreateCommand(); q.CommandText = "CREATE TRIGGER restructure_abort BEFORE INSERT ON control_receipts WHEN NEW.command_id='" + decision.CommandId + "' BEGIN SELECT RAISE(ABORT,'injected failure'); END;"; q.ExecuteNonQuery(); }
        Refuse<SqliteException>(() => store.ApplyControlRestructure(decision, 7), "late successor receipt failure is reported");
        check(Revision() == decision.ExpectedRevision && store.ListEventsAfter(0).Count == cursor && !Package("A").Superseded && Package("B").Version == 1 &&
            !store.ReadControlSnapshot().Packages.Any(p => p.Spec.Id == "A1") && queue.Get(oldQueue.RunId)!.State == HostRequestState.Queued,
            "rollback restores retirement, dependent versions, successor rows, event and old queue together");
        using (var c = new SqliteConnection("Data Source=" + db))
        { c.Open(); using var q = c.CreateCommand(); q.CommandText = "DROP TRIGGER restructure_abort"; q.ExecuteNonQuery(); }
        var applied = store.ApplyControlRestructure(decision, 8);
        check(applied.Status == "approved" && store.ApplyControlRestructure(decision, 9) == applied && Package("B").Version == 2,
            "human restructure applies once and replay preserves the original receipt");
        Refuse<InvalidOperationException>(() => store.ApplyControlRestructure(decision with { DecidedBy = "other" }, 9), "same decision identity cannot change attribution");
        check(Package("A").Superseded && JsonSerializer.Serialize(store.GetWorkPackageVersion("A", 1).Spec) == JsonSerializer.Serialize(originalSource.Spec),
            "source identity remains readable as superseded history");
        check(!Package("A1").Approved && !Package("A2").Approved && Package("A1").Version == 1 && !store.CanIntegrateControlPackage("A1"),
            "restructure approval does not approve successor plans or inherit acceptance");
        check(Package("B") is { Version: 2, Approved: false } && Package("B").Spec.DependsOn.SequenceEqual(new[] { "A1", "A2" }) &&
            Package("C") is { Version: 2, Approved: false } && store.GetWorkPackageVersion("B", 1).Spec.DependsOn.SequenceEqual(new[] { "A" }),
            "direct and transitive dependent contracts require fresh approval while original edges stay historical");
        check(Package("unrelated") is { Version: 1, Approved: true } && queue.Get(oldQueue.RunId)!.State == HostRequestState.Abandoned && !File.Exists(oldMarker),
            "unrelated work stays approved and only the unstarted superseded queue is abandoned");
        check(!queue.MarkStarted(oldQueue.RunId, 9) && queue.Get(oldQueue.RunId)!.State == HostRequestState.Abandoned,
            "atomic queue start cannot execute a superseded source");
        Refuse<InvalidOperationException>(() => Approve("A"), "ordinary approval cannot revive a retired source");
        Refuse<InvalidOperationException>(() => Define("A", version: 1), "ordinary edit cannot reuse a retired source identity");
        await RefuseAsync(() => consumer.QueueAsync(oldBinding with { CommandId = Id(), ExpectedRevision = Revision() }, 9), "a fresh trusted binding cannot restart a superseded source intent");
        Refuse<ArgumentException>(() => Define("bad-dependency", ["A"]), "ordinary planning cannot attach a new dependency to retired work");
        check(!store.ReadControlSnapshot().Knowledge.Questions.Single(q => q.Id == askId).ContractCurrent,
            "a directed question becomes obsolete even though the retired source version number was preserved");
        Refuse<InvalidOperationException>(() => store.ApplyControlKnowledge(new(Id(), Revision(), "human", ControlKnowledgeAction.Answer,
            Text: "cannot answer old contract", QuestionId: askId, ExpectedFromVersion: 1, ExpectedToVersion: 1), 9), "an answer cannot revive a retired question contract");
        store.ApplyControlCommand(new(Id(), Revision(), "human", ControlAction.SetMode, Mode: DevelopmentMode.Manual), 9);
        store.ApplyControlCommand(new(Id(), Revision(), "human", ControlAction.SetMode, Mode: DevelopmentMode.SemiAutomatic), 9);
        check(store.ReadControlSnapshot().Intents.Where(i => i.PackageId == "A").All(i => i.Status == "superseded"), "mode switches never recreate or resume retired execution intents");
        Approve("A1");
        var marker = Path.Combine(root, "successor-new-started");
        var fresh = await consumer.QueueAsync(Binding("A1", Runtime("done", marker, "unused")), 10);
        check(fresh.RunId != oldQueue.RunId && fresh.TaskId != oldQueue.TaskId, "successor execution has a new task and durable run identity");
        await using var host = new BackgroundHost(new(db, Path.Combine(root, "restructure-host"), "restructure-fixture-host", Path.Combine(root, "restructure-endpoint")));
        check(await host.StartAsync(), "successor contract runs through the existing real background host");
        await Wait(() => queue.Get(fresh.RunId)!.State == HostRequestState.Settled);
        check(File.ReadAllLines(marker).Length == 1 && !File.Exists(oldMarker), "only the approved successor actually executes once");
        Deliver(fresh, "successor-part");
        var stale = Propose(Plan(["A1"], Spec("A1-next")));
        Accept(fresh, "successor-part");
        check(!store.ReadControlSnapshot().Restructures.Single(p => p.Id == stale.ProposalId).ContractCurrent,
            "actual U1 review changes make an earlier successor evidence package obsolete");
        Refuse<InvalidOperationException>(() => store.ApplyControlRestructure(Decision(stale.ProposalId), 11), "fresh control revision cannot approve a proposal against changed U1 evidence");
        Authorize("A1");
        var acceptedProposal = Propose(Plan(["A1"], Spec("A1-next")));
        store.ApplyControlRestructure(Decision(acceptedProposal.ProposalId), 11);
        check(store.ListParts(fresh.TaskId).Single().Status == DeliveryPartStatus.Accepted && !store.CanIntegrateControlPackage("A1") && !store.CanIntegrateControlPackage("A1-next"),
            "superseding an accepted source preserves its evidence without granting successor acceptance");
        Refuse<InvalidOperationException>(() => store.MarkIntegrated("successor-part", 1, head, Id(), 12), "direct U1 integration cannot bypass retired contract eligibility");
        Approve("A1-next");
        var next = await consumer.QueueAsync(Binding("A1-next", Runtime("done", Path.Combine(root, "successor-next-started"), "unused")), 12);
        await Wait(() => queue.Get(next.RunId)!.State == HostRequestState.Settled); Deliver(next, "successor-next-part"); Accept(next, "successor-next-part");
        Authorize("A1-next");
        store.MarkIntegrated("successor-next-part", 1, head, Id(), 12);
        Refuse<InvalidOperationException>(() => Propose(Plan(["A1-next"], Spec("cannot-erase-integration"))), "integrated work cannot be removed through successor restructuring");
        check(store.IsIntegrated("successor-next-part", 1), "rejected restructuring retains the real integration mark");

        Define("independent-source"); var independentProposal = Propose(Plan(["independent-source"], Spec("independent-next")));
        Define("unrelated", version: 1);
        store.ApplyControlRestructure(Decision(independentProposal.ProposalId), 13);
        check(Package("independent-source").Superseded, "unrelated work changes do not invalidate a pinned independent proposal");
        Define("changed-source"); var changedProposal = Propose(Plan(["changed-source"], Spec("changed-next")));
        Define("changed-source", version: 1);
        Refuse<InvalidOperationException>(() => store.ApplyControlRestructure(Decision(changedProposal.ProposalId), 13), "source version changes cannot be hidden behind a fresh control revision");
        var rejection = store.ApplyControlRestructure(new(Id(), Revision(), "human", ControlRestructureAction.Reject, ProposalId: changedProposal.ProposalId, Reason: "obsolete route"), 13);
        check(rejection.Status == "rejected" && !Package("changed-source").Superseded, "rejection records a reason without applying an obsolete restructure");

        Define("live"); Approve("live");
        var liveMarker = Path.Combine(root, "restructure-live-started"); var release = Path.Combine(root, "restructure-live-release");
        var live = await consumer.QueueAsync(Binding("live", Runtime("hold", liveMarker, release)), 14);
        await Wait(() => File.Exists(liveMarker)); var liveProposal = Propose(Plan(["live"], Spec("live-next")));
        check(store.ReadControlSnapshot().Restructures.Single(p => p.Id == liveProposal.ProposalId).BlockedReason == "waiting_stop:live", "a real live writer creates a visible stop requirement");
        var liveRevision = Revision();
        Refuse<InvalidOperationException>(() => store.ApplyControlRestructure(Decision(liveProposal.ProposalId), 14), "human approval cannot rewrite a live writer contract");
        check(Revision() == liveRevision && !Package("live").Superseded && store.GetThread(live.ThreadId).Model == "devin/swe-2" && host.Executor.Runs.Get(live.RunId).ThreadId == live.ThreadId, "blocked restructure leaves the original writer identity and contract unchanged");
        File.WriteAllText(release, "release"); await Wait(() => queue.Get(live.RunId)!.State == HostRequestState.Settled);
        store.ApplyControlRestructure(Decision(liveProposal.ProposalId), 15);
        check(Package("live").Superseded && !Package("live-next").Approved, "confirmed settlement permits restructuring but still requires successor plan approval");

        GroupChecks(root, head, check);
        KnowledgeChecks(root, check);
        await CliChecks(root, check);
        Define("uncertain"); Approve("uncertain");
        var uncertain = await consumer.QueueAsync(Binding("uncertain", Runtime("unknown", Path.Combine(root, "restructure-unknown-started"), "unused")), 16);
        await Wait(() => queue.Get(uncertain.RunId)!.State == HostRequestState.Settled);
        check(host.Executor.Runs.Get(uncertain.RunId).Status == HarnessRunStatus.Unknown, "a real process exit without terminal evidence remains Unknown");
        var unknownProposal = Propose(Plan(["uncertain"], Spec("uncertain-next")));
        check(store.ReadControlSnapshot().Restructures.Single(p => p.Id == unknownProposal.ProposalId).BlockedReason == "waiting_reconciliation:uncertain", "settled transport without execution certainty requires ownership reconciliation");
        Refuse<InvalidOperationException>(() => store.ApplyControlRestructure(Decision(unknownProposal.ProposalId), 16), "unknown source cannot be bypassed by successor approval");
        await host.StopAsync();
        using var reopened = new AutomationStore(db);
        check(reopened.ReadControlSnapshot().Restructures.Single(p => p.Id == proposal.ProposalId).SourceContracts.Single().Spec.Goal == split.Replacements[0].Goal && reopened.GetWorkPackageVersion("B", 1).Spec.DependsOn.Single() == "A",
            "reopen preserves goal provenance, decisions and original dependency history in one U1 store");
        check(reopened.ExportControlShadow().Control.Restructures.Count == reopened.ReadControlSnapshot().Restructures.Count,
            "shadow export includes authoritative successor decision history");
        check((await Git(git, "rev-parse", "HEAD")).Trim() == head && File.ReadAllText(Path.Combine(git, "a.txt")) == "fixture\n", "successor approval and execution never modify the fixture repository");
    }

    private static void GroupChecks(string root, string head, Action<bool, string> check)
    {
        using var store = new AutomationStore(Path.Combine(root, "restructure-groups.db"));
        var serial = 0; string Id() => "group-rst-" + ++serial; long Revision() => store.ReadControlSnapshot().Revision;
        WorkPackageSpec Spec(string id, string[]? deps = null) => new(id, id, "Keep group goal", ["a.txt"], ["Show evidence"], "codex", "devin/swe-2", "code", deps ?? []);
        foreach (var id in new[] { "A", "B", "C", "D" }) store.ApplyControlCommand(new(Id(), Revision(), "human", ControlAction.DefinePackage, Spec(id), ExpectedPackageVersion: 0), 1);
        store.ApplyControlRelation(new(Id(), Revision(), "human", ControlRelationAction.DefineGroup, new("competition", ControlRelation.Competitive, ["A", "B"], head, ["quality"])), 1);
        store.ApplyControlRelation(new(Id(), Revision(), "human", ControlRelationAction.DefineGroup, new("independent", ControlRelation.Independent, ["C", "D"])), 1);
        var spec = new ControlRestructureSpec(new Dictionary<string, long> { ["A"] = 1, ["C"] = 1 }, [Spec("X")],
            [new("A", ["X"], "A goal retained"), new("C", ["X"], "C goal retained")],
            [new("new-complementary", ControlRelation.Complementary, ["X", "B", "D"])],
            [new("competition", ["new-complementary"], "Explicitly change competition into complementary work"), new("independent", ["new-complementary"], "Explicit cross-group merge")], "Explicit multi-group merge");
        ControlRestructureReceipt Propose(ControlRestructureSpec proposal) => store.ApplyControlRestructure(new(Id(), Revision(), "M", ControlRestructureAction.Propose, proposal), 2);
        void Refuse(Action action, string label)
        { try { action(); } catch (ArgumentException) { check(true, label); return; } catch (InvalidOperationException) { check(true, label); return; } throw new Exception("FAIL " + label); }
        Refuse(() => Propose(spec with { GroupMappings = [] }), "cross-group restructure cannot silently mix old relationships");
        Refuse(() => Propose(spec with { Groups = [new("new-complementary", ControlRelation.Complementary, ["X", "B"])] }), "replacement groups cannot drop an unaffected member");
        Refuse(() => Propose(spec with { GroupMappings = [new("competition", [], "drop"), new("independent", ["new-complementary"], "keep")] }), "a multi-member group cannot silently disappear");
        Refuse(() => Propose(spec with { Groups = [new("g1", ControlRelation.Competitive, ["X", "B"], head, ["quality"]), new("g2", ControlRelation.Competitive, ["X", "D"], head, ["quality"])],
            GroupMappings = [new("competition", ["g1"], "keep"), new("independent", ["g2"], "keep")] }), "successor relationship definitions still enforce one competition per package");
        var proposed = Propose(spec); store.ApplyControlRestructure(new(Id(), Revision(), "human", ControlRestructureAction.Approve, ProposalId: proposed.ProposalId), 3);
        var groups = store.ReadControlGroups();
        check(groups.Count == 3 && groups.Count(g => g.Superseded) == 2 && groups.Single(g => !g.Superseded).Spec.Members.SequenceEqual(new[] { "X", "B", "D" }),
            "explicit cross-group merge creates a fresh relationship while both old groups remain historical");
        check(groups.Single(g => g.Spec.Id == "competition").Spec.Criteria!.Single() == "quality" && groups.All(g => g.Winner is null), "restructure preserves original criteria and creates no fictional winner");
        var pipeline = new ControlRestructureSpec(new Dictionary<string, long> { ["X"] = 1, ["B"] = 1, ["D"] = 1 }, [Spec("P1"), Spec("P2", ["P1"])],
            [new("X", ["P1", "P2"], "goal retained"), new("B", ["P1"], "goal retained"), new("D", ["P2"], "goal retained")],
            [new("new-pipeline", ControlRelation.Pipeline, ["P1", "P2"])], [new("new-complementary", ["new-pipeline"], "Explicit pipeline replacement")], "Explicit sequence");
        Refuse(() => Propose(pipeline with { Replacements = [Spec("P1"), Spec("P2")] }), "pipeline replacement must declare its actual dependency edges");
        var sequenced = Propose(pipeline); store.ApplyControlRestructure(new(Id(), Revision(), "human", ControlRestructureAction.Approve, ProposalId: sequenced.ProposalId), 4);
        check(store.ReadControlGroups().Single(g => !g.Superseded).Spec.Relation == ControlRelation.Pipeline &&
            store.ReadControlSnapshot().Packages.Single(p => p.Spec.Id == "P2").Spec.DependsOn.Single() == "P1", "successor pipeline uses only the explicitly approved contracts and relationship");
        Refuse(() => store.ApplyControlRelation(new(Id(), Revision(), "human", ControlRelationAction.SelectWinner,
            GroupId: "competition", Winner: "A", ExpectedEvidenceToken: "old"), 4), "a historical competition cannot accept a new selection");
    }

    private static void KnowledgeChecks(string root, Action<bool, string> check)
    {
        using var store = new AutomationStore(Path.Combine(root, "restructure-knowledge.db"));
        var serial = 0; string Id() => "knowledge-rst-" + ++serial; long Revision() => store.ReadControlSnapshot().Revision;
        void Publish(long previous)
        {
            var proposal = Id();
            store.ApplyControlKnowledge(new(proposal, Revision(), "M", ControlKnowledgeAction.Propose, "policy", "Policy version " + (previous + 1), "fixture", previous), 1);
            store.ApplyControlKnowledge(new(Id(), Revision(), "human", ControlKnowledgeAction.Publish, ExpectedKnowledgeVersion: previous, ProposalId: proposal), 1);
        }
        WorkPackageSpec Spec(string id) => new(id, id, "Keep policy goal", ["a.txt"], ["Show evidence"], "codex", "devin/swe-2", "code", []) { KnowledgeRefs = ["policy"] };
        Publish(0);
        store.ApplyControlCommand(new(Id(), Revision(), "human", ControlAction.DefinePackage, Spec("K"), ExpectedPackageVersion: 0), 1);
        store.ApplyControlCommand(new(Id(), Revision(), "human", ControlAction.ApprovePackage, PackageId: "K", ExpectedPackageVersion: 1), 1);
        var spec = new ControlRestructureSpec(new Dictionary<string, long> { ["K"] = 1 }, [Spec("K-next")], [new("K", ["K-next"], "Keep declared policy goal")], [], [], "Explicit policy allocation");
        var stale = store.ApplyControlRestructure(new(Id(), Revision(), "M", ControlRestructureAction.Propose, spec), 2);
        Publish(1);
        check(!store.ReadControlSnapshot().Restructures.Single(p => p.Id == stale.ProposalId).ContractCurrent,
            "new relevant published knowledge invalidates a pinned restructure decision package");
        try
        {
            store.ApplyControlRestructure(new(Id(), Revision(), "human", ControlRestructureAction.Approve, ProposalId: stale.ProposalId), 3);
            throw new Exception("FAIL stale policy successor approval");
        }
        catch (InvalidOperationException) { check(true, "fresh control revision cannot conceal the changed relevant policy"); }
        var fresh = store.ApplyControlRestructure(new(Id(), Revision(), "M", ControlRestructureAction.Propose, spec), 3);
        store.ApplyControlRestructure(new(Id(), Revision(), "human", ControlRestructureAction.Approve, ProposalId: fresh.ProposalId), 3);
        check(store.GetWorkPackageVersion("K", 1).AdoptedKnowledge["policy"] == 1 && !store.ReadControlSnapshot().Knowledge.Adoptions.Any(a => a.PackageId == "K" && a.Status == "pending"),
            "retired knowledge bindings remain immutable and their pending adoption is superseded");
        check(store.ReadControlSnapshot().Packages.Single(p => p.Spec.Id == "K-next").AdoptedKnowledge.Count == 0,
            "restructure does not silently approve or adopt facts into the successor");
        store.ApplyControlCommand(new(Id(), Revision(), "human", ControlAction.ApprovePackage, PackageId: "K-next", ExpectedPackageVersion: 1), 4);
        check(store.ReadControlSnapshot().Packages.Single(p => p.Spec.Id == "K-next").AdoptedKnowledge["policy"] == 2,
            "explicit successor plan approval binds the current declared policy version");
        Publish(2);
        check(store.ReadControlSnapshot().Knowledge.Adoptions.Count(a => a.Status == "pending") == 1 &&
            store.ReadControlSnapshot().Knowledge.Adoptions.Single(a => a.Status == "pending").PackageId == "K-next",
            "later publication targets the active successor and never broadcasts back to retired work");
    }

    private static async Task CliChecks(string root, Action<bool, string> check)
    {
        var db = Path.Combine(root, "restructure-cli.db");
        using (var store = new AutomationStore(db))
            store.ApplyControlCommand(new("define", 0, "human", ControlAction.DefinePackage,
                new("old", "old", "原目标", ["a.txt"], ["Show evidence"], "codex", "devin/swe-2", "code", []), ExpectedPackageVersion: 0), 1);
        var replacement = new WorkPackageSpec("new", "new", "原目标继续", ["a.txt"], ["Show evidence"], "codex", "devin/swe-2", "code", []);
        var spec = new ControlRestructureSpec(new Dictionary<string, long> { ["old"] = 1 }, [replacement], [new("old", ["new"], "保留原目标与证据")], [], [], "CLI真实迁移");
        var file = Path.Combine(root, "后继 command.json");
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(new ControlRestructureCommand("proposal", 1, "M", ControlRestructureAction.Propose, spec)));
        check((await Cli("control", "restructure", db, file)).Exit == 0, "actual CLI persists the successor proposal through the common store");
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(new ControlRestructureCommand("approve", 2, "human", ControlRestructureAction.Approve, ProposalId: "proposal")));
        check((await Cli("control", "restructure", db, file)).Exit == 0, "separate CLI invocation approves the explicit successor proposal");
        var snapshot = await Cli("control", "snapshot", db); using var json = JsonDocument.Parse(snapshot.Text);
        check(snapshot.Exit == 0 && json.RootElement.GetProperty("restructures")[0].GetProperty("status").GetString() == "approved" &&
            json.RootElement.GetProperty("packages").EnumerateArray().Single(p => p.GetProperty("spec").GetProperty("id").GetString() == "old").GetProperty("superseded").GetBoolean() &&
            json.RootElement.GetProperty("intents").GetArrayLength() == 0, "actual CLI snapshot reports same-store retirement and Manual mode creates no execution intent");
    }

    private static async Task<(int Exit, string Text)> Cli(params string[] args)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        start.Environment["FUFUCODE_DATA_ROOT"] = ArchiveChecks.CliDataRoot;
        start.ArgumentList.Add(typeof(Fufucode.Cli.CliOptions).Assembly.Location); foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!; var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); var text = await output; var detail = await error;
        if (process.ExitCode != 0) throw new Exception(detail); return (process.ExitCode, text);
    }

    private static async Task<string> Git(string directory, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!; var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(); var text = await output; var detail = await error;
        if (process.ExitCode != 0) throw new Exception(detail); return text;
    }
}
