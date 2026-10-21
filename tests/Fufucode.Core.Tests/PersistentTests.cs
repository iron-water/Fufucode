using Fufucode.Core;
using Fufucode.Pi;
using Fufucode.App.Abstractions;
using Microsoft.Data.Sqlite;

internal static class PersistentTests
{
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        Console.WriteLine("PASS " + name);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Persistent fixture did not settle.");
            await Task.Delay(10);
        }
    }

    public static async Task RunAsync(string root, string fake, string dotnet)
    {
        var scratch = Path.Combine(root, "tmp", "m4-core-tests", Guid.NewGuid().ToString("N"));
        var work = Path.Combine(scratch, "work");
        var otherWork = Path.Combine(scratch, "other-work");
        Directory.CreateDirectory(work);
        Directory.CreateDirectory(otherWork);
        var layout = new AppDataLayout(Path.Combine(scratch, "appdata"));
        var manager = new PersistentSessionManager(dotnet, fake, layout, requireApprovalBridge: false);
        using (var fixture = System.Text.Json.JsonDocument.Parse("""
            {"entries":[
              {"type":"message","id":"u","parentId":null,"message":{"role":"user","content":"first"}},
              {"type":"message","id":"old","parentId":"u","message":{"role":"assistant","content":"abandoned"}},
              {"type":"compaction","id":"c","parentId":"u","summary":"summary","firstKeptEntryId":"c"},
              {"type":"message","id":"new","parentId":"c","message":{"role":"assistant","content":"current"}}
            ],"leafId":"new"}
            """))
        {
            var tree = new SessionHistory();
            tree.ApplyEntriesResponse(fixture.RootElement);
            Check(!tree.Entries[1].OnCurrentBranch && !tree.Entries[1].BeforeCompaction &&
                tree.Entries[0].BeforeCompaction && tree.CurrentBranchMessages.Count == 2,
                "history distinguishes abandoned branch from pre-compaction active history");
        }
        var project = manager.Metadata.GetOrCreateProject(work);
        var same = manager.Metadata.GetOrCreateProject(Path.Combine(work, "."));
        var other = manager.Metadata.GetOrCreateProject(otherWork);
        Check(project.Id == same.Id && project.Id != other.Id, "normalized project path has stable isolated identity");

        var trustWork = Path.Combine(scratch, "trust-work");
        Directory.CreateDirectory(Path.Combine(trustWork, ".pi", "extensions"));
        var undecided = manager.GetProjectTrustStatus(trustWork);
        Check(undecided.RequiresDecision && undecided.ProtectedResources.Single().EndsWith(
            Path.Combine(".pi", "extensions"), StringComparison.OrdinalIgnoreCase),
            "project extension resource requires an explicit desktop trust decision");
        try { await manager.CreateAsync(trustWork); throw new Exception("undecided project opened"); }
        catch (ProjectTrustDecisionRequiredException error)
        {
            Check(error.Status.ProjectPath == trustWork &&
                manager.Metadata.ListSessions(manager.Metadata.GetOrCreateProject(trustWork).Id).Count == 0,
                "undecided project starts no Pi process or session");
        }
        manager.SetProjectTrustDecision(trustWork, ProjectTrustDecision.DoNotTrust);
        await using (var denied = await manager.CreateAsync(trustWork, backend: BackendId.Pi))
        {
            var state = (await denied.Coordinator.QueryStateAsync()).Data!.Value;
            var flags = state.GetProperty("startupArgs").EnumerateArray().Select(value => value.GetString()).ToArray();
            Check(flags.Contains("--no-approve") && flags.Contains("--no-extensions") && !flags.Contains("--approve"),
                "denied project starts with explicit --no-approve and controlled extension discovery");
        }
        var restartedManager = new PersistentSessionManager(dotnet, fake, layout, requireApprovalBridge: false);
        Check(restartedManager.GetProjectTrustStatus(trustWork).Decision == ProjectTrustDecision.DoNotTrust,
            "project trust decision persists across desktop manager restart");
        restartedManager.SetProjectTrustDecision(trustWork, ProjectTrustDecision.Trust);
        await using (var trusted = await restartedManager.CreateAsync(trustWork, backend: BackendId.Pi))
        {
            var state = (await trusted.Coordinator.QueryStateAsync()).Data!.Value;
            var flags = state.GetProperty("startupArgs").EnumerateArray().Select(value => value.GetString()).ToArray();
            Check(flags.Contains("--approve") && flags.Contains("--no-extensions") && !flags.Contains("--no-approve"),
                "trusted project starts with explicit --approve while extension discovery stays managed");
        }

        var ancestor = Path.Combine(scratch, "trust-ancestor");
        var nested = Path.Combine(ancestor, "nested");
        Directory.CreateDirectory(Path.Combine(ancestor, ".agents", "skills"));
        Directory.CreateDirectory(nested);
        Check(manager.GetProjectTrustStatus(nested).RequiresDecision,
            "ancestor project skills also require a trust decision");

        string savedId;
        string savedFile;
        await using (var session = await manager.CreateAsync(work, "fixture", backend: BackendId.Pi))
        {
            savedId = session.Session.Id;
            savedFile = manager.Metadata.GetSession(savedId).FilePath!;
            var state = (await session.Coordinator.QueryStateAsync()).Data!.Value;
            var startupFlags = state.GetProperty("startupArgs").EnumerateArray().Select(value => value.GetString()).ToArray();
            Check(!manager.GetProjectTrustStatus(work).RequiresDecision && startupFlags.Contains("--no-approve"),
                "resource-free project needs no prompt and still starts with a deny override");
            Check(state.GetProperty("agentDirectory").GetString() == layout.LegacyPiAgentDirectory &&
                savedFile.StartsWith(layout.ProjectSessionsDirectory(project.Id) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase) &&
                File.Exists(savedFile), "persistent launch uses private Pi config and session directory");
            Check(session.History.Entries.Count == 0 && session.Coordinator.Snapshot.Messages.Count == 0,
                "new session starts with empty restored history");
            try { await manager.ResumeAsync(savedId); throw new Exception("ownership check missed"); }
            catch (InvalidOperationException error) when (error.Message.Contains("already owned"))
            { Check(true, "session ownership excludes a second writer"); }
            manager.Metadata.SetDraft(savedId, "unsent text");
            manager.Metadata.SetPreference("theme", "dark");
            await session.RenameAsync("renamed fixture");
            Check(manager.Metadata.GetSession(savedId).Name == "renamed fixture", "session name is saved in Pi and metadata");
            await session.Coordinator.SendPromptAsync("persist_compact");
            await WaitFor(() => session.Coordinator.Snapshot.LastResult == TaskResult.Completed);
            await session.RefreshHistoryAsync();
            Check(session.History.Entries.Count == 4 && session.History.Cursor == "e4" &&
                session.History.Entries[0].BeforeCompaction && session.History.Entries[1].BeforeCompaction &&
                !session.History.Entries[3].BeforeCompaction && session.History.Entries.All(e => e.OnCurrentBranch),
                "get_entries preserves pre-compaction history and marks boundary");
            var empty = await session.Coordinator.QueryEntriesAsync(session.History.Cursor);
            Check(empty.Data!.Value.GetProperty("entries").GetArrayLength() == 0,
                "get_entries since cursor excludes previously read entries");
        }
        await using (var resumed = await manager.ResumeAsync(savedId))
        {
            Check(resumed.Session.FilePath == savedFile && resumed.History.Entries.Count == 4 &&
                resumed.Coordinator.Snapshot.Messages.Count == 3 &&
                resumed.Coordinator.Snapshot.Messages[1].Content.OfType<PiTextBlock>().Single().Text == "Before compaction",
                "close and reopen restores full timeline before returning session");
            Check(manager.Metadata.GetDraft(savedId) == "unsent text" && manager.Metadata.GetPreference("theme") == "dark",
                "draft and preference survive reopen without storing message history in SQLite");
            await resumed.Chat.SendAsync("persist_continue");
            await WaitFor(() => resumed.Coordinator.Snapshot.LastResult == TaskResult.Completed);
            await resumed.RefreshHistoryAsync();
            Check(resumed.History.Entries.Count == 6 && resumed.Coordinator.Snapshot.Messages.Count == 5,
                "new work extends recovered timeline exactly once");
        }

        string crashId;
        await using (var crashed = await manager.CreateAsync(otherWork, backend: BackendId.Pi))
        {
            crashId = crashed.Session.Id;
            try { await crashed.Coordinator.SendPromptAsync("persist_crash"); }
            catch (RpcException) { }
            await WaitFor(() => crashed.Coordinator.Snapshot.Connection == ConnectionState.Faulted);
        }
        await using (var recovered = await manager.ResumeAsync(crashId))
        {
            var state = (await recovered.Coordinator.QueryStateAsync()).Data!.Value;
            Check(recovered.Coordinator.Snapshot.LastResult == TaskResult.Interrupted &&
                recovered.Chat.LastOutcome == SessionOutcome.Interrupted && recovered.Chat.CanSend &&
                recovered.History.CurrentBranchMessages.Single().Role == "user" &&
                state.GetProperty("promptCount").GetInt32() == 0,
                "interrupted recovery does not replay the last prompt");
        }

        string toolCrashId;
        await using (var crashed = await manager.CreateAsync(otherWork, backend: BackendId.Pi))
        {
            toolCrashId = crashed.Session.Id;
            try { await crashed.Coordinator.SendPromptAsync("persist_crash_after_assistant"); }
            catch (RpcException) { }
            await WaitFor(() => crashed.Coordinator.Snapshot.Connection == ConnectionState.Faulted);
            Check(manager.Metadata.GetSession(toolCrashId).WasActive,
                "process crash preserves durable active marker after assistant tool call");
        }
        await using (var recovered = await manager.ResumeAsync(toolCrashId))
        {
            Check(recovered.Chat.LastOutcome == SessionOutcome.Interrupted &&
                recovered.History.CurrentBranchMessages.Last().Role == "assistant",
                "unfinished tool turn restores as interrupted without re-execution");
            var state = (await recovered.Coordinator.QueryStateAsync()).Data!.Value;
            Check(state.GetProperty("promptCount").GetInt32() == 0,
                "tool turn recovery sends no prompt");
        }

        // One query returns complete rows, and persisted ISO timestamps parse across cultures.
        var extra = manager.Metadata.CreateSession(project, "culture fixture");
        var priorCulture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("fr-FR");
            var listed = manager.Metadata.ListSessions(project.Id);
            Check(listed.Any(s => s.Id == extra.Id && s.LastOpened == extra.LastOpened) &&
                listed.Any(s => s.Id == savedId), "P2-15 session listing reads full rows with invariant timestamps");
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = priorCulture; }

        await using (var slowMetadata = await manager.CreateAsync(work, backend: BackendId.Pi))
        {
            using var blocker = new SqliteConnection($"Data Source={layout.DatabasePath}");
            blocker.Open();
            using (var lockCommand = blocker.CreateCommand())
            {
                lockCommand.CommandText = "BEGIN EXCLUSIVE";
                lockCommand.ExecuteNonQuery();
            }
            var timer = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                await slowMetadata.Coordinator.SendPromptAsync("stream");
                await WaitFor(() => slowMetadata.Coordinator.Snapshot.LastResult == TaskResult.Completed);
                Check(timer.Elapsed < TimeSpan.FromSeconds(2),
                    "P2-6 RPC events settle while SQLite metadata writer is blocked");
            }
            finally
            {
                using var unlock = blocker.CreateCommand();
                unlock.CommandText = "COMMIT";
                unlock.ExecuteNonQuery();
            }
        }

        // Simulate a v1 database to prove migration keeps data and makes a pre-change backup.
        using (var connection = new SqliteConnection($"Data Source={layout.DatabasePath}"))
        {
            connection.Open();
            using var change = connection.CreateCommand();
            change.CommandText = "ALTER TABLE sessions DROP COLUMN last_opened_utc; PRAGMA user_version=1";
            change.ExecuteNonQuery();
        }
        var migrated = new DesktopMetadataStore(layout);
        // The backup name carries the schema being migrated to; schema 3 adds fork lineage columns.
        var backups = Directory.GetFiles(layout.Root, $"desktop.db.before-v{DesktopMetadataStore.SchemaVersion}-*.bak");
        Check(migrated.GetProject(project.Id).Id == project.Id && migrated.GetDraft(savedId) == "unsent text" &&
            backups.Length == 1,
            "metadata v1 migration preserves rows and backs up database first");
        using (var backup = new SqliteConnection($"Data Source={backups[0]};Mode=ReadOnly"))
        {
            backup.Open();
            using var version = backup.CreateCommand();
            version.CommandText = "PRAGMA user_version";
            Check(Convert.ToInt32(version.ExecuteScalar()) == 1, "pre-migration backup retains the old schema");
        }
    }

    public static async Task RunRealAsync(string root, string node, string cli)
    {
        var scratch = Path.Combine(root, "tmp", "m4-real-pi", Guid.NewGuid().ToString("N"));
        var work = Path.Combine(scratch, "work");
        Directory.CreateDirectory(work);
        var manager = new PersistentSessionManager(node, cli, new AppDataLayout(Path.Combine(scratch, "appdata")));
        if (Environment.GetEnvironmentVariable("FUFUCODE_TEST_SHUSHENG_API_KEY") is { Length: > 0 })
        {
            Directory.CreateDirectory(manager.Layout.AgentDirectory);
            File.WriteAllText(Path.Combine(manager.Layout.LegacyPiAgentDirectory, "models.json"),
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    providers = new Dictionary<string, object>
                    {
                        ["shusheng"] = new
                        {
                            baseUrl = "https://chat.intern-ai.org.cn/api/v1",
                            api = "openai-completions",
                            apiKey = "$FUFUCODE_TEST_SHUSHENG_API_KEY",
                            models = new[] { new { id = "intern-s2-preview-397b", name = "intern-s2-preview-397b",
                                reasoning = false, input = new[] { "text" }, contextWindow = 1000000, maxTokens = 328000 } }
                        }
                    }
                }));
            File.WriteAllText(Path.Combine(manager.Layout.AgentDirectory, "settings.json"),
                "{\"defaultProvider\":\"shusheng\",\"defaultModel\":\"intern-s2-preview-397b\"}");
        }
        string sessionId;
        string sessionFile;
        var modelTurnCompleted = false;
        await using (var session = await manager.CreateAsync(work, "M4 smoke", backend: BackendId.Pi))
        {
            Check(session.Coordinator.ApprovalAvailability == ApprovalAvailability.Verified,
                "persistent manager loads and verifies the built-in approval bridge");
            sessionId = session.Session.Id;
            var state = (await session.Coordinator.QueryStateAsync()).Data!.Value;
            sessionFile = state.GetProperty("sessionFile").GetString()!;
            Check(sessionFile.StartsWith(manager.Layout.ProjectSessionsDirectory(session.Project.Id) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase) &&
                session.History.CurrentBranchMessages.Count == 0,
                "real Pi selects the private session directory without conversation messages");
            Console.WriteLine($"Real Pi initial session file exists: {File.Exists(sessionFile)}");
            await session.RenameAsync("M4 smoke");
            Check(manager.Metadata.GetSession(sessionId).Name == "M4 smoke", "real Pi accepts a session display name");
            var configured = state.TryGetProperty("model", out var model) && model.ValueKind == System.Text.Json.JsonValueKind.Object;
            Console.WriteLine($"Private Pi model configured: {configured}");
            if (configured && Environment.GetEnvironmentVariable("FUFUCODE_REAL_PERSISTENCE_MODEL_TEST") == "1")
            {
                try
                {
                    await session.Coordinator.SendPromptAsync("Reply with exactly: PERSISTED");
                    var until = DateTime.UtcNow + TimeSpan.FromMinutes(2);
                    while (session.Coordinator.Snapshot.LastResult == TaskResult.Unknown && DateTime.UtcNow < until)
                        await Task.Delay(100);
                    Check(session.Coordinator.Snapshot.LastResult == TaskResult.Completed,
                        "real persistent model turn settled");
                    await session.RefreshHistoryAsync();
                    modelTurnCompleted = true;
                    Check(File.Exists(sessionFile) && session.History.CurrentBranchMessages.Count >= 2,
                        "real Pi wrote a recoverable conversation file");
                }
                catch (RpcException error) when (error.Message.Contains("No API key found", StringComparison.Ordinal))
                {
                    Console.WriteLine("SKIP real persistent model turn: private Pi config has no API key");
                }
            }
        }
        await using (var resumed = await manager.ResumeAsync(sessionId))
        {
            var state = (await resumed.Coordinator.QueryStateAsync()).Data!.Value;
            Check(state.GetProperty("sessionId").GetString() == sessionId &&
                state.GetProperty("sessionFile").GetString()!.StartsWith(manager.Layout.ProjectSessionsDirectory(resumed.Project.Id) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase) &&
                resumed.History.CurrentBranchMessages.Count == resumed.Coordinator.Snapshot.Messages.Count,
                "real Pi reopens the same session ID and restores its available entries");
            if (modelTurnCompleted)
            {
                Check(state.GetProperty("sessionFile").GetString() == sessionFile &&
                    resumed.History.CurrentBranchMessages.Count >= 2,
                    "real model conversation survives process restart in the same file");
                var previousCount = resumed.History.CurrentBranchMessages.Count;
                await resumed.Coordinator.SendPromptAsync("Reply with exactly: RESUMED");
                var until = DateTime.UtcNow + TimeSpan.FromMinutes(2);
                while (resumed.Coordinator.Snapshot.LastResult == TaskResult.Unknown && DateTime.UtcNow < until)
                    await Task.Delay(100);
                Check(resumed.Coordinator.Snapshot.LastResult == TaskResult.Completed,
                    "real model can continue after reopening the session");
                await resumed.RefreshHistoryAsync();
                Check(resumed.History.CurrentBranchMessages.Count >= previousCount + 2 &&
                    File.Exists(sessionFile), "continued turn is persisted in the original session file");
            }
        }
    }
}
