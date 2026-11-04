using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Fufucode.App.Infrastructure.Browser;
using Fufucode.App.Infrastructure;
using Fufucode.App.Views;

namespace Fufucode.App.SelfTest;

/// <summary>真实原生侧栏工装：隔离数据、WebView2、原生 PopupRoot 与窗口局部截图。</summary>
internal static class NativeBrowserSidebarSelfTest
{
    public static async Task<int> RunAsync(MainWindow main)
    {
        var failures = 0;
        void Check(bool value, string name) { Console.WriteLine((value ? "PASS: " : "FAIL: ") + name); if (!value) failures++; }
        var root = Program.SelfTestDataRoot!;
        var evidence = Path.Combine(root, "evidence");
        Directory.CreateDirectory(evidence);
        Console.WriteLine("Sidebar evidence: " + evidence);
        main.Hide();
        using var browser = new BrowserView(new BrowserBookmarkStore(root), new BrowserHistoryStore(root), new BrowserDownloadHistory(root));
        var window = new Window { Title = "Browser sidebar review", Width = 320, Height = 720, Content = browser, Topmost = true };
        try
        {
            window.RequestedThemeVariant = ThemeVariant.Dark;
            window.Show();
            await Task.Delay(500);
            Check(!browser.HostForTesting.HasController && browser.ViewModel.IsPageEmpty, "Empty tab does not allocate a WebView2 controller");
            Check(Reachable(browser, "浏览器地址栏") && Reachable(browser, "浏览器更多操作"), "320 DIP toolbar fits inside the native window");
            Capture(window, Path.Combine(evidence, "dark-320-empty.png"));
            window.RequestedThemeVariant = ThemeVariant.Light;
            window.Width = 480;
            await Task.Delay(300);
            Capture(window, Path.Combine(evidence, "light-480-empty.png"));
            var file = Path.Combine(root, "sidebar-fixture.html");
            File.WriteAllText(file, """
                <!doctype html><meta charset="utf-8"><title>Sidebar native fixture</title>
                <style>body{margin:0;font:16px system-ui;background:#f5f6f7;color:#242629}header{padding:24px;background:white}main{padding:24px}.same{padding:16px;margin:12px 0;background:white;border:1px solid #ddd}#target{margin-top:720px}</style>
                <header><h2>页面预览</h2><p>Native WebView2 acceptance fixture</p></header>
                <main><input aria-label="测试输入" placeholder="输入内容"><button id="submit">确认</button>
                <div class="same">First card</div><div class="same" id="target">Annotation target</div><div style="height:900px">End</div></main>
                """);
            browser.ViewModel.AddressText = file;
            browser.ViewModel.NavigateCommand.Execute(null);
            var host = browser.HostForTesting;
            await Until(() => host.IsReady && browser.ViewModel.DocumentTitle == "Sidebar native fixture" && !browser.ViewModel.IsLoading);
            Check(host.IsReady && host.IsControllerVisibleForTesting, "Real WebView2 document renders in the sidebar");
            window.RequestedThemeVariant = ThemeVariant.Dark;
            window.Width = 900;
            await Task.Delay(350);
            Capture(window, Path.Combine(evidence, "dark-900-page.png"));
            browser.ViewModel.ToggleHistoryCommand.Execute(null);
            await Task.Delay(250);
            var history = browser.FindControl<Border>("HistorySurface")!;
            Check(TopLevel.GetTopLevel(history) is PopupRoot rootWindow && rootWindow.TryGetPlatformHandle()?.Handle != window.TryGetPlatformHandle()?.Handle,
                "History uses a distinct native popup HWND over WebView2");
            Check(browser.FindControl<TextBox>("HistorySearch")?.IsFocused == true, "Opening history focuses its search field");
            Capture(window, Path.Combine(evidence, "dark-900-history.png"));
            browser.ViewModel.IsHistoryOpen = false;
            browser.ViewModel.ApplyDownloadProgress(new BrowserDownloadProgress("fixture-progress", "progress-fixture.bin", "", 4096, 0, BrowserDownloadState.InProgress, null));
            await Task.Delay(250);
            Check(TopLevel.GetTopLevel(browser.FindControl<Border>("DownloadsSurface")) is PopupRoot, "Downloads also use a native popup");
            Capture(window, Path.Combine(evidence, "dark-900-download-fixture.png"));
            browser.ViewModel.IsDownloadsOpen = false;
            var more = browser.FindControl<Button>("MoreButton")!;
            more.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(250);
            Check(more.ContextMenu?.Items.OfType<MenuItem>().Any(item => item.Header?.ToString() == "打开文件") == true,
                "More menu exposes the real file picker entry");
            var menuOrigin = more.ContextMenu!.PointToScreen(new Point());
            var clientOrigin = window.PointToScreen(new Point());
            Check(menuOrigin.X >= clientOrigin.X && menuOrigin.Y >= clientOrigin.Y,
                "More menu anchors below the toolbar inside the browser window");
            Capture(window, Path.Combine(evidence, "dark-900-menu.png"));
            more.ContextMenu?.Close();
            await host.ExecuteScriptAsync("scrollTo(0,720)");
            await Task.Delay(150);
            var script = BrowserView.BuildAnnotationScript("native-probe", "#0078D4");
            Check(await host.ExecuteScriptAsync(script) == "\"annotation-started\"", "Production annotation script starts on the real page");
            var pointJson = await host.ExecuteScriptAsync("(()=>{const r=document.getElementById('target').getBoundingClientRect();return {x:r.x+20,y:r.y+20}})()");
            using var point = JsonDocument.Parse(pointJson!);
            var x = point.RootElement.GetProperty("x").GetDouble();
            var y = point.RootElement.GetProperty("y").GetDouble();
            await Mouse(host, "mousePressed", x, y);
            await Mouse(host, "mouseReleased", x, y);
            using var selection = JsonDocument.Parse((await host.ExecuteScriptAsync("window.__fufuAnnotation.result"))!);
            var selector = selection.RootElement.GetProperty("selector").GetString();
            Check(selector == "#target", "Annotation ignores its overlay and selects the underlying element");
            Check(selection.RootElement.GetProperty("y").GetDouble() > 720, "Annotation clip includes the document scroll offset");
            Check(await host.ExecuteScriptAsync("document.querySelectorAll(" + JsonSerializer.Serialize(selector) + ").length") == "1",
                "Adjust target selector resolves to exactly one element");
            var clip = selection.RootElement;
            var captured = await BrowserView.CaptureAnnotationAsync(host, clip.GetProperty("x").GetDouble(), clip.GetProperty("y").GetDouble(),
                clip.GetProperty("w").GetDouble(), clip.GetProperty("h").GetDouble(), System.Threading.CancellationToken.None);
            Check(captured.Path is not null && captured.Preview is { PixelSize.Width: > 1, PixelSize.Height: > 1 },
                "Scrolled annotation captures a real nonempty PNG region");
            if (captured.Path is not null) { File.Copy(captured.Path, Path.Combine(evidence, "annotation-clip.png"), true); File.Delete(captured.Path); }
            captured.Preview?.Dispose();
            await host.ExecuteScriptAsync("window.__fufuAnnotation.dispose();delete window.__fufuAnnotation;");
            var annotate = browser.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == "浏览器标注");
            annotate.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(200);
            await Mouse(host, "mousePressed", x, y);
            await Mouse(host, "mouseReleased", x, y);
            var desktop = (Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
            await Until(() => desktop.Windows.Any(w => w.Title == "标注"));
            var annotationDialog = desktop.Windows.Single(w => w.Title == "标注");
            await Task.Delay(250);
            var expander = annotationDialog.GetVisualDescendants().OfType<Expander>().Single();
            expander.IsExpanded = true;
            await Task.Delay(250);
            var adjustFields = expander.GetVisualDescendants().OfType<TextBox>().ToArray();
            adjustFields[0].Text = "24";
            await Until(asyncPredicate: () => host.ExecuteScriptAsync("getComputedStyle(document.getElementById('target')).fontSize"), expected: "\"24px\"");
            Check(await host.ExecuteScriptAsync("getComputedStyle(document.querySelector('.same')).fontSize") == "\"16px\"",
                "Adjust changes only the selected element and keeps sibling styles intact");
            Check(adjustFields.All(field => field.Bounds.Width > 140), "Adjust uses two full-width readable field columns");
            Capture(annotationDialog, Path.Combine(evidence, "dark-adjust-dialog.png"));
            browser.ViewModel.NewTabCommand.Execute(null);
            await Until(() => !annotationDialog.IsVisible);
            Check(!annotationDialog.IsVisible, "Switching tabs cancels the annotation dialog");
            var originalTab = browser.ViewModel.Tabs[0];
            browser.ViewModel.SelectTab(originalTab);
            await Task.Delay(200);
            Check(await host.ExecuteScriptAsync("!!window.__fufuAnnotation") == "false", "Cancelled annotation removes overlay and preview styles");
            window.Width = 320;
            browser.ViewModel.ToggleFindCommand.Execute(null);
            await Task.Delay(250);
            Check(Reachable(browser, "浏览器查找输入"), "Find field remains reachable at 320 DIP");
            Capture(window, Path.Combine(evidence, "dark-320-find.png"));
            browser.ViewModel.CloseFindCommand.Execute(null);
            var tab = browser.ViewModel.ActiveTab;
            tab.PageError = "定向错误态样例";
            browser.RefreshNativeVisibilityForTesting();
            await Task.Delay(250);
            Check(!host.IsControllerVisibleForTesting, "Error surface hides the native HWND");
            Capture(window, Path.Combine(evidence, "dark-320-error-fixture.png"));
            tab.PageError = "";
            browser.RefreshNativeVisibilityForTesting();
            await browser.DiscardTabForTestingAsync(tab);
            await browser.ActivateTabForTestingAsync(tab);
            await Until(() => host.IsReady && !tab.IsRestoring && !tab.IsLoading);
            Check(tab.DocumentTitle == "Sidebar native fixture" && host.IsControllerVisibleForTesting, "Discarded tab restores the real document and visible HWND");
            using var site = NativeBrowserModuleSelfTest.LoopbackSite.Start("CONTROL_INPUT", """
                <input id="entry" value="old" oninput="this.dataset.trusted=event.isTrusted">
                <input id="upload" type="file"><input id="check" type="checkbox">
                <button id="sensitive" onclick="this.dataset.clicked='yes'">确认</button>
                <style>#hover:hover{background:rgb(1,2,3)}</style><div id="hover" style="padding:20px">Hover target</div>
                """);
            browser.ApplyAgentControl(BrowserSettings.Default with { BrowserUseEnabled = true, DefaultBrowsePermission = "allow" }, null, null);
            browser.ViewModel.AddressText = site.Url;
            browser.ViewModel.NavigateCommand.Execute(null);
            await Until(() => browser.ViewModel.DocumentTitle == "CONTROL_INPUT" && !browser.ViewModel.IsLoading);
            var bridge = browser.BridgeForTesting!;
            var typed = await bridge.TypeAsync("#entry", "native-input", true);
            Check(!typed.Contains("error") && await host.ExecuteScriptAsync("document.getElementById('entry').value") == "\"native-input\"" &&
                await host.ExecuteScriptAsync("document.getElementById('entry').dataset.trusted") == "\"true\"",
                "Browser Use text input reaches native trusted input events");
            var keyed = await bridge.KeyAsync("Backspace", "#entry");
            Check(!keyed.Contains("error") && await host.ExecuteScriptAsync("document.getElementById('entry').value") == "\"native-inpu\"",
                "Browser Use Backspace edits the real input value");
            var clicked = await bridge.ClickAsync("#check");
            Check(!clicked.Contains("error") && await host.ExecuteScriptAsync("document.getElementById('check').checked") == "true",
                "Browser Use native click toggles a real checkbox");
            var hovered = await bridge.HoverAsync("#hover");
            Check(!hovered.Contains("error") && await host.ExecuteScriptAsync("document.getElementById('hover').matches(':hover')") == "true",
                "Browser Use hover activates the browser CSS hover state");
            Check((await bridge.TypeAsync("#upload", "blocked", true)).Contains("error"), "Browser Use cannot populate file upload controls");
            var approvals = 0;
            browser.SetAgentApprovalHandler((_, _) => { approvals++; return Task.FromResult(false); });
            Check((await bridge.ClickAsync("#sensitive")).Contains("error") && approvals == 1,
                "Sensitive click requires explicit approval");
            browser.SetAgentApprovalHandler((_, _) => Task.FromResult(true));
            Check(!(await bridge.ClickAsync("#sensitive")).Contains("error") &&
                await host.ExecuteScriptAsync("document.getElementById('sensitive').dataset.clicked") == "\"yes\"",
                "Approved sensitive click reaches the original element");
            Console.WriteLine("Native sidebar failures: " + failures);
            return failures == 0 ? 0 : 1;
        }
        finally { window.Close(); }
    }

