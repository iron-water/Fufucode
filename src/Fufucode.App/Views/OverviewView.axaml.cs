using Avalonia.Controls;
namespace Fufucode.App.Views;
public partial class OverviewView : UserControl
{
    public OverviewView() => InitializeComponent();
    public StackPanel Toolbar => ManagementToolbar;
    public StackPanel Body => ManagementBody;
}
