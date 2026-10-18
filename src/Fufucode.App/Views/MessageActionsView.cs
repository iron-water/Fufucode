using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Fufucode.App.Infrastructure;
using Fufucode.App.ViewModels;
using Fufucode.Core.Plugins;

namespace Fufucode.App.Views;

/// <summary>
/// 消息底部操作条。对齐主流 Agent 客户端的做法：
/// 左侧是图标操作（复制 / 点赞 / 点踩 / 朗读 / 引用 / 编辑 / 更多），
/// 右侧是元信息（用量、模型、时间）。
/// 不可用的能力（如未安装语音插件）不显示，而不是显示成可点却无反应。
/// </summary>
public sealed class MessageActionsView : UserControl
{
    private WorkbenchAppearance _appearance = new();
    private readonly Button _copy;
    private readonly Button _like;
    private readonly Button _dislike;
    private readonly Button _speak;
    private readonly Button _quote;
    private readonly Button _edit;
    private readonly Button _more;
    private readonly TextBlock _usage;
    private readonly TextBlock _model;
    private readonly TextBlock _time;

    internal void ApplyAppearance(WorkbenchAppearance appearance)
    {
        _appearance = appearance;
        UpdateVisibility();
    }

    private void UpdateVisibility()
    {
        if (Content is Control content)
            content.Opacity = _appearance.MessageActions == "show" ||
                              (_appearance.MessageActions == "hover" && IsPointerOver) ? 1 : 0;
        IsHitTestVisible = _appearance.MessageActions != "hide";
    }

