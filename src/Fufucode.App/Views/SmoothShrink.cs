using System;
using Avalonia;
using Avalonia.Controls;
using Fufucode.App.Integration;

namespace Fufucode.App.Views;

/// <summary>
/// 流式输出期间，块高度变小（运行状态行收起、临时内容换成定稿）时不一帧缩回，
/// 而是从原高度缓降到新高度，内容只向下伸展、不向回跳。只在所在消息仍在输出时生效，
/// 用户点击展开/收起有自己的动画，不受影响。
/// </summary>
public static class SmoothShrink
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("IsEnabled", typeof(SmoothShrink));

    public static bool GetIsEnabled(Control control) => control.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(Control control, bool value) => control.SetValue(IsEnabledProperty, value);

    static SmoothShrink()
    {
        IsEnabledProperty.Changed.AddClassHandler<Control>((control, change) =>
        {
            if (change.NewValue is true) control.SizeChanged += OnSizeChanged;
            else control.SizeChanged -= OnSizeChanged;
        });
    }

    private sealed class Hold
    {
        public IDisposable? Animation;
        public double OriginalMinHeight;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Control, Hold> Holds = new();

    private static void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (sender is not Control control || !e.HeightChanged || e.PreviousSize.Height <= 0) return;
        var from = e.PreviousSize.Height;
        var to = e.NewSize.Height;
        if (to >= from - 2 || Motion.Reduced || MemoryProfile.DisableVisualEffects) return;
        if (!RichMarkdownView.GetIsStreaming(control)) return;
        var hold = Holds.GetValue(control, _ => new Hold { OriginalMinHeight = control.MinHeight });
        if (hold.Animation is not null) return;
        control.MinHeight = from;
        hold.Animation = Motion.AnimateValue(from, to, Motion.Base, height => control.MinHeight = height, () =>
        {
            hold.Animation = null;
            control.MinHeight = hold.OriginalMinHeight;
        }, TopLevel.GetTopLevel(control));
    }
}
