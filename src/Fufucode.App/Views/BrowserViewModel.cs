using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fufucode.App.Features.BrowserExperience;
using Fufucode.App.Infrastructure.Browser;

namespace Fufucode.App.ViewModels;

/// <summary>预览面的两种用法：自由导航（输入网址）与文件预览（接收本地路径）。</summary>
public enum BrowserSurfaceMode
{
    Free,
    File,
}

/// <summary>单页签动作；保留旧名以兼容既有调用方（例如 BrowserSurfaceAction.Back）。</summary>
public enum BrowserSurfaceAction
{
    Back,
    Forward,
    Reload,
    Stop,
}

/// <summary>一次导航请求。Address 与 Document 互斥：Document 非空时宿主用 NavigateToString。</summary>
public sealed record BrowserNavigationRequest(string? Address, string? Document, BrowserSurfaceMode Mode, string Label);

/// <summary>
/// 浏览器多页签面的状态机与运行时信息。刻意不引用任何 WebView2 类型：宿主通过事件驱动，本类可以完全在无头环境断言。
/// 每页签是独立的 BrowserTab（独立地址草稿/真实导航地址/历史/标题/查找/缩放）。
/// 旧的单页属性（AddressText/DocumentTitle/NavigateCommand 等）保留为“当前页签”的门面，既有调用方不用改。
/// </summary>
public sealed partial class BrowserViewModel : ViewModelBase
{
    private static int _tabCounter;

    public BrowserViewModel()
    {
        RuntimeLink = WebView2RuntimeGate.RuntimeDownloadUrl;
        ActiveTab = CreateTab();
    }

    /// <summary>
    /// 兼容旧调用方：当前页签请求导航。新代码用 TabNavigationRequested（带页签身份）。
    /// </summary>
    public event EventHandler<BrowserNavigationRequest>? NavigationRequested;

    /// <summary>
    /// 兼容旧调用方：当前页签请求后退/前进/刷新/停止。新代码用 TabActionRequested（带页签身份）。
    /// </summary>
    public event EventHandler<BrowserSurfaceAction>? ActionRequested;

    /// <summary>请求宿主导航；参数是该次请求所属页签。</summary>
    public event EventHandler<(BrowserTab Tab, BrowserNavigationRequest Request)>? TabNavigationRequested;

    /// <summary>请求宿主执行页签动作（后退/前进/刷新/停止/查找/开发工具）。</summary>
    public event EventHandler<(BrowserTab Tab, BrowserTabAction Action)>? TabActionRequested;

    /// <summary>新页签已创建，宿主/视图应为其准备独立原生宿主。</summary>
    public event EventHandler<BrowserTab>? TabCreated;

    /// <summary>页签已关闭，宿主/视图应释放其原生宿主。</summary>
    public event EventHandler<BrowserTab>? TabClosed;

    /// <summary>请求复制当前真实地址（由视图层写入剪贴板）。</summary>
    public event EventHandler<string>? AddressCopyRequested;

    /// <summary>请求显式外部打开当前真实地址（只能由用户操作触发）。</summary>
    public event EventHandler<string>? ExternalOpenRequested;

    /// <summary>请求复制当前真实地址为“可分享文本”（视图层写剪贴板并给提示）。</summary>
    public event EventHandler<string>? ShareRequested;

    /// <summary>书签状态变化：当前真实地址是否已收藏。</summary>
    public event EventHandler<(string Address, bool IsBookmarked)>? BookmarkToggled;

    public ObservableCollection<BrowserTab> Tabs { get; } = new();

    /// <summary>已收藏的地址集合（按真实导航地址比较）；只存地址，不落盘到浏览器 profile。</summary>
    private readonly HashSet<string> _bookmarks = new(StringComparer.Ordinal);

    [ObservableProperty] private BrowserTab _activeTab;

    [ObservableProperty] private BrowserRuntimeState _runtimeState = BrowserRuntimeState.Unknown;
    [ObservableProperty] private string _runtimeMessage = string.Empty;
    [ObservableProperty] private string? _projectRoot;

    public string RuntimeLink { get; }

    public bool IsRuntimeAvailable => RuntimeState == BrowserRuntimeState.Available;

    /// <summary>探测完成且不可用才提示；初始未探测（Message 为空）不遮挡宿主。</summary>
    public bool HasRuntimeNotice => !IsRuntimeAvailable && RuntimeMessage.Length > 0;

    public bool CanCloseTab => Tabs.Count > 1;
    public string TabCountText => Tabs.Count == 1 ? "1 个页签" : $"{Tabs.Count} 个页签";

