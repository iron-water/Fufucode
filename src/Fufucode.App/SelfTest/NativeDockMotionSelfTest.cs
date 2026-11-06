using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fufucode.App.Infrastructure;
using Fufucode.App.ViewModels;
using Fufucode.App.Views;

namespace Fufucode.App.SelfTest;

/// <summary>保留真实计时器的原生回归：动画中途刷新、快速反向、宽窄切换和设置返回。</summary>
internal static class NativeDockMotionSelfTest
{
    internal static async Task<int> RunAsync(MainWindow window)
    {
        var evidence = Path.GetFullPath("tmp/dock-motion-evidence");
        Directory.CreateDirectory(evidence);
        var failures = new List<string>();
        var samples = new List<object>();
        void Check(bool condition, string message)
        {
            Console.WriteLine((condition ? "PASS " : "FAIL ") + message);
            if (!condition) failures.Add(message);
        }
        await Task.Delay(1000);
        window.WindowState = WindowState.Normal;
        window.Width = 1440;
        window.Height = 900;
        window.CustomizationForTesting.Change(current => current with { Theme = "dark", Skin = "fluent", Accent = "#26FF8E" });
        window.ResetDockLayout();
        window.OpenDockPage("notifications", DockZone.Bottom);
        await Settle();
        Check(!Motion.Reduced, "native regression runs with real animation timers");
        if (Motion.Reduced) return 1;
        var vm = (MainViewModel)window.DataContext!;
        var shell = window.FindControl<Grid>("ShellGrid")!;
        var workspace = window.FindControl<Grid>("WorkspaceShell")!;
        var host = window.FindControl<TransitioningContentControl>("PageHost")!;
        Check(host.PageTransition is null, "page host has a single explicit transition");

        foreach (var zone in Enum.GetValues<DockZone>())
        {
            var panel = Panel(zone);
            var before = Extent(zone);
            await Toggle(zone);
            Check(window.DockLayoutForTesting.Hidden[zone] && panel.IsVisible,
                zone + " closing changes intent while keeping the animated surface alive");
            await Task.Delay(40);
            var mid = Extent(zone);
            var selected = window.DockLayoutForTesting.Selected[zone]!;
            // 刷新另一栏会同步 VM 和重算布局，不能把当前动画抢写成终态。
            window.OpenDockPage(zone == DockZone.Right ? "files" : "changes",
                zone == DockZone.Right ? DockZone.Left : DockZone.Right);
            Check(Math.Abs(Extent(zone) - mid) < .1 && mid < before && mid > 0,
                zone + " repeated layout preserves the intermediate extent");
            await Toggle(zone);
            Check(!window.DockLayoutForTesting.Hidden[zone], zone + " second click reverses the closing intent");
            await Sample(zone, true, "reverse");
            Check(panel.IsVisible && Math.Abs(Extent(zone) - before) < 1,
                zone + " reversed opening settles at its original extent");
            for (var cycle = 0; cycle < 4; cycle++)
            {
                await Toggle(zone);
                await Task.Delay(25);
                await Toggle(zone);
                await Task.Delay(25);
                await Toggle(zone);
                await Sample(zone, false, "rapid-close-" + cycle);
                Check(!panel.IsVisible && Extent(zone) < .1 && Gap(zone) < .1,
                    zone + " rapid odd clicks finish fully closed without a trailing gap");
                await Toggle(zone);
                await Sample(zone, true, "rapid-open-" + cycle);
                Check(panel.IsVisible && !window.DockLayoutForTesting.Hidden[zone], zone + " reopens after rapid clicks");
            }
            window.OpenDockPage(selected, zone);
        }
        Save("wide-docks.png");

        // 关闭最后一个页签后再开右栏，只显示空栏和添加入口，不能复活变更页。
        foreach (var id in window.DockLayoutForTesting.Pages[DockZone.Right].ToArray()) window.CloseDockPageForTesting(id);
        await Settle();
        Check(window.DockLayoutForTesting.Hidden[DockZone.Right], "closing the last right tab hides the region");
        await Toggle(DockZone.Right);
        await Settle();
        Check(Panel(DockZone.Right).IsVisible && window.DockLayoutForTesting.Pages[DockZone.Right].Count == 0 &&
            !window.GetVisualDescendants().OfType<Button>().Any(button => AutomationProperties.GetAutomationId(button) == "dock-tab-changes"),
            "reopening the empty right region does not resurrect Changes");
        var restored = WorkbenchLayoutState.Restore(window.DockLayoutForTesting.Serialize());
        Check(!restored.Hidden[DockZone.Right] && restored.Pages[DockZone.Right].Count == 0,
            "empty open right region survives layout serialization");
        Save("empty-right-region.png");
        window.CustomizationForTesting.SetZoneVisible(DockZone.Right, false);
        window.CustomizationForTesting.SetZoneVisible(DockZone.Right, true);
        await Settle();
        Check(Panel(DockZone.Right).IsVisible && window.DockLayoutForTesting.Pages[DockZone.Right].Count == 0,
            "settings visibility toggle preserves the empty right region");
        vm.SetRightPanelOpen(false); await Settle();
        vm.SetRightPanelOpen(true); await Settle();
        Check(Panel(DockZone.Right).IsVisible && window.DockLayoutForTesting.Pages[DockZone.Right].Count == 0,
            "view model visibility toggle cannot repopulate Changes");

        window.Width = 1024;
        await Settle();
        foreach (var zone in new[] { DockZone.Left, DockZone.Right })
        {
            await Toggle(zone);
            await Settle();
            Check(Panel(zone).IsVisible, zone + " narrow drawer opens");
            await Toggle(zone);
            await Task.Delay(30);
            await Toggle(zone);
            window.OpenDockPage(window.DockLayoutForTesting.Selected[DockZone.Bottom]!, DockZone.Bottom);
            await Settle();
            Check(Panel(zone).IsVisible && Math.Abs(Panel(zone).Opacity - 1) < .001,
                zone + " narrow drawer reversal survives layout refresh and old hide callbacks");
            await window.NavigateForTestingAsync(AppPage.Settings);
            await Settle();
            await Toggle(zone);
            await Settle();
            Check(vm.IsChatPage && Panel(zone).IsVisible,
                zone + " action from settings opens the requested narrow drawer");
        }
        Save("narrow-drawer.png");
        window.Width = 1440;
        await Settle();
        for (var cycle = 0; cycle < 4; cycle++)
        {
            await Toggle(DockZone.Right);
            await Task.Delay(25);
            await window.NavigateForTestingAsync(AppPage.Settings);
            Check(Extent(DockZone.Left) < .1 && Extent(DockZone.Right) < .1 && Extent(DockZone.Bottom) < .1,
                "settings entry cancels all running dock extents");
            await Task.Delay(35);
            await window.NavigateForTestingAsync(AppPage.Chat);
            var left = Extent(DockZone.Left);
            var right = Extent(DockZone.Right);
            var bottom = Extent(DockZone.Bottom);
            for (var frame = 0; frame < 16; frame++)
            {
                await Task.Delay(20);
                window.UpdateLayout();
                Check(Math.Abs(Extent(DockZone.Left) - left) < .1 && Math.Abs(Extent(DockZone.Right) - right) < .1 &&
                    Math.Abs(Extent(DockZone.Bottom) - bottom) < .1,
                    "settings return " + cycle + " frame " + frame + " keeps restored layout stable");
            }
            Check(vm.IsChatPage && Math.Abs(host.Opacity - 1) < .001, "settings return completes its fade");
        }
        Save("settings-return.png");
        // 窗口缩放必须中断旧尺寸动画，之后旧计时器不能再改回去。
        await Toggle(DockZone.Left);
        await Task.Delay(30);
        window.Width = 1250;
        await Settle();
        var finalLeft = Extent(DockZone.Left);
        await Task.Delay(250);
        Check(Math.Abs(Extent(DockZone.Left) - finalLeft) < .1, "resizing cancels the obsolete extent writer");
        foreach (var zone in Enum.GetValues<DockZone>())
        {
            foreach (var id in window.DockLayoutForTesting.Pages[zone].ToArray()) window.CloseDockPageForTesting(id);
            await Settle();
            // 前面已验证过的空右栏可能本就开着；先确保确实从关闭态测试“重新打开”。
            if (!window.DockLayoutForTesting.Hidden[zone])
            {
                await Toggle(zone);
                await Settle();
            }
            Check(window.DockLayoutForTesting.Hidden[zone], zone + " empty-region reopen fixture starts closed");
            await Toggle(zone);
            await Settle();
            Check(window.DockLayoutForTesting.Pages[zone].Count == 0 && Panel(zone).IsVisible,
                zone + " reopening after closing all tabs does not restore defaults");
            var persisted = WorkbenchLayoutState.Restore(window.DockLayoutForTesting.Serialize());
            Check(persisted.Pages[zone].Count == 0 && !persisted.Hidden[zone],
                zone + " persists its empty visible state symmetrically");
        }
        File.WriteAllText(Path.Combine(evidence, "motion-samples.json"), JsonSerializer.Serialize(samples, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(evidence, "result.json"), JsonSerializer.Serialize(new { failures, sampleCount = samples.Count }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Native dock motion failures: {failures.Count}; samples: {samples.Count}; evidence: {evidence}");
        return failures.Count == 0 ? 0 : 1;

        Control Panel(DockZone zone) => window.FindControl<Border>(zone switch
        { DockZone.Left => "SidebarBorder", DockZone.Right => "RightPanelBorder", _ => "TerminalBorder" })!;
        double Extent(DockZone zone) => zone switch
        {
            DockZone.Left => shell.ColumnDefinitions[0].Width.Value,
            DockZone.Right => workspace.ColumnDefinitions[2].Width.Value,
            _ => workspace.RowDefinitions[2].Height.Value,
        };
        double Gap(DockZone zone) => zone switch
        {
            DockZone.Left => shell.ColumnDefinitions[1].Width.Value,
            DockZone.Right => workspace.ColumnDefinitions[1].Width.Value,
            _ => workspace.RowDefinitions[1].Height.Value,
        };
        Task Toggle(DockZone zone) => window.ActionsForTesting.ExecuteAsync("toggle." + zone.ToString().ToLowerInvariant());
        async Task Settle() { await Task.Delay(350); await Dispatcher.Yield(DispatcherPriority.Background); window.UpdateLayout(); }
        async Task Sample(DockZone zone, bool opening, string phase)
        {
            var previous = Extent(zone);
            var lastGap = Gap(zone);
            var lockedWidth = host.Width;
            for (var frame = 0; frame < 15; frame++)
            {
                await Task.Delay(20);
                window.UpdateLayout();
                var value = Extent(zone);
                var gap = Gap(zone);
                samples.Add(new { zone, phase, frame, value, gap, visible = Panel(zone).IsVisible, contentWidth = host.Bounds.Width });
                if (double.IsFinite(lockedWidth) && double.IsFinite(host.Width))
                    Check(Math.Abs(host.Bounds.Width - lockedWidth) < 1,
                        "central content keeps one measured width during dock motion");
                Check(opening ? value >= previous - .1 && gap >= lastGap - .1 : value <= previous + .1 && gap <= lastGap + .1,
                    zone + " " + phase + " frame " + frame + " progresses without a backward jump");
                previous = value; lastGap = gap;
            }
        }
        void Save(string name)
        {
            using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height));
            bitmap.Render(window);
            bitmap.Save(Path.Combine(evidence, name), PngBitmapEncoderOptions.Default);
        }
    }
}
