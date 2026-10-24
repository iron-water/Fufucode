using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
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

    /// <summary>卡片进入：淡入并轻微上移。</summary>
    private void Card_Attached(object? sender, Avalonia.VisualTreeAttachmentEventArgs e)
    {
        if (sender is Control card) Motion.AnimateIn(card, offsetY: 4, Motion.FastDuration);
    }

    /// <summary>移除一条通知：先播放退场动画，动画结束后再从列表删除。</summary>
    private void Dismiss_Click(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not NotificationCenterViewModel vm ||
            sender is not Button { DataContext: NotificationItemViewModel item } button) return;
        var card = button.FindAncestorOfType<Border>();
        if (card is null) { vm.DismissItemCommand.Execute(item); return; }
        Motion.AnimateOut(card, offsetY: 0, () => vm.DismissItemCommand.Execute(item));
    }
}
