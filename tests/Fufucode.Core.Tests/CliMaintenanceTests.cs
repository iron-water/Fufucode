using System.Text.Json;
using Fufucode.Cli;
using Fufucode.Core;

internal static class CliMaintenanceTests
{
    public static async Task RunAsync(string root, string fake, string dotnet)
    {
        var scratch = Path.Combine(root, "tmp", "cli-maintenance", Guid.NewGuid().ToString("N"));
        var project = Path.Combine(scratch, "project");
        var other = Path.Combine(scratch, "other");
        Directory.CreateDirectory(project);
        Directory.CreateDirectory(other);
        var extension = Path.Combine(scratch, "extension.ts");
        File.WriteAllText(extension, "export default function() {};");
        var layout = new AppDataLayout(Path.Combine(scratch, "data"));
        var manager = new PersistentSessionManager(dotnet, fake, layout,
            Path.Combine(root, "extensions", "fufucode-bridge"), requireApprovalBridge: false);
        var projectRecord = manager.Metadata.GetOrCreateProject(project);
        string EnabledKey() => "app.enabled-extensions:" + projectRecord.Id;
        string AddedKey() => "app.added-extensions:" + projectRecord.Id;
        string ErrorsKey() => "app.extension-errors:" + projectRecord.Id;
        async Task<string> Extensions(string[] arguments, string? sessionId = null)
        {
            using var output = new StringWriter();
            Check(await CliMaintenanceCommands.RunExtensionsAsync(layout, project, arguments, true, output,
                manager, sessionId) == 0, "maintenance success exit code");
            return output.ToString();
        }

        manager.Metadata.SetPreference(ErrorsKey(), JsonSerializer.Serialize(new Dictionary<string, string>
            { [extension] = "old recorded startup error" }));
        var enabledJson = await Extensions(["enable", extension]);
        using var enabled = JsonDocument.Parse(enabledJson);
        var sessionId = enabled.RootElement.GetProperty("sessionId").GetString()!;
        Check(enabled.RootElement.GetProperty("enabled").GetBoolean() &&
            manager.Metadata.GetPreference(EnabledKey())!.Contains(extension.Replace("\\", "\\\\"), StringComparison.Ordinal),
            "CLI enable saves the project selection after a fresh FakePi session succeeds");
        Check(!ProjectExtensionService.List(layout, project).Single(item => item.Path == extension).HasRecordedError,
            "successful FakePi extension validation clears that extension's persisted old error");
        ProjectExtensionService.ApplyToManager(manager, project);
        await using (var session = await manager.ResumeAsync(sessionId))
        {
            var state = (await session.Coordinator.QueryStateAsync()).Data!.Value;
            var startup = state.GetProperty("startupArgs").EnumerateArray().Select(value => value.GetString()).ToArray();
            Check(startup.Contains(extension) && startup.Contains("--no-extensions"),
                "CLI enable preference is applied to future Pi processes");
            var saved = manager.Metadata.GetPreference(EnabledKey());
            await ExpectAsync<InvalidOperationException>(() => Extensions(["disable", extension], sessionId));
            Check(manager.Metadata.GetPreference(EnabledKey()) == saved && manager.UserExtensionPaths.Contains(extension),
                "session lease failure preserves persisted and manager extension selection");
        }

        using (var disabled = JsonDocument.Parse(await Extensions(["disable", extension], sessionId)))
            Check(!disabled.RootElement.GetProperty("enabled").GetBoolean(), "CLI disable returns an explicit disabled result");
        ProjectExtensionService.ApplyToManager(manager, project);
        await using (var session = await manager.ResumeAsync(sessionId))
        {
            var startup = (await session.Coordinator.QueryStateAsync()).Data!.Value.GetProperty("startupArgs")
                .EnumerateArray().Select(value => value.GetString()).ToArray();
            Check(!startup.Contains(extension), "CLI disable removes the extension from future Pi processes");
        }

        var missing = Path.Combine(scratch, "missing.ts");
        manager.Metadata.SetPreference(EnabledKey(), JsonSerializer.Serialize(new[] { missing }));
        manager.Metadata.SetPreference(AddedKey(), JsonSerializer.Serialize(new[] { extension }));
        manager.Metadata.SetPreference(ErrorsKey(), JsonSerializer.Serialize(new Dictionary<string, string>
            { [extension] = "SECRET_API_KEY and private prompt text" }));
        using (var listing = JsonDocument.Parse(await Extensions(["list"])))
        {
            var entries = listing.RootElement.EnumerateArray().ToArray();
            Check(entries.Length == 2 && entries.All(entry => entry.GetProperty("loaded").ValueKind == JsonValueKind.Null) &&
                entries.Any(entry => entry.GetProperty("enabled").GetBoolean() && !entry.GetProperty("exists").GetBoolean()) &&
                !listing.RootElement.GetRawText().Contains("SECRET_API_KEY", StringComparison.Ordinal),
                "CLI extension list reports missing and recorded error flags without leaking messages or claiming loaded status");
        }
        Check(ProjectExtensionService.ApplyToManager(manager, project).SequenceEqual([missing]) && manager.UserExtensionPaths.Count == 0,
            "Core CLI preparation retains missing preference but skips it for the new process");

        var beforeTrust = manager.Metadata.GetPreference(EnabledKey());
        var local = Path.Combine(project, "local.ts");
        File.WriteAllText(local, "export default function() {};");
        await ExpectAsync<ProjectTrustDecisionRequiredException>(() => Extensions(["enable", "local.ts"], sessionId));
        Check(manager.Metadata.GetPreference(EnabledKey()) == beforeTrust && manager.UserExtensionPaths.Count == 0,
            "undecided project-local extension does not save or bypass trust and restores the manager snapshot");
        manager.SetProjectTrustDecision(project, ProjectTrustDecision.DoNotTrust);
        await ExpectAsync<ProjectExtensionNotTrustedException>(() => Extensions(["enable", "local.ts"], sessionId));
        Check(manager.Metadata.GetPreference(EnabledKey()) == beforeTrust, "denied project-local extension leaves preferences unchanged");
        manager.SetProjectTrustDecision(project, ProjectTrustDecision.Trust);
        manager.Metadata.SetPreference(ErrorsKey(), JsonSerializer.Serialize(new Dictionary<string, string>
            { [extension] = "old disabled error", [missing] = "old missing error", [local] = "old local error" }));
        using (var trusted = JsonDocument.Parse(await Extensions(["enable", "local.ts"], sessionId)))
            Check(trusted.RootElement.GetProperty("path").GetString() == local,
                "extension relative paths resolve against the chosen project after an explicit trust decision");
        var afterValidation = ProjectExtensionService.List(layout, project);
        Check(!afterValidation.Single(item => item.Path == local).HasRecordedError &&
            afterValidation.Single(item => item.Path == missing).HasRecordedError &&
            afterValidation.Single(item => item.Path == extension).HasRecordedError,
            "successful validation clears only existing next-selection errors, retaining missing and disabled history");

        var otherProject = manager.Metadata.GetOrCreateProject(other);
        var foreign = manager.Metadata.CreateSession(otherProject);
        var beforeForeign = manager.Metadata.GetPreference(EnabledKey());
        await ExpectAsync<ArgumentException>(() => Extensions(["disable", local], foreign.Id));
        Check(manager.Metadata.GetPreference(EnabledKey()) == beforeForeign,
            "foreign session is rejected before extension preferences can change");
        await ExpectAsync<ArgumentException>(() => Extensions(["enable"]));
        await ExpectAsync<ArgumentException>(() => Extensions(["enable", missing]));
        await ExpectAsync<ArgumentException>(() => Extensions(["disable", ""]));

        var brokenManager = new PersistentSessionManager(Path.Combine(scratch, "missing-executable.exe"), fake,
            layout, requireApprovalBridge: false);
        brokenManager.UserExtensionPaths = [extension];
        var beforeFailure = manager.Metadata.GetPreference(EnabledKey());
        try
        {
            using var output = new StringWriter();
            await CliMaintenanceCommands.RunExtensionsAsync(layout, project, ["disable", local], true,
                output, brokenManager, sessionId);
            throw new Exception("Missing runtime unexpectedly succeeded.");
        }
        catch (Exception error) when (error is not ArgumentException && error.Message != "Missing runtime unexpectedly succeeded.") { }
        Check(manager.Metadata.GetPreference(EnabledKey()) == beforeFailure &&
            brokenManager.UserExtensionPaths.SequenceEqual([extension]),
            "runtime startup failure rolls back the manager snapshot and leaves the selected extensions intact");

        var projectDiscovery = Path.Combine(project, ".pi", "extensions");
        var globalDiscovery = Path.Combine(layout.AgentDirectory, "extensions");
        Directory.CreateDirectory(projectDiscovery);
        Directory.CreateDirectory(globalDiscovery);
        var projectFile = Path.Combine(projectDiscovery, "discovered.TS");
        var projectIndex = Path.Combine(projectDiscovery, "index-package");
        var projectDeclared = Path.Combine(projectDiscovery, "declared-package");
        var globalFile = Path.Combine(globalDiscovery, "global.js");
        var globalIndex = Path.Combine(globalDiscovery, "global-package");
        var emptyDirectory = Path.Combine(projectDiscovery, "no-entry");
        var malformedPackage = Path.Combine(projectDiscovery, "malformed-package");
        foreach (var directory in new[] { projectIndex, projectDeclared, globalIndex, emptyDirectory, malformedPackage })
            Directory.CreateDirectory(directory);
        File.WriteAllText(projectFile, "export default function() {};");
        File.WriteAllText(globalFile, "export default function() {};");
        File.WriteAllText(Path.Combine(projectIndex, "index.ts"), "export default function() {};");
        File.WriteAllText(Path.Combine(globalIndex, "index.js"), "export default function() {};");
        File.WriteAllText(Path.Combine(projectDeclared, "package.json"), "{\"pi\":{\"extensions\":[\"./entry.ts\"]}}");
        File.WriteAllText(Path.Combine(malformedPackage, "package.json"), "invalid JSON");
        File.WriteAllText(Path.Combine(projectDiscovery, "readme.txt"), "not an extension");
        var discoveredPaths = new[] { projectFile, projectIndex, projectDeclared, globalFile, globalIndex };
        var beforeDiscovery = manager.Metadata.GetPreference(EnabledKey());
        using (var listing = JsonDocument.Parse(await Extensions(["list"])))
        {
            var entries = listing.RootElement.EnumerateArray().ToArray();
            Check(discoveredPaths.All(discoveredPath => entries.Any(entry =>
                    entry.GetProperty("path").GetString() == discoveredPath &&
                    !entry.GetProperty("enabled").GetBoolean() && entry.GetProperty("exists").GetBoolean() &&
                    entry.GetProperty("loaded").ValueKind == JsonValueKind.Null)) &&
                !entries.Any(entry => entry.GetProperty("path").GetString() is { } entryPath &&
                    (entryPath == emptyDirectory || entryPath == malformedPackage || entryPath.EndsWith("readme.txt", StringComparison.Ordinal))),
                "CLI discovers direct ts/js, index directories and pi.extensions in project and global directories as disabled");
        }
        ProjectExtensionService.ApplyToManager(manager, project);
        Check(manager.Metadata.GetPreference(EnabledKey()) == beforeDiscovery &&
            !manager.UserExtensionPaths.Intersect(discoveredPaths).Any(),
            "read-only discovery neither persists nor implicitly enables discovered extensions");
        await using (var session = await manager.ResumeAsync(sessionId))
        {
            var startup = (await session.Coordinator.QueryStateAsync()).Data!.Value.GetProperty("startupArgs")
                .EnumerateArray().Select(value => value.GetString()).ToArray();
            Check(startup.Contains("--no-extensions") && !startup.Intersect(discoveredPaths).Any(),
                "fresh FakePi startup does not load read-only discovered extensions");
        }
        Check(!ProjectExtensionService.List(layout, other).Any(item => item.Path == projectFile || item.Path == local) &&
            ProjectExtensionService.List(layout, other).Any(item => item.Path == globalFile && !item.Enabled),
            "project discovery and extension preferences stay isolated while global discovery is shared");

        var diagnostics = new AppDataLayout(Path.Combine(scratch, "diagnostics"));
        diagnostics.EnsureCreated();
        var unsafeRecord = JsonSerializer.Serialize(new
        {
            timestamp = DateTimeOffset.UtcNow, eventType = "SECRET_API_KEY", requestId = "private-request",
            command = "private-prompt", durationMs = 12, exitCode = 1, errorCode = "SECRET_API_KEY",
            rawPayload = "SECRET_API_KEY private-prompt"
        });
        File.WriteAllText(Path.Combine(diagnostics.LogsDirectory, "app-injected.jsonl"),
            unsafeRecord + "\ninvalid raw SECRET_API_KEY\n" + new string('x', 5000) + "\n");
        File.WriteAllText(Path.Combine(diagnostics.LogsDirectory, "raw.log"), "SECRET_API_KEY");
        using (var output = new StringWriter())
        {
            Check(await CliMaintenanceCommands.RunDiagnosticsAsync(diagnostics, ["export"], true, output) == 0,
                "CLI diagnostics export succeeds");
            using var response = JsonDocument.Parse(output.ToString());
            var path = response.RootElement.GetProperty("path").GetString()!;
            var export = File.ReadAllText(path);
            Check(response.RootElement.GetProperty("records").GetInt32() == 1 &&
                !export.Contains("SECRET_API_KEY", StringComparison.Ordinal) && !export.Contains("private-prompt", StringComparison.Ordinal) &&
                !export.Contains("rawPayload", StringComparison.Ordinal),
                "diagnostics export uses the sanitized schema and excludes raw, malformed, oversized and secret records");
            await ExpectAsync<ArgumentException>(() => CliMaintenanceCommands.RunDiagnosticsAsync(diagnostics,
                ["export", path], true, TextWriter.Null));
            Check(File.ReadAllText(path) == export, "diagnostic export never overwrites an existing destination");
        }
        var forbidden = Path.Combine(diagnostics.LogsDirectory, "app-export.jsonl");
        await ExpectAsync<ArgumentException>(() => CliMaintenanceCommands.RunDiagnosticsAsync(diagnostics,
            ["export", forbidden], false, TextWriter.Null));
        Check(!File.Exists(forbidden), "diagnostic export rejects a destination in its source log directory");
        await ExpectAsync<ArgumentException>(() => CliMaintenanceCommands.RunDiagnosticsAsync(diagnostics,
            ["unknown"], true, TextWriter.Null));
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        Console.WriteLine("PASS " + name);
    }

    private static async Task ExpectAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
}
