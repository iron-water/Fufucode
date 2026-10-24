using Fufucode.App.Features.Plugins;
using Fufucode.Core.Plugins;

namespace Fufucode.Plugins.LocalReview;

/// <summary>可选本地审查页插件；页面实现仍由宿主组合根提供。</summary>
public sealed class LocalReviewPluginPackage : IPluginPackage
{
    public string Id => "workspace.local-review";
    public string Version => "1.0.0";

    public IPluginToolHandler? CreateToolHandler(PluginPackageContext context) => null;

    public PluginPageContribution? CreatePageContribution(PluginPackageContext context)
    {
        var factory = context.GetService<ILocalReviewPageFactory>()
            ?? throw new InvalidOperationException("宿主没有提供本地审查页面工厂。");
        return new PluginPageContribution(
            PageId: "local-review",
            Title: "本地审查",
            IconKey: "ShieldCheckmark",
            ModuleId: "workspace.local-review",
            CreateView: () => factory.CreatePage());
    }
}
