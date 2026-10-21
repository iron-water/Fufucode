using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform;
using Fufucode.Fufu.Integration;

namespace Fufucode.Fufu.Pet;

/// <summary>
/// 桌宠悬浮窗：透明、无边框、置顶、不进任务栏、不抢焦点（WS_EX_NOACTIVATE + TOOLWINDOW）。
/// 窗口本体即角色包围盒；透明区命中经 Win32 WM_NCHITTEST 交给渲染器 HitTest 决定穿透。
/// </summary>
internal sealed class PetWindow : Window
{
    private readonly ICharacterRenderer _renderer;
    private readonly PetBehavior _behavior;
    private readonly WanderController _wander;
    private readonly bool _clickThrough;
    private Win32PetHitTest? _hitTest;

    public PetWindow(ICharacterRenderer renderer, PetBehavior behavior, WanderController wander,
        double scale, bool clickThrough)
    {
        _renderer = renderer;
        _behavior = behavior;
        _wander = wander;
        _clickThrough = clickThrough;

        var size = renderer.CharacterSize * scale;
        Width = size.Width;
        Height = size.Height;
        MinWidth = MaxWidth = Width;
        MinHeight = MaxHeight = Height;

        WindowDecorations = global::Avalonia.Controls.WindowDecorations.None;
        Background = Avalonia.Media.Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        Topmost = true;
        ShowInTaskbar = false;
        CanResize = false;
        SizeToContent = SizeToContent.Manual;
        WindowStartupLocation = WindowStartupLocation.Manual;

        Content = renderer.View;
        renderer.View.Width = Width;
        renderer.View.Height = Height;

        behavior.StateChanged += state => renderer.SetState(state);
        renderer.SetState(behavior.State);

        // 拖动：左键按住角色 → BeginMoveDrag；单击 → Interact；右键 → 上下文菜单。
        renderer.View.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(renderer.View).Properties.IsLeftButtonPressed)
            {
                _wander.SuspendFor(TimeSpan.FromSeconds(3));   // 拖动中暂停自由移动
                BeginMoveDrag(e);
                ClampToScreen();
            }
        };
        renderer.View.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton == MouseButton.Left) _behavior.Interact();
        };
    }

    /// <summary>当前窗口所在的屏幕工作区（物理像素，与 <see cref="Window.Position"/> 同坐标系）。</summary>
    public PixelRect CurrentWorkingArea()
    {
        var screen = Screens.ScreenFromPoint(Position) ?? Screens.Primary;
        return screen?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);
    }

    /// <summary>把窗口位置钳回所在屏幕工作区。</summary>
    public void ClampToScreen()
    {
        var wa = CurrentWorkingArea();
        var scaling = Screens.ScreenFromPoint(Position)?.Scaling ?? RenderScaling;
        var pixelW = (int)Math.Round(Width * scaling);
        var pixelH = (int)Math.Round(Height * scaling);
        var x = Math.Clamp(Position.X, wa.X, Math.Max(wa.X, wa.Right - pixelW));
        var y = Math.Clamp(Position.Y, wa.Y, Math.Max(wa.Y, wa.Bottom - pixelH));
        if (x != Position.X || y != Position.Y)
            Position = new PixelPoint(x, y);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        // 不抢焦点/不进 Alt-Tab；透明区点击穿透（除角色实体外）。
        var handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle != IntPtr.Zero)
        {
            Win32WindowHelper.ApplyNoActivateStyles(handle);
            if (_clickThrough)
                _hitTest = Win32PetHitTest.Attach(handle, this, _renderer);
        }
        _wander.Start(this);
    }

    protected override void OnClosed(EventArgs e)
    {
        _hitTest?.Dispose();
        _wander.Stop();
        base.OnClosed(e);
    }
}
