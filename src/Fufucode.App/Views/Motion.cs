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
/// </summary>
internal static class Motion
{
    /// <summary>消息/回合进入时长。</summary>
    public static readonly TimeSpan EnterDuration = TimeSpan.FromMilliseconds(180);

    /// <summary>底部元信息卡进入时长。</summary>
    public static readonly TimeSpan FooterDuration = TimeSpan.FromMilliseconds(160);

    /// <summary>
    /// 消息进入：淡入并轻微上移。只在元素第一次挂到可视树时调用，
    /// 流式刷新不会重播。
    /// </summary>
    public static void AnimateIn(Control control, double offsetY = 6)
    {
        // 用户关闭了系统动画：直接落到终态，不做过渡。
        if (MotionPreference.ReducedMotion)
        {
            control.Opacity = 1;
            control.RenderTransform = null;
            return;
        }

        var translate = new TranslateTransform(0, offsetY);
        control.RenderTransform = translate;
        control.Opacity = 0;
        control.Transitions = new Transitions
        {
            new DoubleTransition
            {
                Property = Visual.OpacityProperty,
                Duration = EnterDuration,
                Easing = new CubicEaseOut(),
            },
            new DoubleTransition
            {
                Property = TranslateTransform.YProperty,
                Duration = EnterDuration,
                Easing = new CubicEaseOut(),
            },
        };

        // 延后一帧再设终值，否则同一布局帧内赋值不会触发过渡。
        Dispatcher.UIThread.Post(() =>
        {
            control.Opacity = 1;
            translate.Y = 0;
        }, DispatcherPriority.Render);
    }

    /// <summary>底部卡进入：淡入，位移更小。</summary>
    public static void AnimateFooter(Control control) => AnimateIn(control, offsetY: 4);
}
