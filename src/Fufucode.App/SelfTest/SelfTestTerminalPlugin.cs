using System;
using System.IO;
using Fufucode.Core;
using Fufucode.Core.Plugins;

namespace Fufucode.App.SelfTest;

/// <summary>
/// 自测专用：终端已是可选插件，界面自测需要真实载荷。只在 <c>--selftest</c> 且能找到开发构建的插件时，
/// 把载荷安装进隔离的数据根；生产路径不受影响，也不会把插件打进 App 包。
/// </summary>
internal static class SelfTestTerminalPlugin
{
    public static void EnsureInstalled(string dataRoot)
    {
        var target = Path.Combine(dataRoot, "plugins", "workspace.terminal");
        if (File.Exists(Path.Combine(target, "manifest.json")) &&
            File.Exists(Path.Combine(target, "Fufucode.Plugins.Terminal.dll"))) return;
        var dll = LocatePayload();
        if (dll is null) return;
        Directory.CreateDirectory(target);
        File.Copy(dll, Path.Combine(target, Path.GetFileName(dll)), true);
        var manifest = Path.Combine(Path.GetDirectoryName(dll)!, "manifest.json");
        if (File.Exists(manifest)) File.Copy(manifest, Path.Combine(target, "manifest.json"), true);
        try { new PluginStateStore(new AppDataLayout(dataRoot)).Install("workspace.terminal"); }
        catch (InvalidOperationException) { }
        catch (IOException) { }
    }

    private static string? LocatePayload()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var dev = Path.Combine(directory.FullName, "src", "Fufucode.Plugins.Terminal", "bin", "Debug", "net10.0",
                "Fufucode.Plugins.Terminal.dll");
            if (File.Exists(dev)) return dev;
            var dist = Path.Combine(directory.FullName, "out", "plugins-dist", "workspace.terminal",
                "Fufucode.Plugins.Terminal.dll");
            if (File.Exists(dist)) return dist;
            directory = directory.Parent;
        }
        return null;
    }
}