    // ---- 地址栏来源安全态（门面） ----
    public BrowserSecurityState Security => ActiveTab.Security;
    public bool ShowSecurity => ActiveTab.ShowSecurity;
    public bool IsSecure => ActiveTab.IsSecure;
    public bool IsInsecure => ActiveTab.IsInsecure;
    public string SecurityToolTip => ActiveTab.SecurityToolTip;
    /// <summary>地址栏显示文本：按设置决定完整网址或仅主机名。</summary>
    public string DisplayAddress => ActiveTab.DisplayAddress;

    /// <summary>当前真实地址是否已收藏；用于星标按钮状态。</summary>
    public bool IsCurrentBookmarked =>
        ActiveTab.NavigationAddress is { Length: > 0 } address && _bookmarks.Contains(address);
    public string BookmarkToolTip => IsCurrentBookmarked ? "取消收藏当前页面" : "收藏当前页面";

    /// <summary>由设置页同步“显示完整网址”策略到全部页签（含后续新建页签）。</summary>
    public void ApplyShowFullUrl(bool showFullUrl)
    {
        _showFullUrl = showFullUrl;
        foreach (var tab in Tabs) tab.ApplyShowFullUrl(showFullUrl);
        OnPropertyChanged(nameof(DisplayAddress));
    }

    private bool _showFullUrl = true;

    // ---- 当前页签门面（兼容既有单页调用方） ----

    public string AddressText
    {
        get => ActiveTab.AddressText;
        set => ActiveTab.AddressText = value;
    }

    public string DocumentTitle => ActiveTab.DocumentTitle;
    public bool IsLoading => ActiveTab.IsLoading;
    public double Progress => ActiveTab.Progress;
    public bool CanGoBack => ActiveTab.CanGoBack;
    public bool CanGoForward => ActiveTab.CanGoForward;
    public string StatusText => ActiveTab.StatusText;
    public BrowserSurfaceMode Mode => ActiveTab.Mode;
    public bool IsFileMode => ActiveTab.IsFileMode;
    public string ModeText => ActiveTab.ModeText;
    public string? CurrentFilePath => ActiveTab.CurrentFilePath;
    public bool CanStop => ActiveTab.CanStop;

    public string FindQuery
    {
        get => ActiveTab.FindQuery;
        set => ActiveTab.FindQuery = value;
    }

    public bool IsFindOpen
    {
        get => ActiveTab.IsFindOpen;
        set => ActiveTab.IsFindOpen = value;
    }

    public string FindStatus => ActiveTab.FindStatus;
    public int ZoomPercent => ActiveTab.ZoomPercent;

    public IRelayCommand NavigateCommand => ActiveTab.NavigateCommand;
    public IRelayCommand BackCommand => ActiveTab.BackCommand;
    public IRelayCommand ForwardCommand => ActiveTab.ForwardCommand;
    public IRelayCommand ReloadCommand => ActiveTab.ReloadCommand;
    public IRelayCommand StopCommand => ActiveTab.StopCommand;
    public IRelayCommand FindNextCommand => ActiveTab.FindNextCommand;
    public IRelayCommand FindPreviousCommand => ActiveTab.FindPreviousCommand;
    public IRelayCommand CloseFindCommand => ActiveTab.CloseFindCommand;
    public IRelayCommand OpenDevToolsCommand => ActiveTab.OpenDevToolsCommand;

    /// <summary>创建并选中一个新页签（供“新建页签”和页面弹出新窗口请求使用）。</summary>
    public BrowserTab CreateTab()
    {
        string id;
        do { id = "tab-" + (++_tabCounter).ToString(); } while (Tabs.Any(tab => tab.Id == id));
        return AddTab(id, select: true);
    }

    /// <summary>
    /// 用给定 id 列表整体重建页签（恢复多页签状态用）。旧的页签全部关闭并释放宿主，新的按顺序创建。
    /// </summary>
    internal IReadOnlyList<BrowserTab> ResetTabs(IReadOnlyList<string> ids)
    {
        foreach (var tab in Tabs.ToArray())
        {
            Detach(tab);
            TabClosed?.Invoke(this, tab);
        }
        Tabs.Clear();
        var created = new List<BrowserTab>();
        foreach (var raw in ids)
        {
            var id = string.IsNullOrWhiteSpace(raw) ? "tab-" + (++_tabCounter).ToString() : raw;
            if (created.Any(tab => tab.Id == id)) continue;
            created.Add(AddTab(id, select: false));
        }
        if (created.Count == 0) created.Add(AddTab("tab-" + (++_tabCounter).ToString(), select: false));
        ActiveTab = created[0];
        OnTabCollectionChanged();
        return created;
    }

