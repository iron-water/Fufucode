using System.Diagnostics;
using System.Text.Json;
using Fufucode.Core.Automation;
using Fufucode.Core.Automation.Harnesses;
using Fufucode.Core.Automation.Hosting;
using Microsoft.Data.Sqlite;

internal static class IntegrationChecks
{
    public static async Task Run(string root, Action<bool, string> check)
    {
        var directory = Path.Combine(root, "integration 审批"); Directory.CreateDirectory(directory);
        await Git(directory, "init", "-q"); await Git(directory, "config", "user.name", "Fixture");
        await Git(directory, "config", "user.email", "fixture@example.invalid");
        File.WriteAllText(Path.Combine(directory, "a.txt"), "fixture\n");
        await Git(directory, "add", "a.txt"); await Git(directory, "commit", "-q", "-m", "fixture");
        var head = (await Git(directory, "rev-parse", "HEAD")).Trim();
        var db = Path.Combine(root, "integration.db");
        using var store = new AutomationStore(db); var queue = new HostJournal(store); var consumer = new ControlIntentConsumer(store);
        var sequence = 0; string Id() => "integration-" + ++sequence;
        long Revision() => store.ReadControlSnapshot().Revision;
        WorkPackageVersion Package(string id) => store.ReadControlSnapshot().Packages.Single(p => p.Spec.Id == id);
        WorkPackageSpec Spec(string id, string artifact = "code") => new(id, id, "Keep reviewed fixture goal", ["a.txt"], ["Return real evidence"], "codex", "devin/swe-2", artifact, []);
        void Define(string id, string artifact = "code", long previous = 0) => store.ApplyControlCommand(new(Id(), Revision(), "human", ControlAction.DefinePackage, Spec(id, artifact), ExpectedPackageVersion: previous), 1);
        void Approve(string id) => store.ApplyControlCommand(new(Id(), Revision(), "human", ControlAction.ApprovePackage, PackageId: id, ExpectedPackageVersion: Package(id).Version), 2);
        void Refuse<T>(Action action, string label) where T : Exception
        { try { action(); } catch (T) { check(true, label); return; } throw new Exception("FAIL " + label); }
        ControlIntegrationCommand Authorization(string id) => new(Id(), Revision(), "human", ControlIntegrationAction.Authorize,
            id, Package(id).Version, store.ReadControlIntegrationCandidate(id)!.EvidenceToken);
        ControlIntegrationReceipt Grant(string id) => store.ApplyControlIntegration(Authorization(id), 6);
        var marker = Path.Combine(root, "integration-started.txt");
        var prefix = new List<string>(); var executable = Environment.ProcessPath!;
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) prefix.Add(typeof(IntegrationChecks).Assembly.Location);
        prefix.AddRange(["--adoption-protocol", "done", marker, "unused"]);
        var runtime = new HarnessRuntime("fixture", "codex", executable, prefix.ToArray(), "fixture-v1");
        await using var host = new BackgroundHost(new(db, Path.Combine(root, "integration-host"), "integration-fixture-host", Path.Combine(root, "integration-endpoint")));
        async Task<ControlQueueReceipt> Execute(string id)
        {
            var suffix = Id();
            store.CreateThread("thread-" + suffix, "fixture", "codex", "devin/swe-2", directory, null, 3);
            store.CreateTask("task-" + suffix, "thread-" + suffix, id, head, 3);
            var queued = await consumer.QueueAsync(new(Id(), Revision(), $"execute:{id}:{Package(id).Version}", Package(id).Version,
                "thread-" + suffix, "task-" + suffix, runtime), 3);
            var until = DateTime.UtcNow.AddSeconds(30);
            while (queue.Get(queued.RunId)!.State != HostRequestState.Settled)
            { if (DateTime.UtcNow > until) throw new TimeoutException("Integration fixture did not settle"); await Task.Delay(25); }
            if (host.Executor.Runs.Get(queued.RunId).Status != HarnessRunStatus.Completed) throw new Exception("Integration fixture did not complete");
            return queued;
        }
        void Deliver(ControlQueueReceipt run, string part, bool accept = true)
        {
            store.CreatePart(part, run.TaskId, part, 4);
            store.RecordDelivery(Id(), part, run.RunId, head, "{\"note\":\"accept integrated rework are opaque words\",\"evidence\":\"fixture\"}", Id(), 4);
            if (accept) Accept(run, part);
        }
        void Accept(ControlQueueReceipt run, string part) => store.RecordDecision(Id(), part, AutomationDecision.Accept,
            null, "{\"checks\":\"fixture\"}", "human", Id(), 5, run.RunId, head, 1);
        void Group(string id, ControlRelation relation, params string[] members) => store.ApplyControlRelation(new(Id(), Revision(), "human",
            ControlRelationAction.DefineGroup, new(id, relation, members, relation == ControlRelation.Competitive ? head : null,
                relation == ControlRelation.Competitive ? ["correctness"] : null)), 1);

