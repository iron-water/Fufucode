using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.Collections.Generic;
using Fufucode.App.Integration;

namespace Fufucode.App.Views;

/// <summary>用同一条四像素边框表达输入焦点；呼吸只更新画刷，不触发布局。</summary>
public sealed class ComposerFocusBorder : Border
{
    protected override Type StyleKeyOverride => typeof(Border);
    private readonly SolidColorBrush _stroke = new(Colors.White);
    private TopLevel? _topLevel;
    private readonly DispatcherTimer _pulse = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private long _started;
    private readonly List<Visual> _visibilityOwners = new();

    public ComposerFocusBorder()
    {
        UpdateStrokeWidth();
        BorderBrush = _stroke;
        // 填充延伸到整个描边下面，半透明呼吸时不会出现内外两种底色。
        BackgroundSizing = BackgroundSizing.OuterBorderEdge;
        BoxShadow = default;
        Transitions = null;
        _pulse.Tick += (_, _) =>
        {
            if (!CanPulse) { RefreshPulse(); return; }
            var seconds = System.Diagnostics.Stopwatch.GetElapsedTime(_started).TotalSeconds;
            _stroke.Opacity = 0.12 + 0.88 * (0.5 + 0.5 * Math.Cos(seconds * Math.PI / 2.4));
        };
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsKeyboardFocusWithinProperty || e.Property == IsVisibleProperty) RefreshPulse();
        };
    }

    private void UpdateStrokeWidth()
    {
        // 以屏幕像素计宽：150% DPI 下也保持 4px，而不是放大成 6px。
        var width = 4 / (_topLevel?.RenderScaling ?? 1);
        BorderThickness = new Thickness(width);
        Padding = new Thickness(17 - width, 15 - width, 17 - width, 13 - width);
    }

    private void OnScalingChanged(object? sender, EventArgs e) => UpdateStrokeWidth();

    internal double PulseStrength => _stroke.Opacity;
    private bool CanPulse => IsEffectivelyVisible && TopLevel.GetTopLevel(this) is not null &&
        !IsKeyboardFocusWithin && !Motion.Reduced && !MemoryProfile.DisableVisualEffects;

    private void RefreshPulse()
    {
        if (CanPulse && _pulse.IsEnabled) return;
        _pulse.Stop();
        _stroke.Opacity = 1;
        if (!CanPulse) return;
        _started = System.Diagnostics.Stopwatch.GetTimestamp();
        _pulse.Start();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _topLevel = TopLevel.GetTopLevel(this);
        if (_topLevel is not null) _topLevel.ScalingChanged += OnScalingChanged;
        UpdateStrokeWidth();
        foreach (var owner in this.GetVisualAncestors())
        {
            _visibilityOwners.Add(owner);
            owner.PropertyChanged += OnOwnerVisibilityChanged;
        }
        RefreshPulse();
    }

    private void OnOwnerVisibilityChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == IsVisibleProperty) RefreshPulse();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _pulse.Stop();
        _stroke.Opacity = 1;
        if (_topLevel is not null) _topLevel.ScalingChanged -= OnScalingChanged;
        _topLevel = null;
        foreach (var owner in _visibilityOwners) owner.PropertyChanged -= OnOwnerVisibilityChanged;
        _visibilityOwners.Clear();
        base.OnDetachedFromVisualTree(e);
    }
}
