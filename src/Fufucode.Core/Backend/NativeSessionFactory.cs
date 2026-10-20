using System.Text.Json;
using Fufucode.App.Abstractions;
using Fufucode.Core.Backend;
using Fufucode.Core.Plugins;

namespace Fufucode.Core;

/// <summary>
/// FUMO 原生内核（纯 C# 实现）的会话工厂：
/// 不启动任何外部进程，直接从应用设置中的端点配置构建模型客户端并返回原生会话。
/// 这是默认后端的持久化路径——会话记录由桌面元数据保存，历史由原生引擎维护。
/// </summary>
public static class NativeSessionFactory
{
    /// <summary>
    /// 依据设置创建会话。端点未配置时返回一个可启动但会在发送时报凭据错误的会话，
    /// 由 UI 的设置页引导用户配置；此处不伪造成功。
    /// </summary>
    public static IChatSession Create(AppSettingsStore settings, string? sessionId, string? workspaceRoot,
        IPureToolDispatcher? toolRegistry = null, IPluginSystemPromptProvider? systemPromptProvider = null,
        string? persistFilePath = null, string? apiKey = null,
        Func<IReadOnlyList<IPureToolDispatcher>>? pluginProviders = null,
        bool supportsImages = true, SkillRegistry? skills = null, McpManager? mcp = null,
        INativeSubagentRuntime? subagents = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var current = settings.Current;
        IPureModelClient? client = null;
        string? modelId = null;
        if (current.Endpoint is { } endpoint)
        {
            var key = apiKey ?? Environment.GetEnvironmentVariable(endpoint.ApiKeyEnvironmentVariable);
            client = new OpenAiCompatibleModelClient(endpoint.BaseUrl, key, endpoint.ModelId);
            // 端点模型为空时使用设置页当前选择的模型；空字符串不能覆盖 SelectedModel。
            modelId = string.IsNullOrWhiteSpace(endpoint.ModelId) ? current.SelectedModel : endpoint.ModelId;
        }
        else
        {
            modelId = current.SelectedModel;
        }

        // 内置工具始终绑定当前会话的工作区；插件工具按组合根提供的实时注册表路由。
        var builtin = new ProductionToolDispatcher(workspaceRoot);
        var baseDispatcher = toolRegistry
            ?? (pluginProviders is null ? builtin : new NativeToolComposite(builtin, pluginProviders));
        var pluginCatalog = toolRegistry as Plugins.IPluginToolCatalog
                            ?? baseDispatcher as Plugins.IPluginToolCatalog;

        // 技能是轻量的 Markdown 资源：发现失败不影响会话；skill 工具统一由注册表处理。
        skills ??= SkillRegistry.Discover(workspaceRoot);
        mcp ??= McpManager.Load(workspaceRoot);
        // 能力链：插件/内置 → Skill → MCP。子代理可按定义白名单使用这些能力。
        IPureToolDispatcher capabilities = new SkillAwareDispatcher(baseDispatcher, skills);
        capabilities = new McpGatewayDispatcher(capabilities, mcp);

        var capabilityDefinitions = new List<PureToolDefinition>();
        if (skills.HasModelInvocableSkills) capabilityDefinitions.Add(SkillRegistry.Definition);
        if (mcp.HasServers) capabilityDefinitions.Add(McpGatewayDispatcher.Definition);
        if (pluginCatalog is { } catalog)
        {
            capabilityDefinitions.AddRange(catalog.ActiveRegistrations.Select(registration =>
                new PureToolDefinition(registration.Name, registration.Description,
                    registration.ParametersJsonSchema)));
        }

        // Subagent 是可选插件能力；核心未安装时不创建、不声明、也不注入工具。
        if (subagents is null && client is not null && NativeSubagentRegistry.Factory is { } factory)
            subagents = factory.Create(client, capabilities, modelId, workspaceRoot, capabilityDefinitions);

        // 调度链：可选 Subagent → MCP → Skill → 插件/内置。
        var dispatcher = subagents?.Wrap(capabilities) ?? capabilities;

        var engine = new PureCSharpAgentEngine(dispatcher, client, maxIterations: 10);
        return new NativeCSharpChatSession(
            engine, sessionId, modelId,
            systemPromptProvider: systemPromptProvider,
            pluginTools: pluginCatalog,
            persistFilePath: persistFilePath,
            supportsImages: supportsImages,
            skills: skills,
            mcp: mcp,
            subagents: subagents);
    }

    /// <summary>FUMO 会话的持久化文件相对路径（按项目与会话 id 存放）。</summary>
    public static string SessionFilePath(AppDataLayout layout, string projectId, string sessionId) =>
        Path.Combine(layout.ProjectSessionsDirectory(projectId), "fumo", sessionId + ".json");
}
