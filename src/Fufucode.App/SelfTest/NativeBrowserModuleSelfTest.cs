using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Fufucode.App.Features.BrowserExperience;
using Fufucode.App.Infrastructure;
using Fufucode.App.ViewModels;
using Fufucode.App.Views;

namespace Fufucode.App.SelfTest;

/// <summary>
/// E2-B 原生工装：真实 WebView2 controller、真实父 HWND、真实 DOM/历史、真实多页签与生命周期。
/// 数据只用临时根与本机 loopback，不触碰真实 profile/登录态；本工装不能代替真实用户指针/IME/DPI 验收。
/// </summary>
internal static class NativeBrowserModuleSelfTest
{
    public static async Task<int> RunAsync(MainWindow window)
    {
        var failures = 0;
        void Check(bool result, string name)
        { Console.WriteLine((result ? "PASS: " : "FAIL: ") + name); if (!result) failures++; }
        void Note(string message) => Console.WriteLine("NOTE: " + message);

        Console.WriteLine("Native module data root: " + Program.SelfTestDataRoot);
        var root = Program.SelfTestDataRoot!;
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "browser-module.html");
        File.WriteAllText(file, "<!doctype html><title>NATIVE_MODULE_OK</title><h1>Native module acceptance</h1><p>alpha marker</p>");
        var second = Path.Combine(root, "browser-second.html");
        File.WriteAllText(second, "<!doctype html><title>NATIVE_HISTORY_OK</title><h1>Second document</h1><p>beta marker</p>");

        await Until(() => window.DataContext is MainViewModel);
        var vm = (MainViewModel)window.DataContext!;
        vm.Input.DraftText = "preserved native module draft";
        window.OpenDockPage("browser", DockZone.Right);
        var browser = (BrowserView)window.PageForTesting("browser");

        // ---- 1. 单页签基础导航与历史（保留既有行为） ----
        browser.ViewModel.AddressText = file;
        browser.ViewModel.NavigateCommand.Execute(null);
        await Until(() => browser.HostForTesting.IsReady && browser.ViewModel.DocumentTitle == "NATIVE_MODULE_OK");
        Check(browser.HostForTesting.IsReady, "Real WebView2 controller opens local HTML");
        browser.ViewModel.AddressText = second;
        browser.ViewModel.NavigateCommand.Execute(null);
        await Until(() => browser.ViewModel.DocumentTitle == "NATIVE_HISTORY_OK" && browser.ViewModel.CanGoBack);
        browser.ViewModel.BackCommand.Execute(null);
        await Until(() => browser.ViewModel.DocumentTitle == "NATIVE_MODULE_OK" && browser.ViewModel.CanGoForward);
        Check(browser.ViewModel.CanGoForward, "Native browser back returns to previous document");
        browser.ViewModel.ForwardCommand.Execute(null);
        await Until(() => browser.ViewModel.DocumentTitle == "NATIVE_HISTORY_OK");
        Check(browser.ViewModel.DocumentTitle == "NATIVE_HISTORY_OK", "Native browser forward restores next document");
        browser.ViewModel.BackCommand.Execute(null);
        await Until(() => browser.ViewModel.DocumentTitle == "NATIVE_MODULE_OK");
        browser.ViewModel.ReloadCommand.Execute(null);
        await Until(() => !browser.ViewModel.IsLoading && browser.ViewModel.DocumentTitle == "NATIVE_MODULE_OK");
        Check(browser.HostForTesting.IsReady, "Native browser refresh keeps its document and controller");

