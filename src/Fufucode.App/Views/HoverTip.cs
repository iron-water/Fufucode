using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;

namespace Fufucode.App.Views;

/// <summary>
/// 图标专用浮动提示：固定出现在图标上方；鼠标离开后延迟 0.3 秒消失，
/// 期间移回图标则取消关闭。输入区空间有限，避免系统 ToolTip 出现在下方遮挡输入。
/// </summary>
internal static class HoverTip
{
    public static void Attach(Control target, Func<string?> text)
    {
        ToolTip.SetTip(target, null);
        var label = new TextBlock
        {
            FontSize = 11,
            TextWrapping = TextWrapping.NoWrap,
            Foreground = new SolidColorBrush(Color.Parse("#FFFFFFFF")),
        };
        var border = new Border
        {
            Child = label,
            Padding = new Thickness(8, 4),
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(Color.Parse("#FF2C2C2C")),
            BorderBrush = new SolidColorBrush(Color.Parse("#3DFFFFFF")),
            CornerRadius = new CornerRadius(4),
        };
        var popup = new Popup
        {
            PlacementTarget = target,
            Placement = PlacementMode.Top,
            IsLightDismissEnabled = false,
            IsHitTestVisible = false,
            Child = border,
        };
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            popup.IsOpen = false;
        };
        // PointerEntered 是直接事件、不冒泡；指针落在图标子元素上时按钮收不到。
        // 用 IsPointerOver（覆盖子树）驱动显示/延迟隐藏。
        target.PropertyChanged += (_, e) =>
        {
            if (e.Property != Avalonia.Input.InputElement.IsPointerOverProperty) return;
            if (target.IsPointerOver)
            {
                timer.Stop();
                var value = text();
                if (string.IsNullOrWhiteSpace(value)) return;
                label.Text = value;
                popup.IsOpen = true;
            }
            else if (popup.IsOpen)
            {
                timer.Stop();
                timer.Start();
            }
        };
        target.DetachedFromVisualTree += (_, _) =>
        {
            timer.Stop();
            popup.IsOpen = false;
        };
    }
}
