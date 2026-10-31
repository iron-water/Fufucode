using System;
using Avalonia;
using Avalonia.Controls;

namespace Fufucode.App.Views;

/// <summary>模型确认边框始终占一个屏幕像素。</summary>
public sealed class ModelChoiceBorder : Border
{
    protected override Type StyleKeyOverride => typeof(Border);
    private TopLevel? _topLevel;

    public ModelChoiceBorder() => UpdateStrokeWidth();

    private void UpdateStrokeWidth() => BorderThickness = new Thickness(1 / (_topLevel?.RenderScaling ?? 1));
    private void OnScalingChanged(object? sender, EventArgs e) => UpdateStrokeWidth();

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _topLevel = TopLevel.GetTopLevel(this);
        if (_topLevel is not null) _topLevel.ScalingChanged += OnScalingChanged;
        UpdateStrokeWidth();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_topLevel is not null) _topLevel.ScalingChanged -= OnScalingChanged;
        _topLevel = null;
        base.OnDetachedFromVisualTree(e);
    }
}