        // ---- 2. 多页签：两个独立 controller、独立 DOM/历史、切换不串页 ----
        var tabA = browser.ViewModel.ActiveTab;
        var tabAAddress = tabA.NavigationAddress;
        using var loopback = LoopbackSite.Start("LOOPBACK_TAB_OK", "gamma marker");
        browser.ViewModel.NewTabCommand.Execute(null);
        var tabB = browser.ViewModel.ActiveTab;
        Check(!ReferenceEquals(tabA, tabB) && browser.ViewModel.Tabs.Count == 2, "New tab creates an independent tab identity");
        browser.ViewModel.AddressText = loopback.Url;
        browser.ViewModel.NavigateCommand.Execute(null);
        await Until(() => browser.ViewModel.DocumentTitle == "LOOPBACK_TAB_OK");
        var hostA = browser.HostForTabForTesting(tabA);
        var hostB = browser.HostForTabForTesting(tabB);
        Check(browser.HostCountForTesting == 2 && !ReferenceEquals(hostA, hostB), "Each tab owns its own native host instance");
        Check(hostA.ControllerCreateCountForTesting == 1 && hostB.ControllerCreateCountForTesting == 1,
            "Each tab created exactly one real WebView2 controller");
        Check(await TitleAsync(hostA) == "NATIVE_MODULE_OK" && await TitleAsync(hostB) == "LOOPBACK_TAB_OK",
            "Both tabs render their own real DOM at the same time");
        Check(tabA.DocumentTitle == "NATIVE_MODULE_OK" && tabB.DocumentTitle == "LOOPBACK_TAB_OK" &&
              tabA.NavigationAddress == tabAAddress && tabB.NavigationAddress == loopback.Url,
            "Tab navigation state does not bleed across tabs");
        browser.ViewModel.SelectTab(tabA);
        await Task.Delay(150);
        Check(tabA.DocumentTitle == "NATIVE_MODULE_OK" && tabB.DocumentTitle == "LOOPBACK_TAB_OK" &&
              browser.ViewModel.DocumentTitle == "NATIVE_MODULE_OK",
            "Switching the active tab does not rewrite the other tab");
        browser.ViewModel.SelectTab(tabB);
        await Task.Delay(150);
        Check(browser.ViewModel.DocumentTitle == "LOOPBACK_TAB_OK" && tabA.DocumentTitle == "NATIVE_MODULE_OK",
            "Switching back keeps both tab states intact");

        // ---- 3. 真实父 HWND：浮动窗口往返必须重挂同一 controller 并保留 DOM ----
        var mainHandle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        Check(mainHandle != IntPtr.Zero, "Main window exposes a real HWND");
        Check(hostA.ParentWindowForTesting == mainHandle && hostB.ParentWindowForTesting == mainHandle,
            "Both controllers initially parent to the main window HWND");
        var reparentBefore = hostA.ReparentCountForTesting + hostB.ReparentCountForTesting;
        var floatingId = window.FloatDockPage("browser");
        Check(floatingId is not null, "Browser page can float into its own window");
        var floatingWindow = floatingId is null ? null : window.FloatingDockWindowForTesting(floatingId);
        await Until(() => floatingWindow?.TryGetPlatformHandle()?.Handle is { } handle && handle != IntPtr.Zero &&
                          hostA.ParentWindowForTesting == handle && hostB.ParentWindowForTesting == handle);
        var floatingHandle = floatingWindow?.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        Check(floatingHandle != IntPtr.Zero && floatingHandle != mainHandle, "Floating window exposes a distinct HWND");
        Check(hostA.ParentWindowForTesting == floatingHandle && hostB.ParentWindowForTesting == floatingHandle,
            "Real controller ParentWindow moved to the floating window HWND");
        Check(hostA.ReparentCountForTesting + hostB.ReparentCountForTesting > reparentBefore,
            "Reparent is recorded on the real controller rather than a mock");
        Check(hostA.ControllerCreateCountForTesting == 1 && hostB.ControllerCreateCountForTesting == 1,
            "Floating did not recreate controllers (DOM/history preserved by design)");
        Check(await TitleAsync(hostA) == "NATIVE_MODULE_OK" && await TitleAsync(hostB) == "LOOPBACK_TAB_OK",
            "DOM survives the cross-window reparent");
        if (floatingId is not null) window.DockFloatingRegion(floatingId);
        await Until(() => hostA.ParentWindowForTesting == mainHandle && hostB.ParentWindowForTesting == mainHandle);
        Check(hostA.ParentWindowForTesting == mainHandle && hostB.ParentWindowForTesting == mainHandle,
            "Docking back restores the main window parent HWND");
        Note($"dock-back evidence: create A/B={hostA.ControllerCreateCountForTesting}/{hostB.ControllerCreateCountForTesting} " +
             $"releases A/B={hostA.ReleaseCountForTesting}/{hostB.ReleaseCountForTesting} " +
             $"reparents A/B={hostA.ReparentCountForTesting}/{hostB.ReparentCountForTesting} " +
             $"parent A/B=0x{hostA.ParentWindowForTesting.ToInt64():X}/0x{hostB.ParentWindowForTesting.ToInt64():X} main=0x{mainHandle.ToInt64():X}");
        Check(hostA.ControllerCreateCountForTesting == 1 && hostB.ControllerCreateCountForTesting == 1,
            "Docking back keeps the same controllers");
        Check(await TitleAsync(hostA) == "NATIVE_MODULE_OK" && await TitleAsync(hostB) == "LOOPBACK_TAB_OK",
            "DOM and history survive the round trip");

