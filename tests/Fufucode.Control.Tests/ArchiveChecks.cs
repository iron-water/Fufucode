using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fufucode.Cli;
using Fufucode.Core;
using Fufucode.Core.Automation;
using Fufucode.Core.Plugins;
using Microsoft.Data.Sqlite;

internal static class ArchiveChecks
{
    /// <summary>
    /// 自动开发命令现在由插件提供：CLI 子进程需要一个真实、隔离的插件安装。
    /// 只复制命令入口程序集与清单；页面/Avalonia 类型不会被 CLI 触碰。
    /// </summary>
    internal static readonly string CliDataRoot = PrepareCliDataRoot();

    private static string PrepareCliDataRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "fufucode-cli-plugin-" + Guid.NewGuid().ToString("N"));
        var layout = new AppDataLayout(root);
        var pluginId = "automation.auto-dev";
        var dir = layout.PluginDirectory(pluginId);
        Directory.CreateDirectory(dir);
        var payload = typeof(Fufucode.Plugins.AutoDev.AutoDevPluginPackage).Assembly.Location;
        File.Copy(payload, Path.Combine(dir, "Fufucode.Plugins.AutoDev.dll"), true);
        File.WriteAllText(Path.Combine(dir, "manifest.json"), """
            {"id":"automation.auto-dev","name":"自动开发工作台","version":"1.0.0",
             "entryAssembly":"Fufucode.Plugins.AutoDev.dll",
             "packageType":"Fufucode.Plugins.AutoDev.AutoDevPluginPackage",
             "commandHostType":"Fufucode.Plugins.AutoDev.AutoDevCommandHost","category":"Automation"}
            """);
        new PluginStateStore(layout).Install(pluginId);
        return root;
    }

    internal static async Task Run(string root, Action<bool, string> check)
    {
        void Refused<T>(Action action, string label) where T : Exception
        {
            try { action(); } catch (T) { check(true, label); return; }
            throw new Exception("FAIL " + label);
        }
        string Hash(string json) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        var db = Path.Combine(root, "archive.db");
        using var store = new AutomationStore(db);
        const string raw = """
            {
              "schemaVersion":1,"revision":7,"mode":"automatic",
              "packages":{"P":{"id":"P","status":"integrated","threadId":"foreign-thread"}},
              "groups":{"G":{"relation":"competitive","winner":"P"}},
              "decisions":{"D":{"status":"accepted","head":"legacy-claim"}},
              "knowledge":{"K":{"version":3,"text":"原始知识"}},
              "questions":{},"intents":{"I":{"status":"running","runId":"foreign-run"}},
              "events":[{"type":"integration.report","mainSha":"unverified-claim"}],
              "receipts":{"old-command":{"digest":"old-hash"}},
              "futureExtension":{"keep":"扩展字段与换行\n原样保留"}
            }
            """;
        var command = new LegacyControlArchiveCommand("archive-1", 0, "local-human",
            "old-discussion-console", 7, Hash(raw), raw);
        var receipt = store.ArchiveLegacyControl(command, 1);
        check(receipt.Status == "history_only", "legacy declarations are captured as explicitly labelled history");
        var exported = store.ExportControlShadow();
        check(exported.Control.Mode == DevelopmentMode.Manual && exported.Control.Packages.Count == 0 &&
            exported.Control.Intents.Count == 0, "legacy automatic/integrated/running claims create no new authority");
        check(exported.LegacyHistory.Single().Json == raw, "groups, knowledge, review, execution and extra fields survive verbatim");
        check(exported.LegacyHistory.Single().ContentSha256 == Hash(raw).ToLowerInvariant(),
            "source history has a verifiable UTF-8 content fingerprint");
        check(exported.Control.Revision == 1 && exported.EventCursor == 1,
            "archive, command receipt and U1 event share one committed revision");
        check(store.ArchiveLegacyControl(command, 2) == receipt && store.ExportControlShadow().LegacyHistory.Count == 1,
            "lost archive acknowledgment replays without duplicate source history");
        Refused<ArgumentException>(() => store.ArchiveLegacyControl(command with { Json = raw + " " }, 3),
            "changed source fails its supplied fingerprint");
        Refused<InvalidOperationException>(() => store.ArchiveLegacyControl(command with { DecidedBy = "different" }, 3),
            "same archive command cannot change attribution");
        Refused<InvalidOperationException>(() => store.ArchiveLegacyControl(command with
            { CommandId = "duplicate-origin", ExpectedRevision = 1 }, 3),
            "source id and source version cannot be overwritten with a new command");
        Refused<ArgumentException>(() => store.ArchiveLegacyControl(command with
            { CommandId = "wrong-source-version", ExpectedRevision = 1, SourceRevision = 8 }, 3),
            "source revision must match the original document");
        var future = raw.Replace("\"schemaVersion\":1", "\"schemaVersion\":2");
        Refused<ArgumentException>(() => store.ArchiveLegacyControl(command with
            { CommandId = "future-schema", ExpectedRevision = 1, Json = future, ContentSha256 = Hash(future) }, 3),
            "unknown legacy schema is refused rather than guessed");
        using (var c = new SqliteConnection("Data Source=" + db))
        {
            c.Open(); using var q = c.CreateCommand();
            q.CommandText = "SELECT (SELECT COUNT(*) FROM threads)+(SELECT COUNT(*) FROM tasks)+(SELECT COUNT(*) FROM part_decisions)+(SELECT COUNT(*) FROM integration_marks)";
            check(Convert.ToInt64(q.ExecuteScalar()) == 0, "source claims create no thread, task, review or integration fact");
            q.CommandText = "CREATE TRIGGER archive_abort BEFORE INSERT ON control_receipts BEGIN SELECT RAISE(ABORT,'late archive failure'); END";
            q.ExecuteNonQuery();
        }
        var second = command with { CommandId = "archive-2", ExpectedRevision = 1, SourceId = "another-origin" };
        Refused<SqliteException>(() => store.ArchiveLegacyControl(second, 4), "late persistence failure aborts archive transaction");
        check(store.ExportControlShadow().LegacyHistory.Count == 1 && store.ReadControlSnapshot().Revision == 1 &&
            store.ListEventsAfter(0).Count == 1, "late archive rollback leaves neither history nor orphan event");
        using (var c = new SqliteConnection("Data Source=" + db))
        {
            c.Open(); using var q = c.CreateCommand(); q.CommandText = "DROP TRIGGER archive_abort"; q.ExecuteNonQuery();
        }
        store.ArchiveLegacyControl(second, 5);
        using var reopened = new AutomationStore(db);
        check(reopened.ExportControlShadow().LegacyHistory.Count == 2 &&
            reopened.ExportControlShadow().LegacyHistory.All(a => a.Json == raw),
            "reopen preserves separate immutable origins and complete source content");
        store.ApplyControlCommand(new("semi-archive", 2, "local-human", ControlAction.SetMode,
            Mode: DevelopmentMode.SemiAutomatic), 6);
        Refused<InvalidOperationException>(() => store.ArchiveLegacyControl(command with
            { CommandId = "not-manual", ExpectedRevision = 3, SourceId = "third-origin" }, 7),
            "history capture requires explicit manual mode");

        // Real local CLI processes use the same SQLite state without any Pi runtime or model.
        var cliDb = Path.Combine(root, "cli-control.db");
        var init = await Cli("control", "init", cliDb);
        check(init.Exit == 0 && JsonDocument.Parse(init.Output).RootElement.GetProperty("mode").GetString() == "Manual",
            "actual CLI initializes an explicit database in manual mode");
        var cliArchive = command with { SourceId = "cli-origin" };
        var file = Path.Combine(root, "archive command 资料.json");
        File.WriteAllText(file, JsonSerializer.Serialize(cliArchive));
        var archive = await Cli("control", "archive", cliDb, file);
        check(archive.Exit == 0 && JsonDocument.Parse(archive.Output).RootElement.GetProperty("status").GetString() == "history_only",
            "actual CLI archives Unicode/space-path command into the same store");
        var export = await Cli("control", "export", cliDb);
        using (var json = JsonDocument.Parse(export.Output))
            check(export.Exit == 0 && json.RootElement.GetProperty("legacyHistory")[0].GetProperty("json").GetString() == raw,
                "actual CLI export preserves source JSON and labels it separately");
        var planFile = Path.Combine(root, "plan command.json");
        var plan = new ControlCommand("cli-plan", 1, "local-human", ControlAction.DefinePackage,
            new("CLI", "CLI package", "Complete one local change", ["src/owned/**"],
                ["Behavior verified"], "codex", "devin/swe-2", "code", []), ExpectedPackageVersion: 0);
        File.WriteAllText(planFile, JsonSerializer.Serialize(plan));
        var apply = await Cli("control", "apply", cliDb, planFile);
        check(apply.Exit == 0 && JsonDocument.Parse(apply.Output).RootElement.GetProperty("revision").GetInt64() == 2,
            "actual CLI applies a versioned planning command to the selected database");
        var snapshot = await Cli("control", "snapshot", cliDb);
        using (var json = JsonDocument.Parse(snapshot.Output))
            check(snapshot.Exit == 0 && json.RootElement.GetProperty("packages")[0].GetProperty("spec").GetProperty("id").GetString() == "CLI" &&
                json.RootElement.GetProperty("intents").GetArrayLength() == 0,
                "separate CLI snapshot reads the persisted plan without dispatch in manual mode");
        var revisionBefore = new AutomationStore(cliDb);
        var before = revisionBefore.ReadControlSnapshot().Revision;
        revisionBefore.Dispose();
        var bad = await Cli("control", "execution.report", cliDb);
        check(bad.Exit == 2, "CLI command whitelist does not accept forged execution reports");
        File.WriteAllText(planFile, "{\"commandId\":\"forged\",\"expectedRevision\":2,\"decidedBy\":\"local-human\",\"action\":\"ExecutionReport\"}");
        var forged = await Cli("control", "apply", cliDb, planFile);
        check(forged.Exit == 2, "planning JSON cannot smuggle an execution report through apply");
        using var after = new AutomationStore(cliDb);
        check(after.ReadControlSnapshot().Revision == before, "rejected report changes no authoritative control revision");
        var missing = Path.Combine(root, "missing-control.db");
        var absent = await Cli("control", "snapshot", missing);
        check(absent.Exit == 3 && !File.Exists(missing), "snapshot of an uninitialized database creates no phantom state");
    }

    internal static async Task<(int Exit, string Output)> Cli(params string[] args)
    {
        var info = new System.Diagnostics.ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        info.ArgumentList.Add(typeof(Fufucode.Cli.Program).Assembly.Location);
        info.Environment["FUFUCODE_DATA_ROOT"] = CliDataRoot;
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = System.Diagnostics.Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(await error))
            throw new Exception("Unexpected CLI diagnostic: " + await error);
        return (process.ExitCode, await output);
    }
}
