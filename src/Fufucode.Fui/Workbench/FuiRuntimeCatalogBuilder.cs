using Fufucode.Core.Backend;
using Fufucode.Core.Plugins;

namespace Fufucode.Fui;

internal sealed record FuiRuntimeCatalogInput(
    bool PlanMode,
    IReadOnlyList<PureToolDefinition> NativeTools,
    IReadOnlyList<PluginToolRegistration> PluginTools,
    IReadOnlyList<SkillDefinition> Skills,
    bool HasMcpFactSource,
    bool McpAvailable,
    IReadOnlyList<McpServerConfig> McpServers,
    bool SubagentsActive,
    IReadOnlyList<NativeSubagentDescriptor> Subagents,
    IReadOnlyList<PluginRuntimeSnapshot> LoadedPlugins,
    IReadOnlyDictionary<string, string> PluginLoadErrors,
    Func<string, bool>? HostToolAllowed = null);

internal static class FuiRuntimeCatalogBuilder
{
    internal static FuiRuntimeCatalog Build(FuiRuntimeCatalogInput input)
    {
        var capabilities = new List<FuiCapabilityEntry>();
        foreach (var tool in input.NativeTools)
        {
            if (input.HostToolAllowed?.Invoke(tool.Name) == false) continue;
            if (input.PlanMode && !ProductionToolDispatcher.IsPlanSafeTool(tool.Name)) continue;
            capabilities.Add(new(tool.Name, tool.Description, FuiCapabilitySource.Native, FuiCapabilityKind.Tool));
        }

        if (!input.PlanMode)
        {
            foreach (var tool in input.PluginTools)
            {
                if (input.HostToolAllowed?.Invoke(tool.Name) == false) continue;
                capabilities.Add(new(tool.Name, tool.Description, FuiCapabilitySource.Plugin, FuiCapabilityKind.Tool));
            }

            if (input.HostToolAllowed?.Invoke(SkillRegistry.ToolName) != false)
                foreach (var skill in input.Skills.Where(skill => !skill.DisableModelInvocation))
                    capabilities.Add(new(skill.Name, skill.Description, FuiCapabilitySource.Skill, FuiCapabilityKind.Skill));

            if (input.McpAvailable && input.HostToolAllowed?.Invoke(McpGatewayDispatcher.ToolName) != false)
                capabilities.Add(new(McpGatewayDispatcher.ToolName, McpGatewayDispatcher.Definition.Description,
                    FuiCapabilitySource.Mcp, FuiCapabilityKind.Gateway));

            if (input.SubagentsActive && input.HostToolAllowed?.Invoke(NativeSubagentContract.ToolName) != false)
                foreach (var agent in input.Subagents)
                    capabilities.Add(new(agent.Name,
                        $"[{agent.Mode}] {agent.Description}",
                        FuiCapabilitySource.Subagent,
                        FuiCapabilityKind.Agent));
        }

        var loaded = input.LoadedPlugins.Select(snapshot =>
        {
            var definition = PluginMarketplaceCatalog.Find(snapshot.Id);
            return new FuiPluginRuntime(
                snapshot.Id,
                definition?.Name ?? snapshot.Id,
                definition?.Version ?? "未知",
                definition?.Category.ToString() ?? "未知",
                snapshot.ToolCount,
                snapshot.HasPageContribution,
                snapshot.HasComposerContribution,
                definition?.Description ?? "插件目录没有此条目的说明。");
        }).ToArray();

        var failures = input.PluginLoadErrors.Select(pair =>
        {
            var definition = PluginMarketplaceCatalog.Find(pair.Key);
            return new FuiPluginLoadFailure(pair.Key, definition?.Name ?? pair.Key, pair.Value);
        }).OrderBy(item => item.Id, StringComparer.OrdinalIgnoreCase).ToArray();

        var servers = input.McpServers.Select(server => new FuiMcpServerFact(
            server.Name,
            server.Transport,
            SafeTarget(server),
            server.Auth?.Type,
            server.Enabled)).ToArray();

        return new FuiRuntimeCatalog(
            capabilities,
            loaded,
            failures,
            servers,
            input.HasMcpFactSource,
            input.HasMcpFactSource ? null : FuiRuntimeCatalogWorkbench.EmptyMcp,
            input.PlanMode);
    }

    private static string SafeTarget(McpServerConfig server)
    {
        if (server.Transport == "stdio")
        {
            var command = string.IsNullOrWhiteSpace(server.Command) ? "未提供命令" : Path.GetFileName(server.Command);
            var count = server.Args?.Count ?? 0;
            return command + (count == 0 ? "" : $" · 参数 {count} 项");
        }

        if (!Uri.TryCreate(server.Url, UriKind.Absolute, out var uri))
            return string.IsNullOrWhiteSpace(server.Url) ? "未提供 URL" : "URL 无法显示";
        var path = string.IsNullOrEmpty(uri.AbsolutePath) || uri.AbsolutePath == "/" ? "" : uri.AbsolutePath;
        return uri.Scheme + "://" + uri.Host + (uri.IsDefaultPort ? "" : ":" + uri.Port) + path;
    }
}
