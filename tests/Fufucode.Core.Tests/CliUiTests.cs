using System.Buffers.Binary;
using System.Text.Json;

internal static class CliUiTests
{
    public static void CheckWave5ReportDtos(JsonElement usage, JsonElement changes)
    {
        var counter = usage.GetProperty("report").GetProperty("session").GetProperty("totalTokens");
        var file = changes.GetProperty("report").GetProperty("files")[0];
        if (counter.GetProperty("value").ValueKind != JsonValueKind.Null || counter.GetProperty("knownSubtotal").GetInt64() != 18 ||
            file.GetProperty("isNew").ValueKind != JsonValueKind.True || file.GetProperty("isDeleted").ValueKind != JsonValueKind.False ||
            file.GetProperty("isBinary").ValueKind != JsonValueKind.False || file.GetProperty("toolCallIds").GetArrayLength() != 1)
            throw new Exception("Wave5 consumer DTOs hid unknown values or change flags.");
        Console.WriteLine("PASS A8 CliUiTests consumer DTO keeps unknown usage and explicit file flags");
    }

    public static async Task RunAsync(string root)
    {
        var binary = new DirectoryInfo(AppContext.BaseDirectory);
        var app = Path.Combine(root, "src", "Fufucode.App", "bin", binary.Parent!.Name, binary.Name,
            OperatingSystem.IsWindows() ? "Fufucode.App.exe" : "Fufucode.App");
        if (!File.Exists(app))
        {
            Console.WriteLine("FAIL CLI UI: Build App before running GUI CLI acceptance: " + app);
            Environment.ExitCode = 1;
            return;
        }
        var artifacts = Path.Combine(root, "tmp", "cli-ui-" + Guid.NewGuid().ToString("N") + " space!");
        Directory.CreateDirectory(artifacts);
        var records = new List<object>();
        async Task<(int Code, string Out, string Error)> Run(params string[] args)
        {
            using var output = new StringWriter(); using var error = new StringWriter();
            var savedOutput = Console.Out; var savedError = Console.Error;
            int code;
            try
            {
                Console.SetOut(output); Console.SetError(error);
                code = await Fufucode.Cli.Program.Main(["ui", .. args, "--ui-app", app]);
            }
            finally { Console.SetOut(savedOutput); Console.SetError(savedError); }
            records.Add(new { command = new[] { "ui" }.Concat(args).Concat(["--ui-app", app]), exitCode = code,
                stdout = output.ToString(), stderr = error.ToString() });
            return (code, output.ToString(), error.ToString());
        }
        void Check(bool condition, string name)
        {
            if (!condition) throw new UiTestFailure(name);
            Console.WriteLine("PASS " + name);
        }
        try
        {
            Check(!TryParseReport((2, "{}", "App startup failed"), out _, out var failed) &&
                failed.Contains("exit 2") && failed.Contains("App startup failed") &&
                !TryParseReport((0, "", ""), out _, out var empty) && empty.Contains("empty stdout") &&
                !TryParseReport((0, "invalid JSON", ""), out _, out var malformed) && malformed.Contains("invalid JSON"),
                "R-002 failed, empty and malformed UI output returns diagnostics without throwing");
            var initial = await Run("snapshot");
            using var chat = ReadReport(initial);
            Check(initial.Code == 0 && initial.Error.Length == 0 && chat.RootElement.GetProperty("page").GetString() == "chat" &&
                chat.RootElement.GetProperty("mode").GetString() == "headless-preview" &&
                chat.RootElement.GetProperty("sessionSource").GetString() == "mock" &&
                !chat.RootElement.GetProperty("truncated").GetBoolean(), "CLI ui snapshot returns real headless controls with explicit mock provenance");
            var nodes = Flatten(chat.RootElement.GetProperty("tree")).ToArray();
            var navigation = chat.RootElement.GetProperty("navigation").EnumerateArray().ToArray();
            Check(new[] { "nav.chat", "nav.extensions", "nav.settings" }.All(id =>
                navigation.Count(node => node.GetProperty("id").GetString() == id) == 1),
                "R-001 navigation IDs are unique in the shared action registry");
            Check(nodes.Any(node => node.GetProperty("type").GetString() == "ChatPage") &&
                new[] { "nav.chat", "nav.extensions", "nav.settings" }.All(id => navigation.Any(node =>
                    node.GetProperty("id").GetString() == id && node.GetProperty("enabled").GetBoolean() &&
                    node.GetProperty("source").GetString() == "shared-action-registry" &&
                    node.GetProperty("actions").EnumerateArray().Any(action => action.GetString() == "invoke"))),
                "CLI snapshot includes actual ChatPage and three enabled shared navigation actions");
            foreach (var (page, type) in new[] { ("settings", "SettingsPage"), ("extensions", "ExtensionsPage"), ("chat", "ChatPage") })
            {
                var result = await Run("invoke", "nav." + page);
                using var tree = ReadReport(result);
                Check(result.Code == 0 && result.Error.Length == 0 && tree.RootElement.GetProperty("page").GetString() == page &&
                    Flatten(tree.RootElement.GetProperty("tree")).Any(node => node.GetProperty("type").GetString() == type),
                    "CLI ui invoke reaches actual " + type + " via shared action registry");
            }
            var png = Path.Combine(artifacts, "settings.png");
            var screenshot = await Run("screenshot", png, "--page", "settings");
            using var shot = ReadReport(screenshot);
            var bytes = await File.ReadAllBytesAsync(png);
            Check(screenshot.Code == 0 && screenshot.Error.Length == 0 && bytes.Length > 5000 &&
                bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) &&
                BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4)) == shot.RootElement.GetProperty("width").GetInt32() &&
                BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4)) == shot.RootElement.GetProperty("height").GetInt32() &&
                shot.RootElement.GetProperty("page").GetString() == "settings", "CLI screenshot is a rendered PNG with matching dimensions and requested page");
            var overwrite = await Run("screenshot", png);
            var preserved = await File.ReadAllBytesAsync(png);
            Check(overwrite.Code == 3 && overwrite.Out.Length == 0 && overwrite.Error.Contains("already exists") &&
                bytes.SequenceEqual(preserved), "CLI screenshot refuses overwrite and preserves existing bytes");
            var denied = await Run("invoke", "input.send");
            Check(denied.Code == 2 && denied.Out.Length == 0 && denied.Error.Contains("Only nav."),
                "CLI preview refuses business actions rather than bypassing approvals");
            var unknown = await Run("set", "EndpointKeyBox", "text", "not-a-key");
            Check(unknown.Code == 2 && unknown.Out.Length == 0, "CLI preview rejects unsupported property reflection");
            var invalid = await Run("snapshot", "--page", "other");
            Check(invalid.Code == 2 && invalid.Error.Contains("Unknown preview page"), "CLI preview rejects unknown pages");
            using var errors = new StringWriter();
            using var output = new StringWriter();
            try
            {
                await Fufucode.Cli.CliUiCommands.RunAsync(["snapshot", "--ui-app", Path.Combine(artifacts, "missing.exe")], output, errors);
                throw new UiTestFailure("Missing App was accepted.");
            }
            catch (FileNotFoundException) { Console.WriteLine("PASS CLI preview reports missing App without starting a real session"); }
        }
        catch (Exception error) when (error is UiTestFailure or JsonException or InvalidOperationException or KeyNotFoundException or IOException)
        {
            Console.WriteLine("FAIL CLI UI: " + error.Message);
            Environment.ExitCode = 1;
        }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(artifacts, "report.json"), JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("UI artifacts: " + artifacts);
        }
    }

    private sealed class UiTestFailure(string message) : Exception(message);

    private static JsonDocument ReadReport((int Code, string Out, string Error) result)
    {
        if (!TryParseReport(result, out var document, out var diagnostic))
            throw new UiTestFailure(diagnostic);
        return document!;
    }

    private static bool TryParseReport((int Code, string Out, string Error) result,
        out JsonDocument? document, out string diagnostic)
    {
        document = null;
        var context = $"exit {result.Code}; stderr: {result.Error}; stdout length: {result.Out.Length}";
        if (result.Code != 0 || !string.IsNullOrWhiteSpace(result.Error) || string.IsNullOrWhiteSpace(result.Out))
        {
            diagnostic = "UI command failed or returned empty stdout; " + context;
            return false;
        }
        try
        {
            // 40 visual levels contain both node objects and children arrays.
            document = JsonDocument.Parse(result.Out, new JsonDocumentOptions { MaxDepth = 128 });
            diagnostic = "";
            return true;
        }
        catch (JsonException error)
        {
            diagnostic = "UI command returned invalid JSON: " + error.Message + "; " + context;
            return false;
        }
    }

    private static IEnumerable<JsonElement> Flatten(JsonElement node)
    {
        yield return node;
        foreach (var child in node.GetProperty("children").EnumerateArray())
            foreach (var descendant in Flatten(child)) yield return descendant;
    }
}
