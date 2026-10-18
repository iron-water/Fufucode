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
    public event EventHandler<string>? StatusChanged;
    public event EventHandler<string>? DocumentTitleChanged;

    /// <summary>页面请求打开一个新窗口/新标签页；宿主决定落到哪个新页签，不再就地覆盖当前页。</summary>
    public event EventHandler<string>? NewWindowRequested;

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
                switch (_pendingNavigation)
                {
                    case (true, var document): _pendingNavigation = null; _core.NavigateToString(document); break;
                    case (false, var address): _pendingNavigation = null; _core.Navigate(address); break;
                    default: _core.Navigate("about:blank"); break;
                }
            }
        }
        catch (Exception error)
        {
            if (!_disposed) StatusChanged?.Invoke(this, "内置浏览器初始化失败：" + error.Message);
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

    private void WireCore(CoreWebView2 core)
    {
        if (core.Settings is { } settings)
        {
            settings.AreHostObjectsAllowed = false;   // 不向页面暴露宿主对象
            settings.IsScriptEnabled = true;          // 网页需要脚本，但本应用自身不注入任何脚本
            settings.AreDefaultScriptDialogsEnabled = true;
        }
        void OnNavigationStarting(object? _, CoreWebView2NavigationStartingEventArgs __) => RaiseState(loading: true);
        void OnHistoryChanged(object? _, object __) => RaiseState(loading: _loading);
        void OnSourceChanged(object? _, CoreWebView2SourceChangedEventArgs __) => RaiseState(loading: _loading);
        void OnTitleChanged(object? _, object __) => DocumentTitleChanged?.Invoke(this, core.DocumentTitle);
        void OnCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e) => OnNavigationCompleted(sender, e);
        void OnNewWindow(object? sender, CoreWebView2NewWindowRequestedEventArgs e) => OnNewWindowRequested(sender, e);
        void OnDownload(object? sender, CoreWebView2DownloadStartingEventArgs e) => OnDownloadStarting(sender, e);

        core.NavigationStarting += OnNavigationStarting;
        core.HistoryChanged += OnHistoryChanged;
        core.SourceChanged += OnSourceChanged;
        core.DocumentTitleChanged += OnTitleChanged;
        core.NavigationCompleted += OnCompleted;
        core.NewWindowRequested += OnNewWindow;
        core.DownloadStarting += OnDownload;

        _coreDetachers.Add(() => core.NavigationStarting -= OnNavigationStarting);
        _coreDetachers.Add(() => core.HistoryChanged -= OnHistoryChanged);
        _coreDetachers.Add(() => core.SourceChanged -= OnSourceChanged);
        _coreDetachers.Add(() => core.DocumentTitleChanged -= OnTitleChanged);
        _coreDetachers.Add(() => core.NavigationCompleted -= OnCompleted);
        _coreDetachers.Add(() => core.NewWindowRequested -= OnNewWindow);
        _coreDetachers.Add(() => core.DownloadStarting -= OnDownload);
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
        StatusChanged?.Invoke(this, e.IsSuccess ? "加载完成" : $"加载失败：{e.WebErrorStatus}");
    }

    /// <summary>页面请求新窗口：交给外层的多页签管理器开新页签，禁止静默覆盖当前页。</summary>
    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (e.Uri is { Length: > 0 } uri) NewWindowRequested?.Invoke(this, uri);
    }

    /// <summary>
    /// 默认不自动下载：取消下载并给出清晰提示，不向系统 Downloads 写文件。
    /// 下载管理（选择目录/进度/恢复）仍不在本包范围。
    /// </summary>
    private void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        e.Cancel = true;    // 不显示默认下载对话框，也不落盘
        e.Handled = true;
        StatusChanged?.Invoke(this, "已阻止自动下载：本版本默认不下载文件，未向系统下载目录写入任何内容。");
    }

    public void Navigate(string? address)
    {
        if (_core is { } core && address is { Length: > 0 }) core.Navigate(address);
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

    public void GoBack() { try { _core?.GoBack(); } catch (Exception e) when (IsControllerGone(e)) { } }
    public void GoForward() { try { _core?.GoForward(); } catch (Exception e) when (IsControllerGone(e)) { } }
    public void Reload() { try { _core?.Reload(); } catch (Exception e) when (IsControllerGone(e)) { } }
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
        StatusChanged = null;
        DocumentTitleChanged = null;
        NewWindowRequested = null;
    }
}
