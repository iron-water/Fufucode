using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fufucode.App.Abstractions;
using Fufucode.App.Infrastructure;
using Fufucode.App.Infrastructure.Browser;
using Fufucode.App.Integration;
using Fufucode.App.ViewModels;
using Fufucode.Core;

namespace Fufucode.App.SelfTest;

internal static class WorkbenchDemoSelfTest
{
    public static async Task RunAsync(Action<string> pass, Action<string> fail)
    {
        void Check(bool result, string label) { if (result) pass(label); else fail(label); }
        var toastXml = System.Xml.Linq.XElement.Parse(WindowsToastService.CreateXml("title <&", "body <&", null));
        Check(toastXml.Descendants("text").Select(e => e.Value).SequenceEqual(new[] { "title <&", "body <&" }),
            "Production toast XML escapes notification text without markup injection");
        var linkId = Guid.NewGuid().ToString("D");
        var linkProject = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "project space 中文"));
        var link = SessionDeepLink.Create(linkId, linkProject);
        var linkOptions = AppOptions.Parse([link]);
        Check(linkOptions.SessionId == linkId && linkOptions.ProjectPath == linkProject &&
            linkOptions.PiNodePath is null && linkOptions.PiCliPath is null,
            "Production deep link round-trips Unicode project and session without executable overrides");
        Check(!SessionDeepLink.TryParse("fufucode://session/not-an-id?project=C%3A%5Ctest", out _, out _) &&
            !SessionDeepLink.TryParse(link + "&pi-node=evil", out _, out _) &&
            !SessionDeepLink.TryParse("fufucode://session/" + linkId + "?project=relative", out _, out _),
            "Production deep link rejects malformed IDs, extra options and relative directories");
        var known = WorkbenchLayoutState.KnownPages;
        using (var registry = new Views.WorkbenchPageRegistry())
        {
            registry.RegisterCore("overview", "Overview", "Chat", new Border());
            var released = 0;
            registry.RegisterOptional("browser", "Browser", "Search", "builtin.browser", () => new Border(), _ => released++);
            var protectedCore = false;
            try { registry.SetEnabled("overview", false, _ => { }); }
            catch (InvalidOperationException) { protectedCore = true; }
            Check(protectedCore && registry.ContainsKey("overview"), "Modules: mandatory core page cannot be disabled");
            registry.SetEnabled("browser", false, _ => { });
            registry.SetEnabled("browser", false, _ => { });
            Check(released == 1 && registry.KnownPageIds.Contains("browser") && !registry.ContainsKey("browser"),
                "Modules: disabled page stays reserved and releases exactly once");
            var collisionRejected = false;
            try { registry.RegisterCore("browser", "Replacement", "Chat", new Border()); }
            catch (InvalidOperationException) { collisionRejected = true; }
            Check(collisionRejected, "Modules: disabled page ID cannot be overwritten");
            registry.SetEnabled("browser", true, _ => { });
            Check(registry["browser"].ModuleId == "builtin.browser", "Modules: activation restores a stable module identity");
        }
        var invalidModules = WorkbenchModulePreferences.Restore("{broken", out var invalidDiagnostic);
        Check(invalidModules.BrowserEnabled && invalidDiagnostic is not null, "Modules: malformed preferences degrade without blocking core");
        WorkbenchModulePreferences.Restore("{\"Version\":99}", out var unknownDiagnostic);
        Check(unknownDiagnostic is not null, "Modules: unsupported preference version is diagnosed");
        var state = new WorkbenchLayoutState();
        state.Move("files", DockZone.Bottom);
        state.Close("browser");
        state.RightWidth = 410;
        var restored = WorkbenchLayoutState.Restore(state.Serialize(), known);
        Check(restored.Find("files") == DockZone.Bottom && restored.Find("browser") is null && restored.RightWidth == 410,
            "Layout serialization preserves positions, closed pages and dimensions");
        Check(WorkbenchLayoutState.Restore("{bad json", known).Find("overview") == DockZone.Left,
            "Invalid layout falls back to defaults");
        var empty = new WorkbenchLayoutState();
        empty.Close("overview"); empty.Close("files");
        Check(empty.Hidden[DockZone.Left] && empty.Selected[DockZone.Left] is null, "L05: closing final page hides its region");
        empty.Open("files", DockZone.Left);
        Check(!empty.Hidden[DockZone.Left] && empty.Selected[DockZone.Left] == "files", "L03: opening a closed page restores its region");
        empty.Open("overview", DockZone.Left); empty.Reorder("overview", 0);
        Check(WorkbenchLayoutState.Restore(empty.Serialize(), known).Pages[DockZone.Left].SequenceEqual(new[] { "overview", "files" }), "L09: reordered tabs survive serialization");
        var session = new RecordingSession();
        var input = new InputViewModel(session) { DraftText = "question", ReferenceContext = "selected passage" };
        await input.SendCommand.ExecuteAsync(null);
        Check(session.Prompt!.Contains("selected passage") && input.DraftText == "" && input.ReferenceContext == "",
            "Send includes reference and clears accepted draft");
        session.Reject = true;
        input.DraftText = "retry"; input.ReferenceContext = "retained reference";
        await input.SendCommand.ExecuteAsync(null);
        Check(input.DraftText == "retry" && input.ReferenceContext == "retained reference",
            "Rejected send restores text and reference");
        session.Reject = false;
        input.Steering = session;
        input.DraftText = "steer"; input.ReferenceContext = "steer reference";
        await input.SteerCommand.ExecuteAsync(null);
        Check(session.Prompt!.Contains("steer reference") && input.ReferenceContext == "", "Steer includes reference");
        input.DraftText = "/tour"; input.ReferenceContext = "command reference";
        await input.SendCommand.ExecuteAsync(null);
        Check(input.DraftText == "/tour" && input.ReferenceContext == "command reference",
            "Extension commands cannot accidentally consume reference text as arguments");

        var priorTheme = Application.Current!.RequestedThemeVariant;
        var window = new MainWindow { Width = 1440, Height = 900 };
        window.Show();
        try
        {
            window.FindControl<TransitioningContentControl>("PageHost")!.PageTransition = null;
            var vm = (MainViewModel)window.DataContext!;
            await Ready();
            var shortcuts = window.ShortcutsForTesting;
            Check(shortcuts.Rows.Count == window.ActionsForTesting.Actions.Count(),
                "Shortcuts integration: catalog projects the actual action registry");
            shortcuts.SetBinding("toggle.bottom", "Ctrl+Alt+J");
            Check(!window.RouteShortcutForTesting(Avalonia.Input.Key.J, Avalonia.Input.KeyModifiers.Control),
                "Shortcuts integration: remapping invalidates the old hard-coded gesture");
            var bottomHidden = window.DockLayoutForTesting.Hidden[DockZone.Bottom];
            Check(window.RouteShortcutForTesting(Avalonia.Input.Key.J, Avalonia.Input.KeyModifiers.Control | Avalonia.Input.KeyModifiers.Alt) &&
                window.DockLayoutForTesting.Hidden[DockZone.Bottom] != bottomHidden,
                "Shortcuts integration: remapped gesture executes the real dock action");
            window.RouteShortcutForTesting(Avalonia.Input.Key.J, Avalonia.Input.KeyModifiers.Control | Avalonia.Input.KeyModifiers.Alt);
            shortcuts.SetBinding("toggle.bottom", "");
            Check(!window.RouteShortcutForTesting(Avalonia.Input.Key.J, Avalonia.Input.KeyModifiers.Control) &&
                !window.RouteShortcutForTesting(Avalonia.Input.Key.J, Avalonia.Input.KeyModifiers.Control | Avalonia.Input.KeyModifiers.Alt),
                "Shortcuts integration: disabled action has no hard-coded or default fallback");
            shortcuts.ResetAction("toggle.bottom");
            Check(window.ActionsForTesting.Actions.First(action => action.Id == "toggle.bottom").Shortcut == "Ctrl+J",
                "Shortcuts integration: displayed shortcut shares the effective binding source");
            vm.Settings.SelectedCategory = vm.Settings.Categories.First(category => category.Id == "shortcuts");
            Check(vm.Settings.ShowShortcuts && vm.Settings.ShortcutsContent is Features.Shortcuts.ShortcutsPage,
                "Shortcuts integration: settings category hosts the real management page");
            vm.Settings.SelectedCategory = vm.Settings.Categories.First(category => category.Id == "appearance");
            vm.Settings.SelectedCategory = vm.Settings.Categories.First(category => category.Id == "storage");
            Check(vm.Settings.ShowStorage && vm.Settings.StorageContent is Features.Storage.StoragePage,
                "Storage integration: settings hosts the real management page");
            Check(window.StorageForTesting.Areas.Any(area => area.Id == "library" && area.IsProtected) &&
                window.StorageForTesting.Areas.Any(area => area.Id == "webview" && area.Path == Path.Combine(Program.SelfTestDataRoot!, "webview")),
                "Storage integration: protected library and browser paths share the test data root");
            vm.Settings.SelectedCategory = vm.Settings.Categories.First(category => category.Id == "appearance");
            var library = (Features.Library.LibraryPageView)window.PageForTesting("library");
            var libraryVm = library.ViewModel!;
            window.LibraryModuleForTesting.IsEnabled = false;
            Check(!window.PageAvailableForTesting("library") && libraryVm.IsDisposed,
                "Library integration: disabling module releases the owned view model");
            window.LibraryModuleForTesting.IsEnabled = true;
            Check(window.PageAvailableForTesting("library") &&
                !ReferenceEquals(((Features.Library.LibraryPageView)window.PageForTesting("library")).ViewModel, libraryVm),
                "Library integration: enabling module creates a fresh view model");
            Check(WorkbenchModulePreferences.Restore(new WorkbenchModulePreferences(LibraryEnabled: false).Serialize(), out _).LibraryEnabled == false,
                "Library integration: disabled preference round-trips");
            Check(Application.Current.TryGetResource("Icon.Search", null, out var searchIcon) && searchIcon is Avalonia.Media.Geometry,
                "A07: search icon resource exists");
            var originalTab = window.GetVisualDescendants().OfType<Button>()
                .First(button => Avalonia.Automation.AutomationProperties.GetAutomationId(button) == "dock-tab-overview");
            window.MoveDockPage("files", DockZone.Left); await Ready();
            Check(window.GetVisualDescendants().Contains(originalTab), "L02: selecting a tab retains other tab controls");
            var page = window.PageForTesting("files");
            foreach (var zone in Enum.GetValues<DockZone>())
            {
                window.MoveDockPage("files", zone); await Ready();
                Check(window.DockLayoutForTesting.Find("files") == zone && ReferenceEquals(page, window.PageForTesting("files")) && page.IsEffectivelyVisible,
                    "Moving files retains the live control: " + zone);
            }
            window.MoveDockPage("files", DockZone.Left);
            var browserPage = (Views.BrowserView)window.PageForTesting("browser");
            browserPage.ViewModel.AddressText = "browser address draft";
            window.MoveDockPage("browser", DockZone.Bottom); await Ready();
            var browserIndex = window.DockLayoutForTesting.Pages[DockZone.Bottom].IndexOf("browser");
            var centralDraft = vm.Input.DraftText = "keep module acceptance draft";
            window.BrowserModuleForTesting.IsEnabled = false; await Ready();
            Check(!window.PageAvailableForTesting("browser") && browserPage.HostForTesting.IsDisposedForTesting && TopLevel.GetTopLevel(browserPage) is null,
                "Modules: disabling browser detaches and disposes its host");
            Check(window.ActionsForTesting.GetDisabledReason("browser")?.Contains("已停用") == true &&
                window.GetVisualDescendants().OfType<Views.DockRegionView>().All(dock => dock.Catalog.All(item => item.Id != "browser")),
                "Modules: page menus and command palette agree on disabled browser");
            Check(vm.Input.DraftText == centralDraft && window.DockLayoutForTesting.Find("browser") == DockZone.Bottom,
                "Modules: disabling preserves central draft and dormant placement");
            window.OpenDockPage("browser", DockZone.Left);
            Check(window.DockLayoutForTesting.Find("browser") == DockZone.Bottom, "Modules: disabled page cannot be opened through direct entry");
            window.BrowserModuleForTesting.IsEnabled = true; await Ready();
            var reactivated = (Views.BrowserView)window.PageForTesting("browser");
            Check(!ReferenceEquals(browserPage, reactivated) && !reactivated.HostForTesting.IsDisposedForTesting &&
                reactivated.ViewModel.AddressText == "browser address draft" &&
                window.DockLayoutForTesting.Pages[DockZone.Bottom].IndexOf("browser") == browserIndex && reactivated.IsEffectivelyVisible,
                "Modules: reactivation restores address draft and page placement with a new host");
            reactivated.RestoreState(new BrowserPageState(Version: 99));
            Check(reactivated.ViewModel.StatusText.Contains("版本不受支持") && reactivated.ViewModel.AddressText == "about:blank",
                "Modules: unknown browser state version falls back to empty page");
            window.ResetDockLayout(); await Ready();
            window.OpenDockPage("terminal", DockZone.Bottom); await Ready();
            foreach (var (zone, name) in new[] { (DockZone.Left, "SidebarBorder"), (DockZone.Right, "RightPanelBorder"), (DockZone.Bottom, "TerminalBorder") })
            {
                var border = window.FindControl<Border>(name)!;
                var point = border.TranslatePoint(new Point(border.Bounds.Width / 2, border.Bounds.Height / 2), window)!.Value;
                window.UpdateDockDropTarget(point);
                Check(window.GetVisualDescendants().OfType<Views.DockRegionView>().Single(dock => dock.IsDropTarget).Zone == zone,
                    "L07: drag feedback identifies target " + zone);
                window.DropDockPage("files", point); await Ready();
                Check(window.DockLayoutForTesting.Find("files") == zone && !window.GetVisualDescendants().OfType<Views.DockRegionView>().Any(dock => dock.IsDropTarget),
                    "L07: dropping moves page and clears feedback " + zone);
            }
            window.UpdateDockDropTarget(new Point(-10, -10));
            window.DropDockPage("files", new Point(-10, -10));
            Check(window.DockLayoutForTesting.Find("files") == DockZone.Bottom && !window.GetVisualDescendants().OfType<Views.DockRegionView>().Any(dock => dock.IsDropTarget),
                "L08: invalid drop leaves layout unchanged");
            window.UpdateDockDropTarget(null);
            window.ResetDockLayout(); await Ready();
            Check(window.DockLayoutForTesting.Find("files") == DockZone.Left && window.DockLayoutForTesting.Find("browser") == DockZone.Right && !window.DockLayoutForTesting.Hidden[DockZone.Bottom],
                "L16: reset restores default positions and hidden regions");
            var splitter = window.FindControl<GridSplitter>("RightPanelSplitter")!;
            var priorWidth = window.RightPreferenceForTesting;
            splitter.Focus();
            window.KeyPress(Avalonia.Input.Key.Left, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.ArrowLeft, null);
            window.KeyRelease(Avalonia.Input.Key.Left, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.ArrowLeft, null); await Ready();
            Check(window.RightPreferenceForTesting > priorWidth && Math.Abs(window.FindControl<Border>("RightPanelBorder")!.Bounds.Width - window.RightPreferenceForTesting) < 1,
                "L11: splitter keyboard resize persists without layout rebound");
            var filesTab = window.GetVisualDescendants().OfType<Button>().First(button => Avalonia.Automation.AutomationProperties.GetAutomationId(button) == "dock-tab-files");
            var dragStart = filesTab.TranslatePoint(new Point(20, 15), window)!.Value;
            var rightBorder = window.FindControl<Border>("RightPanelBorder")!;
            var dragEnd = rightBorder.TranslatePoint(new Point(80, 100), window)!.Value;
            window.MouseDown(dragStart, Avalonia.Input.MouseButton.Left);
            window.MouseMove(dragEnd, Avalonia.Input.RawInputModifiers.LeftMouseButton);
            Check(window.GetVisualDescendants().OfType<Views.DockRegionView>().Any(dock => dock.IsDropTarget), "L07: pointer drag activates target feedback");
            window.KeyPress(Avalonia.Input.Key.Escape, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Escape, null);
            window.KeyRelease(Avalonia.Input.Key.Escape, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Escape, null);
            window.MouseUp(dragEnd, Avalonia.Input.MouseButton.Left); await Ready();
            Check(window.DockLayoutForTesting.Find("files") == DockZone.Left && !window.GetVisualDescendants().OfType<Views.DockRegionView>().Any(dock => dock.IsDropTarget),
                "L08: Escape cancels pointer drag without moving page or retaining feedback");
            foreach (var id in known)
            {
                var livePage = window.PageForTesting(id);
                foreach (var zone in Enum.GetValues<DockZone>())
                {
                    window.MoveDockPage(id, zone); await Ready();
                    Check(ReferenceEquals(livePage, window.PageForTesting(id)) && livePage.IsEffectivelyVisible,
                        "Stage 2 page placement: " + id + " " + zone);
                }
            }
            window.ResetDockLayout(); await Ready();
            var terminal = window.TerminalForTesting;
            terminal.Show(); await Ready();
            var tab = terminal.SelectedTab;
            window.MoveDockPage("terminal", DockZone.Right);
            window.MoveDockPage("terminal", DockZone.Bottom); await Ready();
            Check(ReferenceEquals(tab, terminal.SelectedTab) && tab!.IsRunning, "Moving terminal does not restart its session");
            var terminalView = (Views.TerminalDockView)window.PageForTesting("terminal");
            terminalView.ConfirmCloseForTesting = _ => Task.FromResult(false);
            await terminalView.CloseSelectedAsync();
            Check(terminal.Tabs.Contains(tab!) && tab!.IsRunning, "T04: canceling terminal close keeps its session alive");
            await window.NavigateForTestingAsync(AppPage.Settings); await Ready();
            Check(!window.FindControl<Border>("SidebarBorder")!.IsVisible && !window.FindControl<Border>("RightPanelBorder")!.IsVisible && tab!.IsRunning,
                "Settings hides docks without stopping terminal");
            await window.NavigateForTestingAsync(AppPage.Chat); await Ready();
            Check(window.FindControl<Border>("SidebarBorder")!.IsVisible, "Returning from settings restores docks");
            window.OpenDockPage("overview", DockZone.Left);
            window.OpenDockPage("changes", DockZone.Right);
            vm.Input.DraftText = "Review the current workspace";
            foreach (var width in new[] { 1440, 1200, 900, 800 })
            {
                window.Width = width;
                window.Height = width switch { 1440 => 900, 1200 => 800, 900 => 700, _ => 640 };
                foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
                {
                    Application.Current.RequestedThemeVariant = theme;
                    await Ready();
                    var path = Path.GetFullPath($"tmp/workbench-demo/{width}-{theme}.png");
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No rendered frame");
                    frame.Save(path, PngBitmapEncoderOptions.Default);
                    Check(frame.PixelSize.Width == width && window.PageForTesting("overview").Bounds.Width > 100,
                        "Rendered " + width + " " + theme);
                    var host = window.FindControl<TransitioningContentControl>("PageHost")!;
                    Check(host.Bounds.Width >= 400 && host.Bounds.Height >= 340, "A06: central work area remains usable at " + width + " " + theme);
                    if (width < 1100)
                        Check(!window.FindControl<Border>("SidebarBorder")!.IsVisible && !window.FindControl<Border>("RightPanelBorder")!.IsVisible,
                            "L12: narrow layout collapses side regions without changing stored widths");
                }
            }
            window.Width = 1440;
            window.Height = 900;
            Application.Current.RequestedThemeVariant = ThemeVariant.Light;
            foreach (var id in known.Where(id => id != "overview"))
            {
                window.MoveDockPage(id, DockZone.Right); await Ready();
                var control = window.PageForTesting(id);
                Check(control.IsEffectivelyVisible && control.Bounds.Width >= 260 && control.Bounds.Height > 100,
                    "Panel rendered: " + id);
                using var panel = window.CaptureRenderedFrame()!;
                panel.Save(Path.GetFullPath("tmp/workbench-demo/panel-" + id + ".png"), PngBitmapEncoderOptions.Default);
            }
            window.Width = 800; window.Height = 640;
            await Ready();
            var draftBox = window.GetVisualDescendants().OfType<TextBox>().First(box => box.IsEffectivelyVisible && box.Text == vm.Input.DraftText);
            draftBox.Focus();
            window.OpenDockPage("changes", DockZone.Right); await Ready();
            Check(window.IsRightDrawerForTesting && window.FindControl<Border>("RightPanelBorder")!.IsVisible,
                "L13: explicit narrow open displays right drawer");
            window.MoveDockPage("files", DockZone.Left); await Ready();
            Check(window.FindControl<Border>("SidebarBorder")!.IsVisible && !window.FindControl<Border>("RightPanelBorder")!.IsVisible,
                "L13: left and right drawers are mutually exclusive");
            window.KeyPress(Avalonia.Input.Key.Escape, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Escape, null);
            window.KeyRelease(Avalonia.Input.Key.Escape, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Escape, null); await Ready();
            Check(!window.FindControl<Border>("SidebarBorder")!.IsVisible, "L13: Escape closes left drawer");
            Check(ReferenceEquals(window.FocusManager?.GetFocusedElement(), draftBox), "L13: drawer switch and Escape restore composer focus");
            terminalView.ConfirmCloseForTesting = _ => Task.FromResult(true);
            await terminalView.CloseSelectedAsync();
            Check(!terminal.Tabs.Contains(tab!) && !tab!.IsRunning, "T04: accepting terminal close stops only the selected session");
            window.Width = 1440; window.Height = 900;
            window.OpenDockPage("overview", DockZone.Left);
            window.OpenDockPage("changes", DockZone.Right); await Ready();
            vm.Input.ReferenceContext = "Selected text from the workspace";
            await vm.Input.SendCommand.ExecuteAsync(null);
            await Task.Delay(1600); await Ready();
            Check(window.GetVisualDescendants().OfType<Views.MessageActionsView>().Any(),
                "Message actions are present in the rendered conversation");
            using var conversation = window.CaptureRenderedFrame()!;
            conversation.Save(Path.GetFullPath("tmp/workbench-demo/conversation.png"), PngBitmapEncoderOptions.Default);
        }
        finally { window.Close(); Application.Current.RequestedThemeVariant = priorTheme; }

        if (Environment.GetEnvironmentVariable("FUFUCODE_WORKBENCH_TEST_URL") is { } url)
        {
            var root = Path.GetFullPath("tmp/workbench-real-" + Guid.NewGuid().ToString("N"));
            var project = Directory.CreateDirectory(Path.Combine(root, "project")).FullName;
            var data = Path.Combine(root, "data");
            new AppSettingsStore(new AppDataLayout(data)).Update(settings => settings with
            {
                SelectedModel = "workbench-local/test-model",
                Endpoint = new CompatibleEndpoint("workbench-local", url, "test-model", "FUFUCODE_WORKBENCH_TEST_KEY")
            });
            var real = new MainWindow(new AppOptions(AppSessionMode.Real, project, null, null, null, data));
            real.Show();
            try
            {
                var deadline = DateTime.UtcNow.AddSeconds(40);
                while (real.DataContext is not MainViewModel && DateTime.UtcNow < deadline) await Task.Delay(100);
                var vm = real.DataContext as MainViewModel ?? throw new InvalidOperationException("Real session failed to start");
                Check(vm.Session.Connection == SessionConnection.Connected && vm.Session is not Mock.MockChatSession,
                    "Desktop uses a real installed Pi session");
                real.OpenDockPage("terminal", DockZone.Bottom); await Ready();
                var terminalPage = real.PageForTesting("terminal");
                var terminalInput = terminalPage.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "TerminalInput");
                var run = terminalPage.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "运行");
                terminalInput.Text = "set UI_TOKEN=TERMINAL_UI_"; await Ready();
                ClickNative(run); await Ready();
                terminalInput.Text = "echo %UI_TOKEN%OK"; await Ready();
                ClickNative(run);
                deadline = DateTime.UtcNow.AddSeconds(12);
                while (!real.TerminalForTesting.SelectedTab!.Buffer.PlainText.Contains("TERMINAL_UI_OK") && DateTime.UtcNow < deadline) await Task.Delay(100);
                Check(real.TerminalForTesting.SelectedTab!.Buffer.PlainText.Contains("TERMINAL_UI_OK") && real.TerminalForTesting.SelectedTab.IsRunning,
                    "Tabs: native terminal input and Run button execute real cmd output rather than input echo");
                var settled = new TaskCompletionSource();
                vm.Session.Event += (_, e) => { if (e is SessionEvent.TurnSettled) settled.TrySetResult(); };
                vm.Input.DraftText = "Reply with the local acceptance marker";
                vm.Input.ReferenceContext = "workbench reference marker";
                await vm.Input.SendCommand.ExecuteAsync(null);
                await settled.Task.WaitAsync(TimeSpan.FromSeconds(40));
                await Task.Delay(150);
                var answer = vm.Timeline.Entries.OfType<AssistantMessageViewModel>().SelectMany(entry => entry.Blocks)
                    .OfType<TextBlockViewModel>().Any(block => block.Text.Contains("WORKBENCH_REAL_PI_OK"));
                Check(answer && vm.Session.Activity == SessionActivity.Idle,
                    "Real Pi answer reaches desktop timeline and terminal idle state");
                var messageIds = vm.Timeline.Entries.Where(e => e is UserMessageViewModel or AssistantMessageViewModel).Select(e => e.Id).ToArray();
                Check(messageIds.Length >= 2 && messageIds.All(id => id.Length > 0) && messageIds.Distinct().Count() == messageIds.Length,
                    "R05: real streamed message controls retain distinct runtime identities");
                var parentBeforeCancel = real.CurrentSessionId;
                var cancelFork = real.ForkFromMessageAsync(vm.Timeline.Entries.OfType<AssistantMessageViewModel>().Last());
                deadline = DateTime.UtcNow.AddSeconds(10);
                while (!real.OwnedWindows.Any() && DateTime.UtcNow < deadline) await Task.Delay(50);
                var forkDialog = real.OwnedWindows.Single();
                forkDialog.Close(); await cancelFork.WaitAsync(TimeSpan.FromSeconds(10));
                Check(real.CurrentSessionId == parentBeforeCancel && ReferenceEquals(real.DataContext, vm),
                    "R05: title-bar closing the real fork dialog cancels without creating or switching session");
                var temporaryOptions = real.CreateTemporaryOptions();
                real.OpenDockPage("sidechat", DockZone.Right); await Ready();
                var sidePage = real.PageForTesting("sidechat");
                var sideStart = sidePage.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "新建侧边聊天");
                sideStart.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                var sideChat = sidePage.GetVisualDescendants().OfType<Views.ChatPage>().Single();
                deadline = DateTime.UtcNow.AddSeconds(40);
                while (sideChat.DataContext is not MainViewModel && DateTime.UtcNow < deadline) await Task.Delay(100);
                var sideVm = sideChat.DataContext as MainViewModel ?? throw new InvalidOperationException("Side chat failed to start");
                var sideSettled = new TaskCompletionSource();
                sideVm.Session.Event += (_, e) => { if (e is SessionEvent.TurnSettled) sideSettled.TrySetResult(); };
                sideVm.Input.DraftText = "Side chat acceptance marker";
                await sideVm.Input.SendCommand.ExecuteAsync(null);
                await sideSettled.Task.WaitAsync(TimeSpan.FromSeconds(40)); await Ready();
                Check(!ReferenceEquals(sideVm.Session, vm.Session) && sideVm.Session.LastOutcome == SessionOutcome.Completed &&
                    sideVm.Timeline.Entries.OfType<AssistantMessageViewModel>().SelectMany(e => e.Blocks).OfType<TextBlockViewModel>().Any(b => b.Text.Contains("WORKBENCH_REAL_PI_OK")) &&
                    real.CurrentSessionId == parentBeforeCancel && vm.Session.Activity == SessionActivity.Idle,
                    "Tabs: native side-chat entry sends through independent installed Pi and settles without changing primary session");
                sideVm.Sidebar.BeginRenameCommand.Execute(null);
                sideVm.Sidebar.RenameDraft = "renamed side fixture";
                await sideVm.Sidebar.SaveNameCommand.ExecuteAsync(null);
                Check(sideVm.Sidebar.SelectedSession?.Title == "renamed side fixture" && vm.Sidebar.SelectedSession?.Title != "renamed side fixture",
                    "Tabs: side-chat rename updates its real identity without renaming the primary session");
                real.OpenDockPage("overview", DockZone.Left); await Ready();
                var overview = real.PageForTesting("overview");
                ClickNative(overview.GetVisualDescendants().OfType<Button>().Single(b => ReferenceEquals(b.Command, vm.Sidebar.NewSessionCommand)));
                deadline = DateTime.UtcNow.AddSeconds(40);
                while ((real.CurrentSessionId == parentBeforeCancel || ReferenceEquals(real.DataContext, vm)) && DateTime.UtcNow < deadline) await Task.Delay(100);
                Check(real.CurrentSessionId != parentBeforeCancel && real.DataContext is MainViewModel,
                    "Tabs: overview New Session button creates and opens a real independent conversation");
                var next = (MainViewModel)real.DataContext!;
                next.Sidebar.SelectedSession = next.Sidebar.Sessions.Single(s => s.Id == parentBeforeCancel);
                deadline = DateTime.UtcNow.AddSeconds(40);
                while ((real.CurrentSessionId != parentBeforeCancel || ReferenceEquals(real.DataContext, next)) && DateTime.UtcNow < deadline) await Task.Delay(100);
                vm = (MainViewModel)real.DataContext!;
                Check(real.CurrentSessionId == parentBeforeCancel && vm.Timeline.Entries.OfType<UserMessageViewModel>().Any(u => u.Text.Contains("local acceptance marker")),
                    "Tabs: selecting the previous overview row restores its real history");
                void ClickNative(Button button)
                {
                    real.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), real)!.Value;
                    real.MouseDown(point, Avalonia.Input.MouseButton.Left); real.MouseUp(point, Avalonia.Input.MouseButton.Left);
                }
                await using (var temporary = await RealSessionHost.OpenAsync(temporaryOptions, CancellationToken.None))
                {
                    Check(temporary.Current.Session.Id != real.CurrentSessionId && temporary.Manager.Settings.Current.SelectedModel == "workbench-local/test-model",
                        "R05: temporary chat isolates session identity while inheriting model configuration");
                    var temporarySettled = new TaskCompletionSource();
                    temporary.Chat.Event += (_, e) => { if (e is SessionEvent.TurnSettled) temporarySettled.TrySetResult(); };
                    await temporary.Chat.SendAsync("Temporary chat acceptance marker");
                    await temporarySettled.Task.WaitAsync(TimeSpan.FromSeconds(40));
                    Check(temporary.Chat.LastOutcome == SessionOutcome.Completed && vm.Session.Activity == SessionActivity.Idle,
                        "R05: temporary chat can use the inherited provider without changing the primary conversation");
                }
                real.MoveDockPage("files", DockZone.Bottom);
                real.MoveDockPage("browser", DockZone.Left);
                ((Views.BrowserView)real.PageForTesting("browser")).ViewModel.AddressText = "persisted browser draft";
                real.BrowserModuleForTesting.IsEnabled = false;
                real.Close();
                await Task.Delay(500);
                var reopened = new MainWindow(new AppOptions(AppSessionMode.Real, project, null, null, null, data));
                reopened.Show();
                try
                {
                    deadline = DateTime.UtcNow.AddSeconds(40);
                    while (reopened.DataContext is not MainViewModel && DateTime.UtcNow < deadline) await Task.Delay(100);
                    Check(reopened.DataContext is MainViewModel && reopened.DockLayoutForTesting.Find("files") == DockZone.Bottom,
                        "Real project layout survives window reopen");
                    Check(!reopened.PageAvailableForTesting("browser") && !reopened.BrowserModuleForTesting.IsEnabled &&
                        reopened.DockLayoutForTesting.Find("browser") == DockZone.Left,
                        "Modules: real project reopen preserves disabled browser and dormant placement");
                    reopened.BrowserModuleForTesting.IsEnabled = true; await Ready();
                    Check(((Views.BrowserView)reopened.PageForTesting("browser")).ViewModel.AddressText == "persisted browser draft" &&
                        reopened.DockLayoutForTesting.Find("browser") == DockZone.Left,
                        "Modules: enabling after real project reopen restores browser state and original zone");
                }
                finally { reopened.Close(); }
                await Task.Delay(500);
                string childId;
                await using (var forkHost = await RealSessionHost.OpenAsync(new AppOptions(AppSessionMode.Real, project, null, null, null, data), CancellationToken.None))
                {
                    await forkHost.Current.RefreshHistoryAsync();
                    var point = forkHost.Current.History.Entries.First(e => e.OnCurrentBranch && e.Message?.Role == "user");
                    var parentId = forkHost.Current.Session.Id;
                    var fork = await forkHost.ForkAsync(point.Id);
                    childId = fork.Session.Session.Id;
                    var forkState = (await fork.Session.Coordinator.QueryStateAsync()).Data!.Value;
                    Check(childId != parentId && forkState.GetProperty("sessionId").GetString() == childId && fork.ParentFileUnchanged,
                        "R05: real fork uses the Pi child identity and preserves the parent file");
                    var continued = new TaskCompletionSource();
                    forkHost.Chat.Event += (_, e) => { if (e is SessionEvent.TurnSettled) continued.TrySetResult(); };
                    await forkHost.Chat.SendAsync("Fork continuation acceptance marker");
                    await continued.Task.WaitAsync(TimeSpan.FromSeconds(40));
                    Check(forkHost.Chat.Activity == SessionActivity.Idle && forkHost.Chat.LastOutcome == SessionOutcome.Completed,
                        "R05: forked real Pi session accepts a new request and settles successfully");
                }
                var childWindow = new MainWindow(new AppOptions(AppSessionMode.Real, project, childId, null, null, data));
                childWindow.Show();
                try
                {
                    deadline = DateTime.UtcNow.AddSeconds(40);
                    while (childWindow.DataContext is not MainViewModel && DateTime.UtcNow < deadline) await Task.Delay(100);
                    Check(childWindow.CurrentSessionId == childId && childWindow.DataContext is MainViewModel childVm &&
                        childVm.Timeline.Entries.OfType<UserMessageViewModel>().Any(e => e.Text.Contains("Fork continuation acceptance marker")),
                        "R05: reopening the explicit child restores its continued conversation in the desktop");
                }
                finally { childWindow.Close(); }
            }
            finally { real.Close(); }
        }

        async Task Ready()
        {
            await Task.Delay(80);
            await Dispatcher.UIThread.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.Background);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private sealed class RecordingSession : IChatSession, Fufucode.App.Integration.IPromptSteering
    {
        public string? Prompt { get; private set; }
        public bool Reject { get; set; }
        public SessionConnection Connection => SessionConnection.Connected;
        public SessionActivity Activity => SessionActivity.Running;
        public SessionOutcome? LastOutcome => null;
        public string? CurrentModel => "test";
        public bool CanSend => true;
        public bool CanStop => false;
        public bool CanSteer => true;
        public int QueuedCount => 0;
        public event EventHandler<SessionEvent>? Event { add { } remove { } }
        public Task SendAsync(string text, CancellationToken cancellationToken = default)
        {
            Prompt = text;
            return Reject ? Task.FromException(new InvalidOperationException("Rejected fixture")) : Task.CompletedTask;
        }
        public Task SteerAsync(string text, CancellationToken cancellationToken = default) => SendAsync(text, cancellationToken);
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