        var empty = store.ReadControlInbox();
        check(empty.Mode == DevelopmentMode.Manual && empty.Items.Count == 0 && empty.Revision == 0 && empty.EventCursor == 0,
            "empty human inbox is a read-only Manual view of the sole U1 store");
        Define("A"); Define("B"); Define("draft"); Define("Code"); Define("Proof", "evidence"); Define("Candidate1"); Define("Candidate2");
        Group("independent", ControlRelation.Independent, "A", "B"); Group("bundle", ControlRelation.Complementary, "Code", "Proof");
        Group("competition", ControlRelation.Competitive, "Candidate1", "Candidate2");
        var proposalId = Id();
        store.ApplyControlKnowledge(new(proposalId, Revision(), "M", ControlKnowledgeAction.Propose, "policy", "保留原始来源", "fixture", 0), 1);
        var questionId = Id();
        store.ApplyControlKnowledge(new(questionId, Revision(), "human", ControlKnowledgeAction.Ask, Text: "Keep goal?", FromPackage: "A", ToPackage: "B", ExpectedFromVersion: 1, ExpectedToVersion: 1), 1);
        var restructureId = Id();
        store.ApplyControlRestructure(new(restructureId, Revision(), "M", ControlRestructureAction.Propose,
            new(new Dictionary<string, long> { ["draft"] = 1 }, [Spec("draft-next")], [new("draft", ["draft-next"], "goal retained")], [], [], "bounded reassignment")), 1);
        var firstInbox = store.ReadControlInbox();
        check(firstInbox.Items.Count(i => i.Kind == "plan") == 7 && firstInbox.Items.Single(i => i.Kind == "knowledge").Options.SequenceEqual(new[] { "publish", "reject" }) &&
            firstInbox.Items.Single(i => i.Kind == "question").Options.Single() == "answer" && firstInbox.Items.Single(i => i.Kind == "restructure").Options.Contains("approve"),
            "one inbox collects versioned plans, knowledge, directed question and explicit restructure options");
        check(firstInbox.Items.Single(i => i.Kind == "choice").Options.Length == 0,
            "competition without accepted evidence offers no actionable selection");
        var revisionBeforeRead = Revision(); var eventsBeforeRead = store.ListEventsAfter(0).Count;
        _ = store.ReadControlInbox(); _ = store.ReadControlInbox();
        check(Revision() == revisionBeforeRead && store.ListEventsAfter(0).Count == eventsBeforeRead && queue.List().Count == 0,
            "repeated inbox reads create no decision, receipt, model run or queue work");
        foreach (var id in new[] { "A", "B", "Code", "Proof", "Candidate1", "Candidate2" }) Approve(id);
        store.ApplyControlCommand(new(Id(), Revision(), "human", ControlAction.SetMode, Mode: DevelopmentMode.SemiAutomatic), 2);
        check(await host.StartAsync(), "integration authorization exercises the actual persistent host and protocol subprocess");
        var runA = await Execute("A"); Deliver(runA, "a-1", false); Deliver(runA, "a-2", false);
        var reviews = store.ReadControlInbox().Items.Where(i => i.Kind == "review").ToArray();
        check(reviews.Length == 2 && reviews.All(i => i.Actionable && i.Options.SequenceEqual(new[] { "accept", "rework", "blocked", "defer" })) &&
            store.GetPart("a-1").Status == DeliveryPartStatus.Delivered,
            "opaque card keywords do not decide real parts; stopped delivered parts offer only explicit review actions");
        check(reviews.All(i => i.ContextJson.Contains(runA.RunId) && i.ContextJson.Contains(head) && i.ContextJson.Contains("Keep reviewed fixture goal")),
            "review decision context contains actual run, HEAD, goal and accepted-contract version");
        Refuse<InvalidOperationException>(() => store.ApplyControlIntegration(Authorization("A"), 6), "unreviewed real delivery cannot receive an integration authorization");
        Accept(runA, "a-1"); Accept(runA, "a-2");
        var integration = store.ReadControlInbox().Items.Single(i => i.Kind == "integration" && i.ObjectId == "A");
        check(integration.Actionable && integration.Options.Single() == "authorize" && !store.ReadControlInbox().Items.Any(i => i.Kind == "review" && i.ObjectId.StartsWith("a-")),
            "acceptance removes review items and offers a separate human integration decision");
        using (var decisionContext = JsonDocument.Parse(integration.ContextJson))
            check(decisionContext.RootElement.GetProperty("parts").EnumerateArray().All(p => p.GetProperty("Reviewer").GetString() == "human" &&
                p.GetProperty("ReviewEvidenceJson").GetString()!.Contains("checks") && p.GetProperty("ReviewId").GetString() is not null),
                "integration decision carries each exact current review identity, actor and validation evidence");
        check(store.CanRequestControlIntegration("A") && !store.CanIntegrateControlPackage("A"), "accepted evidence is eligible to request authorization but is not already authorized");
        Refuse<InvalidOperationException>(() => store.MarkIntegrated("a-1", 1, head, Id(), 6), "direct U1 integration refuses accepted evidence without a human grant");
        Refuse<InvalidOperationException>(() => store.ApplyControlIntegration(Authorization("A") with { ExpectedPackageVersion = 2 }, 6),
            "integration authorization cannot name a different contract version");
        Refuse<InvalidOperationException>(() => store.ApplyControlIntegration(Authorization("A") with { ExpectedEvidenceToken = "invented" }, 6),
            "human input cannot substitute an invented integration evidence token");
        var failing = Authorization("A"); var before = Revision(); var cursor = store.ListEventsAfter(0).Count;
        using (var c = new SqliteConnection("Data Source=" + db))
        { c.Open(); using var q = c.CreateCommand(); q.CommandText = "CREATE TRIGGER integration_abort BEFORE INSERT ON control_receipts WHEN NEW.command_id='" + failing.CommandId + "' BEGIN SELECT RAISE(ABORT,'injected failure'); END;"; q.ExecuteNonQuery(); }
        Refuse<SqliteException>(() => store.ApplyControlIntegration(failing, 6), "late grant persistence failure is reported");
        check(Revision() == before && store.ListEventsAfter(0).Count == cursor && store.ReadControlSnapshot().IntegrationAuthorizations.Count == 0 && !store.CanIntegrateControlPackage("A"),
            "authorization, revision, event and receipt roll back together on late failure");
        using (var c = new SqliteConnection("Data Source=" + db))
        { c.Open(); using var q = c.CreateCommand(); q.CommandText = "DROP TRIGGER integration_abort"; q.ExecuteNonQuery(); }
        var grantA = store.ApplyControlIntegration(failing, 7);
        check(store.ApplyControlIntegration(failing, 8) == grantA && store.ReadControlSnapshot().IntegrationAuthorizations.Count == 1,
            "authorization replay returns one immutable grant and creates no extra decision");
        Refuse<InvalidOperationException>(() => store.ApplyControlIntegration(failing with { DecidedBy = "another" }, 8), "grant identity cannot change actor on replay");
        Refuse<InvalidOperationException>(() => Grant("A"), "a new command cannot duplicate the exact active authorization");
        check(store.CanIntegrateControlPackage("A") && !store.IsIntegrated("a-1", 1) && !store.IsIntegrated("a-2", 1),
            "authorizing integration creates no mark or Git mutation");
        Define("B", previous: 1);
        check(store.CanIntegrateControlPackage("A"), "an independent peer contract change must not invalidate this accepted authorized outcome");
        check(store.ReadControlInbox().Items.Single(i => i.Kind == "question").Options.Length == 0,
            "obsolete directed questions remain evidence but offer no answer action");
        store.MarkIntegrated("a-1", 1, head, Id(), 8);
        check(store.CanIntegrateControlPackage("A"), "one human authorization remains valid while its multipart integration progresses");
        var revoke = new ControlIntegrationCommand(Id(), Revision(), "human", ControlIntegrationAction.Revoke, "A", 1, failing.ExpectedEvidenceToken,
            grantA.AuthorizationId, "Stop the remaining integration");
        Refuse<InvalidOperationException>(() => store.ApplyControlIntegration(revoke with { ExpectedEvidenceToken = "another-snapshot" }, 8),
            "revocation must identify the exact recorded authorization snapshot");
        Refuse<ArgumentException>(() => store.ApplyControlIntegration(revoke with { Reason = null }, 8),
            "revocation requires an explicit recorded reason");
        using (var c = new SqliteConnection("Data Source=" + db))
        { c.Open(); using var q = c.CreateCommand(); q.CommandText = "CREATE TRIGGER revoke_abort BEFORE INSERT ON control_receipts WHEN NEW.command_id='" + revoke.CommandId + "' BEGIN SELECT RAISE(ABORT,'injected failure'); END;"; q.ExecuteNonQuery(); }
        var revokeRevision = Revision(); var revokeEvents = store.ListEventsAfter(0).Count;
        Refuse<SqliteException>(() => store.ApplyControlIntegration(revoke, 8), "late revocation persistence failure is reported");
        check(Revision() == revokeRevision && store.ListEventsAfter(0).Count == revokeEvents && store.CanIntegrateControlPackage("A") && store.IsIntegrated("a-1", 1),
            "failed revocation restores active permission and preserves preexisting integration history");
        using (var c = new SqliteConnection("Data Source=" + db))
        { c.Open(); using var q = c.CreateCommand(); q.CommandText = "DROP TRIGGER revoke_abort"; q.ExecuteNonQuery(); }
        store.ApplyControlIntegration(revoke, 8);
        check(!store.CanIntegrateControlPackage("A") && store.IsIntegrated("a-1", 1), "revocation blocks remaining parts without erasing an already recorded integration");
        Refuse<InvalidOperationException>(() => store.MarkIntegrated("a-2", 1, head, Id(), 8), "revoked authorization cannot integrate another part");
        Grant("A"); store.MarkIntegrated("a-2", 1, head, Id(), 9);
        check(store.ReadControlInbox().Items.All(i => !(i.Kind == "integration" && i.ObjectId == "A") && !(i.Kind == "authorization" && i.ContextJson.Contains("\"PackageId\":\"A\""))),
            "completed integration leaves immutable history rather than another pending human decision");

