using System.Threading;
using System.Threading.Tasks;
using Fufucode.App.Abstractions.Automation;
using Fufucode.App.Features.Automation;
using Fufucode.Core.Plugins;
using Fufucode.Plugins.AutoDev.Engine.Harnesses;

namespace Fufucode.Plugins.AutoDev;

public sealed class AutoDevPluginPackage : IPluginPackage
{
    public const string PluginId = "automation.auto-dev";

    private readonly AutoDevExecutionOwner _executionOwner = new();

    public string Id => PluginId;
    public string Version => "1.0.0";

    public IPluginToolHandler? CreateToolHandler(PluginPackageContext context) =>
        new AutoDevToolHandler(context);

    /// <summary>自动开发页：数据根取自宿主上下文，项目上下文由宿主回调更新。</summary>
    public PluginPageContribution? CreatePageContribution(PluginPackageContext context) =>
        new("automation", "自动开发", "Automation", "builtin.automation",
            () => CreatePage(context),
            view => (view as AutomationControlPageView)?.Detach(),
            (view, projectPath) =>
            {
                if (view is AutomationControlPageView { DataContext: AutomationControlViewModel model })
                    model.SetProjectContext(projectPath);
            });

    private static object CreatePage(PluginPackageContext context)
    {
        var page = new AutomationControlPageView();
        var model = new AutomationControlViewModel(context.DataRoot);
        model.SetProjectContext(context.ProjectPath);
        page.Attach(model);
        return page;
    }

    /// <summary>插件启用：登记 harness 协议适配器与宿主执行所有者（唯一后台）。</summary>
    public Task InitializeAsync(PluginPackageContext context, CancellationToken cancellationToken = default)
    {
        BatchHarnessAdapters.Register();
        ExecutionBaseLibrary.Register(_executionOwner);
        return Task.CompletedTask;
    }

    /// <summary>停用/卸载：注销执行所有者与 harness 适配器；运行中的未知终态保留在插件侧记录。</summary>
    public Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        ExecutionBaseLibrary.Unregister(_executionOwner);
        BatchHarnessAdapters.Unregister();
        return Task.CompletedTask;
    }
}
