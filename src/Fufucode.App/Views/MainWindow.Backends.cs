using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Fufucode.Pi;

namespace Fufucode.App;

public partial class MainWindow
{
    private async Task<BackendId?> ChooseNewBackendAsync()
    {
        var dialog = new Window
        {
            Title = "新聊天 · 选择后端", Width = 410, SizeToContent = SizeToContent.Height,
            CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var choices = new ComboBox { ItemsSource = new[] { "Pi", "OMP" }, SelectedIndex = 0 };
        var create = new Button { Content = "创建聊天" };
        var cancel = new Button { Content = "取消" };
        create.Click += (_, _) => dialog.Close((BackendId?)(choices.SelectedIndex == 1 ? BackendId.Omp : BackendId.Pi));
        cancel.Click += (_, _) => dialog.Close(null);
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20), Spacing = 12, Children =
            {
                new TextBlock { Text = "聊天创建后固定使用所选后端。" }, choices,
                new TextBlock { Text = "历史聊天按已保存的后端恢复。", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { create, cancel } }
            }
        };
        return await dialog.ShowDialog<BackendId?>(this);
    }
}
