using Fufucode.App.Abstractions;
using Fufucode.Cli;
using Fufucode.Core;
using Fufucode.Core.Backend;
using Fufucode.Pi;
using System.Text.Json;

internal static class CliTests
{
    public static async Task RunAsync(string root, string fake, string dotnet)
    {
        var work = Path.Combine(root, "tmp", "cli-fake-pi");
        Directory.CreateDirectory(work);
        PiRpcOptions Options() => new(dotnet, [fake], work);
        await using (var chat = await PiSessions.ConnectAsync(Options()))
        {
            using var output = new StringWriter();
            var result = await CliChatRunner.RunAsync(chat, chat, "stream", json: false,
                output, new StringReader(""));
            if (result.Outcome != SessionOutcome.Completed || result.Text != "Final answer" ||
                !output.ToString().Contains("Hel", StringComparison.Ordinal) || !output.ToString().Contains("Final answer", StringComparison.Ordinal))
                throw new Exception($"CLI chat runner did not stream the FakePi assistant response. outcome={result.Outcome} text={result.Text} output={output}");
            Console.WriteLine("PASS CLI chat runner streams FakePi text and waits for settled outcome");
        }

        await using (var chat = await PiSessions.ConnectAsync(Options()))
        {
            using var output = new StringWriter();
            var result = await CliChatRunner.RunAsync(chat, chat, "stream", json: true,
                output, new StringReader(""));
            if (result.Outcome != SessionOutcome.Completed || !output.ToString().Contains("Final answer", StringComparison.Ordinal) ||
                !output.ToString().TrimStart().StartsWith('{'))
                throw new Exception("CLI JSON chat output is not a final machine-readable result.");
            Console.WriteLine("PASS CLI JSON chat runner returns final FakePi transcript");
        }

        await TestApprovalsAsync(Options);
        await TestSessionsAsync(root, fake, dotnet);
        await CliSettingsTests.RunAsync(root, fake, dotnet);
        await CliMaintenanceTests.RunAsync(root, fake, dotnet);
        await CliHostTests.RunAsync();
        await TestDispatchAsync();
        await TestStatusAndRenameAsync(root);
        await TestNativeSessionDeletionAsync(root);
        await TestSessionSurfaceAsync(root);
        // The headless UI group drives the App; it is skipped when the App cannot render in this environment.
        if (Environment.GetEnvironmentVariable("FUFUCODE_SKIP_UI_GROUP") != "1") await RunUiAsync(root);
        await RunContractsAsync(root);
    }

    public static Task RunUiAsync(string root) => CliUiTests.RunAsync(root);
    public static async Task RunContractsAsync(string root)
    {
        SessionUsageQueryTests.Run();
        await ReadOnlyReportTests.RunAsync(root);
    }

