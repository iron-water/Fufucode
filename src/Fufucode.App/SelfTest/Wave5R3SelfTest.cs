using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fufucode.App.Abstractions;
using Fufucode.App.Integration;
using Fufucode.App.ViewModels;
using Fufucode.App.Views;

namespace Fufucode.App.SelfTest;

public static class Wave5R3SelfTest
{
    public static async Task RunAsync(Action<string> pass, Action<string> fail)
    {
        void Check(bool value, string label) { if (value) pass("W5-R3 主窗口：" + label); else fail("W5-R3 主窗口：" + label); }
        var directory = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "tmp", "w5r3"));
        Directory.CreateDirectory(directory);
        var priorTheme = Application.Current!.RequestedThemeVariant;
        var previousRoot = Program.SelfTestDataRoot;
        Program.SelfTestDataRoot = Path.Combine(Path.GetTempPath(), "fufucode-r3-" + Guid.NewGuid().ToString("N"));
        var window = new MainWindow(); window.Show();
        try
        {
            window.FindControl<TransitioningContentControl>("PageHost")!.PageTransition = null;
            var vm = (MainViewModel)window.DataContext!;
            vm.Input.DraftText = "五区往返保留的中文草稿";
            vm.SetRightPanelOpen(true);
            await vm.WorkspacePanel.RefreshAsync();
            var tab = vm.WorkspacePanel.SelectedTab;
            var dock = window.TerminalForTesting; dock.Show();
            var terminal = dock.SelectedTab;
            var input = window.GetVisualDescendants().OfType<TextBox>().First(x => x.Name == "InputBox");
            input.Focus();
            await window.ActionsForTesting.ExecuteAsync("settings");
            await Ready();
            Check(vm.IsSettingsPage && !window.FindControl<Border>("RightPanelBorder")!.IsVisible &&
                !window.FindControl<Border>("TerminalBorder")!.IsVisible && terminal!.IsRunning, "独立设置隐藏工作面板且终端仍运行");
            Check(window.ActionsForTesting.GetDisabledReason("files") == "返回工作区后可使用", "设置期间菜单与快捷键的工作区能力一致");
            foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                vm.Settings.ThemeIndex = theme == ThemeVariant.Light ? 1 : 2;
                Application.Current.RequestedThemeVariant = theme;
                await Ready(); Save("settings-" + theme.ToString().ToLowerInvariant() + ".png");
                var close = window.FindControl<CaptionAssetButton>("CaptionClose")!;
                close.StateForTesting = "hover"; close.RefreshImage();
                Check(close.AssetPathForTesting.Contains("/" + theme.ToString().ToLowerInvariant() + "/close-hover@2x.png") &&
                    close.Bounds.Width == 46 && close.Bounds.Height == 32, "Caption " + theme + " 的46×32完整hover资产");
                Save("caption-hover-" + theme.ToString().ToLowerInvariant() + ".png");
                close.StateForTesting = null; close.RefreshImage();
            }
            vm.Settings.ProxyInput = "http://unsaved-fixture:8080";
            vm.Settings.SelectedCategory = vm.Settings.Categories.Single(x => x.Id == "network");
            await Ready(); Save("settings-network-dark.png");
            vm.Settings.SelectedCategory = vm.Settings.Categories.Single(x => x.Id == "notifications");
            await Ready(); Save("settings-notifications-dark.png");
            vm.Settings.SelectedCategory = vm.Settings.Categories.Single(x => x.Id == "appearance");
            window.ConfirmDiscardSettingsForTesting = _ => Task.FromResult(false);
            await window.NavigateForTestingAsync(AppPage.Chat);
            Check(vm.IsSettingsPage && vm.Settings.HasUnsavedChanges, "未保存网络草稿选择继续编辑阻止离页");
            window.ConfirmDiscardSettingsForTesting = _ => Task.FromResult(true);
            await window.NavigateForTestingAsync(AppPage.Chat); await Ready();
            Check(vm.IsChatPage && !vm.Settings.HasUnsavedChanges && ReferenceEquals(tab, vm.WorkspacePanel.SelectedTab) &&
                ReferenceEquals(terminal, dock.SelectedTab) && dock.IsVisible && vm.IsRightPanelOpen &&
                vm.Input.DraftText == "五区往返保留的中文草稿", "丢弃仅设置草稿，恢复会话右tab底栏与输入");
            Check(ReferenceEquals(window.FocusManager?.GetFocusedElement(), input), "设置返回恢复原composer焦点");
            var maximize = window.FindControl<CaptionAssetButton>("CaptionMaximize")!;
            maximize.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Ready();
            Check(window.WindowState == WindowState.Maximized && maximize.Kind == "restore", "最大化同步还原按钮");
            maximize.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Ready();
            Check(window.WindowState == WindowState.Normal && maximize.Kind == "maximize", "还原同步最大化按钮");
            var originalRow = vm.Sidebar.SelectedSession;
            var longTitleRow = new SessionItemViewModel(new string('长', 100), "fixture", isCurrent: true);
            vm.Sidebar.Sessions.Add(longTitleRow);
            vm.Sidebar.SelectedSession = longTitleRow;
            window.Width = 1200; vm.SetRightPanelOpen(false); await Ready();
            var chat = (ChatPage)window.FindControl<TransitioningContentControl>("PageHost")!.Content!;
            chat.ApplyAppearance(chat.Appearance with { ShowSessionHeader = true }); await Ready();
            var headerTitle = window.GetVisualDescendants().OfType<Avalonia.Controls.TextBlock>().Single(x => x.Name == "ConversationTitle");
            var details = window.GetVisualDescendants().OfType<Button>().Single(x => x.Name == "SessionDetailsButton");
            var titleEnd = headerTitle.TranslatePoint(new Point(headerTitle.Bounds.Width, 0), window);
            var actionsStart = details.TranslatePoint(new Point(0, 0), window);
            Check(headerTitle.Text == longTitleRow.Title && titleEnd is { } edge && actionsStart is { } start &&
                edge.X <= start.X && details.IsEffectivelyVisible && details.Bounds.Width == 32,
                "长标题截断且保留详情与面板入口，不堆叠常态chips");
            Check(window.GetVisualDescendants().OfType<Button>().Any(button => button.IsEffectivelyVisible &&
                button.GetVisualDescendants().OfType<PathIcon>().Any(x => x.Data is not null) &&
                (ToolTip.GetTip(button) as string)?.StartsWith("右侧边栏", StringComparison.Ordinal) == true),
                "工作区入口使用实际存在的图标geometry");
            Save("header-long-title-1200.png");
            vm.Sidebar.SelectedSession = originalRow;
            vm.Sidebar.Sessions.Remove(longTitleRow);
            window.Width = 1440; vm.SetRightPanelOpen(true); await Ready();

            foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                Application.Current.RequestedThemeVariant = theme;
                input.Focus();
                SendKey(Key.K, KeyModifiers.Control); await Ready();
                Check(window.PaletteForTesting.IsOpen && window.FindControl<Grid>("GlobalOverlay")!.IsVisible, "Ctrl+K 打开统一命令面板 " + theme);
                Save("commands-" + theme.ToString().ToLowerInvariant() + ".png");
                window.PaletteForTesting.CloseCommand.Execute(null); await Ready();
                Check(ReferenceEquals(window.FocusManager?.GetFocusedElement(), input), "关闭命令面板恢复触发焦点 " + theme);
                await window.ActionsForTesting.ExecuteAsync("usage"); await Ready();
                Check(window.UsageForTesting.IsMock && window.UsageForTesting.Counters.Any(x => x.IsUnknown), "用量mock保留unknown语义 " + theme);
                Save("usage-" + theme.ToString().ToLowerInvariant() + ".png");
                SendKey(Key.Escape, KeyModifiers.None); await Ready();
                Check(!window.FindControl<Grid>("GlobalOverlay")!.IsVisible, "Esc关闭用量并恢复工作面");
            }
            var screen = window.GetVisualDescendants().OfType<TerminalScreenControl>().First();
            screen.Focus(); SendKey(Key.P, KeyModifiers.Control | KeyModifiers.Shift); await Ready();
            Check(!window.PaletteForTesting.IsOpen, "终端焦点保留Ctrl+Shift+P给shell");
            input.Focus(); SendKey(Key.P, KeyModifiers.Control | KeyModifiers.Shift); await Ready();
            Check(window.PaletteForTesting.IsOpen, "composer焦点Ctrl+Shift+P可打开命令面板");
            window.PaletteForTesting.CloseCommand.Execute(null);
            await Ready();
            await window.ActionsForTesting.ExecuteAsync("commands"); await Ready();
            window.PaletteForTesting.SearchText = "审查"; await Ready();
            var paletteView = window.GetVisualDescendants().OfType<CommandPaletteView>().First();
            var search = paletteView.FindControl<TextBox>("SearchBox")!;
            Console.WriteLine($"E1 palette before: selected={window.PaletteForTesting.SelectedItem?.Id}, reason={window.PaletteForTesting.SelectedItem?.DisabledReason}, page={vm.IsChatPage}");
            search.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Enter }); await Ready();
            Console.WriteLine($"E1 palette after: open={window.PaletteForTesting.IsOpen}, feedback={window.PaletteForTesting.Feedback}, visible={window.PageForTesting("local-review").IsEffectivelyVisible}, width={window.PageForTesting("local-review").Bounds.Width}, dock={window.DockLayoutForTesting.Selected[Infrastructure.DockZone.Right]}");
            Check(!window.PaletteForTesting.IsOpen && window.PageAvailableForTesting("local-review") &&
                window.PageForTesting("local-review").IsEffectivelyVisible &&
                window.PageForTesting("local-review") is Features.LocalReview.LocalReviewPageView { ViewModel: not null },
                "E1 已接线审查搜索结果：Enter打开真实模块页面");
            input.Focus();
            await window.ActionsForTesting.ExecuteAsync("commands"); await Ready();
            search.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Escape }); await Ready();
            Check(!window.PaletteForTesting.IsOpen && ReferenceEquals(window.FocusManager?.GetFocusedElement(), input), "真实面板Esc事件退出并恢复composer");
            SendKey(Key.P, KeyModifiers.Control); await Ready();
            Check(vm.WorkspacePanel.SelectedTab?.Kind == WorkspacePanelKind.Files, "Ctrl+P选择真实Files tab");
            var scripted = new ScriptedChatSession();
            window.NotificationsForTesting.Attach("mock-notification-session", Guid.NewGuid(), scripted);
            window.NotificationsForTesting.BeginSubmission();
            scripted.Raise(new SessionEvent.TurnSettled(SessionOutcome.Completed, null));
            await window.NotificationsForTesting.FlushAsync();
            window.NotificationsForTesting.BeginSubmission();
            scripted.Raise(new SessionEvent.TurnSettled(SessionOutcome.Failed, null));
            await window.NotificationsForTesting.FlushAsync();
            await window.ActionsForTesting.ExecuteAsync("notifications"); await Ready();
            Check(window.NotificationsForTesting.Items.Count == 2, "页内中心渲染可靠完成/失败fixture");
            Save("notifications-dark.png");
            SendKey(Key.Escape, KeyModifiers.None);
            await window.NavigateForTestingAsync(AppPage.Settings);
            window.Width = 800; await Ready(); Save("settings-800.png");
            await window.NavigateForTestingAsync(AppPage.Chat);
            window.Width = 1440; await Ready(); Save("five-zones-dark.png");
            Check(window.FindControl<Border>("CustomCaption")!.Bounds.Height == 48 &&
                window.FindControl<Border>("CustomCaption")!.Bounds.Width == window.Bounds.Width && window.WindowDecorations == WindowDecorations.BorderOnly, "自绘caption独占48DIP全局工具行，不叠系统标题");
            await window.NavigateForTestingAsync(AppPage.Extensions);
            vm.Extensions.PreviewSnapshot(new ExtensionCatalogSnapshot());
            await Ready(); Save("extensions-not-installed-dark.png");
            vm.Extensions.PreviewSnapshot(new ExtensionCatalogSnapshot
            {
                DiscoveredExtensions = [new(@"C:\R3\fixture\example.ts", "project", false)],
            });
            vm.Extensions.ExtensionFilter = "no-match-fixture";
            await Ready(); Save("extensions-no-match-dark.png");
            vm.Extensions.PreviewSnapshot(new ExtensionCatalogSnapshot { QueryError = "fixture：扩展查询失败" });
            await Ready(); Save("extensions-failed-dark.png");
        }
        finally
        {
            window.Close(); await Task.Delay(80);
            Application.Current!.RequestedThemeVariant = priorTheme;
            Program.SelfTestDataRoot = previousRoot;
        }
        async Task Ready() { await Task.Delay(100); await Dispatcher.Yield(DispatcherPriority.Background); window.UpdateLayout(); }
        void SendKey(Key key, KeyModifiers modifiers) => window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers });
        void Save(string name)
        {
            using var frame = window.CaptureRenderedFrame();
            if (frame is null) { fail("W5-R3 无截图帧：" + name); return; }
            frame.Save(Path.Combine(directory, name), PngBitmapEncoderOptions.Default);
        }
    }
}
