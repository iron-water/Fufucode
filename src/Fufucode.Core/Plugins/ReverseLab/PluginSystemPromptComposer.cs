using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Fufucode.Core.Plugins;

/// <summary>
/// 会话 System 人格片段的提供方接缝。会话每次构建请求时读取
/// <see cref="Current"/>，因此插件启停立即生效，不需要重建会话。
/// </summary>
public interface IPluginSystemPromptProvider
{
    /// <summary>当前人格片段；没有启用插件提供人格时为 null。</summary>
    string? Current { get; }
}

/// <summary>
/// 基于插件市场实时状态的人格提供方：每次读取都重新组装，
/// 停用插件后下一次请求即不再携带该插件人格。
/// </summary>
public sealed class PluginStateSystemPromptProvider : IPluginSystemPromptProvider
{
    private readonly PluginStateStore _store;
    private readonly IReadOnlyList<PluginDefinition> _catalog;

    public PluginStateSystemPromptProvider(
        PluginStateStore store, IReadOnlyList<PluginDefinition>? catalog = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _catalog = catalog ?? PluginMarketplaceCatalog.All;
    }

    public string? Current => PluginSystemPromptComposer.Compose(_store, _catalog);
}

/// <summary>
/// 会话 System 指令组装器：把当前“已安装且已启用”插件提供的人格片段
/// （<see cref="PluginDefinition.SystemPromptFragment"/>）动态拼装成一条 System 角色消息。
///
/// 边界：
/// - 只消费 <see cref="PluginStateStore"/> 的实时状态；未安装或未启用的插件不注入人格；
/// - 不缓存片段，每次组装都重新读取，因此停用插件后下一次请求立即不再带人格；
/// - 没有任何插件启用时返回 null，调用方保持原有 System 消息不变（不注入空消息）。
/// </summary>
public static class PluginSystemPromptComposer
{
    /// <summary>人格片段之间的分隔标题，便于模型区分来源插件。</summary>
    private const string FragmentHeaderPrefix = "## 插件人格：";

    /// <summary>
    /// 收集当前启用插件的人格片段，按插件 ID 稳定排序后拼装。
    /// 返回 null 表示当前没有任何启用插件提供人格，调用方不追加 System 消息。
    /// </summary>
    public static string? Compose(PluginStateStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        return Compose(store, PluginMarketplaceCatalog.All);
    }

    /// <summary>
    /// 按给定目录与状态存储组装人格；目录显式传入以便定向测试注入自定义插件定义。
    /// </summary>
    public static string? Compose(PluginStateStore store, IReadOnlyList<PluginDefinition> catalog)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(catalog);

        var fragments = new List<(string Name, string Text)>();
        foreach (var definition in catalog
                     .Where(d => !string.IsNullOrWhiteSpace(d.SystemPromptFragment))
                     .OrderBy(d => d.Id, StringComparer.OrdinalIgnoreCase))
        {
            // 只有“已安装且已启用”才注入：安装 ≠ 启用。
            if (!store.IsActive(definition.Id)) continue;
            fragments.Add((definition.Name, definition.SystemPromptFragment!));
        }

        if (fragments.Count == 0) return null;
        if (fragments.Count == 1) return fragments[0].Text;

        var builder = new StringBuilder();
        foreach (var (name, text) in fragments)
        {
            if (builder.Length > 0) builder.Append("\n\n");
            builder.Append(FragmentHeaderPrefix).Append(name).Append('\n').Append(text);
        }
        return builder.ToString();
    }

    /// <summary>
    /// 便捷方法：把人格片段追加到已有 System 文本之后。
    /// 原有文本为空时直接返回片段；片段为空时返回原文本，不产生多余空行。
    /// </summary>
    public static string AppendTo(string? existingSystemText, string? fragment)
    {
        if (string.IsNullOrEmpty(fragment)) return existingSystemText ?? string.Empty;
        if (string.IsNullOrEmpty(existingSystemText)) return fragment;
        return existingSystemText + "\n\n" + fragment;
    }
}
