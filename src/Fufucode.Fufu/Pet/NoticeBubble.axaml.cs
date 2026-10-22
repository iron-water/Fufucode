using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Fufucode.Fufu.Pet;

/// <summary>
/// FUFU 气泡通知（FUFU-05 基础通知通道）：桌宠上方的轻提示，约 4 秒自动消失。
/// 单实例复用——新通知替换旧文案并重置计时，不弹一串窗。
/// </summary>
public sealed partial class NoticeBubble : Window
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(4);
    private DispatcherTimer? _timer;

    public NoticeBubble()
    {
        InitializeComponent();
    }

    /// <summary>以 <paramref name="anchor"/>（桌宠位置）为锚显示一条通知。</summary>
    public void ShowNotice(string text, PixelPoint anchor)
    {
        Text.Text = text;
        // 气泡放在桌宠上方；贴不到（顶边）时放到桌宠下方。
        Show();
        var y = anchor.Y - (int)Bounds.Height - 6;
        if (y < 0) y = anchor.Y + 156;
        Position = new PixelPoint(Math.Max(0, anchor.X - (int)Bounds.Width / 2 + 70), y);

        _timer?.Stop();
        _timer = new DispatcherTimer { Interval = Lifetime };
        _timer.Tick += (_, _) => { Hide(); _timer?.Stop(); };
        _timer.Start();
    }
}
