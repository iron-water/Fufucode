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
/// 时长统一收敛为三档（对齐 Windows 11 Fluent 节奏）：
/// Fast=100ms（悬停/按压）、Base=160ms（淡入/折叠/内容切换）、Slow=250ms（页面/抽屉）。
/// 位移控制在 4–8px，不做弹跳；缓动全应用只用 CubicEaseOut。
/// 系统关闭动画时（<see cref="MotionPreference.ReducedMotion"/>）一律直接落到终态。
/// </summary>
internal static class Motion
{
    /// <summary>Fast：悬停、按压等即时反馈。</summary>
    public static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(100);

    /// <summary>Base：淡入、折叠、内容切换。</summary>
    public static readonly TimeSpan Base = TimeSpan.FromMilliseconds(160);

    /// <summary>Slow：页面切换、抽屉等大面积过渡。</summary>
    public static readonly TimeSpan Slow = TimeSpan.FromMilliseconds(250);

    // 旧名别名：调用点分布在其它文件（Views/Styles 由其它会话负责），
    // 本轮不改那些文件，先统一指向三档，后续可逐个收编为 Fast/Base/Slow。
    /// <summary>[别名] 旧微交互时长，现为 Fast。</summary>
    public static readonly TimeSpan FastDuration = Fast;

    /// <summary>[别名] 旧常规过渡时长，现为 Base。</summary>
    public static readonly TimeSpan StandardDuration = Base;

    /// <summary>[别名] 旧大面积过渡时长，现为 Slow。</summary>
    public static readonly TimeSpan SlowDuration = Slow;

    /// <summary>[别名] 消息进入时长，现为 Base。</summary>
    public static readonly TimeSpan EnterDuration = Base;

    /// <summary>[别名] 底部元信息卡进入时长，现为 Fast。</summary>
    public static readonly TimeSpan FooterDuration = Fast;

    /// <summary>全应用唯一缓动：CubicEaseOut（快出慢收），替代原 Exponential/Spline 曲线。</summary>
    public static readonly CubicEaseOut CubicEase = new();

    /// <summary>[别名] 旧名称保留给本文件之外的调用点，实例与 <see cref="CubicEase"/> 相同。</summary>
    public static Easing FluentEasing => CubicEase;

    /// <summary>
    /// 无头自测里动画一律即时落终态：断言检查的是最终可见性，不能被 100–250ms 的过渡时序影响。
    /// </summary>
    public static bool Reduced => (Program.SelfTestMode && !Program.NativeMotionSelfTest) || MotionPreference.ReducedMotion;

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
        Action<double> apply, Action? completed = null, TopLevel? frameOwner = null)
    {
        if (Reduced || Math.Abs(to - from) < 0.01)
        {
            apply(to);
            completed?.Invoke();
            return new ActionDisposable(() => { });
        }

        var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        if (frameOwner is not null)
        {
            var cancelled = false;
            void Frame(TimeSpan _)
            {
                if (cancelled) return;
                var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(startedAt);
                var progress = Math.Clamp(elapsed.TotalMilliseconds / duration.TotalMilliseconds, 0, 1);
                apply(from + (to - from) * CubicEase.Ease(progress));
                if (progress < 1) frameOwner.RequestAnimationFrame(Frame);
                else completed?.Invoke();
            }
            frameOwner.RequestAnimationFrame(Frame);
            return new ActionDisposable(() => cancelled = true);
        }
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) =>
        {
            var elapsed = (System.Diagnostics.Stopwatch.GetTimestamp() - startedAt) * 1000.0
                          / System.Diagnostics.Stopwatch.Frequency;
            var progress = Math.Clamp(elapsed / duration.TotalMilliseconds, 0, 1);
            // 与其它过渡共用唯一缓动，避免手写曲线再次分叉。
            var eased = CubicEase.Ease(progress);
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
