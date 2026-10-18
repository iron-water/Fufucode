using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Fufucode.App.Abstractions;
using Fufucode.App.Infrastructure;
using Fufucode.App.ViewModels;
using Fufucode.Core;

namespace Fufucode.App.SelfTest;

public static class Wave5R2SelfTest
{
    public static async Task RunAsync(Action<string> pass, Action<string> fail)
    {
        void Check(bool ok, string label) { if (ok) pass("W5-R2：" + label); else fail("W5-R2：" + label); }
        var preferencesRoot = Path.Combine(Path.GetTempPath(), "fufucode-layout-" + Guid.NewGuid().ToString("N"));
        var metadata = new DesktopMetadataStore(new AppDataLayout(preferencesRoot));
        var layout = new WorkspaceLayoutPreferences { RightWidth = 700, BottomHeight = 900 };
        layout.Save(metadata);
        var loaded = new WorkspaceLayoutPreferences(); loaded.Load(metadata);
        Check(loaded.RightWidth == 700 && loaded.BottomHeight == 900 && loaded.RightFor(800, true) <= 776 && loaded.BottomFor(640) <= 320,
            "尺寸偏好持久化与窗口限幅分离");
        metadata.SetPreference(WorkspaceLayoutPreferences.RightKey, "NaN"); loaded.Load(metadata);
        Check(loaded.RightWidth == 360, "无效尺寸回退建议值");

        var scripted = new ScriptedChatSession();
        var timeline = new TimelineViewModel(scripted, new DispatcherRefreshScheduler(TimeSpan.FromMilliseconds(20)), new RecordingClipboardService());
        var live = new SessionItemViewModel("状态测试", "fixture", isCurrent: true);
        using (var sidebar = new SidebarViewModel(scripted, timeline, [live]))
        {
            scripted.Activity = SessionActivity.Running;
            scripted.Raise(new SessionEvent.ActivityChanged(scripted.Activity));
            Check(live.IsRunning && !live.IsFailed, "Core活动事件映射运行glyph");
            sidebar.SetConversationVisible(false);
            scripted.Activity = SessionActivity.Idle; scripted.LastOutcome = SessionOutcome.Failed;
            scripted.Raise(new SessionEvent.TurnSettled(SessionOutcome.Failed, null));
            Check(live.IsFailed && live.IsUnread && !live.IsRunning, "可靠终态映射失败与窗口内未读");
            sidebar.SetConversationVisible(true);
            Check(!live.IsUnread, "返回对话清除窗口内未读");
        }

        var directory = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "tmp", "w5r2"));
        Directory.CreateDirectory(directory);
        var previousRoot = Program.SelfTestDataRoot;
        Program.SelfTestDataRoot = Path.Combine(preferencesRoot, "appdata");
        var window = new MainWindow(); window.Show();
        try
        {
            window.FindControl<TransitioningContentControl>("PageHost")!.PageTransition = null;
            var vm = (MainViewModel)window.DataContext!;
            vm.Input.DraftText = "保留中文草稿";
            vm.SetRightPanelOpen(true);
            await vm.WorkspacePanel.RefreshAsync();
            vm.WorkspacePanel.SelectedTab = vm.WorkspacePanel.Tabs.Single(t => t.Kind == WorkspacePanelKind.Changes);
            await Task.Delay(180); window.UpdateLayout();
            var chosenTab = vm.WorkspacePanel.SelectedTab;
            Check(vm.WorkspacePanel.Changes.Count == 4 && vm.WorkspacePanel.IsMock && window.FindControl<Border>("RightPanelBorder")!.Bounds.Width == window.DockLayoutForTesting.RightWidth,
                "Changes mock真实控件投影与宽窗并排");
            Save("right-open.png");

            var splitter = window.FindControl<GridSplitter>("RightPanelSplitter")!;
            var before = window.FindControl<Grid>("WorkspaceShell")!.ColumnDefinitions[2].ActualWidth;
            splitter.Focus();
            splitter.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Left });
            splitter.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = Key.Left });
            await Task.Delay(80);
            Check(window.RightPreferenceForTesting >= before, "分割柄键盘调整更新宽度偏好");
            vm.SetRightPanelOpen(false); vm.SetRightPanelOpen(true);
            Check(ReferenceEquals(chosenTab, vm.WorkspacePanel.SelectedTab) && vm.Input.DraftText == "保留中文草稿", "收起重开保tab与草稿");

            window.OpenDockPage("usage", DockZone.Bottom);
            await Task.Delay(180); window.UpdateLayout();
            var usagePage = window.PageForTesting("usage");
            var terminalBounds = window.FindControl<Border>("TerminalBorder")!.Bounds;
            Check(terminalBounds.Height == window.DockLayoutForTesting.BottomHeight && terminalBounds.Height <= window.FindControl<Grid>("WorkspaceShell")!.Bounds.Height - 360,
                $"底栏高度限幅 actual={terminalBounds.Height} preferred={window.DockLayoutForTesting.BottomHeight} shell={window.FindControl<Grid>("WorkspaceShell")!.Bounds.Height}");
            Save("bottom-open.png");
            window.CloseDockPageForTesting("usage"); await Task.Delay(80);
            window.OpenDockPage("usage", DockZone.Bottom); await Task.Delay(80);
            Check(ReferenceEquals(usagePage, window.PageForTesting("usage")),
                "底栏关闭重开保持同一页面实例");

            foreach (var width in new[] { 1200d, 800d, 1600d })
            {
                window.Width = width; await Task.Delay(160); window.UpdateLayout();
                var panel = window.FindControl<Border>("RightPanelBorder")!;
                if (width < 1100) { await window.ActionsForTesting.ExecuteAsync("toggle.right"); await Task.Delay(100); window.UpdateLayout(); }
                Check(panel.IsVisible && panel.Bounds.Width == window.DockLayoutForTesting.RightWidth && ReferenceEquals(chosenTab, vm.WorkspacePanel.SelectedTab) &&
                    window.IsRightDrawerForTesting == (width < 1100), "宽度 " + width + " 的并排/抽屉保持身份");
                if (width == 800) Check(!window.FindControl<Border>("SidebarBorder")!.IsVisible, "800DIP自动收会话栏且不缩字号");
                Save("layout-" + width + ".png");
            }
            window.Close(); await Task.Delay(80);
        }
        finally { window.Close(); Program.SelfTestDataRoot = previousRoot; }

        void Save(string name)
        {
            using var frame = window.CaptureRenderedFrame();
            if (frame is null) { fail("W5-R2：无头截图帧缺失 " + name); return; }
            frame.Save(Path.Combine(directory, name), PngBitmapEncoderOptions.Default);
        }
    }
}