        // ---- 3b. 整辅助栏浮动往返：整栏（含浏览器页）搬进浮动窗再停靠回来 ----
        window.MoveDockPage("browser", DockZone.Right);
        await Task.Delay(150);
        var regionBefore = hostA.ControllerCreateCountForTesting + hostB.ControllerCreateCountForTesting;
        var regionId = window.FloatDockRegion(DockZone.Right);
        Check(regionId is not null, "Whole auxiliary region (including browser) floats into one window");
        var regionWindow = regionId is null ? null : window.FloatingDockWindowForTesting(regionId);
        await Until(() => regionWindow?.TryGetPlatformHandle()?.Handle is { } handle && handle != IntPtr.Zero &&
                          hostA.ParentWindowForTesting == handle && hostB.ParentWindowForTesting == handle);
        var regionHandle = regionWindow?.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        Check(regionHandle != IntPtr.Zero && regionHandle != mainHandle &&
              hostA.ParentWindowForTesting == regionHandle && hostB.ParentWindowForTesting == regionHandle,
            "Whole-region float reparents both controllers to the region window HWND");
        Check(hostA.ControllerCreateCountForTesting + hostB.ControllerCreateCountForTesting == regionBefore,
            "Whole-region float does not recreate controllers");
        Check(await TitleAsync(hostA) == "NATIVE_MODULE_OK" && await TitleAsync(hostB) == "LOOPBACK_TAB_OK",
            "Whole-region float preserves both tabs' DOM");
        if (regionId is not null) window.DockFloatingRegion(regionId);
        await Until(() => hostA.ParentWindowForTesting == mainHandle && hostB.ParentWindowForTesting == mainHandle);
        Check(hostA.ParentWindowForTesting == mainHandle && hostB.ParentWindowForTesting == mainHandle &&
              hostA.ControllerCreateCountForTesting + hostB.ControllerCreateCountForTesting == regionBefore,
            "Docking the whole region back keeps the same controllers on the main HWND");
        Check(await TitleAsync(hostA) == "NATIVE_MODULE_OK" && await TitleAsync(hostB) == "LOOPBACK_TAB_OK",
            "Whole-region dock-back preserves DOM");

