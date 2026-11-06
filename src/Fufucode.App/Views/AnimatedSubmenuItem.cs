using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace Fufucode.App.Views;

/// <summary>对子菜单实际 Popup 表面应用统一过渡，保留原生定位与键盘交互。</summary>
internal sealed class AnimatedSubmenuItem : MenuItem
{
    protected override System.Type StyleKeyOverride => typeof(MenuItem);
    private Popup? _popup;

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        if (_popup is not null) _popup.Opened -= OnPopupOpened;
        base.OnApplyTemplate(e);
        _popup = e.NameScope.Find<Popup>("PART_Popup");
        if (_popup is not null) _popup.Opened += OnPopupOpened;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property == IsSubMenuOpenProperty && change.GetNewValue<bool>() && _popup?.Child is Control surface)
            surface.Opacity = 0;
        base.OnPropertyChanged(change);
    }

    private void OnPopupOpened(object? sender, System.EventArgs e)
    {
        if (_popup?.Child is Control surface) SurfaceMotion.Show(surface, offsetY: 0);
    }
}
