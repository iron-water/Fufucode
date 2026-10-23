using System;
using Avalonia.Controls;
using Fufucode.App.ViewModels;

namespace Fufucode.App.Views;

public partial class OverviewView : UserControl
{
    public OverviewView() => InitializeComponent();
    public StackPanel Toolbar => ManagementToolbar;
    public StackPanel Body => ManagementBody;

    /// <summary>新建会话右侧的下拉：列出六类后端，不可用项禁用。</summary>
    private void NewSessionFlyout_Opening(object? sender, EventArgs e)
    {
        if (sender is not MenuFlyout flyout) return;
        flyout.Items.Clear();
        if (DataContext is not MainViewModel viewModel) return;
        foreach (var option in viewModel.Sidebar.NewSessionBackends)
        {
            flyout.Items.Add(new MenuItem
            {
                Header = option.Label,
                IsEnabled = option.Available,
                Command = viewModel.Sidebar.NewSessionWithBackendCommand,
                CommandParameter = option.Key,
            });
        }

        if (flyout.Items.Count == 0)
        {
            flyout.Items.Add(new MenuItem { Header = "使用默认后端新建会话", Command = viewModel.Sidebar.NewSessionCommand });
        }
    }
}
