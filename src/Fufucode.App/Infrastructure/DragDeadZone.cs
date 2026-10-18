using System;
using Avalonia;

namespace Fufucode.App.Infrastructure;

// UI-1 拖拽死区与微晃动抑制：统一 4px 起拖阈值与拖拽中的位移上报阈值。
// 交互质感来自 open-claude-ui 的「4px 拖拽移动死区」：指针位移小于 4px 时绝不进入
// 拖拽态（过滤单击微颤），拖拽中位移小于 4px 也不上报，避免每帧全量命中测试与重排
// 造成的重绘卡顿。距离用切比雪夫度量（x、y 位移绝对值的较大者），与方向无关。
public static class DragDeadZone
{
    // 进入拖拽态所需的最小指针位移（DIP）。
    public const double Start = 4;

    // 拖拽过程中触发位置上报的最小累计位移（DIP）。
    public const double Report = 4;

    // 切比雪夫距离：x、y 位移绝对值的较大者。方向无关，作为死区判定度量。
    public static double Chebyshev(Point from, Point to)
    {
        return Math.Max(Math.Abs(to.X - from.X), Math.Abs(to.Y - from.Y));
    }

    // 给定位移是否已越过起拖死区（大于等于 Start）。
    public static bool HasStarted(Point from, Point to) => Chebyshev(from, to) >= Start;

    // 给定自上次上报以来的累计位移是否应上报（大于等于 Report）。
    public static bool ShouldReport(Point lastReported, Point current) => Chebyshev(lastReported, current) >= Report;
}
