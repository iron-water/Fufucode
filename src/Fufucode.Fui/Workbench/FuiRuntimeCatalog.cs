namespace Fufucode.Fui;

internal enum FuiCapabilitySource
{
    Native,
    Plugin,
    Skill,
    Mcp,
    Subagent
}

internal enum FuiCapabilityKind
{
    Tool,
    Skill,
    Agent,
    Gateway
}

internal sealed record FuiCapabilityEntry(
    string Name,
    string Description,
    FuiCapabilitySource Source,
    FuiCapabilityKind Kind,
    string? Owner = null)
{
    internal string ChoiceValue => Kind switch
    {
        FuiCapabilityKind.Skill => "skill:" + Name,
        FuiCapabilityKind.Agent => "subagent:" + Name,
        _ => Name
    };
}

internal sealed record FuiPluginRuntime(
    string Id,
    string Name,
    string Version,
    string Category,
    int ToolCount,
    bool HasPageContribution,
    bool HasComposerContribution,
    string Description);

internal sealed record FuiPluginLoadFailure(string Id, string Name, string Reason);

internal sealed record FuiMcpServerFact(
    string Name,
    string Transport,
    string Target,
    string? Authentication,
    bool Enabled);

internal sealed record FuiRuntimeCatalog(
    IReadOnlyList<FuiCapabilityEntry> Capabilities,
    IReadOnlyList<FuiPluginRuntime> LoadedPlugins,
    IReadOnlyList<FuiPluginLoadFailure> PluginFailures,
    IReadOnlyList<FuiMcpServerFact> McpServers,
    bool HasMcpFactSource,
    string? McpUnavailableReason,
    bool PlanMode)
{
    internal static FuiRuntimeCatalog Unavailable(string reason, bool planMode = false) =>
        new([], [], [], [], false, reason, planMode);

    internal IReadOnlyList<FuiChoice> CapabilityChoices(
        FuiCapabilitySource? source = null, FuiCapabilityKind? kind = null)
    {
        var entries = Capabilities
            .Where(entry => source is null || entry.Source == source)
            .Where(entry => kind is null || entry.Kind == kind)
            .OrderBy(entry => entry.Source)
            .ThenBy(entry => entry.Kind)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var choices = new List<FuiChoice>();
        foreach (var group in entries.GroupBy(entry => entry.Source))
        {
            choices.Add(GroupChoice(
                "capability-group:" + group.Key,
                $"{FuiRuntimeCatalogWorkbench.SourceLabel(group.Key)} · {group.Count()}"));
            foreach (var entry in group)
            {
                var detail = FuiRuntimeCatalogWorkbench.CapabilityDetail(entry);
                choices.Add(new FuiChoice(
                    entry.ChoiceValue,
                    $"{FuiRuntimeCatalogWorkbench.KindGlyph(entry.Kind)} {entry.Name} · {FuiRuntimeCatalogWorkbench.KindLabel(entry.Kind)}",
                    detail,
                    SearchText: $"{entry.Name}\n{entry.Description}\n{entry.Owner}"));
            }
        }
        return choices;
    }

    internal IReadOnlyList<FuiChoice> SkillChoices() => Capabilities
        .Where(entry => entry.Kind == FuiCapabilityKind.Skill)
        .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
        .Select(entry => new FuiChoice(
            entry.ChoiceValue,
            entry.Name,
            FuiRuntimeCatalogWorkbench.CapabilityDetail(entry),
            SearchText: entry.Description))
        .ToArray();

    internal IReadOnlyList<FuiChoice> ExtensionChoices()
    {
        var choices = new List<FuiChoice>();
        if (LoadedPlugins.Count == 0)
        {
            choices.Add(new FuiChoice(
                "extensions:empty:loaded",
                "已加载插件 · 0",
                "FUMO 当前没有已加载的插件。此视图不会提供安装、启用或卸载操作。"));
        }
        else
        {
            choices.Add(GroupChoice("extension-group:loaded", $"已加载插件 · {LoadedPlugins.Count}"));
            foreach (var plugin in LoadedPlugins)
            {
                var contributions = new List<string>();
                if (plugin.HasPageContribution) contributions.Add("页面");
                if (plugin.HasComposerContribution) contributions.Add("输入栏");
                var summary = $"能力 · 工具 {plugin.ToolCount}" +
                    (contributions.Count == 0 ? "" : " · 贡献 " + string.Join("/", contributions));
                choices.Add(new FuiChoice(
                    "extension:plugin:" + plugin.Id,
                    $"{StateGlyph(true)} {plugin.Name} · {plugin.Id}",
                    $"{plugin.Name}\n版本 {plugin.Version} · {plugin.Category} · 状态 已加载\n{summary}\n{plugin.Description}",
                    SearchText: $"{plugin.Id}\n{plugin.Name}\n{plugin.Description}"));
            }
        }

        if (PluginFailures.Count != 0)
        {
            choices.Add(GroupChoice("extension-group:failed", $"加载失败 · {PluginFailures.Count}"));
            foreach (var failure in PluginFailures)
            {
                choices.Add(new FuiChoice(
                    "extension:failure:" + failure.Id,
                    $"× {failure.Name} · {failure.Id}",
                    $"{failure.Name}\n状态 加载失败\n{failure.Reason}",
                    SearchText: failure.Reason));
            }
        }

        choices.Add(GroupChoice("extension-group:sources", "工具来源"));
        foreach (var source in Enum.GetValues<FuiCapabilitySource>())
        {
            var entries = Capabilities.Where(entry => entry.Source == source).ToArray();
            var kindCounts = string.Join(" · ", entries.GroupBy(entry => entry.Kind)
                .OrderBy(group => group.Key)
                .Select(group => $"{FuiRuntimeCatalogWorkbench.KindLabel(group.Key)} {group.Count()}"));
            choices.Add(new FuiChoice(
                "extension:source:" + FuiRuntimeCatalogWorkbench.SourceKey(source),
                $"{FuiRuntimeCatalogWorkbench.SourceGlyph(source)} {FuiRuntimeCatalogWorkbench.SourceLabel(source)} · {entries.Length}",
                $"来源 {FuiRuntimeCatalogWorkbench.SourceKey(source)}\n能力 {entries.Length}" +
                (kindCounts.Length == 0 ? " · 无" : "\n" + kindCounts) +
                (PlanMode && source != FuiCapabilitySource.Native
                    ? "\nPlan 模式仅公开明确只读的原生工具。"
                    : ""),
                SearchText: FuiRuntimeCatalogWorkbench.SourceKey(source)));
        }
        return choices;
    }

    internal IReadOnlyList<FuiChoice> McpChoices()
    {
        if (!HasMcpFactSource) return [];
        var choices = new List<FuiChoice>();
        if (McpServers.Count == 0)
        {
            choices.Add(new FuiChoice(
                "mcp:empty",
                "MCP 服务器 · 0",
                "FUMO 已加载 MCP 事实源，但当前会话没有 MCP 服务器。未扫描第三方配置，未联网。"));
        }
        else
        {
            choices.Add(GroupChoice("mcp-group:servers", $"MCP 服务器 · {McpServers.Count}"));
            foreach (var server in McpServers.OrderBy(server => server.Name, StringComparer.OrdinalIgnoreCase))
            {
                choices.Add(new FuiChoice(
                    "mcp:server:" + server.Name,
                    $"{StateGlyph(server.Enabled)} {server.Name} · {server.Transport}",
                    string.Join('\n', new[]
                    {
                        $"服务器 {server.Name}",
                        $"传输 {server.Transport} · 状态 {(server.Enabled ? "已加载" : "已停用")}",
                        "目标 " + server.Target,
                        "认证 " + (server.Authentication ?? "未声明"),
                        "工具发现 未执行；只读视图不会启动服务器、修改配置或联网。"
                    }),
                    SearchText: $"{server.Name}\n{server.Transport}\n{server.Target}"));
            }
        }

        var gateway = Capabilities.Where(entry => entry.Source == FuiCapabilitySource.Mcp).ToArray();
        choices.Add(GroupChoice("mcp-group:capabilities", "MCP 能力"));
        if (gateway.Length == 0)
        {
            choices.Add(new FuiChoice(
                "mcp:gateway:hidden",
                PlanMode ? "MCP 网关 · Plan 模式隐藏" : "MCP 网关 · 未提供",
                PlanMode
                    ? "Plan 模式只声明明确只读的原生工具；MCP 网关不会出现在实际可调用能力中。"
                    : "FUMO 当前没有声明 MCP 网关；此视图没有为此制造能力。"));
        }
        else
        {
            foreach (var entry in gateway)
                choices.Add(new FuiChoice(
                    entry.ChoiceValue,
                    $"{FuiRuntimeCatalogWorkbench.KindGlyph(entry.Kind)} {entry.Name}",
                    FuiRuntimeCatalogWorkbench.CapabilityDetail(entry)));
        }
        return choices;
    }

    internal static bool IsGroupChoice(FuiChoice choice) =>
        choice.Value.StartsWith("capability-group:", StringComparison.Ordinal) ||
        choice.Value.StartsWith("extension-group:", StringComparison.Ordinal) ||
        choice.Value.StartsWith("mcp-group:", StringComparison.Ordinal);

    private static FuiChoice GroupChoice(string value, string label) =>
        new(value, label, SearchText: label);

    private static string StateGlyph(bool available) => available ? "●" : "○";
}

