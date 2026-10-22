using Fufucode.App.Features.Plugins;
using Fufucode.Core.Plugins;

namespace Fufucode.Plugins.Browser;

/// <summary>
/// 可选内置浏览器页插件。页面实现仍由宿主提供（WebView2、设置桥、聊天引用都在 App），
/// 插件只声明安装生命周期和页面贡献；未安装时宿主不会创建浏览器页。
/// </summary>
public sealed class BrowserPluginPackage : IPluginPackage
{
    public string Id => "workspace.browser";
    public string Version => "1.0.0";

    public IPluginToolHandler? CreateToolHandler(PluginPackageContext context) => null;

    public PluginPageContribution? CreatePageContribution(PluginPackageContext context)
    {
        var factory = context.GetService<IBrowserPageFactory>()
            ?? throw new InvalidOperationException("宿主没有提供浏览器页面工厂。");
        return new PluginPageContribution(
            PageId: "browser",
            Title: "浏览器",
            IconKey: "Globe",
            ModuleId: "workspace.browser",
            CreateView: () => factory.CreatePage());
    }
}
