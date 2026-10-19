using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Fufucode.App.Views;

/// <summary>
/// 三点活动指示器：三个小圆点按 0/150/300ms 相位循环明暗。
///
/// 这是全应用唯一的"正在运行"动画，所有后端共用：
/// 思考中、等待响应、工具运行都复用它，不按后端换样式。
/// 使用 DispatcherTimer 而不是逐帧动画，避免每条流式消息都启动新的动画时钟。
/// </summary>
public sealed class ActivityDots : UserControl
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(80);
    private static readonly TimeSpan Cycle = TimeSpan.FromMilliseconds(960);

    private readonly Ellipse[] _dots;
    private readonly DispatcherTimer _timer;
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private bool _running;

    public ActivityDots()
    {
        _dots = new Ellipse[3];
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 3.5,
            VerticalAlignment = VerticalAlignment.Center,
        };
        for (var index = 0; index < _dots.Length; index++)
        {
            var dot = new Ellipse
            {
                Width = 3.5,
                Height = 3.5,
                Fill = Brushes.Gray,
                Opacity = 0.3,
            };
            _dots[index] = dot;
            panel.Children.Add(dot);
        }

        Content = panel;
        IsHitTestVisible = false;
        _timer = new DispatcherTimer { Interval = Interval };
        _timer.Tick += (_, _) => Update();
        AttachedToVisualTree += (_, _) => Start();
        DetachedFromVisualTree += (_, _) => Stop();
    }

    /// <summary>颜色可跟随主题；默认使用次级文字色。</summary>
    public static readonly StyledProperty<IBrush?> DotBrushProperty =
        AvaloniaProperty.Register<ActivityDots, IBrush?>(nameof(DotBrush));

    public IBrush? DotBrush
    {
        get => GetValue(DotBrushProperty);
        set => SetValue(DotBrushProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DotBrushProperty)
        {
            var brush = DotBrush ?? Brushes.Gray;
            foreach (var dot in _dots) dot.Fill = brush;
        }
    }

    private void Start()
    {
        if (_running) return;
        _running = true;
        _timer.Start();
        Update();
    }

    private void Stop()
    {
        _running = false;
        _timer.Stop();
    }

    private void Update()
    {
        var elapsed = (DateTimeOffset.UtcNow - _startedAt).TotalMilliseconds;
        var phase = elapsed % Cycle.TotalMilliseconds;
        for (var index = 0; index < _dots.Length; index++)
        {
            var offset = index * 150d;
            var local = (phase - offset + Cycle.TotalMilliseconds) % Cycle.TotalMilliseconds;
            // 每点 320ms 内从 0.3 亮到 1 再回落，其余时间保持低亮度。
            var t = local < 320 ? local / 320 : 1;
            var wave = Math.Sin(Math.PI * Math.Clamp(t, 0, 1));
            _dots[index].Opacity = 0.3 + 0.7 * wave;
        }
    }
}
