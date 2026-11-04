using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fufucode.App.Features.BrowserExperience;
using Fufucode.App.Infrastructure;
using Fufucode.App.Infrastructure.Browser;
using Fufucode.App.ViewModels;

namespace Fufucode.App.Views;

/// <summary>从内置浏览器发送到对话的引用：文本 + 可选的标注截图文件。</summary>
public sealed record BrowserChatReference(string Text, string? ImagePath);

/// <summary>标注说明 + 可选的实时样式调整（CSS）。</summary>
public sealed record BrowserAnnotationEdit(string Comment, string Css, bool IncludeScreenshot = false);

/// <summary>
/// 右栏 Preview / 工作区浏览器页：页签栏 + 导航 + 地址 + 查找 + 缩放 + WebView2 宿主。
/// 每个页签持有独立的 BrowserViewHost（独立原生 controller、DOM、历史），
/// 因此切换/关闭页签不会串页或覆盖对方状态。
/// 自带 BrowserViewModel（不继承外层 DataContext），因此可以整体搬到右栏任意位置。
/// </summary>
public partial class BrowserView : UserControl, IDisposable
{
    private readonly BrowserViewModel _viewModel;
    private readonly Grid _hostLayer;
    private readonly Dictionary<string, BrowserViewHost> _hosts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _findStarted = new(StringComparer.Ordinal);
    /// <summary>每个页签查找回调的世代号：关闭/换 query/导航/关 tab/Dispose 都会递增，旧回调不得写回。</summary>
    private readonly Dictionary<string, int> _findGeneration = new(StringComparer.Ordinal);
    /// <summary>记录已向 SDK 发起过 Start 的 (query, 文档世代)；同 query 但换文档必须重新 Start。</summary>
    private readonly Dictionary<string, string> _findDocumentEpoch = new(StringComparer.Ordinal);
    private readonly BrowserDownloadHistory? _downloadHistory;
    private readonly HashSet<string> _recordedDownloads = new(StringComparer.Ordinal);
    private BrowserViewHost? _lastHost;
    private bool _disposed;

    // ---- 内存生命周期（热池 / 后台挂起 / 进程预算） ----
    private readonly BrowserMemoryGovernor _memoryGovernor = new();
    private DispatcherTimer? _lifecycleTimer;
    private Window? _lifecycleWindow;
    private DateTimeOffset? _backgroundSince;
    private bool _lifecycleBusy;
    private bool _forceBackgroundForTesting;
    private string? _userDataFolder;
    private int _activationGeneration;
    private int _annotationGeneration;
    private bool _restoringTabs;
    private DispatcherTimer? _feedbackTimer;

    /// <summary>BRW-1：Agent 控制与 CDP 门禁快照；设置变化时由组合根更新。</summary>
    private BrowserSettingsGate _agentGate = BrowserSettingsGate.Disabled;
    /// <summary>当前生效的浏览器设置快照（地址搜索、下载、权限、UA 都从这里取）。</summary>
    private BrowserSettings _settings = BrowserSettings.Default;
    private BrowserSiteRules _siteRules = new(BrowserSettings.Default);
    private WebView2BrowserUseBridge? _bridge;

    /// <summary>XAML（WorkbenchSections 预览页）实例化入口：不连收藏/历史，但仍连真实设置。</summary>
    public BrowserView() : this(null, null, null) { }

    public BrowserView(BrowserBookmarkStore? bookmarks = null, BrowserHistoryStore? history = null,
        BrowserDownloadHistory? downloads = null)
    {
        _downloadHistory = downloads;
        _viewModel = new BrowserViewModel(bookmarks, history);
        if (downloads is not null) _viewModel.RestoreDownloads(downloads.Entries);
        DataContext = _viewModel;
        InitializeComponent();
        var tabScroll = this.FindControl<ScrollViewer>("BrowserTabScroller")!;
        tabScroll.AddHandler(PointerWheelChangedEvent, (_, e) =>
        {
            var maximum = Math.Max(0, tabScroll.Extent.Width - tabScroll.Viewport.Width);
            if (maximum <= 0) return;
            var delta = e.Delta.X != 0 ? e.Delta.X : e.Delta.Y;
            tabScroll.Offset = new Avalonia.Vector(Math.Clamp(tabScroll.Offset.X - delta * 48, 0, maximum), 0);
            e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _hostLayer = this.FindControl<Grid>("HostLayer")
                     ?? throw new InvalidOperationException("BrowserView 缺少 WebView2 宿主层（x:Name=HostLayer）。");
        if (this.FindControl<TextBox>("AddressBox") is { } addressBox) addressBox.TextChanged += Address_TextChanged;
        SizeChanged += (_, _) => UpdateBrowserLayout();
        EffectiveViewportChanged += OnBrowserEffectiveViewportChanged;
        UpdateBrowserLayout();
        Fufucode.App.Integration.MemoryProfile.VisualEffectsChanged += UpdateBrowserMotion;
        PropertyChanged += (_, args) =>
        {
            if (args.Property == IsVisibleProperty) { UpdateBackgroundState(); UpdateHostVisibility(); }
        };
        AddHandler(KeyDownEvent, Browser_KeyDown, RoutingStrategies.Tunnel);
        WireBrowserPopup("HistoryPopup", "HistorySurface", "HistorySearch");
        WireBrowserPopup("DownloadsPopup", "DownloadsSurface", null);
        WireBrowserIme("AddressBox", _addressIme);
        WireBrowserIme("FindBox", _findIme);

        _viewModel.TabNavigationRequested += OnNavigationRequested;
        _viewModel.TabActionRequested += OnActionRequested;
        _viewModel.ZoomRequested += OnZoomRequested;
        _viewModel.TabCreated += OnTabCreated;
        _viewModel.TabClosed += OnTabClosed;
        _viewModel.PropertyChanged += OnTabSelectionChanged;
        _viewModel.AddressCopyRequested += OnAddressCopyRequested;
        _viewModel.ExternalOpenRequested += OnExternalOpenRequested;
        _viewModel.ShareRequested += OnShareRequested;

        // 首个页签的宿主先建好，保持与旧行为一致（构造完成即返回，不触碰运行时）。
        HostFor(_viewModel.ActiveTab);
        UpdateHostVisibility();

        AttachedToVisualTree += (_, _) => AttachLifecycle();
        DetachedFromVisualTree += (_, _) => DetachLifecycle(stopTimer: false);
    }

    private void AttachLifecycle()
    {
        if (_lifecycleTimer is null)
        {
            _lifecycleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _lifecycleTimer.Tick += (_, _) => _ = RunLifecycleAsync();
            _lifecycleTimer.Start();
        }
        if (_lifecycleWindow is not null) _lifecycleWindow.PropertyChanged -= OnLifecycleWindowPropertyChanged;
        _lifecycleWindow = TopLevel.GetTopLevel(this) as Window;
        if (_lifecycleWindow is not null)
            _lifecycleWindow.PropertyChanged += OnLifecycleWindowPropertyChanged;
        UpdateBackgroundState();
        UpdateHostVisibility();
        if (!IsBrowserBackground) _ = ActivateTabAsync(_viewModel.ActiveTab);
    }

    private void DetachLifecycle(bool stopTimer = true)
    {
        if (stopTimer)
        {
            _lifecycleTimer?.Stop();
            _lifecycleTimer = null;
        }
        if (_lifecycleWindow is not null)
            _lifecycleWindow.PropertyChanged -= OnLifecycleWindowPropertyChanged;
        _lifecycleWindow = null;
        // 页面被停靠区收起/浮动窗隐藏时没有 Window 可观察，按后台处理，定时器继续跑。
        if (!stopTimer) _backgroundSince ??= DateTimeOffset.UtcNow;
    }

    private void OnBrowserEffectiveViewportChanged(object? sender, EffectiveViewportChangedEventArgs e)
    {
        if (_disposed) return;
        UpdateBackgroundState();
        UpdateHostVisibility();
    }

    private void OnLifecycleWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty || e.Property == Visual.IsVisibleProperty)
            UpdateBackgroundState();
    }

