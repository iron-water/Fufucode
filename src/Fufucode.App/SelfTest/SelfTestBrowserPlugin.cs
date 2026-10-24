using System;
using System.IO;
using Fufucode.Core;
using Fufucode.Core.Plugins;

namespace Fufucode.App.SelfTest;

/// <summary>
/// 自测专用：浏览器已是可选插件，界面自测需要真实载荷。
/// 只把开发构建的插件安装进隔离数据根；生产启动不会安装它。
/// </summary>
internal static class SelfTestBrowserPlugin
{
    public static void EnsureInstalled(string dataRoot)
    {
        var target = Path.Combine(dataRoot, "plugins", "workspace.browser");
        if (File.Exists(Path.Combine(target, "manifest.json")) &&
            File.Exists(Path.Combine(target, "Fufucode.Plugins.Browser.dll"))) return;
        var dll = LocatePayload();
        if (dll is null) return;
        Directory.CreateDirectory(target);
        File.Copy(dll, Path.Combine(target, Path.GetFileName(dll)), true);
        var manifest = Path.Combine(Path.GetDirectoryName(dll)!, "manifest.json");
        if (File.Exists(manifest)) File.Copy(manifest, Path.Combine(target, "manifest.json"), true);
        try { new PluginStateStore(new AppDataLayout(dataRoot)).Install("workspace.browser"); }
        catch (InvalidOperationException) { }
        catch (IOException) { }
    }

    private static string? LocatePayload()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var dev = Path.Combine(directory.FullName, "src", "Fufucode.Plugins.Browser", "bin", "Debug", "net10.0",
                "Fufucode.Plugins.Browser.dll");
            if (File.Exists(dev)) return dev;
            var dist = Path.Combine(directory.FullName, "out", "plugins-dist", "workspace.browser",
                "Fufucode.Plugins.Browser.dll");
            if (File.Exists(dist)) return dist;
            directory = directory.Parent;
        }
        return null;
    }
}
