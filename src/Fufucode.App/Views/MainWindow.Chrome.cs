using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
        // 只放行真正可交互的控件；空白与被动内容（图标/文字/滚动条容器）都要能拖动窗口，
        // 否则用户按住顶栏点不到按钮的位置就完全拖不动。
        if (IsInteractiveCaptionSource(e.Source)) return;
        if (e.ClickCount == 2) { ToggleCaptionMaximize(); e.Handled = true; return; }
        // DESK-1：必须显式从 VisualRoot 拿 Window 再 BeginMoveDrag。直接 BeginMoveDrag(e)
        // 在 WindowDecorations=BorderOnly 且事件来自子视觉时可能被命中测试吞掉，表现为“顶栏拖不动”。
        var window = TopLevel.GetTopLevel(this) as Window ?? this;
        window.BeginMoveDrag(e);
        e.Handled = true;
    }

    private static bool IsInteractiveCaptionSource(object? source)
    {
        if (source is not Control control) return false;
        for (Visual? cursor = control; cursor is not null; cursor = cursor.GetVisualParent())
        {
            if (cursor is Button or TextBox or ComboBox or ToggleButton or CheckBox or RadioButton
                or Slider or ScrollBar or TabControl or ListBox or TreeView or MenuItem) return true;
        }
        return false;
    }
    private void CaptionMinimize_Click(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void CaptionMaximize_Click(object? sender, RoutedEventArgs e) => ToggleCaptionMaximize();
    private void ToggleCaptionMaximize() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CaptionClose_Click(object? sender, RoutedEventArgs e) => Close();
}
