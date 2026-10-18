using System;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fufucode.App.Views;

namespace Fufucode.App.Browser.Tests;

/// <summary>
/// 浏览器页签在真实无头 Window 下的可达性：320 DIP 窄宽、浅/深主题、查找条、缩放与更多操作入口。
/// 只做真实控件布局断言，不冒充原生指针/IME/DPI 验收。
/// </summary>
internal static class UiSmoke
{
    public static void Run(Action<bool, string> check)
    {
        var view = new BrowserView();
        var window = new Window { Width = 320, Height = 720, Content = view };
        window.Show();
        Pump();

        // 窄宽 + 浅主题
        window.RequestedThemeVariant = ThemeVariant.Light;
        Pump();
        check(Reachable(view), "320 DIP 浅主题下浏览器入口可达");
        check(view.Bounds.Width > 0 && view.Bounds.Width <= 340, "浏览器在窄容器下实际收窄到 320 DIP 附近");

        // 深主题
        window.RequestedThemeVariant = ThemeVariant.Dark;
        Pump();
        check(Reachable(view), "320 DIP 深主题下浏览器入口可达");

        // 查找条展开后仍可达
        view.ViewModel.ToggleFindCommand.Execute(null);
        Pump();
        check(view.ViewModel.IsFindOpen, "查找条可以展开");
        check(Reachable(view), "查找条展开后入口仍可达");
        view.ViewModel.CloseFindCommand.Execute(null);
        Pump();
        check(!view.ViewModel.IsFindOpen, "查找条可以关闭");

        // 页签栏：新建后仍有可点击的新建入口
        view.ViewModel.NewTabCommand.Execute(null);
        Pump();
        check(view.ViewModel.Tabs.Count == 2, "窄宽下仍可新建页签");
        check(view.GetVisualDescendants().OfType<Button>().Any(button =>
                Avalonia.Automation.AutomationProperties.GetName(button) == "浏览器新建页签" && IsReachable(button, view)),
            "窄宽下新建页签按钮可达");

        // 宽布局
        window.Width = 900;
        Pump();
        check(view.Bounds.Width > 700, "恢复宽布局");
        check(Reachable(view), "宽布局下浏览器入口可达");

        // BRW-1：不再有文字状态栏/二重分划线；一体化胶囊地址栏真实存在。
        var captionTexts = view.GetVisualDescendants().OfType<TextBlock>()
            .Select(block => block.Text ?? "").ToArray();
        check(!captionTexts.Any(text => text.Contains("自由导航", StringComparison.Ordinal) ||
                                       text.Contains("加载完成", StringComparison.Ordinal)),
            "已彻底移除“自由导航 / 加载完成”等长文堆砌说明");
        check(captionTexts.All(text => !text.Contains("个页签", StringComparison.Ordinal)),
            "底部文字状态栏（页签计数）不再渲染");

        // 安全态默认不显示（未导航 / 文件预览）
        check(!view.ViewModel.ShowSecurity, "未导航时不渲染锁图标，不做假安全态");

        // 释放后不再保留宿主
        var hostCount = view.HostCountForTesting;
        check(hostCount == 2, "两个页签各有一个宿主实例");
        view.Dispose();
        check(view.HostCountForTesting == 0, "Dispose 释放全部页签宿主");

        window.Close();
    }

    private static bool Reachable(BrowserView view)
    {
        string[] names =
        [
            "浏览器后退", "浏览器前进", "浏览器刷新", "浏览器地址栏",
            "浏览器收藏当前页面", "浏览器分享", "浏览器打开文件", "浏览器更多操作",
            "浏览器新建页签",
        ];
        var controls = view.GetVisualDescendants().OfType<Control>()
            .Where(control => names.Contains(Avalonia.Automation.AutomationProperties.GetName(control), StringComparer.Ordinal))
            .ToList();
        if (controls.Count < names.Length) return false;
        return controls.All(control => IsReachable(control, view));
    }

    private static bool IsReachable(Control control, BrowserView view)
    {
        if (!control.IsVisible || control.Bounds.Width <= 1 || control.Bounds.Height <= 1) return false;
        var origin = control.TranslatePoint(new Point(0, 0), view);
        if (origin is null) return false;
        return origin.Value.X >= -1 && origin.Value.Y >= -1
            && origin.Value.X + control.Bounds.Width <= view.Bounds.Width + 1
            && origin.Value.Y + control.Bounds.Height <= view.Bounds.Height + 1;
    }

    private static void Pump(int rounds = 8)
    {
        for (var index = 0; index < rounds; index++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(15);
        }
    }
}
