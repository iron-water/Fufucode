using System;
using System.Collections.Generic;
using System.Linq;

namespace Fufucode.Core.Plugins;

/// <summary>
/// 已激活插件包贡献的动态 System 段（例如记忆插件每轮读取的记忆文件）。
/// 插件在 InitializeAsync 登记、ShutdownAsync 释放返回的令牌；同一插件可能被多个
/// <see cref="PluginPackageManager"/> 同时激活，任一实例停用只撤回自己的登记。
/// <see cref="PluginSystemPromptComposer"/> 每次组装时调用，并且只采用“已安装且已启用”的插件。
/// </summary>
public static class PluginPromptRegistry
{
    private static readonly object Gate = new();
    private static readonly List<Registration> Entries = [];

    private sealed class Registration(string pluginId, Func<string?> build) : IDisposable
    {
        public string PluginId { get; } = pluginId;
        public Func<string?> Build { get; } = build;
        public void Dispose()
        {
            lock (Gate) Entries.Remove(this);
        }
    }

    public static IDisposable Register(string pluginId, Func<string?> build)
    {
        if (string.IsNullOrWhiteSpace(pluginId)) throw new ArgumentException("插件 ID 为空。", nameof(pluginId));
        ArgumentNullException.ThrowIfNull(build);
        var registration = new Registration(pluginId, build);
        lock (Gate) Entries.Add(registration);
        return registration;
    }

    /// <summary>每个插件取最近一次登记，按插件 ID 稳定排序；单个插件构建失败只跳过该插件。</summary>
    internal static IReadOnlyList<(string PluginId, string Text)> Build(Func<string, bool> isActive)
    {
        Registration[] snapshot;
        lock (Gate)
        {
            snapshot = Entries
                .GroupBy(entry => entry.PluginId, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.Last())
                .OrderBy(entry => entry.PluginId, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        var result = new List<(string, string)>();
        foreach (var entry in snapshot)
        {
            if (!isActive(entry.PluginId)) continue;
            string? text;
            try { text = entry.Build(); }
            catch (Exception) { continue; }
            if (!string.IsNullOrWhiteSpace(text)) result.Add((entry.PluginId, text.Trim()));
        }
        return result;
    }
}
