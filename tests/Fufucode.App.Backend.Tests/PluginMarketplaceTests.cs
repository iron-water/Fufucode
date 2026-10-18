using System;
using System.IO;
using Fufucode.Core;
using Fufucode.Core.Plugins;

namespace Fufucode.App.Backend.Tests;

/// <summary>插件市场（Plugin Marketplace）契约、默认策略矩阵与持久化存取。</summary>
internal static class PluginMarketplaceTests
{
    public static void Run(Checks checks)
    {
        CatalogDefaults(checks);
        LifecycleAndPersistence(checks);
    }

    private static void CatalogDefaults(Checks checks)
    {
        checks.Check(PluginMarketplaceCatalog.All.Count >= 7, "插件市场目录包含至少七个标准插件条目");

        var csharp = PluginMarketplaceCatalog.Find("core.csharp");
        checks.Check(csharp is not null && csharp.IsCoreBuiltin && csharp.DefaultInstalled && csharp.DefaultEnabled,
            "FUMO 原生内核为内置核心，默认已安装且已启用");

        var pi = PluginMarketplaceCatalog.Find("backend.pi");
        var omp = PluginMarketplaceCatalog.Find("backend.omp");
        var codex = PluginMarketplaceCatalog.Find("backend.codex");
        var dsh = PluginMarketplaceCatalog.Find("backend.dsh");
        var claude = PluginMarketplaceCatalog.Find("backend.claude");
        checks.Check(pi is not null && !pi.DefaultInstalled && !pi.DefaultEnabled, "Pi 后端插件默认未安装且未启用（可选插件包）");
        checks.Check(omp is not null && !omp.DefaultInstalled && !omp.DefaultEnabled, "OMP 后端插件默认未安装且未启用（可选插件包）");
        checks.Check(codex is not null && !codex.DefaultInstalled && !codex.DefaultEnabled, "Codex 后端插件默认未安装且未启用（可选插件包）");
        checks.Check(dsh is not null && !dsh.DefaultInstalled && !dsh.DefaultEnabled, "DSH 后端插件默认未安装且未启用（可选插件包）");
        checks.Check(claude is not null && !claude.DefaultInstalled && !claude.DefaultEnabled &&
            claude.Category == PluginCategory.BackendEngine,
            "Claude 后端插件默认未安装且未启用，分类为 BackendEngine");

        var browserUse = PluginMarketplaceCatalog.Find("agent.browser-use");
        var computerUse = PluginMarketplaceCatalog.Find("agent.computer-use");
        checks.Check(browserUse is { DefaultInstalled: false, DefaultEnabled: false },
            "Browser Use 浏览器控制插件默认未安装且未启用");
        checks.Check(computerUse is { DefaultInstalled: false, DefaultEnabled: false },
            "Computer Use 电脑操控插件默认未安装且未启用");
        checks.Check(browserUse!.RequiredPermissions.Count > 0, "Browser Use 明确声明所需系统权限");
        checks.Check(computerUse!.RequiredPermissions.Count > 0, "Computer Use 明确声明所需系统权限");
    }

    private static void LifecycleAndPersistence(Checks checks)
    {
        var root = Path.Combine(Path.GetTempPath(), "fufucode-plugin-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var layout = new AppDataLayout(root);
            var store = new PluginStateStore(layout);

            // 1. 默认状态查询
            var defaultBrowser = store.GetState("agent.browser-use");
            checks.Check(!defaultBrowser.IsInstalled && !defaultBrowser.IsEnabled, "首次查询 Browser Use 为未安装态");

            // 2. 核心组件不可卸载
            checks.Throws<InvalidOperationException>(() => store.Uninstall("core.csharp"), "尝试卸载核心内置组件抛出异常");

            // 3. 安装 Browser Use 插件（需有物理 manifest 载荷）
            var manifestDir = Path.Combine(layout.PluginsDirectory, "agent.browser-use");
            Directory.CreateDirectory(manifestDir);
            File.WriteAllText(Path.Combine(manifestDir, "manifest.json"), "{\"id\":\"agent.browser-use\",\"version\":\"1.0.0\"}");

            var changed = 0;
            store.Changed += () => changed++;
            store.Install("agent.browser-use");
            var installedBrowser = store.GetState("agent.browser-use");
            checks.Check(installedBrowser.IsInstalled && installedBrowser.IsEnabled, "安装后 Browser Use 转为已安装已启用");
            checks.Equal(1, changed, "安装触发 Changed 事件");

            // 4. 持久化落盘与跨实例恢复
            var jsonFile = Path.Combine(root, "plugins.json");
            checks.Check(File.Exists(jsonFile), "插件配置 plugins.json 已真实落盘");
            var reopened = new PluginStateStore(new AppDataLayout(root));
            checks.Check(reopened.GetState("agent.browser-use").IsInstalled, "新实例恢复已安装的 Browser Use 插件");

            // 5. 停用插件
            store.SetEnabled("agent.browser-use", false);
            checks.Check(store.GetState("agent.browser-use").IsInstalled && !store.GetState("agent.browser-use").IsEnabled,
                "停用后 Browser Use 为已安装但未启用");

            // 6. 卸载插件
            store.Uninstall("agent.browser-use");
            var uninstalledBrowser = store.GetState("agent.browser-use");
            checks.Check(!uninstalledBrowser.IsInstalled && !uninstalledBrowser.IsEnabled, "卸载后 Browser Use 恢复未安装未启用");

            // 7. 未安装插件不可设置启用
            checks.Throws<InvalidOperationException>(() => store.SetEnabled("agent.browser-use", true),
                "未安装插件尝试启用抛出异常");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
