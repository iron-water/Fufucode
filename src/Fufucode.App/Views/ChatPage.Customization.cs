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
    /// <summary>插件安装/启停后让已渲染的消息操作条重新判断“朗读”按钮是否显示。</summary>
    internal void RefreshSpeechAvailability()
    {
        foreach (var actions in this.GetVisualDescendants().OfType<MessageActionsView>()) actions.RefreshAvailability();
    }
    private AppActionRegistry? _workbenchActions;
    internal void AttachWorkbenchActions(AppActionRegistry actions) => _workbenchActions = actions;
    internal void SetHeaderMenu(MenuFlyout menu)
    { HeaderActionsButton.Flyout = menu; HeaderActionsButton.IsVisible = menu.Items.Count > 0; }
    internal void ApplyAppearance(WorkbenchAppearance settings)
    {
        Appearance = settings;
        ApplyResponsiveMetrics();
        ConversationHeader.IsVisible = settings.ShowSessionHeader || (DataContext as MainViewModel)?.Sidebar.IsRenaming == true;
        SetComposerExpanded(settings.Composer != "hidden");
        // 输入卡的轮廓由卡片样式/皮肤管理，不套用普通列表行的 4px 圆角。
        ComposerCard.ClearValue(Border.CornerRadiusProperty);
        UpdateComposerClearance();
        InputBox.FontSize = settings.FontSize;
        foreach (var actions in this.GetVisualDescendants().OfType<MessageActionsView>()) actions.ApplyAppearance(settings);
    }

    /// <summary>
    /// 响应式布局：侧边栏/小停靠区（宽 &lt; 500）下隐藏刻度与留白、头像列设为 0、外边距收窄，
    /// 确保输入区和工具栏按钮能在 232 最小侧栏宽度内完整展示而不溢出。
    /// </summary>
    internal void ApplyResponsiveMetrics()
    {
        var width = Bounds.Width;
        var narrow = width > 0 && width < 500;
        var inset = narrow ? 4 : (Appearance.Density switch { "compact" => 16, "airy" => 28, _ => 24 });
        ReadingColumn.Margin = new Thickness(inset, 0);
        ReadingColumn.MaxWidth = narrow ? double.PositiveInfinity : Appearance.ReadWidth;
        ReadingColumn.ColumnDefinitions[0].Width = new GridLength(narrow ? 0 : (Appearance.ShowAvatars ? 40 : 12));
        var rulerVisible = !narrow;
        MessageRuler.IsVisible = rulerVisible;
        RulerTrack.IsVisible = rulerVisible;
        if (ReadingColumnSpacer is not null)
        {
            ReadingColumnSpacer.Width = rulerVisible ? 42 : 0;
            ReadingColumnSpacer.IsVisible = rulerVisible;
        }
        if (rulerVisible) RefreshRuler();
        else MessageRuler.Children.Clear();
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
            var tick = new StackPanel { Spacing = 1, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            // 纯图形不靠字号撑大小：收藏圆点改用 6×6 的 Border（圆角取半径 3）。
            if (entry.IsPinned)
            {
                var dot = new Border
                {
                    Width = 6,
                    Height = 6,
                    CornerRadius = new CornerRadius(3),
                    HorizontalAlignment = HorizontalAlignment.Center,
                };
                // 收藏态沿用刻度样式的 accent 色，圆点不再依赖 TextBlock 的字体颜色。
                dot.Bind(Border.BackgroundProperty,
                    new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("AppAccentText"));
                tick.Children.Add(dot);
            }
            // 统一字阶：刻度数字 10 归入 12，01/02 的轮次编号格式保留。
            tick.Children.Add(new TextBlock
            {
                Text = number.ToString("00"),
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
            });

            var button = new Button
            {
                Content = tick,
                Tag = entry,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Width = 30,
                Height = 28,
                Padding = new Thickness(0),
                Background = Avalonia.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0),
            };
            Avalonia.Automation.AutomationProperties.SetAutomationId(button, $"ruler-turn-{number:00}");
            button.Classes.Add("rulerTick");
            if (entry.IsPinned) button.Classes.Add("pinned");

            // 真实用户消息的开头作为轮次预览，不恢复全局提示层。
            Avalonia.Automation.AutomationProperties.SetName(button, $"第 {number} 轮，跳转到消息");
            Avalonia.Automation.AutomationProperties.SetHelpText(button, TurnPreview(entry.DisplayText));
            _ = new ChatInfoBubble(button, () => ($"第 {number:00} 轮", TurnPreview(entry.DisplayText)), turnPreview: true);

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
        Avalonia.Threading.Dispatcher.UIThread.Post(UpdateCurrentTurnMarker, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    private void UpdateCurrentTurnMarker()
    {
        if (!MessageRuler.IsVisible) { CurrentTurnMarker.IsVisible = false; return; }
        Button? current = null;
        foreach (var button in MessageRuler.Children.OfType<Button>())
        {
            if (button.Tag is not TimelineEntryViewModel entry) continue;
            if (TimelineItems.ContainerFromItem(entry) is not Visual container) continue;
            if (container.TranslatePoint(default, TimelineScroller) is not { } point) continue;
            if (point.Y <= 1) current = button;
            else { current ??= button; break; }
        }
        if (current?.TranslatePoint(default, RulerTrack) is not { } position)
        { CurrentTurnMarker.IsVisible = false; return; }
        CurrentTurnMarker.IsVisible = true;
        CurrentTurnMarker.Margin = new Thickness(0, position.Y + (current.Bounds.Height - 16) / 2, 0, 0);
    }

    internal static string TurnPreview(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "（无文字输入）";
        var value = text.Trim();
        // A bounded excerpt keeps long turns readable without filling the window.
        const int limit = 260;
        if (value.Length <= limit) return value;
        var length = char.IsHighSurrogate(value[limit - 1]) ? limit - 1 : limit;
        return value[..length].TrimEnd() + "…";
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
