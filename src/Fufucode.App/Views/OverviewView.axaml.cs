using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Fufucode.App.ViewModels;

namespace Fufucode.App.Views;

public partial class OverviewView : UserControl
{
    public OverviewView() => InitializeComponent();
    public StackPanel Toolbar => ManagementToolbar;
    public StackPanel Body => ManagementBody;

    /// <summary>选择新建会话使用的后端；本控件不创建会话，创建入口在页头 +。</summary>
    private void NewSessionBar_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || DataContext is not MainViewModel viewModel) return;
        if (!viewModel.Sidebar.HasBackendChoice) return;
        var flyout = new MenuFlyout();
        foreach (var option in viewModel.Sidebar.NewSessionBackends)
        {
            flyout.Items.Add(new MenuItem
            {
                Header = option.IsDefault ? option.Label + "（默认）" : option.Label,
                IsEnabled = option.Available,
                Command = viewModel.Sidebar.NewSessionWithBackendCommand,
                CommandParameter = option.Key,
            });
        }
        flyout.ShowAt(button);
    }
}
