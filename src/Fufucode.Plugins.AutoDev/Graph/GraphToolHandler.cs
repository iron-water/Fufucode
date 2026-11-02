using System.Text.Json;
using Fufucode.App.Abstractions.Graph;
using Fufucode.Core.Backend;
using Fufucode.Core.Plugins;

namespace Fufucode.Plugins.AutoDev.Graph;

/// <summary>Model tool surface. Actor, acceptance, human decisions, purpose and budget are not model parameters.</summary>
public sealed class GraphToolHandler : IPluginToolHandler
{
    private readonly IGraphProjectService _service;
    private readonly string? _projectPath;
    public GraphToolHandler(IGraphProjectService service) => _service = service ?? throw new ArgumentNullException(nameof(service));
    /// <summary>Pin a managed run's tool scope even while the user selects another project in the UI.</summary>
    public GraphToolHandler(IGraphProjectService service, string projectPath)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _projectPath = GraphStorage.Canonical(projectPath);
    }
    public PluginCapability? Capability => PluginCapability.AutoDevelopment;

    private static PluginToolRegistration Tool(string name, string description, string properties, string required = "") =>
        new(name, description, "{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{" + properties + "},\"required\":[" + required + "]}", PluginCapability.AutoDevelopment);
    private const string S = "{\"type\":\"string\"}";
    private const string A = "{\"type\":\"array\",\"items\":{\"type\":\"string\"}}";
    private const string V = "\"expectedRevision\":{\"type\":\"integer\"}";
    public IReadOnlyList<PluginToolRegistration> Registrations { get; } =
    [
        Tool("graph_read", "Read a bounded project graph index by default. Page modules/entries explicitly or request full:true for the entire unabridged snapshot. Counts and omitted fields are explicit; pin revision for stable pages.", "\"revision\":{\"type\":\"integer\"},\"full\":{\"type\":\"boolean\"},\"moduleOffset\":{\"type\":\"integer\"},\"moduleLimit\":{\"type\":\"integer\"},\"includeEntries\":{\"type\":\"boolean\"},\"entryOffset\":{\"type\":\"integer\"},\"entryLimit\":{\"type\":\"integer\"}"),
        Tool("graph_context", "Read project context focused on a module or ticket; historical data stays at its revision.", "\"moduleId\":" + S + ",\"ticketId\":" + S + ",\"revision\":{\"type\":\"integer\"}"),
        Tool("graph_upsert_module", "Propose a sourced module definition within the human project mandate. Changes are attributed to agent.", "\"id\":" + S + ",\"parentId\":" + S + ",\"name\":" + S + ",\"summary\":" + S + ",\"sourcePaths\":" + A + "," + V, "\"name\",\"summary\",\"sourcePaths\",\"expectedRevision\""),
        Tool("graph_remove_module", "Remove an unreferenced leaf module; historical versions remain.", "\"moduleId\":" + S + "," + V, "\"moduleId\",\"expectedRevision\""),
        Tool("graph_upsert_relation", "Record an explicit sourced relation; never guess an unknown dependency.", "\"id\":" + S + ",\"sourceId\":" + S + ",\"targetId\":" + S + ",\"kind\":" + S + ",\"label\":" + S + "," + V, "\"sourceId\",\"targetId\",\"kind\",\"label\",\"expectedRevision\""),
        Tool("graph_remove_relation", "Remove a relation while keeping its history.", "\"relationId\":" + S + "," + V, "\"relationId\",\"expectedRevision\""),
        Tool("graph_create_ticket", "Create a concrete follow-up ticket with exact acceptance conditions under the current human purpose; does not accept or launch it.", "\"title\":" + S + ",\"goal\":" + S + ",\"moduleIds\":" + A + ",\"acceptance\":" + A, "\"title\",\"goal\",\"moduleIds\",\"acceptance\""),
        Tool("graph_add_entry", "Record agent correspondence or suggestions. This cannot record a human decision or alter purpose.", "\"ticketId\":" + S + ",\"moduleId\":" + S + ",\"kind\":{\"type\":\"string\",\"enum\":[\"message\",\"suggestion\",\"note\"]},\"text\":" + S + ",\"reference\":" + S, "\"kind\",\"text\""),
        Tool("graph_evidence", "Record an agent evidence claim for an exact criterion and real reference. In-flight claims mature only after that run settles. Human acceptance is separate.", "\"ticketId\":" + S + ",\"runId\":" + S + ",\"kind\":{\"type\":\"string\",\"enum\":[\"artifact\",\"test\",\"review\",\"note\"]},\"criterion\":" + S + ",\"reference\":" + S + ",\"passed\":{\"type\":\"boolean\"}", "\"ticketId\",\"runId\",\"kind\",\"criterion\",\"reference\",\"passed\"")
    ];

    public Task<PureToolResult> ExecuteAsync(PureToolCall call, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);
        try
        {
            if (_projectPath is null) return ExecuteCore(call, cancellationToken);
            if (_service is GraphProjectService concrete)
                return concrete.InProjectScope(_projectPath, () => ExecuteCore(call, cancellationToken));
            if (GraphStorage.PathKey(_service.ReadSnapshot().ProjectPath) != GraphStorage.PathKey(_projectPath))
                throw new InvalidOperationException("The tool's project scope changed.");
            return ExecuteCore(call, cancellationToken);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or IOException)
        { return Task.FromResult(new PureToolResult(call.Id, false, e.Message)); }
    }

    private Task<PureToolResult> ExecuteCore(PureToolCall call, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_service.ReadOptions().Enabled) throw new InvalidOperationException("Graph is disabled.");
            var registration = Registrations.SingleOrDefault(r => r.Name == call.Name)
                ?? throw new ArgumentException("Unknown Graph tool: " + call.Name);
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
            var data = document.RootElement;
            using var schema = JsonDocument.Parse(registration.ParametersJsonSchema);
            Validate(data, schema.RootElement);
            var revision = Integer(data, "revision");
            if (call.Name == "graph_read")
            {
                var snapshot = _service.ReadSnapshot(revision);
                var full = data.TryGetProperty("full", out var fullValue) && fullValue.GetBoolean();
                if (full)
                {
                    if (data.EnumerateObject().Any(p => p.Name is not ("revision" or "full")))
                        throw new ArgumentException("full:true cannot be combined with paging or includeEntries; it returns the complete snapshot.");
                    return Success(call, GraphStorage.Serialize(snapshot));
                }
                return Success(call, GraphReadIndex.Build(snapshot, PageNumber(data, "moduleOffset", 0, 0, int.MaxValue),
                    PageNumber(data, "moduleLimit", 24, 1, 100), data.TryGetProperty("includeEntries", out var include) && include.GetBoolean(),
                    PageNumber(data, "entryOffset", 0, 0, int.MaxValue), PageNumber(data, "entryLimit", 16, 1, 100)));
            }
            if (call.Name == "graph_context") return Success(call, _service.BuildContext(Text(data, "moduleId"), Text(data, "ticketId"), revision));
            if (_service is not GraphProjectService concrete)
                throw new InvalidOperationException("This Graph service cannot provide an authenticated agent mutation scope.");
            var result = concrete.ExecuteAgentCommand(call.Id, GraphStorage.Serialize(new { call.Name, arguments = data.Clone() }), Integer(data, "expectedRevision"), () =>
            {
                switch (call.Name)
                {
                    case "graph_upsert_module": return _service.UpsertModule(Text(data, "id"), Text(data, "parentId"), Required(data, "name"), Required(data, "summary"), Strings(data, "sourcePaths"));
                    case "graph_remove_module": _service.RemoveModule(Required(data, "moduleId")); return new { status = "removed" };
                    case "graph_upsert_relation": return _service.UpsertRelation(Text(data, "id"), Required(data, "sourceId"), Required(data, "targetId"), Required(data, "kind"), Required(data, "label"));
                    case "graph_remove_relation": _service.RemoveRelation(Required(data, "relationId")); return new { status = "removed" };
                    case "graph_create_ticket": return _service.CreateTicket(Required(data, "title"), Required(data, "goal"), Strings(data, "moduleIds"), Strings(data, "acceptance"));
                    case "graph_add_entry":
                        var kind = Required(data, "kind");
                        if (kind is not ("message" or "suggestion" or "note")) throw new ArgumentException("Model entries cannot record human decisions.");
                        _service.AddEntry(Text(data, "ticketId"), Text(data, "moduleId"), kind, Required(data, "text"), Text(data, "reference"));
                        return new { status = "recorded", actor = "agent" };
                    case "graph_evidence": return concrete.RecordAgentEvidence(Required(data, "ticketId"), Required(data, "runId"), Required(data, "kind"), Required(data, "criterion"), Required(data, "reference"), data.GetProperty("passed").GetBoolean());
                    default: throw new ArgumentException("Unknown model action.");
                }
            });
            return Success(call, result);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or KeyNotFoundException or JsonException or System.IO.IOException or Microsoft.Data.Sqlite.SqliteException)
        { return Task.FromResult(new PureToolResult(call.Id, false, e.Message)); }
    }

    private static void Validate(JsonElement data, JsonElement schema)
    {
        if (data.ValueKind != JsonValueKind.Object) throw new ArgumentException("Tool arguments must be an object.");
        var properties = schema.GetProperty("properties");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in data.EnumerateObject())
        {
            if (!seen.Add(property.Name)) throw new ArgumentException("Duplicate tool argument: " + property.Name);
            if (!properties.TryGetProperty(property.Name, out var rule)) throw new ArgumentException("Unsupported tool argument: " + property.Name);
            var expected = rule.GetProperty("type").GetString();
            if (expected == "string" && property.Value.ValueKind != JsonValueKind.String
                || expected == "array" && (property.Value.ValueKind != JsonValueKind.Array || property.Value.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.String))
                || expected == "integer" && !property.Value.TryGetInt64(out _)
                || expected == "boolean" && property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new ArgumentException("Invalid tool argument type: " + property.Name);
            if (rule.TryGetProperty("enum", out var choices) && !choices.EnumerateArray().Any(v => v.GetString() == property.Value.GetString()))
                throw new ArgumentException("Invalid tool argument value: " + property.Name);
        }
        foreach (var name in schema.GetProperty("required").EnumerateArray())
            if (!data.TryGetProperty(name.GetString()!, out _)) throw new ArgumentException("Missing tool argument: " + name.GetString());
    }

    private static Task<PureToolResult> Success(PureToolCall call, string json) => Task.FromResult(new PureToolResult(call.Id, true, json));
    private static string? Text(JsonElement data, string name) => data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string Required(JsonElement data, string name) => Text(data, name) ?? throw new ArgumentException("Missing argument: " + name);
    private static long? Integer(JsonElement data, string name) => data.TryGetProperty(name, out var value) ? value.GetInt64() : null;
    private static string[] Strings(JsonElement data, string name) => data.GetProperty(name).EnumerateArray().Select(v => v.GetString()!).ToArray();
    private static int PageNumber(JsonElement data, string name, int fallback, int minimum, int maximum)
    {
        var value = Integer(data, name) ?? fallback;
        if (value < minimum || value > maximum) throw new ArgumentOutOfRangeException(name, $"Use {minimum}..{maximum}.");
        return (int)value;
    }
}
