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
        TimelineItems.Padding = settings.Density switch { "compact" => new Thickness(16, 12), "airy" => new Thickness(28, 24), _ => new Thickness(24, 20, 24, 8) };
        ConversationHeader.IsVisible = settings.ShowSessionHeader || (DataContext as MainViewModel)?.Sidebar.IsRenaming == true;
        ComposerCard.IsVisible = settings.Composer != "hidden";
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
    internal void RefreshRuler()
    {
        MessageRuler.Children.Clear();
        if (DataContext is not MainViewModel vm) return;
        var index = 0;
        foreach (var entry in vm.Timeline.Entries.OfType<UserMessageViewModel>())
        {
            var button = new Button { Content = (++index).ToString("00"), Width = 28, Height = 30, Padding = new Thickness(0), FontSize = 10 };
            ToolTip.SetTip(button, entry.Text); button.Click += (_, _) => ScrollToMessage(entry.Id); MessageRuler.Children.Add(button);
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
