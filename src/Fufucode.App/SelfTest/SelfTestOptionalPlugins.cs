using System;
using System.IO;
using Fufucode.Core;
using Fufucode.Core.Plugins;

namespace Fufucode.App.SelfTest;

/// <summary>自测专用：把可选页面插件从开发输出装进隔离数据根；生产启动不安装。</summary>
internal static class SelfTestOptionalPlugins
{
    private static readonly (string Id, string Dll, string Project)[] Plugins =
    [
        ("workspace.browser", "Fufucode.Plugins.Browser.dll", "Fufucode.Plugins.Browser"),
        ("workspace.library", "Fufucode.Plugins.Library.dll", "Fufucode.Plugins.Library"),
        ("workspace.local-review", "Fufucode.Plugins.LocalReview.dll", "Fufucode.Plugins.LocalReview"),
    ];

    public static void EnsureInstalled(string dataRoot)
    {
        foreach (var plugin in Plugins)
        {
            var target = Path.Combine(dataRoot, "plugins", plugin.Id);
            if (File.Exists(Path.Combine(target, "manifest.json")) &&
                File.Exists(Path.Combine(target, plugin.Dll))) continue;
            var dll = LocatePayload(plugin.Project, plugin.Dll);
            if (dll is null) continue;
            Directory.CreateDirectory(target);
            File.Copy(dll, Path.Combine(target, plugin.Dll), true);
            var manifest = Path.Combine(Path.GetDirectoryName(dll)!, "manifest.json");
            if (File.Exists(manifest)) File.Copy(manifest, Path.Combine(target, "manifest.json"), true);
            try { new PluginStateStore(new AppDataLayout(dataRoot)).Install(plugin.Id); }
            catch (InvalidOperationException) { }
            catch (IOException) { }
        }
    }

    private static string? LocatePayload(string project, string dllName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var dev = Path.Combine(directory.FullName, "src", project, "bin", "Debug", "net10.0", dllName);
            if (File.Exists(dev)) return dev;
            var dist = Path.Combine(directory.FullName, "out", "plugins-dist",
                project.Replace("Fufucode.Plugins.", "workspace.").ToLowerInvariant(), dllName);
            if (File.Exists(dist)) return dist;
            directory = directory.Parent;
        }
        return null;
    }
}
