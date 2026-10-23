using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Threading;
using Fufucode.Core.Plugins;

namespace Fufucode.App.Views;

/// <summary>
/// 多 Agent 虚拟光标 Overlay：透明、置顶、不激活、点击穿透。
/// 它只绘制 Agent 的逻辑光标，不移动系统指针，也不发送任何输入。
/// </summary>
public sealed class ComputerCursorOverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;

    private readonly IComputerUseCursorRegistry _registry;
    private readonly Canvas _canvas = new();
    private readonly Dictionary<string, Border> _labels = new(StringComparer.Ordinal);
    private PixelRect _screenBounds;
    private double _scaling = 1;
    private bool _stylesApplied;

    public ComputerCursorOverlayWindow(IComputerUseCursorRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));

        WindowDecorations = WindowDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Topmost = true;
        ShowInTaskbar = false;
        CanResize = false;
        SizeToContent = SizeToContent.Manual;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ShowActivated = false;
        IsHitTestVisible = false;
        Focusable = false;

        _canvas.Background = Brushes.Transparent;
        _canvas.IsHitTestVisible = false;
        Content = _canvas;

        _registry.Changed += OnRegistryChanged;
        Closed += (_, _) =>
        {
            _registry.Changed -= OnRegistryChanged;
            _labels.Clear();
        };
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        ApplyOverlayStyles();
        FitToPrimaryScreen();
        Rebuild();
    }

    private void OnRegistryChanged()
    {
        if (Dispatcher.UIThread.CheckAccess()) Rebuild();
        else Dispatcher.UIThread.Post(Rebuild);
    }

    /// <summary>铺满主屏；坐标使用屏幕物理像素，绘制时换算为窗口 DIP。</summary>
    private void FitToPrimaryScreen()
    {
        var screen = Screens.Primary;
        if (screen is null) return;
        _screenBounds = screen.Bounds;
        _scaling = screen.Scaling <= 0 ? 1 : screen.Scaling;
        Position = new PixelPoint(_screenBounds.X, _screenBounds.Y);
        Width = Math.Max(1, _screenBounds.Width / _scaling);
        Height = Math.Max(1, _screenBounds.Height / _scaling);
        _canvas.Width = Width;
        _canvas.Height = Height;
        Rebuild();
    }

    private void Rebuild()
    {
        _canvas.Children.Clear();
        _labels.Clear();
        if (!IsVisible) return;

        foreach (var state in _registry.Snapshot)
        {
            if (!state.IsVisible) continue;
            var x = (state.X - _screenBounds.X) / _scaling;
            var y = (state.Y - _screenBounds.Y) / _scaling;
            if (x < -40 || y < -40 || x > Width + 40 || y > Height + 40) continue;

            var cursor = BuildCursor(state);
            Canvas.SetLeft(cursor, x);
            Canvas.SetTop(cursor, y);
            _canvas.Children.Add(cursor);
            _labels[state.AgentId] = cursor;
        }
    }

    /// <summary>箭头 + 名称胶囊；颜色由 Agent 身份决定，动作型状态加一圈高亮。</summary>
    private Border BuildCursor(ComputerUseCursorState state)
    {
        var color = ParseColor(state.Color);
        var arrow = new Polygon
        {
            Points = new List<Point>
            {
                new(0, 0),
                new(0, 17),
                new(4.6, 13.2),
                new(7.4, 19.5),
                new(10.2, 18.2),
                new(7.4, 12),
                new(13, 11.6),
            },
            Fill = new SolidColorBrush(color),
            Stroke = Brushes.White,
            StrokeThickness = 1,
            IsHitTestVisible = false,
        };

        var label = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(225, color.R, color.G, color.B)),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(6, 2, 6, 2),
            Margin = new Thickness(12, 14, 0, 0),
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(state.Label) ? state.AgentId : state.Label,
                Foreground = Brushes.White,
                FontSize = 10,
                FontWeight = FontWeight.SemiBold,
                IsHitTestVisible = false,
            },
        };

        return new Border
        {
            Width = 160,
            Height = 46,
            Background = Brushes.Transparent,
            IsHitTestVisible = false,
            Opacity = state.IsPressed ? 1.0 : 0.92,
            Child = new Canvas
            {
                Width = 160,
                Height = 46,
                IsHitTestVisible = false,
                Children = { arrow, label },
            },
        };
    }

    private static Color ParseColor(string value) =>
        Color.TryParse(value, out var color) ? color : Color.FromRgb(0x4F, 0x8E, 0xF7);

    private void ApplyOverlayStyles()
    {
        if (_stylesApplied) return;
        var handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero) return;
        var style = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        SetWindowLongPtr(handle, GwlExStyle,
            new IntPtr(style | WsExTransparent | WsExToolWindow | WsExNoActivate));
        _stylesApplied = true;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
}
