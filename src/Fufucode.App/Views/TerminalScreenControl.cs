using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Fufucode.App.Infrastructure;
using Fufucode.App.ViewModels;

namespace Fufucode.App.Views;

/// <summary>Native fixed-cell terminal surface. It renders the PTY's interpreted screen and bounded scrollback.</summary>
public sealed class TerminalScreenControl : Control
{
    public const double CellWidth = 7.8;
    public const double CellHeight = 18;
    public static readonly StyledProperty<TerminalTabViewModel?> SessionProperty = AvaloniaProperty.Register<TerminalScreenControl, TerminalTabViewModel?>(nameof(Session));
    private readonly Dictionary<int, SolidColorBrush> _brushes = new();
    private TerminalSnapshot? _snapshot;
    private TerminalTabViewModel? _subscribed;
    public TerminalTabViewModel? Session { get => GetValue(SessionProperty); set => SetValue(SessionProperty, value); }

    public TerminalScreenControl()
    {
        Focusable = true; ClipToBounds = true;
        TextInput += (_, e) => { if (Session?.IsRunning == true && !string.IsNullOrEmpty(e.Text)) { Session.SendRaw(e.Text); e.Handled = true; } };
        PointerPressed += (_, _) => Focus();
        KeyDown += OnTerminalKeyDown;
    }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != SessionProperty) return;
        if (_subscribed is not null) _subscribed.Buffer.Changed -= OnBufferChanged;
        _subscribed = Session;
        if (_subscribed is not null) _subscribed.Buffer.Changed += OnBufferChanged;
        OnBufferChanged();
    }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_subscribed is not null) _subscribed.Buffer.Changed -= OnBufferChanged;
    }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_subscribed is not null) { _subscribed.Buffer.Changed -= OnBufferChanged; _subscribed.Buffer.Changed += OnBufferChanged; }
        OnBufferChanged();
    }
    private void OnBufferChanged() { _snapshot = Session?.Buffer.Snapshot(); InvalidateMeasure(); InvalidateVisual(); }
    protected override Size MeasureOverride(Size availableSize) => new(double.IsFinite(availableSize.Width) ? availableSize.Width : (Session?.Buffer.Columns ?? 100) * CellWidth + 20, Math.Max(54, (_snapshot?.Lines.Length ?? 3) * CellHeight + 16));
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(Brush(TerminalBuffer.DefaultBackground), new Rect(Bounds.Size));
        if (_snapshot is null) return;
        var scroller = this.FindAncestorOfType<ScrollViewer>();
        var firstRow = scroller is null ? 0 : Math.Max(0, (int)((scroller.Offset.Y - 8) / CellHeight));
        var lastRow = scroller is null || scroller.Viewport.Height <= 0 ? _snapshot.Lines.Length : Math.Min(_snapshot.Lines.Length, firstRow + (int)Math.Ceiling(scroller.Viewport.Height / CellHeight) + 2);
        for (var row = firstRow; row < lastRow; row++)
        {
            var line = _snapshot.Lines[row];
            for (var col = 0; col < line.Length; col++)
            {
                var cell = line[col];
                var origin = new Point(10 + col * CellWidth, 8 + row * CellHeight);
                if (cell.Background != TerminalBuffer.DefaultBackground) context.FillRectangle(Brush(cell.Background), new Rect(origin, new Size(CellWidth, CellHeight)));
                if (string.IsNullOrEmpty(cell.Text) || cell.Text == " ") continue;
                var typeface = new Typeface(new FontFamily("Consolas, Cascadia Mono, Microsoft YaHei"), FontStyle.Normal, cell.Bold ? FontWeight.Bold : FontWeight.Normal);
                var text = new FormattedText(cell.Text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 13, Brush(cell.Foreground));
                context.DrawText(text, origin);
            }
        }
        if (_snapshot.CursorVisible && Session?.IsRunning == true)
        {
            var origin = new Point(10 + Math.Min(_snapshot.CursorColumn, (Session.Buffer.Columns - 1)) * CellWidth, 8 + _snapshot.CursorLine * CellHeight + CellHeight - 2);
            context.FillRectangle(Brush(TerminalBuffer.DefaultForeground), new Rect(origin, new Size(CellWidth, 2)));
        }
    }
    private SolidColorBrush Brush(int color)
    {
        if (!_brushes.TryGetValue(color, out var brush))
        {
            if (_brushes.Count >= 512) _brushes.Clear();
            brush = new SolidColorBrush(Color.FromRgb((byte)(color >> 16), (byte)(color >> 8), (byte)color)); _brushes[color] = brush;
        }
        return brush;
    }
    private void OnTerminalKeyDown(object? sender, KeyEventArgs e)
    {
        if (Session?.IsRunning != true) return;
        string? text = null;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key >= Key.A && e.Key <= Key.Z) text = ((char)(e.Key - Key.A + 1)).ToString();
        else text = e.Key switch
        {
            Key.Enter => "\r", Key.Back => "\u007f", Key.Tab => "\t", Key.Escape => "\u001b",
            Key.Up => "\u001b[A", Key.Down => "\u001b[B", Key.Right => "\u001b[C", Key.Left => "\u001b[D",
            Key.Home => "\u001b[H", Key.End => "\u001b[F", Key.Delete => "\u001b[3~", Key.PageUp => "\u001b[5~", Key.PageDown => "\u001b[6~", _ => null,
        };
        if (text is null) return;
        Session.SendRaw(text); e.Handled = true;
    }
}
