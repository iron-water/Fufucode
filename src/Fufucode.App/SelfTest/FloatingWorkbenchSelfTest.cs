using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Fufucode.App.Infrastructure;

namespace Fufucode.App.SelfTest;

internal static class FloatingWorkbenchSelfTest
{
    internal static async Task RunAsync(Action<string> pass, Action<string> fail)
    {
        void Check(bool value, string name) { if (value) pass("FloatingDock: " + name); else fail("FloatingDock: " + name); }
        var layout = new WorkbenchLayoutState();
        var individual = layout.FloatPage("files")!;
        Check(layout.Find("files") is null && individual.HomeZone == DockZone.Left && individual.HomeIndices["files"] == 1,
            "floating a page removes its docked identity and remembers its original slot");
        Check(ReferenceEquals(layout.FloatPage("files"), individual) && layout.Floating.Count == 1,
            "repeated floating of a single page reopens its existing container");
        individual.Hidden = true; layout.Open("files", DockZone.Bottom);
        Check(!individual.Hidden && individual.Selected == "files" && layout.Find("files") is null,
            "opening a hidden floating page reveals the same container without redocking");
        var restored = WorkbenchLayoutState.Restore(layout.Serialize(), WorkbenchLayoutState.KnownPages);
        Check(restored.FindFloating("files")?.Id == individual.Id && restored.Find("files") is null,
            "serialization keeps the floating owner and eliminates duplicate dock owners");
        layout.DockFloating(individual.Id);
        Check(layout.Pages[DockZone.Left].SequenceEqual(new[] { "overview", "files" }) && layout.Floating.Count == 0,
            "returning an individual page restores its original slot");
        var region = layout.FloatRegion(DockZone.Right)!;
        Check(region.Pages.SequenceEqual(new[] { "changes", "browser" }) && region.Selected == "changes" && layout.Hidden[DockZone.Right],
            "floating the whole auxiliary bar retains all page order and selection");
        layout.Reorder("browser", 0);
        region.Topmost = true; region.X = int.MaxValue; region.Y = int.MinValue;
        var clone = WorkbenchLayoutState.Restore(layout.Serialize(), WorkbenchLayoutState.KnownPages);
        Check(clone.Floating.Single().Pages.SequenceEqual(new[] { "browser", "changes" }) &&
              clone.Floating.Single().Selected == "changes" && clone.Floating.Single().Topmost,
            "floating order, selection, window coordinates and independent topmost survive restore");
        clone.DockFloating(region.Id, DockZone.Bottom);
        Check(clone.Pages[DockZone.Bottom].SequenceEqual(new[] { "terminal", "browser", "changes" }) &&
            clone.Selected[DockZone.Bottom] == "changes", "explicit redocking preserves the current floating order");
        layout.DockFloating(region.Id);
        Check(layout.Pages[DockZone.Right].SequenceEqual(new[] { "browser", "changes" }),
            "returning a reordered whole bar preserves its new order");
        region = layout.FloatRegion(DockZone.Right)!;
        layout.PinnedPages.Add("browser"); layout.Close("browser");
        Check(region.Pages.Contains("browser"), "pinned pages cannot be closed while floating");
        layout.Move("browser", DockZone.Left);
        Check(layout.Find("browser") == DockZone.Left && !region.Pages.Contains("browser"), "moving a floating page leaves exactly one dock owner");
        layout.OpenInFloating("files", region.Id);
        Check(layout.Find("files") is null && region.Pages.Contains("files") && region.Selected == "files",
            "the floating add menu moves the real page into that container");
        var separate = layout.FloatPage("files")!;
        Check(separate.Id != region.Id && !region.Pages.Contains("files"), "extracting a page from a floating bar creates a distinct container");
        layout.Close("files"); Check(layout.FindFloating("files") is null && !layout.Floating.Contains(separate),
            "closing the last unpinned page removes its empty container");
        var old = WorkbenchLayoutState.Restore("{\"Pages\":{\"Left\":[\"overview\"],\"Right\":[],\"Bottom\":[]},\"Selected\":{},\"Hidden\":{}}", WorkbenchLayoutState.KnownPages);
        Check(old.Floating.Count == 0 && old.Pages[DockZone.Left].SequenceEqual(new[] { "overview" }), "old layouts acquire no floating windows");
        var damaged = new WorkbenchLayoutState();
        damaged.Floating.Add(new() { Id = "duplicate-page", Pages = ["files", "unknown"], Width = 1, Height = 1 });
        damaged.Floating.Add(new() { Id = "valid", Pages = ["sidechat"], Width = 90000, Height = -1, Selected = "unknown" });
        damaged.Floating.Add(new() { Id = "valid", Pages = ["terminal"], HomeZone = DockZone.Bottom });
        var repaired = WorkbenchLayoutState.Restore(damaged.Serialize(), WorkbenchLayoutState.KnownPages);
        Check(repaired.Floating.Count == 1 && repaired.Floating[0].Pages.SequenceEqual(new[] { "sidechat" }) &&
            repaired.Floating[0].Width == 2560 && repaired.Floating[0].Height == 200 && repaired.Floating[0].Selected == "sidechat",
            "restore filters unknown/duplicate identities and bounds window dimensions");

        var previousRoot = Program.SelfTestDataRoot;
        Program.SelfTestDataRoot = Path.Combine(Path.GetTempPath(), "fufucode-floating-" + Guid.NewGuid().ToString("N"));
        var window = new MainWindow { Width = 1440, Height = 900 };
        window.Show();
        try
        {
            await Ready();
            window.MoveDockPage("files", DockZone.Left); await Ready();
            var files = window.PageForTesting("files");
            var tab = window.GetVisualDescendants().OfType<Button>().Single(button => AutomationProperties.GetAutomationId(button) == "dock-tab-files");
            tab.ContextMenu!.Items.OfType<MenuItem>().Single(item => item.Tag as string == "dock-float")
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Ready();
            var state = window.DockLayoutForTesting.FindFloating("files")!;
            var floatingWindow = window.FloatingDockWindowForTesting(state.Id)!;
            Check(floatingWindow.IsVisible && ReferenceEquals(TopLevel.GetTopLevel(files), floatingWindow) && ReferenceEquals(files, window.PageForTesting("files")),
                "real menu moves the original control into a second Window");
            floatingWindow.Close(); await Ready();
            Check(!floatingWindow.IsVisible && state.Hidden && ReferenceEquals(TopLevel.GetTopLevel(files), floatingWindow),
                "closing the floating presentation hides the window and parks the original live page");
            window.OpenDockPage("files", DockZone.Right); await Ready();
            Check(floatingWindow.IsVisible && !state.Hidden && window.FloatingDockWindowForTesting(state.Id) == floatingWindow,
                "reopening the page reveals the same floating Window");
            window.DockFloatingRegion(state.Id); await Ready();
            Check(ReferenceEquals(TopLevel.GetTopLevel(files), window) && files.IsEffectivelyVisible &&
                window.DockLayoutForTesting.Find("files") == DockZone.Left && window.FloatingDockWindowForTesting(state.Id) is null,
                "redocking disposes the container while restoring the original control in its original slot");
            window.MoveDockPage("changes", DockZone.Right); window.MoveDockPage("browser", DockZone.Right); await Ready();
            var right = window.DockLayoutForTesting.Pages[DockZone.Right].ToArray();
            var groupId = window.FloatDockRegion(DockZone.Right)!; await Ready();
            var groupWindow = window.FloatingDockWindowForTesting(groupId)!;
            Check(right.All(id => ReferenceEquals(TopLevel.GetTopLevel(window.PageForTesting(id)), groupWindow)) &&
                !window.FindControl<Border>("RightPanelBorder")!.IsVisible,
                "whole-bar floating moves every original page and frees the docked region");
            window.DockFloatingRegion(groupId, DockZone.Bottom); await Ready();
            Check(right.All(id => window.DockLayoutForTesting.Find(id) == DockZone.Bottom) && !groupWindow.IsVisible,
                "a floating whole bar can be redocked into another region");
            window.OpenDockPage("terminal", DockZone.Bottom); await Ready();
            var terminalVm = window.TerminalForTesting.SelectedTab!;
            terminalVm.InputText = "unsent-terminal-draft";
            var terminalId = window.FloatDockPage("terminal")!; await Ready();
            var terminalWindow = window.FloatingDockWindowForTesting(terminalId)!;
            terminalWindow.Close(); await Ready(); window.OpenDockPage("terminal", DockZone.Bottom); await Ready();
            window.DockFloatingRegion(terminalId, DockZone.Right); await Ready();
            Check(ReferenceEquals(window.TerminalForTesting.SelectedTab, terminalVm) && terminalVm.InputText == "unsent-terminal-draft",
                "terminal floating, hide, reopen and redocking retain its exact tab VM and unsent draft");
            var sideChat = window.PageForTesting("sidechat");
            window.OpenDockPage("sidechat", DockZone.Left); await Ready();
            var sideId = window.FloatDockPage("sidechat")!; await Ready();
            window.DockFloatingRegion(sideId); await Ready();
            Check(ReferenceEquals(sideChat, window.PageForTesting("sidechat")), "side conversation host is reused across floating and redocking");
            var review = window.PageForTesting("local-review");
            window.OpenDockPage("local-review", DockZone.Right); await Ready();
            var reviewId = window.FloatDockPage("local-review")!; await Ready();
            window.LocalReviewModuleForTesting.IsEnabled = false; await Ready();
            Check(!window.PageAvailableForTesting("local-review") && review.Parent is null,
                "disabling an optional module detaches it from its floating host before releasing its VM");
            window.LocalReviewModuleForTesting.IsEnabled = true; await Ready();
            Check(!ReferenceEquals(review, window.PageForTesting("local-review")) &&
                ReferenceEquals(TopLevel.GetTopLevel(window.PageForTesting("local-review")), window.FloatingDockWindowForTesting(reviewId)),
                "reenabling a module creates its new lifecycle in the reserved floating slot");
            Check(!window.FindControl<Border>("CustomStatusBar")!.IsVisible, "floating workbench preserves the default-off main status bar");
            var owned = window.DockLayoutForTesting.Floating.Select(item => window.FloatingDockWindowForTesting(item.Id)!).ToArray();
            var closed = new TaskCompletionSource();
            var closingCount = 0;
            window.Closing += (_, _) => closingCount++;
            window.Closed += (_, _) => closed.TrySetResult();
            window.Close();
            await Task.WhenAny(closed.Task, Task.Delay(TimeSpan.FromSeconds(10)));
            await Ready();
            Console.WriteLine($"Floating close diagnostic: ownerVisible={window.IsVisible}, ownerClosingEvents={closingCount}, ownedVisible={string.Join(',', owned.Select(item => item.IsVisible))}");
            Check(!window.IsVisible && owned.All(item => !item.IsVisible), "normal main-window exit closes every owned floating presentation");
            var restarted = new MainWindow { Width = 1440, Height = 900 };
            restarted.Show();
            try
            {
                await Ready();
                var reopened = restarted.DockLayoutForTesting.FindFloating("local-review");
                Check(reopened?.Id == reviewId && restarted.FloatingDockWindowForTesting(reviewId)?.IsVisible == true &&
                    ReferenceEquals(TopLevel.GetTopLevel(restarted.PageForTesting("local-review")), restarted.FloatingDockWindowForTesting(reviewId)),
                    "a new main window restores the persisted floating layout and real page ownership");
            }
            finally { restarted.Close(); }
        }
        finally { window.Close(); Program.SelfTestDataRoot = previousRoot; }
        async Task Ready() { await Task.Delay(80); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
    }
}