    private BrowserTab AddTab(string id, bool select)
    {
        var tab = new BrowserTab(id);
        tab.ApplyShowFullUrl(_showFullUrl);
        Attach(tab);
        Tabs.Add(tab);
        if (select) ActiveTab = tab;
        TabCreated?.Invoke(this, tab);
        OnTabCollectionChanged();
        return tab;
    }

    /// <summary>关闭指定页签：只影响该页签；关掉最后一个页签时留下一个新的清晰空页。</summary>
    public void CloseTab(BrowserTab? tab)
    {
        if (tab is null || !Tabs.Contains(tab)) return;
        var index = Tabs.IndexOf(tab);
        Detach(tab);
        Tabs.Remove(tab);
        var replacement = Tabs.Count == 0 ? null : Tabs[Math.Min(index, Tabs.Count - 1)];
        TabClosed?.Invoke(this, tab);
        if (replacement is null) replacement = CreateTab();
        if (ReferenceEquals(ActiveTab, tab) || !Tabs.Contains(ActiveTab)) ActiveTab = replacement;
        OnTabCollectionChanged();
    }

    public void SelectTab(BrowserTab? tab)
    {
        if (tab is null || !Tabs.Contains(tab)) return;
        ActiveTab = tab;
    }

    public void PreviewFile(string? path)
    {
        ActiveTab.PreviewFile(path);
        NotifyFacade();
    }

    /// <summary>宿主把真实导航状态回写给当前页签（兼容旧调用签名）。</summary>
    public void ApplyNavigationState(bool canGoBack, bool canGoForward, bool loading, string? source)
    {
        ActiveTab.ApplyNavigationState(canGoBack, canGoForward, loading, source);
        NotifyFacade();
    }

    /// <summary>宿主把真实导航状态回写给指定页签；只有它是活动页签时才同步门面属性。</summary>
    public void ApplyNavigationStateTo(BrowserTab tab, bool canGoBack, bool canGoForward, bool loading, string? source)
    {
        tab.ApplyNavigationState(canGoBack, canGoForward, loading, source);
        if (ReferenceEquals(tab, ActiveTab)) NotifyFacade();
    }

    public void ApplyDocumentTitle(string? title)
    {
        ActiveTab.ApplyDocumentTitle(title);
        OnPropertyChanged(nameof(DocumentTitle));
    }

    public void ApplyDocumentTitleTo(BrowserTab tab, string? title)
    {
        tab.ApplyDocumentTitle(title);
        if (ReferenceEquals(tab, ActiveTab)) OnPropertyChanged(nameof(DocumentTitle));
    }

    public void ReportStatus(string message)
    {
        ActiveTab.ReportStatus(message);
        OnPropertyChanged(nameof(StatusText));
    }

    public void ReportStatusTo(BrowserTab tab, string message)
    {
        tab.ReportStatus(message);
        if (ReferenceEquals(tab, ActiveTab)) OnPropertyChanged(nameof(StatusText));
    }

    /// <summary>活动页签的门面属性统一重发一次，供宿主在直接改页签后刷新绑定。</summary>
    public void RefreshFacade() => NotifyFacade();

    public void ApplyRuntimeProbe(BrowserRuntimeProbe probe)
    {
        RuntimeState = probe.State;
        RuntimeMessage = probe.IsAvailable ? probe.Message : probe.Guidance;
        if (!probe.IsAvailable) ReportStatus("内置浏览器不可用：" + probe.Guidance);
    }

    [RelayCommand] private void NewTab() => CreateTab();
    [RelayCommand] private void SelectTabById(BrowserTab? tab) => SelectTab(tab);
    [RelayCommand] private void CloseTabById(BrowserTab? tab) => CloseTab(tab);
    [RelayCommand] private void SelectTabAt(int index) { if (index >= 0 && index < Tabs.Count) SelectTab(Tabs[index]); }

    /// <summary>当前页签在页签栏中的序号（1 基），供“第 N 个页签”等文本使用。</summary>
    public int ActiveTabOrdinal => Tabs.IndexOf(ActiveTab) + 1;

    [RelayCommand] private void ZoomIn() => ApplyZoom(ActiveTab.ZoomPercent + 10);
    [RelayCommand] private void ZoomOut() => ApplyZoom(ActiveTab.ZoomPercent - 10);
    [RelayCommand] private void ZoomReset() => ApplyZoom(100);