    private bool IsBrowserBackground => _forceBackgroundForTesting || _lifecycleWindow is null ||
        !IsEffectivelyVisible || !_lifecycleWindow.IsVisible || _lifecycleWindow.WindowState == WindowState.Minimized;

    private void UpdateBackgroundState()
    {
        var background = IsBrowserBackground;
        if (background)
        {
            _activationGeneration++;
            _backgroundSince ??= DateTimeOffset.UtcNow;
            foreach (var host in _hosts.Values) host.SetOccluded(true);
        }
        else if (_backgroundSince is not null)
        {
            _backgroundSince = null;
            _ = RestoreActiveAfterBackgroundAsync();
        }
    }

    private string UserDataFolder => _userDataFolder ??=
        BrowserUserDataLayout.Resolve(Program.SelfTestMode ? Program.SelfTestDataRoot : null);

    private async Task RestoreActiveAfterBackgroundAsync()
    {
        var active = _viewModel.ActiveTab;
        if (_disposed) return;
        await ActivateTabAsync(active);
    }

    /// <summary>选中页签：只有当前页签允许创建 controller；被丢弃的页签按需重建并恢复滚动位置。</summary>
    private async Task ActivateTabAsync(BrowserTab tab)
    {
        if (_disposed || _restoringTabs || !ReferenceEquals(tab, _viewModel.ActiveTab) || !_viewModel.Tabs.Contains(tab)) return;
        var generation = ++_activationGeneration;
        tab.Touch();
        var host = HostFor(tab);
        UpdateHostVisibility();
        if (IsBrowserBackground) return;
        host.SetActive();
        if (!host.HasController && string.IsNullOrWhiteSpace(tab.NavigationAddress) && !tab.IsFileMode) return;
        bool Current() => !_disposed && generation == _activationGeneration &&
            ReferenceEquals(tab, _viewModel.ActiveTab) && _viewModel.Tabs.Contains(tab) && !IsBrowserBackground;
        try
        {
            if (!host.HasController)
            {
                tab.IsRestoring = true;
                tab.IsLoading = true;
                UpdateHostVisibility();
                var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnCompleted(object? sender, BrowserHostNavigationCompleted state) => completed.TrySetResult();
                host.NavigationCompleted += OnCompleted;
                try
                {
                    PrepareTabNavigation(tab, host);
                    if (tab.HasPageError) return;
                    var core = await host.EnsureCoreAsync().WaitAsync(TimeSpan.FromSeconds(15));
                    if (!Current() || core is null) return;
                    await completed.Task.WaitAsync(TimeSpan.FromSeconds(15));
                    if (!Current()) return;
                    if (!tab.HasPageError && (Math.Abs(tab.ScrollX) > 0.5 || Math.Abs(tab.ScrollY) > 0.5))
                        await host.RestoreScrollAsync(tab.ScrollX, tab.ScrollY);
                    if (!Current()) return;
                }
                finally { host.NavigationCompleted -= OnCompleted; }
            }
            if (!Current()) return;
            tab.ApplyLifecycle(BrowserTabLifecycle.Active);
            UpdateHostVisibility();
            await EnforceHotPoolAsync(tab);
        }
        catch (TimeoutException)
        {
            if (Current())
            {
                host.DiscardController();
                tab.PageError = "页面恢复超时";
                tab.IsLoading = false;
                tab.ApplyLifecycle(BrowserTabLifecycle.Discarded);
            }
        }
        finally
        {
            if (generation == _activationGeneration)
            {
                tab.IsRestoring = false;
                UpdateHostVisibility();
            }
        }
    }

    private static void PrepareTabNavigation(BrowserTab tab, BrowserViewHost host)
    {
        if (tab.IsFileMode)
        {
            var preview = BrowserFilePreview.Inspect(tab.CurrentFilePath);
            if (preview.Document is not null) host.NavigateToDocument(preview.Document);
            else if (preview.Address is not null) host.Navigate(preview.Address);
            else { tab.PageError = preview.Note ?? "文件已不可用"; tab.IsLoading = false; }
        }
        else if (!string.IsNullOrWhiteSpace(tab.NavigationAddress)) host.Navigate(tab.NavigationAddress);
    }

    private async Task EnforceHotPoolAsync(BrowserTab active)
    {
        var loaded = _hosts
            .Select(entry => (Tab: _viewModel.Tabs.FirstOrDefault(tab => tab.Id == entry.Key), Host: entry.Value))
            .Where(item => item.Tab is not null && item.Host.HasController)
            .OrderBy(item => item.Tab!.LastAccessUtc)
            .ToList();
        var over = loaded.Count - Math.Max(1, _memoryGovernor.HotLimit);
        if (over <= 0) return;
        foreach (var item in loaded)
        {
            if (over <= 0) break;
            if (ReferenceEquals(item.Tab, active)) continue;
            await DiscardTabAsync(item.Tab!);
            if (!item.Host.HasController) over--;
        }
    }

