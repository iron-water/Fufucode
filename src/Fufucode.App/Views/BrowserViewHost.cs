using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fufucode.App.Infrastructure.Browser;
using Microsoft.Web.WebView2.Core;

namespace Fufucode.App.Views;

/// <summary>宿主回写的导航状态。后退/前进可用性取自 WebView2 历史，不做猜测。</summary>
public sealed record BrowserHostNavigationState(bool CanGoBack, bool CanGoForward, bool Loading, string? Source);

/// <summary>一次真实导航完成的结果；只有 IsSuccess 且 http/https 才写入浏览历史。</summary>
public sealed record BrowserHostNavigationCompleted(bool IsSuccess, string? Source, string? Title);

/// <summary>页签音频状态：是否在播放、是否被静音。</summary>
public sealed record BrowserAudioState(bool Playing, bool Muted);

public enum BrowserDownloadState { InProgress, Completed, Interrupted }

/// <summary>下载决策：取消，或写入指定路径（null = 保留 WebView2 的默认路径）。</summary>
public sealed record BrowserDownloadDecision(bool Cancel, string? ResultFilePath, string? Note)
{
    public static BrowserDownloadDecision Allow(string? path = null) => new(false, path, null);
    public static BrowserDownloadDecision Deny(string note) => new(true, null, note);
}

/// <summary>一次真实下载请求；视图层据此决定路径/权限，然后 Resolve。</summary>
public sealed class BrowserDownloadRequest
{
    private readonly TaskCompletionSource<BrowserDownloadDecision> _decision =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public BrowserDownloadRequest(string id, string fileName, string suggestedPath, string? sourceUri, long totalBytes)
    {
        Id = id;
        FileName = fileName;
        SuggestedPath = suggestedPath;
        SourceUri = sourceUri;
        TotalBytes = totalBytes;
    }

    public string Id { get; }
    public string FileName { get; }
    public string SuggestedPath { get; }
    public string? SourceUri { get; }
    public long TotalBytes { get; }
    public Task<BrowserDownloadDecision> Decision => _decision.Task;
    public void Resolve(BrowserDownloadDecision decision) => _decision.TrySetResult(decision);
}

/// <summary>真实下载进度；Completed/Interrupted 是终态。</summary>
public sealed record BrowserDownloadProgress(
    string Id, string FileName, string ResultFilePath, long BytesReceived, long TotalBytes,
    BrowserDownloadState State, string? InterruptReason);

/// <summary>站点权限请求（摄像头/麦克风/定位/通知/剪贴板等），由视图层决定。</summary>
public sealed class BrowserPermissionRequest
{
    private readonly TaskCompletionSource<BrowserPermissionDecision> _decision =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public BrowserPermissionRequest(string uri, string kind, bool isUserInitiated)
    {
        Uri = uri;
        Kind = kind;
        IsUserInitiated = isUserInitiated;
    }

    public string Uri { get; }
    public string Kind { get; }
    public bool IsUserInitiated { get; }
    public string Host => Infrastructure.Browser.BrowserSiteRules.HostOf(Uri) ?? Uri;
    public Task<BrowserPermissionDecision> Decision => _decision.Task;
    public void Resolve(BrowserPermissionDecision decision) => _decision.TrySetResult(decision);
}

public sealed record BrowserPermissionDecision(bool Allow, bool Persist, string? Note)
{
    public static BrowserPermissionDecision Deny(string? note = null) => new(false, false, note);
    public static BrowserPermissionDecision Once() => new(true, false, null);
    public static BrowserPermissionDecision Always() => new(true, true, null);
}

/// <summary>
/// 页面新窗口请求。视图层负责创建新页签，并把该页签的 CoreWebView2 交回宿主；
/// 宿主设置 e.NewWindow 后，window.opener / postMessage / OAuth 弹窗关系得以保留。
/// </summary>
public sealed class BrowserNewWindowRequest
{
    private readonly TaskCompletionSource<CoreWebView2?> _window =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public BrowserNewWindowRequest(string uri, bool isUserInitiated)
    {
        Uri = uri;
        IsUserInitiated = isUserInitiated;
    }

    public string Uri { get; }
    public bool IsUserInitiated { get; }
    public Task<CoreWebView2?> Window => _window.Task;
    public void Resolve(CoreWebView2? window) => _window.TrySetResult(window);
}

/// <summary>
/// 在 Avalonia 里承载 WebView2：只用 Microsoft.Web.WebView2 的 Core（CoreWebView2Controller + 父窗口 HWND），
/// 不引入 WPF/WinForms 宿主，因此不需要改动目标框架。
/// 已知代价：WebView2 是真实子窗口（airspace），靠 Bounds 与 IsVisible 与 Avalonia 布局对齐。
///
/// 父窗口归属：控件被停靠页在“主窗口 ↔ 浮动窗口”之间搬移时，视觉树会先脱离再重挂。
/// 这里不用“回调时 VisualRoot 是否为空”判断，而是比对 controller 的实际父 HWND：
/// 重挂后若父 HWND 变化，就用官方 controller.ParentWindow 重挂同一 controller（保留页面 DOM/历史），
/// 只有确实悬空（未重挂且无 TopLevel）才释放。
/// </summary>
public sealed class BrowserViewHost : Control, IDisposable
{
    private CoreWebView2Environment? _environment;
    private CoreWebView2Controller? _controller;
    private CoreWebView2? _core;
    private readonly List<Action> _coreDetachers = new();
    internal Func<uint, KeyModifiers, bool>? AcceleratorHandler { get; set; }
    private bool _initializeRequested;
    private bool _initializing;
    private bool _disposed;
    private bool _occluded;
    /// <summary>
    /// 控制器创建前的唯一待发导航：地址与文档二选一，最后一次请求胜出（不再被 _pendingAddress 覆盖）。
    /// </summary>
    private (bool IsDocument, string Value)? _pendingNavigation;
    /// <summary>创建前用户已 Stop：初始化完成后不导航，并立即收敛加载状态。</summary>
    private bool _pendingStop;
    /// <summary>创建前设置的缩放（百分比），初始化完成后写入真实 controller。</summary>
    private double? _pendingZoomPercent;
    private int _lifecycleToken;
    private int _attachGeneration;
    private int _controllerCreateCount;
    private int _reparentCount;
    private int _releaseCount;
    private int _reparentFailureCount;
    private IntPtr _intendedParentInParent = IntPtr.Zero;
    private IntPtr _ownerWindowHandle = IntPtr.Zero;
    private Window? _attachedWindow;

