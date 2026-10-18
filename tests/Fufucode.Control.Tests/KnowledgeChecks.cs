using System.Diagnostics;
using System.Text.Json;
using Fufucode.Core.Automation;
using Microsoft.Data.Sqlite;

internal static class KnowledgeChecks
{
    public static async Task Run(string root, Action<bool, string> check)
    {
        var db = Path.Combine(root, "knowledge.db");
        using var store = new AutomationStore(db);
        _ = new Fufucode.Core.Automation.Hosting.HostJournal(store);
        void Refuse(Action action, string name)
        {
            try { action(); }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or KeyNotFoundException or SqliteException)
            { check(true, name); return; }
            check(false, name);
        }
        long Revision() => store.ReadControlSnapshot().Revision;
        ControlKnowledgeReceipt Apply(ControlKnowledgeCommand command) => store.ApplyControlKnowledge(command, 100);
        ControlKnowledgeCommand Propose(string id, string key, long previous = 0) =>
            new(id, Revision(), "M", ControlKnowledgeAction.Propose, key, "通知保留三个地点", "docs/decisions.md", previous);
        var invalid = Propose("no-source", "notifications") with { Source = " " };
        Refuse(() => Apply(invalid), "knowledge rejects a fact without an evidence source");
        check(Revision() == 0 && store.ReadControlSnapshot().Knowledge.Proposals.Count == 0,
            "invalid proposal writes no state or event");
        var first = Propose("proposal-a", "notifications");
        var receipt = Apply(first);
        check(receipt.Status == "pending" && store.ReadControlSnapshot().Knowledge.Facts.Count == 0,
            "knowledge proposal is not a published fact");
        check(Apply(first) == receipt && Revision() == 1 && store.ListEventsAfter(0).Count == 1,
            "knowledge replay returns its original receipt without another event");
        Refuse(() => Apply(first with { Text = "different" }), "knowledge command identity cannot be reused with changed contents");
        Apply(Propose("proposal-b", "notifications"));
        var publish = new ControlKnowledgeCommand("publish-a", Revision(), "human", ControlKnowledgeAction.Publish,
            ExpectedKnowledgeVersion: 0, ProposalId: "proposal-a");
        Apply(publish);
        var facts = store.ReadControlSnapshot().Knowledge.Facts;
        check(facts.Count == 1 && facts[0] is { Version: 1, Text: "通知保留三个地点", Source: "docs/decisions.md", PublishedBy: "human" },
            "publication keeps exact proposed text, source, version and human decision");
        check(store.ReadControlSnapshot().Knowledge.Proposals.Single(p => p.Id == "proposal-a").Status == "published_unadopted",
            "publication states that execution adoption is a separate pending capability");
        Refuse(() => Apply(new("stale-publication", Revision(), "M", ControlKnowledgeAction.Publish,
            ExpectedKnowledgeVersion: 0, ProposalId: "proposal-b")), "competing obsolete proposal cannot overwrite the latest fact");
        Refuse(() => Apply(publish with { CommandId = "publish-again", ExpectedRevision = Revision() }),
            "a decided proposal cannot publish another version");
        Apply(Propose("proposal-v2", "notifications", 1) with { Text = "窗口状态栏默认关闭" });
        Apply(new("publish-v2", Revision(), "M", ControlKnowledgeAction.Publish, ExpectedKnowledgeVersion: 1, ProposalId: "proposal-v2"));
        check(store.ReadControlSnapshot().Knowledge.Facts.Select(f => f.Version).SequenceEqual([1L, 2L]) &&
            store.ReadControlSnapshot().Knowledge.Facts[0].Text == "通知保留三个地点",
            "publishing a new fact retains its immutable prior version");
        Apply(new("reject-b", Revision(), "human", ControlKnowledgeAction.RejectProposal, ProposalId: "proposal-b", Reason: "superseded evidence"));
        check(store.ReadControlSnapshot().Knowledge.Proposals.Single(p => p.Id == "proposal-b").Reason == "superseded evidence",
            "rejected proposal retains the concrete decision reason");
        Refuse(() => Apply(Propose("bad-enum", "unknown") with { Action = (ControlKnowledgeAction)99 }),
            "unknown knowledge action is refused");
        using (var c = new SqliteConnection("Data Source=" + db))
        {
            c.Open(); using var q = c.CreateCommand();
            q.CommandText = "SELECT (SELECT COUNT(*) FROM host_requests)+(SELECT COUNT(*) FROM tasks)+(SELECT COUNT(*) FROM control_intents)";
            check(Convert.ToInt64(q.ExecuteScalar()) == 0, "knowledge and decisions invent no task, intent or host dispatch");
        }

