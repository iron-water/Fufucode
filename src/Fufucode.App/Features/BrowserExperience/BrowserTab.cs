using System;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fufucode.App.Infrastructure.Browser;
using Fufucode.App.ViewModels;

namespace Fufucode.App.Features.BrowserExperience;

/// <summary>地址栏安全态：来自真实导航地址的协议，不做猜测。</summary>
public enum BrowserSecurityState
{
    /// <summary>不显示锁图标（文件预览、about:blank、内部页）。</summary>
    None,

    /// <summary>HTTPS：显示锁图标。</summary>
    Secure,

    /// <summary>HTTP：显示不安全提示。</summary>
    Insecure,
}

/// <summary>页内查找/缩小增大的动作，由页签视图转成宿主调用。</summary>
public enum BrowserTabAction
{
    Back,
    Forward,
    Reload,
    Stop,
    FindNext,
    FindPrevious,
    CloseFind,
    OpenDevTools,
}

/// <summary>
/// 一个浏览器页签的独立状态机：地址草稿、真实导航地址、历史可用性、标题、查找与缩放各自独立。
/// 刻意不引用 WebView2 类型，宿主通过事件驱动，本类可以在无头环境完整断言。
/// </summary>
public sealed partial class BrowserTab : ObservableObject
{
    public BrowserTab(string id)
    {
        Id = id;
    }

    public string Id { get; }

    /// <summary>请求宿主导航（Address 或 Document 二选一）。</summary>
    public event EventHandler<BrowserNavigationRequest>? NavigationRequested;

    /// <summary>请求宿主执行后退/前进/刷新/停止/查找/开发工具。</summary>
    public event EventHandler<BrowserTabAction>? ActionRequested;

    [ObservableProperty] private string _addressText = string.Empty;
    [ObservableProperty] private string _documentTitle = string.Empty;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _canGoBack;
    [ObservableProperty] private bool _canGoForward;
    [ObservableProperty] private string _statusText = "输入网址开始浏览，或打开本地文件预览。";
    [ObservableProperty] private BrowserSurfaceMode _mode = BrowserSurfaceMode.Free;
    [ObservableProperty] private string? _currentFilePath;
    [ObservableProperty] private string? _projectRoot;
    [ObservableProperty] private string _findQuery = string.Empty;
    [ObservableProperty] private bool _isFindOpen;
    [ObservableProperty] private string _findStatus = string.Empty;
    [ObservableProperty] private int _zoomPercent = 100;
    [ObservableProperty] private string? _navigationAddress;
    [ObservableProperty] private bool _isSelected;
    /// <summary>地址栏来源的安全态：null/空 = 不显示锁图标（文件预览、about:blank 与新页签）。</summary>
    [ObservableProperty] private BrowserSecurityState _security = BrowserSecurityState.None;
    /// <summary>当前页签的地址显示文本：显示完整网址关闭时只显示主机名。</summary>
    [ObservableProperty] private string _displayAddress = string.Empty;
    /// <summary>地址栏是否被用户改过而未提交；草稿不能被后台导航回写覆盖（浏览器语义）。</summary>
    private bool _addressDirty;
    private bool _syncingAddress;

    /// <summary>是否在地址栏显示完整网址（含协议/路径/查询）；关闭时只显示主机名。由浏览器设置驱动。</summary>
    public bool ShowFullUrl { get; private set; } = true;

    /// <summary>地址栏普通文字的搜索 URL 模板；由设置同步。</summary>
    public string SearchUrlTemplate { get; private set; } = BrowserAddressResolver.DefaultSearchTemplate;

    /// <summary>由设置同步显示策略；变化时立即重算当前显示文本，不触发导航。</summary>
    public void ApplyShowFullUrl(bool showFullUrl)
    {
        if (ShowFullUrl == showFullUrl) return;
        ShowFullUrl = showFullUrl;
        RecomputeDisplayAddress(AddressText);
    }

    public void ApplySearchUrlTemplate(string? template)
        => SearchUrlTemplate = string.IsNullOrWhiteSpace(template) ? BrowserAddressResolver.DefaultSearchTemplate : template;

