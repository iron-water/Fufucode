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
            CreateView: () => new TerminalDockPage());
}
