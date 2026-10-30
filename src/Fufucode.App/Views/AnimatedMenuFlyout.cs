using Avalonia.Controls;

namespace Fufucode.App.Views;

/// <summary>辅助菜单沿锚点轻移淡入，保留原生菜单定位、键盘和子菜单行为。</summary>
internal sealed class AnimatedMenuFlyout : MenuFlyout
{
    private Control? _presenter;

    public AnimatedMenuFlyout()
    {
        Opened += (_, _) =>
        {
            if (_presenter is not null) SurfaceMotion.Show(_presenter, offsetY: -5);
        };
        Closed += (_, _) =>
        {
            if (_presenter is not null) SurfaceMotion.Reset(_presenter);
        };
    }

    protected override Control CreatePresenter() => _presenter = base.CreatePresenter();
}
