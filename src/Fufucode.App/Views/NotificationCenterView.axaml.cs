using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Fufucode.App.ViewModels;

namespace Fufucode.App.Views;

public partial class NotificationCenterView : UserControl
{
    public NotificationCenterView()
    {
        InitializeComponent();
        KeyDown += (_, args) =>
        {
            if (args.Key != Key.Escape || DataContext is not NotificationCenterViewModel vm) return;
            vm.CloseCommand.Execute(null);
            args.Handled = true;
        };
    }

    private void Activate_Click(object? sender, RoutedEventArgs args)
    {
        if (DataContext is NotificationCenterViewModel vm && sender is Button { DataContext: NotificationItemViewModel item })
            vm.ActivateCommand.Execute(item);
    }
}
