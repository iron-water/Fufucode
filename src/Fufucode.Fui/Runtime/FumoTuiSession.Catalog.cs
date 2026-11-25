using Fufucode.Core.Backend;
using Fufucode.Core.Plugins;

namespace Fufucode.Fui;

internal sealed partial class FumoTuiSession
{
    public async Task<FuiRuntimeCatalog> ReadRuntimeCatalogAsync(CancellationToken ct)
    {
        using var operation = BeginOperation(ct);
        ct = operation.Token;
        await _operations.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            return BuildRuntimeCatalog();
        }
        finally { _operations.Release(); }
    }

    private FuiRuntimeCatalog BuildRuntimeCatalog()
    {
        var native = _session.Chat as NativeCSharpChatSession;
        var pluginTools = native?.PluginTools?.ActiveRegistrations ?? [];
        var skills = native?.Skills?.All ?? [];
        var subagents = native?.Subagents is { IsActive: true } runtime ? runtime.Agents : [];
        var mcp = native?.Mcp;
        var errors = _project.Plugins.LoadErrors.ToDictionary(
            pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        return FuiRuntimeCatalogBuilder.Build(new(
            ExecutionMode == "plan",
            ProductionToolDispatcher.GetBuiltinToolDefinitions(),
            pluginTools,
            skills,
            mcp is not null,
            mcp?.HasServers == true,
            mcp?.Servers ?? [],
            native?.Subagents is { IsActive: true },
            subagents,
            _project.Plugins.GetActivePluginSnapshots(),
            errors,
            native?.HostToolAllowed));
    }
}
