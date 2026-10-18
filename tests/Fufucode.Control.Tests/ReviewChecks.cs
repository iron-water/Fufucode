using System.Diagnostics;
using System.Text.Json;
using Fufucode.Core.Automation;
using Microsoft.Data.Sqlite;

internal static class ReviewChecks
{
    public static async Task Run(AutomationStore store, ControlQueueReceipt run, string head,
        string db, string root, Action<bool, string> check)
    {
        var n = 0; string Id() => "review-fixture-" + ++n;
        long Revision() => store.ReadControlSnapshot().Revision;
        void Refuse<T>(Action action, string label) where T : Exception
        { try { action(); } catch (T) { check(true, label); return; } throw new Exception("FAIL " + label); }
        void Sql(string sql)
        { using var c = new SqliteConnection("Data Source=" + db); c.Open(); using var q = c.CreateCommand(); q.CommandText = sql; q.ExecuteNonQuery(); }
        foreach (var id in new[] { "accept", "rework", "blocked", "defer", "untouched", "race1", "race2", "cli" })
        {
            store.CreatePart("review-" + id, run.TaskId, id, 20);
            store.RecordDelivery(Id(), "review-" + id, run.RunId, head, "{\"note\":\"accept rework integrated are opaque words\"}", Id(), 21);
        }
        ControlReviewCommand Command(string part, AutomationDecision decision = AutomationDecision.Accept)
        {
            var item = store.ReadControlInbox().Items.Single(i => i.Kind == "review" && i.ObjectId == "review-" + part);
            return new(Id(), Revision(), "human-review", run.TaskId, item.ObjectId, item.Version!.Value,
                run.RunId, head, item.EvidenceToken, decision, decision == AutomationDecision.Accept ? null : "Explicit fixture reason",
                "{\"checks\":[\"local-fixture\"],\"source\":\"independent reviewer\"}");
        }
        var pending = Command("accept"); var before = Revision(); var events = store.ListEventsAfter(0).Count;
        Refuse<InvalidOperationException>(() => store.ApplyControlReview(pending with { ExpectedRevision = before - 1 }, 22), "review refuses stale control revision");
        Refuse<InvalidOperationException>(() => store.ApplyControlReview(pending with { TaskId = "wrong-task" }, 22), "review refuses a part from another task");
        Refuse<InvalidOperationException>(() => store.ApplyControlReview(pending with { ExpectedPartRevision = 2 }, 22), "review refuses replacement part revision");
        Refuse<InvalidOperationException>(() => store.ApplyControlReview(pending with { ExpectedRunId = "invented" }, 22), "review refuses mismatched producing run");
        Refuse<InvalidOperationException>(() => store.ApplyControlReview(pending with { ExpectedHeadSha = new string('f', 40) }, 22), "review refuses mismatched producing HEAD");
        Refuse<InvalidOperationException>(() => store.ApplyControlReview(pending with { ExpectedEvidenceToken = "invented" }, 22), "review refuses altered delivery context");
        Refuse<ArgumentOutOfRangeException>(() => store.ApplyControlReview(pending with { Decision = (AutomationDecision)99 }, 22), "undefined decision cannot silently become defer");
        var missingDecision = JsonSerializer.SerializeToNode(pending)!; missingDecision.AsObject().Remove("Decision");
        Refuse<JsonException>(() => JsonSerializer.Deserialize<ControlReviewCommand>(missingDecision.ToJsonString()), "missing explicit JSON decision cannot default to Accept");
        Refuse<ArgumentException>(() => store.ApplyControlReview(pending with { EvidenceJson = "{}" }, 22), "review requires explicit evidence");
        Refuse<JsonException>(() => store.ApplyControlReview(pending with { EvidenceJson = "not-json" }, 22), "review rejects malformed evidence JSON");
        foreach (var decision in new[] { AutomationDecision.Rework, AutomationDecision.Blocked, AutomationDecision.Defer })
            Refuse<ArgumentException>(() => store.ApplyControlReview(pending with { Decision = decision, Findings = " " }, 22), "non-accept decision requires a reason: " + decision);
        check(Revision() == before && store.ListEventsAfter(0).Count == events && store.ListDecisions(pending.PartId).Count == 0,
            "all rejected reviews leave part, events and control revision unchanged");
        Sql("CREATE TRIGGER review_abort BEFORE INSERT ON control_receipts WHEN NEW.command_id='" + pending.CommandId + "' BEGIN SELECT RAISE(ABORT,'injected review failure'); END;");
        Refuse<SqliteException>(() => store.ApplyControlReview(pending, 22), "late review receipt failure is surfaced");
        check(store.GetPart(pending.PartId).Status == DeliveryPartStatus.Delivered && store.ListDecisions(pending.PartId).Count == 0 &&
            Revision() == before && store.ListEventsAfter(0).Count == events, "late failure rolls back U1 decision, part, task, events and control revision together");
        Sql("DROP TRIGGER review_abort;");
        // Model status Unknown and an unapproved contract are independent fences.
        Sql("UPDATE harness_runs SET status='Unknown' WHERE run_id='" + run.RunId + "';");
        var unknown = store.ReadControlInbox().Items.Single(i => i.ObjectId == pending.PartId);
        check(!unknown.Actionable && unknown.Options.Length == 0 && !unknown.ContextJson.Contains("opaque words"), "Unknown writer hides card and decision actions");
        Refuse<InvalidOperationException>(() => store.ApplyControlReview(pending, 22), "gateway cannot bypass Unknown writer");
        Sql("UPDATE harness_runs SET status='Completed' WHERE run_id='" + run.RunId + "'; UPDATE control_packages SET approved_version=NULL WHERE package_id='Review';");
        check(!store.ReadControlInbox().Items.Single(i => i.ObjectId == pending.PartId).Actionable, "unapproved contract does not offer review actions");
        Refuse<InvalidOperationException>(() => store.ApplyControlReview(Command("accept"), 22), "gateway cannot review a withdrawn contract");
        Sql("UPDATE control_packages SET approved_version=version WHERE package_id='Review';");
        var result = store.ApplyControlReview(pending, 23);
        check(result.RunId == run.RunId && result.HeadSha == head && result.PartRevision == 1 && result.Revision == before + 1,
            "human review receipt binds real task, part revision, run and HEAD");
        check(store.GetPart("review-accept").Status == DeliveryPartStatus.Accepted && store.GetPart("review-untouched").Status == DeliveryPartStatus.Delivered &&
            !store.IsIntegrated("review-accept", 1), "accept changes exactly one part and creates no integration mark");
        check(store.ListEventsAfter(0).Any(e => e.Type == "control.review.applied" && e.RunId == run.RunId && e.PartId == pending.PartId),
            "control event carries exact producing run and reviewed part");
        var staleSecond = Command("rework", AutomationDecision.Rework);
        foreach (var (part, decision, status) in new[] {
            ("rework", AutomationDecision.Rework, DeliveryPartStatus.ReworkRequested),
            ("blocked", AutomationDecision.Blocked, DeliveryPartStatus.Blocked),
            ("defer", AutomationDecision.Defer, DeliveryPartStatus.Deferred) })
        {
            store.ApplyControlReview(Command(part, decision), 24);
            check(store.GetPart("review-" + part).Status == status && store.ListDecisions("review-accept").Count == 1 &&
                store.GetPart("review-untouched").Status == DeliveryPartStatus.Delivered, "partial " + decision + " preserves independently accepted and pending siblings");
        }
        var settledRevision = Revision(); var settledEvents = store.ListEventsAfter(0).Count;
        using (var reopened = new AutomationStore(db))
            check(reopened.ApplyControlReview(pending, 25) == result && Revision() == settledRevision && store.ListEventsAfter(0).Count == settledEvents,
                "lost response replay after unrelated decisions and process reopen returns original receipt without another decision");
        Refuse<InvalidOperationException>(() => store.ApplyControlReview(pending with { DecidedBy = "another" }, 25), "replayed identity cannot change actor");
        Refuse<InvalidOperationException>(() => store.ApplyControlReview(pending with { EvidenceJson = "{\"checks\":[\"edited\"]}" }, 25), "replayed identity cannot change validation evidence");
        Refuse<InvalidOperationException>(() => store.ApplyControlReview(pending with { Decision = AutomationDecision.Rework, Findings = "changed" }, 25), "replayed identity cannot change decision");
        Refuse<InvalidOperationException>(() => store.ApplyControlReview(staleSecond, 25), "obsolete client review cannot overwrite an already decided part");
        check(!store.CanRequestControlIntegration("Review"), "one accepted part does not make a mixed review bundle integration-ready");
        var a = Command("race1"); var b = Command("race2");
        using var barrier = new Barrier(2);
        Task<bool> Compete(ControlReviewCommand cmd) => Task.Run(() =>
        {
            using var other = new AutomationStore(db); barrier.SignalAndWait();
            try { other.ApplyControlReview(cmd, 26); return true; } catch (InvalidOperationException) { return false; }
        });
        var race = await Task.WhenAll(Compete(a), Compete(b));
        check(race.Count(v => v) == 1 && Revision() == settledRevision + 1, "two clients at one revision commit exactly one partial review");
        var cliCommand = Command("cli"); var file = Path.Combine(root, "review 决定.json");
        File.WriteAllText(file, JsonSerializer.Serialize(cliCommand));
        async Task<string> Cli()
        {
            var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            start.Environment["FUFUCODE_DATA_ROOT"] = ArchiveChecks.CliDataRoot;
            foreach (var arg in new[] { typeof(Fufucode.Cli.CliOptions).Assembly.Location, "control", "review", db, file }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!; var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); var text = await output; var detail = await error;
            if (process.ExitCode != 0) throw new Exception(detail); return text;
        }
        var response = await Cli(); var replay = await Cli(); using var json = JsonDocument.Parse(response);
        check(response == replay && json.RootElement.GetProperty("runId").GetString() == run.RunId && store.GetPart("review-cli").Status == DeliveryPartStatus.Accepted,
            "real CLI review and repeated cross-process submission share the same immutable receipt");
        // Feed actual Core context through the JS client, not a camelCase mock.
        var actualItem = store.ReadControlInbox().Items.Single(i => i.ObjectId == "review-untouched");
        var itemFile = Path.Combine(root, "review-context.json");
        File.WriteAllText(itemFile, JsonSerializer.Serialize(actualItem, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        var script = Path.Combine(root, "review-context.cjs");
        File.WriteAllText(script, "const {buildControlDecision}=require(process.argv[2]);const fs=require('fs');console.log(JSON.stringify(buildControlDecision(JSON.parse(fs.readFileSync(process.argv[3],'utf8')))));");
        var node = new ProcessStartInfo("node") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { script, Path.GetFullPath("docs/frontend-prototype/automation/control-commands.js"), itemFile }) node.ArgumentList.Add(arg);
        using var js = Process.Start(node)!; var jsOutput = js.StandardOutput.ReadToEndAsync(); var jsError = js.StandardError.ReadToEndAsync(); await js.WaitForExitAsync();
        var jsText = await jsOutput; var jsDetail = await jsError; if (js.ExitCode != 0) throw new Exception(jsDetail);
        using var template = JsonDocument.Parse(jsText);
        check(template.RootElement.GetProperty("taskId").GetString() == run.TaskId && template.RootElement.GetProperty("expectedRunId").GetString() == run.RunId &&
            template.RootElement.GetProperty("expectedEvidenceToken").GetString() == actualItem.EvidenceToken,
            "browser command template preserves actual Core PascalCase task/run/context identity");
    }
}
