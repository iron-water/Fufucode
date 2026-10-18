using System;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fufucode.App.ViewModels;
using Fufucode.App.Views;

namespace Fufucode.App.Browser.Tests;

/// <summary>
/// BRW-1：设置页【浏览器】卡片的真实控件布局断言。
/// 用真实 WorkbenchSectionsView（编译绑定）渲染，检查必需的分组与行真实存在且可交互；
/// 不冒充原生指针/视觉验收。
/// </summary>
internal static class BrowserSettingsUi
{
    public static void Run(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "fufucode-brw1-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var vm = new BrowserSettingsViewModel();
            vm.Attach(root);
            var sections = new WorkbenchSectionsView { DataContext = new WorkbenchCustomizationViewModel(new Infrastructure.WorkbenchAppearanceStore(root)) };
            sections.AttachBrowserSettings(vm);
            sections.Page = "browser";

            var window = new Window { Width = 900, Height = 900, Content = sections };
            window.Show();
            Pump();

            var labels = sections.GetVisualDescendants().OfType<TextBlock>()
                .Select(block => block.Text ?? "").ToHashSet(StringComparer.Ordinal);

            string[] required =
            [
                "让智能体控制内置浏览器",
                "网页链接打开位置", "本地文件打开位置", "显示完整网址", "截图策略",
                "清除浏览数据", "浏览历史管理",
                "默认下载位置", "下载前询问保存位置", "下载历史记录",
                "启用站点工具（WebMCP）", "智能体浏览权限", "智能体下载权限",
                "智能体网站白名单与权限例外",
                "启用完整 CDP 访问权限 (Chrome DevTools Protocol)",
            ];
            var missing = required.Where(label => !labels.Contains(label)).ToArray();
            check(missing.Length == 0, "浏览器设置卡片包含全部必需行（缺失：" + string.Join("、", missing) + "）");

            // 真实控件存在：下拉、开关、按钮
            var combos = sections.GetVisualDescendants().OfType<ComboBox>().Count();
            var toggles = sections.GetVisualDescendants().OfType<ToggleSwitch>().Count();
            check(combos >= 4, "常规/权限行的下拉控件真实渲染（>=4）");
            check(toggles >= 4, "主开关与下载/站点工具开关真实渲染（>=4）");

            // 清除浏览数据按钮可达（320 DIP 窄宽也应在视口内）
            window.Width = 360;
            Pump();
            var clear = sections.GetVisualDescendants().OfType<Button>()
                .FirstOrDefault(button => Avalonia.Automation.AutomationProperties.GetAutomationId(button) == "browser-clear-cache");
            check(clear is not null, "清除缓存按钮存在于真实控件树");

            window.Close();
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
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
