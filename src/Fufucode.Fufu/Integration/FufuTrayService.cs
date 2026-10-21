using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;

namespace Fufucode.Fufu.Integration;

/// <summary>FUFU 独立托盘图标：左键恢复桌宠；右键菜单每次弹出前重建以反映真实状态。</summary>
internal sealed class FufuTrayService : IDisposable
{
    private const string IconUri = "avares://fufu/Assets/fufu.png";

    private readonly TrayIcon _tray;
    private readonly NativeMenu _menu;
    private readonly Action<NativeMenu> _fillMenu;
    private bool _disposed;

    public FufuTrayService(Action activate, Action<NativeMenu> fillMenu)
    {
        _fillMenu = fillMenu;
        _menu = new NativeMenu();
        _menu.NeedsUpdate += (_, _) => RebuildMenu();
        RebuildMenu();

        _tray = new TrayIcon
        {
            ToolTipText = "FUFU",
            Menu = _menu,
            IsVisible = true,
        };
        _tray.Clicked += (_, _) => activate();
        var icon = TryLoadIcon();
        if (icon is not null) _tray.Icon = icon;

        // Avalonia 12：TrayIcon 必须挂到 Application 的 Icons 附加属性上才会真正创建平台图标（否则 IsVisible 是空操作）。
        if (Application.Current is { } app)
            TrayIcon.SetIcons(app, new TrayIcons { _tray });
        FufuLog.Write($"TrayIcon created, IsVisible={_tray.IsVisible}, icon={(icon is not null ? "loaded" : "MISSING")}, attached={Application.Current is not null}");
    }

    public void RebuildMenu()
    {
        if (_disposed) return;
        _menu.Items.Clear();
        _fillMenu(_menu);
    }

    private static WindowIcon? TryLoadIcon()
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri(IconUri));
            return new WindowIcon(stream);
        }
        catch (Exception) { return null; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _tray.Dispose();
    }
}
