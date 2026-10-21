using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Fufucode.App.Features.SettingsExperience;
using Fufucode.App.Integration;
using Fufucode.App.ViewModels;
using Fufucode.Core;

namespace Fufucode.App.SelfTest;

/// <summary>
/// 后台常驻（托盘）与单实例的无头验收：
/// 设置默认值、真实落盘、开关即时生效、关窗隐藏/恢复/关闭语义、第二实例激活事件。
/// 无头平台没有真实托盘图标，窗口部分使用 SimulateTrayAvailableForTesting 接缝。
/// </summary>
internal static class BackgroundModeSelfTest
{
    internal static async Task RunAsync(Action<string> pass, Action<string> fail)
    {
        void Check(bool value, string name)
        {
            if (value) pass("Background: " + name);
            else fail("Background: " + name);
        }

        var root = Path.Combine(Path.GetTempPath(), "fufucode-background-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var previousRoot = Program.SelfTestDataRoot;
        MainWindow? window = null;
        try
        {
            Check(SettingsNavCatalog.Find("background") is { Tier: SettingsNavTier.Other },
                "后台与托盘进入设置目录（其他分类，有内容页）");
            Check(new BackgroundSettings().CloseToTray == false && new AppSettings().Background is null,
                "默认关闭托盘常驻，旧配置缺失字段时保持关窗即退出");

            var settingsRoot = Path.Combine(root, "settings");
            var store = new AppSettingsStore(new AppDataLayout(settingsRoot));
            var vm = new SettingsPageViewModel();
            vm.Attach(Context(store));
            vm.SelectCategory("background");
            Check(vm.ShowBackground && !vm.CloseToTray, "设置页可打开后台分类，初始开关为关");

            vm.CloseToTray = true;
            Check(store.Current.Background?.CloseToTray == true && vm.BackgroundSaveError is null,
                "开启托盘常驻写入设置存储");
            var reloaded = new AppSettingsStore(new AppDataLayout(settingsRoot));
            Check(reloaded.Current.Background?.CloseToTray == true, "重新加载 settings.json 后开关保持开启");

            vm.CloseToTray = false;
            Check(store.Current.Background?.CloseToTray == false, "关闭开关立即写回并恢复关窗即退出");

            // ── 单实例：命名对象存在即视为已有实例；激活事件能送达 ──
            var name = @"Local\Fufucode.SingleInstance.selftest-" + Guid.NewGuid().ToString("N");
            var first = SingleInstanceGuard.TryAcquire(name, out var guard);
            Check(first == SingleInstanceAcquireResult.Acquired && guard is not null, "首个实例取得单实例守卫");
            if (guard is { } instance)
            {
                var activated = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                instance.ActivationRequested += () => activated.TrySetResult(true);
                var second = SingleInstanceGuard.TryAcquire(name, out var secondGuard);
                Check(second == SingleInstanceAcquireResult.AlreadyRunning && secondGuard is null, "第二个实例报告已有实例");
                var signalled = SingleInstanceGuard.SignalExisting(name);
                var delivered = await Task.WhenAny(activated.Task, Task.Delay(2000)) == activated.Task;
                Check(signalled && delivered, "第二个实例的激活请求送达首个实例");
                instance.Dispose();
            }
            var afterRelease = SingleInstanceGuard.TryAcquire(name, out var reacquired);
            Check(afterRelease == SingleInstanceAcquireResult.Acquired, "首个实例释放后可以重新取得守卫");
            reacquired?.Dispose();

            // ── 窗口语义：开启时关窗隐藏，托盘恢复；关闭设置后关窗真实退出 ──
            Program.SelfTestDataRoot = Path.Combine(root, "window");
            window = new MainWindow { Width = 1100, Height = 760 };
            window.SimulateTrayAvailableForTesting = true;
            window.Show();
            await Ready();

            window.BackgroundStoreForTesting.Update(settings => settings with
            {
                Background = new BackgroundSettings(CloseToTray: true),
            });
            window.Close();
            Check(!window.IsVisible && window.HiddenToTrayForTesting, "开启后关闭窗口只隐藏，不退出");

            window.ShowFromTray();
            Check(window.IsVisible && !window.HiddenToTrayForTesting, "托盘动作恢复并激活主窗口");

            // ── 托盘菜单结构：固定/最近会话、新建会话、关于、主题色（含默认色）、设置、FUFU 占位与退出 ──
            var trayMenu = new Avalonia.Controls.NativeMenu();
            window.FillTrayMenuForTesting(trayMenu);
            var itemHeaders = trayMenu.Items.OfType<Avalonia.Controls.NativeMenuItem>().Select(i => i.Header).ToArray();
            Check(itemHeaders.Contains("显示主窗口") && itemHeaders.Contains("固定会话") &&
                  itemHeaders.Contains("最近会话") && itemHeaders.Contains("新建会话") &&
                  itemHeaders.Contains("关于 Fufucode") && itemHeaders.Contains("主题色") &&
                  itemHeaders.Contains("设置") && itemHeaders.Contains("召唤 FUFU（即将推出）") &&
                  itemHeaders.Contains("退出"), "托盘菜单完整包含全部预期条目与退出项");

            var fufuItem = trayMenu.Items.OfType<Avalonia.Controls.NativeMenuItem>().FirstOrDefault(i => i.Header == "召唤 FUFU（即将推出）");
            Check(fufuItem is { IsEnabled: false }, "召唤 FUFU 项为禁用占位态");

            var themeMenu = trayMenu.Items.OfType<Avalonia.Controls.NativeMenuItem>().FirstOrDefault(i => i.Header == "主题色")?.Menu;
            var themeHeaders = themeMenu?.Items.OfType<Avalonia.Controls.NativeMenuItem>().Select(i => i.Header).ToArray() ?? [];
            Check(themeHeaders.Contains("默认色（跟随系统）") && themeHeaders.Contains("Fufucode 蓝") &&
                  themeHeaders.Contains("青绿色") && themeHeaders.Contains("紫色"),
                  "主题色子菜单包含默认色与标准调色盘选项");

            window.BackgroundStoreForTesting.Update(settings => settings with
            {
                Background = new BackgroundSettings(CloseToTray: false),
            });
            window.Close();
            await Task.Delay(250);
            Check(!window.HiddenToTrayForTesting, "关闭设置后关窗走真实退出流程");
            window = null;
        }
        finally
        {
            if (window is not null)
            {
                window.SimulateTrayAvailableForTesting = false;
                window.Close();
                await Task.Delay(150);
            }
            Program.SelfTestDataRoot = previousRoot;
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static Task Ready() => Task.Delay(120);

    private static SettingsPageViewModel.Context Context(AppSettingsStore store) => new(
        Store: store,
        IsMock: true,
        CurrentModel: () => null,
        QueryModels: null,
        AgentDirectory: null,
        DataRoot: null,
        LogsDirectory: null,
        ApprovalText: () => null,
        ApprovalDiagnostic: () => null,
        ActiveExtensionPaths: () => Array.Empty<string>(),
        LastStartupFailure: () => null,
        ProjectPath: () => "(selftest)",
        OpenFolder: _ => Task.CompletedTask,
        CopyText: _ => Task.CompletedTask,
        PiVersion: () => "selftest",
        NodeVersion: () => "selftest");
}
