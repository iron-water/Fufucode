using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Fufucode.App.Features.Library;

namespace Fufucode.App.Library.Tests;

/// <summary>
/// 资料库页面无头冒烟：在真实无头 Window 中加载 LibraryPageView（真实编译绑定），
/// 验证列表/预览/标签/引用，以及工作台式“移出视觉树再重挂”的生命周期与最终释放。
/// 不做像素比对；原生指针/输入法/DPI/主题切换留给接线后的 V 项验收。
/// </summary>
internal static class UiSmoke
{
    public static void Run(LibraryService service, string sourceDirectory, Action<bool, string> check)
    {
        var builder = AppBuilder.Configure<SmokeApp>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true });
        builder.SetupWithoutStarting();

        using var viewModel = new LibraryViewModel(service);
        var view = new LibraryPageView();
        view.Attach(viewModel);
        var window = new Window { Width = 960, Height = 720, Content = view };
        window.Show();
        check(Pump(() => viewModel.Entries.Count > 0), "无头视图加载资料条目（真实编译绑定）");

        var list = view.GetLogicalDescendants().OfType<ListBox>().FirstOrDefault();
        check(list is not null, "资料条目列表控件存在");
        check(list!.ItemCount == viewModel.Entries.Count && list.ItemCount > 0, "列表项数量与视图模型一致");

        list.SelectedIndex = 0;
        check(Pump(() => viewModel.SelectedEntry is not null), "选中条目绑定生效");
        var selectedTitle = viewModel.SelectedEntry!.Title;
        check(view.GetLogicalDescendants().OfType<TextBlock>()
                .Any(block => block.Text is { Length: > 0 } text && text.Contains(selectedTitle, StringComparison.Ordinal)),
            "详情区渲染选中条目标题");
        var preview = viewModel.PreviewText;
        check(preview.Length > 0, "选中条目有可预览文本");
        var probe = preview[..Math.Min(12, preview.Length)];
        check(view.GetLogicalDescendants().OfType<TextBlock>()
                .Any(block => block.Text is { Length: > 0 } text && text.Contains(probe, StringComparison.Ordinal)),
            "预览区渲染真实文件内容");

        var tagButton = view.GetLogicalDescendants().OfType<Button>()
            .FirstOrDefault(button => button.Tag is string tag && tag == "工作");
        check(tagButton is not null, "标签筛选按钮渲染");
        tagButton!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check(Pump(() => viewModel.SelectedTag == "工作"), "点击标签按钮设置筛选");
        check(viewModel.Entries.All(entry => entry.Tags.Contains("工作")), "标签筛选后列表只保留匹配条目");

        viewModel.ClearTagFilterCommand.Execute(null);
        check(Pump(() => viewModel.SelectedTag is null), "清除标签筛选");
        list.SelectedIndex = 0;
        check(Pump(() => viewModel.SelectedEntry is not null), "清除筛选后可重新选中条目");

        var citationRaised = 0;
        viewModel.CitationCopyRequested += _ => citationRaised++;
        viewModel.CopyCitationCommand.Execute(null);
        check(citationRaised == 1 && viewModel.CitationText.Contains("版本"), "生成引用并请求复制（含版本可回溯）");

        // ---- 工作台式临时脱离：不能等同于所有者销毁 ----
        var beforeRemoval = viewModel.Entries.Count;
        window.Content = null;
        PumpTicks();
        check(!viewModel.IsDisposed, "临时移出视觉树不销毁视图模型");
        check(view.IsAttached && ReferenceEquals(view.ViewModel, viewModel), "临时移出后视图仍持有同一视图模型");
        check(viewModel.Entries.Count == beforeRemoval, "临时移出后条目仍在");

        var addedFile = Path.Combine(sourceDirectory, "重挂后新增资料.txt");
        File.WriteAllText(addedFile, "重挂之后新增的资料内容，关键词 移出重挂。\n", new UTF8Encoding(false));
        var added = Task.Run(() => service.ImportAsync(new LibraryImportRequest(
            LibraryEntryKind.File, LibraryStorageMode.Copy, addedFile, Tags: new[] { "重挂" }))).GetAwaiter().GetResult();
        check(added.IsImported, "移出期间新增资料成功");

        window.Content = view;
        PumpTicks();
        check(!viewModel.IsDisposed && view.IsAttached, "重新挂载后视图模型仍可用");

        viewModel.ReloadCommand.Execute(null);
        check(Pump(() => viewModel.Entries.Count == service.ListEntries().Count && viewModel.Entries.Count > beforeRemoval),
            "重挂后刷新看到新增条目");
        var reloadedList = view.GetLogicalDescendants().OfType<ListBox>().FirstOrDefault();
        check(reloadedList is not null && reloadedList.ItemCount == viewModel.Entries.Count,
            "重挂后列表控件反映新增条目");

        viewModel.SearchText = "移出重挂";
        viewModel.ReloadCommand.Execute(null);
        check(Pump(() => viewModel.Entries.Count == 1 && viewModel.Entries[0].Id == added.EntryId), "重挂后检索仍可用");
        viewModel.SearchText = string.Empty;
        viewModel.ReloadCommand.Execute(null);
        check(Pump(() => viewModel.Entries.Count == service.ListEntries().Count), "重挂后清空检索恢复全量");

        var refreshedList = view.GetLogicalDescendants().OfType<ListBox>().FirstOrDefault();
        refreshedList!.SelectedIndex = 0;
        check(Pump(() => viewModel.SelectedEntry is not null), "重挂后可选中条目");
        viewModel.CopyCitationCommand.Execute(null);
        check(citationRaised == 2 && viewModel.CitationText.Length > 0, "重挂后引用仍可用");

        // ---- 窄容器（默认右侧停靠栏宽度）下操作按钮必须仍可达 ----
        window.Width = 320;
        check(Pump(() => view.Bounds.Width > 0 && view.Bounds.Width <= 340), "窗口收窄到停靠栏宽度");
        check(Pump(() => ActionButtonsReachable(view)), "窄宽度下标签/引用/删除按钮仍可达（未被压成零宽）");
        window.Width = 960;
        check(Pump(() => view.Bounds.Width > 700), "窗口恢复宽布局");
        check(Pump(() => ActionButtonsReachable(view)), "恢复宽度后操作按钮仍可达");

        // ---- 重复挂载幂等 + 最终释放取消订阅 ----
        check(WriteClipboard(window, "哨兵-未发送"), "写入剪贴板哨兵");
        view.Attach(viewModel);
        view.Attach(viewModel);
        check(view.IsAttached, "重复 Attach 保持挂载");

        viewModel.CopyCitationCommand.Execute(null);
        var citationText = viewModel.CitationText;
        check(Pump(() => ReadClipboard(window) == citationText), "挂载状态下引用写入剪贴板（订阅生效且仅此一次路径）");

        view.Detach();
        check(!view.IsAttached && view.ViewModel is null, "最终 Detach 清除挂载状态");
        check(viewModel.IsDisposed, "最终 Detach 释放视图模型");

        check(WriteClipboard(window, "哨兵-释放后"), "释放后写入剪贴板哨兵");
        viewModel.CopyCitationCommand.Execute(null);
        PumpTicks();
        check(ReadClipboard(window) == "哨兵-释放后", "Detach 后没有残留订阅（重复 Attach 未重复订阅）");

        window.Close();
    }

    private static bool ActionButtonsReachable(LibraryPageView view)
    {
        string[] labels = ["添加标签", "同步来源变化", "生成引用", "删除条目"];
        var buttons = view.GetLogicalDescendants().OfType<Button>()
            .Where(button => button.Content is string content && labels.Contains(content, StringComparer.Ordinal))
            .ToList();
        if (buttons.Count != labels.Length) return false;
        return buttons.All(button =>
        {
            if (!button.IsVisible || button.Bounds.Width <= 1 || button.Bounds.Height <= 1)
            {
                Console.WriteLine($"[diag] {button.Content} 不可见或零尺寸 bounds={button.Bounds.Size} visible={button.IsVisible}");
                return false;
            }
            // 必须完整落在视图可视区内：只判宽度会漏掉“渲染但横向溢出被裁”的情况。
            var origin = button.TranslatePoint(new Point(0, 0), view);
            if (origin is null) return false;
            var reachable = origin.Value.X >= -1 && origin.Value.Y >= -1
                && origin.Value.X + button.Bounds.Width <= view.Bounds.Width + 1
                && origin.Value.Y + button.Bounds.Height <= view.Bounds.Height + 1;
            if (!reachable)
            {
                Console.WriteLine($"[diag] {button.Content} 溢出可视区 bounds={button.Bounds.Size} origin={origin} view={view.Bounds.Size}");
            }
            return reachable;
        });
    }

    private static bool Pump(Func<bool> condition, int timeoutMs = 5000)
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

    private static void PumpTicks(int rounds = 6)
    {
        for (var index = 0; index < rounds; index++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(15);
        }
    }

    private static bool WriteClipboard(Window window, string text)
    {
        var clipboard = window.Clipboard;
        if (clipboard is null) return false;
        var task = clipboard.SetTextAsync(text);
        return Pump(() => task.IsCompleted);
    }

    private static string? ReadClipboard(Window window)
    {
        var clipboard = window.Clipboard;
        if (clipboard is null) return null;
        var task = clipboard.TryGetTextAsync();
        return Pump(() => task.IsCompleted) ? task.Result : null;
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
