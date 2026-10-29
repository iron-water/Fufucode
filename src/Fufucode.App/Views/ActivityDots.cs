using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Fufucode.App.Views;

/// <summary>
/// 三点活动指示器：三个小圆点按 0/150/300ms 相位循环明暗。
///
/// 这是全应用唯一的"正在运行"动画，所有后端共用：
/// 思考中、等待响应、工具运行都复用它，不按后端换样式。
/// 使用 DispatcherTimer 而不是逐帧动画，避免每条流式消息都启动新的动画时钟。
///
/// 生命周期：只有真正可见（自身与全部祖先可见且已入可视树）时才走 80ms 计时；
/// 挂载/卸载、自身 IsVisible、祖先折叠/展开都会重新评估，隐藏时停表不占 CPU。
/// </summary>
public sealed class ActivityDots : UserControl
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(80);
    private static readonly TimeSpan Cycle = TimeSpan.FromMilliseconds(960);

    private readonly Ellipse[] _dots;
    private readonly DispatcherTimer _timer;
    private readonly List<Visual> _visibilityAncestors = new();
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
    }

    /// <summary>颜色可跟随主题；默认使用次级文字色。</summary>
    public static readonly StyledProperty<IBrush?> DotBrushProperty =
        AvaloniaProperty.Register<ActivityDots, IBrush?>(nameof(DotBrush));

    public IBrush? DotBrush
    {
        get => GetValue(DotBrushProperty);
        set => SetValue(DotBrushProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        TrackAncestorVisibility();
        SyncTimer();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        // 先解除祖先订阅并显式停表：离开可视树后计时没有意义，
        // 也避免 VisualRoot 尚未清空时被 SyncTimer 误判为可见而重启。
        UntrackAncestorVisibility();
        Stop();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == VisualParentProperty)
        {
            // 换父节点后祖先链变了，重新订阅再评估一次（Avalonia 12 已密封旧的虚方法）。
            if (VisualRoot is not null)
            {
                TrackAncestorVisibility();
                SyncTimer();
            }
            return;
        }

        if (change.Property == DotBrushProperty)
        {
            var brush = DotBrush ?? Brushes.Gray;
            foreach (var dot in _dots) dot.Fill = brush;
        }
        else if (change.Property == IsVisibleProperty)
        {
            // 自身显示/隐藏时立即启停，不必等父级布局。
            SyncTimer();
        }
    }

    private void TrackAncestorVisibility()
    {
        UntrackAncestorVisibility();
        // Avalonia 12 的 IsEffectivelyVisibleChanged 是 internal 事件，无法公开订阅；
        // 改为监听整条祖先链的 IsVisibleProperty，父级折叠/展开时也能重新启停。
        foreach (var ancestor in this.GetVisualAncestors())
        {
            ancestor.PropertyChanged += OnAncestorPropertyChanged;
            _visibilityAncestors.Add(ancestor);
        }
    }

    private void UntrackAncestorVisibility()
    {
        foreach (var ancestor in _visibilityAncestors)
        {
            ancestor.PropertyChanged -= OnAncestorPropertyChanged;
        }

        _visibilityAncestors.Clear();
    }

    private void OnAncestorPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        // 祖先的其它属性变化忽略，只在可见性切换时重新评估。
        if (e.Property == IsVisibleProperty) SyncTimer();
    }

    private void SyncTimer()
    {
        // 与 IsEffectivelyVisible 等价：已入可视树，且自身与全部祖先都可见。
        // 直接沿父链核对而不读 IsEffectivelyVisible：Avalonia 12 没有公开的可见性
        // 变更事件，且在属性回调中有效可见性可能尚未刷新，会导致漏启动。
        if (IsTreeVisible()) Start(); else Stop();
    }

    private bool IsTreeVisible()
    {
        if (VisualRoot is null || !IsVisible) return false;
        foreach (var ancestor in this.GetVisualAncestors())
        {
            if (!ancestor.IsVisible) return false;
        }
        return true;
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