    public event EventHandler<BrowserRuntimeProbe>? RuntimeProbeCompleted;
    public event EventHandler<BrowserHostNavigationState>? NavigationStateChanged;
    public event EventHandler<BrowserHostNavigationCompleted>? NavigationCompleted;
    public event EventHandler<string>? StatusChanged;
    public event EventHandler<string>? DocumentTitleChanged;
    /// <summary>真实 favicon PNG 字节；null 表示页面没有图标。</summary>
    public event EventHandler<byte[]?>? FaviconChanged;
    public event EventHandler<BrowserAudioState>? AudioStateChanged;

    /// <summary>右键菜单里的应用动作；参数是动作 ID（send-page / send-selection / annotate / open-external / copy-address）。</summary>
    public event EventHandler<string>? ContextMenuActionRequested;

    /// <summary>真实下载请求；没有订阅者时保持 WebView2 默认下载行为。</summary>
    public event EventHandler<BrowserDownloadRequest>? DownloadRequested;

    /// <summary>真实下载进度（含终态）；用于下载列表与历史落盘。</summary>
    public event EventHandler<BrowserDownloadProgress>? DownloadProgressChanged;

    /// <summary>站点权限请求；没有订阅者时保持 WebView2 默认行为。</summary>
    public event EventHandler<BrowserPermissionRequest>? PermissionRequested;

    /// <summary>页面内链接改投系统浏览器（设置：网页链接打开位置 = 系统默认浏览器）。</summary>
    public event EventHandler<string>? OpenExternallyRequested;

    private bool _openLinksExternally;
    private bool _suppressExternalNavigation;

    /// <summary>设置是否把所有页面内 http/https 跳转交给系统浏览器；地址栏主动导航不受影响。</summary>
    public void SetOpenLinksExternally(bool value) => _openLinksExternally = value;

    /// <summary>地址栏/Agent 主动导航期间临时关闭外部跳转，保证主动导航仍在内置浏览器完成。</summary>
    private IDisposable SuppressExternalNavigation()
    {
        _suppressExternalNavigation = true;
        return new Suppression(this);
    }

    private sealed class Suppression(BrowserViewHost host) : IDisposable
    {
        public void Dispose() => host._suppressExternalNavigation = false;
    }

    /// <summary>页面请求打开新窗口；视图层创建页签后把真实 CoreWebView2 交回，保留 opener 关系。</summary>
    public event EventHandler<BrowserNewWindowRequest>? NewWindowRequested;

