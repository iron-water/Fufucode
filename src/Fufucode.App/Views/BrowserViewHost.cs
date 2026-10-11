using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fufucode.App.Infrastructure.Browser;
using Microsoft.Web.WebView2.Core;

namespace Fufucode.App.Views;

/// <summary>宿主回写的导航状态。后退/前进可用性取自 WebView2 历史，不做猜测。</summary>
public sealed record BrowserHostNavigationState(bool CanGoBack, bool CanGoForward, bool Loading, string? Source);

/// <summary>
/// 在 Avalonia 里承载 WebView2：只用 Microsoft.Web.WebView2 的 Core（CoreWebView2Controller + 父窗口 HWND），
/// 不引入 WPF/WinForms 宿主，因此不需要改动目标框架。
/// 已知代价：WebView2 是真实子窗口（airspace），靠 Bounds 与 IsVisible 与 Avalonia 布局对齐。
/// </summary>
public sealed class BrowserViewHost : Control, IDisposable
{
    private CoreWebView2Environment? _environment;
    private CoreWebView2Controller? _controller;
    private CoreWebView2? _core;
    private bool _initializeRequested;
    private bool _initializing;
    private bool _disposed;
    private bool _occluded;
    private string? _pendingAddress;
    private string? _pendingDocument;

    public event EventHandler<BrowserRuntimeProbe>? RuntimeProbeCompleted;
    public event EventHandler<BrowserHostNavigationState>? NavigationStateChanged;
    public event EventHandler<string>? StatusChanged;
    public event EventHandler<string>? DocumentTitleChanged;

