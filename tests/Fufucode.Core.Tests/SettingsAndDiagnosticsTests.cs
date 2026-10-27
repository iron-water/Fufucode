using System.Text.Json;
using Fufucode.Core;
using Fufucode.Pi;

internal static class SettingsAndDiagnosticsTests
{
    private sealed class FixtureCredential : ICredentialResolver
    {
        public ValueTask<string?> ResolveAsync(string name, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<string?>(name == "FUFUCODE_TEST_KEY" ? "private-fixture-key" : null);
    }
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        Console.WriteLine("PASS " + name);
    }

    /// <summary>设置存储的独立回归（不启动任何后端），也供 --settings-only 单跑。</summary>
    public static async Task<AppDataLayout> RunSettingsAsync(string root)
    {
        var layout = new AppDataLayout(Path.Combine(root, "tmp", "m6-core-tests", Guid.NewGuid().ToString("N")));
        var store = new AppSettingsStore(layout);
        var changed = 0;
        store.Changed += _ => changed++;
        store.Update(s => s with { SelectedModel = "fixture/fixture-model", ThinkingLevel = "medium", Theme = "dark",
            ProxyUrl = "http://localhost:7890", Endpoint = new CompatibleEndpoint("fixture", "https://example.test/v1",
                "fixture-model", "FUFUCODE_TEST_KEY") });
        var loaded = new AppSettingsStore(layout);
        Check(changed == 1 && loaded.Current.SelectedModel == "fixture/fixture-model" &&
            loaded.Current.ThinkingLevel == "medium" && loaded.Current.Theme == "dark",
            "M6 settings persist and notify once");
        try { loaded.Update(s => s with { ProxyUrl = "http://user:secret@localhost:7890" });
            throw new Exception("credential-bearing proxy URL was accepted"); }
        catch (ArgumentException) { }
        Check(!File.ReadAllText(Path.Combine(layout.Root, "settings.json")).Contains("secret", StringComparison.Ordinal),
            "M6 settings reject credential-bearing URLs");
        var piJson = loaded.BuildPiModelsJson()!;
        using (var pi = JsonDocument.Parse(piJson))
            Check(pi.RootElement.GetProperty("providers").GetProperty("fixture").GetProperty("apiKey").GetString() ==
                "$FUFUCODE_TEST_KEY" && !piJson.Contains("actual-secret", StringComparison.Ordinal),
                "M6 compatible endpoint exports Pi models.json shape without a key value");
        var path = Path.Combine(layout.Root, "settings.json");
        if (OperatingSystem.IsWindows())
        {
            using (var blocker = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var update = Task.Run(() => store.Update(s => s with { Theme = "light" }));
                await Task.Delay(120);
                blocker.Dispose();
                await update;
            }
            Check(store.Current.Theme == "light" && new AppSettingsStore(layout).Current.Theme == "light",
                "R-008 transient Windows rename contention retries atomic settings replacement");
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
            try
            {
                try { store.Update(s => s with { Theme = "dark" }); throw new Exception("Read-only settings were overwritten."); }
                catch (UnauthorizedAccessException) { }
                Check(store.Current.Theme == "light" && new AppSettingsStore(layout).Current.Theme == "light" &&
                    Directory.GetFiles(layout.Root, "settings.json.tmp-*").Length == 0,
                    "R-008 permanent read-only failure preserves disk and memory and removes temporary files");
            }
            finally { File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly); }
            // 读文件时的独占占用：短暂占用应由重试恢复，长时间占用应退回上次成功读取的快照。
            using (var lockFile = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var pendingLoad = Task.Run(() => new AppSettingsStore(layout));
                await Task.Delay(80);
                lockFile.Dispose();
                var recovered = await pendingLoad;
                Check(recovered.LastLoadError is null && !recovered.UsedLastGood &&
                    recovered.Current.Endpoint?.ProviderName == "fixture" && recovered.Current.Theme == "light",
                    "R-009 transient settings read contention retries instead of dropping the endpoint");
            }
            AppSettingsStore lockedLoad;
            using (var lockFile = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
                lockedLoad = new AppSettingsStore(layout);
            Check(lockedLoad.LastLoadError is not null && lockedLoad.UsedLastGood &&
                lockedLoad.Current.Endpoint?.ProviderName == "fixture" && lockedLoad.Current.Theme == "light",
                "R-009 settings unreadable at startup fall back to the last good snapshot, not defaults");
            Check(lockedLoad.ReloadIfFailed() && !lockedLoad.UsedLastGood && lockedLoad.LastLoadError is null,
                "R-009 settings reload after contention recovers without restarting");
        }
        File.WriteAllText(path, """{"model":"legacy","thinking":"high","theme":"light"}""");
        Check(new AppSettingsStore(layout).Current is { SelectedModel: "legacy", ThinkingLevel: "high", Theme: "light" },
            "M6 version-zero settings migrate in memory");
        // 让磁盘与兜底快照都回到带端点的状态，验证主文件损坏时端点不会丢。
        loaded.Update(s => s with { Theme = "light" });
        File.WriteAllText(path, "{broken-json");
        var corrupt = new AppSettingsStore(layout);
        Check(corrupt.LastLoadError is not null && corrupt.UsedLastGood &&
            corrupt.Current.Endpoint?.ProviderName == "fixture" && corrupt.Current.Theme == "light" &&
            File.ReadAllText(path) == "{broken-json",
            "M6 corrupt settings fall back to the last good snapshot without destroying source");
        corrupt.Update(s => s with { Theme = "dark" });
        var repaired = new AppSettingsStore(layout);
        Check(Directory.GetFiles(layout.Root, "settings.json.before-repair-*.bak").Length == 1 &&
            repaired.Current.Theme == "dark" && repaired.Current.Endpoint?.ProviderName == "fixture",
            "M6 repairing corrupt settings keeps a source backup and the endpoint");
        File.WriteAllText(path, """{"version":99,"theme":"dark"}""");
        var newer = new AppSettingsStore(layout);
        try { newer.Update(s => s with { Theme = "light" }); throw new Exception("newer settings overwrite was allowed"); }
        catch (InvalidOperationException) { }
        Check(File.ReadAllText(path).Contains("\"version\":99", StringComparison.Ordinal),
            "M6 newer settings version cannot be overwritten");
        return layout;
    }

