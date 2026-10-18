using System.Text.Json;
using Fufucode.Core.Automation;
using Fufucode.Core.Automation.Hosting;

namespace Fufucode.Cli;

/// <summary>Explicit local entry point for the existing single execution host.</summary>
public static class CliHostCommands
{
    public static async Task<int> RunAsync(CliOptions options, TextWriter output, CancellationToken ct)
    {
        var args = options.Arguments;
        if (options.SessionId is not null || args.Count != 2 || args[0] is not ("serve" or "status" or "stop") ||
            !Path.IsPathFullyQualified(args[1]))
            throw new ArgumentException("Usage: host serve|status|stop <absolute-host-options.json>.");
        var file = new FileInfo(args[1]);
        if (!file.Exists || file.Length > 64 * 1024) throw new ArgumentException("Host options must be an existing file smaller than 64 KiB.");
        HostOptions config;
        try { config = JsonSerializer.Deserialize<HostOptions>(await File.ReadAllTextAsync(file.FullName, ct),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new ArgumentException("Missing host options."); }
        catch (JsonException error) { throw new ArgumentException("Invalid host options JSON.", error); }
        if (!Path.IsPathFullyQualified(config.Database) || !Path.IsPathFullyQualified(config.EvidenceDirectory) ||
            !Path.IsPathFullyQualified(config.EndpointFile) || string.IsNullOrWhiteSpace(config.HostId) ||
            config.Port is < 0 or > 65535 || config.IdleMs is < 1 or > 60000 ||
            config.PushMs is < 1 or > 60000 || config.EventLimit is < 1 or > 1000)
            throw new ArgumentException("Host options require absolute paths, a host ID, and bounded port/timing/event limits.");
        if (!File.Exists(config.Database)) throw new FileNotFoundException("Initialize the explicitly selected control database first.");
        // Validate the existing authority without creating or migrating another database.
        using (var authority = AutomationStore.AttachControlClient(config.Database)) authority.ReadControlClientSnapshot();

        if (args[0] == "serve")
        {
            await using var host = new BackgroundHost(config);
            if (!await host.StartAsync()) throw new InvalidOperationException("The host slot is already active or unverifiable; no second host started.");
            await output.WriteLineAsync(JsonSerializer.Serialize(new { hostId = host.HostId, port = host.Port,
                processId = Environment.ProcessId, state = "serving" }));
            await output.FlushAsync(ct);
            try { await host.StopCompleted.WaitAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Ctrl+C is an explicit clean stop; the owning host settles its own work.
                await host.StopAsync();
                return 130;
            }
            await host.StopAsync();
            return 0;
        }

        var port = HostClient.ReadEndpoint(config.EndpointFile)
            ?? throw new InvalidOperationException("No host endpoint was published.");
        await using var client = await HostClient.ConnectAsync(port, ct);
        var hello = await client.SendAsync(HostProtocol.Hello, null, ct);
        if (!hello.Payload.TryGetProperty("hostId", out var identity) || identity.GetString() != config.HostId)
            throw new InvalidOperationException("Endpoint host identity does not match; no stop command was sent.");
        var result = args[0] == "stop" ? await client.StopAsync(ct)
            : await client.SendAsync(HostProtocol.Snapshot, null, ct);
        await output.WriteLineAsync(result.Payload.GetRawText());
        return result.Type == HostProtocol.Error ? 3 : 0;
    }
}