    private async Task DiscardTabAsync(BrowserTab tab)
    {
        if (!_hosts.TryGetValue(tab.Id, out var host)) { tab.ApplyLifecycle(BrowserTabLifecycle.Discarded); return; }
        if (tab.IsPlayingAudio || host.HasActiveDownloads) return;
        var accessed = tab.LastAccessUtc;
        var generation = _activationGeneration;
        if (host.HasController)
        {
            (double X, double Y)? scroll = null;
            try { scroll = await host.CaptureScrollAsync().WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (TimeoutException) { return; }
            if (_disposed || !_viewModel.Tabs.Contains(tab) || accessed != tab.LastAccessUtc || generation != _activationGeneration) return;
            if (scroll is { } position) { tab.ScrollX = position.X; tab.ScrollY = position.Y; }
            host.DiscardController();
        }
        tab.IsLoading = false;
        tab.IsRestoring = false;
        tab.ApplyLifecycle(BrowserTabLifecycle.Discarded);
    }

    private async Task SuspendTabAsync(BrowserTab tab)
    {
        if (!_hosts.TryGetValue(tab.Id, out var host) || tab.IsPlayingAudio || host.HasActiveDownloads) return;
        var generation = _activationGeneration;
        var suspended = await host.TrySuspendAsync();
        if (_disposed || generation != _activationGeneration || !_viewModel.Tabs.Contains(tab)) return;
        tab.ApplyLifecycle(suspended ? BrowserTabLifecycle.Suspended : BrowserTabLifecycle.Warm);
    }

    /// <summary>每 30 秒执行一次：后台挂起/丢弃 + 热池 LRU + 进程预算；采样在后台线程。</summary>
    private async Task RunLifecycleAsync()
    {
        if (_disposed || _lifecycleBusy) return;
        _lifecycleBusy = true;
        UpdateBrowserMotion();
        try
        {
            var now = DateTimeOffset.UtcNow;
            var background = IsBrowserBackground;
            if (background) _backgroundSince ??= now; else _backgroundSince = null;

            var tabs = _viewModel.Tabs
                .Select(tab => new BrowserTabMemoryInfo(tab.Id, tab.Lifecycle, tab.LastAccessUtc,
                    ReferenceEquals(tab, _viewModel.ActiveTab)))
                .ToArray();
            BrowserProcessSnapshot snapshot;
            try
            {
                // GetProcessInfos 必须在 UI 线程；逐进程内存读取放到后台。
                var infos = BrowserRuntime.GetProcessInfos(UserDataFolder);
                snapshot = infos.Count > 0
                    ? await Task.Run(() => BrowserRuntime.SampleProcesses(infos)).WaitAsync(TimeSpan.FromSeconds(3))
                    : BrowserRuntime.SnapshotAfterEnvironmentTeardown();
            }
            catch (Exception error) when (error is TimeoutException or InvalidOperationException)
            {
                snapshot = BrowserProcessSnapshot.Empty;
            }
            if (_disposed) return;

            MemorySnapshotForTesting = snapshot;
            var generation = _activationGeneration;
            var decision = _memoryGovernor.Evaluate(tabs, snapshot.TotalPrivateBytes, now, background, _backgroundSince);
            LastDecisionForTesting = decision;
            if (decision.IsEmpty) return;

            foreach (var id in decision.Discard)
            {
                if (_disposed || generation != _activationGeneration) return;
                var tab = _viewModel.Tabs.FirstOrDefault(candidate => candidate.Id == id);
                if (tab is not null && !ReferenceEquals(tab, _viewModel.ActiveTab)) await DiscardTabAsync(tab);
            }
            foreach (var id in decision.Suspend)
            {
                if (_disposed || generation != _activationGeneration) return;
                var tab = _viewModel.Tabs.FirstOrDefault(candidate => candidate.Id == id);
                if (tab is not null && !ReferenceEquals(tab, _viewModel.ActiveTab)) await SuspendTabAsync(tab);
            }
            if (decision.DiscardActive && IsBrowserBackground && generation == _activationGeneration)
                await DiscardTabAsync(_viewModel.ActiveTab);
            MemorySnapshotForTesting = snapshot;
            LastDecisionForTesting = decision;
        }
        finally { _lifecycleBusy = false; }
    }

    internal BrowserProcessSnapshot MemorySnapshotForTesting { get; private set; } = BrowserProcessSnapshot.Empty;
    internal BrowserMemoryDecision LastDecisionForTesting { get; private set; } = BrowserMemoryDecision.Empty;
    internal Task RunLifecycleForTestingAsync() => RunLifecycleAsync();
    internal async Task RunLifecycleForTestingAsync(DateTimeOffset? backgroundSince)
    {
        _forceBackgroundForTesting = backgroundSince is not null;
        _backgroundSince = backgroundSince;
        try { await RunLifecycleAsync(); }
        finally { _forceBackgroundForTesting = false; }
    }
    internal Task ActivateTabForTestingAsync(BrowserTab tab) => ActivateTabAsync(tab);
    internal void RefreshNativeVisibilityForTesting() => UpdateHostVisibility();
    internal Task DiscardTabForTestingAsync(BrowserTab tab) => DiscardTabAsync(tab);
    internal Task SuspendTabForTestingAsync(BrowserTab tab) => SuspendTabAsync(tab);
    internal BrowserProcessSnapshot SampleProcessesForTesting() => BrowserRuntime.Sample(UserDataFolder);
    internal BrowserMemoryGovernor MemoryGovernorForTesting => _memoryGovernor;

    public BrowserViewModel ViewModel => _viewModel;

    /// <summary>当前 Agent 控制门禁（供组合根读取，判断是否需要注册 Browser Use 工具）。</summary>
    internal BrowserSettingsGate AgentGateForTesting => _agentGate;

    /// <summary>
    /// 应用用户设置：同步“显示完整网址”，并记录 Agent 控制/完整 CDP 门禁。
    /// 桥只创建一次；关闭开关后桥仍在，但每次调用都会再次校验门禁并如实拒绝。
    /// </summary>
    public void ApplyAgentControl(BrowserSettings settings, BrowserSettingsStore? store, BrowserSettingsViewModel? settingsViewModel)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings.Normalize();
        _siteRules = new BrowserSiteRules(_settings);
        _viewModel.ApplySiteRules(_siteRules);
        _viewModel.ApplyShowFullUrl(_settings.ShowFullUrl);
        _viewModel.ApplySearchUrlTemplate(_settings.SearchUrlTemplate);
        _memoryGovernor.BudgetBytes = Math.Max(128L, _settings.BrowserMemoryBudgetMb) * 1024 * 1024;
        _memoryGovernor.HotLimit = _settings.BrowserHotTabLimit;
        _memoryGovernor.BackgroundSuspendAfter = TimeSpan.FromMinutes(_settings.BrowserBackgroundSuspendMinutes);
        _memoryGovernor.BackgroundDiscardAfter = TimeSpan.FromMinutes(_settings.BrowserBackgroundDiscardMinutes);
        _memoryGovernor.BackgroundDiscardAllAfter = TimeSpan.FromMinutes(_settings.BrowserBackgroundDiscardAllMinutes);
        _agentGate = new BrowserSettingsGate(_settings.BrowserUseEnabled, _settings.EnableFullCdpAccess);
        foreach (var host in _hosts.Values)
        {
            host.SetUserAgent(_settings.UserAgent);
            host.SetAutofill(_settings.EnableAutofill);
            host.SetPasswordAutosave(_settings.EnablePasswordAutosave);
            host.SetOpenLinksExternally(_siteRules.OpensWebExternally);
        }
        _bridge ??= new WebView2BrowserUseBridge(() => this, () => _agentGate);
        BrowserAgentBridgeChanged?.Invoke(this, _bridge);
    }

    /// <summary>当前生效设置（真实来源：browser-settings.json）；只读快照。</summary>
    public BrowserSettings CurrentSettings => _settings;

    /// <summary>站点权限判定（白名单/默认权限）；供下载与导航使用。</summary>
    public BrowserSiteRules SiteRules => _siteRules;

    /// <summary>接线智能体动作的人工审批通道（由 MainWindow 接到确认卡片）。</summary>
    public void SetAgentApprovalHandler(Func<BrowserApprovalRequest, System.Threading.CancellationToken, Task<bool>>? handler)
    {
        if (_bridge is not null) _bridge.ApprovalHandler = handler;
    }

    /// <summary>桥与门禁变化通知；组合根据此把真实桥接进插件工具注册表。</summary>
    internal event EventHandler<WebView2BrowserUseBridge>? BrowserAgentBridgeChanged;

    /// <summary>当前桥（未应用设置时为 null）。</summary>
    internal WebView2BrowserUseBridge? BridgeForTesting => _bridge;

    /// <summary>把网页引用/标注发送到当前对话；由 MainWindow 接线到聊天输入。</summary>
    public event EventHandler<BrowserChatReference>? ChatReferenceRequested;

    /// <summary>右键菜单动作：复制地址、外部打开、发送页面/选区到对话、进入标注。</summary>
    private async void HandleContextMenuAction(BrowserTab tab, BrowserViewHost host, string action)
    {
        switch (action)
        {
            case "copy-address":
                if (tab.NavigationAddress is { Length: > 0 } address) await CopyAddressAsync(address);
                else _viewModel.ReportStatusTo(tab, "当前没有可复制的地址。");
                break;
            case "open-external":
                if (tab.NavigationAddress is { Length: > 0 } open) await OpenExternalAsync(open);
                else _viewModel.ReportStatusTo(tab, "当前没有可外部打开的地址。");
                break;
            case "send-page":
                await SendPageToChatAsync(tab, host, selectionOnly: false);
                break;
            case "send-selection":
                await SendPageToChatAsync(tab, host, selectionOnly: true);
                break;
            case "annotate":
                await StartAnnotationAsync(tab, host);
                break;
        }
    }

