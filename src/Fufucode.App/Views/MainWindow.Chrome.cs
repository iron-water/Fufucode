using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Fufucode.App;

public partial class MainWindow
{
    private void InitializeCaption()
    {
        PropertyChanged += (_, change) =>
        {
            if (change.Property == WindowStateProperty)
            {
                CaptionMaximize.Kind = WindowState == WindowState.Maximized ? "restore" : "maximize";
                ToolTip.SetTip(CaptionMaximize, WindowState == WindowState.Maximized ? "还原" : "最大化");
            }
        };
    }

    private void CaptionDrag_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.Source is Control control && (control is Button || control.GetVisualAncestors().OfType<Button>().Any())) return;
        if (e.ClickCount == 2) ToggleCaptionMaximize();
        else BeginMoveDrag(e);
        e.Handled = true;
    }
    private void CaptionMinimize_Click(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void CaptionMaximize_Click(object? sender, RoutedEventArgs e) => ToggleCaptionMaximize();
    private void ToggleCaptionMaximize() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CaptionClose_Click(object? sender, RoutedEventArgs e) => Close();
}