    [RelayCommand] private void CopyAddress()
    {
        var address = ActiveTab.NavigationAddress;
        if (string.IsNullOrWhiteSpace(address)) { ReportStatus("当前没有可复制的地址。"); return; }
        AddressCopyRequested?.Invoke(this, address);
    }

    [RelayCommand] private void OpenExternal()
    {
        var address = ActiveTab.NavigationAddress;
        if (string.IsNullOrWhiteSpace(address)) { ReportStatus("当前没有可打开的地址。"); return; }
        ExternalOpenRequested?.Invoke(this, address);
    }

    /// <summary>分享当前真实地址（复制为文本）；没有真实地址时如实提示，不伪造成功。</summary>
    [RelayCommand] private void Share()
    {
        var address = ActiveTab.NavigationAddress;
        if (string.IsNullOrWhiteSpace(address)) { ReportStatus("当前没有可分享的地址。"); return; }
        ShareRequested?.Invoke(this, address);
    }

    /// <summary>切换当前真实地址的收藏状态；无真实地址时不写入，避免收藏草稿。</summary>
    [RelayCommand]
    private void ToggleBookmark()
    {
        var address = ActiveTab.NavigationAddress;
        if (string.IsNullOrWhiteSpace(address))
        {
            ReportStatus("当前页面还没有可收藏的地址。");
            return;
        }
        var bookmarked = _bookmarks.Contains(address) ? !_bookmarks.Remove(address) : _bookmarks.Add(address);
        NotifyBookmark();
        ReportStatus(bookmarked ? "已收藏当前页面。" : "已取消收藏当前页面。");
    }

    /// <summary>页面导航/切换后刷新星标状态（真实地址变了，收藏状态随之变化）。</summary>
    private void NotifyBookmark()
    {
        OnPropertyChanged(nameof(IsCurrentBookmarked));
        OnPropertyChanged(nameof(BookmarkToolTip));
        BookmarkToggled?.Invoke(this, (ActiveTab.NavigationAddress ?? string.Empty, IsCurrentBookmarked));
    }

    [RelayCommand] private void ToggleFind()
    {
        ActiveTab.IsFindOpen = !ActiveTab.IsFindOpen;
        if (!ActiveTab.IsFindOpen) ActiveTab.CloseFindCommand.Execute(null);
        NotifyFacade();
    }

    public void ApplyZoom(int percent)
    {
        var clamped = Math.Clamp(percent, MinZoomPercent, MaxZoomPercent);
        ActiveTab.ZoomPercent = clamped;
        ZoomRequested?.Invoke(this, (ActiveTab, clamped / 100.0));
        ReportStatus($"缩放 {clamped}%");
        OnPropertyChanged(nameof(ZoomPercent));
    }

    /// <summary>请求把当前页签的缩放因子推给宿主。</summary>
    public event EventHandler<(BrowserTab Tab, double Factor)>? ZoomRequested;

    public const int MinZoomPercent = 50;
    public const int MaxZoomPercent = 200;

    private void Attach(BrowserTab tab)
    {
        tab.NavigationRequested += OnTabNavigationRequested;
        tab.ActionRequested += OnTabActionRequested;
        tab.PropertyChanged += OnTabPropertyChanged;
    }

    private void Detach(BrowserTab tab)
    {
        tab.NavigationRequested -= OnTabNavigationRequested;
        tab.ActionRequested -= OnTabActionRequested;
        tab.PropertyChanged -= OnTabPropertyChanged;
    }

    private void OnTabNavigationRequested(object? sender, BrowserNavigationRequest request)
    {
        if (sender is not BrowserTab tab) return;
        TabNavigationRequested?.Invoke(this, (tab, request));
        if (ReferenceEquals(tab, ActiveTab)) NavigationRequested?.Invoke(this, request);
        NotifyFacade();
    }

    private void OnTabActionRequested(object? sender, BrowserTabAction action)
    {
        if (sender is not BrowserTab tab) return;
        TabActionRequested?.Invoke(this, (tab, action));
        if (ReferenceEquals(tab, ActiveTab) && ToSurfaceAction(action) is { } legacy) ActionRequested?.Invoke(this, legacy);
    }

