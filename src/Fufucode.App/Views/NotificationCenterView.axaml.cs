using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Fufucode.App.ViewModels;

namespace Fufucode.App.Views;

public partial class NotificationCenterView : UserControl
{
    internal bool HasChannelPromptForTesting => ChannelPrompt.IsVisible;
    internal string? ChannelPromptForTesting => ChannelPromptText.Text;
    internal void SetCloseAllowed(bool allowed)
    { ClosePageButton.IsEnabled = allowed; ToolTip.SetTip(ClosePageButton, allowed ? "关闭通知页" : "请先取消固定页签"); }
    internal void ShowChannelPrompt(NotificationItemViewModel? notice)
    {
        ChannelPrompt.IsVisible = notice is not null;
        ChannelPromptText.Text = notice is null ? "" : notice.Title + "\n" + notice.Message;
    }
    public NotificationCenterView()
    {
        InitializeComponent();
        KeyDown += (_, args) =>
        {
            if (args.Key != Key.Escape || DataContext is not NotificationCenterViewModel vm) return;
            vm.CloseCommand.Execute(null);
            args.Handled = true;
        };
    }

    private void Activate_Click(object? sender, RoutedEventArgs args)
    {
        if (DataContext is NotificationCenterViewModel vm && sender is Button { DataContext: NotificationItemViewModel item })
            vm.ActivateCommand.Execute(item);
    }
}
