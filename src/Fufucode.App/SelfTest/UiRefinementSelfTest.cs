using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fufucode.App.Infrastructure;
using Fufucode.App.ViewModels;
using Fufucode.App.Views;
using Fufucode.Core;

namespace Fufucode.App.SelfTest;

internal static class UiRefinementSelfTest
{
    internal static async Task RunAsync(Action<string> pass, Action<string> fail)
    {
        void Check(bool ok, string message) { if (ok) pass("UI refinement: " + message); else fail("UI refinement: " + message); }
        var root = Path.GetFullPath("tmp/ui-refinement");
        Directory.CreateDirectory(root);
        var folder = Directory.CreateDirectory(Path.Combine(root, "project", "folder")).FullName;
        File.WriteAllText(Path.Combine(folder, "child.txt"), "fixture");
        var window = new MainWindow { Width = 1440, Height = 900 };
        window.Show();
        try
        {
            window.CustomizationForTesting.Change(current => current with { Theme = "dark", Accent = "#26FF8E" });
            await Ready();
            Check(window.TryFindResource("AppAccent", window.ActualThemeVariant, out var accent) &&
                accent is ISolidColorBrush { Color: var color } && color == Color.Parse("#26FF8E"),
                "interaction accent preserves the exact selected RGB without whitening");
            var vm = (MainViewModel)window.DataContext!;
            var startupMethod = typeof(MainWindow).GetMethod("ShowStartup", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            startupMethod.Invoke(window, new object[] { "正在启动会话…", true });
            Check(!window.FindControl<Border>("StartupOverlay")!.IsVisible && window.FindControl<ProgressBar>("NavigationProgress")!.IsVisible,
                "startup progress uses the caption strip without reviving the old centered startup card");
            typeof(MainWindow).GetMethod("HideStartupOverlay", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(window, null);
            await window.ActionsForTesting.ExecuteAsync("commands");
            Check(window.FindControl<Grid>("GlobalOverlay")!.IsVisible, "search action opens the command palette");
            await window.ActionsForTesting.ExecuteAsync("commands");
            Check(!window.FindControl<Grid>("GlobalOverlay")!.IsVisible, "second search action closes rather than reopening the command palette");
            var search = new TextBox { Classes = { "searchInput" }, Width = 240 };
            var searchWindow = new Window { Content = search, Width = 300, Height = 100 };
            searchWindow.Show();
            search.Focus();
            await Ready();
            var searchBorder = search.GetVisualDescendants().OfType<Border>().FirstOrDefault(border => border.Name == "PART_BorderElement");
            Check(searchBorder?.BorderBrush is ISolidColorBrush { Color: var searchColor } && searchColor != Colors.Transparent &&
                searchBorder.BorderThickness.Bottom == 2, "focused search renders its two-DIP accent underline");
            searchWindow.Close();
            var links = new System.Collections.Generic.List<MessageLinkViewModel>();
            var referencedFiles = new System.Collections.Generic.List<MessageFileViewModel>();
            MessageReferenceExtractor.Extract("`https://github.com/ximeiorg/ochwpro`（README） `https://index.ximei.me/models/index.yaml`", links, referencedFiles);
            Check(links.Count == 2 && referencedFiles.Count == 0 && links[0].Url == "https://github.com/ximeiorg/ochwpro" &&
                links[1].Url == "https://index.ximei.me/models/index.yaml",
                "website extraction excludes inline-code delimiters and adjacent Chinese annotations");
            var entryCount = vm.Timeline.Entries.Count;
            vm.Sidebar.SetBackends(new[]
            {
                new SidebarViewModel.NewSessionBackendOption("fumo", "FUMO", true, true),
                new SidebarViewModel.NewSessionBackendOption("fixture", "Fixture", true),
            });
            vm.Sidebar.SelectNewSessionBackendCommand.Execute("fixture");
            Check(vm.Sidebar.DefaultBackendLabel == "Fixture" && vm.Timeline.Entries.Count == entryCount,
                "backend selection changes only the pending backend without creating or resetting a session");
            var commandBlock = new ToolCallBlockViewModel { InputJson = "{\"command\":\"printf fixture\"}", Output = "fixture output" };
            Check(commandBlock.InputDisplay == "printf fixture" && commandBlock.ShowTextOutput,
                "command card keeps command input and returned output as separate visible data");
            foreach (var (extension, source) in new[]
            {
                ("cs", "public class Example { string value = \"fixture\"; }"),
                ("py", "def example():\n    return \"fixture\""),
                ("js", "const example = () => \"fixture\";"),
                ("kt", "fun example(): String = \"fixture\""),
            })
            {
                var syntax = SyntaxHighlighter.Highlight(source, extension, true);
                Check(syntax is not null && syntax.SelectMany(line => line).Any(span => span.Color is not null),
                    "source preview produces colored tokens for " + extension);
            }
            var related = new AssistantMessageViewModel(new RecordingClipboardService());
            for (var index = 0; index < 12; index++) related.Files.Add(new MessageFileViewModel($"src/file{index}.cs"));
            related.ToggleRelatedFiles();
            Check(related.VisibleRelatedFiles.Count() == 3 && related.HasMoreRelatedFiles,
                "related file card initially reveals only three rows");
            related.ToggleMoreRelatedFiles();
            Check(related.VisibleRelatedFiles.Count() == 12, "related file card explicitly expands remaining rows");
            related.ToggleMoreRelatedFiles();
            Check(related.VisibleRelatedFiles.Count() == 3, "related file card collapses remaining rows again");
            var preview = new TurnFileChangeViewModel("fixture.cs", 1, 1);
            preview.AddPreview("{\"old_string\":\"old line\",\"new_string\":\"new line\"}");
            preview.ToggleDiff(true);
            Check(preview.IsExpanded && preview.HasDiff && preview.DiffModel is not null,
                "change preview uses recorded edit content without a journal snapshot");
            preview.ToggleDiff(true);
            Check(!preview.IsExpanded, "second change-row activation collapses the preview");
            var card = window.GetVisualDescendants().OfType<ComposerFocusBorder>().FirstOrDefault(control => control.IsEffectivelyVisible);
            Check(card is not null && Math.Abs(card.BorderThickness.Left * window.RenderScaling - 4) < 0.01,
                "composer uses one four-screen-pixel focus border");
            Check(card?.BorderBrush is ISolidColorBrush { Color: var stroke } && stroke == Colors.White &&
                card.BackgroundSizing == BackgroundSizing.OuterBorderEdge && card.BoxShadow.Count == 0,
                "composer breathes with a single pure-white stroke over one uniform fill");
            var tips = window.GetVisualDescendants().OfType<Control>().Where(control => ToolTip.GetTip(control) is not null).ToArray();
            Check(tips.All(control => !ToolTip.GetServiceEnabled(control)), "hover tooltip service disabled even on existing plugin controls");
            Check(card is not null && !ToolTip.GetServiceEnabled(card), "tooltip service disabled for future controls");
            Check(!window.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ScrollBar>()
                .Any(bar => bar.Name == "TimelineScrollBar"), "chat page has no separate permanent timeline scrollbar");

            window.MoveDockPage("files", DockZone.Left);
            window.MoveDockPage("changes", DockZone.Left);
            await Ready();
            var dock = window.FindControl<DockRegionView>("LeftDock")!;
            var tab = dock.GetVisualDescendants().OfType<Button>().Single(control => AutomationProperties.GetAutomationId(control) == "dock-tab-changes");
            window.MouseMove(tab.TranslatePoint(new Point(15, 15), window)!.Value);
            await Ready();
            Check(tab.IsPointerOver && tab.Classes.Contains("fluentDockTab"), "hover uses the dedicated tab template");
            Check(tab.GetVisualDescendants().OfType<ContentPresenter>().All(presenter => presenter.Name != "PART_ContentPresenter"), "tab has no default button overlay presenter");
            var blankTabPoint = tab.TranslatePoint(new Point(3, tab.Bounds.Height - 4), window)!.Value;
            window.MouseDown(blankTabPoint, MouseButton.Left);
            window.MouseUp(blankTabPoint, MouseButton.Left);
            await Ready();
            Check(window.DockLayoutForTesting.Selected[DockZone.Left] == "changes", "clicking tab padding outside its text selects the page");
            var filesTab = dock.GetVisualDescendants().OfType<Button>().Single(control => AutomationProperties.GetAutomationId(control) == "dock-tab-files");
            var tailPoint = filesTab.TranslatePoint(new Point(filesTab.Bounds.Width - 2, filesTab.Bounds.Height - 4), window)!.Value;
            window.MouseDown(tailPoint, MouseButton.Left); window.MouseUp(tailPoint, MouseButton.Left);
            await Ready();
            Check(window.DockLayoutForTesting.Selected[DockZone.Left] == "files", "tab tail padding around the close button also selects the page");
            Check(dock.GetVisualDescendants().OfType<Border>().Where(border => border.ZIndex == 1 && border.Opacity > 0).All(border => border.CornerRadius == new CornerRadius(0)), "hover preserves square contour");
            using (var frame = window.CaptureRenderedFrame()) frame?.Save(Path.Combine(root, "tabs-hover.png"), PngBitmapEncoderOptions.Default);

            var splitter = window.FindControl<DeadZoneGridSplitter>("LeftPanelSplitter")!;
            Check(splitter.Bounds.Width >= 24, "splitter transparent hit area spans at least 24 pixels");
            var edge = splitter.TranslatePoint(new Point(splitter.Bounds.Width / 2 - 10, 180), window)!.Value;
            window.MouseMove(edge);
            await Ready();
            Check(splitter.IsPointerOver && splitter.Classes.Contains("interactionActive"), "pointer ten pixels from divider activates it");
            var grip = splitter.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "SplitGrip");
            Check(Math.Abs(grip.Bounds.Width * window.RenderScaling - 4) < 0.01, "active divider is four screen pixels wide");
            window.MouseDown(edge, MouseButton.Left);
            window.MouseMove(new Point(edge.X + 40, edge.Y), RawInputModifiers.LeftMouseButton);
            await Ready();
            Check(splitter.Classes.Contains("dragging"), "highlight remains latched while dragging");
            window.MouseUp(new Point(edge.X + 40, edge.Y), MouseButton.Left);
            await Ready();
            Check(!splitter.Classes.Contains("dragging"), "release clears dragging state");

            vm.WorkspacePanel.AttachContext(Path.GetDirectoryName(folder)!, "ui-refinement", _ => Task.FromResult(new ChangeRangeReport("workspace", Path.GetDirectoryName(folder)!, true, [], null)), _ => Task.FromResult(new ChangeRangeReport("session-writes", Path.GetDirectoryName(folder)!, true, [], null)));
            await vm.WorkspacePanel.RefreshFilesAsync();
            var directory = vm.WorkspacePanel.Files.Single(item => item.Name == "folder");
            var section = vm.WorkspacePanel.FileTreeSections.Single(item => ReferenceEquals(item.Root, directory));
            await vm.WorkspacePanel.ToggleDirectoryAsync(directory);
            Check(ReferenceEquals(section, vm.WorkspacePanel.FileTreeSections.Single(item => ReferenceEquals(item.Root, directory))) && section.Rows.Count == 2, "expansion reuses the original folder card");
            await vm.WorkspacePanel.ToggleDirectoryAsync(directory);
            Check(section.Rows.Count == 1, "collapse removes children without rebuilding the card");

            var island = window.GetVisualDescendants().OfType<Border>().Single(control => AutomationProperties.GetAutomationId(control) == "session-island");
            var islandPoint = island.TranslatePoint(new Point(island.Bounds.Width / 2, island.Bounds.Height / 2), window)!.Value;
            window.MouseDown(islandPoint, MouseButton.Left); window.MouseUp(islandPoint, MouseButton.Left);
            await Ready();
            Check(window.FindControl<Panel>("IslandOverlay")!.IsVisible, "island opens from the real pill");
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            await Ready();
            Check(!window.FindControl<Panel>("IslandOverlay")!.IsVisible, "escape dismisses the island");

            window.CloseDockPageForTesting("notifications");
            await window.ActionsForTesting.ExecuteAsync("notifications");
            await Ready();
            Check(window.NotificationsForTesting is not null && window.NotificationCenterVisibleForTesting,
                "notification button opens the notification page");
            await window.ActionsForTesting.ExecuteAsync("notifications");
            await Ready();
            Check(!window.NotificationCenterVisibleForTesting && !window.DockLayoutForTesting.AllPageIds().Contains("notifications"),
                "second notification button click closes that page");

            await window.NavigateForTestingAsync(AppPage.Settings);
            await Ready();
            var rows = window.GetVisualDescendants().OfType<Border>().Where(border => border.Classes.Contains("settingsRow") && border.IsEffectivelyVisible).ToArray();
            Check(rows.Length > 0 && rows.All(row => row.BorderThickness == new Thickness(1)), "settings cards retain a one-pixel outline");
            if (rows.FirstOrDefault() is { } row)
            {
                window.MouseMove(row.TranslatePoint(new Point(20, row.Bounds.Height / 2), window)!.Value);
                await Ready();
                Check(row.BorderBrush is ILinearGradientBrush, "hover reveals the gradient card outline");
            }
            var settingsPage = window.GetVisualDescendants().OfType<SettingsPage>().First(page => page.IsEffectivelyVisible);
            var categories = settingsPage.FindControl<ListBox>("SettingsCategoryList")!;
            var marker = settingsPage.FindControl<Border>("SettingsNavigationMarker")!;
            categories.SelectedItem = categories.Items[1];
            await Ready();
            var firstMarkerY = ((TranslateTransform)marker.RenderTransform!).Y;
            categories.SelectedItem = categories.Items[2];
            await Ready();
            Check(marker.IsVisible && ((TranslateTransform)marker.RenderTransform!).Y != firstMarkerY &&
                !categories.GetVisualDescendants().OfType<Border>().Any(border => border.Classes.Contains("navAccent")),
                "settings navigation moves one shared marker instead of flashing per-row markers");
            categories.SelectedItem = categories.Items[0];
            await Ready();
            var settingsScroll = settingsPage.FindControl<ScrollViewer>("SettingsScroller")!;
            var scrollBar = settingsScroll.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ScrollBar>()
                .First(bar => bar.Orientation == Avalonia.Layout.Orientation.Vertical && bar.IsEffectivelyVisible);
            Check(scrollBar.GetVisualDescendants().OfType<Border>().All(border => border.Name != "SlimScrollTrack"),
                "vertical scrollbars paint no track");
            Check(settingsScroll.Presenter is { } presenter && Grid.GetColumnSpan(presenter) == 1,
                "scrollbar gutter stays outside the text content");
            window.MouseMove(new Point(600, 50));
            await Task.Delay(950); await Ready();
            Check(scrollBar.Opacity == 0, "idle scrollbar is completely hidden");
            settingsScroll.Offset = new Vector(0, 100);
            await Ready();
            Check(scrollBar.Opacity == 1 && settingsScroll.Offset.Y > 0, "scrolling reveals the thumb and moves content");
            await Task.Delay(950); await Ready();
            Check(scrollBar.Opacity == 0, "scrollbar hides again after scrolling stops");
            using (var frame = window.CaptureRenderedFrame()) frame?.Save(Path.Combine(root, "settings-hover.png"), PngBitmapEncoderOptions.Default);

            await window.ActionsForTesting.ExecuteAsync("commands");
            await Ready();
            var palette = window.GetVisualDescendants().OfType<CommandPaletteView>().Single(view => view.IsEffectivelyVisible);
            var close = palette.FindControl<Button>("CloseButton")!;
            Check(close.Width == 46 && close.Height == 32 && close.Background is not null &&
                close.Content is PathIcon { Width: 14, Height: 14 }, "command panel close action has a compact icon inside a filled button");
            Check(palette.FindControl<Button>("ExecuteButton")!.Classes.Contains("accent"), "command panel uses the shared primary action style");
            window.PaletteForTesting.CloseCommand.Execute(null);
        }
        finally { window.Close(); }

        async Task Ready()
        {
            await Task.Delay(120);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }
}