internal static class FuiRuntimeCatalogWorkbench
{
    internal const string CapabilitiesFooter = "Tab 来源 · Shift+Tab 类型 · ↑↓ 选择 · Enter 查看 · Esc 关闭";
    internal const string ExtensionsFooter = "只读 · ↑↓ 选择 · Enter 查看 · Esc 关闭";
    internal const string McpFooter = "只读 · 不扫配置/不联网 · ↑↓ 选择 · Enter 查看 · Esc 关闭";
    internal const string EmptyCapabilities = "FUMO 未提供可证明的能力目录。";
    internal const string EmptyExtensions = "当前会话未提供插件运行事实源。";
    internal const string EmptyMcp = "当前会话未提供 MCP 事实源；未扫描第三方配置文件，未联网。";

    internal static string SourceKey(FuiCapabilitySource source) => source switch
    {
        FuiCapabilitySource.Native => "native",
        FuiCapabilitySource.Plugin => "plugin",
        FuiCapabilitySource.Skill => "skill",
        FuiCapabilitySource.Mcp => "mcp",
        FuiCapabilitySource.Subagent => "subagent",
        _ => "unknown"
    };

    internal static string SourceLabel(FuiCapabilitySource source) => source switch
    {
        FuiCapabilitySource.Native => "原生",
        FuiCapabilitySource.Plugin => "插件",
        FuiCapabilitySource.Skill => "技能",
        FuiCapabilitySource.Mcp => "MCP",
        FuiCapabilitySource.Subagent => "子代理",
        _ => "未知"
    };