    public bool CanStop => IsLoading;
    public string ModeText => Mode == BrowserSurfaceMode.File ? "文件预览" : "自由导航";
    public bool IsFileMode => Mode == BrowserSurfaceMode.File;
    /// <summary>锁图标是否显示：只有真实 https/http 页面才显示安全态。</summary>
    public bool ShowSecurity => Security != BrowserSecurityState.None;
    public bool IsSecure => Security == BrowserSecurityState.Secure;
    public bool IsInsecure => Security == BrowserSecurityState.Insecure;
    public string SecurityToolTip => Security switch
    {
        BrowserSecurityState.Secure => "连接安全（HTTPS）",
        BrowserSecurityState.Insecure => "连接不安全（HTTP）：此页面未加密",
        _ => "当前页面没有可显示的安全信息",
    };

    public string TabHeader => string.IsNullOrWhiteSpace(DocumentTitle)
        ? (string.IsNullOrWhiteSpace(NavigationAddress) ? "新页签" : Shorten(NavigationAddress!))
        : DocumentTitle;

    /// <summary>智能体审批用的当前页面主机名（非 web 页面为空）。</summary>
    public string SecureHostForAgent => BrowserSiteRules.HostOf(NavigationAddress) ?? string.Empty;

    /// <summary>地址栏提交：归一化后交给宿主；被白名单拒绝时只提示，不改变当前页面。</summary>
    [RelayCommand]
    private void Navigate()
    {
        var target = BrowserAddressResolver.Resolve(AddressText, ProjectRoot, SearchUrlTemplate);
        if (!target.IsNavigable)
        {
            StatusText = target.Note ?? "无法导航。";
            return;
        }
        Mode = BrowserSurfaceMode.Free;
        CurrentFilePath = null;
        NavigationAddress = target.Address;
        SetAddressFromNavigation(target.Address ?? string.Empty);
        ApplySecurityFromAddress(target.Address);
        IsLoading = true;
        Progress = 0;
        StatusText = target.Kind == BrowserTargetKind.Search ? $"搜索：{target.Query}" : "正在加载…";
        NavigationRequested?.Invoke(this, new BrowserNavigationRequest(target.Address, null, Mode, target.Address ?? string.Empty));
    }

    /// <summary>接收本地路径进入文件预览模式。不受支持的类型只提示，不导航。</summary>
    public void PreviewFile(string? path)
    {
        var target = BrowserFilePreview.Inspect(path);
        if (!target.IsSupported)
        {
            StatusText = target.Note ?? "无法预览该文件。";
            return;
        }
        Mode = BrowserSurfaceMode.File;
        CurrentFilePath = target.FullPath;
        NavigationAddress = target.Address;
        SetAddressFromNavigation(target.FullPath ?? string.Empty);
        Security = BrowserSecurityState.None;
        IsLoading = true;
        Progress = 0;
        StatusText = $"预览{target.KindText}：{target.Name}";
        NavigationRequested?.Invoke(this, new BrowserNavigationRequest(target.Address, target.Document, Mode, target.Name));
    }

    /// <summary>宿主把真实导航状态回写给本页签（后退/前进可用性来自 WebView2 历史，不猜测）。</summary>
    public void ApplyNavigationState(bool canGoBack, bool canGoForward, bool loading, string? source)
    {
        CanGoBack = canGoBack;
        CanGoForward = canGoForward;
        IsLoading = loading;
        Progress = loading ? 0 : 1;
        if (loading) StatusText = "正在加载…";
        if (source is { Length: > 0 })
        {
            NavigationAddress = source == "about:blank" ? null : source;
            // 用户改过地址栏且未提交时保留草稿，不被后台导航回写覆盖。
            if (Mode == BrowserSurfaceMode.Free && !_addressDirty) SetAddressFromNavigation(source);
            ApplySecurityFromAddress(Mode == BrowserSurfaceMode.Free ? source : null);
        }
        OnPropertyChanged(nameof(TabHeader));
    }

