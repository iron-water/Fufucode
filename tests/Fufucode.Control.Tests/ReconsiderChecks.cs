using System.Diagnostics;
using System.Text.Json;
using Fufucode.Core.Automation;
using Microsoft.Data.Sqlite;

internal static class ReconsiderChecks
{
    public static async Task Run(AutomationStore store, ControlQueueReceipt run, string head,
        string db, string root, Action<bool, string> check)
    {
        var n = 0; string Id() => "reconsider-fixture-" + ++n;
        long Revision() => store.ReadControlSnapshot().Revision;
        void Refuse<T>(Action action, string label) where T : Exception
        { try { action(); } catch (T) { check(true, label); return; } throw new Exception("FAIL " + label); }
        void Sql(string sql)
        { using var c = new SqliteConnection("Data Source=" + db); c.Open(); using var q = c.CreateCommand(); q.CommandText = sql; q.ExecuteNonQuery(); }
        ControlReconsiderCommand Command(string suffix)
        {
            var part = "review-" + suffix; var item = store.ReadControlInbox().Items.Single(i => i.Kind == "reconsider" && i.ObjectId == part);
            return new(Id(), Revision(), "human-reconsider", run.TaskId, part, 1, run.RunId, head, item.EvidenceToken,
                store.ListDecisions(part).Last().DecisionId, "Blocking condition has been checked locally; reconsider original evidence");
        }
        // Keep an untouched historical blocked decision available for other independent fixtures.
        store.CreatePart("reconsider-blocked", run.TaskId, "Blocked review fixture", 27);
        store.RecordDelivery(Id(), "reconsider-blocked", run.RunId, head, "{\"checks\":\"fixture\"}", Id(), 27);
        store.RecordDecision(Id(), "reconsider-blocked", AutomationDecision.Blocked, "Wait for local check", "{}", "human", Id(), 27, run.RunId, head, 1);
        var initial = store.ListDecisions("review-defer").Single(); var delivery = store.ListRevisions("review-defer").Single();
        var command = Command("defer"); var before = Revision(); var events = store.ListEventsAfter(0).Count;
        check(!store.ReadControlInbox().Items.Any(i => i.Kind == "reconsider" && i.ObjectId is "review-accept" or "review-rework"),
            "accepted and repair-required parts offer no unchanged-evidence reconsider action");
        Refuse<ArgumentException>(() => store.ApplyControlReconsider(command with { Reason = " " }, 28), "reconsider requires an explicit new reason");
        foreach (var bad in new[] {
            command with { ExpectedRevision = before - 1 }, command with { ExpectedDecisionId = "invented" },
            command with { ExpectedRunId = "invented" }, command with { ExpectedHeadSha = new string('f', 40) },
            command with { ExpectedPartRevision = 2 }, command with { ExpectedEvidenceToken = "invented" },
            command with { TaskId = "wrong-task" }, command with { PartId = "review-accept" }, command with { PartId = "review-rework" } })
            Refuse<InvalidOperationException>(() => store.ApplyControlReconsider(bad, 28), "reconsider rejects stale or mismatched bound identity " + bad.CommandId);
        Sql("UPDATE harness_runs SET status='Unknown' WHERE run_id='" + run.RunId + "';");
        var unknown = store.ReadControlInbox().Items.Single(i => i.Kind == "reconsider" && i.ObjectId == command.PartId);
        using (var unknownContext = JsonDocument.Parse(unknown.ContextJson))
            check(!unknown.Actionable && unknown.Options.Length == 0 &&
                unknownContext.RootElement.GetProperty("part").GetProperty("CardJson").ValueKind == JsonValueKind.Null &&
                unknownContext.RootElement.GetProperty("part").GetProperty("ReviewEvidenceJson").ValueKind == JsonValueKind.Null &&
                unknownContext.RootElement.GetProperty("part").GetProperty("ReviewFindings").ValueKind == JsonValueKind.Null,
                "Unknown reconsider hides actual card, evidence, findings and actions");
        Refuse<InvalidOperationException>(() => store.ApplyControlReconsider(command, 28), "Unknown writer cannot reopen review");
        Sql("UPDATE harness_runs SET status='Completed' WHERE run_id='" + run.RunId + "'; UPDATE threads SET status=2 WHERE thread_id='" + run.ThreadId + "';");
        var frozen = store.ReadControlInbox().Items.Single(i => i.Kind == "reconsider" && i.ObjectId == command.PartId);
        check(!frozen.Actionable && frozen.BlockedReason == "thread_frozen", "frozen review inbox offers no misleading reconsider action");
        Refuse<InvalidOperationException>(() => store.ApplyControlReconsider(command, 28), "a frozen thread cannot be activated by reconsider");
        Refuse<InvalidOperationException>(() => store.RecordDecision(Id(), "review-untouched", AutomationDecision.Accept, null, "{}", "legacy", Id(), 28,
            run.RunId, head, 1), "direct controlled U1 review also respects the frozen-thread fence");
        Sql("UPDATE threads SET status=0 WHERE thread_id='" + run.ThreadId + "'; UPDATE control_packages SET approved_version=NULL WHERE package_id='Review';");
        Refuse<InvalidOperationException>(() => store.ApplyControlReconsider(command, 28), "withdrawn contract cannot reopen review");
        Sql("UPDATE control_packages SET approved_version=version WHERE package_id='Review';");
        var blocked = Command("blocked");
        Sql("INSERT INTO integration_marks VALUES('review-blocked',1,'fixture-imported-mark',28);");
        Refuse<InvalidOperationException>(() => store.ApplyControlReconsider(blocked, 28), "reconsider cannot silently rewind an imported integration mark");
        Sql("DELETE FROM integration_marks WHERE part_id='review-blocked';");
        Sql("CREATE TRIGGER reconsider_abort BEFORE INSERT ON control_receipts WHEN NEW.command_id='" + command.CommandId + "' BEGIN SELECT RAISE(ABORT,'injected reopen failure'); END;");
        Refuse<SqliteException>(() => store.ApplyControlReconsider(command, 28), "late reopen persistence failure is reported");
        check(Revision() == before && store.GetPart(command.PartId).Status == DeliveryPartStatus.Deferred && store.ListEventsAfter(0).Count == events &&
            store.ListDecisions(command.PartId).Single() == initial, "failed reopen restores original decision, part, revision and journal together");
        Sql("DROP TRIGGER reconsider_abort;");
        Sql("UPDATE threads SET status=1 WHERE thread_id='" + run.ThreadId + "';");
        var result = store.ApplyControlReconsider(command, 29);
        check(store.GetThread(run.ThreadId).Status == ThreadStatus.Active, "reopening a completed review thread explicitly returns it to active");
        check(store.GetPart(command.PartId).Status == DeliveryPartStatus.Delivered && store.ListDecisions(command.PartId).Single() == initial &&
            store.ListRevisions(command.PartId).Single() == delivery, "reopening retains original delivery bytes/run/HEAD/revision and immutable decision");
        check(store.GetPart("review-accept").Status == DeliveryPartStatus.Accepted && store.GetPart("review-rework").Status == DeliveryPartStatus.ReworkRequested &&
            store.GetPart("review-blocked").Status == DeliveryPartStatus.Blocked, "reopening one part preserves every sibling status");
        check(store.ReadControlInbox().Items.Any(i => i.Kind == "review" && i.ObjectId == command.PartId) &&
            !store.ReadControlInbox().Items.Any(i => i.Kind == "reconsider" && i.ObjectId == command.PartId), "explicit reopening returns only that part to the real review inbox");
        Refuse<InvalidOperationException>(() => store.ApplyControlReconsider(command with { CommandId = Id(), ExpectedRevision = Revision() }, 29), "a different command cannot reopen already pending evidence twice");
        Refuse<InvalidOperationException>(() => store.ApplyControlReconsider(command with { Reason = "changed" }, 29), "replayed reopen identity cannot change its reason");
        var item = store.ReadControlInbox().Items.Single(i => i.Kind == "review" && i.ObjectId == command.PartId);
        var review = new ControlReviewCommand(Id(), Revision(), "human-new-review", run.TaskId, command.PartId, 1, run.RunId, head,
            item.EvidenceToken, AutomationDecision.Defer, "A second local issue remains", "{\"checks\":[\"new-review\"]}");
        store.ApplyControlReview(review, 1); // A caller's clock can move backwards; insertion order remains authoritative.
        check(store.ListDecisions(command.PartId).Count == 2 && store.ListDecisions(command.PartId).First() == initial &&
            store.ReadControlInbox().Items.Single(i => i.Kind == "reconsider" && i.ObjectId == command.PartId).ContextJson.Contains("human-new-review"),
            "backwards timestamp cannot make the old decision replace the latest appended review");
        Refuse<InvalidOperationException>(() => store.ApplyControlReconsider(command with { CommandId = Id(), ExpectedRevision = Revision() }, 30), "prior decision/context cannot reopen a later defer review");
        var next = Command("defer"); var file = Path.Combine(root, "reconsider 决定.json"); File.WriteAllText(file, JsonSerializer.Serialize(next));
        async Task<string> Cli()
        {
            var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in new[] { typeof(Fufucode.Cli.CliOptions).Assembly.Location, "control", "reconsider", db, file }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!; var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); var text = await output; var detail = await error;
            if (process.ExitCode != 0) throw new Exception(detail); return text;
        }
        var response = await Cli(); check(response == await Cli() && store.GetPart(command.PartId).Status == DeliveryPartStatus.Delivered,
            "actual CLI reopen and cross-process retry return one immutable receipt");
        item = store.ReadControlInbox().Items.Single(i => i.Kind == "review" && i.ObjectId == command.PartId);
        store.ApplyControlReview(new(Id(), Revision(), "human-final", run.TaskId, command.PartId, 1, run.RunId, head, item.EvidenceToken,
            AutomationDecision.Accept, null, "{\"checks\":[\"resolved\"]}"), 0);
        check(store.ListDecisions(command.PartId).Count == 3 && store.ListDecisions(command.PartId).First() == initial &&
            store.GetPart(command.PartId).Status == DeliveryPartStatus.Accepted && !store.IsIntegrated(command.PartId, 1),
            "repeated reconsider cycles append decisions, preserve oldest history, and accept without integration");
        using var reopened = new AutomationStore(db); var stable = Revision();
        check(reopened.ApplyControlReconsider(command, 31) == result && Revision() == stable && reopened.ListDecisions(command.PartId).Count == 3,
            "lost first reopen response remains replayable after later reviews and process reopen");
        check(store.ReadControlIntegrationCandidate("Review")!.ContextJson.Contains("human-final") &&
            !store.ReadControlInbox().Items.Any(i => i.Kind == "reconsider" && i.ObjectId == command.PartId),
            "integration evidence selects the latest accepted review despite clock reversal");
        // Even a caller that changes only a status cannot record another decision without a current reopen.
        Sql("UPDATE delivery_parts SET status=0 WHERE part_id='review-defer';");
        Refuse<InvalidOperationException>(() => store.RecordDecision(Id(), command.PartId, AutomationDecision.Accept, null, "{}", "legacy", Id(), 32,
            run.RunId, head, 1), "direct U1 cannot add a decision without a matching current reopen");
        Sql("UPDATE delivery_parts SET status=1 WHERE part_id='review-defer';");
        MigrationChecks.Run(root, check);
    }
}
