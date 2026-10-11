using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Fufucode.App.ViewModels;
using Fufucode.App.Infrastructure.Browser;

namespace Fufucode.App.Views;

/// <summary>
/// 右栏 Preview 的内容：地址栏 + 后退/前进/刷新/停止 + 加载进度 + WebView2 宿主。
/// 自带 BrowserViewModel（不继承外层 DataContext），因此可以整体搬到右栏任意位置。
/// </summary>
public partial class BrowserView : UserControl, IDisposable
{
    private readonly BrowserViewModel _viewModel = new();
    private readonly BrowserViewHost _host;
    private string? _navigationAddress;

    public BrowserView()
    {
        DataContext = _viewModel;
        InitializeComponent();
        _host = this.FindControl<BrowserViewHost>("Host")
                ?? throw new InvalidOperationException("BrowserView 缺少 WebView2 宿主（x:Name=Host）。");

        _viewModel.NavigationRequested += (_, request) =>
        {
            _navigationAddress = request.Address;
            if (request.Document is not null) _host.NavigateToDocument(request.Document);
            else _host.Navigate(request.Address);
        };
        _viewModel.ActionRequested += (_, action) =>
        {
            switch (action)
            {
                case BrowserSurfaceAction.Back: _host.GoBack(); break;
                case BrowserSurfaceAction.Forward: _host.GoForward(); break;
                case BrowserSurfaceAction.Reload: _host.Reload(); break;
                case BrowserSurfaceAction.Stop: _host.Stop(); break;
            }
        };
        _host.NavigationStateChanged += (_, state) =>
        {
            if (state.Source is { Length: > 0 } && state.Source != "about:blank") _navigationAddress = state.Source;
            _viewModel.ApplyNavigationState(state.CanGoBack, state.CanGoForward, state.Loading, state.Source);
        };
        _host.StatusChanged += (_, message) => _viewModel.ReportStatus(message);
        _host.RuntimeProbeCompleted += (_, probe) => _viewModel.ApplyRuntimeProbe(probe);
        _host.DocumentTitleChanged += (_, title) => _viewModel.ApplyDocumentTitle(title);
    }

    public BrowserViewModel ViewModel => _viewModel;
    internal BrowserViewHost HostForTesting => _host;
    public void Dispose() => _host.Dispose();
    internal BrowserPageState CaptureState() => new(AddressDraft: _viewModel.AddressText,
        NavigationAddress: _navigationAddress, FilePath: _viewModel.CurrentFilePath);

    internal void RestoreState(BrowserPageState? state)
    {
        if (state is null) return;
        if (state.Version != 1)
        {
            _viewModel.AddressText = "";
            _viewModel.NavigateCommand.Execute(null);
            _viewModel.ReportStatus("浏览器页面状态版本不受支持，已恢复空页。");
            return;
        }
        if (!string.IsNullOrWhiteSpace(state.FilePath)) _viewModel.PreviewFile(state.FilePath);
        else if (!string.IsNullOrWhiteSpace(state.NavigationAddress))
        {
            _viewModel.AddressText = state.NavigationAddress;
            _viewModel.NavigateCommand.Execute(null);
        }
        _viewModel.AddressText = state.AddressDraft ?? "";
    }

    /// <summary>直接向本实例推送本地路径进入文件预览模式。</summary>
    public void PreviewFile(string? path) => _viewModel.PreviewFile(path);

    /// <summary>seam 上声明的 x:Name，工作区据此用 TryPreviewFile 定位。</summary>
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

    private void Address_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Return && e.Key != Key.Enter) return;
        if (_viewModel.NavigateCommand.CanExecute(null)) _viewModel.NavigateCommand.Execute(null);
    }

    private async void OpenFile_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
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

    private async void OpenRuntimeDownload_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) { _viewModel.ReportStatus("当前环境无法打开外部链接。"); return; }
        try { await topLevel.Launcher.LaunchUriAsync(new Uri(_viewModel.RuntimeLink)); }
        catch (Exception error) { _viewModel.ReportStatus("打开下载页失败：" + error.Message); }
    }

    private async void CopyRuntimeLink_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
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
