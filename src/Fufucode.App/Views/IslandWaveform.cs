using System;
using System.Collections.Generic;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fufucode.App.Integration;

namespace Fufucode.App.Views;

public enum IslandWaveformState
{
    Idle,
    Running,
    Waiting,
    Failed,
}

/// <summary>会话活动波形：48 点环形采样，持续动画共用一个 200ms 时钟。</summary>
public sealed class IslandWaveform : Control
{
    private const int Capacity = 48;
    private const double TransitionMilliseconds = 160;
    private const double MarkerMilliseconds = 600;
    private readonly double[] _samples = new double[Capacity];
    private readonly List<Visual> _visibilityOwners = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly TranslateTransform _shake = new();
    private readonly long _breatheStarted = Stopwatch.GetTimestamp();
    private int _next;
    private double _filtered;
    private long _lastSample;
    private double _sampleIntervalMs = 1000;
    private long _transitionStarted;
    private Color _colorFrom;
    private Color _colorTo;
    private bool _transitioning;
    private bool _failureHadWave;
    private bool _attached;
    private bool _frameQueued;
    private int _frameVersion;
    private StreamGeometry? _line;
    private StreamGeometry? _area;
    private Size _geometrySize;
    private Point _latestPoint;

    public static readonly StyledProperty<IslandWaveformState> StateProperty =
        AvaloniaProperty.Register<IslandWaveform, IslandWaveformState>(nameof(State));
    public static readonly StyledProperty<IBrush?> IdleBrushProperty =
        AvaloniaProperty.Register<IslandWaveform, IBrush?>(nameof(IdleBrush), Brushes.Gray);
    public static readonly StyledProperty<IBrush?> RunningBrushProperty =
        AvaloniaProperty.Register<IslandWaveform, IBrush?>(nameof(RunningBrush), Brushes.Gray);
    public static readonly StyledProperty<IBrush?> FailedBrushProperty =
        AvaloniaProperty.Register<IslandWaveform, IBrush?>(nameof(FailedBrush), Brushes.Gray);

