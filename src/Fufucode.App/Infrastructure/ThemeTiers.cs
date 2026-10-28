using System;
using Avalonia.Media;

namespace Fufucode.App.Infrastructure;

/// <summary>
/// Fluent 暗色三档底色（窗口底 / 一般栏 / 高亮）的派生规则。
/// 纯函数，便于无头回归：不读 UI、不写持久文件。
/// </summary>
public static class ThemeTiers
{
    /// <summary>高亮档与一般栏的最小加权亮度差（0~255 量纲）。低于它就算“看不出来”。</summary>
    public const double MinHighlightSeparation = 10;

    /// <summary>加权亮度：只用于比较两档底色谁更亮，不用于对比度合规计算。</summary>
    public static double Luminance(Color color) => color.R * .2126 + color.G * .7152 + color.B * .0722;

    /// <summary>
    /// 高亮档必须比一般栏亮一档：两档的备选值有重叠（#333333 同属两档），
    /// 用户选到同色时设置卡与导航行的悬停/选中会和卡片底色一模一样，等于没有高光。
    /// 差值不足时以一般栏提亮 14 作为高亮。
    /// </summary>
    public static Color EnsureVisibleHighlight(Color bar, Color highlight) =>
        Luminance(highlight) < Luminance(bar) + MinHighlightSeparation ? Lift(bar, 14) : highlight;

    private static Color Lift(Color color, int delta) => Color.FromArgb(255,
        (byte)Math.Min(255, color.R + delta),
        (byte)Math.Min(255, color.G + delta),
        (byte)Math.Min(255, color.B + delta));
}
