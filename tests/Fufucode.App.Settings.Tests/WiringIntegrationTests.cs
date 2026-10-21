using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Fufucode.App.ViewModels;
using Fufucode.Core;
using Fufucode.Core.Backend;
using Fufucode.Core.Plugins;

namespace Fufucode.App.Settings.Tests;

/// <summary>
/// 设置 → 运行时管理器 → 页面/工具的三层接线测试。
/// 设置页拥有自己的 PluginPackageManager；工作区宿主拥有另一个。两者只共享
/// plugins.json 与 PluginStateNotifier，本测试证明跨实例写入会收敛，而不是旧快照。
/// </summary>
internal static class WiringIntegrationTests
{
    private const string PluginId = "agent.browser-use";
    private const string PageId = "wiring-test-page";
    private const string ToolName = "wiring_test_tool";

    public static int Run(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "fufucode-wiring-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var passed = 0;
        void Check(bool condition, string name) { check(condition, name); passed++; }
        PluginPackageManager.RegisterPackageFactory(PluginId, () => new FakePackage());
        try
        {
            var layout = new AppDataLayout(root);
            var runtimeStore = new PluginStateStore(layout);
            var runtimeGateway = new PluginCapabilityGateway(runtimeStore);
            var runtimeTools = new PluginToolRegistry(runtimeGateway);
            var runtime = new PluginPackageManager(layout, runtimeTools, runtimeStore);
            runtime.InitializeInstalledPlugins();

            var settings = new SettingsPageViewModel();
            settings.Attach(PluginGatingTests.BuildContext(root));
            var item = Find(settings, PluginId);
            Check(!item.IsInstalled && !settings.IsCdpActive, "初始状态：设置页与运行时均未激活");

            settings.InstallOrUninstallPlugin(item);
            Check(item.IsInstalled && settings.IsCdpActive, "设置页安装后自身能力门禁解锁");
            Check(runtime.PageContribution(PluginId)?.PageId == PageId,
                "独立运行时管理器收到状态通知并挂载插件页贡献");
            Check(runtimeTools.ActiveToolNames.Contains(ToolName),
                "独立运行时管理器注册了插件工具");

            settings.TogglePluginEnabled(item);
            Check(item.IsInstalled && !item.IsEnabled && !settings.IsCdpActive,
                "设置页停用后安装状态保留、能力关闭");
            Check(runtime.PageContribution(PluginId) is null,
                "停用同步移除运行时页面贡献");
            Check(!runtimeTools.ActiveToolNames.Contains(ToolName),
                "停用同步撤销运行时工具");

            settings.TogglePluginEnabled(item);
            Check(runtime.PageContribution(PluginId)?.PageId == PageId &&
                  runtimeTools.ActiveToolNames.Contains(ToolName),
                "重新启用后页面与工具同时恢复");
        }
        finally
        {
            PluginPackageManager.UnregisterPackageFactory(PluginId);
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
        return passed;
    }

    private static PluginItemViewModel Find(SettingsPageViewModel settings, string id)
    {
        foreach (var item in settings.MarketplacePlugins)
            if (item.Id == id) return item;
        throw new InvalidOperationException("插件条目缺失：" + id);
    }

    private sealed class FakePackage : IPluginPackage
    {
        public string Id => PluginId;
        public string Version => "1.0.0";
        public IPluginToolHandler? CreateToolHandler(PluginPackageContext context) => new FakeHandler();
        public PluginPageContribution? CreatePageContribution(PluginPackageContext context) =>
            new(PageId, "接线测试页", "Globe", "test.module", () => new object());
    }

    private sealed class FakeHandler : IPluginToolHandler
    {
        public IReadOnlyList<PluginToolRegistration> Registrations { get; } =
        [
            new(ToolName, "接线测试工具", "{\"type\":\"object\"}", PluginCapability.BrowserControl),
        ];

        public Task<PureToolResult> ExecuteAsync(PureToolCall call, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PureToolResult(call.Id, true, "ok"));
    }
}
