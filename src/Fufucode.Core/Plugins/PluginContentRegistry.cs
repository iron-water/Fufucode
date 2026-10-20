using System;
using System.Collections.Generic;

namespace Fufucode.Core.Plugins;

/// <summary>
/// 消息富内容渲染器的登记表：插件激活时注册围栏语言，停用时注销。
///
/// 用静态表而不是事件链，是因为渲染发生在逐条消息的模板实例化里，
/// 每次都要问"这个语言现在归谁渲染"，不适合走订阅广播。
/// 同一语言只允许一个插件占用；后注册者覆盖前者，注销时按插件 id 精确移除。
/// </summary>
public static class PluginContentRegistry
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, (string PluginId, PluginContentContribution Contribution)> Entries =
        new(StringComparer.OrdinalIgnoreCase);

    public static void Register(string pluginId, PluginContentContribution contribution)
    {
        if (string.IsNullOrWhiteSpace(pluginId) || string.IsNullOrWhiteSpace(contribution.FenceLanguage)) return;
        lock (Gate)
        {
            Entries[contribution.FenceLanguage] = (pluginId, contribution);
        }
    }

    /// <summary>注销某插件注册的全部语言；被别的插件接管的语言保持不动。</summary>
    public static void Unregister(string pluginId)
    {
        lock (Gate)
        {
            var owned = new List<string>();
            foreach (var pair in Entries)
            {
                if (string.Equals(pair.Value.PluginId, pluginId, StringComparison.Ordinal)) owned.Add(pair.Key);
            }
            foreach (var key in owned) Entries.Remove(key);
        }
    }

    public static bool IsContentLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language)) return false;
        lock (Gate)
        {
            return Entries.ContainsKey(language);
        }
    }

    /// <summary>按语言创建插件视图；未注册或插件抛异常时返回 null，由调用方降级为源码显示。</summary>
    public static object? CreateView(string language, PluginContentContext context)
    {
        PluginContentContribution? contribution;
        lock (Gate)
        {
            if (!Entries.TryGetValue(language, out var entry)) return null;
            contribution = entry.Contribution;
        }

        try
        {
            return contribution.CreateView(context);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
