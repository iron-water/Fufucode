using Avalonia;
using Avalonia.Controls;

namespace Fufucode.Fufu.Pet;

/// <summary>
/// 角色渲染器抽象：窗口/行为层只依赖本接口，动画引擎（占位绘制 → Live2D）可替换。
/// 实现必须可在 UI 线程上提供自己的视图控件，并响应行为状态变化。
/// </summary>
public interface ICharacterRenderer
{
    /// <summary>角色视图控件；宿主窗口直接以其尺寸为窗口尺寸。</summary>
    Control View { get; }

    /// <summary>角色在未缩放逻辑坐标下的尺寸（窗口包围盒基准）。</summary>
    Size CharacterSize { get; }

    /// <summary>切换行为状态；实现负责状态对应的动画/表情。</summary>
    void SetState(PetState state);

    /// <summary>
    /// 命中测试：客户端坐标（DIP）是否在角色实体上。
    /// 用于透明区域鼠标穿透；返回 false 的点应让点击落到桌面。
    /// </summary>
    bool HitTest(Point clientPoint, double scale);
}
