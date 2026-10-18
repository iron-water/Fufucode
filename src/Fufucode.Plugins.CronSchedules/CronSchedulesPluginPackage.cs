using System.Threading;
using System.Threading.Tasks;
using Fufucode.Core.Plugins;

namespace Fufucode.Plugins.CronSchedules;

public sealed class CronSchedulesPluginPackage : IPluginPackage
{
    public string Id => "automation.cron";
    public string Version => "1.0.0";

    public IPluginToolHandler? CreateToolHandler(PluginPackageContext context) => null;
}
