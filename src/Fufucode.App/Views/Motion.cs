using System;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Fufucode.App.Infrastructure;

namespace Fufucode.App.Views;

/// <summary>
/// 全应用共享的动画原语。视觉参数集中在这里，不随后端变化。
///
/// 时值与缓动对齐 Windows 11 的 Fluent 动效：
/// 常用 150–250ms，快出慢入，位移控制在 4–8px，不做弹跳。
/// 系统关闭动画时（<see cref="MotionPreference.ReducedMotion"/>）一律直接落到终态。
/// </summary>
internal static class Motion
{
    /// <summary>微交互：悬停、指示器、淡入淡出。</summary>
    public static readonly TimeSpan FastDuration = TimeSpan.FromMilliseconds(150);

    /// <summary>常规过渡：卡片、折叠、列表项。</summary>
    public static readonly TimeSpan StandardDuration = TimeSpan.FromMilliseconds(200);

    /// <summary>较大面积：页面切换、抽屉。</summary>
    public static readonly TimeSpan SlowDuration = TimeSpan.FromMilliseconds(250);

    /// <summary>消息进入时长（沿用既有节奏）。</summary>
    public static readonly TimeSpan EnterDuration = StandardDuration;

    /// <summary>底部元信息卡进入时长。</summary>
    public static readonly TimeSpan FooterDuration = FastDuration;

    /// <summary>Windows Fluent 的 fast-out-slow-in 曲线。</summary>
    public static Easing FluentEasing { get; } = new ExponentialEaseOut();

    /// <summary>
    /// 无头自测里动画一律即时落终态：断言检查的是最终可见性，不能被 150–250ms 的过渡时序影响。
    /// </summary>
    public static bool Reduced => Program.SelfTestMode || MotionPreference.ReducedMotion;

    /// <summary>
    /// 消息进入：淡入并轻微上移。只在元素第一次挂到可视树时调用，
    /// 流式刷新不会重播。
    /// </summary>
    public static void AnimateIn(Control control, double offsetY = 6, TimeSpan? duration = null)
    {
        if (Reduced)
        {
            control.Opacity = 1;
            control.RenderTransform = null;
            return;
        }

        var time = duration ?? EnterDuration;
        var translate = new TranslateTransform(0, offsetY);
        control.RenderTransform = translate;
        control.Opacity = 0;
        control.Transitions = new Transitions
        {
            new DoubleTransition { Property = Visual.OpacityProperty, Duration = time, Easing = FluentEasing },
            new DoubleTransition { Property = TranslateTransform.YProperty, Duration = time, Easing = FluentEasing },
        };

        // 延后一帧再设终值，否则同一布局帧内赋值不会触发过渡。
        Dispatcher.UIThread.Post(() =>
        {
            control.Opacity = 1;
            translate.Y = 0;
        }, DispatcherPriority.Render);
    }

    /// <summary>底部卡进入：淡入，位移更小。</summary>
    public static void AnimateFooter(Control control) => AnimateIn(control, offsetY: 4, FooterDuration);

    /// <summary>
    /// 把一个数值从当前值缓动到目标值（16ms 一帧），用于不能直接上过渡的属性，
    /// 例如 Grid 列宽。返回的对象可用于中途取消。
    /// </summary>
    public static IDisposable AnimateValue(double from, double to, TimeSpan duration,
        Action<double> apply, Action? completed = null)
    {
        if (Reduced || Math.Abs(to - from) < 0.01)
        {
            apply(to);
            completed?.Invoke();
            return new ActionDisposable(() => { });
        }

        var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) =>
        {
            var elapsed = (System.Diagnostics.Stopwatch.GetTimestamp() - startedAt) * 1000.0
                          / System.Diagnostics.Stopwatch.Frequency;
            var progress = Math.Clamp(elapsed / duration.TotalMilliseconds, 0, 1);
            // 与其它过渡一致的快出慢入。
            var eased = 1 - Math.Pow(1 - progress, 3);
            apply(from + (to - from) * eased);
            if (progress < 1) return;
            timer.Stop();
            apply(to);
            completed?.Invoke();
        };
        timer.Start();
        return new ActionDisposable(() => timer.Stop());
    }

    private sealed class ActionDisposable(Action dispose) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            dispose();
        }
    }

    /// <summary>淡出并轻微下移，结束后可选地隐藏元素。</summary>
    public static void AnimateOut(Control control, double offsetY = 4, Action? onCompleted = null)
    {
        if (Reduced)
        {
            control.Opacity = 0;
            onCompleted?.Invoke();
            return;
        }

        var translate = new TranslateTransform(0, 0);
        control.RenderTransform = translate;
        control.Transitions = new Transitions
        {
            new DoubleTransition { Property = Visual.OpacityProperty, Duration = FastDuration, Easing = FluentEasing },
            new DoubleTransition { Property = TranslateTransform.YProperty, Duration = FastDuration, Easing = FluentEasing },
        };

        Dispatcher.UIThread.Post(() =>
        {
            control.Opacity = 0;
            translate.Y = offsetY;
        }, DispatcherPriority.Render);

        DispatcherTimer.RunOnce(() => onCompleted?.Invoke(), FastDuration + TimeSpan.FromMilliseconds(20));
    }

    /// <summary>
    /// 显示/隐藏一个原本用 IsVisible 硬切的元素：先挂上过渡，再改属性。
    /// 隐藏完成后再真正撤掉可见性，避免瞬间消失。
    /// </summary>
    public static void SetVisible(Control control, bool visible, double offsetY = 4)
    {
        if (Reduced)
        {
            control.IsVisible = visible;
            control.Opacity = 1;
            return;
        }

        if (visible)
        {
            if (!control.IsVisible)
            {
                control.IsVisible = true;
                control.Opacity = 0;
                var translate = new TranslateTransform(0, offsetY);
                control.RenderTransform = translate;
                control.Transitions = new Transitions
                {
                    new DoubleTransition { Property = Visual.OpacityProperty, Duration = StandardDuration, Easing = FluentEasing },
                    new DoubleTransition { Property = TranslateTransform.YProperty, Duration = StandardDuration, Easing = FluentEasing },
                };
                Dispatcher.UIThread.Post(() => { control.Opacity = 1; translate.Y = 0; }, DispatcherPriority.Render);
            }
            return;
        }

        if (!control.IsVisible) return;
        AnimateOut(control, offsetY, () => control.IsVisible = false);
    }
}
