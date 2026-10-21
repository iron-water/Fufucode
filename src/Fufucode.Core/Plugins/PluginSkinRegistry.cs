using System;
using System.Collections.Generic;
using System.Linq;

namespace Fufucode.Core.Plugins;

/// <summary>
/// 已激活插件提供的皮肤贡献注册表。
/// 默认安装只内置 Fluent；Metro / Material 3 / Aero / Luna / Liquid Glass 等皮肤
/// 由插件市场安装并在插件激活时登记，卸载/停用后立即从皮肤选择器消失。
/// </summary>
public static class PluginSkinRegistry
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, PluginSkinContribution> Entries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>插件皮肤集合发生变化（安装、启用、停用、卸载）。</summary>
    public static event Action? Changed;

    /// <summary>当前可用（已安装且已启用）的插件皮肤。</summary>
    public static IReadOnlyList<PluginSkinContribution> Contributions
    {
        get
        {
            lock (Gate) return Entries.Values.ToArray();
        }
    }

    /// <summary>插件激活后登记其皮肤贡献；同一插件重复登记按覆盖处理。</summary>
    public static void Register(string pluginId, PluginSkinContribution contribution)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentNullException.ThrowIfNull(contribution);
        bool changed;
        lock (Gate)
        {
            changed = !Entries.TryGetValue(pluginId, out var existing) || !Equals(existing, contribution);
            Entries[pluginId] = contribution;
        }
        if (changed) Changed?.Invoke();
    }

    /// <summary>插件停用/卸载时撤销贡献。</summary>
    public static void Unregister(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        bool removed;
        lock (Gate) removed = Entries.Remove(pluginId);
        if (removed) Changed?.Invoke();
    }

    /// <summary>按持久化键查找插件皮肤（大小写不敏感）。</summary>
    public static PluginSkinContribution? Find(string? storageKey)
    {
        if (string.IsNullOrWhiteSpace(storageKey)) return null;
        var key = storageKey.Trim();
        lock (Gate) return Entries.Values.FirstOrDefault(c => string.Equals(c.StorageKey, key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>清空注册表（测试用；不触发业务副作用）。</summary>
    public static void Reset()
    {
        bool changed;
        lock (Gate) { changed = Entries.Count > 0; Entries.Clear(); }
        if (changed) Changed?.Invoke();
    }
}