    public bool IsReady => _core is not null;
    internal bool IsDisposedForTesting => _disposed;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        EffectiveViewportChanged += OnEffectiveViewportChanged;
        EnsureInitialized();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        EffectiveViewportChanged -= OnEffectiveViewportChanged;
        // 断开即释放真实子窗口，避免留下孤儿 HWND；再次显示时惰性重建（cookie 等在 UserDataFolder 中保留）。
        UpdateVisibility();
        Dispatcher.UIThread.Post(() => { if (VisualRoot is null) ReleaseController(); });
    }

    private void OnEffectiveViewportChanged(object? sender, EffectiveViewportChangedEventArgs e)
    {
        UpdateBounds();
        UpdateVisibility();
        EnsureInitialized();
    }

    /// <summary>惰性初始化：只有真正可见且有尺寸时才创建 WebView2，无头自测因此完全不触碰运行时。</summary>
    private void EnsureInitialized()
    {
        if (_disposed || _initializeRequested || _initializing) return;
        if (!IsEffectivelyVisible) return;
        if (Bounds.Width <= 0 || Bounds.Height <= 0) return;
        if (VisualRoot is null) return;
        if (!OperatingSystem.IsWindows()) { StatusChanged?.Invoke(this, "内置浏览器仅支持 Windows。"); _initializeRequested = true; return; }
        if (TryGetParentWindow() == IntPtr.Zero) return; // 窗口句柄尚未就绪（或无头环境）：静默等待下一次机会
        _initializeRequested = true;
        _initializing = true;
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            var probe = WebView2RuntimeGate.Probe();
            RuntimeProbeCompleted?.Invoke(this, probe);
            if (!probe.IsAvailable) { StatusChanged?.Invoke(this, probe.Guidance); return; }

            var parent = TryGetParentWindow();
            if (parent == IntPtr.Zero) { _initializeRequested = false; return; } // 竞态：等下一次视口变化重试

            var folder = BrowserUserDataLayout.Resolve(Program.SelfTestMode ? Program.SelfTestDataRoot : null);
            var folderError = BrowserUserDataLayout.TryEnsure(folder);
            if (folderError is not null) { StatusChanged?.Invoke(this, "浏览器数据目录不可写：" + folderError); return; }

            var environment = await CoreWebView2Environment.CreateAsync(null, folder, null);
            if (_disposed || TryGetParentWindow() != parent) return;
            _environment = environment;
            var controller = await environment.CreateCoreWebView2ControllerAsync(parent);
            if (_disposed || TryGetParentWindow() != parent) { controller.Close(); return; }
            _controller = controller;
            _core = _controller.CoreWebView2;
            WireCore(_core);

            UpdateBounds();
            UpdateVisibility();
            RaiseState(loading: false);

            if (_pendingAddress is { Length: > 0 }) { _core.Navigate(_pendingAddress); _pendingAddress = null; }
            else if (_pendingDocument is not null) { _core.NavigateToString(_pendingDocument); _pendingDocument = null; }
            else _core.Navigate("about:blank");
        }
        catch (Exception error)
        {
            if (!_disposed) StatusChanged?.Invoke(this, "内置浏览器初始化失败：" + error.Message);
        }
        finally
        {
            _initializing = false;
        }
    }

    private void WireCore(CoreWebView2 core)
    {
        if (core.Settings is { } settings)
        {
            settings.AreHostObjectsAllowed = false;   // 不向页面暴露宿主对象
            settings.IsScriptEnabled = true;          // 网页需要脚本，但本应用自身不注入任何脚本
            settings.AreDefaultScriptDialogsEnabled = true;
        }
        core.NavigationStarting += (_, _) => RaiseState(loading: true);
        core.HistoryChanged += (_, _) => RaiseState(loading: _loading);
        core.SourceChanged += (_, _) => RaiseState(loading: _loading);
        core.DocumentTitleChanged += (_, _) => DocumentTitleChanged?.Invoke(this, core.DocumentTitle);
        core.NavigationCompleted += OnNavigationCompleted;
        core.NewWindowRequested += OnNewWindowRequested;
        core.DownloadStarting += OnDownloadStarting;
    }

    private bool _loading;

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        RaiseState(loading: false);
        StatusChanged?.Invoke(this, e.IsSuccess ? "加载完成" : $"加载失败：{e.WebErrorStatus}");
    }

    /// <summary>单 tab 起步：新窗口请求就地导航，不弹出不可控的独立窗口。</summary>
    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (_core is not null && e.Uri is { Length: > 0 }) _core.Navigate(e.Uri);
    }

    /// <summary>下载交给系统默认下载文件夹，不写入项目目录。</summary>
    private void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            var name = DeriveFileName(e.DownloadOperation?.Uri);
            e.ResultFilePath = Path.Combine(folder, name);
            e.Handled = true;
            StatusChanged?.Invoke(this, "已开始下载到系统下载文件夹。");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            e.Handled = false;
            StatusChanged?.Invoke(this, "下载失败：" + error.Message);
        }
    }

    /// <summary>文件名取自下载 URI 的本地路径段；取不到就用固定名，不猜测。</summary>
    private static string DeriveFileName(string? uri)
    {
        if (uri is { Length: > 0 } && Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
            return SanitizeFileName(Path.GetFileName(parsed.LocalPath));
        return "download.bin";
    }

    private static string SanitizeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "download.bin";
        foreach (var invalid in Path.GetInvalidFileNameChars()) name = name!.Replace(invalid, '_');
        return name!.Trim().Length == 0 ? "download.bin" : name;
    }

    public void Navigate(string? address)
    {
        if (_core is { } core && address is { Length: > 0 }) core.Navigate(address);
        else _pendingAddress = address;
    }

    public void NavigateToDocument(string? document)
    {
        if (_core is { } core && document is not null) core.NavigateToString(document);
        else _pendingDocument = document;
    }

    public void GoBack() => _core?.GoBack();
    public void GoForward() => _core?.GoForward();
    public void Reload() => _core?.Reload();
    public void Stop() => _core?.Stop();

    private void RaiseState(bool loading)
    {
        _loading = loading;
        NavigationStateChanged?.Invoke(this, new BrowserHostNavigationState(
            _core?.CanGoBack ?? false, _core?.CanGoForward ?? false, loading, _core?.Source));
    }

    public void UpdateBounds()
    {
        if (_controller is null) return;
        var topLevel = VisualRoot as TopLevel ?? TopLevel.GetTopLevel(this);
        if (topLevel is null) return;
        var origin = Avalonia.VisualExtensions.TranslatePoint(this, default, topLevel);
        if (origin is null) return;
        var scaling = topLevel.RenderScaling;
        var x = (int)Math.Round(origin.Value.X * scaling);
        var y = (int)Math.Round(origin.Value.Y * scaling);
        var width = (int)Math.Max(0, Math.Round(Bounds.Width * scaling));
        var height = (int)Math.Max(0, Math.Round(Bounds.Height * scaling));
        try { _controller.Bounds = new System.Drawing.Rectangle(x, y, width, height); }
        catch (Exception error) when (error is COMException or InvalidOperationException or ObjectDisposedException)
        {
            // 控制器已释放：布局回调晚于释放是正常竞态，忽略。
        }
    }

    private void UpdateVisibility()
    {
        if (_controller is null) return;
        var visible = !_occluded && IsEffectivelyVisible && Bounds.Width > 0 && Bounds.Height > 0 && VisualRoot is not null;
        try { _controller.IsVisible = visible; }
        catch (Exception error) when (error is COMException or InvalidOperationException or ObjectDisposedException) { }
    }

    /// <summary>
    /// 取父窗口句柄。用 Win32 IsWindow 校验而不是依赖平台句柄描述符字符串：
    /// 无头环境下描述符可能非空但句柄无效，校验后一律当作“尚未就绪”，不创建 WebView2。
    /// </summary>
    private IntPtr TryGetParentWindow()
    {
        if (!OperatingSystem.IsWindows()) return IntPtr.Zero;
        var topLevel = TopLevel.GetTopLevel(this);
        var handle = topLevel?.TryGetPlatformHandle();
        if (handle is null || handle.Handle == IntPtr.Zero) return IntPtr.Zero;
        return IsWindow(handle.Handle) ? handle.Handle : IntPtr.Zero;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);

    private void ReleaseController()
    {
        _pendingAddress = null;
        _pendingDocument = null;
        _core = null;
        try { _controller?.Close(); }
        catch (Exception error) when (error is COMException or InvalidOperationException or ObjectDisposedException) { }
        _controller = null;
        _environment = null;
        _initializeRequested = false;
        _initializing = false;
        _loading = false;
    }

    public void SetOccluded(bool value) { _occluded = value; UpdateVisibility(); }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        EffectiveViewportChanged -= OnEffectiveViewportChanged;
        ReleaseController();
        RuntimeProbeCompleted = null;
        NavigationStateChanged = null;
        StatusChanged = null;
        DocumentTitleChanged = null;
    }
}