    public MessageActionsView()
    {
        _copy = WorkbenchUi.IconButton("Copy", "复制消息");
        _like = WorkbenchUi.IconButton("ThumbLike", "有帮助");
        _dislike = WorkbenchUi.IconButton("ThumbDislike", "没帮助");
        _speak = WorkbenchUi.IconButton("Speaker", "朗读这条消息");
        _quote = WorkbenchUi.IconButton("ArrowReply", "引用到主会话");
        _edit = WorkbenchUi.IconButton("Edit", "编辑成新草稿");
        _more = WorkbenchUi.IconButton("More", "更多消息操作");

        _usage = new TextBlock { Classes = { "meta", "tabularNumeric" }, VerticalAlignment = VerticalAlignment.Center, IsVisible = false };
        _model = new TextBlock { Classes = { "meta" }, VerticalAlignment = VerticalAlignment.Center, IsVisible = false };
        _time = new TextBlock { Classes = { "meta", "tabularNumeric" }, VerticalAlignment = VerticalAlignment.Center };

        var icons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        icons.Children.Add(_copy);
        icons.Children.Add(_like);
        icons.Children.Add(_dislike);
        icons.Children.Add(_speak);
        icons.Children.Add(_quote);
        icons.Children.Add(_edit);
        icons.Children.Add(_more);

        var meta = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 10,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0),
        };
        meta.Children.Add(_usage);
        meta.Children.Add(_model);
        meta.Children.Add(_time);

        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"),
            Margin = new Thickness(0, 6, 0, 0),
        };
        Grid.SetColumn(icons, 0);
        Grid.SetColumn(meta, 1);
        row.Children.Add(icons);
        row.Children.Add(meta);

        // 复制菜单：普通文本 / Markdown / 消息 ID（与主流客户端一致）。
        var copyMenu = new MenuFlyout();
        var copyPlain = new MenuItem { Header = "复制纯文本" };
        var copyMarkdown = new MenuItem { Header = "复制 Markdown" };
        var copyIdItem = new MenuItem { Header = "复制消息 ID" };
        copyPlain.Click += async (_, _) => await CopyAsync(MessageText(DataContext));
        copyMarkdown.Click += async (_, _) => await CopyAsync(MessageMarkdown(DataContext));
        copyIdItem.Click += async (_, _) =>
            await CopyAsync((DataContext as TimelineEntryViewModel)?.Id ?? string.Empty);
        copyMenu.Items.Add(copyPlain);
        copyMenu.Items.Add(copyMarkdown);
        copyMenu.Items.Add(copyIdItem);
        _copy.Flyout = copyMenu;

        var menu = new MenuFlyout();
        var fork = new MenuItem { Header = "从此消息分叉" };
        var retry = new MenuItem { Header = "分叉并重试" };
        var ask = new MenuItem { Header = "在侧边聊天中提问" };
        fork.Click += async (_, _) => await ForkAsync(false);
        retry.Click += async (_, _) => await ForkAsync(true);
        ask.Click += (_, _) => FindChat()?.AskAbout(MessageText(DataContext), Source());
        menu.Items.Add(fork);
        menu.Items.Add(retry);
        menu.Items.Add(ask);
        _more.Flyout = menu;

        _copy.Click += async (_, _) => await CopyAsync(MessageText(DataContext));
        // 点赞/点踩：先只做本地反馈（按钮保持按下态），不伪造已上报。
        _like.Click += (_, _) => SetFeedback(_like, _dislike, positive: true);
        _dislike.Click += (_, _) => SetFeedback(_like, _dislike, positive: false);
        _quote.Click += (_, _) => FindChat()?.AddContext(MessageText(DataContext), Source());
        _edit.Click += (_, _) => FindChat()?.EditMessage(MessageText(DataContext));
        _speak.Click += async (_, _) =>
        {
            var service = FindChat()?.SpeechService;
            if (service is null) return;
            try
            {
                if (service.IsSpeaking) service.Stop();
                else await service.SpeakAsync(MessageText(DataContext));
            }
            catch (Exception error) { FindChat()?.ReportActionError("朗读失败：" + error.Message); }
        };

        DataContextChanged += (_, _) => Bind();
        Content = row;
        AttachedToVisualTree += (_, _) =>
        {
            if (FindChat() is { } chat) ApplyAppearance(chat.Appearance);
            Bind();
        };
        PointerEntered += (_, _) =>
        {
            if (Content is Control content && _appearance.MessageActions == "hover")
            {
                content.Opacity = 1;
                IsHitTestVisible = true;
            }
        };
        PointerExited += (_, _) => UpdateVisibility();
    }

    private static void SetFeedback(Button like, Button dislike, bool positive)
    {
        var already = positive ? like.Classes.Contains("active") : dislike.Classes.Contains("active");
        like.Classes.Set("active", !already && positive);
        dislike.Classes.Set("active", !already && !positive);
    }

    /// <summary>按当前消息类型刷新可见性与元信息。</summary>
    private void Bind()
    {
        var isUser = DataContext is UserMessageViewModel;
        var assistant = DataContext as AssistantMessageViewModel;
        var timeline = DataContext as TimelineEntryViewModel;

        // 与主流客户端一致：用户消息只提供"复制"和"更多"，不出现点赞/朗读等助手专属操作。
        _edit.IsVisible = isUser;
        _like.IsVisible = !isUser;
        _dislike.IsVisible = !isUser;
        _speak.IsVisible = !isUser && FindChat()?.SpeechService is not null;
        _quote.IsVisible = !isUser;
        _model.IsVisible = assistant?.HasModel == true;
        _model.Text = assistant?.ModelText ?? string.Empty;
        _usage.IsVisible = assistant?.HasUsage == true;
        _usage.Text = assistant?.UsageText ?? string.Empty;
        _time.IsVisible = timeline?.HasTime == true;
        _time.Text = timeline?.TimeText ?? string.Empty;
    }

    private async Task ForkAsync(bool retry)
    {
        try
        {
            if (TopLevel.GetTopLevel(this) is MainWindow window && DataContext is TimelineEntryViewModel source)
                await window.ForkFromMessageAsync(source, retry, FindChat());
        }
        catch (Exception error) { FindChat()?.ReportActionError(error.Message); }
    }

    private async Task CopyAsync(string text)
    {
        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
                await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, text);
        }
        catch (Exception error) { FindChat()?.ReportActionError("复制失败：" + error.Message); }
    }

    private ChatPage? FindChat() => this.GetVisualAncestors().OfType<ChatPage>().FirstOrDefault();
    private string Source() => (DataContext is UserMessageViewModel ? "用户消息" : "助手回复") + " · " + (DataContext as TimelineEntryViewModel)?.Id;

    /// <summary>把消息渲染为 Markdown：文本原样、思考块引用化、工具调用渲染为代码块。</summary>
    internal static string MessageMarkdown(object? message) => message switch
    {
        UserMessageViewModel user => user.Text,
        AssistantMessageViewModel assistant => string.Join("\n\n", assistant.Blocks.Select(RenderBlock)),
        _ => string.Empty,
    };

    private static string RenderBlock(BlockViewModel block) => block switch
    {
        TextBlockViewModel text => text.Text,
        ThinkingBlockViewModel thinking => "> " + thinking.Text.Replace("\n", "\n> "),
        ToolCallBlockViewModel tool => "```" + tool.ToolName + "\n" + tool.InputJson + "\n```",
        _ => string.Empty,
    };

    internal static string MessageText(object? message) => message switch
    {
        UserMessageViewModel user => user.Text,
        AssistantMessageViewModel assistant => string.Join("\n\n",
            assistant.Blocks.OfType<TextBlockViewModel>().Select(block => block.Text)),
        _ => string.Empty,
    };
}