    public IslandWaveformState State
    {
        get => GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    public IBrush? IdleBrush
    {
        get => GetValue(IdleBrushProperty);
        set => SetValue(IdleBrushProperty, value);
    }

    public IBrush? RunningBrush
    {
        get => GetValue(RunningBrushProperty);
        set => SetValue(RunningBrushProperty, value);
    }

    public IBrush? FailedBrush
    {
        get => GetValue(FailedBrushProperty);
        set => SetValue(FailedBrushProperty, value);
    }

    public IslandWaveform()
    {
        Classes.Add("islandWave");
        IsHitTestVisible = false;
        ClipToBounds = true;
        RenderTransform = _shake;
        _timer.Tick += (_, _) => Tick();
    }

    /// <summary>在 UI 线程馈送 0..1 采样；等待审批与失败时保留缓冲。</summary>
    public void Push(double v)
    {
        if (State is IslandWaveformState.Waiting or IslandWaveformState.Failed) return;
        v = double.IsFinite(v) ? Math.Clamp(v, 0, 1) : 0;
        _filtered = _filtered * 0.7 + v * 0.3;
        _samples[_next] = _filtered;
        _next = (_next + 1) % Capacity;
        var now = Stopwatch.GetTimestamp();
        if (_lastSample != 0)
        {
            var gap = Stopwatch.GetElapsedTime(_lastSample, now).TotalMilliseconds;
            if (gap is >= 40 and <= 2500)
                _sampleIntervalMs = Math.Clamp(_sampleIntervalMs * 0.7 + gap * 0.3, 200, 1500);
        }
        _lastSample = now;
        _line = _area = null;
        InvalidateVisual();
        if (State == IslandWaveformState.Running) RequestFrame();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == StateProperty)
        {
            var now = Stopwatch.GetTimestamp();
            var previous = change.GetOldValue<IslandWaveformState>();
            if (previous == State) return;
            _colorFrom = _transitioning ? CurrentColor(now) : StateColor(previous);
            _colorTo = StateColor(State);
            _transitionStarted = now;
            _failureHadWave = previous is IslandWaveformState.Running or IslandWaveformState.Waiting;
            // 每秒一次采样时，从 0 起步的一阶低通要十几秒才接近真实值；进入运行时先落中值。
            if (State == IslandWaveformState.Running && previous is IslandWaveformState.Idle or IslandWaveformState.Failed)
                _filtered = 0.5;
            _transitioning = CanRender && !ReducedMotion;
            _shake.X = 0;
            InvalidateVisual();
            RequestFrame();
        }
        else if (change.Property == IdleBrushProperty || change.Property == RunningBrushProperty ||
                 change.Property == FailedBrushProperty)
        {
            _colorTo = StateColor(State);
            InvalidateVisual();
        }
        else if (change.Property == IsVisibleProperty)
        {
            SyncTimer();
            // 有效可见性在属性通知之后传播；父级重新显示时再核对一次。
            Dispatcher.UIThread.Post(SyncTimer, DispatcherPriority.Render);
        }
        else if (change.Property == VisualParentProperty && _attached)
        {
            TrackVisibility();
            SyncTimer();
            Dispatcher.UIThread.Post(SyncTimer, DispatcherPriority.Render);
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        TrackVisibility();
        SyncTimer();
        Dispatcher.UIThread.Post(SyncTimer, DispatcherPriority.Render);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        StopTimer();
        UntrackVisibility();
        base.OnDetachedFromVisualTree(e);
    }

    private static bool ReducedMotion => Motion.Reduced || MemoryProfile.DisableVisualEffects;

    private bool CanRender
    {
        get
        {
            if (!_attached || !IsEffectivelyVisible || !IsVisible) return false;
            // Avalonia 12 的 IsEffectivelyVisibleChanged 非公开，监听祖先的 IsVisible 代替。
            foreach (var owner in _visibilityOwners)
                if (!owner.IsVisible) return false;
            return true;
        }
    }

    private void TrackVisibility()
    {
        UntrackVisibility();
        foreach (var owner in this.GetVisualAncestors())
        {
            _visibilityOwners.Add(owner);
            owner.PropertyChanged += OwnerVisibilityChanged;
        }
    }

    private void UntrackVisibility()
    {
        foreach (var owner in _visibilityOwners) owner.PropertyChanged -= OwnerVisibilityChanged;
        _visibilityOwners.Clear();
    }

    private void OwnerVisibilityChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != IsVisibleProperty) return;
        SyncTimer();
        Dispatcher.UIThread.Post(SyncTimer, DispatcherPriority.Render);
    }

    private void SyncTimer()
    {
        if (!CanRender) { StopTimer(); return; }
        if (!_timer.IsEnabled) _timer.Start();
        InvalidateVisual();
    }

    private void StopTimer()
    {
        _timer.Stop();
        _transitioning = false;
        _shake.X = 0;
        ++_frameVersion;
        _frameQueued = false;
    }

    private void Tick()
    {
        if (!CanRender) { StopTimer(); return; }
        var now = Stopwatch.GetTimestamp();
        AdvanceTransition(now);
        // 失败态在抖动结束后是静止平线，不必每 200ms 重绘。
        if (!_transitioning && State == IslandWaveformState.Failed) return;
        InvalidateVisual();
    }

    // 160ms 过渡不能依赖 200ms 采样时钟；只在过渡/最新点淡出期间请求合成帧，绝不另开定时器。
    private void RequestFrame()
    {
        if (_frameQueued || !CanRender || ReducedMotion || TopLevel.GetTopLevel(this) is not { } root) return;
        var version = _frameVersion;
        _frameQueued = true;
        root.RequestAnimationFrame(_ =>
        {
            if (version != _frameVersion) return;
            _frameQueued = false;
            if (!CanRender) { StopTimer(); return; }
            var now = Stopwatch.GetTimestamp();
            AdvanceTransition(now);
            InvalidateVisual();
            // 只有状态过渡需要合成帧；波形滚动与最新点淡出由 200ms 时钟推进。
            if (_transitioning) RequestFrame();
        });
    }

    private void AdvanceTransition(long now)
    {
        var progress = TransitionProgress(now);
        if (ReducedMotion || progress >= 1)
        {
            _transitioning = false;
            _shake.X = 0;
            return;
        }
        _shake.X = _transitioning && State == IslandWaveformState.Failed
            ? 3 * Math.Sin(progress * Math.PI * 4) : 0;
    }

    private double TransitionProgress(long now) =>
        Math.Clamp(Stopwatch.GetElapsedTime(_transitionStarted, now).TotalMilliseconds / TransitionMilliseconds, 0, 1);

    private Color CurrentColor(long now) => _transitioning
        ? Lerp(_colorFrom, _colorTo, TransitionProgress(now)) : StateColor(State);

    private Color StateColor(IslandWaveformState state) => BrushColor(state switch
    {
        IslandWaveformState.Running or IslandWaveformState.Waiting => RunningBrush,
        IslandWaveformState.Failed => FailedBrush,
        _ => IdleBrush,
    });

    private static Color BrushColor(IBrush? brush) => brush is ISolidColorBrush solid
        ? Color.FromArgb((byte)Math.Round(solid.Color.A * solid.Opacity), solid.Color.R, solid.Color.G, solid.Color.B)
        : Colors.Gray;

    private static Color Lerp(Color from, Color to, double t)
    {
        static byte Channel(byte a, byte b, double progress) => (byte)Math.Round(a + (b - a) * progress);
        return Color.FromArgb(Channel(from.A, to.A, t), Channel(from.R, to.R, t),
            Channel(from.G, to.G, t), Channel(from.B, to.B, t));
    }

    private double MarkerOpacity(long now) => _lastSample == 0 ? 0 :
        Math.Clamp(1 - Stopwatch.GetElapsedTime(_lastSample, now).TotalMilliseconds / MarkerMilliseconds, 0, 1);

    private double ScrollPhase(long now) => State == IslandWaveformState.Running && _lastSample != 0
        ? Math.Clamp(Stopwatch.GetElapsedTime(_lastSample, now).TotalMilliseconds / _sampleIntervalMs, 0, 1)
        : 0;

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var size = Bounds.Size;
        if (size.Width <= 12 || size.Height <= 12) return;
        var now = Stopwatch.GetTimestamp();
        var color = CurrentColor(now);
        var period = State == IslandWaveformState.Waiting ? 8000 : 4000;
        var breathing = !ReducedMotion && State is IslandWaveformState.Idle or IslandWaveformState.Waiting
            ? 0.875 + 0.125 * Math.Cos(Stopwatch.GetElapsedTime(_breatheStarted, now).TotalMilliseconds * Math.PI * 2 / period)
            : 1;
        using var clip = context.PushClip(new Rect(size));
        using var opacity = context.PushOpacity(breathing);
        var stroke = new SolidColorBrush(color);
        var wave = State is IslandWaveformState.Running or IslandWaveformState.Waiting ||
            State == IslandWaveformState.Failed && _transitioning && _failureHadWave;
        if (wave)
        {
            EnsureGeometry(size);
            var fill = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb((byte)Math.Round(color.A * 0.12), color.R, color.G, color.B), 0),
                    new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1),
                },
            };
            // 采样之间按估计间隔把整条线左移一格，避免每秒馈送时折线一跳一跳。
            var spacing = (size.Width - 12) / (Capacity - 1);
            using var scroll = context.PushTransform(Matrix.CreateTranslation(-ScrollPhase(now) * spacing, 0));
            context.DrawGeometry(fill, null, _area!);
            context.DrawGeometry(null, new Pen(stroke, 2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), _line!);
            if (State == IslandWaveformState.Running && MarkerOpacity(now) is > 0 and var markerOpacity)
            {
                using var markerFade = context.PushOpacity(markerOpacity);
                context.DrawEllipse(stroke, null, _latestPoint, 6, 6);
            }
        }
        else
        {
            var y = size.Height * 0.3;
            context.DrawLine(new Pen(stroke, 1, lineCap: PenLineCap.Round), new Point(6, y), new Point(size.Width - 6, y));
        }
        if (State == IslandWaveformState.Waiting)
        {
            var ink = BrushColor(IdleBrush);
            var hatch = new Pen(new SolidColorBrush(Color.FromArgb(15, ink.R, ink.G, ink.B)), 1);
            // 45°，两条斜线的垂直距离为 8px。
            for (var x = -size.Height; x < size.Width; x += 8 * Math.Sqrt(2))
                context.DrawLine(hatch, new Point(x, size.Height), new Point(x + size.Height, 0));
        }
    }

    private void EnsureGeometry(Size size)
    {
        if (_line is not null && size == _geometrySize) return;
        _geometrySize = size;
        _line = new StreamGeometry();
        _area = new StreamGeometry();
        using var line = _line.Open();
        using var area = _area.Open();
        var baseline = size.Height - 6;
        for (var i = 0; i < Capacity; i++)
        {
            var point = new Point(6 + i * (size.Width - 12) / (Capacity - 1),
                baseline - _samples[(_next + i) % Capacity] * (size.Height - 12));
            if (i == 0)
            {
                line.BeginFigure(point, false);
                area.BeginFigure(new Point(point.X, baseline), true);
            }
            else line.LineTo(point);
            area.LineTo(point);
            _latestPoint = point;
        }
        line.EndFigure(false);
        area.LineTo(new Point(_latestPoint.X, baseline));
        area.EndFigure(true);
    }
}
