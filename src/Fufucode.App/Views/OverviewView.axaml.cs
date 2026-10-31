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

    /// <summary>
    /// 一条“新建会话 · 当前后端”：只有一个可用后端时点击直接新建；
    /// 多个后端时点击展开下拉，选哪个就用哪个新建。
    /// </summary>
    private void NewSessionBar_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || DataContext is not MainViewModel viewModel) return;
        if (!viewModel.Sidebar.HasBackendChoice)
        {
            viewModel.Sidebar.NewSessionCommand.Execute(null);
            return;
        }
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
