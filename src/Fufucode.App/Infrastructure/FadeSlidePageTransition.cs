// 当前未接线（保留供页面切换使用）。
using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Media;
using Avalonia.Styling;
using Fufucode.App.Views;

namespace Fufucode.App.Infrastructure;

/// <summary>
/// WinUI 式页面入场：新页淡入同时上滑一小段（对应 Frame NavigationThemeTransition），
/// 旧页立即隐藏不参与动画。对齐 UniGetUI EntrancePageTransition：
/// 250ms / 28px / CubicEaseOut，尊重系统「减少动画」。
/// </summary>
public sealed class FadeSlidePageTransition : IPageTransition
{
    public TimeSpan Duration { get; set; } = Motion.Slow;

    /// <summary>新页淡入时的上滑距离（px）。</summary>
    public double VerticalOffset { get; set; } = 28;

    public async Task Start(Visual? from, Visual? to, bool forward, CancellationToken cancellationToken)
    {
        // 减少动画：旧页隐藏、新页即现，无交叠。
        if (MotionPreference.ReducedMotion)
        {
            if (from is not null)
            {
                from.Opacity = 0;
            }

            if (to is not null)
            {
                to.Opacity = 1;
            }

            return;
        }

        // 旧页立即隐藏，只让新页做入场动画。
        if (from is not null)
        {
            from.Opacity = 0;
        }

        if (to is null)
        {
            return;
        }

        var enter = new Animation
        {
            Duration = Duration,
            // 统一复用 Motion 的唯一缓动 CubicEaseOut，原 SplineEasing 曲线已删除。
            Easing = Motion.CubicEase,
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame
                {
                    Cue = new Cue(0d),
                    Setters =
                    {
                        new Setter(Visual.OpacityProperty, 0d),
                        new Setter(TranslateTransform.YProperty, VerticalOffset),
                    },
                },
                new KeyFrame
                {
                    Cue = new Cue(1d),
                    Setters =
                    {
                        new Setter(Visual.OpacityProperty, 1d),
                        new Setter(TranslateTransform.YProperty, 0d),
                    },
                },
            },
        };

        try
        {
            await enter.RunAsync(to, cancellationToken);
        }
        finally
        {
            // 被取消也要还原，避免 presenter 复用时旧页停留在不可见态。
            if (from is not null)
            {
                from.Opacity = 1;
            }
        }
    }
}
