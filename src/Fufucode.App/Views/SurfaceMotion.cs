using System;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Fufucode.App.Integration;

namespace Fufucode.App.Views;

/// <summary>浮层用同一段可中断的淡入和轻移；关闭再打开时沿当前进度继续。</summary>
internal static class SurfaceMotion
{
    private static readonly ConditionalWeakTable<Control, State> States = new();

    public static void Show(Control surface, double offsetY = -6, bool scale = false) =>
        States.GetValue(surface, control => new State(control)).Show(offsetY, scale);

    public static void Hide(Control surface, Action completed) =>
        States.GetValue(surface, control => new State(control)).Hide(completed);

    public static void Reset(Control surface)
    {
        if (States.TryGetValue(surface, out var state)) state.Reset();
    }

    private sealed class State
    {
        private readonly Control _surface;
        private readonly TranslateTransform _offset = new();
        private readonly ScaleTransform _scale = new(1, 1);
        private IDisposable? _animation;
        private double _progress = 1;
        private double _offsetY;
        private bool _useScale;
        private int _generation;

        public State(Control surface)
        {
            _surface = surface;
            var transforms = new TransformGroup();
            if (surface.RenderTransform is Transform existing) transforms.Children.Add(existing);
            transforms.Children.Add(_scale);
            transforms.Children.Add(_offset);
            surface.RenderTransform = transforms;
            surface.RenderTransformOrigin = new RelativePoint(.5, 0, RelativeUnit.Relative);
            surface.DetachedFromVisualTree += (_, _) => Reset();
        }

        public void Show(double offsetY, bool scale)
        {
            if (_animation is null) _progress = 0;
            _offsetY = offsetY;
            _useScale = scale;
            _surface.IsVisible = true;
            MoveTo(1, null);
        }

        public void Hide(Action completed) => MoveTo(0, completed);

        private void MoveTo(double target, Action? completed)
        {
            _animation?.Dispose(); _animation = null;
            var generation = ++_generation;
            if (Motion.Reduced || MemoryProfile.DisableVisualEffects)
            {
                Apply(target);
                completed?.Invoke();
                return;
            }
            Apply(_progress);
            _animation = Motion.AnimateValue(_progress, target,
                target > 0 ? Motion.Slow : Motion.Base, Apply, () =>
                {
                    if (generation != _generation) return;
                    _animation = null;
                    completed?.Invoke();
                });
        }

        private void Apply(double value)
        {
            _progress = value;
            _surface.Opacity = value;
            _offset.Y = _offsetY * (1 - value);
            _scale.ScaleX = _scale.ScaleY = _useScale ? .985 + .015 * value : 1;
        }

        public void Reset()
        {
            _generation++;
            _animation?.Dispose(); _animation = null;
            Apply(1);
        }
    }
}
