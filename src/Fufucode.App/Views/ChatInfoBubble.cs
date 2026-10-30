using System;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Fufucode.App.Views;

/// <summary>聊天页 Canvas 内直接绘制的信息气泡，仅用于状态图标和轮次预览。</summary>
internal sealed class ChatInfoBubble
{
    private readonly Control _target;
    private readonly Func<(string Title, string Detail)> _content;
    private readonly bool _turnPreview;
    private readonly HintDrawing _drawing = new();
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromMilliseconds(160) };
    private Canvas? _layer;
    private bool _pressed;

    internal ChatInfoBubble(Control target, Func<(string Title, string Detail)> content, bool turnPreview = false)
    {
        _target = target;
        _content = content;
        _turnPreview = turnPreview;
        AutomationProperties.SetAutomationId(_drawing, "chat-info-bubble");
        target.PointerEntered += (_, _) => { _pressed = false; UpdateVisibility(); };
        target.PointerExited += (_, _) => UpdateVisibility();
        target.GotFocus += (_, _) => { _pressed = false; UpdateVisibility(); };
        target.LostFocus += (_, _) => UpdateVisibility();
        target.AddHandler(InputElement.PointerPressedEvent, (_, _) => { _pressed = true; Close(); },
            Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        target.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.Key is Key.Escape or Key.Enter or Key.Space) { _pressed = true; Close(); }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        target.DetachedFromVisualTree += (_, _) => Close();
        target.PropertyChanged += (_, e) =>
        {
            if (e.Property == Visual.IsVisibleProperty || e.Property == InputElement.IsEnabledProperty) UpdateVisibility();
        };
        _refresh.Tick += (_, _) =>
        {
            if (!_target.IsEffectivelyVisible || TopLevel.GetTopLevel(_target) is not Window { IsActive: true }) Close();
            else Refresh();
        };
    }

    private void UpdateVisibility()
    {
        if (_pressed || !_target.IsEffectivelyVisible || !_target.IsEnabled ||
            (!_target.IsPointerOver && !_target.IsKeyboardFocusWithin)) { Close(); return; }
        _layer = _target.FindAncestorOfType<ChatPage>()?.FindControl<Canvas>("ChatHintLayer");
        if (_layer is null) return;
        // 每个聊天页一次只画当前目标的一个提示，不叠加窗口或描边。
        _layer.Children.Clear();
        _layer.Children.Add(_drawing);
        Refresh();
        _refresh.Start();
    }

    private void Refresh()
    {
        if (_layer is null || !_layer.Children.Contains(_drawing)) { Close(); return; }
        var (title, detail) = _content();
        if (string.IsNullOrWhiteSpace(detail) || detail == "—") detail = "默认";
        AutomationProperties.SetHelpText(_target, title + "：" + detail);
        var maxWidth = Math.Max(40, Math.Min(_turnPreview ? 240 : 280, _layer.Bounds.Width - 16));
        _drawing.Update(detail, new Typeface(TextElement.GetFontFamily(_target)),
            Brush("AppTextPrimary") ?? Brushes.White,
            Brush(_turnPreview ? "AppDialogBackground" : "AppContentBackground") ?? new SolidColorBrush(Color.Parse("#202020")), maxWidth, _turnPreview);
        _drawing.Measure(new Size(maxWidth, 70));
        var size = _drawing.DesiredSize;
        var point = _target.TranslatePoint(default, _layer) ?? default;
        var x = _turnPreview ? point.X + _target.Bounds.Width + 5 : point.X + (_target.Bounds.Width - size.Width) / 2;
        var y = _turnPreview ? point.Y + (_target.Bounds.Height - size.Height) / 2 : point.Y - size.Height - 3;
        if (!_turnPreview && y < 4) y = point.Y + _target.Bounds.Height + 3;
        Canvas.SetLeft(_drawing, Math.Clamp(x, 4, Math.Max(4, _layer.Bounds.Width - size.Width - 4)));
        Canvas.SetTop(_drawing, Math.Clamp(y, 4, Math.Max(4, _layer.Bounds.Height - size.Height - 4)));
    }

    private IBrush? Brush(string key) => _target.TryFindResource(key, _target.ActualThemeVariant, out var value) ? value as IBrush : null;
    private void Close()
    {
        _refresh.Stop();
        _layer?.Children.Remove(_drawing);
        _layer = null;
    }

    private sealed class HintDrawing : Control
    {
        private TextLayout? _text;
        private string? _value;
        private double _maxWidth;
        private IBrush? _background;
        private IBrush? _foreground;
        private bool _turn;

        internal void Update(string value, Typeface typeface, IBrush foreground, IBrush background, double maxWidth, bool turn)
        {
            if (_value == value && _maxWidth == maxWidth && _turn == turn &&
                ReferenceEquals(_foreground, foreground) && ReferenceEquals(_background, background)) return;
            _value = value;
            _maxWidth = maxWidth;
            _turn = turn;
            _background = background;
            _foreground = foreground;
            _text?.Dispose();
            _text = new TextLayout(value, typeface, 12, foreground, textWrapping: turn ? TextWrapping.Wrap : TextWrapping.NoWrap,
                textTrimming: TextTrimming.CharacterEllipsis, maxWidth: Math.Max(20, maxWidth - 16),
                maxLines: turn ? 3 : 1, lineHeight: 16);
            InvalidateMeasure();
            InvalidateVisual();
        }

        protected override Size MeasureOverride(Size availableSize) => _text is null ? default :
            new Size(Math.Ceiling(_text.Width) + 16, Math.Ceiling(_text.Height) + 10);

        public override void Render(DrawingContext context)
        {
            if (_text is null) return;
            context.DrawRectangle(_background, null, new Rect(Bounds.Size), 4, 4);
            _text.Draw(context, new Point(8, 5));
        }
    }
}
