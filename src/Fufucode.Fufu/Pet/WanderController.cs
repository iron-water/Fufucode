using Avalonia;
using Avalonia.Threading;

namespace Fufucode.Fufu.Pet;

/// <summary>
/// 自由移动执行器：Walk 状态下朝随机目标点平移窗口，逐 tick 推进并做边界钳制。
/// 速度约 90 DIP/s；目标点落在当前屏幕工作区内。
/// </summary>
internal sealed class WanderController
{
    private const double SpeedDipPerSecond = 90;

    private readonly PetBehavior _behavior;
    private readonly Random _random = new();
    private DispatcherTimer? _ticker;
    private PetWindow? _window;
    private PixelPoint? _target;
    private DateTimeOffset _suspendUntil = DateTimeOffset.MinValue;

    public WanderController(PetBehavior behavior) => _behavior = behavior;

    public void Start(PetWindow window)
    {
        _window = window;
        _ticker ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _ticker.Tick += Tick;
        _ticker.Start();
    }

    public void Stop()
    {
        if (_ticker is null) return;
        _ticker.Tick -= Tick;
        _ticker.Stop();
        _ticker = null;
        _window = null;
        _target = null;
    }

    /// <summary>拖动等外部操作期间暂停自主移动。</summary>
    public void SuspendFor(TimeSpan duration) => _suspendUntil = DateTimeOffset.UtcNow + duration;

    private void Tick(object? sender, EventArgs e)
    {
        if (_window is null || !_window.IsVisible) return;
        if (DateTimeOffset.UtcNow < _suspendUntil) return;
        if (_behavior.State != PetState.Walk || !_behavior.FreeRoam || _behavior.MovementPaused)
        {
            _target = null;
            return;
        }

        var wa = _window.CurrentWorkingArea();
        var scaling = _window.Screens.ScreenFromPoint(_window.Position)?.Scaling
                      ?? _window.RenderScaling;
        var pixelW = (int)Math.Round(_window.Width * scaling);
        var pixelH = (int)Math.Round(_window.Height * scaling);
        var maxX = Math.Max(wa.X, wa.Right - pixelW);
        var maxY = Math.Max(wa.Y, wa.Bottom - pixelH);
        var pos = _window.Position;

        if (_target is null)
        {
            _target = new PixelPoint(
                (int)(wa.X + _random.NextDouble() * Math.Max(1, maxX - wa.X)),
                (int)(wa.Y + _random.NextDouble() * Math.Max(1, maxY - wa.Y)));
        }

        var dx = _target.Value.X - pos.X;
        var dy = _target.Value.Y - pos.Y;
        var distance = Math.Sqrt(dx * dx + dy * dy);
        var step = SpeedDipPerSecond * scaling * _ticker!.Interval.TotalSeconds;

        if (distance <= Math.Max(step, 2))
        {
            _window.Position = _target.Value;
            _target = null;         // 到达：由状态机决定下一段
            return;
        }

        var nx = pos.X + dx / distance * step;
        var ny = pos.Y + dy / distance * step;
        _window.Position = new PixelPoint((int)Math.Round(nx), (int)Math.Round(ny));
    }
}