    private static async Task TestApprovalsAsync(Func<PiRpcOptions> options)
    {
        foreach (var json in new[] { false, true })
        {
            var marker = Path.Combine(options().WorkingDirectory, "approved-" + Guid.NewGuid().ToString("N"));
            var fixture = options() with { Arguments = options().Arguments.Concat(["--approval-path", marker]).ToArray() };
            await using var chat = await PiSessions.ConnectAsync(fixture);
            using var output = new StringWriter(); using var errors = new StringWriter();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var result = await CliChatRunner.RunAsync(chat, chat, "cli_approval", json, output,
                new StringReader("approve list\napprove accept approval-1\n"), timeout.Token, errors);
            Check(result.Outcome == SessionOutcome.Completed && output.ToString().Contains("approval-1") &&
                File.Exists(marker) && File.ReadAllText(marker) == "approved",
                "CLI approve list and accept use the pending Core token and execute the approved action");
            if (json)
            {
                foreach (var line in output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries))
                { using var record = JsonDocument.Parse(line); }
                Check(!output.ToString().Contains("Allow?") && !output.ToString().Contains("Stop failed:"),
                    "CLI JSON approval records contain data only");
            }
        }
        await using (var chat = await PiSessions.ConnectAsync(options()))
        {
            using var output = new StringWriter(); using var errors = new StringWriter();
            var result = await CliChatRunner.RunAsync(chat, chat, "cli_approval", true, output,
                new StringReader("approve reject approval-1\n"), diagnostics: errors);
            Check(result.Outcome == SessionOutcome.Failed && result.Error is not null && errors.ToString().Length > 0,
                "CLI approve reject reports an approval failure");
        }
        await using (var chat = await PiSessions.ConnectAsync(options()))
        {
            var result = await CliChatRunner.RunAsync(chat, chat, "cli_approval", true, TextWriter.Null,
                new StringReader("approve reject approval-1 extra\n"), diagnostics: TextWriter.Null);
            Check(result.Outcome == SessionOutcome.Failed && result.FailureExitCode == 2,
                "CLI malformed approval commands retain the usage-error exit code instead of succeeding");
        }
        await using (var chat = await PiSessions.ConnectAsync(options()))
        {
            using var output = new StringWriter();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var result = await CliChatRunner.RunAsync(chat, chat, "cli_approval", true, output,
                new StringReader(""), timeout.Token, TextWriter.Null);
            Check(result.Outcome == SessionOutcome.Failed && result.Error!.Contains("EOF"),
                "CLI unattended EOF terminates instead of waiting on approval");
        }
        await using (var chat = await PiSessions.ConnectAsync(options()))
        {
            using var source = new GateReader();
            using var input = new CliInputReader(source);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var result = await CliChatRunner.RunAsync(chat, chat, "approval", true, TextWriter.Null,
                input, timeout.Token, TextWriter.Null);
            source.Release();
            Check(result.Outcome == SessionOutcome.Failed && result.Error!.Contains("expired"),
                "CLI approval deadline terminates a still-open stdin pipe without waiting for further input");
        }
        await using (var chat = await PiSessions.ConnectAsync(options()))
        {
            using var output = new StringWriter();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var result = await CliChatRunner.RunAsync(chat, chat, "cli_dialogs", true, output,
                new StringReader("approve accept select-1 Allow\napprove accept input-1 \"\"\napprove reject editor-1\n"),
                timeout.Token, TextWriter.Null);
            Check(result.Outcome == SessionOutcome.Failed && output.ToString().Contains("select-1") &&
                output.ToString().Contains("input-1") && output.ToString().Contains("editor-1"),
                "CLI serializes concurrent select, input and editor approvals without competing stdin readers");
            await ExpectAsync<InvalidOperationException>(() => CliApprovalCommands.ExecuteAsync(chat,
                ["accept", "select-1", "Allow"], true, TextWriter.Null));
            Console.WriteLine("PASS CLI refuses late approval tokens");
        }
        Check(CliCommandLine.Split("approve accept input-1 \"\"").SequenceEqual(["approve", "accept", "input-1", ""]) &&
            CliCommandLine.Split("extensions enable 'C:\\a b\\extension.ts'")[2] == "C:\\a b\\extension.ts",
            "CLI REPL preserves empty quoted values and Windows paths");
        foreach (var prompt in new[] { "handled", "/slow.ui" })
        {
            await using var chat = await PiSessions.ConnectAsync(options());
            using var output = new StringWriter();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var result = await CliChatRunner.RunAsync(chat, chat, prompt, true, output,
                new StringReader("approve accept slow-ui\n"), timeout.Token, TextWriter.Null);
            using var record = JsonDocument.Parse(output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)[^1]);
            Check(result.Outcome == SessionOutcome.Completed && result.Handled && record.RootElement.GetProperty("outcome").GetString() == "handled",
                "CLI handled extension command finishes only after Core get_state confirms idle, without claiming model text");
        }
    }

    private static async Task TestSessionsAsync(string root, string fake, string dotnet)
    {
        var layout = new AppDataLayout(Path.Combine(root, "tmp", "cli-session-tests", Guid.NewGuid().ToString("N")));
        var project = Path.Combine(layout.Root, "project"); var foreign = Path.Combine(layout.Root, "foreign");
        Directory.CreateDirectory(project); Directory.CreateDirectory(foreign);
        var manager = new PersistentSessionManager(dotnet, fake, layout, requireApprovalBridge: false);
        string id; string file;
        await using (var session = await manager.CreateAsync(project, backend: BackendId.Pi))
        {
            id = session.Session.Id; file = session.Session.FilePath!;
            await CliChatRunner.RunAsync(session.Chat, null, "persist_continue", true, TextWriter.Null, new StringReader(""));
            await session.RefreshHistoryAsync();
            using var output = new StringWriter();
            await CliSessionCommands.ShowAsync(session, true, output);
            using var shown = JsonDocument.Parse(output.ToString());
            Check(shown.RootElement.GetProperty("id").GetString() == id && shown.RootElement.GetProperty("summary").GetArrayLength() > 0,
                "CLI sessions show returns FakePi metadata and bounded transcript summaries");
            await ExpectAsync<InvalidOperationException>(() => CliSessionCommands.DeleteAsync(layout, project, id, true, TextWriter.Null));
            Check(File.Exists(file), "Core session deletion refuses a live session lease");
        }
        await ExpectAsync<InvalidOperationException>(() => CliSessionCommands.DeleteAsync(layout, foreign, id, true, TextWriter.Null));
        Check(File.Exists(file), "Core session deletion verifies project ownership");
        manager.Metadata.SetDraft(id, "draft");
        using (var output = new StringWriter())
        {
            await CliSessionCommands.DeleteAsync(layout, project, id, true, output);
            using var deleted = JsonDocument.Parse(output.ToString());
            Check(deleted.RootElement.GetProperty("deleted").GetBoolean() && !File.Exists(file) && manager.Metadata.GetDraft(id) is null &&
                !manager.Metadata.ListSessions(manager.Metadata.GetOrCreateProject(project).Id).Any(s => s.Id == id),
                "CLI sessions delete removes the closed Pi JSONL and Core metadata including its draft");
        }
        var outside = Path.Combine(layout.Root, "outside.jsonl"); File.WriteAllText(outside, "keep");
        var tampered = manager.Metadata.CreateSession(manager.Metadata.GetOrCreateProject(project));
        manager.Metadata.UpdateSessionFile(tampered.Id, outside);
        await ExpectAsync<InvalidOperationException>(() => CliSessionCommands.DeleteAsync(layout, project, tampered.Id, true, TextWriter.Null));
        Check(File.ReadAllText(outside) == "keep" && manager.Metadata.GetSession(tampered.Id).FilePath == outside,
            "Core session deletion rejects paths outside the project directory without modifying data");
    }

    private static async Task TestDispatchAsync()
    {
        var oldOut = Console.Out; var oldError = Console.Error;
        using var output = new StringWriter(); using var errors = new StringWriter();
        try
        {
            Console.SetOut(output); Console.SetError(errors);
            foreach (var args in new[] { new[] { "approve", "list", "--json" }, new[] { "sessions", "show" },
                new[] { "models", "set" }, new[] { "chat", "--unknown" }, new[] { "--project" } })
                if (await Fufucode.Cli.Program.Main(args) != 2) throw new Exception("CLI incomplete command did not return exit 2.");
            if (errors.ToString().Length == 0 || output.ToString().Length != 0)
                throw new Exception("CLI incomplete command diagnostics did not stay on stderr.");
        }
        finally { Console.SetOut(oldOut); Console.SetError(oldError); }
        Console.WriteLine("PASS CLI incomplete commands return exit 2 with diagnostics only on stderr");
        var literal = CliOptions.Parse(["chat", "--json", "--", "--project", "literal prompt"]);
        Check(literal.LiteralArguments && literal.Arguments.SequenceEqual(["--project", "literal prompt"]),
            "CLI -- preserves option-like prompt content");
        var direct = CliOptions.Parse(["fix", "the", "build"]);
        Check(direct.Command == "chat" && direct.Arguments.SequenceEqual(["fix", "the", "build"]),
            "CLI treats an unknown first positional as a direct chat prompt");
        var flags = CliOptions.Parse(["-c", "-y", "--plan", "-m", "provider/model", "review", "this"]);
        Check(flags.Continue && flags.AutoApprove && flags.PermissionMode == "plan" &&
            flags.ModelOverride == "provider/model" && flags.Command == "chat",
            "CLI parses continue, auto-approve, plan and model override together");
        var resume = CliOptions.Parse(["-r"]);
        Check(resume.Continue && resume.SessionId is null,
            "CLI -r without an id resumes the most recent conversation");
    }

    /// <summary>状态总览只读、会话改名不依赖外部后端运行时。</summary>
    private static async Task TestStatusAndRenameAsync(string root)
    {
        var layout = new AppDataLayout(Path.Combine(root, "tmp", "cli-status", Guid.NewGuid().ToString("N")));
        var project = Path.Combine(layout.Root, "project");
        Directory.CreateDirectory(project);
        var metadata = new DesktopMetadataStore(layout);
        var record = metadata.CreateSession(metadata.GetOrCreateProject(project), "before", BackendId.NativeCSharp);

        var oldOut = Console.Out; var oldError = Console.Error;
        try
        {
            using var output = new StringWriter();
            Console.SetOut(output);
            var statusCode = await Fufucode.Cli.Program.Main(["status", "--json", "--project", project,
                "--app-data", layout.Root]);
            Console.SetOut(oldOut);
            var statusJson = output.ToString();
            output.GetStringBuilder().Clear();
            Check(statusCode == 0, "CLI status exits 0 for the default native backend");
            using (var status = JsonDocument.Parse(statusJson))
            {
                Check(status.RootElement.GetProperty("backend").GetProperty("preferred").GetString() == "fumo" &&
                    status.RootElement.GetProperty("backend").GetProperty("available").GetBoolean() &&
                    status.RootElement.GetProperty("sessions").GetProperty("count").GetInt32() == 1,
                    "CLI status reports backend availability and project sessions without opening one");
            }

            Console.SetOut(output);
            var renameCode = await Fufucode.Cli.Program.Main(["sessions", "rename", record.Id, "after", "--project", project,
                "--app-data", layout.Root, "--json"]);
            Console.SetOut(oldOut);
            Check(renameCode == 0, "CLI sessions rename succeeds for a closed session");
            Check(metadata.GetSession(record.Id).Name == "after",
                "CLI sessions rename persists the new name without starting the backend");

            output.GetStringBuilder().Clear();
            Console.SetOut(output);
            var commandsCode = await Fufucode.Cli.Program.Main(["commands", "--project", project,
                "--app-data", layout.Root, "--json"]);
            Console.SetOut(oldOut);
            var commandsJson = output.ToString();
            Check(commandsCode == 0, "CLI commands lists the native tool surface without a model call");
            using (var commands = JsonDocument.Parse(commandsJson))
            {
                var names = commands.RootElement.EnumerateArray()
                    .Select(command => command.GetProperty("name").GetString())
                    .ToArray();
                Check(names.Contains("read_file") && names.Contains("update_plan"),
                    "CLI commands reports builtin tools and the plan tool");
            }
        }
        finally { Console.SetOut(oldOut); Console.SetError(oldError); }
    }

    /// <summary>默认后端 FUMO 的历史是 .json，删除必须接受它，并按 fumo/ 租约目录拒绝活会话。</summary>
    private static async Task TestNativeSessionDeletionAsync(string root)
    {
        var layout = new AppDataLayout(Path.Combine(root, "tmp", "cli-native-delete", Guid.NewGuid().ToString("N")));
        var project = Path.Combine(layout.Root, "project");
        Directory.CreateDirectory(project);
        var manager = new PersistentSessionManager(string.Empty, string.Empty, layout);
        string sessionId; string sessionFile;
        await using (var live = await manager.CreateAsync(project, backend: BackendId.NativeCSharp))
        {
            sessionId = live.Session.Id;
            sessionFile = live.Session.FilePath!;
            await ExpectAsync<InvalidOperationException>(() =>
                CliSessionCommands.DeleteAsync(layout, project, sessionId, true, TextWriter.Null));
            Check(manager.Metadata.GetSession(sessionId).Id == sessionId,
                "CLI sessions delete refuses a live FUMO session lease");
        }
        // 新会话可能尚未落盘；写入一个合法 JSON 历史后再验证删除确实移除 .json 文件。
        Directory.CreateDirectory(Path.GetDirectoryName(sessionFile)!);
        File.WriteAllText(sessionFile, "[]");
        await CliSessionCommands.DeleteAsync(layout, project, sessionId, true, TextWriter.Null);
        var projectRecord = manager.Metadata.GetOrCreateProject(project);
        Check(!File.Exists(sessionFile) &&
            manager.Metadata.ListSessions(projectRecord.Id).All(s => s.Id != sessionId),
            "CLI sessions delete removes the FUMO .json history and metadata");
    }

    /// <summary>FUMO 离线报告、离线查看/导出、--new/--backend 与后端选择。</summary>
    private static async Task TestSessionSurfaceAsync(string root)
    {
        var layout = new AppDataLayout(Path.Combine(root, "tmp", "cli-session-surface",
            Guid.NewGuid().ToString("N")));
        var project = Path.Combine(layout.Root, "project");
        Directory.CreateDirectory(project);
        var metadata = new DesktopMetadataStore(layout);
        var record = metadata.CreateSession(metadata.GetOrCreateProject(project), "offline",
            BackendId.NativeCSharp);
        var file = Path.Combine(layout.ProjectSessionsDirectory(record.ProjectId), "fumo", record.Id + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var messages = new List<PureAgentMessage>
        {
            new(PureAgentRole.User, "hello"),
            new(PureAgentRole.Assistant, "world", Usage: new PureUsage(10, 5, 15))
        };
        File.WriteAllText(file, JsonSerializer.Serialize(new
        {
            version = 2, messages, modelId = "test-model", thinkingLevel = "high", executionMode = "plan"
        }));
        metadata.UpdateSessionFile(record.Id, file);

        var report = ReadOnlySessionReport.Load(layout, project, record.Id);
        Check(report.History.Entries.Count == 2 && report.ModelId == "test-model" &&
            report.ThinkingLevel == "high" && report.ExecutionMode == "plan",
            "ReadOnlySessionReport parses FUMO .json history and session settings");
        Check(SessionUsageQuery.QueryHistory(report.History).Session.TotalTokens.Value == 15,
            "FUMO offline usage keeps the recorded token total");

        var oldOut = Console.Out; var oldError = Console.Error;
        try
        {
            async Task<(int Code, string Out)> Run(params string[] args)
            {
                using var writer = new StringWriter();
                Console.SetOut(writer);
                try { return (await Fufucode.Cli.Program.Main(args), writer.ToString()); }
                finally { Console.SetOut(oldOut); }
            }

            var usage = await Run("usage", "show", "--project", project, "--app-data", layout.Root, "--json");
            Check(usage.Code == 0, "CLI usage show works for the default FUMO backend");
            using (var usageJson = JsonDocument.Parse(usage.Out))
                Check(usageJson.RootElement.GetProperty("report").GetProperty("session")
                    .GetProperty("totalTokens").GetProperty("value").GetInt64() == 15,
                    "CLI usage show reports the FUMO token total");

            var shown = await Run("sessions", "show", record.Id, "--offline", "--project", project,
                "--app-data", layout.Root, "--json");
            Check(shown.Code == 0, "CLI sessions show defaults to offline and does not take a lease");
            using (var shownJson = JsonDocument.Parse(shown.Out))
                Check(shownJson.RootElement.GetProperty("backend").GetString() == "fumo" &&
                    shownJson.RootElement.GetProperty("model").GetString() == "test-model" &&
                    shownJson.RootElement.GetProperty("summary").GetArrayLength() == 2,
                    "CLI sessions show reports offline FUMO entries and model");

            var markdown = await Run("sessions", "export", record.Id, "--format", "md",
                "--project", project, "--app-data", layout.Root);
            Check(markdown.Code == 0 && markdown.Out.Contains("hello") && markdown.Out.Contains("world"),
                "CLI sessions export writes Markdown text to stdout");

            var exportPath = Path.Combine(layout.Root, "session-export.json");
            var exported = await Run("sessions", "export", record.Id, "--format", "json", "-o", exportPath,
                "--project", project, "--app-data", layout.Root, "--json");
            Check(exported.Code == 0 && File.Exists(exportPath),
                "CLI sessions export writes JSON to a new file");
            using (var exportedJson = JsonDocument.Parse(File.ReadAllText(exportPath)))
                Check(exportedJson.RootElement.GetProperty("messages").GetArrayLength() == 2,
                    "CLI sessions JSON export contains the offline branch messages");

            var backends = await Run("backends", "list", "--app-data", layout.Root, "--json");
            Check(backends.Code == 0 && backends.Out.Contains("\"fumo\"") &&
                backends.Out.Contains("\"available\":true"),
                "CLI backends list reports the default native backend");
            var use = await Run("backends", "use", "fumo", "--app-data", layout.Root, "--json");
            Check(use.Code == 0 && use.Out.Contains("\"saved\":true"),
                "CLI backends use persists the default backend");

            var parsed = CliOptions.Parse(["--new", "--backend", "fumo", "hello"]);
            Check(parsed.NewSession && parsed.Backend == BackendId.NativeCSharp && parsed.Command == "chat",
                "CLI parses --new and --backend for a fresh conversation");
            await ExpectAsync<ArgumentException>(() =>
            {
                CliOptions.Parse(["--new", "--session", "abc", "hello"]);
                return Task.CompletedTask;
            });
            await ExpectAsync<ArgumentException>(() =>
            {
                CliOptions.Parse(["--backend", "pi", "--session", "abc", "hello"]);
                return Task.CompletedTask;
            });
            Console.WriteLine("PASS CLI rejects --new with --session and --backend with --session");
        }
        finally { Console.SetOut(oldOut); Console.SetError(oldError); }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Console.WriteLine("PASS " + message);
    }

    private static async Task ExpectAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }

    private sealed class GateReader : TextReader
    {
        private readonly ManualResetEventSlim _release = new(false);
        public override string? ReadLine() { _release.Wait(); return null; }
        public void Release() => _release.Set();
        protected override void Dispose(bool disposing) { if (disposing) Release(); base.Dispose(disposing); }
    }
}
