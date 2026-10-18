using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Fufucode.App.Features.Storage;

namespace Fufucode.App.Storage.Tests;

/// <summary>
/// 存储页无头冒烟：真实无头 Window 中加载 StoragePage，验证区域行、保留天数双向绑定、
/// 预览/结果渲染与「站点数据必须先确认」的门禁。不做像素比对；
/// 原生指针/DPI/主题切换留给接线后的 V 项验收。
/// </summary>
internal static class UiSmoke
{
    public static void Run(StorageManagerViewModel viewModel, string dataRoot, string? linkedCachePath, string? externalPath, Action<bool, string> check)
    {
        var builder = AppBuilder.Configure<SmokeApp>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true });
        builder.SetupWithoutStarting();

        var page = new StoragePage(viewModel);
        var window = new Window { Width = 1000, Height = 760, Content = page };
        window.Show();
        PumpTicks();

        check(FindButton(page, "重新计量") is not null, "页面渲染「重新计量」按钮");
        check(FindButton(page, "预览清理") is not null, "页面渲染「预览清理」按钮");
        check(FindButton(page, "执行清理") is not null, "页面渲染「执行清理」按钮");
        check(page.GetLogicalDescendants().OfType<Button>().Count(button => button.Content is string label && label == "打开") >= viewModel.Areas.Count,
            "每个存储区域都有「打开」按钮");
        check(Texts(page).Any(text => text.Contains("会话数据")), "区域行渲染区域标题");
        check(Texts(page).Any(text => text.Contains(dataRoot, StringComparison.OrdinalIgnoreCase)), "区域行渲染真实路径");

        var measureButton = FindButton(page, "重新计量")!;
        check(measureButton.IsEnabled, "空闲时可以计量");
        viewModel.IsMeasuring = true;
        PumpTicks();
        check(!measureButton.IsEnabled, "计量进行中禁用「重新计量」");
        viewModel.IsMeasuring = false;
        PumpTicks();
        check(measureButton.IsEnabled, "计量结束后恢复可用");

        var retention = page.GetLogicalDescendants().OfType<NumericUpDown>().FirstOrDefault();
        check(retention is not null, "页面提供日志保留天数控件");
        viewModel.LogRetentionDays = 30;
        PumpTicks();
        check(Convert.ToInt32(retention!.Value ?? 0m) == 30, "保留天数从视图模型同步到控件");
        retention.Value = 7m;
        PumpTicks();
        check(viewModel.LogRetentionDays == 7, "控件修改回写到视图模型");

        var previewButton = FindButton(page, "预览清理")!;
        previewButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check(Pump(() => viewModel.HasPreview || viewModel.Status.Contains("失败")), "点击「预览清理」生成预览");
        check(!viewModel.Status.Contains("失败"), "预览没有失败：" + viewModel.Status);
        PumpTicks();
        check(Texts(page).Any(text => text.Contains("预计释放")), "预览摘要渲染到页面");
        check(Texts(page).Any(text => text.Contains("可重建缓存")), "预览目标行渲染清理范围");
        if (linkedCachePath is not null)
        {
            check(viewModel.HasSkipped, "预览跳过清单进入视图模型");
            check(Pump(() => Texts(page).Any(text => text.Contains("跳过（未删除）"))), "页面渲染「跳过（未删除）」分组");
            check(Texts(page).Any(text => text.Contains("链接未跟随")), "页面显示跳过原因（链接未跟随）");
            check(Texts(page).Any(text => text.Contains(linkedCachePath, StringComparison.OrdinalIgnoreCase)), "页面显示被跳过的链接路径");
        }

        var cleanButton = FindButton(page, "执行清理")!;
        check(cleanButton.IsEnabled, "未选站点数据时预览后可直接执行清理");

        var siteDataOption = viewModel.Scopes.Single(option => option.Scope == StorageCleanupScope.BrowserSiteData);
        var siteDataBox = page.GetLogicalDescendants().OfType<CheckBox>()
            .FirstOrDefault(box => box.Tag is StorageCleanupScope scope && scope == StorageCleanupScope.BrowserSiteData);
        check(siteDataBox is not null, "页面渲染站点数据范围勾选框");
        siteDataBox!.IsChecked = true;
        PumpTicks();
        check(siteDataOption.IsEnabled, "勾选站点数据范围写回视图模型");
        check(!viewModel.HasPreview, "勾选站点数据范围后旧预览立即失效");
        previewButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check(Pump(() => viewModel.HasPreview || viewModel.Status.Contains("失败")), "重新预览包含站点数据范围");
        PumpTicks();
        check(!cleanButton.IsEnabled, "含站点数据时未确认不得执行清理");
        var confirm = page.GetLogicalDescendants().OfType<CheckBox>()
            .FirstOrDefault(box => box.Content is string text && text.Contains("我已了解"));
        check(confirm is not null, "页面提供站点数据确认行");
        confirm!.IsChecked = true;
        PumpTicks();
        check(cleanButton.IsEnabled, "确认后才允许执行清理");
        confirm.IsChecked = false;
        PumpTicks();
        check(!cleanButton.IsEnabled, "取消确认后再次禁止执行清理");

        siteDataBox.IsChecked = false;
        PumpTicks();
        previewButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check(Pump(() => viewModel.HasPreview || viewModel.Status.Contains("失败")), "回到默认范围后重新预览");
        PumpTicks();
        cleanButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check(Pump(() => viewModel.HasResult || viewModel.Status.Contains("失败")), "点击「执行清理」记录结果");
        check(viewModel.Status.Contains("清理完成"), "清理完成状态：" + viewModel.Status);
        PumpTicks();
        check(Texts(page).Any(text => text.Contains("释放")), "清理结果显示在页面");
        check(Texts(page).Any(text => text.Contains("清理结果")), "结果分组标题渲染");
        if (externalPath is not null)
        {
            check(File.Exists(Path.Combine(externalPath, "outside.bin")), "页面执行清理后链接目标文件仍在（未删除链接目标）");
            check(Texts(page).Any(text => text.Contains("跳过（未删除）")), "结果区继续显示未删除的跳过条目");
        }

        window.Close();
    }

    private static Button? FindButton(Control root, string content) =>
        root.GetLogicalDescendants().OfType<Button>().FirstOrDefault(button => (string?)button.Content == content);

    private static IEnumerable<string> Texts(Control root) =>
        root.GetLogicalDescendants().OfType<TextBlock>().Select(block => block.Text ?? string.Empty);

    private static bool Pump(Func<bool> condition, int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition()) return true;
            Thread.Sleep(15);
        }
        Dispatcher.UIThread.RunJobs();
        return condition();
    }

    private static void PumpTicks(int rounds = 8)
    {
        for (var index = 0; index < rounds; index++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
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
