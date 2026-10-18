using System.Text.Json;
using System.Text.Json.Serialization;
using Fufucode.Core.Automation;

namespace Fufucode.Cli;

/// <summary>Local OS-authorized control client. Does not discover or launch an agent runtime.</summary>
public static class CliControlCommands
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task<int> RunAsync(CliOptions options, TextWriter output, CancellationToken ct = default)
    {
        var args = options.Arguments;
        if (options.SessionId is not null || args.Count < 2 ||
            args[0] is not ("init" or "snapshot" or "state" or "groups" or "inbox" or "apply" or "relation" or "knowledge" or "restructure" or "integration" or "review" or "reconsider" or "repair" or "consume-repair" or "archive" or "export") ||
            args.Count != (args[0] is "apply" or "relation" or "knowledge" or "restructure" or "integration" or "review" or "reconsider" or "repair" or "consume-repair" or "archive" ? 3 : 2))
            throw new ArgumentException("Usage: control init|snapshot|state|groups|inbox|export <absolute.db> | apply|relation|knowledge|restructure|integration|review|reconsider|repair|consume-repair|archive <absolute.db> <command.json>.");
        if (!Path.IsPathFullyQualified(args[1])) throw new ArgumentException("Control database path must be absolute.");
        if (args[0] != "init" && !File.Exists(args[1]))
            throw new FileNotFoundException("Initialize the explicitly selected control database first.");
        // Validate the command before creating/migrating any database.
        object? input = null;
        if (args[0] is "apply" or "relation" or "knowledge" or "restructure" or "integration" or "review" or "reconsider" or "repair" or "consume-repair" or "archive")
        {
            if (new FileInfo(args[2]).Length > AutomationStore.MaximumLegacyControlBytes * 2L)
                throw new ArgumentException("Control command file is too large.");
            var text = await File.ReadAllTextAsync(args[2], ct);
            try
            {
                input = args[0] switch
                {
                    "apply" => JsonSerializer.Deserialize<ControlCommand>(text, Json),
                    "relation" => JsonSerializer.Deserialize<ControlRelationCommand>(text, Json),
                    "knowledge" => JsonSerializer.Deserialize<ControlKnowledgeCommand>(text, Json),
                    "restructure" => JsonSerializer.Deserialize<ControlRestructureCommand>(text, Json),
                    "integration" => JsonSerializer.Deserialize<ControlIntegrationCommand>(text, Json),
                    "review" => JsonSerializer.Deserialize<ControlReviewCommand>(text, Json),
                    "reconsider" => JsonSerializer.Deserialize<ControlReconsiderCommand>(text, Json),
                    "repair" => JsonSerializer.Deserialize<ControlRepairCommand>(text, Json),
                    "consume-repair" => JsonSerializer.Deserialize<ControlRepairBinding>(text, Json),
                    _ => (object?)JsonSerializer.Deserialize<LegacyControlArchiveCommand>(text, Json)
                };
            }
            catch (JsonException error) { throw new ArgumentException("Invalid control command JSON.", error); }
            if (input is null) throw new ArgumentException("Control command is required.");
        }
        ct.ThrowIfCancellationRequested();
        using var store = new AutomationStore(args[1]);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        object result = args[0] switch
        {
            "apply" => store.ApplyControlCommand((ControlCommand)input!, now),
            "relation" => store.ApplyControlRelation((ControlRelationCommand)input!, now),
            "knowledge" => store.ApplyControlKnowledge((ControlKnowledgeCommand)input!, now),
            "restructure" => store.ApplyControlRestructure((ControlRestructureCommand)input!, now),
            "integration" => store.ApplyControlIntegration((ControlIntegrationCommand)input!, now),
            "review" => store.ApplyControlReview((ControlReviewCommand)input!, now),
            "reconsider" => store.ApplyControlReconsider((ControlReconsiderCommand)input!, now),
            "repair" => store.ApplyControlRepair((ControlRepairCommand)input!, now),
            "consume-repair" => await new ControlIntentConsumer(store).QueueRepairAsync((ControlRepairBinding)input!, now, ct),
            "inbox" => store.ReadControlInbox(),
            "state" => store.ReadControlClientSnapshot(),
            "groups" => store.ReadControlGroups(),
            "archive" => store.ArchiveLegacyControl((LegacyControlArchiveCommand)input!, now),
            "export" => store.ExportControlShadow(),
            _ => store.ReadControlSnapshot()
        };
        await output.WriteLineAsync(JsonSerializer.Serialize(result, Json));
        return 0;
    }
}
