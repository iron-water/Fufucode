using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Fufucode.App.Infrastructure;
using Fufucode.App.Integration;

namespace Fufucode.App.Views;

public partial class BrowserView
{
    private readonly ImeEnterGuard _addressIme = new();
    private readonly ImeEnterGuard _findIme = new();
    private readonly System.Collections.Generic.List<Action> _imeDetachers = new();

    private void WireBrowserIme(string name, ImeEnterGuard guard)
    {
        if (this.FindControl<TextBox>(name) is not { } box) return;
        Avalonia.Controls.Presenters.TextPresenter? presenter = null;
        void Changed(object? sender, Avalonia.AvaloniaPropertyChangedEventArgs args)
        {
            if (args.Property == Avalonia.Controls.Presenters.TextPresenter.PreeditTextProperty)
                guard.OnPreeditChanged(presenter?.PreeditText);
        }
        void Attach()
        {
            var next = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(box)
                .OfType<Avalonia.Controls.Presenters.TextPresenter>().FirstOrDefault();
            if (ReferenceEquals(next, presenter)) return;
            if (presenter is not null) presenter.PropertyChanged -= Changed;
            presenter = next;
            if (presenter is not null) presenter.PropertyChanged += Changed;
            guard.OnPreeditChanged(presenter?.PreeditText);
        }
        void Applied(object? sender, TemplateAppliedEventArgs args) => Attach();
        box.TemplateApplied += Applied;
        Attach();
        _imeDetachers.Add(() =>
        {
            box.TemplateApplied -= Applied;
            if (presenter is not null) presenter.PropertyChanged -= Changed;
        });
    }

    private void UpdateBrowserLayout()
    {
        _viewModel.IsCompact = Bounds.Width < 640;
        _viewModel.PanelWidth = Math.Clamp(Bounds.Width - 16, 240, 400);
        UpdateBrowserMotion();
    }

    private void UpdateBrowserMotion()
    {
        if (Dispatcher.UIThread.CheckAccess()) _viewModel.AnimateLoading = !Motion.Reduced && !MemoryProfile.DisableVisualEffects;
        else Dispatcher.UIThread.Post(UpdateBrowserMotion);
    }

    private void WireBrowserPopup(string name, string surfaceName, string? focusName)
    {
        if (this.FindControl<Popup>(name) is not { } popup) return;
        Control? returnFocus = null;
        popup.Opened += (_, _) =>
        {
            returnFocus = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Control;
            if (this.FindControl<Control>(surfaceName) is { } surface) SurfaceMotion.Show(surface, offsetY: -4);
            if (focusName is not null) this.FindControl<Control>(focusName)?.Focus();
        };
        popup.Closed += (_, _) =>
        {
            if (_disposed || !IsEffectivelyVisible) return;
            if (returnFocus?.IsEffectivelyVisible == true) returnFocus.Focus();
            else this.FindControl<Button>("MoreButton")?.Focus();
        };
    }

    private void ShowBrowserFeedback(string message)
    {
        if (_disposed || !IsEffectivelyVisible || string.IsNullOrWhiteSpace(message) ||
            message is "加载完成" or "正在加载…" || message.StartsWith("输入网址", StringComparison.Ordinal) ||
            message.StartsWith("预览", StringComparison.Ordinal) || message.StartsWith("搜索：", StringComparison.Ordinal)) return;
        _viewModel.FeedbackText = message;
        _viewModel.IsFeedbackOpen = true;
        _feedbackTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _feedbackTimer.Tick -= CloseBrowserFeedback;
        _feedbackTimer.Tick += CloseBrowserFeedback;
        _feedbackTimer.Stop();
        _feedbackTimer.Start();
    }

    private void CloseBrowserFeedback(object? sender, EventArgs e)
    {
        _feedbackTimer?.Stop();
        _viewModel.IsFeedbackOpen = false;
    }

    private void Address_GotFocus(object? sender, RoutedEventArgs e)
    {
        _viewModel.IsAddressEditing = true;
    }

    private void Address_LostFocus(object? sender, RoutedEventArgs e) => _viewModel.IsAddressEditing = false;

    private void Browser_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        if (_viewModel.IsSuggestionsOpen || _viewModel.IsHistoryOpen || _viewModel.IsDownloadsOpen || _viewModel.IsFeedbackOpen)
        {
            _viewModel.IsSuggestionsOpen = _viewModel.IsHistoryOpen = _viewModel.IsDownloadsOpen = _viewModel.IsFeedbackOpen = false;
            e.Handled = true;
        }
        else if (_viewModel.IsFindOpen)
        {
            _viewModel.CloseFindCommand.Execute(null);
            this.FindControl<Button>("MoreButton")?.Focus();
            e.Handled = true;
        }
    }

    private void Annotate_Click(object? sender, RoutedEventArgs e)
    {
        if (_hosts.TryGetValue(_viewModel.ActiveTab.Id, out var host))
            _ = StartAnnotationAsync(_viewModel.ActiveTab, host);
    }

    private void Retry_Click(object? sender, RoutedEventArgs e)
    {
        var tab = _viewModel.ActiveTab;
        tab.PageError = string.Empty;
        var host = HostFor(tab);
        if (host.HasController)
        {
            UpdateHostVisibility();
            host.Reload();
        }
        else _ = ActivateTabAsync(tab);
    }

    private async void OpenDownloadFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: string path } || TopLevel.GetTopLevel(this) is not { } top) return;
        var directory = Path.GetDirectoryName(path);
        if (directory is null || !Directory.Exists(directory)) { ShowBrowserFeedback("下载文件夹已不存在"); return; }
        try
        {
            var folder = await top.StorageProvider.TryGetFolderFromPathAsync(new Uri(directory));
            if (folder is null || !await top.Launcher.LaunchFileAsync(folder))
                ShowBrowserFeedback("系统未能打开下载文件夹");
        }
        catch (Exception error) { ShowBrowserFeedback("无法打开下载文件夹：" + error.Message); }
    }
}
