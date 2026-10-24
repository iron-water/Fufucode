using Fufucode.App.Features.Plugins;
using Fufucode.Core.Plugins;

namespace Fufucode.Plugins.Library;

/// <summary>可选资料库页插件；页面实现仍由宿主组合根提供。</summary>
public sealed class LibraryPluginPackage : IPluginPackage
{
    public string Id => "workspace.library";
    public string Version => "1.0.0";

    public IPluginToolHandler? CreateToolHandler(PluginPackageContext context) => null;

    public PluginPageContribution? CreatePageContribution(PluginPackageContext context)
    {
        var factory = context.GetService<ILibraryPageFactory>()
            ?? throw new InvalidOperationException("宿主没有提供资料库页面工厂。");
        return new PluginPageContribution(
            PageId: "library",
            Title: "资料库",
            IconKey: "Folder",
            ModuleId: "workspace.library",
            CreateView: () => factory.CreatePage());
    }
}
