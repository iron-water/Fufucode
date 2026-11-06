using System;
using Avalonia.Controls;
using Avalonia.Layout;
using Fufucode.App.Integration;

namespace Fufucode.App.Views;

/// <summary>同一区域的布局刷新、反向开关和抽屉显隐共用一个动画，旧回调不能覆盖新目标。</summary>
internal sealed class DockRegionMotion(Control panel, Control splitter, Action<double, double> setExtent) : IDisposable
{
    private IDisposable? _animation;
    private bool _initialized;
    private bool _drawer;
    private bool _visible;
    private double _target;
    private double _panelExtent;
    private double _gapExtent;
    private double _opacity = 1;
    private int _generation;

    internal bool IsAnimating => _animation is not null;

    public void SetDocked(double extent, double actualExtent, double actualGap, bool visible, bool animate, bool snap)
    {
        var target = visible ? extent : 0;
        var sameTarget = _initialized && !_drawer && _visible == visible && Math.Abs(_target - target) < .01;
        if (sameTarget && IsAnimating && !snap && !Reduced) return;
        var continuing = IsAnimating && !_drawer;
        var from = continuing ? _panelExtent : actualExtent;
        var gap = continuing ? _gapExtent : actualGap;
        Cancel();
        _initialized = true;
        _drawer = false;
        _visible = visible;
        _target = target;
        panel.Opacity = 1;
        var targetGap = visible ? MainWindow.DockSplitterSize : 0;
        if (snap || Reduced || !(animate || continuing) ||
            (Math.Abs(from - target) < .01 && Math.Abs(gap - targetGap) < .01))
        {
            ApplyExtent(target, targetGap);
            panel.IsVisible = visible;
            splitter.IsVisible = visible;
            return;
        }

        panel.IsVisible = true;
        splitter.IsVisible = visible;
        ApplyExtent(from, gap);
        Run(progress => ApplyExtent(from + (target - from) * progress, gap + (targetGap - gap) * progress),
            () => { panel.IsVisible = visible; splitter.IsVisible = visible; });
    }

    public void SetDrawer(bool visible, bool animate, bool snap)
    {
        var sameTarget = _initialized && _drawer && _visible == visible;
        if (sameTarget && IsAnimating && !snap && !Reduced) return;
        var continuing = IsAnimating && _drawer;
        var from = continuing ? _opacity : panel.IsVisible ? panel.Opacity : 0;
        Cancel();
        _initialized = true;
        _drawer = true;
        _visible = visible;
        _target = 0;
        ApplyExtent(0, 0);
        splitter.IsVisible = false;
        var target = visible ? 1d : 0d;
        if (snap || Reduced || !(animate || continuing) || Math.Abs(from - target) < .01)
        {
            panel.IsVisible = visible;
            if (!visible) panel.ClearValue(Layoutable.MarginProperty);
            SetOpacity(1);
            return;
        }

        panel.IsVisible = true;
        SetOpacity(from);
        Run(progress => SetOpacity(from + (target - from) * progress), () =>
        {
            panel.IsVisible = visible;
            if (!visible) panel.ClearValue(Layoutable.MarginProperty);
            SetOpacity(1);
        });
    }

    private static bool Reduced => Motion.Reduced || MemoryProfile.DisableVisualEffects;

    private void ApplyExtent(double extent, double gap)
    {
        _panelExtent = Math.Max(0, extent);
        _gapExtent = Math.Max(0, gap);
        setExtent(_panelExtent, _gapExtent);
    }

    private void SetOpacity(double opacity) => panel.Opacity = _opacity = opacity;

    private void Run(Action<double> apply, Action completed)
    {
        var generation = _generation;
        var finished = false;
        var animation = Motion.AnimateValue(0, 1, Motion.StandardDuration,
            progress => { if (generation == _generation) apply(progress); }, () =>
            {
                if (generation != _generation) return;
                finished = true;
                _animation = null;
                completed();
            }, frameOwner: TopLevel.GetTopLevel(panel));
        if (!finished) _animation = animation;
        else animation.Dispose();
    }

    private void Cancel()
    {
        _generation++;
        _animation?.Dispose();
        _animation = null;
    }

    public void Dispose() => Cancel();
}
