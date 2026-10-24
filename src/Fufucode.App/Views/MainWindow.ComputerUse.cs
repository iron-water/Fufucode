using System;
using Avalonia.Threading;
using Fufucode.App.Views;
using Fufucode.Core.Plugins;

namespace Fufucode.App;

public partial class MainWindow
{
    /// <summary>
    /// 进程级虚拟光标注册表。插件通过 ServiceResolver 取得它；
    /// 这里持有同一实例并交给 Overlay 绘制，插件程序集不直接引用 App。
    /// </summary>
    private readonly ComputerUseVirtualCursorRegistry _computerCursors = new();
    private ComputerCursorOverlayWindow? _cursorOverlay;
    private bool _cursorOverlayUnavailable;

    internal IComputerUseCursorRegistry ComputerCursorsForTesting => _computerCursors;
    internal ComputerCursorOverlayWindow? CursorOverlayForTesting => _cursorOverlay;

    /// <summary>
    /// 光标层按需创建：没有光标时不常驻一个透明置顶窗；首个光标出现才显示，
    /// 全部移除或窗口关闭时销毁。无头自测没有真实屏幕/窗口句柄，只保留注册表。
    /// </summary>
    private void AttachComputerCursorOverlay()
    {
        _computerCursors.Changed += OnComputerCursorsChanged;
        OnComputerCursorsChanged();
    }

    private void OnComputerCursorsChanged()
    {
        if (Program.SelfTestMode) return;
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(OnComputerCursorsChanged);
            return;
        }

        var hasCursors = _computerCursors.Snapshot.Count > 0;
        if (hasCursors && _cursorOverlay is null && !_cursorOverlayUnavailable)
        {
            try
            {
                _cursorOverlay = new ComputerCursorOverlayWindow(_computerCursors);
                _cursorOverlay.Show();
            }
            catch (Exception)
            {
                // 透明置顶窗在无桌面/远程会话中可能不可创建：不影响电脑操控工具本身。
                _cursorOverlayUnavailable = true;
                _cursorOverlay = null;
            }
        }
        else if (!hasCursors && _cursorOverlay is not null)
        {
            try { _cursorOverlay.Close(); }
            catch { }
            _cursorOverlay = null;
        }
    }

    private void DisposeComputerCursorOverlay()
    {
        _computerCursors.Changed -= OnComputerCursorsChanged;
        _computerCursors.Clear();
        try { _cursorOverlay?.Close(); }
        catch { }
        _cursorOverlay = null;
    }
}