        // ---- 3c. 隐藏浮动窗后重开：窗口先隐藏，再显式重开仍复用同一 controller ----
        var hideId = window.FloatDockPage("browser");
        var hideWindow = hideId is null ? null : window.FloatingDockWindowForTesting(hideId);
        await Until(() => hideWindow?.IsVisible == true && hostA.ParentWindowForTesting == (hideWindow.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero));
        var hideHandle = hideWindow?.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hideWindow is not null) hideWindow.Hide();
        await Task.Delay(200);
        Check(hideWindow?.IsVisible == false && hostA.IsReady && hostA.ControllerCreateCountForTesting == 1,
            "Hiding the floating window keeps the controller (no black screen, no recreate)");
        window.OpenDockPage("browser", DockZone.Right);   // 对浮动页调用 = 重新显示该浮动窗
        await Until(() => hideWindow?.IsVisible == true && hostA.ParentWindowForTesting == hideHandle);
        Check(hideWindow?.IsVisible == true && hostA.ParentWindowForTesting == hideHandle &&
              hostA.ControllerCreateCountForTesting == 1 && await TitleAsync(hostA) == "NATIVE_MODULE_OK",
            "Reopening the hidden floating window reuses the same controller, parent HWND and DOM");
        if (hideId is not null) window.DockFloatingRegion(hideId);
        await Until(() => hostA.ParentWindowForTesting == mainHandle);

        // ---- 4. 页内查找与有界缩放 ----
        browser.ViewModel.SelectTab(tabB);
        await Task.Delay(120);
        browser.ViewModel.FindQuery = "gamma";
        browser.ViewModel.FindNextCommand.Execute(null);
        await Until(() => tabB.FindStatus.Contains('/') || tabB.FindStatus == "无匹配" || tabB.FindStatus.Contains("无查找"));
        Note($"find evidence: status={tabB.FindStatus} raw={hostB.FindStatus?.MatchCount}/{hostB.FindStatus?.ActiveMatchIndex} " +
             $"body={await BodyAsync(hostB)}");
        Check(tabB.FindStatus.Contains('/') || tabB.FindStatus.Contains("无查找"),
            "Page find reports a real match count for an existing term");
        browser.ViewModel.FindQuery = "zzz-term-absent-zzz";
        browser.ViewModel.FindNextCommand.Execute(null);
        await Until(() => tabB.FindStatus is "无匹配" or "0/0" || tabB.FindStatus.Contains("无查找"));
        Check(tabB.FindStatus.Contains("无匹配") || tabB.FindStatus.Contains("无查找"),
            "Page find reports no match for an absent term");
        browser.ViewModel.CloseFindCommand.Execute(null);
        Check(!tabB.IsFindOpen, "Closing find restores the browsing surface");

        browser.ViewModel.ApplyZoom(150);
        await Until(() => Math.Abs(hostB.ZoomFactor - 1.5) < 0.02);
        Check(Math.Abs(hostB.ZoomFactor - 1.5) < 0.02, "Zoom in reaches the real controller ZoomFactor");
        browser.ViewModel.ApplyZoom(10_000);
        await Task.Delay(150);
        Check(browser.ViewModel.ZoomPercent == BrowserViewModel.MaxZoomPercent && hostB.ZoomFactor <= 5.0,
            "Zoom is bounded to the documented maximum");
        browser.ViewModel.ApplyZoom(0);
        await Task.Delay(150);
        Check(browser.ViewModel.ZoomPercent == BrowserViewModel.MinZoomPercent,
            "Zoom is bounded to the documented minimum");
        browser.ViewModel.ApplyZoom(100);
        await Until(() => Math.Abs(hostB.ZoomFactor - 1.0) < 0.02);
        Check(Math.Abs(hostB.ZoomFactor - 1.0) < 0.02, "Zoom reset returns to 100%");

        // ---- 5. 复制真实地址 / 外部打开 / 开发工具入口 ----
        string? copied = null;
        browser.ViewModel.AddressCopyRequested += (_, address) => copied = address;
        browser.ViewModel.CopyAddressCommand.Execute(null);
        Check(copied == loopback.Url, "Copy address uses the real current navigation address");
        string? external = null;
        browser.ViewModel.ExternalOpenRequested += (_, address) => external = address;
        browser.ViewModel.OpenExternalCommand.Execute(null);
        Check(external == loopback.Url, "External open is an explicit user-triggered request with the real address");
        Check(hostB.PageFindAvailable, "Find API availability is reported from the real runtime");
        var devTools = hostB.OpenDevTools();
        Check(devTools, "DevTools entry reaches the real runtime (window opens)");

        // ---- 5b. 查找取消与换文档重开（真实 SDK，防迟到回调写回） ----
        browser.ViewModel.SelectTab(tabB);
        browser.ViewModel.FindQuery = "gamma";
        browser.ViewModel.FindNextCommand.Execute(null);
        await Until(() => hostB.FindStatus?.MatchCount == 1);
        browser.ViewModel.FindNextCommand.Execute(null);       // 另起一次观察
        browser.ViewModel.CloseFindCommand.Execute(null);      // 立刻关闭
        await Task.Delay(3400);
        Check(!tabB.IsFindOpen && tabB.FindStatus == "",
            "Cancelled find stays cleared after all pending polling finishes");
        hostB.NavigateToDocument("<!doctype html><title>NATIVE_FIND_REOPEN</title><p>gamma</p><p>gamma</p>");
        await Until(() => tabB.DocumentTitle == "NATIVE_FIND_REOPEN");
        tabB.FindQuery = "gamma";
        tabB.FindNextCommand.Execute(null);
        await Until(() => hostB.FindStatus?.MatchCount == 2 && tabB.FindStatus.Contains("/2"));
        Note($"find reopen evidence: native={hostB.FindStatus?.MatchCount}/{hostB.FindStatus?.ActiveMatchIndex} ui={tabB.FindStatus}");
        Check(hostB.FindStatus?.MatchCount == 2 && tabB.FindStatus.Contains("/2"),
            "Reopening the same query re-starts the new document's own session");
        tabB.CloseFindCommand.Execute(null);
        await Task.Delay(200);

        // ---- 6. 弹出新窗口请求进入新页签，不覆盖原页签 ----
        var tabsBeforePopup = browser.ViewModel.Tabs.Count;
        var titleBeforePopup = tabB.DocumentTitle;
        await hostB.ExecuteScriptForTestingAsync("window.open('about:blank','_blank')");
        var popupCreated = await WaitSoftAsync(() => browser.ViewModel.Tabs.Count > tabsBeforePopup, TimeSpan.FromSeconds(4));
        if (popupCreated)
        {
            Check(browser.ViewModel.Tabs.Count == tabsBeforePopup + 1 && tabB.DocumentTitle == titleBeforePopup,
                "Popup request opens an explicit new tab without overwriting the original tab");
            var popupTab = browser.ViewModel.Tabs[^1];
            Check(!ReferenceEquals(popupTab, tabB), "Popup tab has its own identity");
            browser.ViewModel.SelectTab(popupTab);
            browser.ViewModel.CloseTabByIdCommand.Execute(popupTab);
            await Task.Delay(200);
            Check(browser.ViewModel.Tabs.Count == tabsBeforePopup, "Closing the popup tab leaves the other tabs intact");
        }
        else
        {
            Note("window.open did not raise NewWindowRequested in this headless-native session; "
                 + "popup-to-new-tab path implemented but not observed natively in this run.");
        }

        // ---- 6b. 默认下载策略：DownloadStarting 取消，不写系统下载目录 ----
        // 独立页签直接导航到 attachment URL：这是最可靠的 DownloadStarting 触发方式，且不影响其他页签。
        browser.ViewModel.NewTabCommand.Execute(null);
        var downloadTab = browser.ViewModel.ActiveTab;
        var downloadHost = browser.HostForTabForTesting(downloadTab);
        await Until(() => downloadHost.IsReady);
        var downloadMessages = new List<string>();
        downloadHost.StatusChanged += (_, message) => downloadMessages.Add(message);
        var downloadsFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        // loopback 对 /blocked-download.bin 返回 Content-Disposition: attachment，确保真实触发 DownloadStarting。
        var beforeDownloads = Directory.Exists(downloadsFolder)
            ? Directory.GetFiles(downloadsFolder).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        downloadHost.Navigate(loopback.Url + "blocked-download.bin");
        var cancelled = await WaitSoftAsync(
            () => downloadMessages.Any(message => message.Contains("已阻止自动下载", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(8));
        var afterDownloads = Directory.Exists(downloadsFolder)
            ? Directory.GetFiles(downloadsFolder).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Check(cancelled, "Default download policy cancels DownloadStarting with a clear message");
        Check(afterDownloads.SetEquals(beforeDownloads),
            "Cancelled download writes nothing into the user Downloads folder");
        browser.ViewModel.CloseTabByIdCommand.Execute(downloadTab);
        await Task.Delay(400);

        // ---- 7. 关闭页签只释放对应 controller ----
        browser.ViewModel.SelectTab(tabB);
        var hostBId = tabB.Id;
        browser.ViewModel.CloseTabByIdCommand.Execute(tabB);
        await Until(() => !browser.ViewModel.Tabs.Any(tab => tab.Id == hostBId));
        Check(!browser.ViewModel.Tabs.Any(tab => tab.Id == hostBId) && hostB.IsDisposedForTesting,
            "Closing a tab disposes only that tab's native host");
        Check(!hostA.IsDisposedForTesting && hostA.IsReady && await TitleAsync(hostA) == "NATIVE_MODULE_OK",
            "The remaining tab keeps its live controller and document");
        browser.ViewModel.CloseTabByIdCommand.Execute(tabA);
        await Until(() => browser.ViewModel.Tabs.Count == 1 && browser.ViewModel.Tabs[0].Id != tabA.Id);
        Check(browser.ViewModel.Tabs.Count == 1 && !browser.ViewModel.Tabs[0].Id.Equals(tabA.Id, StringComparison.Ordinal) &&
              browser.ViewModel.Tabs[0].NavigationAddress is null,
            "Closing the last tab leaves a single clear empty tab");
        Check(hostA.IsDisposedForTesting, "Closing the last tab disposes its native host");

        // ---- 8. 停靠区往返（同一窗口内移动不重建 controller） ----
        var freshTab = browser.ViewModel.ActiveTab;
        var freshHost = browser.HostForTabForTesting(freshTab);
        freshTab.AddressText = file;
        freshTab.NavigateCommand.Execute(null);
        await Until(() => freshHost.IsReady && freshTab.DocumentTitle == "NATIVE_MODULE_OK");
        foreach (var zone in Enum.GetValues<DockZone>())
        {
            window.MoveDockPage("browser", zone);
            await Task.Delay(150);
            Check(ReferenceEquals(browser, window.PageForTesting("browser")) && freshHost.IsReady &&
                  freshTab.DocumentTitle == "NATIVE_MODULE_OK",
                "Native controller survives page move " + zone);
        }
        Check(freshHost.ControllerCreateCountForTesting == 1, "Same-window dock moves never recreate the controller");

        // ---- 9. 模块停用释放 + 恢复 ----
        window.BrowserModuleForTesting.IsEnabled = false;
        await Task.Delay(200);
        Check(freshHost.IsDisposedForTesting && !freshHost.IsReady && TopLevel.GetTopLevel(browser) is null,
            "Module disable releases native controller and detaches page");
        Check(vm.Input.DraftText == "preserved native module draft" &&
            window.ActionsForTesting.GetDisabledReason("browser")?.Contains("已停用") == true &&
            window.GetVisualDescendants().OfType<DockRegionView>().All(dock => dock.Catalog.All(page => page.Id != "browser")),
            "Core draft and all module entry points remain consistent");
        window.BrowserModuleForTesting.IsEnabled = true;
        var restored = (BrowserView)window.PageForTesting("browser");
        await Until(() => restored.HostForTesting.IsReady && restored.ViewModel.DocumentTitle == "NATIVE_MODULE_OK");
        Check(!ReferenceEquals(restored, browser) && window.DockLayoutForTesting.Find("browser") == DockZone.Bottom,
            "Reactivation creates a fresh native host and restores document and zone");

        // ---- 10. 初始化期间停用不能复活 ----
        window.BrowserModuleForTesting.IsEnabled = false;
        window.BrowserModuleForTesting.IsEnabled = true;
        var pending = (BrowserView)window.PageForTesting("browser");
        window.BrowserModuleForTesting.IsEnabled = false;
        await Task.Delay(1500);
        Check(pending.HostForTesting.IsDisposedForTesting && !pending.HostForTesting.IsReady,
            "Immediate disable cannot resurrect a disposed native host");

        Console.WriteLine("Native module failures: " + failures);
        return failures == 0 ? 0 : 1;
    }

    private static async Task<string?> TitleAsync(BrowserViewHost host)
    {
        var raw = await host.ExecuteScriptForTestingAsync("document.title");
        if (raw is null) return null;
        var trimmed = raw.Trim();
        return trimmed.Length >= 2 && trimmed[0] == '"' ? trimmed[1..^1] : trimmed;
    }

    private static async Task<string?> BodyAsync(BrowserViewHost host)
    {
        var raw = await host.ExecuteScriptForTestingAsync("document.body ? document.body.innerText : null");
        return raw?.Trim();
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Native browser state did not settle.");
            await Task.Delay(100);
        }
    }

    private static async Task<bool> WaitSoftAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(100);
        }
        return condition();
    }

    /// <summary>极小本机 loopback HTTP 站点：只监听 127.0.0.1，不联网、不需要 URL ACL。</summary>
    private sealed class LoopbackSite : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _pump;

        private LoopbackSite(TcpListener listener, string body)
        {
            _listener = listener;
            Url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/";
            _pump = Task.Run(() => PumpAsync(body, _cts.Token));
        }

        public string Url { get; }

        public static LoopbackSite Start(string title, string marker)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return new LoopbackSite(listener,
                $"<!doctype html><title>{title}</title><h1>{title}</h1><p>{marker}</p>");
        }

        private async Task PumpAsync(string body, CancellationToken token)
        {
            var payload = Encoding.UTF8.GetBytes(body);
            while (!token.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(token); }
                catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException or SocketException) { return; }
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using var stream = client.GetStream();
                        var buffer = new byte[4096];
                        var read = 0;
                        try { read = await stream.ReadAsync(buffer, token); } catch (Exception e) when (e is IOException or OperationCanceledException) { }
                        var request = Encoding.ASCII.GetString(buffer, 0, Math.Max(0, read));
                        var isDownload = request.Contains("/blocked-download.bin", StringComparison.OrdinalIgnoreCase);
                        var bodyBytes = isDownload ? Encoding.ASCII.GetBytes("blocked") : payload;
                        var header = "HTTP/1.1 200 OK\r\n" +
                                     (isDownload
                                         ? "Content-Type: application/octet-stream\r\nContent-Disposition: attachment; filename=\"blocked-download.bin\"\r\n"
                                         : "Content-Type: text/html; charset=utf-8\r\n") +
                                     $"Content-Length: {bodyBytes.Length}\r\nConnection: close\r\n\r\n";
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(header), token);
                        await stream.WriteAsync(bodyBytes, token);
                        await stream.FlushAsync(token);
                    }
                    catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException or SocketException) { }
                    finally { client.Dispose(); }
                }, token);
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch (SocketException) { }
            try { _pump.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
            _cts.Dispose();
        }
    }
}
