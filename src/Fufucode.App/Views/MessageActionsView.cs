using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.VisualTree;
using Fufucode.App.ViewModels;
using Fufucode.App.Infrastructure;

namespace Fufucode.App.Views;

public sealed class MessageActionsView : UserControl
{
    private WorkbenchAppearance _appearance = new();
    internal void ApplyAppearance(WorkbenchAppearance appearance)
    { _appearance = appearance; UpdateVisibility(); }
    private void UpdateVisibility()
    {
        if (Content is Control content) content.Opacity = _appearance.MessageActions == "show" || _appearance.MessageActions == "hover" && IsPointerOver ? 1 : 0;
        IsHitTestVisible = _appearance.MessageActions != "hide";
    }
    public MessageActionsView()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(0,6,0,0) };
        var copy = WorkbenchUi.IconButton("Copy","复制消息");
        var quote = WorkbenchUi.IconButton("ArrowReply","引用到主会话");
        var edit = WorkbenchUi.IconButton("Edit","编辑成新草稿");
        var ask = WorkbenchUi.IconButton("Chat","在侧边聊天中提问");
        var more = WorkbenchUi.IconButton("More", "更多消息操作");
        var menu = new MenuFlyout();
        var fork = new MenuItem { Header = "从此消息分叉" };
        var retry = new MenuItem { Header = "分叉并重试" };
        fork.Click += async (_, _) => await ForkAsync(false);
        retry.Click += async (_, _) => await ForkAsync(true);
        menu.Items.Add(fork); menu.Items.Add(retry);
        more.Flyout = menu;
        copy.Click += async (_, _) =>
        {
            try
            {
                if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
                    await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard,MessageText(DataContext));
            }
            catch (Exception error) { FindChat()?.ReportActionError("复制失败：" + error.Message); }
        };
        quote.Click += (_, _) => FindChat()?.AddContext(MessageText(DataContext),Source());
        edit.Click += (_, _) => FindChat()?.EditMessage(MessageText(DataContext));
        ask.Click += (_, _) => FindChat()?.AskAbout(MessageText(DataContext),Source());
        row.Children.Add(copy); row.Children.Add(quote); row.Children.Add(edit); row.Children.Add(ask); row.Children.Add(more);
        DataContextChanged += (_, _) => edit.IsVisible = DataContext is UserMessageViewModel;
        Content = row;
        AttachedToVisualTree += (_, _) => { if (FindChat() is { } chat) ApplyAppearance(chat.Appearance); };
        PointerEntered += (_, _) => { if (Content is Control content && _appearance.MessageActions == "hover") { content.Opacity = 1; IsHitTestVisible = true; } };
        PointerExited += (_, _) => UpdateVisibility();
    }

    private async System.Threading.Tasks.Task ForkAsync(bool retry)
    {
        try { if (TopLevel.GetTopLevel(this) is MainWindow window && DataContext is TimelineEntryViewModel source) await window.ForkFromMessageAsync(source, retry, FindChat()); }
        catch (Exception error) { FindChat()?.ReportActionError(error.Message); }
    }

    private ChatPage? FindChat() => this.GetVisualAncestors().OfType<ChatPage>().FirstOrDefault();
    private string Source() => (DataContext is UserMessageViewModel ? "用户消息" : "助手回复") + " · " + (DataContext as TimelineEntryViewModel)?.Id;
    internal static string MessageText(object? message) => message switch
    {
        UserMessageViewModel user => user.Text,
        AssistantMessageViewModel assistant => string.Join("\n\n",assistant.Blocks.OfType<TextBlockViewModel>().Select(block => block.Text)),
        _ => string.Empty,
    };
}
