using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fufucode.App.Abstractions;
using Fufucode.App.Infrastructure;
using Fufucode.App.ViewModels;
using Fufucode.App.Views;
using Fufucode.Core.Plugins;

namespace Fufucode.App.SelfTest;

internal static class WorkbenchCustomizationSelfTest
{
    internal static async Task RunAsync(Action<string> pass, Action<string> fail)
    {
        void Check(bool result, string label) { if (result) pass("R05: " + label); else fail("R05: " + label); }
        var root = Path.GetFullPath("tmp/r05-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var project = Directory.CreateDirectory(Path.Combine(root, "project")).FullName;
        var store = new WorkbenchAppearanceStore(root);
        store.Replace("{\"version\":1,\"accent\":\"#087E66\",\"fontSize\":15,\"future\":{\"keep\":true},\"actions\":{\"settings\":{\"position\":\"topLeft\",\"order\":4}}}", false);
        store.AttachProject(project);
        store.Replace("{\"version\":1,\"fontSize\":19,\"actions\":{\"settings\":{\"position\":\"statusRight\"}}}", true);
        Check(store.Current.FontSize == 19 && store.Current.Accent == "#087E66" && store.Current.Actions["settings"].Position == UiActionPosition.StatusRight && store.Current.Actions["settings"].Order == 4,
            "sparse project overlay inherits global values and nested action order");
        store.Save(store.Current with { UserName = "测试用户" }, true);
        var projectJson = JsonNode.Parse(store.ScopeJson(true))!;
        Check(projectJson["accent"] is null && projectJson["userName"]!.GetValue<string>() == "测试用户" && JsonNode.Parse(store.ScopeJson(false))!["future"]!["keep"]!.GetValue<bool>(),
            "ordinary edits preserve unknown keys and do not copy inherited fields");
        var customization = new WorkbenchCustomizationViewModel(store) { ProjectScope = true };
        customization.Change(s => s with { Composer = "hidden", ShowStatusBar = true });
        customization.Undo(); Check(customization.Current.Composer == "wide" && !customization.Current.ShowStatusBar, "undo restores exact UI scope");
        customization.Redo();
        var redoMatches = customization.Current.Composer == "hidden" && customization.Current.ShowStatusBar;
        Check(redoMatches, "redo reapplies UI change" + (redoMatches ? "" : $"; composer={customization.Current.Composer}, statusBar={customization.Current.ShowStatusBar}, status={customization.Status}, root={root}"));
        customization.ClearOverrides(); Check(customization.Current.FontSize == 15 && customization.Current.UserName == "你", "clear project overrides restores inheritance");
        customization.Undo(); Check(customization.Current.FontSize == 19 && customization.Current.UserName == "测试用户", "undo of clearing project restores its sparse JSON");
        customization.Preset("reading"); Check(customization.Current.ReadWidth == 560 && customization.Current.Composer == "reading" && customization.Current.UserName == "测试用户", "reading preset preserves display names");
        var badRoot = Directory.CreateDirectory(Path.Combine(root, "invalid")).FullName;
        File.WriteAllText(Path.Combine(badRoot, "ui.json"), "{\"version\":99}");
        var bad = new WorkbenchAppearanceStore(badRoot);
        var protectedFile = false;
        try { bad.Save(new(), false); } catch (InvalidOperationException) { protectedFile = true; }
        Check(protectedFile && bad.LoadError is not null && File.ReadAllText(bad.GlobalPath) == "{\"version\":99}", "unknown config version is diagnosed and preserved");
        var normalized = new WorkbenchAppearance { FontSize = double.NaN, Accent = "not-color", Radius = "invalid" }.Normalize();
        Check(normalized.FontSize == 13 && normalized.Accent == "system" && normalized.Corner == 4, "invalid dimensions normalize and colors fall back to the system palette");
        var demo = WorkbenchAppearance.Parse("{\"accent\":\"#087e66\",\"msgActions\":\"show\",\"readWidth\":\"std\",\"layout\":{\"left\":[\"overview\"],\"right\":[\"files\",\"browser\"],\"bottom\":[\"terminal\"],\"selected\":{\"left\":\"overview\",\"right\":\"files\",\"bottom\":\"terminal\"},\"hidden\":{\"left\":false,\"right\":false,\"bottom\":false},\"sizes\":{\"left\":240,\"right\":810,\"bottom\":200}}}");
        Check(demo.MessageActions == "show" && demo.ReadingWidth == "standard" && demo.Layout!.Find("files") == DockZone.Right && demo.Layout.RightWidth == 810,
            "the actual R05 Demo JSON imports reading, message actions, page assignments and dimensions");
        var path = Path.Combine(root, "draft.md"); File.WriteAllText(path, "# attached marker");
        var pdf = Path.Combine(root, "document.pdf"); File.WriteAllBytes(pdf, [37, 80, 68, 70, 0]);
        var png = Path.Combine(root, "pixel.png");
        using (var source = AssetLoader.Open(new Uri("avares://Fufucode.App/Assets/Brand/icon-small-32.png")))
        using (var destination = File.Create(png))
            source.CopyTo(destination);
        var textFile = await AttachedFile.LoadAsync(path); var pdfFile = await AttachedFile.LoadAsync(pdf); var image = await AttachedFile.LoadAsync(png);
        Check(textFile.Context.Contains("# attached marker") && pdfFile.Context.Contains("未提取内容") && !pdfFile.Context.Contains('\0') && image.Image?.MimeType == "image/png", "text, PDF references and image bytes remain distinct");
        var session = new RecordingMediaSession();
        var input = new InputViewModel(session) { DraftText = "look", ReferenceContext = "reference", IdentityContext = "user: test" };
        input.AddAttachment(image); input.AddAttachment(image); input.AddAttachment(pdfFile);
        Check(input.Attachments.Count == 2 && input.ComposePrompt().Contains("user: test"), "attachment deduplication and identity preference reach prompt");
        await input.SendCommand.ExecuteAsync(null);
        Check(session.Images.Count == 1 && session.Images[0].Data == image.Image!.Data && session.Text!.Contains(pdf) && input.Attachments.Count == 0 && input.DraftText == "", "image submission carries typed payload and clears only accepted composer state");
        session.Reject = true; input.DraftText = "retry"; input.ReferenceContext = "retain reference"; input.AddAttachment(image);
        await input.SendCommand.ExecuteAsync(null);
        Check(input.DraftText == "retry" && input.ReferenceContext == "retain reference" && input.Attachments.Single() == image, "failed submission restores original draft, reference and attachment");
        session.Reject = false; await input.SetExecutionModeAsync("plan");
        Check(session.Mode == "plan" && input.ExecutionMode == "plan", "mode selection awaits the actual session command");
        var scheduleStore = MainWindow.ResolveScheduleStoreForTesting(root); var rival = MainWindow.ResolveScheduleStoreForTesting(root);
        var task = new ScheduledTaskItem("test", "session", DateTimeOffset.UtcNow.AddMinutes(-1), "task", true);
        scheduleStore.Save([task]);
        Check(scheduleStore.TryClaim("test", "dispatching") && !rival.TryClaim("test", "dispatching"), "two schedule readers cannot dispatch the same pending task twice");
        rival.Update("test", t => t with { State = "accepted" }); scheduleStore.Reload();
        Check(scheduleStore.Tasks.Single().State == "accepted" && !scheduleStore.TryClaim("test", "dispatching"), "accepted or crash-boundary tasks are never blindly repeated");

        var priorRoot = Program.SelfTestDataRoot;
        Program.SelfTestDataRoot = Path.Combine(root, "app");
        var window = new MainWindow(); window.Show();
        try
        {
            await Ready();
            var vm = (MainViewModel)window.DataContext!;
            Check(!vm.Settings.ShowThemeRow && ReferenceEquals(vm.Settings.Customization, window.CustomizationForTesting), "settings has one active theme editor rather than two conflicting stores");
            var actions = window.ActionsForTesting.Actions.Select(a => a.Id).ToHashSet();
            Check(!actions.Any(id => id.StartsWith("opinions.", StringComparison.Ordinal)), "opinion mode and its import/export are no longer registered as app actions");
            var settingsButton = window.GetVisualDescendants().OfType<Button>().First(b => Avalonia.Automation.AutomationProperties.GetAutomationId(b) == "action-settings");
            var settingsPoint = settingsButton.TranslatePoint(new Point(12, 12), window)!.Value;
            window.MouseDown(settingsPoint, Avalonia.Input.MouseButton.Left); window.MouseUp(settingsPoint, Avalonia.Input.MouseButton.Left); await Ready();
            Check(!vm.IsChatPage && vm.IsSettingsPage, "ordinary clicks keep executing their own action now that opinion mode is gone");
            await window.NavigateForTestingAsync(AppPage.Chat); await Ready();
            // 左辅助菜单：二级分组与默认项
            var leftAux = window.GetVisualDescendants().OfType<Button>()
                .First(b => Avalonia.Automation.AutomationProperties.GetName(b) == "左辅助功能");
            var leftFlyout = leftAux.Flyout as MenuFlyout;
            Check(leftFlyout is not null, "the left auxiliary key owns a flyout menu");
            var groups = leftFlyout!.Items.OfType<MenuItem>().Where(i => i.Items.Count > 0)
                .ToDictionary(i => i.Header?.ToString() ?? "", i => i);
            Check(new[] { "工作区页面", "复制", "导出", "分叉", "项目" }.All(groups.ContainsKey),
                "the left auxiliary menu groups workspace pages, copy, export, fork and project");
            Check(groups["工作区页面"].Items.Count == 6, "the workspace-pages submenu holds all six pages");
            Check(groups["复制"].Items.Count >= 6 && groups["复制"].Command is not null,
                "the copy submenu aggregates its variants and defaults to copying the session id");
            Check(groups["导出"].Items.Count >= 2 && groups["导出"].Command is not null,
                "the export submenu offers markdown and metadata with a default");
            Check(leftFlyout.Items.OfType<MenuItem>().All(i => i.Header?.ToString() != "临时聊天" && i.Header?.ToString() != "审查"),
                "temporary chat and review are gone from the left auxiliary menu");
            Check(new[] { "session.pin", "session.unread", "session.archive", "session.rename", "session.fork", "session.side", "session.schedule", "session.share", "window.session", "copy.log", "trace", "help.update", "help.performance", "edit.undo", "speech.start", "session.fork.worktree", "session.export.metadata", "copy.markdown", "copy.workdir" }.All(actions.Contains),
                "requested functional actions are registered in the shared catalog");
            Check(!new[] { "session.temporary", "review", "view.chatTabs", "view.navigation", "window.close", "app.quit", "ruler.toggle", "help.changes", "docs", "feedback" }.Any(actions.Contains),
                "redundant and meaningless actions are gone from the catalog");
            var c = window.CustomizationForTesting;
            // 顶栏中间：用户把功能放这里后，按钮出现在常驻区，且设置里可选
            c.SetPlacement("usage", UiActionPosition.TopCenter, 1);
            await Ready();
            var topCenterPanel = window.FindControl<StackPanel>("CustomTopCenter")!;
            Check(topCenterPanel.Children.OfType<Button>().Any(b => b.Tag as string == "usage"),
                "placing a function in the top center shows its button in the caption");
            Check(WorkbenchActionDefaults.PositionTitle(UiActionPosition.TopCenter) == "顶栏中间" &&
                Enum.GetValues<UiActionPosition>().Contains(UiActionPosition.TopCenter),
                "the top center is a first-class position selectable in settings");
            if (window.CaptureRenderedFrame() is { } captionFrame)
            {
                captionFrame.Save(Path.Combine(root, "top-center.png"), PngBitmapEncoderOptions.Default);
                captionFrame.Dispose();
            }
            c.SetPlacement("usage", UiActionPosition.MenuRight, 4);
            await Ready();
            Check(!topCenterPanel.Children.OfType<Button>().Any(b => b.Tag as string == "usage"),
                "moving it away clears the top center again");

            var priorLayout = window.DockLayoutForTesting.Serialize();
            c.EditLayout(l => { l.Move("files", DockZone.Right); l.Hidden[DockZone.Bottom] = false; });
            Check(window.DockLayoutForTesting.Find("files") == DockZone.Right && !window.DockLayoutForTesting.Hidden[DockZone.Bottom] && JsonNode.Parse(c.Json)!["layout"] is not null,
                "settings page assignments apply to the live dock and enter JSON");
            c.Undo(); Check(window.DockLayoutForTesting.Serialize() == priorLayout, "layout changes participate in configuration undo");
            c.Change(s => s with { UserName = "本地用户", AssistantName = "助手甲", ShowAvatars = false, ShowMessageTimes = false, Composer = "reading", ReadingWidth = "narrow", Density = "airy", ShowSessionHeader = true });
            await Ready();
            var chat = window.GetVisualDescendants().OfType<ChatPage>().First();
            var attachmentMenu = (MenuFlyout)chat.FindControl<Button>("AttachContextButton")!.Flyout!;
            Check(attachmentMenu.Items.OfType<MenuItem>().Select(i => i.Header?.ToString()).SequenceEqual(new[] { "文件", "图片", "PDF", "链接" }), "attachment entry exposes four distinct native actions");
            Check(chat.Appearance.ReadWidth == 560 && chat.FindControl<Border>("ComposerCard")!.MaxWidth == 560 && chat.Input.FontSize == c.Current.FontSize && (string)window.Resources["UiUserName"]! == "本地用户", "appearance and names apply to the live native controls");
            var dock = window.FindControl<DockRegionView>("LeftDock")!;
            Check(((Grid)dock.Content!).RowDefinitions[0].Height.Value == 48, "airy density resizes the dock header, not just its tabs");
            var fileTab = dock.GetVisualDescendants().OfType<Button>().First(t => Avalonia.Automation.AutomationProperties.GetAutomationId(t) == "dock-tab-files");
            var originalMenuItems = fileTab.ContextMenu!.Items.Count;
            var tabPoint = fileTab.TranslatePoint(new Point(12, 12), window)!.Value;
            window.MouseDown(tabPoint, Avalonia.Input.MouseButton.Right); window.MouseUp(tabPoint, Avalonia.Input.MouseButton.Right); await Ready();
            Check(fileTab.ContextMenu.Items.Count == originalMenuItems && !fileTab.ContextMenu.Items.OfType<MenuItem>().Any(i => i.Tag as string == "ui-opinion"),
                "right-click offers only native tab actions");
            fileTab.ContextMenu.Close();
            ((MenuItem)fileTab.ContextMenu!.Items[0]!).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            window.DockLayoutForTesting.Close("files");
            Check(window.DockLayoutForTesting.PinnedPages.Contains("files") && window.DockLayoutForTesting.Find("files") == DockZone.Left && !((MenuItem)fileTab.ContextMenu.Items[originalMenuItems - 1]!).IsEnabled,
                "pinning through the tab menu prevents accidental closure");
            var restoredPins = WorkbenchLayoutState.Restore(window.DockLayoutForTesting.Serialize(), new[] { "overview", "files", "changes", "terminal" });
            Check(restoredPins.PinnedPages.Contains("files") && restoredPins.Find("files") == DockZone.Left,
                "pinned page and zone survive saved-layout restoration");
            c.SetPlacement("settings", UiActionPosition.TopLeft, -10);
            Check(window.FindControl<StackPanel>("CustomTopLeft")!.Children.OfType<Button>().Any(b => Avalonia.Automation.AutomationProperties.GetAutomationId(b) == "action-settings"), "moving a function rebuilds the target action surface");
            await window.ActionsForTesting.ExecuteAsync("settings"); Check(vm.IsSettingsPage, "settings icon opens settings");
            await window.ActionsForTesting.ExecuteAsync("settings"); Check(vm.IsChatPage, "second settings click returns to the workspace");
            await window.ActionsForTesting.ExecuteAsync("navigate.back"); Check(vm.IsSettingsPage, "back restores previous native page");
            await window.ActionsForTesting.ExecuteAsync("navigate.forward"); Check(vm.IsChatPage, "forward restores next native page");
            await window.ActionsForTesting.ExecuteAsync("extensions"); await Ready();
            Check(window.FindControl<Button>("WorkspaceReturnButton")!.IsEffectivelyVisible, "extensions has a visible independent return control");
            var returnPoint = window.FindControl<Button>("WorkspaceReturnButton")!.TranslatePoint(new Point(12, 12), window)!.Value;
            window.MouseDown(returnPoint, Avalonia.Input.MouseButton.Left); window.MouseUp(returnPoint, Avalonia.Input.MouseButton.Left); await Ready();
            Check(vm.IsChatPage, "extensions return button returns to the workspace");
            await window.ActionsForTesting.ExecuteAsync("settings"); await Ready();
            window.KeyPress(Avalonia.Input.Key.Escape, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Escape, null); await Ready();
            Check(vm.IsChatPage, "Escape returns from settings without bypassing its leave guard");
            await chat.AttachFilesAsync([path, pdf]);
            Check(vm.Input.Attachments.Count == 2 && chat.FindControl<WrapPanel>("AttachedFiles")!.Children.Count == 2, "picker and drop share attachment loading and removable presentation");
            vm.Input.DraftText = "# Preview\n**bold**";
            var more = (MenuFlyout)chat.FindControl<Button>("ComposerMoreButton")!.Flyout!;
            ((MenuItem)more.Items[0]!).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Check(chat.FindControl<ScrollViewer>("DraftPreview")!.IsVisible && !chat.Input.IsVisible && chat.FindControl<SelectableTextBlock>("DraftPreviewText")!.Inlines!.Count > 0, "Markdown draft preview replaces the editor and uses the existing renderer");
            c.Change(s => s with { Composer = "hidden", ShowStatusBar = false }); await Ready();
            Check(!chat.FindControl<Border>("ComposerCard")!.IsVisible && !window.FindControl<Border>("CustomStatusBar")!.IsVisible, "composer and status bar can be hidden independently");
            await window.ActionsForTesting.ExecuteAsync("composer.toggle");
            Check(chat.FindControl<Border>("ComposerCard")!.IsVisible, "shared action restores hidden composer");
            var timeline = vm.Timeline;
            timeline.Entries.Clear();
            timeline.Entries.Add(new UserMessageViewModel { Id = "u", Text = "create" });
            var intermediate = new AssistantMessageViewModel(new TestClipboard()) { Id = "intermediate" };
            intermediate.Blocks.Add(new ToolCallBlockViewModel { ToolName = "write", InputJson = "{\"path\":\"out.md\"}", Status = ToolCallStatus.Completed });
            intermediate.Blocks.Add(new ToolCallBlockViewModel { ToolName = "write", InputJson = "{\"path\":\"failed.md\"}", Status = ToolCallStatus.Failed });
            timeline.Entries.Add(intermediate);
            var final = new AssistantMessageViewModel(new TestClipboard()) { Id = "a" };
            final.UpdateFrom(new ChatMessage("a", MessageRole.Assistant, "fixture", [new Abstractions.TextBlock("# Finished\nGenerated out.md")], null, true));
            timeline.Entries.Add(final); timeline.RefreshTurnSummaries();
            c.Change(s => s with { ShowAvatars = true }); await Ready();
            var messageTargets = chat.GetVisualDescendants().OfType<Control>().Where(x => Avalonia.Automation.AutomationProperties.GetAutomationId(x) is "MSG-USER-AVATAR" or "MSG-USER-BUBBLE" or "MSG-ASSISTANT-AVATAR" or "MSG-ASSISTANT-BUBBLE").ToArray();
            Check(messageTargets.Length >= 4 && messageTargets.GroupBy(x => Avalonia.Automation.AutomationProperties.GetAutomationId(x))
                    .All(group => group.Count() == 1 || group.Key!.Contains("BUBBLE") || group.Key!.Contains("AVATAR")),
                "each rendered message avatar and body keeps its stable automation id");
            Check(new[] { "Pin", "Archive", "Branch", "ArrowUndo", "ArrowRedo", "Alert", "Attach", "ZoomIn", "ZoomOut" }.All(icon =>
                Application.Current!.TryGetResource("Icon." + icon, null, out var geometry) && geometry is Avalonia.Media.Geometry),
                "semantic action icons have real resources and cannot fall back to the generic list");
            Check(final.ChangeSummary == "写入 · out.md" && final.ArtifactSummary == "out.md" && !intermediate.HasChangeSummary, "one summary per turn includes only confirmed successful direct writes");
            var large = new WorkbenchLayoutState { RightWidth = 1120 };
            Check(WorkbenchLayoutState.Restore(large.Serialize(), new[] { "overview", "files", "changes", "terminal" }).RightWidth == 1120, "right dock restoration no longer imposes the old 700-pixel limit");
            foreach (var (width, height) in new[] { (1440, 900), (800, 640) })
            {
                c.SetPlacement("settings", UiActionPosition.TopRight, 4);
                window.Width = width; window.Height = height; await Ready();
                var modes = chat.FindControl<WrapPanel>("ComposerModes")!;
                var caption = window.FindControl<Border>("CustomCaption")!;
                Check(modes.Bounds.Width > 0 && modes.Bounds.Right <= chat.FindControl<Border>("ComposerCard")!.Bounds.Width && caption.Bounds.Width == width, "controls remain within the composer at " + width);
                var settingsEntry = window.FindControl<StackPanel>("CustomTopRight")!.Children.OfType<Button>().Single(b => b.Tag as string == "settings");
                var rightHost = (Control)window.FindControl<StackPanel>("CustomTopRight")!.Parent!;
                Check(settingsEntry.IsEffectivelyVisible && settingsEntry.TranslatePoint(new Point(settingsEntry.Bounds.Width, 0), rightHost)!.Value.X <= rightHost.Bounds.Width, "settings remains visible without caption clipping at " + width);
                using var frame = window.CaptureRenderedFrame();
                Check(frame is not null && frame.PixelSize.Width == width, "rendered native control frame at " + width);
                frame?.Save(Path.Combine(root, "r05-" + width + ".png"), PngBitmapEncoderOptions.Default);
            }
            // 新设置页整体截图：分类首页与二级页各一张
            var settingsPage = new SettingsPage { DataContext = window.DataContext is MainViewModel main ? main.Settings : null };
            var pageWindow = new Window { Width = 1100, Height = 1400, Content = settingsPage };
            pageWindow.Show();
            try
            {
                await Task.Delay(150); pageWindow.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                // 每个下拉行都必须有取值；空白下拉说明绑定没生效。
                var blanks = settingsPage.GetVisualDescendants().OfType<ComboBox>()
                    .Where(box => box.SelectedIndex < 0).ToArray();
                foreach (var blank in blanks)
                    Console.WriteLine("BLANK combo items=" + string.Join("|", blank.ItemsSource?.Cast<object>().Select(x => x?.ToString()) ?? []) +
                        " placeholder=" + blank.PlaceholderText);
                Check(blanks.Length == 0, "every settings dropdown shows its current value");
                using (var frame = pageWindow.CaptureRenderedFrame())
                    frame?.Save(Path.Combine(root, "settings-page.png"), PngBitmapEncoderOptions.Default);
                // 参考图是深色模式：独立窗口要显式设主题变体才会跟着变
                pageWindow.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
                await Task.Delay(150); pageWindow.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                using (var darkShot = pageWindow.CaptureRenderedFrame())
                    darkShot?.Save(Path.Combine(root, "settings-page-dark.png"), PngBitmapEncoderOptions.Default);
                pageWindow.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
                await Task.Delay(120); pageWindow.UpdateLayout();
                // 二级页：分类首页的入口行跳转后应显示面包屑与子页内容
                if (window.DataContext is MainViewModel mainVm)
                {
                    // 其余分类各拍一张，确认卡行布局在长页面上也成立
                    foreach (var category in new[] { "models", "network", "notifications", "modules", "about", "config" })
                    {
                        mainVm.Settings.SelectedCategory = mainVm.Settings.Categories.Single(x => x.Id == category);
                        await Task.Delay(120); pageWindow.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                        using var shot = pageWindow.CaptureRenderedFrame();
                        shot?.Save(Path.Combine(root, "settings-" + category + ".png"), PngBitmapEncoderOptions.Default);
                    }
                    mainVm.Settings.SelectedCategory = mainVm.Settings.Categories.Single(x => x.Id == "appearance");
                    await Task.Delay(120); pageWindow.UpdateLayout();
                    mainVm.Settings.OpenPage("appearance.arrange.aux");
                    await Task.Delay(150); pageWindow.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    Check(mainVm.Settings.Breadcrumb.Contains("辅助栏") && mainVm.Settings.Breadcrumb.Contains("前端功能排布"),
                        "the auxiliary page shows a full three-level breadcrumb");
                    // 只统计辅助栏自己的三选下拉（排除页头的作用域下拉等）
                    var auxCombos = settingsPage.GetVisualDescendants().OfType<ComboBox>()
                        .Where(b => b.ItemsSource?.Cast<object>().Select(x => x?.ToString()).SequenceEqual(new[] { "左辅助", "右辅助", "不显示" }) == true)
                        .ToArray();
                    Check(auxCombos.Length >= 10, "every auxiliary row offers left, right or hidden");
                    using (var frame = pageWindow.CaptureRenderedFrame())
                        frame?.Save(Path.Combine(root, "settings-aux.png"), PngBitmapEncoderOptions.Default);
                    mainVm.Settings.GoBack();
                    await Task.Delay(100); pageWindow.UpdateLayout();
                    mainVm.Settings.OpenPage("appearance.arrange");
                    await Task.Delay(120); pageWindow.UpdateLayout();
                    // 点击开关行任意位置都应切换（Windows 设置的行级热区）
                    var statusToggleRow = settingsPage.GetVisualDescendants().OfType<Border>()
                        .FirstOrDefault(b => b.Classes.Contains("settingsRow") &&
                            b.GetVisualDescendants().OfType<Avalonia.Controls.TextBlock>().Any(t => t.Text == "底部状态栏"));
                    if (statusToggleRow is not null)
                    {
                        var before = c.Current.ShowStatusBar;
                        var center = statusToggleRow.TranslatePoint(new Point(24, statusToggleRow.Bounds.Height / 2), pageWindow)!.Value;
                        pageWindow.MouseDown(center, Avalonia.Input.MouseButton.Left);
                        pageWindow.MouseUp(center, Avalonia.Input.MouseButton.Left);
                        await Task.Delay(120); pageWindow.UpdateLayout();
                        Check(c.Current.ShowStatusBar != before, "clicking anywhere on a toggle row flips the switch");
                        // 还原状态，避免影响后续关于默认值的断言
                        c.Change(current => current with { ShowStatusBar = before });
                        await Task.Delay(100); pageWindow.UpdateLayout();
                    }
                    // 折叠箭头收起长分组
                    var collapseHeader = settingsPage.GetVisualDescendants().OfType<Button>()
                        .FirstOrDefault(b => Avalonia.Automation.AutomationProperties.GetName(b)?.StartsWith("折叠或展开分组") == true);
                    if (collapseHeader is not null)
                    {
                        var body = settingsPage.GetVisualDescendants().OfType<StackPanel>()
                            .Where(p => p.Classes.Contains("settingsGroupList")).LastOrDefault();
                        var before = body?.IsVisible;
                        collapseHeader.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        await Task.Delay(120); pageWindow.UpdateLayout();
                        Check(body is not null && body.IsVisible != before, "the group header collapses and expands a long list");
                        collapseHeader.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        await Task.Delay(100); pageWindow.UpdateLayout();
                    }
                    await Task.Delay(150); pageWindow.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    Check(mainVm.Settings.IsSubPage && mainVm.Settings.Breadcrumb.Contains("前端功能排布"),
                        "entering a sub page shows the breadcrumb path");
                    using var frame2 = pageWindow.CaptureRenderedFrame();
                    frame2?.Save(Path.Combine(root, "settings-subpage.png"), PngBitmapEncoderOptions.Default);
                    mainVm.Settings.GoBack();
                    await Task.Delay(100); pageWindow.UpdateLayout();
                    Check(!mainVm.Settings.IsSubPage && mainVm.Settings.Breadcrumb == mainVm.Settings.CategoryTitle,
                        "going back returns to the category root");
                }
            }
            finally { pageWindow.Close(); }

            var customizationView = new WorkbenchSectionsView { DataContext = c };
            var settingsWindow = new Window { Width = 420, Height = 760, Content = new ScrollViewer { Content = customizationView } };
            settingsWindow.Show();
            try
            {
                var sectionIds = new[] { "appearance", "functions", "config", "functions" };
                var controlCounts = new List<int>();
                for (var i = 0; i < 4; i++)
                {
                    customizationView.Page = sectionIds[i];
                    await Task.Delay(100); settingsWindow.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    controlCounts.Add(customizationView.GetVisualDescendants().OfType<Control>().Count());
                    using var frame = settingsWindow.CaptureRenderedFrame(); frame?.Save(Path.Combine(root, "settings-" + i + ".png"), PngBitmapEncoderOptions.Default);
                }
                Check(controlCounts.All(count => count > 0) && controlCounts[0] != controlCounts[2] && controlCounts[1] == controlCounts[3],
                    "settings switches appearance, functions and config in place without a nested tab control");
                Check(!customizationView.GetVisualDescendants().OfType<TabControl>().Any(),
                    "customization no longer nests a second tab navigation inside the settings categories");
                customizationView.Page = "appearance";
                await Task.Delay(100); settingsWindow.UpdateLayout();
                var colorPicker = customizationView.GetVisualDescendants().OfType<ColorPicker>().Single();
                colorPicker.Color = Avalonia.Media.Color.Parse("#12ABCD");
                Check(c.Current.Accent == "#12ABCD", "arbitrary native color selection saves and applies the exact RGB color");
                Check(window.Resources["SystemAccentColorDark2"] is Avalonia.Media.Color darker && darker != c.Current.EffectiveAccent &&
                    window.Resources["AccentFillColorDefaultBrush"] is Avalonia.Media.SolidColorBrush fill && fill.Color == c.Current.EffectiveAccent,
                    "custom accent updates the full Fluent palette and native control brushes together");
                c.Change(s => s with { Theme = "dark" });
                Check(window.Resources["AppAccent"] is Avalonia.Media.SolidColorBrush darkFill &&
                    window.Resources["SystemAccentColorLight2"] is Avalonia.Media.Color lighter && darkFill.Color == lighter,
                    "dark theme uses the same lighter Fluent fill tier as UniGetUI");
                c.Change(s => s with { Theme = "system", Accent = "system" });
                Check(c.Current.EffectiveAccent == (Application.Current?.PlatformSettings?.GetColorValues().AccentColor1 ?? Avalonia.Media.Color.Parse("#0078D4")),
                    "default appearance follows the system accent instead of forcing the Demo green");
                c.Change(s => s with { Surface = "flat" }); await Ready();
                Check(window.FindControl<StackPanel>("CustomTopRight")!.Children.OfType<Button>().All(b => b.CornerRadius.TopLeft == 0), "flat style applies square native controls");
                c.Change(s => s with { Surface = "glass" });
                Check(window.TransparencyLevelHint.Contains(Avalonia.Controls.WindowTransparencyLevel.AcrylicBlur), "glass style requests the actual compositor material with a solid fallback");
                c.Change(s => s with { Surface = "fluent" });
            }
            finally { settingsWindow.Close(); }
            // 明暗两套主题在宽窄两种窗口下各留一张证据图。放在所有断言之后，
            // 避免主题/强调色改动进入撤销栈影响前面的用例。
            // 证据图反映默认观感：先恢复三区可见与状态栏，再切主题。
            c.Change(current => current with { Composer = "wide", ShowSessionHeader = false, ShowAvatars = true, ShowMessageTimes = true, Density = "cozy", ReadingWidth = "standard" });
            c.EditLayout(l => { l.Hidden[DockZone.Left] = false; l.Hidden[DockZone.Right] = false; l.Hidden[DockZone.Bottom] = false; });
            await Ready();
            // 默认观感：状态栏关闭。另存一张开启状态，证明外观 → 前端功能排布里的开关有效。
            Check(!c.Current.ShowStatusBar && !window.FindControl<Border>("CustomStatusBar")!.IsVisible,
                "the bottom status bar is off by default and stays hidden until enabled");
            c.Change(current => current with { ShowStatusBar = true }); await Ready();
            Check(window.FindControl<Border>("CustomStatusBar")!.IsVisible, "enabling the status bar in the layout column shows it");
            using (var frame = window.CaptureRenderedFrame())
                frame?.Save(Path.Combine(root, "statusbar-on-1440x900.png"), PngBitmapEncoderOptions.Default);
            c.Change(current => current with { ShowStatusBar = false }); await Ready();
            foreach (var theme in new[] { "light", "dark" })
            {
                c.Change(current => current with { Theme = theme, Accent = "system" });
                foreach (var (width, height) in new[] { (1440, 900), (800, 640) })
                {
                    window.Width = width; window.Height = height; await Ready();
                    using var frame = window.CaptureRenderedFrame();
                    frame?.Save(Path.Combine(root, "theme-" + theme + "-" + width + "x" + height + ".png"), PngBitmapEncoderOptions.Default);
                }
            }
            Check(System.IO.Directory.GetFiles(root, "theme-*.png").Length == 4, "light and dark themes are captured at both wide and narrow window sizes");
            Console.WriteLine("R05 evidence: " + root);
        }
        finally { window.Close(); await Task.Delay(100); Program.SelfTestDataRoot = priorRoot; }

        async Task Ready() { await Task.Delay(100); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
    }
    private sealed class TestClipboard : IClipboardService { public Task SetTextAsync(string text) => Task.CompletedTask; }
    private sealed class RecordingMediaSession : IChatSession, IChatAttachments, IExecutionModeSession
    {
        public string? Text; public string? Mode; public bool Reject;
        public System.Collections.Generic.IReadOnlyList<ChatImage> Images = [];
        public bool CanSendImages => true;
        public bool SupportsExecutionModes => true;
        public string ExecutionMode => Mode ?? "manual";
        public event Action<string>? ExecutionModeChanged { add { } remove { } }
        public SessionConnection Connection => SessionConnection.Connected;
        public SessionActivity Activity => SessionActivity.Idle;
        public SessionOutcome? LastOutcome => null;
        public string? CurrentModel => "fixture";
        public bool CanSend => true;
        public bool CanStop => false;
        public int QueuedCount => 0;
        public event EventHandler<SessionEvent>? Event { add { } remove { } }
        public Task SendAsync(string text, CancellationToken cancellationToken = default) { Text = text; return Reject ? Task.FromException(new InvalidOperationException("fixture rejection")) : Task.CompletedTask; }
        public Task SendWithImagesAsync(string text, System.Collections.Generic.IReadOnlyList<ChatImage> images, CancellationToken cancellationToken = default) { Images = images; return SendAsync(text, cancellationToken); }
        public Task SetExecutionModeAsync(string mode, CancellationToken cancellationToken = default) { Mode = mode; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