    internal static string SourceGlyph(FuiCapabilitySource source) => source switch
    {
        FuiCapabilitySource.Native => "◆",
        FuiCapabilitySource.Plugin => "■",
        FuiCapabilitySource.Skill => "◇",
        FuiCapabilitySource.Mcp => "◇",
        FuiCapabilitySource.Subagent => "●",
        _ => "?"
    };

    internal static string KindLabel(FuiCapabilityKind kind) => kind switch
    {
        FuiCapabilityKind.Tool => "工具",
        FuiCapabilityKind.Skill => "技能",
        FuiCapabilityKind.Agent => "代理",
        FuiCapabilityKind.Gateway => "网关",
        _ => "能力"
    };

    internal static string KindGlyph(FuiCapabilityKind kind) => kind switch
    {
        FuiCapabilityKind.Tool => "▸",
        FuiCapabilityKind.Skill => "◇",
        FuiCapabilityKind.Agent => "●",
        FuiCapabilityKind.Gateway => "⇄",
        _ => "·"
    };

    internal static string CapabilityDetail(FuiCapabilityEntry entry)
    {
        var owner = string.IsNullOrWhiteSpace(entry.Owner) ? "" : $"\n归属 {entry.Owner}";
        var invocation = entry.Kind switch
        {
            FuiCapabilityKind.Skill => $"\n调用 /skill:{entry.Name}",
            FuiCapabilityKind.Agent => $"\n调用 subagent · {entry.Name}",
            _ => ""
        };
        return $"{SourceKey(entry.Source)} · {KindLabel(entry.Kind)} · {entry.Description}{owner}{invocation}";
    }

    internal static string CapabilityTitle(FuiCapabilitySource? source, FuiCapabilityKind? kind) =>
        "能力目录 · " +
        (source is { } selectedSource ? SourceLabel(selectedSource) : "全部来源") + " · " +
        (kind is { } selectedKind ? KindLabel(selectedKind) : "全部类型");

    internal static FuiCapabilitySource? NextSource(FuiCapabilitySource? source, bool backward = false)
    {
        var values = Enum.GetValues<FuiCapabilitySource>();
        if (source is null) return backward ? values[^1] : values[0];
        var index = Array.IndexOf(values, source.Value);
        index = backward ? index - 1 : index + 1;
        return index < 0 || index >= values.Length ? null : values[index];
    }

    internal static FuiCapabilityKind? NextKind(FuiCapabilityKind? kind, bool backward = false)
    {
        var values = Enum.GetValues<FuiCapabilityKind>();
        if (kind is null) return backward ? values[^1] : values[0];
        var index = Array.IndexOf(values, kind.Value);
        index = backward ? index - 1 : index + 1;
        return index < 0 || index >= values.Length ? null : values[index];
    }

}
