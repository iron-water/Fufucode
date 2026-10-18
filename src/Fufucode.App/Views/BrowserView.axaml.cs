using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Avalonia.Platform.Storage;
using Fufucode.App.Features.BrowserExperience;
using Fufucode.App.Infrastructure;
using Fufucode.App.Infrastructure.Browser;
using Fufucode.App.ViewModels;

namespace Fufucode.App.Views;

/// <summary>
/// 右栏 Preview / 工作区浏览器页：页签栏 + 导航 + 地址 + 查找 + 缩放 + WebView2 宿主。
/// 每个页签持有独立的 BrowserViewHost（独立原生 controller、DOM、历史），
/// 因此切换/关闭页签不会串页或覆盖对方状态。
/// 自带 BrowserViewModel（不继承外层 DataContext），因此可以整体搬到右栏任意位置。
/// </summary>
public partial class BrowserView : UserControl, IDisposable
{
    private readonly BrowserViewModel _viewModel = new();
    private readonly Grid _hostLayer;
    private readonly Dictionary<string, BrowserViewHost> _hosts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _findStarted = new(StringComparer.Ordinal);
    /// <summary>每个页签查找回调的世代号：关闭/换 query/导航/关 tab/Dispose 都会递增，旧回调不得写回。</summary>
    private readonly Dictionary<string, int> _findGeneration = new(StringComparer.Ordinal);
    /// <summary>记录已向 SDK 发起过 Start 的 (query, 文档世代)；同 query 但换文档必须重新 Start。</summary>
    private readonly Dictionary<string, string> _findDocumentEpoch = new(StringComparer.Ordinal);
    private BrowserViewHost? _lastHost;
    private bool _disposed;

    /// <summary>BRW-1：Agent 控制与 CDP 门禁快照；设置变化时由组合根更新。</summary>
    private BrowserSettingsGate _agentGate = BrowserSettingsGate.Disabled;
    private WebView2BrowserUseBridge? _bridge;