    private static Task<string?> Mouse(BrowserViewHost host, string type, double x, double y) =>
        host.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonSerializer.Serialize(new { type, x, y, button = "left", clickCount = 1 }));

    private static bool Reachable(BrowserView browser, string name)
    {
        var control = browser.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => AutomationProperties.GetName(c) == name);
        var point = control?.TranslatePoint(new Point(), browser);
        return control?.IsEffectivelyVisible == true && control.Bounds.Width > 1 && point is { } p && p.X >= 0 && p.X + control.Bounds.Width <= browser.Bounds.Width + 1;
    }

    private static async Task Until(Func<Task<string?>> asyncPredicate, string expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (await asyncPredicate() != expected)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Browser script state did not settle");
            await Task.Delay(100);
        }
    }

    private static async Task Until(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(25);
        while (!predicate()) { if (DateTime.UtcNow > deadline) throw new TimeoutException("Sidebar native state did not settle"); await Task.Delay(100); }
    }

    // 截取本测试窗口客户区；不读取桌面其它区域。包含原生子 HWND 与原生浮窗。
    private static void Capture(Window window, string path)
    {
        var hwnd = window.TryGetPlatformHandle()!.Handle;
        GetClientRect(hwnd, out var rect);
        var origin = new NativePoint();
        ClientToScreen(hwnd, ref origin);
        var width = rect.Right; var height = rect.Bottom;
        var screen = GetDC(IntPtr.Zero);
        var dc = CreateCompatibleDC(screen);
        var bitmap = CreateCompatibleBitmap(screen, width, height);
        var previous = SelectObject(dc, bitmap);
        try
        {
            if (!BitBlt(dc, 0, 0, width, height, screen, origin.X, origin.Y, 0x00CC0020)) throw new InvalidOperationException("Native screenshot failed");
            var info = new BitmapInfo { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 };
            var pixels = new byte[width * height * 4];
            if (GetDIBits(dc, bitmap, 0, (uint)height, pixels, ref info, 0) == 0) throw new InvalidOperationException("Native pixels unavailable");
            using var image = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            using (var buffer = image.Lock()) Marshal.Copy(pixels, 0, buffer.Address, pixels.Length);
            image.Save(path, PngBitmapEncoderOptions.Default);
            Console.WriteLine("SCREENSHOT: " + path);
        }
        finally { SelectObject(dc, previous); DeleteObject(bitmap); DeleteDC(dc); ReleaseDC(IntPtr.Zero, screen); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo { public uint Size; public int Width, Height; public ushort Planes, BitCount; public uint Compression, ImageSize; public int XPels, YPels; public uint Used, Important; }
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd, ref NativePoint point);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint op);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint first, uint lines, byte[] pixels, ref BitmapInfo info, uint usage);
}
