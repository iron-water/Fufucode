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

/// <summary>一段色线沿会话项的圆角边框周游；窗口/父容器隐藏和卸载时停止计时。</summary>
public sealed class SessionOrbit : Control
{
    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<SessionOrbit, IBrush?>(nameof(Stroke));
    public IBrush? Stroke { get => GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public static readonly StyledProperty<CornerRadius> CornerRadiusProperty =
        AvaloniaProperty.Register<SessionOrbit, CornerRadius>(nameof(CornerRadius), new CornerRadius(4));
    public CornerRadius CornerRadius { get => GetValue(CornerRadiusProperty); set => SetValue(CornerRadiusProperty, value); }
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly List<Visual> _ancestors = new();
    private readonly long _start = Stopwatch.GetTimestamp();
    internal bool IsAnimationRunning => _timer.IsEnabled;

    static SessionOrbit() => AffectsRender<SessionOrbit>(StrokeProperty, CornerRadiusProperty);

    public SessionOrbit()
    {
        IsHitTestVisible = false;
        _timer.Tick += (_, _) => InvalidateVisual();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        foreach (var ancestor in this.GetVisualAncestors())
        {
            _ancestors.Add(ancestor);
            ancestor.PropertyChanged += OnAncestorChanged;
        }
        MemoryProfile.VisualEffectsChanged += SyncTimer;
        SyncTimer();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _timer.Stop();
        foreach (var ancestor in _ancestors) ancestor.PropertyChanged -= OnAncestorChanged;
        _ancestors.Clear();
        MemoryProfile.VisualEffectsChanged -= SyncTimer;
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty) SyncTimer();
    }

    private void OnAncestorChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == IsVisibleProperty) SyncTimer();
    }

    private void SyncTimer()
    {
        if (VisualRoot is not null && IsVisible && _ancestors.TrueForAll(a => a.IsVisible) &&
            !MemoryProfile.DisableVisualEffects) _timer.Start();
        else _timer.Stop();
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Stroke is null || Bounds.Width < 4 || Bounds.Height < 4) return;
        const double thickness = 1.5;
        var inset = thickness / 2;
        var rect = new Rect(inset, inset, Bounds.Width - thickness, Bounds.Height - thickness);
        var radius = Math.Clamp(CornerRadius.TopLeft - inset, 0, Math.Min(rect.Width, rect.Height) / 2);
        var perimeter = 2 * (rect.Width + rect.Height - 4 * radius) + 2 * Math.PI * radius;
        var length = Math.Clamp(perimeter * .24, 24, 90);
        var phase = MemoryProfile.DisableVisualEffects ? 0 : Stopwatch.GetElapsedTime(_start).TotalSeconds / 2.8;
        var pen = new Pen(Stroke, thickness)
        {
            DashStyle = new DashStyle(new[] { length / thickness, (perimeter - length) / thickness },
                -phase * perimeter / thickness),
            LineCap = PenLineCap.Round,
        };
        context.DrawRectangle(null, pen, rect, radius, radius);
    }
}
