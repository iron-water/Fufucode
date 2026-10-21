using System;
using System.IO;
using System.Threading.Tasks;
using Fufucode.App.Features.Plugins;
using Fufucode.App.Infrastructure;
using Fufucode.App.ViewModels;
using Fufucode.Core;
using Fufucode.Core.Plugins;

namespace Fufucode.App.Settings.Tests;

/// <summary>
/// 设置页的插件能力门禁定向测试：
/// 未安装时显示“未安装：请在插件市场中安装 Browser Use 扩展”且禁止打开 CDP 开关；
/// 安装并启用后解锁；卸载后回到未安装态；未启用与未安装的文案必须不同。
/// 使用真实临时数据根 + 真实 plugins.json，不伪造插件状态。
/// </summary>
internal static class PluginGatingTests
{
    public static int Run(Action<bool, string> check)
    {
        var passed = 0;
        var root = Path.Combine(Path.GetTempPath(), "fufucode-plugin-gating-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            void Check(bool condition, string name) { check(condition, name); passed++; }
            var dataRoot = Path.Combine(root, "appdata");
            Directory.CreateDirectory(dataRoot);

            var customization = new WorkbenchCustomizationViewModel(new WorkbenchAppearanceStore(dataRoot));

            // ── 未安装 ──
            Check(!PluginAccess.AdvancedControlEnabled(customization), "未安装时浏览器高级控制不可用");
            Check(PluginAccess.Status(customization) == PluginAccess.BrowserUseNotInstalledMessage,
                "未安装时状态文案为“未安装：请在插件市场中安装 Browser Use 扩展”");
            Check(PluginAccess.Status(customization).Contains("插件市场", StringComparison.Ordinal),
                "未安装文案给出插件市场入口指向");
            Check(!PluginAccess.ComputerControlEnabled(customization), "未安装时电脑操控不可用");

            // ── 安装并启用 ──
            // 状态存储把“没有真实载荷的已安装”如实降级为未安装；夹具先建立载荷契约，
            // 这样测到的是“安装后能力解锁”，而不是绕过生产门禁。
            SeedPayload(dataRoot, PluginCapabilityRegistry.ProviderId(PluginCapability.BrowserControl)!);
            SeedPayload(dataRoot, PluginCapabilityRegistry.ProviderId(PluginCapability.ComputerControl)!);
            var store = new PluginStateStore(new AppDataLayout(dataRoot));
            store.Install(PluginCapabilityRegistry.ProviderId(PluginCapability.BrowserControl)!);
            Check(PluginAccess.AdvancedControlEnabled(customization), "安装并启用后浏览器高级控制解锁");
            Check(PluginAccess.Status(customization) != PluginAccess.BrowserUseNotInstalledMessage,
                "解锁后不再显示未安装文案");
            Check(!PluginAccess.ComputerControlEnabled(customization), "仅装浏览器插件不影响电脑操控门禁");

            // ── 停用 ≠ 未安装 ──
            store.SetEnabled(PluginCapabilityRegistry.ProviderId(PluginCapability.BrowserControl)!, false);
            Check(!PluginAccess.AdvancedControlEnabled(customization), "停用后浏览器高级控制重新不可用");
            var disabledText = PluginAccess.Status(customization);
            Check(disabledText != PluginAccess.BrowserUseNotInstalledMessage,
                "停用文案与未安装文案必须不同");
            Check(disabledText.Contains("停用", StringComparison.Ordinal), "停用文案如实说明已停用");

            // ── 卸载回到未安装 ──
            store.Install(PluginCapabilityRegistry.ProviderId(PluginCapability.BrowserControl)!);
            store.Uninstall(PluginCapabilityRegistry.ProviderId(PluginCapability.BrowserControl)!);
            Check(!PluginAccess.AdvancedControlEnabled(customization), "卸载后浏览器高级控制再次不可用");
            Check(PluginAccess.Status(customization) == PluginAccess.BrowserUseNotInstalledMessage,
                "卸载后回到未安装文案");

            // ── 电脑操控独立解锁 ──
            store.Install(PluginCapabilityRegistry.ProviderId(PluginCapability.ComputerControl)!);
            Check(PluginAccess.ComputerControlEnabled(customization), "安装电脑操控插件后该能力解锁");
            Check(!PluginAccess.AdvancedControlEnabled(customization), "电脑操控解锁不影响 Browser Use 门禁");

            // ── 设置页 VM 的状态投影与数据根一致（卸载=不注入，安装=解锁） ──
            var settings = new SettingsPageViewModel();
            settings.AttachNavPreference(dataRoot);
            Check(!settings.IsBrowserUseInstalled, "未接线数据根时浏览器控制按未安装处理");
            Check(!settings.IsCdpActive && !settings.BrowserUseAdvancedEnabled,
                "未接线数据根时不渲染可用的 CDP 开关");
            Check(settings.BrowserUseUnavailableMessage == PluginAccess.BrowserUseNotInstalledMessage,
                "未接线数据根时显示未安装提示");
            settings.InstallOrUninstallPlugin(Find(settings, "agent.computer-use"));
            Check(!settings.IsComputerUseInstalled && settings.ComputerUseStatusText.Contains("未安装", StringComparison.Ordinal),
                "mock 无数据根时安装命令不伪造已安装状态");

            // ── SEEP-1：Seep 逆向工作台在市场页与门禁上的真实投影 ──
            Check(!PluginAccess.ReverseLabEnabled(customization), "未安装时 Seep 工作台不可用");
            Check(PluginAccess.ReverseLabStatus(customization).StartsWith("未安装", StringComparison.Ordinal),
                "未安装时 Seep 工作台显示安装指引而不是可用开关");
            Check(PluginAccess.ReverseLabStatus(customization).Contains("插件市场", StringComparison.Ordinal),
                "Seep 未安装文案给出插件市场入口指向");

            var reverseLabStore = new PluginStateStore(new AppDataLayout(dataRoot));
            Check(!reverseLabStore.GetState(PluginAccess.ReverseLabPluginId).IsInstalled,
                "Seep 工作台默认未安装（非必需插件不预先安装）");
            reverseLabStore.Install(PluginAccess.ReverseLabPluginId);
            Check(PluginAccess.ReverseLabEnabled(customization), "安装并启用后 Seep 工作台解锁");
            reverseLabStore.SetEnabled(PluginAccess.ReverseLabPluginId, false);
            Check(!PluginAccess.ReverseLabEnabled(customization), "停用后 Seep 工作台重新不可用");
            Check(PluginAccess.ReverseLabStatus(customization).Contains("停用", StringComparison.Ordinal),
                "Seep 停用文案如实说明已停用，不为“未安装”");
            reverseLabStore.Uninstall(PluginAccess.ReverseLabPluginId);
            Check(PluginAccess.ReverseLabStatus(customization).StartsWith("未安装", StringComparison.Ordinal),
                "Seep 卸载后回到未安装文案");

            // 市场页条目投影：真实列出 Seep 插件，未安装时不渲染可用开关。
            settings.Attach(BuildContext(dataRoot));
            var seep = Find(settings, PluginAccess.ReverseLabPluginId);
            Check(seep.CategoryName == "工具扩展", "Seep 工作台在市场页归类为工具扩展");
            Check(!seep.IsInstalled && !seep.IsEnabled, "Seep 工作台在市场页如实显示未安装未启用");
            Check(!settings.IsReverseLabInstalled && !settings.ReverseLabEnabled,
                "Seep 未安装时设置页不渲染可用开关");
            Check(settings.ReverseLabStatusText.Contains("未安装", StringComparison.Ordinal),
                "Seep 未安装时设置页显示未安装状态文本");

            settings.InstallOrUninstallPlugin(seep);
            Check(settings.IsReverseLabInstalled && settings.ReverseLabEnabled,
                "市场页安装 Seep 后设置页状态与门禁同步解锁");
            Check(!settings.ReverseLabStatusText.Contains("未安装", StringComparison.Ordinal),
                "Seep 解锁后不再显示未安装文案");
            settings.TogglePluginEnabled(seep);
            Check(seep.IsInstalled && !seep.IsEnabled, "Seep 停用后仍是已安装（安装 ≠ 启用）");
            Check(!settings.ReverseLabEnabled && settings.ReverseLabStatusText.Contains("停用", StringComparison.Ordinal),
                "Seep 停用后设置页如实显示停用并关闭开关");
            settings.TogglePluginEnabled(seep);
            Check(settings.ReverseLabEnabled, "Seep 重新启用后设置页门禁恢复");
            settings.InstallOrUninstallPlugin(seep);
            Check(!settings.IsReverseLabInstalled && settings.ReverseLabStatusText.Contains("未安装", StringComparison.Ordinal),
                "Seep 从市场页卸载后回到未安装态");
            _ = passed;
            return passed;
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
            catch (IOException) { /* 清理失败不影响结论 */ }
        }
    }

