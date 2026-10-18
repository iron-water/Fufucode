using Fufucode.Core;
using System.Text.Json;
using Fufucode.Cli;
using Fufucode.Core.Automation.Hosting;

internal static class CliHostTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "fufucode-cli-host-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var db = Path.Combine(root, "control.db"); var endpoint = Path.Combine(root, "host.port");
        var config = new HostOptions(db, Path.Combine(root, "evidence"), "cli-host-test", endpoint);
        var file = Path.Combine(root, "host.json"); File.WriteAllText(file, JsonSerializer.Serialize(config));
        void Check(bool value, string name) { if (!value) throw new Exception(name); Console.WriteLine("PASS CLI host " + name); }
        async Task<int> Command(string action, string options, StringWriter? output = null) =>
            await CliHostCommands.RunAsync(CliOptions.Parse(["host", action, options]), output ?? new StringWriter(), CancellationToken.None);
        try { await Command("serve", file); throw new Exception("Missing authority accepted"); }
        catch (FileNotFoundException) { Check(!File.Exists(db), "does not create a missing authority"); }
        try { await Command("serve", "relative.json"); throw new Exception("Relative path accepted"); }
        catch (ArgumentException) { Check(true, "rejects relative options before side effects"); }
        using var initOutput = new StringWriter();
        await CliControlCommands.RunAsync(CliOptions.Parse(["control", "init", db]), initOutput);
        var invalid = Path.Combine(root, "invalid.json"); File.WriteAllText(invalid, JsonSerializer.Serialize(config with { IdleMs = 0 }));
        try { await Command("serve", invalid); throw new Exception("Invalid timing accepted"); }
        catch (ArgumentException) { Check(!File.Exists(endpoint), "rejects unbounded host settings before starting"); }
        using var servingOutput = new StringWriter();
        var serve = Command("serve", file, servingOutput);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (!File.Exists(endpoint) && !serve.IsCompleted) await Task.Delay(20, timeout.Token);
            Check(!serve.IsCompleted && HostClient.ReadEndpoint(endpoint) is > 0, "publishes a real loopback endpoint");
            using var status = new StringWriter(); Check(await Command("status", file, status) == 0 && status.ToString().Contains("cli-host-test"), "status reads the existing host");
            var wrong = Path.Combine(root, "wrong.json"); File.WriteAllText(wrong, JsonSerializer.Serialize(config with { HostId = "other-host" }));
            try { await Command("stop", wrong); throw new Exception("Wrong host stopped"); }
            catch (InvalidOperationException) { Check(!serve.IsCompleted, "wrong endpoint identity cannot stop the host"); }
            Check(await Command("stop", file) == 0, "explicit same-host stop is acknowledged");
            Check(await serve.WaitAsync(timeout.Token) == 0, "serving entry exits after a recorded clean stop");
            try { await Command("status", file); throw new Exception("Stopped host reported alive"); }
            catch (InvalidOperationException) { Check(true, "stopped endpoint is not reported alive"); }
        }
        finally
        {
            if (!serve.IsCompleted) { await Command("stop", file); await serve.WaitAsync(TimeSpan.FromSeconds(20)); }
        }
    }
}
