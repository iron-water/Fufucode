using System;
using Avalonia;
using Avalonia.Controls;
using Fufucode.App.Infrastructure;

namespace Fufucode.App.Views;

/// <summary>
/// 可折叠内容容器：展开/收起时对高度与不透明度做过渡，而不是瞬间出现或消失。
///
/// 做法是用 <see cref="Layoutable.MaxHeightProperty"/> 作为可动画的替身：
/// 展开前先量出内容的完整高度，过渡到该高度后解除限制，
/// 这样后续内容增长（例如流式新增工具）不会被裁剪。
/// </summary>
public sealed class CollapsiblePanel : ContentControl
{
    public static readonly StyledProperty<bool> IsExpandedProperty =
        AvaloniaProperty.Register<CollapsiblePanel, bool>(nameof(IsExpanded));

    private IDisposable? _animation;
    private bool _attached;

    public bool IsExpanded
    {
        get => GetValue(IsExpandedProperty);
        set => SetValue(IsExpandedProperty, value);
    }

    public CollapsiblePanel()
    {
        ClipToBounds = true;
        MaxHeight = 0;
        Opacity = 0;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        Apply(IsExpanded, animate: false);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _animation?.Dispose();
        _animation = null;
        _attached = false;
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != IsExpandedProperty || !_attached) return;
        Apply(IsExpanded, animate: Bounds.Height > 0 || IsExpanded);
    }

    private void Apply(bool expanded, bool animate)
    {
        _animation?.Dispose();
        _animation = null;

        if (!animate || Motion.Reduced)
        {
            MaxHeight = expanded ? double.PositiveInfinity : 0;
            Opacity = expanded ? 1 : 0;
            return;
        }

        // 从当前可见高度反向，不先抬到完整内容高度；首帧折叠也不会撑开页面。
        var from = double.IsFinite(MaxHeight) ? Math.Min(Bounds.Height, MaxHeight) : Bounds.Height;
        var target = expanded ? MeasureContentHeight() : 0;
        var opacity = Opacity;
        MaxHeight = from;
        _animation = Motion.AnimateValue(from, target, Motion.StandardDuration,
            value =>
            {
                MaxHeight = value;
                var progress = Math.Abs(target - from) < .01 ? 1 : (value - from) / (target - from);
                Opacity = opacity + ((expanded ? 1 : 0) - opacity) * progress;
            },
            () =>
            {
                MaxHeight = expanded ? double.PositiveInfinity : 0;
                Opacity = expanded ? 1 : 0;
            });
    }

    /// <summary>直接量内容，不受当前 MaxHeight 影响。</summary>
    private double MeasureContentHeight()
    {
        if (Content is not Control content) return 0;
        var width = Bounds.Width > 0 ? Bounds.Width : double.PositiveInfinity;
        content.Measure(new Size(width, double.PositiveInfinity));
        return Math.Max(0, content.DesiredSize.Height);
    }
}