        var code = await Execute("Code"); Deliver(code, "code-part");
        check(!store.ReadControlInbox().Items.Single(i => i.Kind == "integration" && i.ObjectId == "Code").Actionable,
            "accepted complementary code cannot request authorization before required proof exists");
        var proof = await Execute("Proof"); Deliver(proof, "proof-part"); var codeCommand = Authorization("Code"); store.ApplyControlIntegration(codeCommand, 10);
        Deliver(proof, "proof-extra");
        check(store.CanRequestControlIntegration("Code") && !store.CanIntegrateControlPackage("Code"),
            "changed accepted proof expires the old grant even when the bundle is again eligible");
        Refuse<InvalidOperationException>(() => store.ApplyControlIntegration(codeCommand with { CommandId = Id(), ExpectedRevision = Revision() }, 11),
            "a fresh global revision cannot authorize a stale complementary evidence token");
        Refuse<InvalidOperationException>(() => store.MarkIntegrated("code-part", 1, head, Id(), 11), "direct integration rechecks complementary grant evidence at the same transaction boundary");
        Grant("Code"); store.MarkIntegrated("code-part", 1, head, Id(), 11);

        var candidate = await Execute("Candidate1"); Deliver(candidate, "candidate-part");
        check(store.ReadControlInbox().Items.Single(i => i.Kind == "choice").Actionable && !store.CanRequestControlIntegration("Candidate1"),
            "accepted competitor offers selection before it can request a separate integration authorization");
        Refuse<InvalidOperationException>(() => Grant("Candidate1"), "integration authorization cannot bypass competition selection");
        void Choose() => store.ApplyControlRelation(new(Id(), Revision(), "human", ControlRelationAction.SelectWinner,
            GroupId: "competition", Winner: "Candidate1", ExpectedEvidenceToken: store.ReadControlGroups().Single(g => g.Spec.Id == "competition").Members.Single(m => m.PackageId == "Candidate1").EvidenceToken), 12);
        Choose(); Grant("Candidate1"); Define("Candidate2", previous: 1);
        check(!store.CanIntegrateControlPackage("Candidate1"), "competitive peer version changes invalidate the selection and integration authorization");
        Choose(); Grant("Candidate1"); store.MarkIntegrated("candidate-part", 1, head, Id(), 12);
        check(store.ReadControlInbox().Items.All(i => !(i.Kind == "integration" && i.ObjectId == "Candidate1")), "integrated winner requires no further integration approval");

