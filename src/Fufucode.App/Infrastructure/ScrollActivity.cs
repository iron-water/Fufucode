using System;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Threading;
using Fufucode.App.Integration;
using Fufucode.App.Views;

namespace Fufucode.App.Infrastructure;

/// <summary>滚动时短暂显示滑块；透明的滚动槽始终保留，不挤压或覆盖正文。</summary>
public sealed class ScrollActivity : AvaloniaObject
{
    public static readonly AttachedProperty<bool> EnabledProperty =
        AvaloniaProperty.RegisterAttached<ScrollActivity, ScrollBar, bool>("Enabled");
    private static readonly AttachedProperty<State?> StateProperty =
        AvaloniaProperty.RegisterAttached<ScrollActivity, ScrollBar, State?>("State");

    static ScrollActivity()
    {
        EnabledProperty.Changed.AddClassHandler<ScrollBar>((bar, _) =>
        {
            bar.GetValue(StateProperty)?.Dispose();
            bar.SetValue(StateProperty, GetEnabled(bar) ? new State(bar) : null);
        });
    }

    public static bool GetEnabled(ScrollBar bar) => bar.GetValue(EnabledProperty);
    public static void SetEnabled(ScrollBar bar, bool value) => bar.SetValue(EnabledProperty, value);

    private sealed class State : IDisposable
    {
        private readonly ScrollBar _bar;
        private readonly DispatcherTimer _idle = new() { Interval = TimeSpan.FromMilliseconds(700) };
        private bool _dragging;

        public State(ScrollBar bar)
        {
            _bar = bar;
            bar.Opacity = 0;
            bar.Transitions = Motion.Reduced || MemoryProfile.DisableVisualEffects ? null : new Transitions
            {
                new DoubleTransition { Property = Visual.OpacityProperty, Duration = Motion.Base, Easing = Motion.CubicEase }
            };
            bar.PropertyChanged += OnPropertyChanged;
            bar.PointerEntered += OnPointerEntered;
            bar.PointerExited += OnPointerExited;
            bar.PointerCaptureLost += OnCaptureLost;
            bar.AddHandler(Thumb.DragStartedEvent, OnDragStarted);
            bar.AddHandler(Thumb.DragCompletedEvent, OnDragCompleted);
            bar.DetachedFromVisualTree += OnDetached;
            _idle.Tick += OnIdle;
        }

        private void Reveal()
        {
            if (!_bar.IsEffectivelyVisible) return;
            _bar.Opacity = 1;
            _idle.Stop();
            _idle.Start();
        }

        private void OnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == RangeBase.ValueProperty) Reveal();
            else if (e.Property == Visual.IsVisibleProperty && !_bar.IsVisible) Hide();
        }

        private void OnPointerEntered(object? sender, PointerEventArgs e) => Reveal();
        private void OnPointerExited(object? sender, PointerEventArgs e) { _idle.Stop(); _idle.Start(); }
        private void OnDragStarted(object? sender, VectorEventArgs e) { _dragging = true; Reveal(); }
        private void OnDragCompleted(object? sender, VectorEventArgs e) { _dragging = false; Reveal(); }
        private void OnIdle(object? sender, EventArgs e)
        {
            if (_dragging || _bar.IsPointerOver) return;
            Hide();
        }
        private void OnCaptureLost(object? sender, PointerCaptureLostEventArgs e) { _dragging = false; Reveal(); }
        private void OnDetached(object? sender, VisualTreeAttachmentEventArgs e) { _dragging = false; Hide(); }
        private void Hide() { _idle.Stop(); _bar.Opacity = 0; }

        public void Dispose()
        {
            _idle.Stop();
            _idle.Tick -= OnIdle;
            _bar.PropertyChanged -= OnPropertyChanged;
            _bar.PointerEntered -= OnPointerEntered;
            _bar.PointerExited -= OnPointerExited;
            _bar.PointerCaptureLost -= OnCaptureLost;
            _bar.RemoveHandler(Thumb.DragStartedEvent, OnDragStarted);
            _bar.RemoveHandler(Thumb.DragCompletedEvent, OnDragCompleted);
            _bar.DetachedFromVisualTree -= OnDetached;
            _bar.ClearValue(Visual.OpacityProperty);
            _bar.ClearValue(Animatable.TransitionsProperty);
        }
    }
}