        void Define(string id, long previous = 0) => store.ApplyControlCommand(new("define-" + id + "-" + previous,
            Revision(), "M", ControlAction.DefinePackage,
            new(id, id, "One bounded task", ["src/owned/**"], ["Verified behavior"], "codex", "devin/swe-2", "code", []),
            ExpectedPackageVersion: previous), 200);
        Define("A"); Define("B");
        var ask = new ControlKnowledgeCommand("question", Revision(), "human", ControlKnowledgeAction.Ask,
            Text: "Which notification route owns this event?", FromPackage: "A", ToPackage: "B", ExpectedFromVersion: 1, ExpectedToVersion: 1);
        Apply(ask);
        check(store.ReadControlSnapshot().Knowledge.Questions.Single() is { FromPackage: "A", ToPackage: "B", ContractCurrent: true, Answer: null },
            "question targets two explicit current package versions");
        Refuse(() => Apply(ask with { CommandId = "self-question", ExpectedRevision = Revision(), ToPackage = "A" }),
            "directed question cannot target its own package");
        var answer = new ControlKnowledgeCommand("answer", Revision(), "human", ControlKnowledgeAction.Answer,
            Text: "Use the auxiliary notification page", ExpectedFromVersion: 1, ExpectedToVersion: 1, QuestionId: "question");
        Apply(answer);
        check(store.ReadControlSnapshot().Knowledge.Questions.Single().Answer == answer.Text &&
            store.ReadControlSnapshot().Knowledge.Facts.Count == 2,
            "recorded human answer does not automatically become public knowledge");
        Refuse(() => Apply(answer with { CommandId = "replace-answer", ExpectedRevision = Revision(), Text = "overwrite" }),
            "an existing answer cannot be overwritten");
        Apply(ask with { CommandId = "question-2", ExpectedRevision = Revision() });
        Define("B", 1);
        check(!store.ReadControlSnapshot().Knowledge.Questions.Single(q => q.Id == "question-2").ContractCurrent,
            "package revision makes an old directed question visibly obsolete");
        Refuse(() => Apply(answer with { CommandId = "answer-obsolete", ExpectedRevision = Revision(), QuestionId = "question-2" }),
            "obsolete question cannot receive a current-contract answer");

        // Fail after publication mutation and event insertion, at the durable receipt boundary.
        Apply(Propose("rollback-proposal", "rollback"));
        var before = Revision(); var beforeEvents = store.ListEventsAfter(0).Count;
        var rollback = new ControlKnowledgeCommand("rollback-publish", before, "human", ControlKnowledgeAction.Publish,
            ExpectedKnowledgeVersion: 0, ProposalId: "rollback-proposal");
        using (var c = new SqliteConnection("Data Source=" + db))
        {
            c.Open(); using var q = c.CreateCommand();
            q.CommandText = "CREATE TRIGGER knowledge_abort BEFORE INSERT ON control_receipts WHEN NEW.command_id='rollback-publish' BEGIN SELECT RAISE(ABORT,'injected failure'); END;";
            q.ExecuteNonQuery();
        }
        Refuse(() => Apply(rollback), "late publication persistence failure is reported");
        check(Revision() == before && store.ListEventsAfter(0).Count == beforeEvents &&
            !store.ReadControlSnapshot().Knowledge.Facts.Any(f => f.Key == "rollback") &&
            store.ReadControlSnapshot().Knowledge.Proposals.Single(p => p.Id == "rollback-proposal").Status == "pending",
            "publication rollback preserves proposal, fact history, revision and event cursor together");
        using (var c = new SqliteConnection("Data Source=" + db))
        { c.Open(); using var q = c.CreateCommand(); q.CommandText = "DROP TRIGGER knowledge_abort"; q.ExecuteNonQuery(); }
        Apply(rollback);
        using var reopened = new AutomationStore(db);
        check(reopened.ReadControlSnapshot().Knowledge.Facts.Count == 3 &&
            reopened.ReadControlSnapshot().Knowledge.Questions.Count == 2,
            "knowledge and question history survive reopening the sole U1 store");
        check(reopened.ExportControlShadow().Control.Knowledge.Facts.Count == 3,
            "shadow export reads authoritative knowledge in the same control snapshot");

        var cliDb = Path.Combine(root, "knowledge-cli.db");
        var init = await Cli("control", "init", cliDb);
        check(init.Exit == 0, "actual CLI initializes the explicit knowledge database");
        var file = Path.Combine(root, "knowledge command 中文.json");
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(new ControlKnowledgeCommand("cli-proposal", 0, "human",
            ControlKnowledgeAction.Propose, "fixture", "保留证据", "temp fixture", 0)));
        var cli = await Cli("control", "knowledge", cliDb, file);
        using var json = JsonDocument.Parse(cli.Output);
        check(cli.Exit == 0 && json.RootElement.GetProperty("status").GetString() == "pending",
            "actual CLI calls the shared knowledge API with a Unicode command path");
        var snapshot = await Cli("control", "snapshot", cliDb);
        using var current = JsonDocument.Parse(snapshot.Output);
        check(snapshot.Exit == 0 && current.RootElement.GetProperty("knowledge").GetProperty("proposals").GetArrayLength() == 1 &&
            current.RootElement.GetProperty("knowledge").GetProperty("facts").GetArrayLength() == 0,
            "a separate CLI process observes proposal and fact separation in the same store");
    }

    private static async Task<(int Exit, string Output)> Cli(params string[] args)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(typeof(Fufucode.Cli.CliOptions).Assembly.Location);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        var text = await output; var detail = await error;
        if (process.ExitCode != 0) Console.WriteLine("CLI refused: " + detail);
        return (process.ExitCode, text);
    }
}