        var obsoleteProposal = Id();
        store.ApplyControlKnowledge(new(obsoleteProposal, Revision(), "M", ControlKnowledgeAction.Propose,
            "policy", "Competing stale proposal", "fixture", 0), 13);
        store.ApplyControlKnowledge(new(Id(), Revision(), "human", ControlKnowledgeAction.Publish, ProposalId: proposalId, ExpectedKnowledgeVersion: 0), 13);
        check(store.ReadControlInbox().Items.Single(i => i.Kind == "knowledge" && i.ObjectId == obsoleteProposal).Options.SequenceEqual(new[] { "reject" }),
            "obsolete fact proposals offer rejection but cannot publish over a new fact version");
        store.ApplyControlCommand(new(Id(), Revision(), "human", ControlAction.DefinePackage, Spec("Knowledge") with { KnowledgeRefs = ["policy"] }, ExpectedPackageVersion: 0), 13);
        Approve("Knowledge"); var knowledgeRun = await Execute("Knowledge"); Deliver(knowledgeRun, "knowledge-part"); var knowledgeGrant = Grant("Knowledge");
        using (var knowledgeContext = JsonDocument.Parse(store.ReadControlIntegrationCandidate("Knowledge")!.ContextJson))
            check(knowledgeContext.RootElement.GetProperty("boundKnowledge").GetString()!.Contains("保留原始来源"),
                "integration decision carries the exact bound public fact text and source");
        var nextFact = Id();
        store.ApplyControlKnowledge(new(nextFact, Revision(), "M", ControlKnowledgeAction.Propose, "policy", "Updated policy", "fixture-v2", 1), 14);
        store.ApplyControlKnowledge(new(Id(), Revision(), "human", ControlKnowledgeAction.Publish, ProposalId: nextFact, ExpectedKnowledgeVersion: 1), 14);
        check(!store.CanIntegrateControlPackage("Knowledge") && !store.ReadControlSnapshot().IntegrationAuthorizations.Single(a => a.Id == knowledgeGrant.AuthorizationId).ContractCurrent &&
            store.ReadControlInbox().Items.Single(i => i.Kind == "adoption" && i.ObjectId == "Knowledge").Options.Single() == "adopt",
            "published relevant knowledge invalidates old permission and offers a separate adoption decision");
        Refuse<InvalidOperationException>(() => store.MarkIntegrated("knowledge-part", 1, head, Id(), 14),
            "an existing grant cannot integrate accepted evidence after its knowledge contract changes");
        store.ApplyControlKnowledge(new(Id(), Revision(), "human", ControlKnowledgeAction.Adopt, Key: "policy", ExpectedKnowledgeVersion: 2)
            { PackageId = "Knowledge", ExpectedPackageVersion = 1 }, 14);
        check(!store.CanIntegrateControlPackage("Knowledge") && store.GetPart("knowledge-part").Status == DeliveryPartStatus.Accepted,
            "new adopted contract retains old accepted history without inheriting its authorization");
        var freshKnowledge = await Execute("Knowledge"); Deliver(freshKnowledge, "knowledge-next-part"); Grant("Knowledge");
        store.MarkIntegrated("knowledge-next-part", 1, head, Id(), 15);
        check(store.IsIntegrated("knowledge-next-part", 1) && !store.IsIntegrated("knowledge-part", 1),
            "only newly delivered and separately authorized evidence integrates after adoption");
        Define("Live"); Approve("Live");
        var livePrefix = prefix.ToArray(); var liveMarker = Path.Combine(root, "inbox-live-started"); var release = Path.Combine(root, "inbox-live-release");
        livePrefix[^3] = "hold"; livePrefix[^2] = liveMarker; livePrefix[^1] = release;
        var liveSuffix = Id();
        store.CreateThread("thread-" + liveSuffix, "fixture", "codex", "devin/swe-2", directory, null, 16);
        store.CreateTask("task-" + liveSuffix, "thread-" + liveSuffix, "Live", head, 16);
        var live = await consumer.QueueAsync(new(Id(), Revision(), "execute:Live:1", 1, "thread-" + liveSuffix, "task-" + liveSuffix,
            runtime with { PrefixArguments = livePrefix }), 16);
        var until = DateTime.UtcNow.AddSeconds(30);
        while (!File.Exists(liveMarker)) { if (DateTime.UtcNow > until) throw new TimeoutException("Inbox live fixture did not start"); await Task.Delay(25); }
        store.CreatePart("live-part", live.TaskId, "early metadata", 16);
        store.RecordDelivery(Id(), "live-part", live.RunId, head, "{\"text\":\"early-body-marker\"}", Id(), 16);
        try
        {
            var liveItem = store.ReadControlInbox().Items.Single(i => i.Kind == "review" && i.ObjectId == "live-part");
            check(!liveItem.Actionable && liveItem.Options.Length == 0 && !liveItem.ContextJson.Contains("early-body-marker"),
                "live-writer review projection exposes only identity/state and never its premature card body");
            Refuse<InvalidOperationException>(() => Accept(live, "live-part"), "direct U1 review cannot bypass the active-writer inbox fence");
            Refuse<InvalidOperationException>(() => store.ApplyControlReview(new(Id(), Revision(), "human", live.TaskId,
                "live-part", 1, live.RunId, head, liveItem.EvidenceToken, AutomationDecision.Accept, null, "{\"checks\":[\"fixture\"]}"), 16),
                "client review gateway cannot bypass a genuinely active writer");
        }
        finally { File.WriteAllText(release, "release"); }
        until = DateTime.UtcNow.AddSeconds(30);
        while (queue.Get(live.RunId)!.State != HostRequestState.Settled) { if (DateTime.UtcNow > until) throw new TimeoutException("Inbox live fixture did not settle"); await Task.Delay(25); }
        var settledItem = store.ReadControlInbox().Items.Single(i => i.Kind == "review" && i.ObjectId == "live-part");
        check(settledItem.Actionable && settledItem.ContextJson.Contains("early-body-marker"),
            "only confirmed settlement makes the registered review body available for an explicit decision");
        Accept(live, "live-part");
        check(store.GetPart("live-part").Status == DeliveryPartStatus.Accepted,
            "direct U1 review accepts the exact registered revision only after confirmed settlement");
        using var reopened = new AutomationStore(db);
        check(reopened.ReadControlSnapshot().IntegrationAuthorizations.Any(a => a.Status == "revoked" && a.RevokedBy == "human") &&
            reopened.ExportControlShadow().Control.IntegrationAuthorizations.Count == store.ReadControlSnapshot().IntegrationAuthorizations.Count,
            "reopen and shadow export retain authorization/revocation actors and original evidence snapshots in the sole store");
        var inbox = reopened.ReadControlInbox(); var beforeEvents = reopened.ListEventsAfter(0).Count;
        check(inbox.Revision == reopened.ReadControlSnapshot().Revision && inbox.EventCursor == beforeEvents,
            "human inbox binds control revision and shared U1 cursor in one read transaction");
        await CliChecks(root, db, check);
        Define("Review"); Approve("Review");
        var reviewRun = await Execute("Review");
        await ReviewChecks.Run(store, reviewRun, head, db, root, check);
        await ReconsiderChecks.Run(store, reviewRun, head, db, root, check);
        await host.StopAsync();
        check((await Git(directory, "rev-parse", "HEAD")).Trim() == head && File.ReadAllText(Path.Combine(directory, "a.txt")) == "fixture\n",
            "human authorization and revocation never rewrite the fixture repository");
    }

    private static async Task CliChecks(string root, string db, Action<bool, string> check)
    {
        var inbox = await Cli("control", "inbox", db); using var json = JsonDocument.Parse(inbox.Text);
        check(inbox.Exit == 0 && json.RootElement.GetProperty("items").EnumerateArray().Any(i => i.GetProperty("kind").GetString() == "plan"),
            "actual CLI reads the human decision inbox from authoritative U1 state");
        using var store = new AutomationStore(db);
        var authorization = store.ReadControlSnapshot().IntegrationAuthorizations.Single(a => a.PackageId == "Code" && a.ContractCurrent && a.Status == "authorized");
        var file = Path.Combine(root, "集成决定 command.json");
        var command = new ControlIntegrationCommand("cli-revoke", store.ReadControlSnapshot().Revision, "human-cli", ControlIntegrationAction.Revoke,
            authorization.PackageId, authorization.PackageVersion, authorization.EvidenceToken, authorization.Id, "Keep recorded marks, revoke future permission");
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(command));
        var result = await Cli("control", "integration", db, file); using var receipt = JsonDocument.Parse(result.Text);
        check(result.Exit == 0 && receipt.RootElement.GetProperty("status").GetString() == "revoked" && store.IsIntegrated("code-part", 1),
            "actual CLI applies a pinned human revocation without undoing real integration history");
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