    public BrowserView()
    {
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
    }

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
        _viewModel.ApplyShowFullUrl(settings.ShowFullUrl);
        _agentGate = new BrowserSettingsGate(settings.BrowserUseEnabled, settings.EnableFullCdpAccess);
        _bridge ??= new WebView2BrowserUseBridge(() => this, () => _agentGate);
        BrowserAgentBridgeChanged?.Invoke(this, _bridge);
    }

    /// <summary>桥与门禁变化通知；组合根据此把真实桥接进插件工具注册表。</summary>
    internal event EventHandler<WebView2BrowserUseBridge>? BrowserAgentBridgeChanged;

    /// <summary>当前桥（未应用设置时为 null）。</summary>
    internal WebView2BrowserUseBridge? BridgeForTesting => _bridge;

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
        if (state.Version is not (1 or 2))
        {
            _viewModel.ResetTabs(new[] { _viewModel.ActiveTab.Id });
            _viewModel.AddressText = "";
            _viewModel.ActiveTab.NavigateCommand.Execute(null);
            _viewModel.ReportStatus("浏览器页面状态版本不受支持，已恢复空页。");
            return;
        }
        if (state.Version == 1)
        {
            // 旧 v1 单页状态：仍然恢复，不丢用户旧配置。
            RestoreIntoTab(_viewModel.ActiveTab, state.FilePath, state.NavigationAddress, state.AddressDraft);
            return;
        }
        // 过滤 null/空白记录并成对归一化 ID：重复 ID 保留首个合法条目，后续重命名而不是丢弃。
        var savedTabs = NormalizeTabs(state.Tabs, state);
        // 整体重建页签：旧页签的宿主被释放，新页签按保存的 id 建独立宿主。
        var restored = _viewModel.ResetTabs(savedTabs.Select(tab => tab.Id).ToArray());
        for (var index = 0; index < savedTabs.Count; index++)
            RestoreIntoTab(restored[index], savedTabs[index].FilePath, savedTabs[index].NavigationAddress, savedTabs[index].AddressDraft);
        var active = restored.FirstOrDefault(tab => tab.Id == state.ActiveTabId) ?? restored[0];
        _viewModel.SelectTab(active);
        UpdateHostVisibility();
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

    private static void RestoreIntoTab(BrowserTab tab, string? filePath, string? navigationAddress, string addressDraft)
    {
        if (!string.IsNullOrWhiteSpace(filePath)) tab.PreviewFile(filePath);
        else if (!string.IsNullOrWhiteSpace(navigationAddress))
        {
            tab.AddressText = navigationAddress;
            tab.NavigateCommand.Execute(null);
        }
        tab.AddressText = addressDraft ?? "";
    }

    /// <summary>直接向当前页签推送本地路径进入文件预览模式。</summary>
    public void PreviewFile(string? path) => _viewModel.PreviewFile(path);

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
        host.AcceleratorHandler = (key, modifiers) => NativeShortcutHandler?.Invoke(key, modifiers) == true;
        host.NavigationStateChanged += (_, state) => _viewModel.ApplyNavigationStateTo(tab, state.CanGoBack, state.CanGoForward, state.Loading, state.Source);
        host.DocumentTitleChanged += (_, title) => _viewModel.ApplyDocumentTitleTo(tab, title);
        host.StatusChanged += (_, message) => _viewModel.ReportStatusTo(tab, message);
        host.RuntimeProbeCompleted += (_, probe) => _viewModel.ApplyRuntimeProbe(probe);
        // 页面弹出新窗口 → 明确进入一个新页签，禁止静默覆盖当前页签。
        host.NewWindowRequested += (_, uri) =>
        {
            var created = _viewModel.CreateTab();
            var createdHost = HostFor(created);
            createdHost.SetOccluded(true);
            created.AddressText = uri;
            created.NavigateCommand.Execute(null);
            UpdateHostVisibility();
        };
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
            entry.Value.SetOccluded(!ReferenceEquals(entry.Key, _viewModel.ActiveTab.Id));
    }

    private void OnTabSelectionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(BrowserViewModel.ActiveTab)) return;
        var tab = _viewModel.ActiveTab;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_disposed || !ReferenceEquals(tab, _viewModel.ActiveTab)) return;
            var selected = this.GetVisualDescendants().OfType<Button>().FirstOrDefault(button => button.Classes.Contains("browserTab") && ReferenceEquals(button.DataContext, tab));
            if (selected?.Parent is Control row) row.BringIntoView();
        }, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    private void OnTabCreated(object? sender, BrowserTab tab)
    {
        HostFor(tab);
        UpdateHostVisibility();
    }

    private void OnTabClosed(object? sender, BrowserTab tab) => ReleaseHost(tab);

    private void OnNavigationRequested(object? sender, (BrowserTab Tab, BrowserNavigationRequest Request) e)
    {
        // 导航到新文档：旧查找会话与其迟到回调全部失效，同 query 也必须重新 Start。
        InvalidateFind(e.Tab.Id, resetDocumentEpoch: true);
        e.Tab.FindStatus = string.Empty;
        var host = HostFor(e.Tab);
        UpdateHostVisibility();
        if (e.Request.Document is not null) host.NavigateToDocument(e.Request.Document);
        else host.Navigate(e.Request.Address);
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
            case BrowserTabAction.Stop: host.Stop(); break;
            case BrowserTabAction.CloseFind:
                InvalidateFind(tab.Id, resetDocumentEpoch: false);
                host.StopFind();
                tab.FindStatus = string.Empty;
                break;
            case BrowserTabAction.OpenDevTools:
                if (!host.OpenDevTools()) _viewModel.ReportStatusTo(tab, "当前页面暂不能打开开发工具。");
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

    private async Task OpenExternalAsync(string address)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) { _viewModel.ReportStatus("当前环境无法打开外部链接。"); return; }
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri)) { _viewModel.ReportStatus("地址无效，无法外部打开。"); return; }
        try { await topLevel.Launcher.LaunchUriAsync(uri); _viewModel.ReportStatus("已在外部打开。"); }
        catch (Exception error) { _viewModel.ReportStatus("外部打开失败：" + error.Message); }
    }

    private void Address_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Return && e.Key != Key.Enter) return;
        if (_viewModel.NavigateCommand.CanExecute(null)) _viewModel.NavigateCommand.Execute(null);
    }

    private void Find_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Return or Key.Enter)) return;
        if (_viewModel.FindNextCommand.CanExecute(null)) _viewModel.FindNextCommand.Execute(null);
    }

    private void More_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control anchor) return;
        var menu = new ContextMenu();
        var copy = new MenuItem { Header = "复制当前地址" };
        copy.Click += (_, _) => _viewModel.CopyAddressCommand.Execute(null);
        menu.Items.Add(copy);
        var open = new MenuItem { Header = "在默认浏览器打开" };
        open.Click += (_, _) => _viewModel.OpenExternalCommand.Execute(null);
        menu.Items.Add(open);
        var share = new MenuItem { Header = "分享当前地址（复制链接）" };
        share.Click += (_, _) => _viewModel.ShareCommand.Execute(null);
        menu.Items.Add(share);
        var bookmark = new MenuItem { Header = "收藏 / 取消收藏当前页面" };
        bookmark.Click += (_, _) => _viewModel.ToggleBookmarkCommand.Execute(null);
        menu.Items.Add(bookmark);
        menu.Items.Add(new Separator());
        var find = new MenuItem { Header = "页内查找 (Ctrl+F)" };
        find.Click += (_, _) => _viewModel.ToggleFindCommand.Execute(null);
        menu.Items.Add(find);
        var zoomIn = new MenuItem { Header = "放大 (+10%)" };
        zoomIn.Click += (_, _) => _viewModel.ZoomInCommand.Execute(null);
        menu.Items.Add(zoomIn);
        var zoomOut = new MenuItem { Header = "缩小 (-10%)" };
        zoomOut.Click += (_, _) => _viewModel.ZoomOutCommand.Execute(null);
        menu.Items.Add(zoomOut);
        var zoomReset = new MenuItem { Header = $"重置缩放 (当前 {_viewModel.ZoomPercent}%)" };
        zoomReset.Click += (_, _) => _viewModel.ZoomResetCommand.Execute(null);
        menu.Items.Add(zoomReset);
        menu.Items.Add(new Separator());
        var devtools = new MenuItem { Header = "开发者工具" };
        devtools.Click += (_, _) => _viewModel.OpenDevToolsCommand.Execute(null);
        menu.Items.Add(devtools);
        menu.Open(anchor);
    }

    private async void InspectElement_Click(object? sender, RoutedEventArgs e)
    {
        var host = _lastHost ?? (_hosts.TryGetValue(_viewModel.ActiveTab.Id, out var h) ? h : null);
        if (host is null || !host.IsReady)
        {
            _viewModel.ReportStatus("请先加载网页再拾取元素。");
            return;
        }
        const string inspectScript = "(function(){if(window.__fufuInspectorActive){window.__fufuInspectorCleanup?.();return 'Inspector deactivated';}window.__fufuInspectorActive=true;let o=document.createElement('div');o.style.position='fixed';o.style.pointerEvents='none';o.style.border='2px solid #0078D4';o.style.background='rgba(0,120,212,0.2)';o.style.zIndex='999999';document.body.appendChild(o);function onMove(e){let r=e.target?.getBoundingClientRect();if(!r)return;o.style.top=r.top+'px';o.style.left=r.left+'px';o.style.width=r.width+'px';o.style.height=r.height+'px';}function onClick(e){e.preventDefault();e.stopPropagation();let sel=(e.target?.id)?'#'+e.target.id:(e.target?.className)?'.'+e.target.className.split(' ').join('.'):e.target?.tagName?.toLowerCase()||'element';cleanup();navigator.clipboard?.writeText(sel);alert('已拾取元素并复制到剪贴板: '+sel);}function onKey(e){if(e.key==='Escape')cleanup();}function cleanup(){window.__fufuInspectorActive=false;o.remove();document.removeEventListener('mousemove',onMove,true);document.removeEventListener('click',onClick,true);document.removeEventListener('keydown',onKey,true);}window.__fufuInspectorCleanup=cleanup;document.addEventListener('mousemove',onMove,true);document.addEventListener('click',onClick,true);document.addEventListener('keydown',onKey,true);return 'Inspector activated';})();";
        var result = await host.ExecuteScriptAsync(inspectScript);
        _viewModel.ReportStatus(result?.Contains("activated") == true ? "元素检查器已激活：悬停高亮，点击复制选择器，按 Esc 退出。" : "元素检查器已关闭。");
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
