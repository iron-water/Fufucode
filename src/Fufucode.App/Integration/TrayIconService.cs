using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;

namespace Fufucode.App.Integration;

/// <summary>
/// 系统托盘图标。左键恢复主窗口；右键菜单在每次弹出前由组合根填充，
/// 使固定/最近会话与当前外观设置始终反映实际状态。
/// </summary>
internal sealed class TrayIconService : IDisposable
{
    private const string BrandIconUri = "avares://Fufucode.App/Assets/Brand/fufucode.ico";

    private readonly TrayIcon _tray;
    private readonly NativeMenu _menu;
    private readonly Action<NativeMenu> _fillMenu;
    private bool _disposed;

    public TrayIconService(Action showWindow, Action<NativeMenu> fillMenu)
    {
        ArgumentNullException.ThrowIfNull(showWindow);
        ArgumentNullException.ThrowIfNull(fillMenu);
        _fillMenu = fillMenu;
        _menu = new NativeMenu();
        _menu.NeedsUpdate += (_, _) => RebuildMenu();
        RebuildMenu();

        _tray = new TrayIcon
        {
            ToolTipText = "Fufucode",
            Menu = _menu,
            IsVisible = true,
        };
        // Win32 实现对左键单击触发 Clicked；右键由平台弹出 Menu。
        _tray.Clicked += (_, _) => showWindow();
        if (TryLoadBrandIcon() is { } icon) _tray.Icon = icon;

        if (Application.Current is { } app)
        {
            var icons = TrayIcon.GetIcons(app);
            if (icons is null)
            {
                icons = new TrayIcons();
                TrayIcon.SetIcons(app, icons);
            }
            if (!icons.Contains(_tray))
            {
                icons.Add(_tray);
            }
        }
    }

    internal NativeMenu MenuForTesting => _menu;
    internal void RebuildMenu()
    {
        if (_disposed) return;
        _menu.Items.Clear();
        _fillMenu(_menu);
    }

    public bool IsVisible
    {
        get => _tray.IsVisible;
        set
        {
            if (_disposed) return;
            _tray.IsVisible = value;
        }
    }

    private static WindowIcon? TryLoadBrandIcon()
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri(BrandIconUri));
            return new WindowIcon(stream);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (Application.Current is { } app)
        {
            var icons = TrayIcon.GetIcons(app);
            icons?.Remove(_tray);
        }
        _tray.Dispose();
    }
}
