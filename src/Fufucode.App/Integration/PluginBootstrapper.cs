using System;
using System.Collections.Generic;
using System.IO;
using Fufucode.Core;
using Fufucode.Core.Plugins;

namespace Fufucode.App.Integration;

/// <summary>
/// 启动期把默认安装的插件从分发载荷部署到 `<c>&lt;dataRoot&gt;/plugins/&lt;id&gt;/</c>`。
/// `PluginStateStore` 只按磁盘载荷判断“已安装”，没有载荷的默认安装项会被当成未安装，
/// 因此每次启动都做一次幂等同步：已存在则跳过，不存在则从 `plugins-dist` 复制。
/// </summary>
public static class PluginBootstrapper
{
    /// <summary>本进程需要默认随包的插件；其它可选插件不在这里分发。</summary>
    private static readonly string[] BundledPluginIds = ["performance.monitor"];

    /// <summary>把默认随包的插件部署到数据根；返回每个插件的状态与原因。</summary>
    public static IReadOnlyList<(string Id, bool Ready, string? Reason)> EnsureBundled(AppDataLayout layout)
    {
        var results = new List<(string, bool, string?)>(BundledPluginIds.Length);
        var stateStore = new PluginStateStore(layout);
        foreach (var id in BundledPluginIds)
        {
            try
            {
                // 用户明确卸载过：不重新部署载荷，尊重卸载决定。
                if (stateStore.TryGetStoredState(id, out var stored) && !stored.IsInstalled)
                {
                    results.Add((id, false, "用户已卸载"));
                    continue;
                }

                var target = Path.Combine(layout.PluginsDirectory, id);
                var manifest = Path.Combine(target, "manifest.json");
                if (File.Exists(manifest))
                {
                    results.Add((id, true, null));
                    continue;
                }

                var source = Path.Combine(AppContext.BaseDirectory, "plugins-dist", id);
                if (!Directory.Exists(source))
                {
                    results.Add((id, false, $"分发载荷缺失：{source}"));
                    continue;
                }

                Directory.CreateDirectory(target);
                CopyTree(source, target);
                results.Add((id, File.Exists(manifest), File.Exists(manifest) ? null : "复制后 manifest 缺失"));
            }
            catch (IOException error)
            {
                results.Add((id, false, "部署失败：" + error.Message));
            }
            catch (UnauthorizedAccessException error)
            {
                results.Add((id, false, "部署失败：" + error.Message));
            }
        }
        return results;
    }

    private static void CopyTree(string source, string target)
    {
        foreach (var file in Directory.EnumerateFiles(source))
        {
            var name = Path.GetFileName(file);
            // 宿主已加载的共享程序集不复制进插件目录：PackageLoadContext 先解析宿主程序集，
            // 复制进来只会在升级后留下过期副本。
            if (IsHostAssembly(name)) continue;
            File.Copy(file, Path.Combine(target, name), overwrite: true);
        }
        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            var name = Path.GetFileName(directory);
            if (string.Equals(name, "runtimes", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, ".shared-packages", StringComparison.OrdinalIgnoreCase)) continue;
            CopyTree(directory, Path.Combine(target, name));
        }
    }

    private static bool IsHostAssembly(string name) =>
        name.StartsWith("Fufucode.Core", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("Fufucode.App", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("Fufucode.Cli", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("Avalonia", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("CommunityToolkit", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("SkiaSharp", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("SQLitePCLRaw", StringComparison.OrdinalIgnoreCase);
}