    public bool IsReady => _core is not null;
    internal bool IsDisposedForTesting => _disposed;
    internal bool HasControllerForTesting => _controller is not null;
    internal int ControllerCreateCountForTesting => _controllerCreateCount;
    internal int ReparentCountForTesting => _reparentCount;
    internal int ReleaseCountForTesting => _releaseCount;
    internal int ReparentFailureCountForTesting => _reparentFailureCount;
    internal string? LastReparentFailureForTesting => _lastReparentFailure;
    private string? _lastReparentFailure;
    internal string? LastReleaseTraceForTesting => _lastReleaseTrace;
    private string? _lastReleaseTrace;
    internal bool IsControllerVisibleForTesting => SafeControllerVisible();
    internal IntPtr ParentWindowForTesting => SafeControllerParent();
    internal IntPtr IntendedParentForTesting => _intendedParentInParent;
    internal IntPtr OwnerWindowForTesting => _ownerWindowHandle;
    internal bool IsInitializingForTesting => _initializing;
    internal bool HasPendingStopForTesting => _pendingStop;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attachGeneration++;
        _attachedWindow = TopLevel.GetTopLevel(this) as Window;
        CaptureOwnerWindow();
        EffectiveViewportChanged -= OnEffectiveViewportChanged;
        EffectiveViewportChanged += OnEffectiveViewportChanged;
        // 重挂到另一个窗口时，先把已有 controller 重挂到新父 HWND，再按需惰性初始化。
        Dispatcher.UIThread.Post(SynchronizeWithParent, DispatcherPriority.Loaded);
        EnsureInitialized();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        EffectiveViewportChanged -= OnEffectiveViewportChanged;
        UpdateVisibility();
        // 关键时序：浮动窗（被主窗拥有的 owned window）关闭会销毁它的子 HWND，
        // 进而销毁 WebView 的子窗口。脱离时若根是被拥有的浮动窗，立刻把 controller 重挂到 Owner（主窗）；
        // 停靠区内部搬移时根是无 Owner 的主窗，保持不变。之后再异步处理最终挂载/释放。
        // 浮动窗的关闭与页面脱离在同一同步批次里，异步回调会晚于 HWND 销毁。
        // 因此这里用挂载时捕获的窗口，立刻把 controller 重挂到它的 Owner（主窗）。
        var ownerHandle = _attachedWindow?.Owner?.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (_controller is not null)
        {
            if (IsValidWindow(ownerHandle)) ReparentTo(ownerHandle);
            else if (VisualRoot is not null && IsValidWindow(_ownerWindowHandle)) ReparentTo(_ownerWindowHandle);
        }
        _attachedWindow = null;
        var generation = _attachGeneration;
        // 同一批里可能马上挂到另一个 Window。浮动窗关闭时它的子 HWND 会随之销毁，
        // 因此先在脱离回调里把 controller 重挂到所属主窗口（Owner），保住 DOM/历史，
        // 之后真正挂到新停靠位时再对齐父 HWND；只有确实没有可归依窗口才释放。
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed || generation != _attachGeneration) return;
            if (VisualRoot is not null || TopLevel.GetTopLevel(this) is not null) { SynchronizeWithParent(); return; }
            // 停靠返回不是“同批重挂”：页面会先停放在 _parkedPages，随后才挂回主窗停靠位。
            // Owner 主窗仍存活时保留 controller（DOM/历史），等下一次挂载再由 SynchronizeWithParent 归位；
            // 只有 Owner 也不可用（主窗关闭）或 controller 已失效才释放。
            if (_controller is not null && IsValidWindow(_ownerWindowHandle))
            {
                if (SafeControllerParent() != _ownerWindowHandle) ReparentTo(_ownerWindowHandle);
                return;
            }
            ReleaseController();
        }, DispatcherPriority.Loaded);
    }

    /// <summary>记录可归依的窗口句柄：浮动窗用它的 Owner（主窗口），主窗用自身。</summary>
    private void CaptureOwnerWindow()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (TopLevel.GetTopLevel(this) is not Window window) return;
        var owner = window.Owner;
        var handle = owner?.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero || !IsValidWindow(handle)) handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle != IntPtr.Zero && IsValidWindow(handle)) _ownerWindowHandle = handle;
    }

    private bool ReparentTo(IntPtr parent)
    {
        if (_controller is null || !IsValidWindow(parent)) return false;
        if (SafeControllerParent() == parent) { _intendedParentInParent = parent; return true; }
        try
        {
            _controller.ParentWindow = parent;
            _intendedParentInParent = parent;
            _reparentCount++;
            UpdateBounds();
            UpdateVisibility();
            return true;
        }
        catch (Exception error) when (IsControllerGone(error))
        {
            _reparentFailureCount++;
            _lastReparentFailure = error.GetType().Name + ": " + error.Message + " hresult=0x" + (error.HResult & 0xFFFFFFFF).ToString("X8");
            return false;
        }
    }

    private static bool IsValidWindow(IntPtr handle) =>
        handle != IntPtr.Zero && OperatingSystem.IsWindows() && IsWindow(handle);

    private void OnEffectiveViewportChanged(object? sender, EffectiveViewportChangedEventArgs e)
    {
        UpdateBounds();
        UpdateVisibility();
        if (_controller is not null) { SynchronizeWithParent(); return; }
        EnsureInitialized();
    }

    /// <summary>
    /// 让原生 controller 的父 HWND、Bounds、可见性与当前视觉树一致：
    /// 父窗口变化时用 controller.ParentWindow 重挂（保留 controller/DOM/历史），而不是重建。
    /// </summary>
    private void SynchronizeWithParent()
    {
        if (_disposed) return;
        var parent = TryGetParentWindow();
        if (parent == IntPtr.Zero) return;
        if (_controller is null) { EnsureInitialized(); return; }
        if (SafeControllerParent() != parent && !ReparentTo(parent))
        {
            // 重挂失败只在确实无法复用 controller 时释放，交给下一次挂载重建。
            ReleaseController();
            EnsureInitialized();
            return;
        }
        UpdateBounds();
        UpdateVisibility();
    }

    /// <summary>惰性初始化：只有真正可见且有尺寸时才创建 WebView2，无头自测因此完全不触碰运行时。</summary>
    private void EnsureInitialized()
    {
        if (_disposed || _initializeRequested || _initializing) return;
        if (!IsEffectivelyVisible) return;
        if (Bounds.Width <= 0 || Bounds.Height <= 0) return;
        if (VisualRoot is null) return;
        // 无头自测不创建真实 WebView2：既没有真实 HWND，也不应触碰运行时。
        // 原生模块自测显式打开 NativeModuleSelfTest，仍走真实控制器。
        if (Program.SelfTestMode && !Program.NativeModuleSelfTest) { _initializeRequested = true; return; }
        if (!OperatingSystem.IsWindows()) { StatusChanged?.Invoke(this, "内置浏览器仅支持 Windows。"); _initializeRequested = true; return; }
        var parent = TryGetParentWindow();
        if (parent == IntPtr.Zero) return; // 窗口句柄尚未就绪（或无头环境）：静默等待下一次机会
        _initializeRequested = true;
        _initializing = true;
        var token = ++_lifecycleToken;
        _ = InitializeAsync(token, parent);
    }

    private async Task InitializeAsync(int token, IntPtr parent)
    {
        try
        {
            var probe = WebView2RuntimeGate.Probe();
            RuntimeProbeCompleted?.Invoke(this, probe);
            if (!probe.IsAvailable) { StatusChanged?.Invoke(this, probe.Guidance); return; }

            var folder = BrowserUserDataLayout.Resolve(Program.SelfTestMode ? Program.SelfTestDataRoot : null);
            var folderError = BrowserUserDataLayout.TryEnsure(folder);
            if (folderError is not null) { StatusChanged?.Invoke(this, "浏览器数据目录不可写：" + folderError); return; }

            var environment = await BrowserEnvironmentProvider.GetAsync(folder);
            if (Abandon(token)) return;
            _environment = environment;

            var controller = await environment.CreateCoreWebView2ControllerAsync(parent);
            if (Abandon(token)) { controller.Close(); return; }
            _controller = controller;
            controller.AcceleratorKeyPressed += OnAcceleratorKeyPressed;
            _intendedParentInParent = SafeControllerParent();
            _controllerCreateCount++;
            _core = controller.CoreWebView2;
            WireCore(_core);
            _coreReady?.TrySetResult(_core);
            // 扩展加载失败只影响扩展，不阻断页面；结果写进状态区便于诊断。
            _ = LoadExtensionsAsync(folder);

            // 创建期间父窗口可能已经换了（同一批跨窗搬移）：这里再对齐一次，改动的是同一 controller。
            SynchronizeWithParent();
            // 创建前的早期缩放必须进入真实 controller。
            if (_pendingZoomPercent is { } earlyZoom) controller.ZoomFactor = earlyZoom / 100.0;

            if (_pendingStop)
            {
                // 用户已在初始化期间 Stop：取消待发导航，不加载任何被取消的页面。
                _pendingStop = false;
                _pendingNavigation = null;
                RaiseState(loading: false);
                StatusChanged?.Invoke(this, "已停止加载。");
            }
            else
            {
                RaiseState(loading: false);
                using (SuppressExternalNavigation())
                {
                    switch (_pendingNavigation)
                    {
                        case (true, var document): _pendingNavigation = null; _core.NavigateToString(document); break;
                        case (false, var address): _pendingNavigation = null; _core.Navigate(address); break;
                        default: _core.Navigate("about:blank"); break;
                    }
                }
            }
        }
        catch (Exception error)
        {
            if (!_disposed) StatusChanged?.Invoke(this, "内置浏览器初始化失败：" + error.Message);
            _coreReady?.TrySetResult(null);
            _coreReady = null;
            _initializeRequested = false;
        }
        finally
        {
            _initializing = false;
        }

        bool Abandon(int captured)
        {
            if (!_disposed && captured == _lifecycleToken) return false;
            // 生命周期已经前进：本次创建作废，并允许后续挂载重新初始化。
            _initializeRequested = false;
            return true;
        }
    }

    private async Task LoadFaviconAsync(CoreWebView2 core)
    {
        try
        {
            using var stream = await core.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png).ConfigureAwait(true);
            if (stream is null) { FaviconChanged?.Invoke(this, null); return; }
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory).ConfigureAwait(true);
            FaviconChanged?.Invoke(this, memory.ToArray());
        }
        catch (Exception error) when (error is COMException or InvalidOperationException or ObjectDisposedException or IOException)
        {
            FaviconChanged?.Invoke(this, null);
        }
    }

    /// <summary>静音/取消静音当前页签。</summary>
    public bool ToggleMute()
    {
        if (_core is not { } core) return false;
        try { core.IsMuted = !core.IsMuted; AudioStateChanged?.Invoke(this, new BrowserAudioState(SafeIsPlayingAudio(), SafeIsMuted())); return true; }
        catch (Exception error) when (IsControllerGone(error)) { return false; }
    }

    private bool SafeIsPlayingAudio() { try { return _core?.IsDocumentPlayingAudio ?? false; } catch (Exception e) when (IsControllerGone(e)) { return false; } }
    private bool SafeIsMuted() { try { return _core?.IsMuted ?? false; } catch (Exception e) when (IsControllerGone(e)) { return false; } }

    private async Task LoadExtensionsAsync(string userDataFolder)
    {
        if (_core is not { } core) return;
        try
        {
            var result = await Infrastructure.Browser.BrowserExtensionLoader
                .LoadAsync(userDataFolder, core.Profile).ConfigureAwait(true);
            if (result.Loaded.Count > 0)
                StatusChanged?.Invoke(this, "已加载浏览器扩展：" + string.Join("、", result.Loaded));
            if (result.Failures.Count > 0)
                StatusChanged?.Invoke(this, "浏览器扩展加载失败：" + string.Join("；", result.Failures));
        }
        catch (Exception error) when (error is InvalidOperationException or NotSupportedException
            or System.Runtime.InteropServices.COMException or ObjectDisposedException)
        {
            StatusChanged?.Invoke(this, "浏览器扩展不可用：" + error.Message);
        }
    }

    private void WireCore(CoreWebView2 core)
    {
        if (core.Settings is { } settings)
        {
            settings.AreHostObjectsAllowed = false;   // 不向页面暴露宿主对象
            settings.IsScriptEnabled = true;          // 网页需要脚本，但本应用自身不注入任何脚本
            settings.AreDefaultScriptDialogsEnabled = true;
            ApplyUserAgent(settings);
            ApplyAutofill(settings);
        }
        void OnNavigationStarting(object? _, CoreWebView2NavigationStartingEventArgs e)
        {
            RaiseState(loading: true);
            // 页面内导航到系统浏览器（设置：网页链接打开位置 = 系统默认浏览器）。
            if (_openLinksExternally && e.Uri is { Length: > 0 } target &&
                Uri.TryCreate(target, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
                !string.Equals(target, SafeSource(), StringComparison.OrdinalIgnoreCase) &&
                !_suppressExternalNavigation)
            {
                e.Cancel = true;
                Dispatcher.UIThread.Post(() => OpenExternallyRequested?.Invoke(this, target));
                StatusChanged?.Invoke(this, "已在系统浏览器打开：" + target);
            }
        }
        void OnHistoryChanged(object? _, object __) => RaiseState(loading: _loading);
        void OnSourceChanged(object? _, CoreWebView2SourceChangedEventArgs __) => RaiseState(loading: _loading);
        void OnTitleChanged(object? _, object __) => DocumentTitleChanged?.Invoke(this, core.DocumentTitle);
        void OnCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e) => OnNavigationCompleted(sender, e);
        void OnNewWindow(object? sender, CoreWebView2NewWindowRequestedEventArgs e) => OnNewWindowRequested(sender, e);
        void OnDownload(object? sender, CoreWebView2DownloadStartingEventArgs e) => OnDownloadStarting(sender, e);
        void OnContextMenu(object? sender, CoreWebView2ContextMenuRequestedEventArgs e) => OnContextMenuRequested(e);
        void OnPermission(object? sender, CoreWebView2PermissionRequestedEventArgs e) => OnPermissionRequested(e);
        void OnFavicon(object? sender, object e) => _ = LoadFaviconAsync(core);
        void OnAudio(object? sender, object e) => AudioStateChanged?.Invoke(this,
            new BrowserAudioState(SafeIsPlayingAudio(), SafeIsMuted()));

        core.NavigationStarting += OnNavigationStarting;
        core.HistoryChanged += OnHistoryChanged;
        core.SourceChanged += OnSourceChanged;
        core.DocumentTitleChanged += OnTitleChanged;
        core.NavigationCompleted += OnCompleted;
        core.NewWindowRequested += OnNewWindow;
        core.DownloadStarting += OnDownload;
        core.ContextMenuRequested += OnContextMenu;
        core.PermissionRequested += OnPermission;
        core.FaviconChanged += OnFavicon;
        core.IsDocumentPlayingAudioChanged += OnAudio;
        core.IsMutedChanged += OnAudio;

        _coreDetachers.Add(() => core.NavigationStarting -= OnNavigationStarting);
        _coreDetachers.Add(() => core.HistoryChanged -= OnHistoryChanged);
        _coreDetachers.Add(() => core.SourceChanged -= OnSourceChanged);
        _coreDetachers.Add(() => core.DocumentTitleChanged -= OnTitleChanged);
        _coreDetachers.Add(() => core.NavigationCompleted -= OnCompleted);
        _coreDetachers.Add(() => core.NewWindowRequested -= OnNewWindow);
        _coreDetachers.Add(() => core.DownloadStarting -= OnDownload);
        _coreDetachers.Add(() => core.ContextMenuRequested -= OnContextMenu);
        _coreDetachers.Add(() => core.PermissionRequested -= OnPermission);
        _coreDetachers.Add(() => core.FaviconChanged -= OnFavicon);
        _coreDetachers.Add(() => core.IsDocumentPlayingAudioChanged -= OnAudio);
        _coreDetachers.Add(() => core.IsMutedChanged -= OnAudio);
    }

    private string? _userAgent;

    /// <summary>设置页面 UA；空值恢复 WebView2 默认。初始化前调用会在 WireCore 时生效。</summary>
    public void SetUserAgent(string? userAgent)
    {
        _userAgent = string.IsNullOrWhiteSpace(userAgent) ? null : userAgent.Trim();
        if (_core?.Settings is { } settings) ApplyUserAgent(settings);
    }

    private bool? _autofill;
    private bool? _passwordAutosave;

    public void SetAutofill(bool enabled) { _autofill = enabled; if (_core?.Settings is { } settings) ApplyAutofill(settings); }
    public void SetPasswordAutosave(bool enabled) { _passwordAutosave = enabled; if (_core?.Settings is { } settings) ApplyAutofill(settings); }

    private void ApplyAutofill(CoreWebView2Settings settings)
    {
        try
        {
            if (_autofill is { } autofill) settings.IsGeneralAutofillEnabled = autofill;
            if (_passwordAutosave is { } password) settings.IsPasswordAutosaveEnabled = password;
        }
        catch (Exception error) when (error is COMException or InvalidOperationException or NotSupportedException or ObjectDisposedException) { }
    }

    private void ApplyUserAgent(CoreWebView2Settings settings)
    {
        try { settings.UserAgent = _userAgent is { Length: > 0 } value ? value : settings.UserAgent; }
        catch (Exception error) when (error is COMException or InvalidOperationException or NotSupportedException or ObjectDisposedException) { }
    }

    private void OnAcceleratorKeyPressed(object? sender, CoreWebView2AcceleratorKeyPressedEventArgs e)
    {
        if (e.KeyEventKind is not (CoreWebView2KeyEventKind.KeyDown or CoreWebView2KeyEventKind.SystemKeyDown)) return;
        var modifiers = KeyModifiers.None;
        if (Pressed(0x11)) modifiers |= KeyModifiers.Control;
        if (Pressed(0x10)) modifiers |= KeyModifiers.Shift;
        if (Pressed(0x12)) modifiers |= KeyModifiers.Alt;
        if (Pressed(0x5B) || Pressed(0x5C)) modifiers |= KeyModifiers.Meta;
        if (AcceleratorHandler?.Invoke(e.VirtualKey, modifiers) == true) e.Handled = true;
        static bool Pressed(int key) => (GetKeyState(key) & 0x8000) != 0;
    }
    [DllImport("user32.dll")] private static extern short GetKeyState(int key);

    private bool _loading;

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        RaiseState(loading: false);
        var source = SafeSource();
        var title = _core?.DocumentTitle;
        NavigationCompleted?.Invoke(this, new BrowserHostNavigationCompleted(e.IsSuccess, source, title));
        StatusChanged?.Invoke(this, e.IsSuccess ? "加载完成" : $"加载失败：{e.WebErrorStatus}");
    }

    /// <summary>
    /// 页面请求新窗口：交给外层的多页签管理器开新页签；用 deferral 等新页签的 controller 就绪，
    /// 再把它的 CoreWebView2 赋给 e.NewWindow，而不是只导航一个互不相关的页签。
    /// </summary>
    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        if (NewWindowRequested is null) return;
        e.Handled = true;
        var deferral = e.GetDeferral();
        var request = new BrowserNewWindowRequest(e.Uri ?? string.Empty, e.IsUserInitiated);
        _ = ApplyNewWindowAsync(request, e, deferral);
        NewWindowRequested.Invoke(this, request);
    }

    private async Task ApplyNewWindowAsync(BrowserNewWindowRequest request,
        CoreWebView2NewWindowRequestedEventArgs args, CoreWebView2Deferral deferral)
    {
        CoreWebView2? window = null;
        try { window = await request.Window.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
        catch (TimeoutException) { StatusChanged?.Invoke(this, "新窗口打开超时，已取消该弹窗。"); }
        catch (Exception error) when (error is InvalidOperationException or ObjectDisposedException or COMException)
        {
            StatusChanged?.Invoke(this, "新窗口打开失败：" + error.Message);
        }
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                if (window is not null && !_disposed) args.NewWindow = window;
            }
            catch (Exception error) when (error is InvalidOperationException or ObjectDisposedException or COMException)
            {
                StatusChanged?.Invoke(this, "新窗口接管失败：" + error.Message);
            }
            finally { deferral.Complete(); }
        });
    }

    /// <summary>
    /// 站点权限：默认由 WebView2 自己处理；有订阅者时用 deferral 等 UI 决策。
    /// 决策必须显式设置 State，否则请求会一直挂起。
    /// </summary>
    private void OnPermissionRequested(CoreWebView2PermissionRequestedEventArgs e)
    {
        if (PermissionRequested is null) return;
        var request = new BrowserPermissionRequest(e.Uri, e.PermissionKind.ToString(), e.IsUserInitiated);
        e.Handled = true;
        var deferral = e.GetDeferral();
        _ = ApplyPermissionDecisionAsync(request, e, deferral);
    }

    private async Task ApplyPermissionDecisionAsync(BrowserPermissionRequest request,
        CoreWebView2PermissionRequestedEventArgs args, CoreWebView2Deferral deferral)
    {
        BrowserPermissionDecision decision;
        try { decision = await request.Decision.ConfigureAwait(false); }
        catch (Exception) { decision = BrowserPermissionDecision.Deny("权限决策失败。"); }
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                args.State = decision.Allow ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny;
                args.SavesInProfile = decision.Allow && decision.Persist;
                if (!decision.Allow && decision.Note is { Length: > 0 } note)
                    StatusChanged?.Invoke(this, note);
            }
            catch (Exception error) when (error is COMException or InvalidOperationException or ObjectDisposedException)
            {
                StatusChanged?.Invoke(this, "权限设置失败：" + error.Message);
            }
            finally { deferral.Complete(); }
        });
    }

    /// <summary>
    /// 在 WebView2 原生右键菜单上追加应用动作，而不是用 Avalonia 自绘菜单覆盖它：
    /// 原生项（复制/粘贴/检查等）全部保留，应用动作只做真正的跨面板操作。
    /// </summary>
    private void OnContextMenuRequested(CoreWebView2ContextMenuRequestedEventArgs e)
    {
        if (_environment is null) return;
        // 逆序插入，最终顺序为：发送本页 / 引用选中文字 / 标注区域 / 在默认浏览器打开 / 复制地址。
        InsertMenuItem(e, "复制本页地址", "copy-address");
        InsertMenuItem(e, "在默认浏览器打开本页", "open-external");
        InsertMenuItem(e, "标注页面区域…", "annotate");
        InsertMenuItem(e, "引用选中文字到对话", "send-selection");
        InsertMenuItem(e, "发送本页到对话", "send-page");
    }

    private void InsertMenuItem(CoreWebView2ContextMenuRequestedEventArgs e, string label, string action)
    {
        try
        {
            var item = CreateMenuItem(label);
            if (item is null) return;
            item.CustomItemSelected += (_, _) =>
                Dispatcher.UIThread.Post(() => ContextMenuActionRequested?.Invoke(this, action));
            e.MenuItems.Insert(0, item);
        }
        catch (Exception error) when (error is COMException or InvalidOperationException or NotSupportedException or ArgumentException)
        {
            // 运行时没有该菜单 API 时保持原生菜单可用，不阻断右键。
        }
    }

    private CoreWebView2ContextMenuItem? CreateMenuItem(string label)
    {
        if (_environment is null) return null;
        try { return _environment.CreateContextMenuItem(label, null, CoreWebView2ContextMenuItemKind.Command); }
        catch (ArgumentNullException)
        {
            // 部分运行时要求图标流非空：用 1x1 透明 PNG，视觉上不显示图标。
            using var icon = new MemoryStream(Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg=="));
            return _environment.CreateContextMenuItem(label, icon, CoreWebView2ContextMenuItemKind.Command);
        }
    }

    private readonly Dictionary<string, CoreWebView2DownloadOperation> _downloads = new(StringComparer.Ordinal);
    private TaskCompletionSource<CoreWebView2?>? _coreReady;

    /// <summary>
    /// 等待真实 CoreWebView2 就绪（弹窗接手、Agent 初始化都可用）。
    /// 未可见/无尺寸时返回的任务保持挂起，由下一次挂载继续初始化。
    /// </summary>
    public Task<CoreWebView2?> EnsureCoreAsync()
    {
        if (_core is { } core) return Task.FromResult<CoreWebView2?>(core);
        _coreReady ??= new TaskCompletionSource<CoreWebView2?>(TaskCreationOptions.RunContinuationsAsynchronously);
        EnsureInitialized();
        return _coreReady.Task;
    }

    /// <summary>
    /// 真实下载：把路径与权限决策交给视图层（设置 → 浏览器），宿主只负责 WebView2 生命周期。
    /// 没有订阅者时保持原生默认下载；有订阅者时用 deferral 等待决策，再落盘。
    /// </summary>
    private void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        if (e.DownloadOperation is not { } operation || DownloadRequested is null) return;
        var request = new BrowserDownloadRequest(
            Guid.NewGuid().ToString("N"),
            Path.GetFileName(e.ResultFilePath),
            e.ResultFilePath,
            operation.Uri,
            operation.TotalBytesToReceive is { } total ? (long)Math.Min(total, long.MaxValue) : 0);
        e.Handled = true;
        var deferral = e.GetDeferral();
        DownloadRequested.Invoke(this, request);
        _ = ApplyDownloadDecisionAsync(request, e, operation, deferral);
    }

    private async Task ApplyDownloadDecisionAsync(BrowserDownloadRequest request,
        CoreWebView2DownloadStartingEventArgs args, CoreWebView2DownloadOperation operation,
        CoreWebView2Deferral deferral)
    {
        BrowserDownloadDecision decision;
        try { decision = await request.Decision.ConfigureAwait(false); }
        catch (Exception) { decision = BrowserDownloadDecision.Deny("下载决策失败，已取消。"); }
        // WebView2 事件参数只能在创建它的 UI 线程上写；决策可能由后台续体完成。
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                if (decision.Cancel)
                {
                    args.Cancel = true;
                    StatusChanged?.Invoke(this, decision.Note ?? "下载已取消。");
                }
                else
                {
                    if (!string.IsNullOrWhiteSpace(decision.ResultFilePath))
                        args.ResultFilePath = decision.ResultFilePath!;
                    AttachDownload(request.Id, operation, args.ResultFilePath);
                    StatusChanged?.Invoke(this, "开始下载：" + Path.GetFileName(args.ResultFilePath));
                }
            }
            catch (Exception error) when (error is COMException or InvalidOperationException or ObjectDisposedException or ArgumentException)
            {
                args.Cancel = true;
                StatusChanged?.Invoke(this, "下载初始化失败：" + error.Message);
            }
            finally { deferral.Complete(); }
        });
    }

    private void AttachDownload(string id, CoreWebView2DownloadOperation operation, string resultPath)
    {
        _downloads[id] = operation;
        void Raise() => DownloadProgressChanged?.Invoke(this, new BrowserDownloadProgress(
            id,
            Path.GetFileName(resultPath),
            operation.ResultFilePath,
            operation.BytesReceived,
            operation.TotalBytesToReceive is { } total ? (long)Math.Min(total, long.MaxValue) : 0,
            operation.State switch
            {
                CoreWebView2DownloadState.Completed => BrowserDownloadState.Completed,
                CoreWebView2DownloadState.Interrupted => BrowserDownloadState.Interrupted,
                _ => BrowserDownloadState.InProgress,
            },
            operation.InterruptReason == CoreWebView2DownloadInterruptReason.None
                ? null
                : operation.InterruptReason.ToString()));
        void OnBytes(object? _, object __) => Raise();
        void OnState(object? _, object __)
        {
            Raise();
            if (operation.State is CoreWebView2DownloadState.Completed or CoreWebView2DownloadState.Interrupted)
            {
                operation.BytesReceivedChanged -= OnBytes;
                operation.StateChanged -= OnState;
                _downloads.Remove(id);
            }
        }
        operation.BytesReceivedChanged += OnBytes;
        operation.StateChanged += OnState;
        Raise();
    }

    /// <summary>取消一个仍在进行的下载；未知 ID 返回 false。</summary>
    public bool CancelDownload(string id)
    {
        if (!_downloads.TryGetValue(id, out var operation)) return false;
        try { operation.Cancel(); return true; }
        catch (Exception error) when (IsControllerGone(error)) { return false; }
    }

    public void Navigate(string? address)
    {
        if (_core is { } core && address is { Length: > 0 })
        {
            using (SuppressExternalNavigation()) core.Navigate(address);
        }
        else if (address is { Length: > 0 })
        {
            // 新的用户导航撤销此前的待发 Stop：旧请求保持取消，新请求照常加载。
            _pendingStop = false;
            _pendingNavigation = (false, address);
        }
    }

    public void NavigateToDocument(string? document)
    {
        if (_core is { } core && document is not null) core.NavigateToString(document);
        else if (document is not null)
        {
            _pendingStop = false;
            _pendingNavigation = (true, document);
        }
    }

    public void GoBack()
    {
        try { using (SuppressExternalNavigation()) _core?.GoBack(); } catch (Exception e) when (IsControllerGone(e)) { }
    }
    public void GoForward()
    {
        try { using (SuppressExternalNavigation()) _core?.GoForward(); } catch (Exception e) when (IsControllerGone(e)) { }
    }
    public void Reload()
    {
        try { using (SuppressExternalNavigation()) _core?.Reload(); } catch (Exception e) when (IsControllerGone(e)) { }
    }
    public void Stop()
    {
        // 未就绪时 Stop 也必须有效：取消待发导航，初始化完成后不再加载被取消的页面。
        if (_core is not { } core)
        {
            _pendingStop = true;
            _pendingNavigation = null;
            // 即时收敛加载状态：不能只等初始化终态，否则 tab.IsLoading 会一直为 true。
            RaiseState(loading: false);
            StatusChanged?.Invoke(this, "已停止加载。");
            return;
        }
        try { core.Stop(); } catch (Exception e) when (IsControllerGone(e)) { }
    }

    /// <summary>打开开发者工具窗口；只能由用户显式触发。</summary>
    public bool OpenDevTools()
    {
        if (_core is not { } core) return false;
        try { core.OpenDevToolsWindow(); return true; }
        catch (Exception error) when (error is COMException or InvalidOperationException or NotSupportedException or ObjectDisposedException) { return false; }
    }

    /// <summary>页内查找；运行时若无 Find API 返回 false，由界面给出不可用原因。</summary>
    public bool PageFindAvailable => SafeFind() is not null;

    public bool TryStartFind(string query, out string note)
    {
        note = string.Empty;
        if (SafeFind() is not { } find) { note = "当前 WebView2 运行时不支持页内查找。"; return false; }
        try
        {
            var options = _environment?.CreateFindOptions();
            if (options is null) { note = "当前 WebView2 运行时不支持页内查找。"; return false; }
            options.FindTerm = query ?? string.Empty;
            options.ShouldHighlightAllMatches = true;
            options.SuppressDefaultFindDialog = true;   // 用应用内查找条，不弹 WebView2 默认 Find UI
            // Start 会替换进行中的会话；先 Stop 保证每次从第一个匹配开始。
            try { find.Stop(); } catch (Exception error) when (IsControllerGone(error)) { }
            _ = find.StartAsync(options);
            return true;
        }
        catch (Exception error) when (error is COMException or InvalidOperationException or NotSupportedException or ObjectDisposedException)
        {
            note = "页面查找失败：" + error.Message;
            return false;
        }
    }

    public void FindNext() { try { SafeFind()?.FindNext(); } catch (Exception e) when (IsControllerGone(e)) { } }
    public void FindPrevious() { try { SafeFind()?.FindPrevious(); } catch (Exception e) when (IsControllerGone(e)) { } }
    public void StopFind() { try { SafeFind()?.Stop(); } catch (Exception e) when (IsControllerGone(e)) { } }
    public (int MatchCount, int ActiveMatchIndex)? FindStatus
    {
        get
        {
            if (SafeFind() is not { } find) return null;
            try { return (find.MatchCount, find.ActiveMatchIndex); }
            catch (Exception e) when (IsControllerGone(e)) { return null; }
        }
    }

    /// <summary>缩放因子；WebView2 内部会归一到其支持范围，越界值不在这里静默改写用户输入。</summary>
    public double ZoomFactor
    {
        get
        {
            try { return _controller?.ZoomFactor ?? (_pendingZoomPercent is { } pending ? pending / 100.0 : 1.0); }
            catch (Exception e) when (IsControllerGone(e)) { return 1.0; }
        }
        set => SetZoomPercent(value * 100.0);
    }

    /// <summary>按百分比设置缩放；未就绪时先记住，初始化完成后写入真实 controller。</summary>
    public void SetZoomPercent(double percent)
    {
        if (_controller is { } controller)
        {
            try { controller.ZoomFactor = percent / 100.0; return; }
            catch (Exception e) when (IsControllerGone(e)) { }
        }
        _pendingZoomPercent = percent;
    }

    internal async Task<string?> ExecuteScriptForTestingAsync(string script)
    {
        if (_core is not { } core) return null;
        try { return await core.ExecuteScriptAsync(script); }
        catch (Exception error) when (error is COMException or InvalidOperationException or ObjectDisposedException) { return null; }
    }

    private CoreWebView2Find? SafeFind()
    {
        try { return _core?.Find; }
        catch (Exception error) when (error is COMException or InvalidOperationException or NotSupportedException or ObjectDisposedException) { return null; }
    }

    private bool SafeControllerVisible()
    {
        try { return _controller?.IsVisible ?? false; }
        catch (Exception e) when (IsControllerGone(e)) { return false; }
    }

    private IntPtr SafeControllerParent()
    {
        try { return _controller?.ParentWindow ?? IntPtr.Zero; }
        catch (Exception e) when (IsControllerGone(e)) { return IntPtr.Zero; }
    }

    private static bool IsControllerGone(Exception error) =>
        error is COMException or InvalidOperationException or ObjectDisposedException;

    private void RaiseState(bool loading)
    {
        _loading = loading;
        NavigationStateChanged?.Invoke(this, new BrowserHostNavigationState(
            SafeCanGoBack(), SafeCanGoForward(), loading, SafeSource()));
    }

    private bool SafeCanGoBack() { try { return _core?.CanGoBack ?? false; } catch (Exception e) when (IsControllerGone(e)) { return false; } }
    private bool SafeCanGoForward() { try { return _core?.CanGoForward ?? false; } catch (Exception e) when (IsControllerGone(e)) { return false; } }
    private string? SafeSource() { try { return _core?.Source; } catch (Exception e) when (IsControllerGone(e)) { return null; } }

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
        catch (Exception error) when (IsControllerGone(error)) { }
    }

    private void UpdateVisibility()
    {
        if (_controller is null) return;
        var visible = !_occluded && IsEffectivelyVisible && Bounds.Width > 0 && Bounds.Height > 0 && VisualRoot is not null;
        try { _controller.IsVisible = visible; }
        catch (Exception error) when (IsControllerGone(error)) { }
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
        _releaseCount++;
        _lastReleaseTrace = Environment.StackTrace;
        _lifecycleToken++;   // 让任何在途初始化作废
        _pendingNavigation = null;
        _pendingStop = false;
        foreach (var detach in _coreDetachers) { try { detach(); } catch (Exception e) when (IsControllerGone(e)) { } }
        _coreDetachers.Clear();
        _core = null;
        try { if (_controller is { } controller) { controller.AcceleratorKeyPressed -= OnAcceleratorKeyPressed; controller.Close(); } }
        catch (Exception error) when (IsControllerGone(error)) { }
        _controller = null;
        _environment = null;
        _coreReady?.TrySetResult(null);
        _coreReady = null;
        _initializeRequested = false;
        _initializing = false;
        _loading = false;
        _intendedParentInParent = IntPtr.Zero;
    }

    public void SetOccluded(bool value) { _occluded = value; UpdateVisibility(); }

    /// <summary>通过 Chrome DevTools Protocol 执行 CDP 方法（Browser Use 核心驱动）。</summary>
    public async Task<string?> CallDevToolsProtocolMethodAsync(string methodName, string parametersJson)
    {
        if (_core is null) return null;
        try
        {
            return await _core.CallDevToolsProtocolMethodAsync(methodName, parametersJson);
        }
        catch (Exception ex)
        {
            return "{\"error\":\"" + ex.Message.Replace("\"", "\\\"") + "\"}";
        }
    }

    /// <summary>在页面上下文中执行 JavaScript 脚本并返回结果（支持 DOM 提取与无障碍树）。</summary>
    public async Task<string?> ExecuteScriptAsync(string javaScript)
    {
        if (_core is null) return null;
        try
        {
            return await _core.ExecuteScriptAsync(javaScript);
        }
        catch (Exception ex)
        {
            return "{\"error\":\"" + ex.Message.Replace("\"", "\\\"") + "\"}";
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _attachGeneration++;
        EffectiveViewportChanged -= OnEffectiveViewportChanged;
        ReleaseController();
        RuntimeProbeCompleted = null;
        NavigationStateChanged = null;
        NavigationCompleted = null;
        StatusChanged = null;
        DocumentTitleChanged = null;
        FaviconChanged = null;
        AudioStateChanged = null;
        NewWindowRequested = null;
        ContextMenuActionRequested = null;
        DownloadRequested = null;
        DownloadProgressChanged = null;
        PermissionRequested = null;
        _downloads.Clear();
    }
}