    public static async Task RunAsync(string root, string fake, string dotnet)
    {
        var layout = await RunSettingsAsync(root);

        var sessionLayout = new AppDataLayout(Path.Combine(root, "tmp", "m6-session-settings", Guid.NewGuid().ToString("N")));
        var projectPath = Path.Combine(sessionLayout.Root, "project");
        Directory.CreateDirectory(projectPath);
        var manager = new PersistentSessionManager(dotnet, fake, sessionLayout,
            requireApprovalBridge: false, credentialResolver: new FixtureCredential());
        manager.Settings.Update(s => s with { SelectedModel = "fixture/fixture-model", ThinkingLevel = "medium",
            ProxyUrl = "http://localhost:7890", Endpoint = new CompatibleEndpoint("fixture",
                "https://example.test/v1", "fixture-model", "FUFUCODE_TEST_KEY") });
        string sessionId;
        await using (var session = await manager.CreateAsync(projectPath, backend: BackendId.Pi))
        {
            sessionId = session.Session.Id;
            Check(session.Chat.CurrentModel == "fixture/fixture-model",
                "M6 new session adapter exposes the model selected after startup");
            var state = (await session.Coordinator.QueryStateAsync()).Data!.Value;
            Check(state.GetProperty("model").GetProperty("provider").GetString() == "fixture" &&
                state.GetProperty("model").GetProperty("id").GetString() == "fixture-model" &&
                state.GetProperty("thinkingLevel").GetString() == "medium" &&
                state.GetProperty("proxy").GetString() == "http://localhost:7890" &&
                state.GetProperty("fixtureKeyPresent").GetBoolean(),
                "M6 new session applies model, thinking, proxy and resolved endpoint key");
            var models = File.ReadAllText(Path.Combine(sessionLayout.LegacyPiAgentDirectory, "models.json"));
            Check(models.Contains("$FUFUCODE_TEST_KEY", StringComparison.Ordinal) &&
                !models.Contains("private-fixture-key", StringComparison.Ordinal),
                "M6 Pi models file references the process variable without writing its value");
        }
        manager.Settings.Update(s => s with { SelectedModel = "other/changed", ThinkingLevel = "high" });
        await using (var resumed = await manager.ResumeAsync(sessionId))
        {
            var state = (await resumed.Coordinator.QueryStateAsync()).Data!.Value;
            Check(!state.GetProperty("commandOrder").EnumerateArray().Any(x => x.GetString() == "set_model"),
                "M6 resume does not replace the session model from current app preferences");
        }
        manager.Settings.Update(s => s with { SelectedModel = null, ThinkingLevel = null, Endpoint = null });
        await using (var withoutEndpoint = await manager.CreateAsync(projectPath, backend: BackendId.Pi))
        {
            var models = File.ReadAllText(Path.Combine(sessionLayout.LegacyPiAgentDirectory, "models.json"));
            using var parsed = JsonDocument.Parse(models);
            Check(!parsed.RootElement.GetProperty("providers").TryGetProperty("fixture", out _) &&
                !File.Exists(Path.Combine(sessionLayout.AgentDirectory, "fufucode-provider.txt")),
                "M6 removing endpoint removes only its owned Pi provider");
        }

        var marker = "sensitive-prompt-42";
        string logPath;
        await using (var log = new DiagnosticLog(layout))
        {
            logPath = log.FilePath;
            log.Record(new PiDiagnosticEvent(DateTimeOffset.UtcNow, marker, marker, marker, 1, null, marker));
            await using var client = await PiRpcClient.StartAsync(new PiRpcOptions(dotnet, [fake], root,
                DiagnosticSink: log));
            await client.RequestAsync("prompt", JsonSerializer.SerializeToElement(new { message = marker }));
            var liveDeadline = DateTimeOffset.UtcNow.AddSeconds(5);
            IReadOnlyList<PiDiagnosticEvent> live = [];
            while (DateTimeOffset.UtcNow < liveDeadline)
            {
                live = await DiagnosticExport.ReadRecentAsync(logPath);
                if (live.Any(item => item.EventType == "request_completed")) break;
                await Task.Delay(20);
            }
            Check(live.Any(item => item.EventType == "request_completed"),
                "production trace metadata is readable before the live diagnostic writer is disposed");
        }
        var text = File.ReadAllText(logPath);
        Check(!text.Contains(marker, StringComparison.Ordinal) && text.Contains("request_completed", StringComparison.Ordinal) &&
            text.Contains("requestId", StringComparison.Ordinal),
            "M6 diagnostic log records command metadata and redacts payload-like labels");
        await using var exported = new MemoryStream();
        var exportedCount = await DiagnosticExport.WriteJsonLinesAsync(layout, exported);
        var exportText = System.Text.Encoding.UTF8.GetString(exported.ToArray());
        Check(exportedCount > 0 && !exportText.Contains(marker, StringComparison.Ordinal) &&
            exportText.Contains("request_completed", StringComparison.Ordinal),
            "M7 diagnostic export contains only sanitized metadata");
        File.WriteAllText(Path.Combine(layout.Root, "auth.json"), marker);
        await using var bundle = new MemoryStream();
        var bundleCount = await DiagnosticExport.WriteArchiveAsync(layout, bundle, "test");
        bundle.Position = 0;
        using var archive = new System.IO.Compression.ZipArchive(bundle, System.IO.Compression.ZipArchiveMode.Read);
        using var bundleReader = new StreamReader(archive.GetEntry("diagnostics.jsonl")!.Open());
        var bundleText = await bundleReader.ReadToEndAsync();
        Check(bundleCount == exportedCount && archive.Entries.Count == 2 &&
            archive.GetEntry("system.json") is not null && !bundleText.Contains(marker, StringComparison.Ordinal),
            "production diagnostic ZIP contains system metadata and sanitized logs only, excludes credential files");
        File.AppendAllText(logPath, "\n{broken-json\n" + JsonSerializer.Serialize(new PiDiagnosticEvent(DateTimeOffset.UtcNow,
            marker, marker, marker, 1, null, marker), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }) + "\n");
        var recent = await DiagnosticExport.ReadRecentAsync(logPath, 2);
        Check(recent.Count == 2 && recent.Last().EventType == "other" && recent.Last().Command == "other" && recent.Last().RequestId is null,
            "production trace tail is bounded, skips malformed records and sanitizes imported labels");
        if (OperatingSystem.IsWindows())
        {
            var vault = new WindowsPasswordVaultCredentialResolver();
            var credentialName = "FUFUCODE_ACCEPTANCE_" + Guid.NewGuid().ToString("N");
            try
            {
                vault.Save(credentialName, "non-secret-acceptance-value");
                Check(vault.ListNames().Contains(credentialName) &&
                    await vault.ResolveAsync(credentialName) == "non-secret-acceptance-value",
                    "production credential manager enumerates owned names and resolves saved credentials");
                vault.Delete(credentialName);
                Check(!vault.ListNames().Contains(credentialName) && await vault.ResolveAsync(credentialName) is null,
                    "production credential removal leaves no owned entry and falls back to absent environment");
            }
            finally { vault.Delete(credentialName); }
        }
    }
}
