using System;
using Avalonia.Controls;
using Avalonia.Platform;

namespace Fufucode.App.Integration;

/// <summary>
/// 系统托盘图标。只在“关闭窗口时最小化到托盘”开启时可见；默认关闭时完全不创建，
/// 保持“关窗即退出”的既有行为。
///
/// 左键单击 → 显示并激活主窗口；右键菜单 → 显示主窗口 / 退出。
/// 托盘是可选的后台入口：创建失败（平台不支持、无桌面会话）时由调用方回退为不常驻，
/// 不能出现“窗口已隐藏但没有恢复入口”的状态。
/// </summary>
internal sealed class TrayIconService : IDisposable
{
    private const string BrandIconUri = "avares://Fufucode.App/Assets/Brand/fufucode.ico";

    private readonly TrayIcon _tray;
    private readonly NativeMenuItem _showItem;
    private readonly NativeMenuItem _exitItem;
    private bool _disposed;

    public TrayIconService(Action showWindow, Action exitApplication)
    {
        ArgumentNullException.ThrowIfNull(showWindow);
        ArgumentNullException.ThrowIfNull(exitApplication);

        _showItem = new NativeMenuItem("显示主窗口");
        _showItem.Click += (_, _) => showWindow();

        _exitItem = new NativeMenuItem("退出");
        _exitItem.Click += (_, _) => exitApplication();

        var menu = new NativeMenu();
        menu.Add(_showItem);
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(_exitItem);

        _tray = new TrayIcon
        {
            ToolTipText = "Fufucode",
            Menu = menu,
            IsVisible = false,
        };
        // 左键单击恢复窗口；右键由平台弹出 Menu。
        _tray.Clicked += (_, _) => showWindow();

        if (TryLoadBrandIcon() is { } icon) _tray.Icon = icon;
    }

    /// <summary>托盘图标是否显示。设置关闭时立即隐藏，不留下隐形入口。</summary>
    public bool IsVisible
    {
        get => _tray.IsVisible;
        set
        {
            if (_disposed) return;
            _tray.IsVisible = value;
        }
    }

    /// <summary>品牌图标加载失败不影响托盘功能（系统会显示默认图标）。</summary>
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
        _tray.Dispose();
    }
}
