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
    private readonly Button _speak;
    private readonly Button _quote;
    private readonly Button _edit;
    private readonly Button _fork;
    private readonly Button _retry;
    private readonly Button _ask;
    private readonly TextBlock _usage;
    private readonly TextBlock _model;
    private readonly TextBlock _time;
    private readonly StackPanel _icons;

    internal void ApplyAppearance(WorkbenchAppearance appearance)
    {
        _appearance = appearance;
        UpdateVisibility();
    }

    /// <summary>
    /// 只淡出图标操作；用量/模型/时间始终可见。
    /// 之前整条一起淡出，用户不悬停就完全看不到底部信息，像是"没有底部工具栏"。
    /// </summary>
    private void UpdateVisibility()
    {
        if (_icons is null) return;
        var visible = _appearance.MessageActions == "show" ||
                      (_appearance.MessageActions == "hover" && IsPointerOver);
        _icons.Opacity = visible ? 1 : 0;
        _icons.IsHitTestVisible = _appearance.MessageActions != "hide";
    }

    public MessageActionsView()
    {
        _copy = WorkbenchUi.IconButton("Copy", "复制消息");
        _speak = WorkbenchUi.IconButton("Speaker", "朗读这条消息");
        _quote = WorkbenchUi.IconButton("ArrowReply", "引用到主会话");
        _edit = WorkbenchUi.IconButton("Edit", "编辑成新草稿");
        // 分叉三件事直接出现在操作条：不再藏进"更多"菜单。
        _fork = WorkbenchUi.IconButton("Branch", "从此消息分叉");
        _retry = WorkbenchUi.IconButton("ArrowClockwise", "分叉并重试");
        _ask = WorkbenchUi.IconButton("Chat", "在侧边聊天中提问");

        _usage = new TextBlock { Classes = { "meta", "tabularNumeric" }, VerticalAlignment = VerticalAlignment.Center, IsVisible = false };
        _model = new TextBlock { Classes = { "meta" }, VerticalAlignment = VerticalAlignment.Center, IsVisible = false };
        _time = new TextBlock { Classes = { "meta", "tabularNumeric" }, VerticalAlignment = VerticalAlignment.Center };

        // 命中区：整条栏 + 图标容器都用透明背景铺满，指针经过间隙时仍算“在栏内”，
        // 避免显隐按单个图标判定造成的一闪一闪。
        Background = Brushes.Transparent;
        _icons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent,
        };
        _icons.Children.Add(_copy);
        _icons.Children.Add(_speak);
        _icons.Children.Add(_quote);
        _icons.Children.Add(_fork);
        _icons.Children.Add(_retry);
        _icons.Children.Add(_ask);
        _icons.Children.Add(_edit);

        var meta = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 10,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        meta.Children.Add(_usage);
        meta.Children.Add(_model);
        meta.Children.Add(_time);

        var row = new Grid
        {
            // 图标在左、元信息（模型/时间）靠右，避免时间戳停在句子中间。
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            Margin = new Thickness(0, 6, 0, 0),
        };
        Grid.SetColumn(_icons, 0);
        Grid.SetColumn(meta, 2);
        row.Children.Add(_icons);
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

        _copy.Click += async (_, _) => await CopyAsync(MessageText(DataContext));
        _fork.Click += async (_, _) => await ForkAsync(false);
        _retry.Click += async (_, _) => await ForkAsync(true);
        _ask.Click += (_, _) => FindChat()?.AskAbout(MessageText(DataContext), Source());
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
        // PointerEntered/Exited 是直接事件、不冒泡：按钮铺满后本控件收不到，
        // 改跟 IsPointerOver 属性（覆盖子树），与设置行/会话行同一套语义。
        PropertyChanged += (_, e) =>
        {
            if (e.Property == Avalonia.Input.InputElement.IsPointerOverProperty) UpdateVisibility();
            // 底部卡只在回合结束后出现；出现时做一次淡入，避免突然插入造成滚动跳动。
            if (e.Property == IsVisibleProperty && IsVisible) Motion.AnimateFooter(this);
        };
        AttachedToVisualTree += (_, _) => { if (IsVisible) Motion.AnimateFooter(this); };
    }

    /// <summary>宿主在语音插件安装/启停后调用，重新计算依赖服务可用性的按钮。</summary>
    internal void RefreshAvailability() => Bind();

    /// <summary>按当前消息类型刷新可见性与元信息。</summary>
    private void Bind()
    {
        var isUser = DataContext is UserMessageViewModel;
        var assistant = DataContext as AssistantMessageViewModel;
        var timeline = DataContext as TimelineEntryViewModel;

        // 用户消息只提供"复制"与"编辑成草稿"；分叉/重试/侧边提问都是
        // 针对助手输出的操作，直接作为图标常驻助手消息操作条。
        _edit.IsVisible = isUser;
        _speak.IsVisible = !isUser && FindChat()?.SpeechService is not null;
        _quote.IsVisible = !isUser;
        _fork.IsVisible = !isUser;
        _retry.IsVisible = !isUser;
        _ask.IsVisible = !isUser;
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
