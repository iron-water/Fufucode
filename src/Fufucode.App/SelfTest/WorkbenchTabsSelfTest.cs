using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Fufucode.App.Infrastructure;
using Fufucode.App.ViewModels;
using Fufucode.App.Views;
using Fufucode.Core;

namespace Fufucode.App.SelfTest;

internal static class WorkbenchTabsSelfTest
{
    internal static async Task RunAsync(Action<string> pass, Action<string> fail)
    {
        void Check(bool value, string label) { if (value) pass("Tabs: " + label); else fail("Tabs: " + label); }
        var root = Path.GetFullPath("tmp/tabs-tests-" + Guid.NewGuid().ToString("N"));
        var project = Directory.CreateDirectory(Path.Combine(root, "project")).FullName;
        await Git("init"); await Git("config", "user.name", "Fixture"); await Git("config", "user.email", "fixture@example.invalid");
        var file = Path.Combine(project, "tracked.txt");
        File.WriteAllText(file, "original\nkeep\n");
        await Git("add", "tracked.txt"); await Git("commit", "-m", "fixture");
        File.WriteAllText(file, "staged\nkeep\n"); await Git("add", "tracked.txt");
        File.WriteAllText(file, "updated\nkeep\n");
        File.WriteAllText(Path.Combine(project, "new.txt"), "new marker\n");
        var dir = Directory.CreateDirectory(Path.Combine(project, "folder"));
        File.WriteAllText(Path.Combine(dir.FullName, "child.txt"), "child\n");
        var previous = Program.SelfTestDataRoot;
        Program.SelfTestDataRoot = Path.Combine(root, "appdata");
        var window = new MainWindow { Width = 1440, Height = 900 }; window.Show();
        try
        {
            await Ready();
            var vm = (MainViewModel)window.DataContext!;
            var workspace = vm.WorkspacePanel;
            workspace.AttachContext(project, "tabs", ct => ChangeRangeQuery.WorkspaceAsync(project, ct), ct => ChangeRangeQuery.WorkspaceAsync(project, ct));
            await workspace.RefreshAsync();
            foreach (var id in WorkbenchLayoutState.KnownPages)
            {
                // "terminal" and "browser" are optional pages now; without their module/plugin
                // they must not exist, and the test should not treat their absence as a failure.
                if (!window.PageAvailableForTesting(id))
                {
                    Check(id is "terminal" or "browser" or "automation" &&
                          (id != "terminal" || window.TerminalPageForTesting is null),
                        id + " is absent because its optional module/plugin is not installed");
                    continue;
                }
                window.MoveDockPage(id, DockZone.Left); await Ready();
                var page = window.PageForTesting(id);
                Check(page.IsEffectivelyVisible && page.Bounds.Width > 0, id + " opens in left sidebar");
                window.MoveDockPage(id, DockZone.Right); await Ready();
                Check(ReferenceEquals(page, window.PageForTesting(id)) && page.IsEffectivelyVisible, id + " moves without replacing state");
                var tab = window.GetVisualDescendants().OfType<Button>().Single(b => Avalonia.Automation.AutomationProperties.GetAutomationId(b) == "dock-tab-" + id);
                var close = tab.ContextMenu!.Items.OfType<MenuItem>().Single(m => m.Tag as string == "dock-close");
                close.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); await Ready();
                Check(window.DockLayoutForTesting.Find(id) is null, id + " closes from its native menu");
                window.OpenDockPage(id, DockZone.Right); await Ready();
                Check(ReferenceEquals(page, window.PageForTesting(id)) && page.IsEffectivelyVisible, id + " reopens the same content");
            }
            await window.ActionsForTesting.ExecuteAsync("toggle.left"); await Ready();
            Check(window.DockLayoutForTesting.Pages[DockZone.Left].Count > 0 &&
                window.FindControl<Border>("SidebarBorder")!.IsVisible &&
                window.FindControl<DockRegionView>("LeftDock")!.GetVisualDescendants().OfType<Button>()
                    .Any(b => Avalonia.Automation.AutomationProperties.GetAutomationId(b)?.StartsWith("dock-tab-", StringComparison.Ordinal) == true && b.IsEffectivelyVisible),
                "empty region reopens by seeding an unowned default page");
            window.OpenDockPage("files", DockZone.Right); await Ready();
            var files = window.PageForTesting("files");
            Click(files.GetVisualDescendants().OfType<Button>().Single(b => b.DataContext is WorkspacePanelFileItemViewModel { Name: "folder" }));
            await Until(() => workspace.CurrentDirectory == dir.FullName && !workspace.IsFilesLoading);
            Check(workspace.Files.Single().Name == "child.txt", "file row enters actual directory");
            Click(files.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "上一级"));
            await Until(() => workspace.CurrentDirectory == project && !workspace.IsFilesLoading);
            Click(files.GetVisualDescendants().OfType<Button>().Single(b => b.DataContext is WorkspacePanelFileItemViewModel { Name: "tracked.txt" })); await Ready();
            Click(files.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "打开预览")); await Ready();
            var previewBrowser = files.GetVisualDescendants().OfType<BrowserView>().Single(b => b.Name == BrowserView.PreviewControlName);
            Check(workspace.IsFilePreviewOpen && previewBrowser.ViewModel.CurrentFilePath == file, "file preview button opens the actual selected file in-place");
            var backButton = files.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "← 返回列表");
            Click(backButton); await Ready();
            Check(!workspace.IsFilePreviewOpen, "returning from in-place file preview restores file list");
            File.WriteAllText(Path.Combine(project, "refresh.txt"), "refresh\n");
            window.OpenDockPage("files", DockZone.Right); await Ready();
            Click(files.GetVisualDescendants().OfType<Button>().Single(b => Avalonia.Automation.AutomationProperties.GetName(b) == "刷新文件"));
            await Until(() => workspace.Files.Any(f => f.Name == "refresh.txt"));
            Check(workspace.Files.Any(f => f.Name == "refresh.txt"), "detached file tab retains working refresh command");
            files.GetVisualDescendants().OfType<TextBox>().Single(t => Avalonia.Automation.AutomationProperties.GetName(t) == "搜索当前目录").Text = "REFRESH";
            await Ready();
            Check(workspace.VisibleFiles.Single().Name == "refresh.txt", "native file search filters actual rows without case sensitivity");
            workspace.FileFilter = ""; await Ready();
            window.OpenDockPage("changes", DockZone.Right); await Ready();
            var changes = window.PageForTesting("changes");
            changes.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "ChangesList").SelectedItem = workspace.Changes.Single(c => c.Path == "tracked.txt");
            await Until(() => workspace.HasDiff);
            Check(workspace.DiffText.Contains("-original") && workspace.DiffText.Contains("+updated") && !workspace.DiffText.Contains("-staged"), "selected native change row loads HEAD-to-current diff including staged changes");
            Click(changes.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "打开当前文件")); await Ready();
            Check(workspace.IsFilePreviewOpen && previewBrowser.ViewModel.CurrentFilePath == file, "changes open-current-file opens in-place file preview");
            Click(backButton); await Ready();
            workspace.SelectedChange = workspace.Changes.Single(c => c.Path == "new.txt");
            await Until(() => workspace.HasDiff);
            Check(workspace.DiffText.Contains("+new marker"), "untracked file shows actual added lines rather than empty diff");
            window.ResetDockLayout(); window.OpenDockPage("changes", DockZone.Right); await Ready();
            Check(changes.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "ChangesList").Bounds.Height > 60,
                "default layout keeps change list usable when the diff and bottom terminal are open");
            using (var frame = window.CaptureRenderedFrame()) frame?.Save(Path.Combine(root, "changes.png"), PngBitmapEncoderOptions.Default);
            var mainSession = vm.Session;
            window.OpenDockPage("sidechat", DockZone.Right); await Ready();
            var side = window.PageForTesting("sidechat");
            Click(side.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "新建侧边聊天"));
            var sideChat = side.GetVisualDescendants().OfType<ChatPage>().Single();
            await Until(() => sideChat.DataContext is MainViewModel);
            var sideVm = (MainViewModel)sideChat.DataContext!;
            sideVm.Input.DraftText = "side draft marker";
            window.OpenDockPage("files", DockZone.Right); window.OpenDockPage("sidechat", DockZone.Right); await Ready();
            Check(!ReferenceEquals(mainSession, sideVm.Session) && vm.Input.DraftText != "side draft marker" && sideVm.Input.DraftText == "side draft marker", "side chat creates independent session and retains draft across tab switches");
            await workspace.RefreshFilesAsync();
            Check(workspace.Files.Any(f => f.Name == "tracked.txt"), "creating side chat leaves main workspace resources alive");
            window.CustomizationForTesting.EditLayout(l => l.RightWidth = 440);
            await Ready();
            var toolbar = sideChat.FindControl<Grid>("ComposerToolbar")!;
            Check(toolbar.Children.All(c => c.Bounds.Left >= 0 && c.Bounds.Right <= toolbar.Bounds.Width + 1), "side-chat composer controls fit the narrow dock");
            using (var frame = window.CaptureRenderedFrame()) frame?.Save(Path.Combine(root, "sidechat.png"), PngBitmapEncoderOptions.Default);
            window.CustomizationForTesting.EditLayout(l => l.RightWidth = 232); await Ready();
            Check(toolbar.Children.All(c => c.Bounds.Left >= 0 && c.Bounds.Right <= toolbar.Bounds.Width + 1), "side-chat composer controls also fit the minimum sidebar width");
            using (var frame = window.CaptureRenderedFrame()) frame?.Save(Path.Combine(root, "sidechat-232.png"), PngBitmapEncoderOptions.Default);
            window.CustomizationForTesting.EditLayout(l => l.RightWidth = 440); await Ready();
            Check(!window.PageAvailableForTesting("ask") && !window.PageAvailableForTesting("outline"), "reference draft and outline pages are gone from the page catalog");
            var overview = window.PageForTesting("overview"); window.OpenDockPage("overview", DockZone.Left); await Ready();
            Check(overview.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "新建会话") &&
                new[] { "置顶", "分区", "项目", "会话" }.All(label =>
                    overview.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == label)) &&
                !overview.GetVisualDescendants().OfType<Button>().Any(b => AutomationProperties.GetName(b) == "重命名此会话"),
                "session management exposes the four categories without the obsolete always-visible rename button");
            foreach (var page in new[] { AppPage.Settings, AppPage.Extensions })
            {
                await window.NavigateForTestingAsync(page); await Ready();
                window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null); await Ready();
                Check(vm.IsChatPage, page + " returns by Escape");
            }
            await ((Mock.MockChatSession)vm.Session).GenerateLongConversationAsync(20);
            Check(!window.PageAvailableForTesting("preview") && window.DockLayoutForTesting.Find("preview") is null,
                "file preview is not an independent dock page and stays in-place in workspace panel");
            window.Width = 800; await Ready();
            window.OpenDockPage("outline", DockZone.Right); await Ready();
            Check(!window.FindControl<Border>("RightPanelBorder")!.IsVisible && vm.IsChatPage,
                "the narrow drawer collapses instead of crowding the conversation");
            // 旧Pin/专用浮窗断言已被用户新F1语义取代；保留并加强独立渠道与同一控件生命周期门。
            var delivery = window.NotificationDeliveryForTesting;
            Check(delivery.DeliversToToast && delivery.DeliversToPage && !delivery.DeliversToWindows,
                "notification delivery defaults to in-app and page prompts with Windows opt-in");
            window.OpenNotificationCenterOverlay(); await Ready();
            Check(vm.CurrentPage == AppPage.Chat && window.NotificationCenterVisibleForTesting,
                "opening the notification function page keeps the workspace page");
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null); await Ready();
            Check(!window.NotificationCenterVisibleForTesting, "Escape closes the notification function page");
            delivery.DeliversToToast = false; await Ready();
            Check(!delivery.DeliversToToast && delivery.DeliversToPage &&
                !window.FindControl<Border>("NotificationPrompt")!.IsVisible,
                "turning toast off preserves the independent notification page channel");
            delivery.DeliversToToast = true; await Ready();
            delivery.DeliversToToast = false; delivery.DeliversToWindows = true;
            window.NotificationsForTesting.LatestPrompt = new NotificationItemViewModel("toast-fallback-test", "mock", Guid.NewGuid(), 1,
                AppNotificationKind.Completed, "Finished", "Safe message", null, true);
            await Ready();
            Check(window.FindControl<Border>("NotificationPrompt")!.IsVisible && delivery.WindowsDeliveryStatus.Contains("回退", StringComparison.Ordinal),
                "failed Windows delivery falls back to in-app prompt even when toast preference is off");
            window.NotificationsForTesting.LatestPrompt = null;
            delivery.DeliversToWindows = false; delivery.DeliversToToast = true; await Ready();
            Check(!window.ActionsForTesting.Actions.Any(action => action.Id.StartsWith("floating.", StringComparison.Ordinal)),
                "dedicated floating-window actions are retired");
            window.OpenDockPage("sidechat", DockZone.Right); await Ready();
            var sideHost = window.SideChatHostForTesting;
            var floatingId = window.FloatDockPage("sidechat")!; await Ready();
            Check(ReferenceEquals(TopLevel.GetTopLevel(sideHost), window.FloatingDockWindowForTesting(floatingId)),
                "auxiliary chat floats in its generic page without a second conversation host");
            window.DockFloatingRegion(floatingId); await Ready();
            Check(ReferenceEquals(window.SideChatHostForTesting, sideHost) && ReferenceEquals(TopLevel.GetTopLevel(sideHost), window),
                "redocking auxiliary chat returns the original host");
            Console.WriteLine("Tabs evidence: " + root);
        }
        finally { window.Close(); await Task.Delay(150); Program.SelfTestDataRoot = previous; }
        async Task Ready() { await Task.Delay(100); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
        void Click(Button button)
        {
            var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
            window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
        }
        async Task Until(Func<bool> condition)
        {
            var end = DateTime.UtcNow.AddSeconds(10);
            while (!condition() && DateTime.UtcNow < end) await Ready();
            if (!condition()) throw new TimeoutException("Tab operation did not complete");
            await Ready();
        }
        async Task Git(params string[] args)
        {
            var start = new ProcessStartInfo("git") { WorkingDirectory = project, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in args) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(); await output;
            if (process.ExitCode != 0) throw new InvalidOperationException(await error);
            await error;
        }
    }
}
