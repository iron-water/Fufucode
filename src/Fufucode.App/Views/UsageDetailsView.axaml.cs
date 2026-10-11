using Avalonia.Controls;
using Avalonia.Input;
using Fufucode.App.ViewModels;

namespace Fufucode.App.Views;

public partial class UsageDetailsView : UserControl
{
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