    /// <summary>只有真实 https/http 页面才点亮锁图标；本地文件、about:blank 与内部页不显示。</summary>
    private void ApplySecurityFromAddress(string? address)
    {
        Security = Uri.TryCreate(address, UriKind.Absolute, out var uri)
            ? uri.Scheme switch
            {
                "https" => BrowserSecurityState.Secure,
                "http" => BrowserSecurityState.Insecure,
                _ => BrowserSecurityState.None,
            }
            : BrowserSecurityState.None;
        OnPropertyChanged(nameof(SecurityToolTip));
    }

    partial void OnAddressTextChanged(string value)
    {
        if (!_syncingAddress) _addressDirty = true;
    }

    private void SetAddressFromNavigation(string value)
    {
        _syncingAddress = true;
        try { AddressText = value; }
        finally { _syncingAddress = false; }
        _addressDirty = false;
        RecomputeDisplayAddress(value);
    }

    /// <summary>显示完整网址关闭时只显示主机名（对齐 Codex/Chrome 的省略显示）。</summary>
    private void RecomputeDisplayAddress(string value)
    {
        if (ShowFullUrl) { DisplayAddress = value; return; }
        DisplayAddress = Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Host.Length > 0
            ? uri.Host
            : value;
    }

    public void ApplyDocumentTitle(string? title)
    {
        DocumentTitle = title is { Length: > 0 } ? title : string.Empty;
        OnPropertyChanged(nameof(TabHeader));
    }

    public void ReportStatus(string message) => StatusText = message;

    [RelayCommand(CanExecute = nameof(CanGoBack))] private void Back() => ActionRequested?.Invoke(this, BrowserTabAction.Back);
    [RelayCommand(CanExecute = nameof(CanGoForward))] private void Forward() => ActionRequested?.Invoke(this, BrowserTabAction.Forward);
    [RelayCommand] private void Reload() => ActionRequested?.Invoke(this, BrowserTabAction.Reload);
    [RelayCommand(CanExecute = nameof(CanStop))] private void Stop() => ActionRequested?.Invoke(this, BrowserTabAction.Stop);
    [RelayCommand] private void FindNext() => ActionRequested?.Invoke(this, BrowserTabAction.FindNext);
    [RelayCommand] private void FindPrevious() => ActionRequested?.Invoke(this, BrowserTabAction.FindPrevious);
    [RelayCommand] private void CloseFind() { IsFindOpen = false; FindStatus = string.Empty; ActionRequested?.Invoke(this, BrowserTabAction.CloseFind); }
    [RelayCommand] private void OpenDevTools() => ActionRequested?.Invoke(this, BrowserTabAction.OpenDevTools);

    partial void OnCanGoBackChanged(bool value) => BackCommand.NotifyCanExecuteChanged();
    partial void OnCanGoForwardChanged(bool value) => ForwardCommand.NotifyCanExecuteChanged();
    partial void OnIsLoadingChanged(bool value) { StopCommand.NotifyCanExecuteChanged(); OnPropertyChanged(nameof(CanStop)); }
    partial void OnModeChanged(BrowserSurfaceMode value) { OnPropertyChanged(nameof(ModeText)); OnPropertyChanged(nameof(IsFileMode)); }
    partial void OnDocumentTitleChanged(string value) => OnPropertyChanged(nameof(TabHeader));
    partial void OnNavigationAddressChanged(string? value) => OnPropertyChanged(nameof(TabHeader));
    partial void OnSecurityChanged(BrowserSecurityState value)
    {
        OnPropertyChanged(nameof(ShowSecurity));
        OnPropertyChanged(nameof(IsSecure));
        OnPropertyChanged(nameof(IsInsecure));
        OnPropertyChanged(nameof(SecurityToolTip));
    }

    internal BrowserTabState CaptureState() => new(Id, AddressText, NavigationAddress, CurrentFilePath, DocumentTitle);

    private static string Shorten(string value) => value.Length <= 24 ? value : value[..23] + "…";
}
