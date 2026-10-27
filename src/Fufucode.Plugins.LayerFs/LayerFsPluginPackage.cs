using Fufucode.App.Features.Plugins;
using Fufucode.Core.Plugins;

namespace Fufucode.Plugins.LayerFs;

public sealed class LayerFsPluginPackage : IPluginPackage
{
    private readonly List<LayerFsPage> _pages = [];
    public string Id => "workspace.layerfs";
    public string Version => "1.0.0";
    public IPluginToolHandler? CreateToolHandler(PluginPackageContext context) => null;

    public PluginPageContribution? CreatePageContribution(PluginPackageContext context)
    {
        LayerFsPage? current = null;
        var root = context.DataRoot ?? Path.GetDirectoryName(Path.GetDirectoryName(context.PackageDirectory))!;
        return new PluginPageView("layerfs", "LayerFS", "Folder", Id, () =>
        {
            current = new LayerFsPage(context.PackageDirectory, root);
            _pages.Add(current);
            return current;
        }, path => current?.SetProject(path)).Contribution;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        foreach (var page in _pages) page.Dispose();
        _pages.Clear();
        return Task.CompletedTask;
    }
}
