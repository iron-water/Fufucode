using System.Runtime.InteropServices;
using Avalonia;
using Fufucode.Fufu.Pet;

namespace Fufucode.Fufu.Integration;

/// <summary>桌宠窗口的 Win32 细节：不抢焦点的扩展样式 + 窗体过程子类化原语。</summary>
internal static class Win32WindowHelper
{
    internal const int GWLP_WNDPROC = -4;
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    /// <summary>
    /// WS_EX_NOACTIVATE：点击桌宠不夺走当前前台窗口焦点；
    /// WS_EX_TOOLWINDOW：不进 Alt-Tab 列表。
    /// </summary>
    public static void ApplyNoActivateStyles(IntPtr hwnd)
    {
        var style = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(style | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE));
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    internal static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    internal static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    internal static extern IntPtr CallWindowProcW(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg,
        IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    internal static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT { public int X; public int Y; }
}

/// <summary>
/// 透明区域鼠标穿透：子类化窗体过程，WM_NCHITTEST 时把光标位置换算为 DIP 客户端坐标，
/// 交给渲染器 HitTest；不在角色实体上的点击返回 HTTRANSPARENT 落到下层窗口。
/// 安装失败时静默放弃穿透（窗口仍可拖动/点击），不影响可用性。
/// </summary>
internal sealed class Win32PetHitTest : IDisposable
{
    private const int WM_NCHITTEST = 0x0084;
    private const int HTTRANSPARENT = -1;
    private const int HTCLIENT = 1;

    private readonly IntPtr _hwnd;
    private readonly PetWindow _window;
    private readonly ICharacterRenderer _renderer;
    private readonly double _appliedScale;
    private readonly IntPtr _oldProc;
    private readonly Win32WndProc _newProc;   // 保活委托，防 GC 回收后回调崩进程
    private bool _disposed;

    private delegate IntPtr Win32WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private Win32PetHitTest(IntPtr hwnd, PetWindow window, ICharacterRenderer renderer)
    {
        _hwnd = hwnd;
        _window = window;
        _renderer = renderer;
        // 窗口 DIP 宽 = CharacterSize * settingsScale → 渲染器坐标系换算系数
        _appliedScale = window.Width / renderer.CharacterSize.Width;
        _newProc = WndProc;
        _oldProc = Win32WindowHelper.SetWindowLongPtr(hwnd, Win32WindowHelper.GWLP_WNDPROC,
            Marshal.GetFunctionPointerForDelegate(_newProc));
    }

    public static Win32PetHitTest? Attach(IntPtr hwnd, PetWindow window, ICharacterRenderer renderer)
    {
        try
        {
            var hook = new Win32PetHitTest(hwnd, window, renderer);
            return hook._oldProc == IntPtr.Zero ? null : hook;
        }
        catch { return null; }
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_NCHITTEST)
        {
            // lParam = 屏幕物理像素坐标（低 16 位 x、高 16 位 y，有符号）。
            var sx = (short)(lParam.ToInt64() & 0xFFFF);
            var sy = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
            var scaling = _window.Screens.ScreenFromWindow(_window)?.Scaling
                          ?? _window.RenderScaling;
            // 屏幕像素 → 客户端 DIP → 渲染器逻辑坐标
            var point = new Point((sx - _window.Position.X) / scaling,
                                  (sy - _window.Position.Y) / scaling);
            return _renderer.HitTest(point, _appliedScale)
                ? new IntPtr(HTCLIENT)
                : new IntPtr(HTTRANSPARENT);
        }
        return Win32WindowHelper.CallWindowProcW(_oldProc, hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Win32WindowHelper.SetWindowLongPtr(_hwnd, Win32WindowHelper.GWLP_WNDPROC, _oldProc);
    }
}
