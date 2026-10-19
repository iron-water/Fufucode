using System;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Fufucode.App.Features.BrowserExperience;
using Fufucode.App.Infrastructure;
using Fufucode.App.Infrastructure.Browser;
using Fufucode.App.ViewModels;
using Fufucode.App.Views;

namespace Fufucode.App.Browser.Tests;

// E2-B 浏览器页签/生命周期定向测试（独立入口，未注册到共享 SelfTestRunner）：
//   dotnet run --project tests/Fufucode.App.Browser.Tests
// 覆盖多页签独立状态、关闭/重开、v1/v2 状态恢复与未知版本降级、查找/缩放有界、
// 动作目录禁用原因与执行入口。真实 WebView2/HWND 由 App 的 --native-module-selftest 覆盖。
internal static class Program
{
    private static int _passed;

    private static int Main()
    {
        AppBuilder.Configure<SmokeApp>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true })
            .SetupWithoutStarting();

        try
        {
            var view = new BrowserView();
            var vm = view.ViewModel;
            var tabA = vm.ActiveTab!;

            Check(vm.Tabs.Count == 1 && vm.ActiveTab is not null, "初始只有一个活动页签");

            // ---- 多页签独立状态 ----
            vm.AddressText = "example.com";
            vm.NewTabCommand.Execute(null);
            var tabB = vm.ActiveTab!;
            Check(!ReferenceEquals(tabA, tabB) && vm.Tabs.Count == 2, "新建页签得到独立身份");
            Check(tabA.AddressText == "example.com" && tabB.AddressText == "", "新页签不继承另一个页签的地址草稿");
            vm.AddressText = "https://example.org/b";
            tabA.ApplyNavigationState(true, false, false, "https://example.com/");
            tabB.ApplyNavigationState(false, true, false, "https://example.org/b");
            Check(tabA.CanGoBack && !tabA.CanGoForward && !tabB.CanGoBack && tabB.CanGoForward,
                "两个页签的历史状态互相独立");
            Check(tabA.NavigationAddress == "https://example.com/" && tabB.NavigationAddress == "https://example.org/b",
                "两个页签的真实导航地址不串页");
            vm.SelectTab(tabA);
            // 页签 A 的地址栏草稿是用户改过且未提交的 "example.com"，不应被后台导航回写成 source。
            Check(vm.AddressText == "example.com" && vm.CanGoBack && !vm.CanGoForward,
                "切回页签 A 时门面属性反映 A 的状态（草稿未被导航回写覆盖）");
            vm.SelectTab(tabB);
            Check(vm.AddressText == "https://example.org/b" && !vm.CanGoBack && vm.CanGoForward,
                "切到页签 B 时门面属性反映 B 的状态");

            // ---- 关闭页签 ----
            vm.CloseTabByIdCommand.Execute(tabB);
            Check(vm.Tabs.Count == 1 && ReferenceEquals(vm.ActiveTab, tabA), "关闭页签 B 后只剩 A 且 A 状态保留");
            Check(tabA.AddressText == "example.com" || tabA.NavigationAddress == "https://example.com/",
                "关闭其他页签不改写保留页签的地址");
            var fresh = vm.ActiveTab!;
            vm.CloseTabByIdCommand.Execute(fresh);
            Check(vm.Tabs.Count == 1 && !ReferenceEquals(vm.Tabs[0], fresh) && vm.Tabs[0].NavigationAddress is null,
                "关闭最后一个页签留下一个清晰空页");
            Check(!vm.CanCloseTab, "单页签时关闭动作不可用");
            Check(BrowserSurfaceActions.GetDisabledReason(view, BrowserSurfaceActions.CloseTab) is not null,
                "动作接缝报告关闭动作的禁用原因");

            // ---- v2 捕获/恢复往返 ----
            var tab1 = vm.ActiveTab!;
            tab1.ApplyNavigationState(false, false, false, "https://one.example/");
            tab1.AddressText = "draft-one";   // 用户在导航完成后输入、未提交的草稿
            vm.NewTabCommand.Execute(null);
            var tab2 = vm.ActiveTab!;
            tab2.ApplyNavigationState(false, false, false, "https://two.example/");
            tab2.AddressText = "draft-two";
            var captured = view.CaptureState();
            Check(captured.Version == 2 && captured.Tabs is { Count: 2 }, "捕获状态为 v2 且包含两个页签");

            var view2 = new BrowserView();
            view2.RestoreState(captured);
            Check(view2.ViewModel.Tabs.Count == 2, "恢复 v2 状态重建两个页签");
            Check(view2.ViewModel.Tabs[0].NavigationAddress == "https://one.example/" &&
                  view2.ViewModel.Tabs[1].NavigationAddress == "https://two.example/",
                "恢复后两个页签各自保留真实导航地址");
            Check(view2.ViewModel.Tabs[0].AddressText == "draft-one" && view2.ViewModel.Tabs[1].AddressText == "draft-two",
                "恢复后地址草稿保真");
            Check(ReferenceEquals(view2.ViewModel.ActiveTab, view2.ViewModel.Tabs[1]), "恢复后活动页签为捕获时的页签");

            // ---- v1 旧状态继续可读 ----
            var view3 = new BrowserView();
            view3.RestoreState(new BrowserPageState(Version: 1, AddressDraft: "legacy-draft",
                NavigationAddress: "https://legacy.example/", FilePath: null));
            Check(view3.ViewModel.Tabs.Count == 1 &&
                  view3.ViewModel.ActiveTab.NavigationAddress == "https://legacy.example/",
                "v1 单页状态仍可恢复（不丢旧配置）");
            Check(view3.ViewModel.AddressText == "legacy-draft", "v1 恢复保留地址草稿");

            // ---- 未知版本诚实降级 ----
            var view4 = new BrowserView();
            view4.RestoreState(new BrowserPageState(Version: 99));
            Check(view4.ViewModel.StatusText.Contains("版本不受支持") &&
                  view4.ViewModel.Tabs.Count == 1 &&
                  view4.ViewModel.AddressText == "about:blank",
                "未支持的版本降级为 about:blank 空页并如实说明");

            // ---- 查找与有界缩放 ----
            var view5 = new BrowserView();
            view5.ViewModel.FindQuery = "term-a";
            Check(view5.ViewModel.FindQuery == "term-a", "查找词是页签局部状态");
            view5.ViewModel.ApplyZoom(10_000);
            Check(view5.ViewModel.ZoomPercent == BrowserViewModel.MaxZoomPercent, "缩放上限被限制在 200%");
            view5.ViewModel.ApplyZoom(0);
            Check(view5.ViewModel.ZoomPercent == BrowserViewModel.MinZoomPercent, "缩放下限被限制在 50%");
            view5.ViewModel.ApplyZoom(130);
            Check(view5.ViewModel.ZoomPercent == 130, "缩放取有界值 130%");

            // ---- 动作目录与禁用原因 ----
            Check(BrowserSurfaceActions.Catalog.Count >= 13, "动作目录包含浏览器基础动作");
            Check(BrowserSurfaceActions.Catalog.All(action => action.Scope == BrowserSurfaceActions.ScopeName),
                "所有浏览器动作使用同一作用域名");
            Check(BrowserSurfaceActions.Catalog.Select(action => action.Id).Distinct().Count() == BrowserSurfaceActions.Catalog.Count,
                "动作 ID 无重复");
            var idle = new BrowserView();
            Check(BrowserSurfaceActions.GetDisabledReason(idle, BrowserSurfaceActions.Back) is not null &&
                  BrowserSurfaceActions.GetDisabledReason(idle, BrowserSurfaceActions.Forward) is not null &&
                  BrowserSurfaceActions.GetDisabledReason(idle, BrowserSurfaceActions.Stop) is not null,
                "无历史/未加载时后退前进停止均报告禁用原因");
            Check(BrowserSurfaceActions.GetDisabledReason(idle, BrowserSurfaceActions.CopyAddress) is not null &&
                  BrowserSurfaceActions.GetDisabledReason(idle, BrowserSurfaceActions.OpenExternal) is not null,
                "无真实地址时复制/外部打开报告禁用原因");
            Check(!BrowserSurfaceActions.TryExecute(idle, BrowserSurfaceActions.Back), "禁用动作不会被错误执行");
            Check(BrowserSurfaceActions.TryExecute(idle, BrowserSurfaceActions.NewTab) && idle.ViewModel.Tabs.Count == 2,
                "新建页签动作走真实实现");

            // ---- 恢复相邻边界：生成的恢复 ID 不得抢占后续合法原 ID ----
            var collision = new BrowserView();
            collision.RestoreState(new BrowserPageState(Version: 2, ActiveTabId: "tab-restored-1",
                Tabs: new[]
                {
                    new BrowserTabState("", "blank-draft", null, null, ""),
                    new BrowserTabState("tab-restored-1", "selected-real-draft", null, null, "tab-restored-1"),
                }));
            Check(collision.ViewModel.Tabs.Count == 2, "空 ID 与后续合法原 ID 条目都保留");
            Check(collision.ViewModel.ActiveTab.Id == "tab-restored-1" &&
                  collision.ViewModel.ActiveTab.AddressText == "selected-real-draft",
                "生成的恢复 ID 不抢占后续合法原 ID，选中页保持对应原条目");

            // ---- 相邻边界：未就绪 Stop 立即收敛加载，新导航撤销旧取消 ----
            var stopView = new BrowserView();
            var stopTab = stopView.ViewModel.ActiveTab;
            stopTab.AddressText = "https://pending.example/";
            stopTab.NavigateCommand.Execute(null);
            Check(stopTab.IsLoading, "导航后页签进入加载态");
            var stopHost = stopView.HostForTesting;
            stopHost.Stop();
            Check(!stopTab.IsLoading, "未就绪 Stop 立即收敛加载状态");
            Check(stopHost.HasPendingStopForTesting, "未就绪 Stop 记录待发取消");
            stopTab.AddressText = "https://retry.example/";
            stopTab.NavigateCommand.Execute(null);
            Check(!stopHost.HasPendingStopForTesting, "Stop 之后的新导航撤销旧待发取消");

            // ---- Browser Use & CDP 契约与设置 ----
            Check(BrowserUseActionNames.Navigate == "browser_navigate" &&
                  BrowserUseActionNames.Click == "browser_click" &&
                  BrowserUseActionNames.CallCdp == "browser_cdp",
                "Browser Use 标准动作契约已定义");
            var settingsVm = new SettingsPageViewModel();
            Check(settingsVm.Categories.Any(c => c.Id == "browser" && c.Title == "浏览器" && c.Icon == "Globe"),
                "系统设置中已注册专属【浏览器】分类");
            var appearance = new WorkbenchAppearance();
            Check(appearance.BrowserUseEnabled && appearance.WebUrlOpenTarget == "builtin" && appearance.EnableFullCdpAccess,
                "浏览器偏好具备标准默认值并支持持久化");

            // ---- BRW-1：浏览器设置存储、安全态、白名单与 Browser Use 桥门禁 ----
            BrowserSettingsTests.Run(Check);
            BrowserSettingsUi.Run(Check);
            BrowserUsabilityTests.Run(Check);

            // ---- 320 DIP / 浅深主题入口可达（真实控件布局） ----
            UiSmoke.Run(Check);

            Console.WriteLine($"\n全部通过：{_passed} 项断言");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL " + name);
        _passed++;
        Console.WriteLine("PASS " + name);
    }

    private sealed class SmokeApp : Application
    {
        public override void Initialize()
        {
            Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
            Styles.Add(new StyleInclude(new Uri("avares://Fufucode.App/"))
            {
                Source = new Uri("avares://Fufucode.App/Styles/FufucodeTheme.axaml"),
            });
            Styles.Add(new StyleInclude(new Uri("avares://Fufucode.App/"))
            {
                Source = new Uri("avares://Fufucode.App/Styles/WorkbenchStyles.axaml"),
            });
        }
    }
}
