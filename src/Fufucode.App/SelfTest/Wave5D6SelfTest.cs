using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Fufucode.App.Infrastructure;
using Point = Avalonia.Point;
using Fufucode.App.ViewModels;
using Fufucode.App.Views;

namespace Fufucode.App.SelfTest;

/// <summary>
/// D6 无头断言：①D-R4-1 窄宽抽屉不覆盖会话头动作区与 composer；②Files 选中/双击接通内置浏览器预览。
/// 像素渲染与真实 WebView2 导航不在无头能力内——那部分列「用户实测清单」，不在这里放宽断言冒充通过。
/// </summary>
public static class Wave5D6SelfTest
{
    public static async Task RunAsync(Action<string> pass, Action<string> fail)
    {
        void Check(bool ok, string label) { if (ok) pass("W5-D6：" + label); else fail("W5-D6：" + label); }
        var directory = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "tmp", "w5d6"));
        Directory.CreateDirectory(directory);
        await LayoutAsync(Check, directory);
        await PreviewWiringAsync(Check);
    }

    // ── D-R4-1：三态布局（正常宽并排 / 窄宽抽屉 / 抽屉+底栏展开） ──
    private static async Task LayoutAsync(Action<bool, string> check, string directory)
    {
        var previousRoot = Program.SelfTestDataRoot;
        Program.SelfTestDataRoot = Path.Combine(Path.GetTempPath(), "fufucode-d6-layout-" + Guid.NewGuid().ToString("N"));
        var window = new MainWindow();
        window.Show();
        try
        {
            window.FindControl<TransitioningContentControl>("PageHost")!.PageTransition = null;
            var vm = (MainViewModel)window.DataContext!;
            await window.ActionsForTesting.ExecuteAsync("toggle.bottom");
            vm.SetRightPanelOpen(true);
            await vm.WorkspacePanel.RefreshAsync();
            vm.WorkspacePanel.SelectedTab = vm.WorkspacePanel.Tabs.Single(t => t.Kind == WorkspacePanelKind.Files);
            await Task.Delay(150);
            window.UpdateLayout();

            // ① 正常宽：并排，覆盖关系不存在 → 避让边距必须为 0。
            window.Width = 1440;
            window.Height = 900;
            await Task.Delay(200);
            window.UpdateLayout();
            var wide = window.FindControl<Border>("RightPanelBorder")!;
            check(!window.IsRightDrawerForTesting && wide.Margin == default(Avalonia.Thickness) &&
                  wide.Bounds.Width == window.DockLayoutForTesting.RightWidth, "1440 并排：无避让边距且遵循右栏尺寸偏好");
            Save(window, directory, "01-wide-1440.png");

            // ② 窄宽 1024：切抽屉，抽屉必须落在会话头之下、composer 之上。
            window.Width = 1024;
            await Task.Delay(120); window.UpdateLayout();
            await window.ActionsForTesting.ExecuteAsync("toggle.right");
            await Task.Delay(220);
            window.UpdateLayout();
            var chat = (ChatPage)window.FindControl<TransitioningContentControl>("PageHost")!.Content!;
            var headerBottom = chat.Bounds.Top + chat.HeaderBandHeight;
            var composerTop = chat.Bounds.Top + chat.Bounds.Height - chat.BottomReserveHeight;
            check(chat.HeaderBandHeight == 0 && !chat.FindControl<Border>("ConversationHeader")!.IsVisible && chat.BottomReserveHeight > 0, "1024 默认隐藏会话头，输入区避让带仍有效");
            var drawer = window.FindControl<Border>("RightPanelBorder")!;
            check(window.IsRightDrawerForTesting && drawer.IsVisible && drawer.Bounds.Width == window.DockLayoutForTesting.RightWidth,
                $"1024 显式打开抽屉且遵循右栏尺寸偏好 actual={drawer.Bounds.Width} preferred={window.DockLayoutForTesting.RightWidth} visible={drawer.IsVisible}");
            check(drawer.Bounds.Top >= headerBottom - 0.5, "抽屉顶不压会话头动作区");
            check(drawer.Bounds.Bottom <= composerTop + 0.5, $"抽屉底不压 composer bottom={drawer.Bounds.Bottom} composer={composerTop} chat={chat.Bounds}");
            check(drawer.Bounds.Height >= 200, "抽屉在避让带内仍保留 ≥200DIP 可用高度");
            var scrim = window.FindControl<Button>("RightDrawerScrim")!;
            check(scrim.Bounds.Top >= headerBottom - 0.5 && scrim.Bounds.Bottom <= composerTop + 0.5,
                "遮罩同步避让，页头与输入区保持可点");
            Save(window, directory, "02-narrow-1024-drawer.png");

            // ③ 抽屉 + 底栏展开：内容带被两头压缩后的最紧态。
            window.OpenDockPage("notifications", DockZone.Bottom);
            await Task.Delay(220);
            window.UpdateLayout();
            var tightHeader = chat.Bounds.Top + chat.HeaderBandHeight;
            var tightComposer = chat.Bounds.Top + chat.Bounds.Height - chat.BottomReserveHeight;
            var tight = window.FindControl<Border>("RightPanelBorder")!;
            check(tight.Bounds.Top >= tightHeader - 0.5 && tight.Bounds.Bottom <= tightComposer + 0.5,
                $"抽屉+底栏展开的最紧态仍不覆盖两区 bottom={tight.Bounds.Bottom} composer={tightComposer}");
            check(tight.Bounds.Height >= 200, "最紧态抽屉仍保留 ≥200DIP");
            Save(window, directory, "03-narrow-1024-drawer-terminal.png");
            await window.ActionsForTesting.ExecuteAsync("toggle.bottom");
            await Task.Delay(120);

            // 收起右栏：避让边距归零，不留残留。
            await window.ActionsForTesting.ExecuteAsync("toggle.right");
            await Task.Delay(180);
            window.UpdateLayout();
            check(!drawer.IsVisible && drawer.Margin == default(Avalonia.Thickness), "收起右栏后避让边距归零");
        }
        finally
        {
            window.Close();
            await Task.Delay(80);
            Program.SelfTestDataRoot = previousRoot;
        }
    }

    // ── Files 选中 / 双击 → E1 内置浏览器预览 ──
    private static async Task PreviewWiringAsync(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "fufucode-d6-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "子目录"));
        var filePath = Path.Combine(root, "预览文件.txt");
        File.WriteAllText(filePath, "fixture");
        try
        {
            using var vm = new WorkspacePanelViewModel(new RecordingClipboardService());
            vm.AttachMock(root);
            await vm.RefreshFilesAsync();
            var file = vm.Files.Single(x => !x.IsDirectory);
            var folder = vm.Files.Single(x => x.IsDirectory);

            var view = new WorkspacePanelView { DataContext = vm };
            var host = new Window { Content = view, Width = 420, Height = 620 };
            host.Show();
            try
            {
                host.UpdateLayout();
                var browser = view.FindControl<BrowserView>(BrowserView.PreviewControlName);
                check(browser is not null, "x:Name=PreviewBrowser 契约在真实右栏内可定位");
                if (browser is null) return;
                check(!browser.ViewModel.IsFileMode, "初始未选择文件时浏览器保持自由导航态");

                // 单击文件：走行内按钮的真实 Click 路由（与用户点击同一路径）。
                var row = RowOf(view, vm, file);
                check(row is not null, "Files 行可定位（列表已实现容器）");
                if (row is null) return;
                Click(row);
                await Task.Delay(80);
                check(browser.ViewModel.IsFileMode && browser.ViewModel.CurrentFilePath == file.FullPath &&
                      browser.ViewModel.ModeText == "文件预览", "Files 单击文件 → 内置浏览器进入文件预览态");
                check(vm.SelectedTab!.Kind == WorkspacePanelKind.Files, "单击只就位预览目标，不抢 Files tab");

                // 双击（同一文件行连续第二次点击）：打开预览并切到 Preview tab。
                Click(row);
                await Task.Delay(80);
                check(vm.IsFilePreviewOpen &&
                      browser.ViewModel.CurrentFilePath == file.FullPath,
                    "Files 双击文件 → 进入工作面板内文件预览态且预览目标不变");

                // 目录不进入预览（只做逐层浏览）。
                vm.CloseFilePreviewCommand.Execute(null);
                await Task.Delay(60);
                var folderRow = RowOf(view, vm, folder);
                if (folderRow is not null) Click(folderRow);
                await Task.Delay(120);
                check(browser.ViewModel.CurrentFilePath == file.FullPath && vm.CurrentDirectory == folder.FullPath,
                    "目录只做逐层浏览，不抢占预览目标");
            }
            finally
            {
                host.Close();
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>取文件/目录行内的点击按钮（真实 Click 路由的发起者）。</summary>
    private static Button? RowOf(WorkspacePanelView view, WorkspacePanelViewModel vm,
        WorkspacePanelFileItemViewModel item)
    {
        var list = view.FindControl<ListBox>("FilesList");
        var container = list?.ContainerFromIndex(vm.Files.IndexOf(item)) as Visual;
        return container?.GetVisualDescendants().OfType<Button>().FirstOrDefault();
    }

    private static void Click(Button row) => row.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static void Save(Window window, string directory, string name)
    {
        using var frame = window.CaptureRenderedFrame();
        if (frame is null) return;
        frame.Save(Path.Combine(directory, name), PngBitmapEncoderOptions.Default);
    }
}
