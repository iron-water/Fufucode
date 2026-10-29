using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace Fufucode.App.Infrastructure;

// UI-1 分栏 Splitter 的 4px 拖拽死区。
//
// Avalonia 的 GridSplitter 由 Thumb 驱动：拖动开始后每一个 DragDelta 都立即改动布局，
// 哪怕是一次点击里几像素的手抖也会触发重排，既误触桌面布局、又在高 DPI 下造成可见抖动。
//
// 本类子类化 GridSplitter，重写 OnDragDelta 做累计位移门：自本次拖动开始起的累计位移
// 小于 4px 前，直接返回、不调用基类（布局不变、不重排）；越过死区后放行给基类，此后按
// 原始增量正常拖动。用重写而非事件处理器，是因为 DragDeltaEvent 是冒泡事件、且
// GridSplitter 的类处理器先于任何实例处理器执行——只有重写能真正排在布局改动之前。
//
// 死区只对「指针拖动」生效：键盘方向键的增量拖动是用户明确的一次一步意图，必须即时生效，
// 不能被死区吞掉。二者共用同一 DragDeltaEvent，因此以「本次拖动是否由指针按下发起」
// （pointerInitiated）区分——键盘拖动不经过 PointerPressed，不会进入死区。
public class DeadZoneGridSplitter : GridSplitter
{
    // 复用 GridSplitter 的控件样式键：子类默认 StyleKey 是自己的类型，Fluent 的 GridSplitter
    // 模板（含 Thumb 部件与键盘/指针拖动逻辑）与 GridSplitter.dockSplitter 等样式都不会命中。
    protected override System.Type StyleKeyOverride => typeof(GridSplitter);

    private double _accumulated;
    private bool _open;
    private bool _pointerInitiated;

    /// <summary>
    /// UI-1 死区门纯判定，供定向测试确定性验证：给定「是否指针发起」「当前累计位移」「本次增量」，
    /// 返回 (是否吞掉本次 DragDelta, 新累计位移, 新放行状态)。
    /// </summary>
    public static (bool Swallow, double Accumulated, bool Open) EvaluateDelta(bool pointerInitiated, double accumulated, bool open, Point delta)
    {
        if (!pointerInitiated || open)
        {
            // 键盘拖动或已越死区：放行，不进入死区。
            return (false, accumulated, open);
        }

        accumulated += DragDeadZone.Chebyshev(default, delta);
        return accumulated < DragDeadZone.Start
            ? (true, accumulated, false)
            : (false, accumulated, true);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        _pointerInitiated = e.GetCurrentPoint(this).Properties.IsLeftButtonPressed;
        base.OnPointerPressed(e);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        _pointerInitiated = false;
        Classes.Remove("dragging");
        base.OnPointerCaptureLost(e);
    }

    protected override void OnDragStarted(VectorEventArgs e)
    {
        _accumulated = 0;
        _open = false;
        // 拖动期间分隔条会随指针移动，:pointerover 会反复得失（表现为强调色闪烁）；
        // 用显式类把高亮钉住，拖动结束再撤掉。
        Classes.Add("dragging");
        base.OnDragStarted(e);
    }

    protected override void OnDragCompleted(VectorEventArgs e)
    {
        Classes.Remove("dragging");
        base.OnDragCompleted(e);
    }

    /// <summary>
    /// LiquidGlass 玻璃布局下，dock 宿主不再由 Grid 列宽承载（列全塌成 `*`/0），
    /// GridSplitter 基类改列宽不再有效。此委托接管拖增量 —— 返回 true 表示已处理，
    /// 跳掉基类的列宽写入。非玻璃皮肤为 null，行为不变。
    /// </summary>
    public Func<Vector, bool>? ManualResizeDelta { get; set; }

    protected override void OnDragDelta(VectorEventArgs e)
    {
        var (swallow, accumulated, open) = EvaluateDelta(_pointerInitiated, _accumulated, _open,
            new Point(e.Vector.X, e.Vector.Y));
        _accumulated = accumulated;
        _open = open;
        if (swallow)
        {
            // 仍在死区内：本次增量不改变布局、不触发重排。
            e.Handled = true;
            return;
        }

        if (ManualResizeDelta?.Invoke(e.Vector) == true)
        {
            // 玻璃模式接管：增量已写进 dock 状态，不走基类列宽路径。
            e.Handled = true;
            return;
        }

        base.OnDragDelta(e);
    }
}
