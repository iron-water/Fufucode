using System.Text.Json;
using Fufucode.Cli;
using Fufucode.Core;
using Fufucode.Pi;

internal static class CliSettingsTests
{
    private const string FixtureSecret = "cli-private-fixture-key";
    private sealed class Credentials : ICredentialStore
    {
        private string? _secret = FixtureSecret;
        public void Save(string name, string password)
        { ArgumentException.ThrowIfNullOrWhiteSpace(password); _secret = password; }
        public void Delete(string name) => _secret = null;
        public ValueTask<string?> ResolveAsync(string name, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<string?>(name == "FUFUCODE_TEST_KEY" ? _secret : null);
        }
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        Console.WriteLine("PASS " + name);
    }

    // Called by CliTests.RunAsync; intentionally uses the existing FakePi executable and real pipes.
    public static async Task RunAsync(string root, string fake, string dotnet)
    {
        var layout = new AppDataLayout(Path.Combine(root, "tmp", "cli-settings-tests", Guid.NewGuid().ToString("N")));
        var project = Path.Combine(layout.Root, "project");
        Directory.CreateDirectory(project);
        var credentials = new Credentials();
        var manager = new PersistentSessionManager(dotnet, fake, layout,
            requireApprovalBridge: false, credentialResolver: credentials);

        async Task<string> Settings(bool json, params string[] arguments)
        {
            using var output = new StringWriter();
            await CliSettingsCommands.RunAsync(arguments, manager.Settings, credentials, json, output);
            return output.ToString();
        }

        var endpoint = """{"providerName":"fixture","baseUrl":"https://example.test/v1","modelId":"fixture-model","apiKeyEnvironmentVariable":"FUFUCODE_TEST_KEY"}""";
        await Settings(false, "set", "theme", "dark");
        await Settings(false, "set", "selectedModel", "fixture/fixture-model");
        await Settings(false, "set", "thinkingLevel", "medium");
        await Settings(false, "set", "proxyUrl", "http://localhost:7890");
        await Settings(false, "set", "endpoint", endpoint);
        using (var setResult = JsonDocument.Parse(await Settings(true, "set", "theme", "light")))
            Check(setResult.RootElement.GetProperty("key").GetString() == "theme" &&
                setResult.RootElement.GetProperty("updated").GetBoolean(), "CLI settings set returns a JSON acknowledgement");
        var loaded = new AppSettingsStore(layout);
        Check(loaded.Current is { Theme: "light", SelectedModel: "fixture/fixture-model", ThinkingLevel: "medium" } &&
            loaded.Current.Endpoint?.ApiKeyEnvironmentVariable == "FUFUCODE_TEST_KEY",
            "CLI settings edits persist using Core validation");
        var secretWrite = await Settings(false, "set", "endpointCredential", FixtureSecret);
        Check(secretWrite == "endpointCredentialConfigured=true" + Environment.NewLine && !secretWrite.Contains(FixtureSecret),
            "CLI credential save uses the separate credential store and only prints configuration status");
        using (var status = JsonDocument.Parse(await Settings(true, "get", "endpointCredential")))
            Check(status.RootElement.GetProperty("endpointCredential").GetBoolean(), "CLI credential get returns status only");

        var all = await Settings(true, "get");
        using (var parsed = JsonDocument.Parse(all))
            Check(parsed.RootElement.GetProperty("endpointCredentialConfigured").GetBoolean() &&
                parsed.RootElement.GetProperty("endpoint").GetProperty("providerName").GetString() == "fixture" &&
                parsed.RootElement.GetProperty("theme").GetString() == "light" && !all.Contains(FixtureSecret),
                "CLI settings get JSON returns configuration and credential status without secret values");
        var readable = await Settings(false, "get");
        Check(readable.Contains("endpointCredentialConfigured=true", StringComparison.Ordinal) &&
            !readable.Contains(FixtureSecret, StringComparison.Ordinal), "CLI settings text output redacts credentials");
        using (var parsed = JsonDocument.Parse(await Settings(true, "get", "theme")))
            Check(parsed.RootElement.EnumerateObject().Count() == 1 && parsed.RootElement.GetProperty("theme").GetString() == "light",
                "CLI settings get key returns exactly one field");

        await using (var session = await manager.CreateAsync(project))
        {
            var state = (await session.Coordinator.QueryStateAsync()).Data!.Value;
            Check(state.GetProperty("model").GetProperty("id").GetString() == "fixture-model" &&
                state.GetProperty("thinkingLevel").GetString() == "medium" &&
                state.GetProperty("proxy").GetString() == "http://localhost:7890" &&
                state.GetProperty("fixtureKeyPresent").GetBoolean(),
                "CLI settings apply to a new FakePi session through the Core launch path");

            using var output = new StringWriter();
            await CliSettingsCommands.SetModelAsync(session, manager.Settings, "fixture/org/model", true, output);
            using var result = JsonDocument.Parse(output.ToString());
            var confirmed = (await session.Coordinator.QueryStateAsync()).Data!.Value;
            Check(result.RootElement.GetProperty("saved").GetBoolean() &&
                result.RootElement.GetProperty("model").GetString() == "fixture/org/model" &&
                confirmed.GetProperty("model").GetProperty("id").GetString() == "org/model" &&
                session.Chat.CurrentModel == "fixture/org/model" &&
                new AppSettingsStore(layout).Current.SelectedModel == "fixture/org/model",
                "CLI models set confirms the live FakePi model and persists its full ID");
            using var text = new StringWriter();
            await CliSettingsCommands.SetModelAsync(session, manager.Settings, "fixture/text-model", false, text);
            Check(text.ToString().Contains("fixture/text-model", StringComparison.Ordinal), "CLI models set prints its confirmed model");

            var previous = manager.Settings.Current;
            await session.Coordinator.SendPromptAsync("state_fail");
            await session.Coordinator.StopAsync();
            using var failedOutput = new StringWriter();
            try
            {
                await CliSettingsCommands.SetModelAsync(session, manager.Settings, "fixture/unsaved", true, failedOutput);
                throw new Exception("CLI saved a model without successful get_state confirmation.");
            }
            catch (RpcException) { }
            Check(manager.Settings.Current == previous && failedOutput.ToString().Length == 0 &&
                new AppSettingsStore(layout).Current.SelectedModel == "fixture/text-model",
                "CLI failed model confirmation preserves settings and emits no success result");
        }

        foreach (var arguments in new[]
        {
            new[] { "get", "apiKey" }, new[] { "get", "theme", "extra" }, new[] { "set", "theme" },
            new[] { "set", "endpointCredentialConfigured", "true" }, new[] { "set", "theme", "invalid" },
            new[] { "set", "selectedModel", "missing-provider" }, new[] { "set", "selectedModel", "fixture/ " },
            new[] { "set", "thinkingLevel", "invalid" }, new[] { "set", "proxyUrl", "http://user:secret@localhost" },
            new[] { "set", "endpoint", "{broken" }, new[] { "set", "endpoint", "{}" },
            new[] { "set", "endpoint", endpoint.Replace("FUFUCODE_TEST_KEY", "invalid-name", StringComparison.Ordinal) }
        })
        {
            var before = manager.Settings.Current;
            try { await Settings(true, arguments); throw new Exception("Invalid CLI setting arguments were accepted."); }
            catch (ArgumentException) { }
            Check(before == manager.Settings.Current, "CLI invalid settings arguments preserve saved configuration");
        }

        foreach (var model in new[] { "", "provider", "/model", "provider/", "provider/white space" })
        {
            try { SessionModelPreference.Parse(model); throw new Exception("Invalid model form was accepted."); }
            catch (ArgumentException) { }
        }
        var secretDelete = await Settings(true, "set", "endpointCredential", "null");
        using (var deleted = JsonDocument.Parse(secretDelete))
            Check(!deleted.RootElement.GetProperty("configured").GetBoolean() && !secretDelete.Contains(FixtureSecret),
                "CLI credential deletion only returns updated configuration status");
        await Settings(false, "set", "endpoint", "null");
        await Settings(false, "set", "selectedModel", "null");
        await Settings(false, "set", "thinkingLevel", "null");
        await Settings(false, "set", "proxyUrl", "null");
        using (var parsed = JsonDocument.Parse(await Settings(true, "get", "endpointCredentialConfigured")))
            Check(!parsed.RootElement.GetProperty("endpointCredentialConfigured").GetBoolean(),
                "CLI cleared endpoint reports no configured credential");
        await using (var cleared = await manager.CreateAsync(project))
        {
            var state = (await cleared.Coordinator.QueryStateAsync()).Data!.Value;
            Check(state.GetProperty("model").ValueKind == JsonValueKind.Null &&
                state.GetProperty("thinkingLevel").ValueKind == JsonValueKind.Null &&
                !state.GetProperty("fixtureKeyPresent").GetBoolean(),
                "CLI nullable settings clear model, thinking and endpoint for the next FakePi session");
        }
        Check(!File.ReadAllText(Path.Combine(layout.Root, "settings.json")).Contains(FixtureSecret),
            "CLI never persists credential values in settings.json");
    }
}
