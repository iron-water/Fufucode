using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fufucode.App.Infrastructure.Browser;
using Fufucode.App.ViewModels;

namespace Fufucode.App.ViewModels;

/// <summary>预览面的两种用法：自由导航（输入网址）与文件预览（接收本地路径）。</summary>
public enum BrowserSurfaceMode
{
    Free,
    File,
}

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
/// 浏览器面状态机。刻意不引用任何 WebView2 类型：宿主通过事件驱动，本类可以完全在无头环境断言。
/// 文件放在 Views/ 是因为它只服务于 BrowserView* 这一组文件。
/// </summary>
public sealed partial class BrowserViewModel : ViewModelBase
{
    public BrowserViewModel()
    {
        RuntimeLink = WebView2RuntimeGate.RuntimeDownloadUrl;
    }

    /// <summary>请求宿主导航（Address 或 Document 二选一）。</summary>
    public event EventHandler<BrowserNavigationRequest>? NavigationRequested;

    /// <summary>请求宿主执行后退/前进/刷新/停止。</summary>
    public event EventHandler<BrowserSurfaceAction>? ActionRequested;

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
    [ObservableProperty] private BrowserRuntimeState _runtimeState = BrowserRuntimeState.Unknown;
    [ObservableProperty] private string _runtimeMessage = string.Empty;

    public string RuntimeLink { get; }

    public bool IsRuntimeAvailable => RuntimeState == BrowserRuntimeState.Available;

    /// <summary>探测完成且不可用才提示；初始未探测（Message 为空）不遮挡宿主。</summary>
    public bool HasRuntimeNotice => !IsRuntimeAvailable && RuntimeMessage.Length > 0;
    public bool CanStop => IsLoading;
    public string ModeText => Mode == BrowserSurfaceMode.File ? "文件预览" : "自由导航";
    public bool IsFileMode => Mode == BrowserSurfaceMode.File;

    /// <summary>地址栏提交：归一化后交给宿主；被白名单拒绝时只提示，不改变当前页面。</summary>
    [RelayCommand]
    private void Navigate()
    {
        var target = BrowserAddressResolver.Resolve(AddressText, ProjectRoot);
        if (!target.IsNavigable)
        {
            StatusText = target.Note ?? "无法导航。";
            return;
        }
        Mode = BrowserSurfaceMode.Free;
        CurrentFilePath = null;
        AddressText = target.Address ?? string.Empty;
        IsLoading = true;
        Progress = 0;
        StatusText = "正在加载…";
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
        AddressText = target.FullPath ?? string.Empty;
        IsLoading = true;
        Progress = 0;
        StatusText = $"预览{target.KindText}：{target.Name}";
        NavigationRequested?.Invoke(this, new BrowserNavigationRequest(target.Address, target.Document, Mode, target.Name));
    }

    /// <summary>宿主把真实导航状态回写给 VM（后退/前进可用性来自 WebView2 历史，不猜测）。</summary>
    public void ApplyNavigationState(bool canGoBack, bool canGoForward, bool loading, string? source)
    {
        CanGoBack = canGoBack;
        CanGoForward = canGoForward;
        IsLoading = loading;
        Progress = loading ? 0 : 1;
        if (loading) StatusText = "正在加载…";
        if (source is { Length: > 0 } && Mode == BrowserSurfaceMode.Free) AddressText = source;
    }

    public void ApplyDocumentTitle(string? title) => DocumentTitle = title is { Length: > 0 } ? title : string.Empty;

    public void ReportStatus(string message) => StatusText = message;

    public void ApplyRuntimeProbe(BrowserRuntimeProbe probe)
    {
        RuntimeState = probe.State;
        RuntimeMessage = probe.IsAvailable ? probe.Message : probe.Guidance;
        if (!probe.IsAvailable) ReportStatus("内置浏览器不可用：" + probe.Guidance);
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void Back() => ActionRequested?.Invoke(this, BrowserSurfaceAction.Back);

    [RelayCommand(CanExecute = nameof(CanGoForward))]
    private void Forward() => ActionRequested?.Invoke(this, BrowserSurfaceAction.Forward);

    [RelayCommand]
    private void Reload() => ActionRequested?.Invoke(this, BrowserSurfaceAction.Reload);

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop() => ActionRequested?.Invoke(this, BrowserSurfaceAction.Stop);

    partial void OnCanGoBackChanged(bool value) => BackCommand.NotifyCanExecuteChanged();
    partial void OnCanGoForwardChanged(bool value) => ForwardCommand.NotifyCanExecuteChanged();
    partial void OnIsLoadingChanged(bool value)
    {
        StopCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanStop));
    }
    partial void OnModeChanged(BrowserSurfaceMode value)
    {
        OnPropertyChanged(nameof(ModeText));
        OnPropertyChanged(nameof(IsFileMode));
    }
    partial void OnRuntimeStateChanged(BrowserRuntimeState value)
    {
        OnPropertyChanged(nameof(IsRuntimeAvailable));
        OnPropertyChanged(nameof(HasRuntimeNotice));
    }
}
