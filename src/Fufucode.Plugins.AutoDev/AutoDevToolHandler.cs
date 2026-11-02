using System.Text.Json;
using Fufucode.App.Abstractions.Graph;
using Fufucode.Core.Automation.Rebirth;
using Fufucode.Core.Backend;
using Fufucode.Core.Plugins;
using Fufucode.Plugins.AutoDev.Graph;

namespace Fufucode.Plugins.AutoDev;

/// <summary>
/// Compatibility read surface. Model mutations use the authenticated Graph command scope;
/// human decisions and execution receipts cannot be supplied by a model tool call.
/// </summary>
public sealed class AutoDevToolHandler : IPluginToolHandler
{
    private readonly PluginPackageContext _context;
    private readonly IGraphProjectService? _graph;
    private readonly GraphToolHandler? _tools;
    private static readonly IReadOnlyList<PluginToolRegistration> Reads =
    [
        new("autodev_status", "Read the current project authority and real execution state.", "{\"type\":\"object\",\"properties\":{}}", PluginCapability.AutoDevelopment),
        new("autodev_advance", "Read the next authorized ticket; this does not launch or complete work.", "{\"type\":\"object\",\"properties\":{}}", PluginCapability.AutoDevelopment),
        new("autodev_preflight", "Read deterministic ticket admission without changing it.", "{\"type\":\"object\",\"properties\":{\"ticketId\":{\"type\":\"string\"}},\"required\":[\"ticketId\"]}", PluginCapability.AutoDevelopment)
    ];

    public AutoDevToolHandler(PluginPackageContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _graph = context.GetService<IGraphProjectService>();
        _tools = _graph is null ? null : string.IsNullOrWhiteSpace(context.ProjectPath)
            ? new GraphToolHandler(_graph) : new GraphToolHandler(_graph, context.ProjectPath);
        Registrations = _tools is null ? Reads : Reads.Concat(_tools.Registrations).ToArray();
    }
    public IReadOnlyList<PluginToolRegistration> Registrations { get; }
    public PluginCapability? Capability => PluginCapability.AutoDevelopment;

    public Task<PureToolResult> ExecuteAsync(PureToolCall call, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);
        if (_tools is not null && _tools.Registrations.Any(t => t.Name == call.Name))
            return _tools.ExecuteAsync(call, cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Reads.Any(r => r.Name == call.Name))
                throw new InvalidOperationException("Model tools cannot impersonate human decisions, edit purpose, approve gates, reconcile unknown outcomes, or report execution completion. Use the scoped graph_* tools.");
            if (_graph is not null)
            {
                if (!_graph.ReadOptions().Enabled) throw new InvalidOperationException("Graph is disabled.");
                var snapshot = _graph.ReadSnapshot();
                object data = call.Name switch
                {
                    "autodev_status" => snapshot,
                    "autodev_advance" => new { next = snapshot.Tickets.FirstOrDefault(t => t.Status == "Authorized"), snapshot.ProjectStatus },
                    _ => new { ticket = snapshot.Tickets.SingleOrDefault(t => t.Id == TicketId(call)) ?? throw new KeyNotFoundException("Unknown ticket in this project."), admission = "Read graph state; only the execution owner admits and starts work." }
                };
                return Result(call, data);
            }
            // Legacy data remains readable; opening a compatibility tool cannot create a project or
            // mutate a mandate. Graph's identity mapping is used whenever the host provides it.
            var root = _context.DataRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Fufucode");
            var path = _context.ProjectPath ?? _context.PackageDirectory;
            var database = RebirthPaths.Database(root, path);
            if (!File.Exists(database)) throw new InvalidOperationException("Project authority is not initialized. Open Graph to supply a human purpose.");
            using var kernel = new RebirthKernel(database);
            var legacy = kernel.ReadSnapshot(RebirthPaths.ProjectId(path));
            object value = call.Name switch
            {
                "autodev_status" => legacy,
                "autodev_advance" => new { next = legacy.Tickets.FirstOrDefault(t => t.Status == RebirthTicketStatus.Authorized) },
                _ => legacy.Tickets.Any(t => t.Id == TicketId(call))
                    ? kernel.InspectAdmission(TicketId(call), "graph", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                    : throw new KeyNotFoundException("Unknown ticket in this project.")
            };
            return Result(call, value);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or KeyNotFoundException or IOException or JsonException)
        { return Task.FromResult(new PureToolResult(call.Id, false, e.Message)); }
    }

    private static string TicketId(PureToolCall call)
    {
        using var data = JsonDocument.Parse(call.ArgumentsJson);
        return data.RootElement.GetProperty("ticketId").GetString() ?? throw new ArgumentException("ticketId is required.");
    }
    private static Task<PureToolResult> Result(PureToolCall call, object value) => Task.FromResult(new PureToolResult(call.Id, true,
        JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } })));
}
