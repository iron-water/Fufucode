using System;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Fufucode.App;

public partial class MainWindow
{
    // 图标提示：窗口内浮层，显示在目标控件正上方；离开后延迟 0.3 秒消失。
    // 不用 Popup —— Popup 是独立顶层，会按应用默认主题渲染成白底，
    // 且在打开时让指针离开目标，触发悬停高亮的“闪一下”。
    private readonly DispatcherTimer _tipTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly System.Collections.Generic.Dictionary<Control, (Func<string?> Text, Control? Anchor)> _tipTargets = new();
    private Control? _tipHover;

    /// <summary>登记某个控件的窗口内提示；由窗口级 PointerMoved 驱动，不依赖 IsPointerOver。</summary>
    internal void RegisterWindowTip(Control target, Func<string?> text, Control? anchor = null)
    {
        ToolTip.SetTip(target, null);
        _tipTargets[target] = (text, anchor);
    }

    /// <summary>指针移到某个控件上：切换提示（无提示则延迟隐藏）。</summary>
    private void OnTipHoverChanged(Control? target)
    {
        if (ReferenceEquals(target, _tipHover)) return;
        _tipHover = target;
        if (target is not null && _tipTargets.TryGetValue(target, out var entry))
            ShowWindowTip(entry.Anchor ?? target, entry.Text());
        else
            HideWindowTipSoon();
    }

    private void InitializeWindowTips() => _tipTimer.Tick += (_, _) => HideWindowTip();

    internal void ShowWindowTip(Control target, string? text)
    {
        if (TipLayer is null || TipBox is null || TipText is null) return;
        if (string.IsNullOrWhiteSpace(text)) { HideWindowTip(); return; }
        _tipTimer.Stop();
        TipText.Text = text;
        TipLayer.IsVisible = true;
        TipLayer.UpdateLayout();
        TipBox.Measure(Size.Infinity);
        var size = TipBox.DesiredSize;
        var anchor = target.TranslatePoint(new Point(target.Bounds.Width / 2, 0), TipLayer)
            ?? new Point(TipLayer.Bounds.Width / 2, 0);
        var layerWidth = TipLayer.Bounds.Width;
        var x = Math.Clamp(anchor.X - size.Width / 2, 4, Math.Max(4, layerWidth - size.Width - 4));
        var y = Math.Max(4, anchor.Y - size.Height - 6);
        TipBox.Margin = new Thickness(x, y, 0, 0);
    }

    internal void HideWindowTipSoon()
    {
        _tipTimer.Stop();
        _tipTimer.Start();
    }

    private void HideWindowTip()
    {
        _tipTimer.Stop();
        if (TipLayer is not null) TipLayer.IsVisible = false;
    }
}
