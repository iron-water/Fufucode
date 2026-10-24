using System;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Threading;
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
        AvaloniaProperty.Register<CollapsiblePanel, bool>(nameof(IsExpanded), true);

    private IDisposable? _settleTimer;
    private bool _attached;

    public bool IsExpanded
    {
        get => GetValue(IsExpandedProperty);
        set => SetValue(IsExpandedProperty, value);
    }

    public CollapsiblePanel()
    {
        ClipToBounds = true;
        Transitions = new Transitions
        {
            new DoubleTransition
            {
                Property = MaxHeightProperty,
                Duration = Motion.StandardDuration,
                Easing = Motion.FluentEasing,
            },
            new DoubleTransition
            {
                Property = OpacityProperty,
                Duration = Motion.FastDuration,
                Easing = Motion.FluentEasing,
            },
        };
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        Apply(IsExpanded, animate: false);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _settleTimer?.Dispose();
        _settleTimer = null;
        _attached = false;
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != IsExpandedProperty || !_attached) return;
        Apply(IsExpanded, animate: !Motion.Reduced);
    }

    private void Apply(bool expanded, bool animate)
    {
        _settleTimer?.Dispose();
        _settleTimer = null;

        if (!animate)
        {
            MaxHeight = expanded ? double.PositiveInfinity : 0;
            Opacity = expanded ? 1 : 0;
            return;
        }

        if (expanded)
        {
            var target = MeasureContentHeight();
            MaxHeight = 0;
            Opacity = 0;
            Dispatcher.UIThread.Post(() =>
            {
                MaxHeight = target;
                Opacity = 1;
            }, DispatcherPriority.Background);

            // 过渡结束后解除高度限制，后续内容增长不会被裁掉。
            _settleTimer = DispatcherTimer.RunOnce(
                () => MaxHeight = double.PositiveInfinity,
                Motion.StandardDuration + TimeSpan.FromMilliseconds(30));
            return;
        }

        var current = Math.Max(Bounds.Height, MeasureContentHeight());
        MaxHeight = current;
        Opacity = 1;
        Dispatcher.UIThread.Post(() =>
        {
            MaxHeight = 0;
            Opacity = 0;
        }, DispatcherPriority.Background);
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