    /// <summary>读取真实标题/地址/选区，组装成可粘贴到对话的引用文本。</summary>
    private async Task SendPageToChatAsync(BrowserTab tab, BrowserViewHost host, bool selectionOnly)
    {
        var raw = await host.ExecuteScriptAsync(
            "JSON.stringify({title:document.title||'',url:location.href||'',selection:(window.getSelection()?.toString()||'')})");
        if (string.IsNullOrWhiteSpace(raw))
        {
            _viewModel.ReportStatusTo(tab, "无法读取当前页面的内容。");
            return;
        }
        string title = string.Empty, url = tab.NavigationAddress ?? string.Empty, selection = string.Empty;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(UnquoteJson(raw));
            var root = doc.RootElement;
            if (root.TryGetProperty("title", out var t)) title = t.GetString() ?? string.Empty;
            if (root.TryGetProperty("url", out var u)) url = u.GetString() ?? url;
            if (root.TryGetProperty("selection", out var s)) selection = s.GetString() ?? string.Empty;
        }
        catch (System.Text.Json.JsonException) { }

        if (selectionOnly && string.IsNullOrWhiteSpace(selection))
        {
            _viewModel.ReportStatusTo(tab, "没有选中文字；请先在页面里选中内容再右键。");
            return;
        }
        var text = new System.Text.StringBuilder();
        text.AppendLine("【网页引用】");
        if (title.Length > 0) text.AppendLine("标题：" + title);
        text.AppendLine("地址：" + url);
        if (selection.Length > 0) text.AppendLine("选中内容：\n" + selection.Trim());
        if (_disposed || !_viewModel.Tabs.Contains(tab) || !ReferenceEquals(tab, _viewModel.ActiveTab)) return;
        if (ChatReferenceRequested is { } receiver)
        {
            receiver.Invoke(this, new BrowserChatReference(text.ToString().TrimEnd(), null));
            _viewModel.ReportStatusTo(tab, "已发送到对话输入框。");
        }
        else await CopyAddressAsync(text.ToString().TrimEnd());
    }

    /// <summary>
    /// 下载决策：站点权限 block 直接拒绝；approval 或“下载前询问”弹保存对话框；
    /// allow 且配置了默认目录时直接落到该目录，否则保留 WebView2 的默认下载位置。
    /// 所有路径都来自真实文件系统检查，不伪造成功。
    /// </summary>
    /// <summary>
    /// 弹窗处理：按设置可改投系统浏览器；否则开新页签并把新页签的真实 CoreWebView2 交回。
    /// 不覆盖原页签，也不伪造 opener 关系。
    /// </summary>
    private async Task HandleNewWindowAsync(BrowserNewWindowRequest request)
    {
        if (_siteRules.OpensWebExternally && Uri.TryCreate(request.Uri, UriKind.Absolute, out _))
        {
            await OpenExternalAsync(request.Uri);
            request.Resolve(null);
            return;
        }
        var created = _viewModel.CreateTab();
        var createdHost = HostFor(created);
        UpdateHostVisibility();
        // 目标导航由 WebView2 在赋 NewWindow 后发起，保留 opener 与初始脚本且不重复请求。
        createdHost.SetActive();
        Microsoft.Web.WebView2.Core.CoreWebView2? core = null;
        try { core = await createdHost.EnsureCoreAsync().WaitAsync(TimeSpan.FromSeconds(9)); }
        catch (TimeoutException) { }
        request.Resolve(core);
        if (core is null) _viewModel.ReportStatusTo(created, "新窗口未能建立浏览器运行时，已保留为新页签。");
    }

    private async Task HandleDownloadRequestAsync(BrowserDownloadRequest request)
    {
        try
        {
            var permission = _siteRules.DownloadPermissionFor(request.SourceUri);
            if (string.Equals(permission, "block", StringComparison.OrdinalIgnoreCase))
            {
                request.Resolve(BrowserDownloadDecision.Deny("站点下载权限为“阻止”，未下载任何文件。"));
                return;
            }

            var fileName = SanitizeFileName(request.FileName);
            string? targetPath = null;
            var needsPrompt = string.Equals(permission, "approval", StringComparison.OrdinalIgnoreCase) ||
                              _settings.PromptDownloadLocation;
            if (needsPrompt)
            {
                targetPath = await PickDownloadPathAsync(fileName);
                if (targetPath is null)
                {
                    request.Resolve(BrowserDownloadDecision.Deny("用户取消了下载。"));
                    return;
                }
            }
            else if (!string.IsNullOrWhiteSpace(_settings.DefaultDownloadDirectory))
            {
                var folder = Path.GetFullPath(_settings.DefaultDownloadDirectory);
                Directory.CreateDirectory(folder);
                targetPath = UniquePath(Path.Combine(folder, fileName));
            }
            request.Resolve(BrowserDownloadDecision.Allow(targetPath));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            request.Resolve(BrowserDownloadDecision.Deny("下载路径不可用：" + error.Message));
        }
    }

    private async Task<string?> PickDownloadPathAsync(string fileName)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is not { } storage) return null;
        var extension = Path.GetExtension(fileName);
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "保存下载文件",
            SuggestedFileName = fileName,
            DefaultExtension = extension.Length > 1 ? extension[1..] : null,
            ShowOverwritePrompt = true,
        });
        return file?.TryGetLocalPath();
    }

    private static string SanitizeFileName(string? fileName)
    {
        var name = Path.GetFileName(fileName ?? string.Empty);
        foreach (var invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
        return string.IsNullOrWhiteSpace(name) ? "download.bin" : name;
    }

    /// <summary>同名文件已存在时追加 "(1)"、"(2)"，不覆盖已有文件。</summary>
    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var folder = Path.GetDirectoryName(path) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var index = 1; index < 10_000; index++)
        {
            var candidate = Path.Combine(folder, $"{name} ({index}){extension}");
            if (!File.Exists(candidate)) return candidate;
        }
        return path;
    }

    private static string PermissionLabel(string uri, string kind) => kind switch
    {
        "Camera" => "使用摄像头",
        "Microphone" => "使用麦克风",
        "Geolocation" => "获取你的位置",
        "Notifications" => "发送通知",
        "ClipboardRead" => "读取剪贴板",
        "Autoplay" => "自动播放媒体",
        "MultipleAutomaticDownloads" => "连续下载多个文件",
        _ => "使用 " + kind,
    };

    /// <summary>
    /// 站点权限：站点被 block 时直接拒绝；没有规则时询问用户（记住本进程内的决定），
    /// 规则为 allow 时放行且写入 profile。
    /// </summary>
    private async Task HandlePermissionRequestAsync(BrowserPermissionRequest request)
    {
        var site = _siteRules.PermissionFor(request.Uri);
        if (string.Equals(site, "block", StringComparison.OrdinalIgnoreCase))
        {
            request.Resolve(BrowserPermissionDecision.Deny($"{request.Host} 的站点权限为“禁止访问”，已拒绝{PermissionLabel(request.Uri, request.Kind)}。"));
            return;
        }
        // 智能体浏览站点许可不授予摄像头、麦克风等网页原生权限。
        var remembered = _siteRules.PermissionDecision(request.Host, request.Kind);
        if (remembered is { } known)
        {
            request.Resolve(known ? BrowserPermissionDecision.Once() : BrowserPermissionDecision.Deny());
            return;
        }

        // 需要批准：非用户发起的请求（后台脚本自动触发）一律先拒绝，避免无提示的静默授权。
        if (!request.IsUserInitiated)
        {
            request.Resolve(BrowserPermissionDecision.Deny("已拒绝后台自动触发的权限请求；请在页面中主动操作后重试。"));
            return;
        }

        var decision = await AskPermissionAsync(request, PermissionLabel(request.Uri, request.Kind));
        if (_disposed || request.Decision.IsCompleted) return;
        _siteRules.RememberPermission(request.Host, request.Kind, decision.Allow);
        request.Resolve(decision);
    }

    private async Task<BrowserPermissionDecision> AskPermissionAsync(BrowserPermissionRequest request, string label)
    {
        if (_disposed || request.Decision.IsCompleted) return BrowserPermissionDecision.Deny();
        var owner = TopLevel.GetTopLevel(this) as Window;
        var dialog = new Window
        {
            Title = "站点权限", Classes = { "browserDialog" }, Width = 400,
            SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var panel = new StackPanel { Classes = { "browserDialogBody" } };
        panel.Children.Add(new TextBlock { Text = request.Host, Classes = { "browserHeading" } });
        panel.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        var deny = new Button { Content = "拒绝" };
        var allow = new Button { Content = "本次运行允许" };
        buttons.Children.Add(deny);
        buttons.Children.Add(allow);
        panel.Children.Add(buttons);
        dialog.Content = panel;
        var completion = new TaskCompletionSource<BrowserPermissionDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        deny.Click += (_, _) => { completion.TrySetResult(BrowserPermissionDecision.Deny()); dialog.Close(); };
        allow.Click += (_, _) => { completion.TrySetResult(BrowserPermissionDecision.Once()); dialog.Close(); };
        dialog.Closed += (_, _) => completion.TrySetResult(BrowserPermissionDecision.Deny());
        if (owner is not null) _ = dialog.ShowDialog(owner); else dialog.Show();
        await Task.WhenAny(completion.Task, request.Decision);
        dialog.Close();
        return completion.Task.IsCompleted ? await completion.Task : BrowserPermissionDecision.Deny();
    }

    private void ApplyDownloadProgress(BrowserDownloadProgress progress)
    {
        _viewModel.ApplyDownloadProgress(progress);
        if (progress.State != BrowserDownloadState.Completed || _recordedDownloads.Contains(progress.Id)) return;
        if (_settings.DownloadHistoryEnabled && _downloadHistory is not null)
        {
            _downloadHistory.Add(new BrowserDownloadEntry(
                progress.FileName,
                progress.ResultFilePath,
                progress.TotalBytes,
                "completed",
                DateTimeOffset.Now));
            _recordedDownloads.Add(progress.Id);
        }
    }

    private static string UnquoteJson(string raw)
    {
        var text = raw.Trim();
        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"')
        {
            try { return JsonSerializer.Deserialize<string>(text) ?? text; }
            catch (JsonException) { }
        }
        return text;
    }

    /// <summary>
    /// Agent 请求导航：走与用户点击同一套规范化与宿主调用，不绕过地址白名单。
    /// 导航请求是同步派发给宿主的，因此这里不做额外的异步等待，避免 sync-over-async 死锁。
    /// </summary>
    internal bool NavigateFromAgent(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        _viewModel.ActiveTab.AddressText = url;
        _viewModel.NavigateCommand.Execute(null);
        return _viewModel.IsLoading || _viewModel.ActiveTab.NavigationAddress is { Length: > 0 };
    }
    internal Func<uint, KeyModifiers, bool>? NativeShortcutHandler { get; set; }
    /// <summary>测试用：优先返回当前活动页签的宿主；已释放后返回最近一个真实宿主（不再新建）。</summary>
    internal BrowserViewHost HostForTesting =>
        _hosts.TryGetValue(_viewModel.ActiveTab.Id, out var host) ? host : _lastHost ?? HostFor(_viewModel.ActiveTab);
    internal BrowserViewHost HostForTabForTesting(BrowserTab tab) => HostFor(tab);
    internal int HostCountForTesting => _hosts.Count;
    internal IReadOnlyList<BrowserTab> TabsForTesting => _viewModel.Tabs.ToList();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _activationGeneration++;
        CancelAnnotation();
        _feedbackTimer?.Stop();
        Fufucode.App.Integration.MemoryProfile.VisualEffectsChanged -= UpdateBrowserMotion;
        foreach (var detach in _imeDetachers) detach();
        _imeDetachers.Clear();
        _viewModel.IsHistoryOpen = _viewModel.IsDownloadsOpen = _viewModel.IsSuggestionsOpen = _viewModel.IsFeedbackOpen = false;
        DetachLifecycle(stopTimer: true);
        EffectiveViewportChanged -= OnBrowserEffectiveViewportChanged;
        _viewModel.TabNavigationRequested -= OnNavigationRequested;
        _viewModel.TabActionRequested -= OnActionRequested;
        _viewModel.ZoomRequested -= OnZoomRequested;
        _viewModel.TabCreated -= OnTabCreated;
        _viewModel.TabClosed -= OnTabClosed;
        _viewModel.PropertyChanged -= OnTabSelectionChanged;
        _viewModel.AddressCopyRequested -= OnAddressCopyRequested;
        _viewModel.ExternalOpenRequested -= OnExternalOpenRequested;
        _viewModel.ShareRequested -= OnShareRequested;
        foreach (var tabId in _hosts.Keys.ToArray()) InvalidateFind(tabId, resetDocumentEpoch: true);
        foreach (var host in _hosts.Values) host.Dispose();
        _hosts.Clear();
        _findStarted.Clear();
        _findGeneration.Clear();
        _findDocumentEpoch.Clear();
        _viewModel.Dispose();
    }

    internal BrowserPageState CaptureState()
    {
        var tabs = _viewModel.Tabs.Select(tab => tab.CaptureState()).ToArray();
        return new BrowserPageState(BrowserPageState.CurrentVersion, _viewModel.AddressText,
            _viewModel.ActiveTab.NavigationAddress, _viewModel.CurrentFilePath,
            _viewModel.ActiveTab.Id, tabs);
    }

    internal void RestoreState(BrowserPageState? state)
    {
        if (state is null) return;
        if (state.Version is not (1 or 2 or 3))
        {
            _viewModel.ResetTabs(new[] { _viewModel.ActiveTab.Id });
            _viewModel.AddressText = "";
            _viewModel.ActiveTab.NavigateCommand.Execute(null);
            _viewModel.ReportStatus("浏览器页面状态版本不受支持，已恢复空页。");
            return;
        }
        if (state.Version == 1)
        {
            _viewModel.ActiveTab.Restore(new BrowserTabState(_viewModel.ActiveTab.Id,
                state.AddressDraft ?? string.Empty, state.NavigationAddress, state.FilePath, string.Empty));
            _ = ActivateTabAsync(_viewModel.ActiveTab);
            return;
        }
        // 过滤 null/空白记录并成对归一化 ID：重复 ID 保留首个合法条目，后续重命名而不是丢弃。
        var savedTabs = NormalizeTabs(state.Tabs, state);
        // 整体重建页签：旧页签的宿主被释放，新页签按保存的 id 建独立宿主。
        _restoringTabs = true;
        _activationGeneration++;
        try
        {
            var restored = _viewModel.ResetTabs(savedTabs.Select(tab => tab.Id).ToArray());
            for (var index = 0; index < savedTabs.Count; index++) restored[index].Restore(savedTabs[index]);
            _viewModel.SelectTab(restored.FirstOrDefault(tab => tab.Id == state.ActiveTabId) ?? restored[0]);
        }
        finally { _restoringTabs = false; }
        UpdateHostVisibility();
        _ = ActivateTabAsync(_viewModel.ActiveTab);
    }

    /// <summary>
    /// 把持久化页签记录归一化成“合法且 ID 唯一”的列表：
    /// 过滤 null/空白条目，重复 ID 的首个条目保留原 ID，其余重命名；至少保留一个可恢复条目。
    /// 不因个别坏数据丢弃全部配置。
    /// </summary>
    private static IReadOnlyList<BrowserTabState> NormalizeTabs(IReadOnlyList<BrowserTabState>? tabs, BrowserPageState state)
    {
        var entries = (tabs ?? Array.Empty<BrowserTabState>()).Where(tab => tab is not null).ToList();
        // 先预留全部合法原 ID，生成的替代 ID 不得抢占后面合法原条目的身份。
        var reserved = new HashSet<string>(entries
            .Where(tab => !string.IsNullOrWhiteSpace(tab.Id))
            .Select(tab => tab.Id!), StringComparer.Ordinal);
        var result = new List<BrowserTabState>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        var counter = 0;
        foreach (var tab in entries)
        {
            var id = tab.Id;
            if (string.IsNullOrWhiteSpace(id) || !used.Add(id))
            {
                // 空 ID 或重复 ID：重命名到既未占用、也不与任何合法原 ID 冲突的稳定 ID，保留该条目的草稿/地址。
                do { id = "tab-restored-" + (++counter).ToString(); } while (reserved.Contains(id) || !used.Add(id));
            }
            result.Add(tab with { Id = id });
        }
        if (result.Count == 0)
        {
            // 空列表或全部为坏记录：留下一个可用的空页，同时保留 v1 的顶层字段（若有）。
            result.Add(new BrowserTabState("tab-restored-1", state.AddressDraft ?? "", state.NavigationAddress, state.FilePath, ""));
        }
        return result;
    }

    /// <summary>直接向当前页签推送本地路径进入文件预览模式。</summary>
    public void PreviewFile(string? path) => _viewModel.PreviewFile(path);

    /// <summary>Ctrl+L：聚焦并全选地址栏。</summary>
    public void FocusAddress()
    {
        if (this.FindControl<TextBox>("AddressBox") is not { } box) return;
        box.Focus();
        box.SelectAll();
    }

    /// <summary>seam 上声明的 x:Name，D 轨据此用 TryPreviewFile 定位（见 docs/archive/progress/progress-e.md）。</summary>
    public const string PreviewControlName = "PreviewBrowser";

    /// <summary>
    /// E 侧入口：D 轨在 Files tab 选中文件时调用，把本地路径推给内置浏览器预览。
    /// 右栏未挂载或 Preview 未启用时返回 false 且不抛异常——调用方据此决定是否给用户提示，
    /// 不要改成抛异常（右栏可整体收起，这是正常状态而非错误）。
    /// </summary>
    public static bool TryPreviewFile(Control? panel, string? path)
    {
        if (panel is null || string.IsNullOrWhiteSpace(path)) return false;
        BrowserView? view;
        try
        {
            // FindControl 在没有 NameScope 的控件上会抛 InvalidOperationException 而不是返回 null，
            // 右栏可整体收起/尚未初始化，这里统一按“不可用”处理。
            view = Avalonia.Controls.NameScope.GetNameScope(panel) is null
                ? null
                : panel.FindControl<BrowserView>(PreviewControlName);
        }
        catch (InvalidOperationException) { return false; }
        if (view is null) return false;
        view.PreviewFile(path);
        return true;
    }

    private BrowserViewHost HostFor(BrowserTab tab)
    {
        if (_hosts.TryGetValue(tab.Id, out var existing)) return existing;
        var host = new BrowserViewHost();
        host.SetOccluded(true);
        host.SetUserAgent(_settings.UserAgent);
        host.SetAutofill(_settings.EnableAutofill);
        host.SetPasswordAutosave(_settings.EnablePasswordAutosave);
        host.SetOpenLinksExternally(_siteRules.OpensWebExternally);
        host.OpenExternallyRequested += (_, address) => _ = OpenExternalAsync(address);
        host.AcceleratorHandler = (key, modifiers) => NativeShortcutHandler?.Invoke(key, modifiers) == true;
        host.NavigationStateChanged += (_, state) =>
        {
            if (_disposed) return;
            _viewModel.ApplyNavigationStateTo(tab, state.CanGoBack, state.CanGoForward, state.Loading, state.Source);
            if (host.HasController) tab.ApplyLifecycle(ReferenceEquals(tab, _viewModel.ActiveTab) && !IsBrowserBackground
                ? BrowserTabLifecycle.Active : BrowserTabLifecycle.Warm);
        };
        host.NavigationCompleted += (_, state) =>
        {
            if (_disposed) return;
            if (state.IsSuccess) { tab.PageError = string.Empty; _viewModel.RecordVisit(state.Source, state.Title); }
            UpdateHostVisibility();
        };
        host.ContextMenuActionRequested += (_, action) => HandleContextMenuAction(tab, host, action);
        host.FaviconChanged += (_, bytes) => tab.ApplyFavicon(bytes);
        host.AudioStateChanged += (_, state) => tab.ApplyAudioState(state.Playing, state.Muted);
        host.DownloadRequested += (_, request) => _ = HandleDownloadRequestAsync(request);
        host.DownloadProgressChanged += (_, progress) => ApplyDownloadProgress(progress);
        host.PermissionRequested += (_, request) => _ = HandlePermissionRequestAsync(request);
        host.DocumentTitleChanged += (_, title) => _viewModel.ApplyDocumentTitleTo(tab, title);
        host.StatusChanged += (_, message) =>
        {
            if (_disposed) return;
            _viewModel.ReportStatusTo(tab, message);
            if (message.StartsWith("加载失败：", StringComparison.Ordinal) || message.StartsWith("内置浏览器初始化失败", StringComparison.Ordinal) || message.StartsWith("浏览器数据目录不可写", StringComparison.Ordinal))
            {
                tab.PageError = message;
                tab.IsLoading = tab.IsRestoring = false;
                UpdateHostVisibility();
            }
        };
        host.RuntimeProbeCompleted += (_, probe) => _viewModel.ApplyRuntimeProbe(probe);
        // 页面弹出新窗口 → 新页签接管同一个 CoreWebView2，保留 opener/postMessage/OAuth 弹窗关系。
        host.NewWindowRequested += (_, request) => _ = HandleNewWindowAsync(request);
        _hosts[tab.Id] = host;
        _hostLayer.Children.Add(host);
        _lastHost = host;
        return host;
    }

    private void ReleaseHost(BrowserTab tab)
    {
        if (!_hosts.TryGetValue(tab.Id, out var host)) return;
        InvalidateFind(tab.Id, resetDocumentEpoch: true);
        _hosts.Remove(tab.Id);
        _hostLayer.Children.Remove(host);
        _findGeneration.Remove(tab.Id);
        _findDocumentEpoch.Remove(tab.Id);
        host.Dispose();
        UpdateHostVisibility();
    }

    private void UpdateHostVisibility()
    {
        _viewModel.RefreshFacade();
        foreach (var entry in _hosts)
        {
            var tab = _viewModel.Tabs.FirstOrDefault(candidate => candidate.Id == entry.Key);
            var active = entry.Key == _viewModel.ActiveTab.Id && !IsBrowserBackground;
            entry.Value.SetContentHidden(tab is null || tab.IsPageEmpty || tab.HasPageError || tab.IsRestoring || _viewModel.HasRuntimeNotice);
            entry.Value.SetOccluded(!active);
            if (!active && entry.Value.HasController)
            {
                entry.Value.SetMemoryTargetLow(true);
                if (tab?.Lifecycle == BrowserTabLifecycle.Active) tab.ApplyLifecycle(BrowserTabLifecycle.Warm);
            }
            else if (active && entry.Value.HasController)
            {
                entry.Value.SetMemoryTargetLow(false);
            }
        }
    }

    private void OnTabSelectionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BrowserViewModel.StatusText)) ShowBrowserFeedback(_viewModel.StatusText);
        if (e.PropertyName == nameof(BrowserViewModel.IsFindOpen) && _viewModel.IsFindOpen)
            Dispatcher.UIThread.Post(() => this.FindControl<TextBox>("FindBox")?.Focus());
        if (e.PropertyName != nameof(BrowserViewModel.ActiveTab)) return;
        var tab = _viewModel.ActiveTab;
        CancelAnnotation();
        foreach (var item in _viewModel.Tabs) item.IsRestoring = false;
        _viewModel.IsSuggestionsOpen = _viewModel.IsHistoryOpen = _viewModel.IsDownloadsOpen = false;
        _viewModel.Tabs.FirstOrDefault(candidate => ReferenceEquals(candidate, tab))?.Touch();
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed || !ReferenceEquals(tab, _viewModel.ActiveTab)) return;
            var selected = this.GetVisualDescendants().OfType<Button>().FirstOrDefault(button => button.Classes.Contains("browserTab") && ReferenceEquals(button.DataContext, tab));
            if (selected?.Parent is Control row) row.BringIntoView();
        }, DispatcherPriority.Loaded);
        _ = ActivateTabAsync(tab);
    }

    private void OnTabCreated(object? sender, BrowserTab tab)
    {
        HostFor(tab);
        if (!ReferenceEquals(tab, _viewModel.ActiveTab)) tab.ApplyLifecycle(BrowserTabLifecycle.Discarded);
        UpdateHostVisibility();
    }

    private void OnTabClosed(object? sender, BrowserTab tab)
    {
        tab.ApplyLifecycle(BrowserTabLifecycle.Closed);
        tab.DisposeFavicon();
        ReleaseHost(tab);
    }

    private void OnNavigationRequested(object? sender, (BrowserTab Tab, BrowserNavigationRequest Request) e)
    {
        // 导航到新文档：旧查找会话与其迟到回调全部失效，同 query 也必须重新 Start。
        _suppressSuggestionsUntil = DateTime.UtcNow.AddSeconds(2);
        _viewModel.CloseSuggestions();
        InvalidateFind(e.Tab.Id, resetDocumentEpoch: true);
        e.Tab.FindStatus = string.Empty;
        var host = HostFor(e.Tab);
        if (ReferenceEquals(e.Tab, _viewModel.ActiveTab))
        {
            _activationGeneration++;
            CancelAnnotation();
            e.Tab.IsRestoring = false;
            if (!IsBrowserBackground) host.SetActive();
        }
        if (e.Request.Document is not null) host.NavigateToDocument(e.Request.Document);
        else host.Navigate(e.Request.Address);
        UpdateHostVisibility();
    }

    private void OnActionRequested(object? sender, (BrowserTab Tab, BrowserTabAction Action) e) => ApplyAction(e.Tab, e.Action);

    private void OnZoomRequested(object? sender, (BrowserTab Tab, double Factor) e)
    {
        if (_hosts.TryGetValue(e.Tab.Id, out var host)) host.SetZoomPercent(e.Factor * 100.0);
    }

    private void OnAddressCopyRequested(object? sender, string address) => _ = CopyAddressAsync(address);
    private void OnExternalOpenRequested(object? sender, string address) => _ = OpenExternalAsync(address);
    private void OnShareRequested(object? sender, string address) => _ = CopyAddressAsync(address);

    private void ApplyAction(BrowserTab tab, BrowserTabAction action)
    {
        if (!_hosts.TryGetValue(tab.Id, out var host)) return;
        switch (action)
        {
            case BrowserTabAction.Back: host.GoBack(); break;
            case BrowserTabAction.Forward: host.GoForward(); break;
            case BrowserTabAction.Reload: host.Reload(); break;
            case BrowserTabAction.Stop:
                if (ReferenceEquals(tab, _viewModel.ActiveTab)) _activationGeneration++;
                tab.IsRestoring = false;
                host.Stop();
                UpdateHostVisibility();
                break;
            case BrowserTabAction.CloseFind:
                InvalidateFind(tab.Id, resetDocumentEpoch: false);
                host.StopFind();
                tab.FindStatus = string.Empty;
                break;
            case BrowserTabAction.OpenDevTools:
                if (!host.OpenDevTools()) _viewModel.ReportStatusTo(tab, "当前页面暂不能打开开发工具。");
                break;
            case BrowserTabAction.ToggleMute:
                if (!host.ToggleMute()) _viewModel.ReportStatusTo(tab, "当前页面暂不能切换静音。");
                break;
            case BrowserTabAction.FindNext:
            case BrowserTabAction.FindPrevious:
                ApplyFind(tab, host, action);
                break;
        }
    }

    private void ApplyFind(BrowserTab tab, BrowserViewHost host, BrowserTabAction action)
    {
        if (!host.PageFindAvailable)
        {
            tab.FindStatus = "当前运行时无查找";
            return;
        }
        var epoch = _findDocumentEpoch.GetValueOrDefault(tab.Id, "0");
        var started = _findStarted.GetValueOrDefault(tab.Id);
        if (started is null || !string.Equals(started, tab.FindQuery + "\u0000" + epoch, StringComparison.Ordinal))
        {
            if (!host.TryStartFind(tab.FindQuery, out var note)) { tab.FindStatus = note; return; }
            _findStarted[tab.Id] = tab.FindQuery + "\u0000" + epoch;
        }
        if (action == BrowserTabAction.FindNext) host.FindNext();
        else host.FindPrevious();
        // Find 的 MatchCount/ActiveMatchIndex 由运行时异步更新，这里轮询真实值而不是读一次快照。
        var generation = BumpFindGeneration(tab.Id);
        _ = RefreshFindStatusAsync(tab, host, tab.FindQuery, generation);
    }

    private int BumpFindGeneration(string tabId)
    {
        var next = _findGeneration.GetValueOrDefault(tabId, 0) + 1;
        _findGeneration[tabId] = next;
        return next;
    }

    /// <summary>关闭查找/更换 query/导航/关闭 tab/Dispose 时调用：隔离所有已失效的迟到回调。</summary>
    private void InvalidateFind(string tabId, bool resetDocumentEpoch)
    {
        BumpFindGeneration(tabId);
        _findStarted.Remove(tabId);
        if (resetDocumentEpoch) _findDocumentEpoch[tabId] = Guid.NewGuid().ToString("N");
    }

    private async Task RefreshFindStatusAsync(BrowserTab tab, BrowserViewHost host, string query, int generation)
    {
        // Start 完成后计数才填充；先等一小段，避免读到上一个查询的陈旧计数。
        await Task.Delay(400);
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (_findGeneration.GetValueOrDefault(tab.Id, 0) != generation) return;
            if (!string.Equals(tab.FindQuery, query, StringComparison.Ordinal)) return;
            var status = host.FindStatus;
            if (status is { MatchCount: > 0 } value)
            {
                // 本运行时的 ActiveMatchIndex 为 1 基；为 0/负值时保守显示为第 1 个。
                var active = value.ActiveMatchIndex <= 0 ? 1 : value.ActiveMatchIndex;
                tab.FindStatus = $"{active}/{value.MatchCount}";
                return;
            }
            await Task.Delay(150);
        }
        if (_findGeneration.GetValueOrDefault(tab.Id, 0) != generation) return;
        tab.FindStatus = host.PageFindAvailable ? "无匹配" : "当前运行时无查找";
    }

    private async Task CopyAddressAsync(string address)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) { _viewModel.ReportStatus("当前环境没有可用剪贴板。"); return; }
        try
        {
            await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, address);
            _viewModel.ReportStatus("已复制当前地址。");
        }
        catch (Exception error) { _viewModel.ReportStatus("复制失败：" + error.Message); }
    }

    internal bool SuppressExternalOpenForTesting { get; set; }

    private async Task OpenExternalAsync(string address)
    {
        if (Program.SelfTestMode && SuppressExternalOpenForTesting) return;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) { _viewModel.ReportStatus("当前环境无法打开外部链接。"); return; }
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri)) { _viewModel.ReportStatus("地址无效，无法外部打开。"); return; }
        try { await topLevel.Launcher.LaunchUriAsync(uri); _viewModel.ReportStatus("已在外部打开。"); }
        catch (Exception error) { _viewModel.ReportStatus("外部打开失败：" + error.Message); }
    }

    private void Address_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Down or Key.Up)
        {
            if (_viewModel.IsSuggestionsOpen && !string.IsNullOrWhiteSpace(_viewModel.AddressText))
            {
                // 首次按下方向键时按当前草稿刷新候选（TextChanged 不触发时也能拿到结果）。
                if (_viewModel.Suggestions.Count == 0) _viewModel.RefreshSuggestions(_viewModel.AddressText);
                _viewModel.MoveSuggestion(e.Key == Key.Down ? 1 : -1);
                e.Handled = true;
                return;
            }
        }
        if (e.Key == Key.Escape && _viewModel.IsSuggestionsOpen)
        {
            _viewModel.CloseSuggestions();
            e.Handled = true;
            return;
        }
        if (e.Key is not (Key.Return or Key.Enter)) return;
        if (_addressIme.ShouldSwallowEnter()) return;
        e.Handled = true;
        if (_viewModel.IsSuggestionsOpen && _viewModel.SelectedSuggestion is { } selected)
        {
            _viewModel.AcceptSuggestionCommand.Execute(selected);
            e.Handled = true;
            return;
        }
        _viewModel.CloseSuggestions();
        if (_viewModel.NavigateCommand.CanExecute(null)) _viewModel.NavigateCommand.Execute(null);
    }

    private void Address_TextChanged(object? sender, TextChangedEventArgs e)
    {
        var text = _viewModel.AddressText;
        // 导航回写地址时也会触发 TextChanged：此时不重新打开候选，避免刚按 Enter 又弹一层。
        if (_viewModel.IsLoading || DateTime.UtcNow < _suppressSuggestionsUntil)
        {
            _viewModel.CloseSuggestions();
            return;
        }
        if (string.IsNullOrWhiteSpace(text) || (sender is TextBox box && !box.IsFocused))
        {
            _viewModel.CloseSuggestions();
            return;
        }
        if (_viewModel.ActiveTab.IsFileMode) { _viewModel.CloseSuggestions(); return; }
        _viewModel.RefreshSuggestions(text);
    }

    private DateTime _suppressSuggestionsUntil;
    private BrowserTab? _draggingTab;
    private Point _dragStart;
    private bool _tabDragMoved;

    private void Tab_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: BrowserTab tab }) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _draggingTab = tab;
        _dragStart = e.GetPosition(this);
        _tabDragMoved = false;
    }

    private void Tab_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_draggingTab is null) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { _draggingTab = null; return; }
        var position = e.GetPosition(this);
        if (!_tabDragMoved && Math.Abs(position.X - _dragStart.X) < 8 && Math.Abs(position.Y - _dragStart.Y) < 8) return;
        _tabDragMoved = true;
        var target = HitTestTab(position);
        if (target is null || ReferenceEquals(target, _draggingTab)) return;
        var index = _viewModel.Tabs.IndexOf(target);
        if (index >= 0) _viewModel.MoveTab(_draggingTab, index);
    }

    private void Tab_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _draggingTab = null;
        _tabDragMoved = false;
    }

    private BrowserTab? HitTestTab(Point pointInView)
    {
        foreach (var button in this.GetVisualDescendants().OfType<Button>())
        {
            if (!button.Classes.Contains("browserTab")) continue;
            var origin = button.TranslatePoint(new Point(0, 0), this);
            if (origin is null) continue;
            var rect = new Rect(origin.Value, button.Bounds.Size);
            if (rect.Contains(pointInView) && button.DataContext is BrowserTab tab) return tab;
        }
        return null;
    }

    private void Find_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Return or Key.Enter) || _findIme.ShouldSwallowEnter()) return;
        var command = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? _viewModel.FindPreviousCommand : _viewModel.FindNextCommand;
        if (command.CanExecute(null)) command.Execute(null);
        e.Handled = true;
    }

    private void Download_Click(object? sender, RoutedEventArgs e) => _viewModel.ToggleDownloadsCommand.Execute(null);

    private void CloseDownloads_Click(object? sender, RoutedEventArgs e) => _viewModel.CloseDownloadsCommand.Execute(null);

    private void CancelDownload_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: string id }) return;
        var cancelled = false;
        foreach (var host in _hosts.Values) cancelled |= host.CancelDownload(id);
        _viewModel.ReportStatus(cancelled ? "已取消下载。" : "该下载已结束，无法取消。");
    }

    private async void OpenFile_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is not { } storage) { _viewModel.ReportStatus("当前环境无法打开文件选择器。"); return; }
        try
        {
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择要预览的文件",
                AllowMultiple = false,
            });
            var file = files?.FirstOrDefault();
            if (file is null) return;
            var path = file.TryGetLocalPath();
            if (string.IsNullOrEmpty(path)) { _viewModel.ReportStatus("无法读取该文件的本地路径。"); return; }
            _viewModel.PreviewFile(path);
        }
        catch (Exception error)
        {
            _viewModel.ReportStatus("打开文件失败：" + error.Message);
        }
    }

    private async void OpenRuntimeDownload_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) { _viewModel.ReportStatus("当前环境无法打开外部链接。"); return; }
        try { await topLevel.Launcher.LaunchUriAsync(new Uri(_viewModel.RuntimeLink)); }
        catch (Exception error) { _viewModel.ReportStatus("打开下载页失败：" + error.Message); }
    }

    private async void CopyRuntimeLink_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.Clipboard is not { } clipboard) { _viewModel.ReportStatus("当前环境没有可用剪贴板。"); return; }
        try
        {
            await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, _viewModel.RuntimeLink);
            _viewModel.ReportStatus("已复制下载链接。");
        }
        catch (Exception error) { _viewModel.ReportStatus("复制失败：" + error.Message); }
    }
}
