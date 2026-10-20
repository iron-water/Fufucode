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
        SubagentRunner? subagents = null)
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
        Diagnostics.StartupProbe.Mark("fumo.skills.begin", "discover skills");
        skills ??= SkillRegistry.Discover(workspaceRoot);
        Diagnostics.StartupProbe.Mark("fumo.skills.end", $"{skills.All.Count} skills" );
        Diagnostics.StartupProbe.Mark("fumo.mcp.begin", "load MCP manager");
        mcp ??= McpManager.Load(workspaceRoot);
        Diagnostics.StartupProbe.Mark("fumo.mcp.end", $"{mcp.Servers.Count} MCP servers");
        // 子代理使用独立的内置工具调度器（不继承插件/MCP），只按 agent 定义放行工具。
        if (subagents is null && client is not null)
            subagents = new SubagentRunner(AgentRegistry.Discover(workspaceRoot), client, builtin, modelId, workspaceRoot);

        // 调度链：MCP → Skill → Subagent → 插件/内置。
        IPureToolDispatcher chain = baseDispatcher;
        if (subagents is not null) chain = new SubagentDispatcher(chain, subagents);
        chain = new SkillAwareDispatcher(chain, skills);
        var dispatcher = new McpGatewayDispatcher(chain, mcp);

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
