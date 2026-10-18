using Avalonia.Controls;
using Avalonia.Input;
using Fufucode.App.ViewModels;

namespace Fufucode.App.Views;

public partial class UsageDetailsView : UserControl
{
    internal void SetCloseAllowed(bool allowed)
    { ClosePageButton.IsEnabled = allowed; ToolTip.SetTip(ClosePageButton, allowed ? "关闭用量页" : "请先取消固定页签"); }
    public UsageDetailsView()
    {
        InitializeComponent();
        KeyDown += (_, args) =>
        {
            if (args.Key != Key.Escape || DataContext is not UsageDetailsViewModel vm) return;
            vm.CloseCommand.Execute(null);
            args.Handled = true;
        };
    }
}
