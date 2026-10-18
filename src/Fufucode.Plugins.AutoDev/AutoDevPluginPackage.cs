using System.Threading;
using System.Threading.Tasks;
using Fufucode.Core.Plugins;

namespace Fufucode.Plugins.AutoDev;

public sealed class AutoDevPluginPackage : IPluginPackage
{
    public string Id => "automation.auto-dev";
    public string Version => "1.0.0";

    public IPluginToolHandler? CreateToolHandler(PluginPackageContext context) =>
        new AutoDevToolHandler(context);
}
