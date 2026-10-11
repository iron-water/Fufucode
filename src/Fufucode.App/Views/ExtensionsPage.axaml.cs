using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;

namespace Fufucode.App.Views;

public partial class ExtensionsPage : UserControl
{
    public ExtensionsPage()
    {
        InitializeComponent();
    }

    private void ExtensionsHeader_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (sender is not Grid grid ||
            this.FindControl<WrapPanel>("ExtensionsHeaderActions") is not { } actions) return;
        var narrow = e.NewSize.Width < 680;
        if (grid.Classes.Contains("narrow") == narrow) return;
        grid.Classes.Set("narrow", narrow);
        grid.ColumnDefinitions = new ColumnDefinitions(narrow ? "48,*" : "56,*,Auto");
        grid.RowDefinitions = new RowDefinitions(narrow ? "Auto,Auto" : "Auto");
        Grid.SetColumn(actions, narrow ? 0 : 2);
        Grid.SetRow(actions, narrow ? 1 : 0);
        Grid.SetColumnSpan(actions, narrow ? 2 : 1);
    }

    /// <summary>扩展、命令和待生效变更：窄行把操作区移到说明下方。</summary>
    private void ActionRow_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (sender is not Grid grid || grid.Children.LastOrDefault() is not { } actions) return;
        var narrow = e.NewSize.Width < 600;
        if (grid.Classes.Contains("narrow") == narrow) return;
        grid.Classes.Set("narrow", narrow);
        grid.ColumnDefinitions = new ColumnDefinitions(narrow ? "*" : "*,Auto");
        grid.RowDefinitions = new RowDefinitions(narrow ? "Auto,Auto" : "Auto");
        Grid.SetColumn(actions, narrow ? 0 : 1);
        Grid.SetRow(actions, narrow ? 1 : 0);
        actions.HorizontalAlignment = narrow
            ? Avalonia.Layout.HorizontalAlignment.Left
            : Avalonia.Layout.HorizontalAlignment.Right;
        actions.Margin = narrow ? new Avalonia.Thickness(0, 12, 0, 0) : default;
    }

    /// <summary>移除确认 Flyout 的“取消”：只关弹层，不触发删除。</summary>
    private void RemoveConfirmCancel_Click(object? sender, RoutedEventArgs e)
    {
        (sender as Control)?.GetLogicalAncestors().OfType<Flyout>().FirstOrDefault()?.Hide();
    }
}
