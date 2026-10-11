using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Fufucode.App.ViewModels;

namespace Fufucode.App;

public partial class MainWindow
{
    private AppPage _settingsReturnPage = AppPage.Chat;
    private Control? _settingsReturnFocus;
    private bool _navigationPending;
    internal Func<string, Task<bool>>? ConfirmDiscardSettingsForTesting;
    internal Task NavigateForTestingAsync(AppPage page) => NavigateWorkspaceAsync(page);

    private async Task NavigateWorkspaceAsync(AppPage page)
    {
        if (_vm is not { } vm || _navigationPending || page == vm.CurrentPage) return;
        _navigationPending = true;
        try
        {
            if (vm.IsSettingsPage && !await ConfirmLeaveSettingsAsync()) return;
            if (page == AppPage.Settings)
            {
                _settingsReturnPage = vm.CurrentPage;
                _settingsReturnFocus = _overlayReturnFocus ?? FocusManager?.GetFocusedElement() as Control;
            }
            var returning = vm.IsSettingsPage;
            vm.CurrentPage = page;
            RecordNavigation();
            if (returning) RestoreFocus(_settingsReturnFocus);
        }
        finally { _navigationPending = false; }
    }

    private async Task<bool> ConfirmLeaveSettingsAsync()
    {
        if (_vm?.Settings.IsSettingsBusy == true)
        {
            _vm.Timeline.AddNotification("设置正在保存或应用，请等待完成后返回。");
            return false;
        }
        if (_vm?.Settings is not { HasUnsavedChanges: true } settings) return true;
        var discard = ConfirmDiscardSettingsForTesting is { } test
            ? await test(settings.UnsavedChangesText)
            : await AskDiscardSettingsAsync(settings.UnsavedChangesText);
        if (discard) settings.DiscardUnsavedChanges();
        return discard;
    }

    private Task<bool> AskDiscardSettingsAsync(string changes)
    {
        var keep = new Button { Content = "继续编辑", MinWidth = 96, IsCancel = true };
        var discard = new Button { Content = "丢弃草稿", MinWidth = 96 };
        var dialog = new Window
        {
            Title = "尚未保存的设置", Width = 440, Height = 220,
            CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(24), Spacing = 16,
                Children =
                {
                    new TextBlock { Text = "返回前处理设置草稿", FontSize = 20 },
                    new TextBlock { Text = changes, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right,
                        Children = { keep, discard },
                    },
                },
            },
        };
        keep.Click += (_, _) => dialog.Close(false);
        discard.Click += (_, _) => dialog.Close(true);
        dialog.Opened += (_, _) => keep.Focus();
        return dialog.ShowDialog<bool>(this);
    }

    private void WireSettingsReturn(MainViewModel vm)
    {
        vm.Settings.ReturnRequested += async (_, _) => await NavigateWorkspaceAsync(_settingsReturnPage);
    }
}
