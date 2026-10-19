using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.VisualTree;
using Fufucode.App.Infrastructure;
using Fufucode.App.ViewModels;

namespace Fufucode.App.Views;

public partial class ChatPage
{
    internal WorkbenchAppearance Appearance { get; private set; } = new();

    /// <summary>语音朗读服务：由宿主注入；未安装语音插件时为 null（按钮不显示）。</summary>
    internal Fufucode.Core.Plugins.ISpeechService? SpeechService { get; set; }
    private AppActionRegistry? _workbenchActions;
    internal void AttachWorkbenchActions(AppActionRegistry actions) => _workbenchActions = actions;
    internal void SetHeaderMenu(MenuFlyout menu)
    { HeaderActionsButton.Flyout = menu; HeaderActionsButton.IsVisible = menu.Items.Count > 0; }
    internal void ApplyAppearance(WorkbenchAppearance settings)
    {
        Appearance = settings;
        TimelineItems.MaxWidth = settings.ReadWidth;
        // 底部留出富余：回合结束后出现的消息操作条（复制/点赞/更多）需要净空，
        // 否则最后一条消息贴到输入区上沿、操作条被输入区挡住。
        TimelineItems.Padding = settings.Density switch { "compact" => new Thickness(16, 12, 16, 28), "airy" => new Thickness(28, 24, 28, 40), _ => new Thickness(24, 20, 24, 32) };
        ConversationHeader.IsVisible = settings.ShowSessionHeader || (DataContext as MainViewModel)?.Sidebar.IsRenaming == true;
        ComposerCard.IsVisible = settings.Composer != "hidden";
        // 沉浸式：输入区收起时留一个上箭头按钮，避免只能回设置里改回来。
        ComposerRevealButton.IsVisible = settings.Composer == "hidden";
        // 沉浸式：输入区收起时留一个上箭头按钮，避免"只能去设置里改回来"。
        ComposerRevealButton.IsVisible = settings.Composer == "hidden";
        ComposerCard.MaxWidth = settings.Composer == "reading" ? settings.ReadWidth : double.PositiveInfinity;
        if (settings.SkinResolution.Skin == Features.Skins.SkinId.Fluent)
            ComposerCard.CornerRadius = new CornerRadius(settings.Corner);
        else ComposerCard.ClearValue(Border.CornerRadiusProperty);
        InputBox.FontSize = settings.FontSize;
        // 刻度是对话区固定结构：常显示，不提供开关。
        MessageRuler.IsVisible = true;
        RefreshRuler();
        foreach (var actions in this.GetVisualDescendants().OfType<MessageActionsView>()) actions.ApplyAppearance(settings);
    }
    /// <summary>
    /// 左侧轮次刻度：只列用户轮次，悬停显示这一轮的指令，点击跳转，右键收藏。
    /// 收藏只做原位高亮，不改变顺序——刻度的价值是空间记忆，重排会让位置失去意义。
    /// </summary>
    internal void RefreshRuler()
    {
        MessageRuler.Children.Clear();
        if (DataContext is not MainViewModel vm) return;

        var numbered = vm.Timeline.Entries.OfType<UserMessageViewModel>()
            .Select((entry, index) => (Entry: entry, Number: index + 1))
            .ToList();

        // 收藏按会话持久化：每次重建刻度时套用一次，重启后仍能找回固定的轮次。
        var sessionId = vm.Sidebar.SelectedSession?.Id;
        if (!string.IsNullOrEmpty(sessionId))
        {
            var pinnedIds = PinnedTurnsStore.Load(sessionId);
            foreach (var item in numbered)
                item.Entry.IsPinned = pinnedIds.Contains(item.Entry.Id);
        }

        foreach (var (entry, number) in numbered)
        {
            var tick = new StackPanel { Spacing = 1, HorizontalAlignment = HorizontalAlignment.Center };
            if (entry.IsPinned)
            {
                tick.Children.Add(new TextBlock
                {
                    Text = "●",
                    FontSize = 6,
                    HorizontalAlignment = HorizontalAlignment.Center,
                });
            }
            tick.Children.Add(new TextBlock
            {
                Text = number.ToString("00"),
                FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Center,
            });

            var button = new Button
            {
                Content = tick,
                Width = 30,
                Height = 28,
                Padding = new Thickness(0),
                Background = Avalonia.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0),
            };
            button.Classes.Add("rulerTick");
            if (entry.IsPinned) button.Classes.Add("pinned");

            // 悬停预览：完整显示这一轮的用户指令，长文本换行而不是截成省略号。
            var preview = string.IsNullOrWhiteSpace(entry.Text) ? "（无文字输入）" : entry.Text.Trim();
            var tipBlock = new TextBlock
            {
                Text = preview,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                MaxWidth = 340,
            };
            ToolTip.SetTip(button, tipBlock);
            ToolTip.SetShowDelay(button, 200);

            var menu = new MenuFlyout();
            var pin = new MenuItem { Header = entry.IsPinned ? "取消收藏" : "收藏这一轮" };
            pin.Click += (_, _) =>
            {
                entry.IsPinned = !entry.IsPinned;
                if (!string.IsNullOrEmpty(sessionId))
                    PinnedTurnsStore.Set(sessionId, entry.Id, entry.IsPinned);
                RefreshRuler();
            };
            menu.Items.Add(pin);
            button.ContextFlyout = menu;

            var id = entry.Id;
            button.Click += (_, _) => ScrollToMessage(id);
            MessageRuler.Children.Add(button);
        }
    }

    internal void FindConversation() => FindConversation_Click(HeaderActionsButton, new Avalonia.Interactivity.RoutedEventArgs());
    internal void BeginRename()
    {
        if (DataContext is not MainViewModel vm) return;
        vm.Sidebar.BeginRenameCommand.Execute(null); ConversationHeader.IsVisible = true; RenameBox.Focus();
    }
    internal void ToggleComposer() => _workbenchActions?.ExecuteAsync("composer.toggle");
    internal Task ChooseForkPointAsync() => TopLevel.GetTopLevel(this) is MainWindow window ? window.ForkFromMessageAsync(origin: this) : Task.CompletedTask;
}