    private static BrowserSurfaceAction? ToSurfaceAction(BrowserTabAction action) => action switch
    {
        BrowserTabAction.Back => BrowserSurfaceAction.Back,
        BrowserTabAction.Forward => BrowserSurfaceAction.Forward,
        BrowserTabAction.Reload => BrowserSurfaceAction.Reload,
        BrowserTabAction.Stop => BrowserSurfaceAction.Stop,
        _ => null,
    };

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (ReferenceEquals(sender, ActiveTab)) OnPropertyChanged(e.PropertyName switch
        {
            nameof(BrowserTab.AddressText) => nameof(AddressText),
            nameof(BrowserTab.DocumentTitle) => nameof(DocumentTitle),
            nameof(BrowserTab.IsLoading) => nameof(IsLoading),
            nameof(BrowserTab.Progress) => nameof(Progress),
            nameof(BrowserTab.CanGoBack) => nameof(CanGoBack),
            nameof(BrowserTab.CanGoForward) => nameof(CanGoForward),
            nameof(BrowserTab.StatusText) => nameof(StatusText),
            nameof(BrowserTab.Mode) => nameof(Mode),
            nameof(BrowserTab.CurrentFilePath) => nameof(CurrentFilePath),
            nameof(BrowserTab.FindQuery) => nameof(FindQuery),
            nameof(BrowserTab.IsFindOpen) => nameof(IsFindOpen),
            nameof(BrowserTab.FindStatus) => nameof(FindStatus),
            nameof(BrowserTab.ZoomPercent) => nameof(ZoomPercent),
            nameof(BrowserTab.Security) => nameof(Security),
            nameof(BrowserTab.DisplayAddress) => nameof(DisplayAddress),
            _ => e.PropertyName ?? string.Empty,
        });
        if (ReferenceEquals(sender, ActiveTab) &&
            e.PropertyName is nameof(BrowserTab.Security) or nameof(BrowserTab.NavigationAddress))
        {
            OnPropertyChanged(nameof(ShowSecurity));
            OnPropertyChanged(nameof(IsSecure));
            OnPropertyChanged(nameof(IsInsecure));
            OnPropertyChanged(nameof(SecurityToolTip));
            NotifyBookmark();
        }
    }

    private void NotifyFacade()
    {
        OnPropertyChanged(nameof(AddressText));
        OnPropertyChanged(nameof(DocumentTitle));
        OnPropertyChanged(nameof(IsLoading));
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(Mode));
        OnPropertyChanged(nameof(IsFileMode));
        OnPropertyChanged(nameof(ModeText));
        OnPropertyChanged(nameof(CurrentFilePath));
        OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(FindQuery));
        OnPropertyChanged(nameof(IsFindOpen));
        OnPropertyChanged(nameof(FindStatus));
        OnPropertyChanged(nameof(ZoomPercent));
        OnPropertyChanged(nameof(Security));
        OnPropertyChanged(nameof(ShowSecurity));
        OnPropertyChanged(nameof(IsSecure));
        OnPropertyChanged(nameof(IsInsecure));
        OnPropertyChanged(nameof(SecurityToolTip));
        OnPropertyChanged(nameof(DisplayAddress));
        OnPropertyChanged(nameof(IsCurrentBookmarked));
        OnPropertyChanged(nameof(BookmarkToolTip));
    }

    private void OnTabCollectionChanged()
    {
        SyncSelection();
        OnPropertyChanged(nameof(CanCloseTab));
        OnPropertyChanged(nameof(TabCountText));
        CloseTabByIdCommand.NotifyCanExecuteChanged();
    }

    partial void OnActiveTabChanged(BrowserTab value)
    {
        value.ApplyShowFullUrl(_showFullUrl);
        SyncSelection();
        NotifyFacade();
        OnPropertyChanged(nameof(NavigateCommand));
        OnPropertyChanged(nameof(BackCommand));
        OnPropertyChanged(nameof(ForwardCommand));
        OnPropertyChanged(nameof(ReloadCommand));
        OnPropertyChanged(nameof(StopCommand));
        OnPropertyChanged(nameof(FindNextCommand));
        OnPropertyChanged(nameof(FindPreviousCommand));
        OnPropertyChanged(nameof(CloseFindCommand));
        OnPropertyChanged(nameof(OpenDevToolsCommand));
        OnPropertyChanged(nameof(ActiveTabOrdinal));
    }

    private void SyncSelection()
    {
        foreach (var tab in Tabs) tab.IsSelected = ReferenceEquals(tab, ActiveTab);
    }

    partial void OnRuntimeStateChanged(BrowserRuntimeState value)
    {
        OnPropertyChanged(nameof(IsRuntimeAvailable));
        OnPropertyChanged(nameof(HasRuntimeNotice));
    }
}