    /// <summary>建立最小真实载荷（manifest），满足 PluginStateStore 的“已安装”契约。</summary>
    private static void SeedPayload(string dataRoot, string pluginId)
    {
        var directory = Path.Combine(dataRoot, "plugins", pluginId);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "manifest.json"),
            "{\"id\":\"" + pluginId + "\",\"version\":\"1.0.0\",\"entryAssembly\":\"plugin.dll\"}");
    }

    private static PluginItemViewModel Find(SettingsPageViewModel settings, string id)
    {
        foreach (var item in settings.MarketplacePlugins)
            if (item.Id == id) return item;
        throw new InvalidOperationException("插件条目缺失：" + id);
    }

    /// <summary>构造最小真实运行上下文：只接线数据根，其余回调返回固定只读值。</summary>
    internal static SettingsPageViewModel.Context BuildContext(string dataRoot) =>
        new(
            Store: new AppSettingsStore(new AppDataLayout(dataRoot)),
            IsMock: false,
            CurrentModel: () => null,
            QueryModels: null,
            AgentDirectory: null,
            DataRoot: dataRoot,
            LogsDirectory: null,
            ApprovalText: () => null,
            ApprovalDiagnostic: () => null,
            ActiveExtensionPaths: () => Array.Empty<string>(),
            LastStartupFailure: () => null,
            ProjectPath: () => "(test)",
            OpenFolder: _ => Task.CompletedTask,
            CopyText: _ => Task.CompletedTask,
            PiVersion: () => "(test)",
            NodeVersion: () => "(test)");
}

