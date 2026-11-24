using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Fufucode.App.Views;

namespace Fufucode.App.SelfTest;

/// <summary>Read the composited result, not just DOM/controller visibility. Both windows and the HTTP site belong to the fixture.</summary>
internal static class NativeBrowserSurfaceChecks
{
    internal static async Task RunAsync(string root, Action<bool, string> check)
    {
        var backing = new Window { Title = "Browser layer fixture", Width = 540, Height = 500,
            Background = Brushes.Magenta, Topmost = true, WindowStartupLocation = WindowStartupLocation.CenterScreen };
        using var browser = new BrowserView { MinHeight = 600 };
        var footer = new Border { Background = Brushes.Gold };
        var scroll = new ScrollViewer { Content = browser, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden };
        var layout = new Grid { RowDefinitions = new RowDefinitions("*,48") };
        layout.Children.Add(scroll);
        Grid.SetRow(footer, 1);
        layout.Children.Add(footer);
        var window = new Window { Title = "Browser viewport fixture", Content = layout, Width = 480, Height = 430,
            RequestedThemeVariant = ThemeVariant.Dark, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Topmost = true, TransparencyLevelHint = [WindowTransparencyLevel.Mica, WindowTransparencyLevel.None] };
        window.Resources["AppWindowBackground"] = Brushes.MidnightBlue;
        var respond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var site = NativeBrowserModuleSelfTest.LoopbackSite.Start("SURFACE_FIXTURE", "<style>html,body{background:#147858}</style>", respond.Task);
        try
        {
            backing.Show();
            window.Show(backing);
            await Task.Delay(200);
            var box = browser.FindControl<TextBox>("AddressBox")!;
            box.Focus();
            box.Text = site.Url;
            box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            var host = browser.HostForTesting;
            await host.EnsureCoreAsync().WaitAsync(TimeSpan.FromSeconds(25));
            await MatchAsync(host, new Point(65, 60), Colors.MidnightBlue, "First navigation paints an opaque surface before the HTTP response", check);
            check(browser.ViewModel.AddressText == site.Url && browser.ViewModel.IsLoading, "Uncommitted startup about:blank does not replace the typed address");
            respond.TrySetResult();
            await MatchAsync(host, new Point(65, 60), Color.Parse("#147858"), "Typed URL paints the real page over the backing window", check);
            await MatchAsync(footer, new Point(40, 20), Colors.Gold, "Native page cannot paint outside the clipped viewport", check);
            scroll.Offset = new Vector(0, 55);
            await Task.Delay(150);
            await MatchAsync(footer, new Point(40, 20), Colors.Gold, "Scrolled native page still respects the adjacent row", check);
            scroll.Offset = default;
            await MatchAsync(host, new Point(65, 60), Color.Parse("#147858"), "Restoring the viewport keeps the page at its original position", check);
            if (OwnsScreenPoint(window, footer.PointToScreen(new Point(40, 20))))
                NativeBrowserSidebarSelfTest.Capture(window, Path.Combine(root, "evidence", "viewport-page.png"));
            var first = browser.ViewModel.ActiveTab;
            browser.ViewModel.NewTabCommand.Execute(null);
            var empty = browser.ViewModel.ActiveTab;
            await MatchAsync(host, new Point(65, 60), Colors.MidnightBlue, "New tab covers the old native page and backing window", check);
            browser.ViewModel.SelectTab(first);
            await MatchAsync(host, new Point(65, 60), Color.Parse("#147858"), "Returning to the tab restores its composited pixels", check);
            browser.ViewModel.CloseTab(empty);

            // A populated about:blank popup is a real document, not the app's empty-tab surface.
            var popupResult = await host.ExecuteScriptForTestingAsync("(() => { const w=window.open('about:blank','_blank'); if (!w) return false; w.document.body.style.background='#3b66a1'; w.document.title='POPUP_PAINT'; return true; })()");
            var popup = browser.ViewModel.ActiveTab;
            var popupHost = browser.HostForTesting;
            await MatchAsync(popupHost, new Point(65, 60), Color.Parse("#3b66a1"), "Script-populated popup paints its own about:blank document", check);
            check(popupResult == "true" && popup != first && await popupHost.ExecuteScriptForTestingAsync("window.opener !== null") == "true",
                "Popup handoff preserves its opener");
            if (popup != first) await popupHost.ExecuteScriptForTestingAsync("window.close()");
            await MatchAsync(host, new Point(65, 60), Color.Parse("#147858"), "Closing the popup reveals only the original page", check);
            check(popup != first && !browser.ViewModel.Tabs.Contains(popup) && browser.ViewModel.ActiveTab == first,
                "window.close() removes only the popup tab");
        }
        finally { respond.TrySetResult(); window.Close(); backing.Close(); }
    }

    private static async Task MatchAsync(Control control, Point point, Color expected, string label, Action<bool, string> check)
    {
        var deadline = Environment.TickCount64 + 10000;
        Color? last = null;
        var window = (Window)TopLevel.GetTopLevel(control)!;
        do
        {
            await Task.Delay(80);
            var at = control.PointToScreen(point);
            if (!OwnsScreenPoint(window, at)) continue; // Do not sample the user's task switcher/another foreground window.
            var dc = GetDC(IntPtr.Zero);
            uint pixel;
            try { pixel = GetPixel(dc, at.X, at.Y); }
            finally { ReleaseDC(IntPtr.Zero, dc); }
            last = Color.FromRgb((byte)pixel, (byte)(pixel >> 8), (byte)(pixel >> 16));
            if (pixel != uint.MaxValue && Math.Abs(last.Value.R - expected.R) <= 2 && Math.Abs(last.Value.G - expected.G) <= 2 && Math.Abs(last.Value.B - expected.B) <= 2)
            { check(true, label); return; }
        } while (Environment.TickCount64 < deadline);
        check(false, $"{label} (expected {expected}, saw {last?.ToString() ?? "no unobscured fixture pixel"})");
    }

    private static bool OwnsScreenPoint(Window window, PixelPoint point) =>
        GetAncestor(WindowFromPoint(new NativePoint(point.X, point.Y)), 2 /* GA_ROOT */) == window.TryGetPlatformHandle()!.Handle;

    [StructLayout(LayoutKind.Sequential)] private readonly record struct NativePoint(int X, int Y);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern uint GetPixel(IntPtr dc, int x, int y);
}
