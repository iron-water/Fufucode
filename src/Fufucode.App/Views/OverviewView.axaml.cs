using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Fufucode.App.ViewModels;

namespace Fufucode.App.Views;

public partial class OverviewView : UserControl
{
    public OverviewView() => InitializeComponent();
    public StackPanel Toolbar => ManagementToolbar;
    public StackPanel Body => ManagementBody;

    /// <summary>后端条：点击展开下拉，选哪个后端就用哪个新建会话。</summary>
    private void BackendChip_PointerPressed(object? sender, PointerPressedEventArgs e)
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
        e.Handled = true;
    }
}
