using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Fufucode.App.Infrastructure;
using Fufucode.App.Views;

namespace Fufucode.App.SelfTest;

// Runs inside the normal headless SelfTest host (Motion.Reduced), without user data.
internal static class DockInteractionSelfTest
{
    internal static void Run(Action<string> pass, Action<string> fail)
    {
        void Check(bool condition, string name) { if (condition) pass("DockInteraction: " + name); else fail("DockInteraction: " + name); }
        var dock = new DockRegionView { Zone = DockZone.Left };
        dock.Catalog = new[]
        {
            new WorkbenchPage("overview", "管理", "AppsList", new Border(), "core"),
            new WorkbenchPage("files", "文件", "Folder", new Border(), "core")
        };
        var state = new WorkbenchLayoutState();
        state.Pages[DockZone.Left] = ["overview", "files"];
        state.Selected[DockZone.Left] = "overview";
        dock.ApplyAppearance(new WorkbenchAppearance());
        var window = new Window { Content = dock, Width = 400, Height = 300 };
        window.Show();
        try
        {
            dock.Render(state);
            window.UpdateLayout();
            var rows = dock.GetVisualDescendants().OfType<Grid>().Where(grid => grid.Classes.Contains("dockTabRow")).ToArray();
            Check(rows.Length == 2, "both tab rows rendered");
            var strokes = rows.SelectMany(row => row.Children.OfType<Border>()).Where(border => border.ZIndex == 1).ToArray();
            Check(strokes.Length == 2 && strokes.All(stroke => !stroke.IsHitTestVisible && stroke.BorderThickness.Left == 2 && stroke.BorderThickness.Top == 2 && stroke.BorderThickness.Right == 0 && stroke.BorderThickness.Bottom == 0), "contours are independent non-input left/top borders");
            Check(strokes.Count(stroke => stroke.Opacity == 1) == 1, "one selection contour");
            state.Selected[DockZone.Left] = "files";
            dock.Render(state);
            Check(strokes[0].Opacity == 0 && strokes[1].Opacity == 1, "selection transfers to full new row");
            state.Selected[DockZone.Left] = "overview";
            dock.Render(state);
            Check(strokes[0].Opacity == 1 && strokes[1].Opacity == 0, "rapid reselection leaves no stale contour");
            Check(MainWindow.DockSplitterHitSize >= 20 && MainWindow.DockSplitterSize == 8, "hit target expands without widening layout gap");
            dock.Catalog = Enumerable.Range(0, 8).Select(index => new WorkbenchPage("page-" + index, "功能页 " + index,
                "Folder", new Border(), "core")).ToArray();
            state.Pages[DockZone.Left] = dock.Catalog.Select(page => page.Id).ToList();
            state.Selected[DockZone.Left] = "page-0";
            dock.Render(state); window.UpdateLayout(); Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            state.Selected[DockZone.Left] = "page-7";
            dock.Render(state);
            Check(dock.Catalog.Last().Content.IsVisible && !dock.Catalog.First().Content.IsVisible,
                "far-tab content switches immediately without waiting for header scrolling");
            window.UpdateLayout(); Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Check(dock.TabScrollForTesting.Offset.X > 0 && !dock.TabScrollForTesting.BringIntoViewOnFocusChange,
                "selected far tab is revealed without a focus-triggered preliminary jump");
        }
        finally { window.Content = null; window.Close(); }
    }
}
