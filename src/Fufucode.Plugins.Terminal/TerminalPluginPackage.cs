using System;
using Fufucode.Core.Plugins;

namespace Fufucode.Plugins.Terminal;

/// <summary>交互终端插件包入口，由宿主插件管理器按 manifest 动态实例化。</summary>
public sealed class TerminalPluginPackage : IPluginPackage
{
    public string Id => "workspace.terminal";
    public string Version => "2.0.0";

    public IPluginToolHandler? CreateToolHandler(PluginPackageContext context) => null;

    public PluginPageContribution? CreatePageContribution(PluginPackageContext context) =>
        new(
            PageId: "terminal",
            Title: "终端",
            IconKey: "Terminal",
            ModuleId: "workspace.terminal",
            CreateView: () => new TerminalDockPage(PageDataRoot(context)));

    /// <summary>
    /// 插件自己的设置目录。包目录布局为 &lt;dataRoot&gt;/plugins/&lt;id&gt;，因此数据根是它的上两级；
    /// 无法判定时退回包目录（设置仍能保存，只是落在插件目录内）。
    /// </summary>
    private static string PageDataRoot(PluginPackageContext context)
    {
        var package = System.IO.Path.GetFullPath(context.PackageDirectory);
        var plugins = System.IO.Path.GetDirectoryName(package);
        var root = plugins is null ? null : System.IO.Path.GetDirectoryName(plugins);
        return string.IsNullOrEmpty(root) ? package : root;
    }
}
