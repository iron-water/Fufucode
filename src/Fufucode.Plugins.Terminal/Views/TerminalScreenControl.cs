using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.VisualTree;
using Fufucode.Plugins.Terminal.Features.TerminalExperience;
using Fufucode.Plugins.Terminal.Infrastructure;
using Fufucode.Plugins.Terminal.ViewModels;

namespace Fufucode.Plugins.Terminal.Views;

/// <summary>Native fixed-cell terminal surface. It renders the PTY's interpreted screen and bounded scrollback.</summary>
public sealed class TerminalScreenControl : Control
{
    internal Func<string, KeyEventArgs, bool>? ShortcutMatches { get; set; }
    public const double CellWidth = 7.8;
    public const double CellHeight = 18;
    public const double OriginX = 10;
    public const double OriginY = 8;

    // The terminal surface is a fixed dark palette independent of the app theme (see WorkbenchStyles),
    // so the interaction highlights are tuned against that surface rather than against theme resources.
    private static readonly IBrush SelectionBrush = new SolidColorBrush(Color.FromArgb(0x5A, 0x61, 0xAF, 0xEF));
    private static readonly IBrush SearchBrush = new SolidColorBrush(Color.FromArgb(0x8C, 0xE5, 0xC0, 0x7B));

    public static readonly StyledProperty<TerminalTabViewModel?> SessionProperty = AvaloniaProperty.Register<TerminalScreenControl, TerminalTabViewModel?>(nameof(Session));
    public static readonly StyledProperty<IBrush?> SurfaceBrushProperty = AvaloniaProperty.Register<TerminalScreenControl, IBrush?>(nameof(SurfaceBrush));
    public static readonly StyledProperty<IBrush?> TextBrushProperty = AvaloniaProperty.Register<TerminalScreenControl, IBrush?>(nameof(TextBrush));
    public static readonly StyledProperty<IBrush?> SelectionFillProperty = AvaloniaProperty.Register<TerminalScreenControl, IBrush?>(nameof(SelectionFill));
    public IBrush? SurfaceBrush { get => GetValue(SurfaceBrushProperty); set => SetValue(SurfaceBrushProperty, value); }
    public IBrush? TextBrush { get => GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }
    public IBrush? SelectionFill { get => GetValue(SelectionFillProperty); set => SetValue(SelectionFillProperty, value); }
    private readonly Dictionary<int, SolidColorBrush> _brushes = new();
    private TerminalSnapshot? _snapshot;
    private TerminalTabViewModel? _subscribed;

    public TerminalTabViewModel? Session { get => GetValue(SessionProperty); set => SetValue(SessionProperty, value); }

    /// <summary>Raised after a pointer selection gesture or a cleared selection.</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>Raised when Ctrl+F asks for the output find bar.</summary>
    public event EventHandler? FindRequested;

    /// <summary>Raised on Escape while the find bar is active.</summary>
    public event EventHandler? FindDismissRequested;

    /// <summary>Raised after clipboard text was merged into the current tab's command draft.</summary>
    public event EventHandler? PasteCompleted;

    /// <summary>Set by the dock so Escape only stops reaching the shell while the find bar is open.</summary>
    public bool FindActive { get; set; }

    /// <summary>Test seam: replaces the real clipboard writer.</summary>
    internal Func<string, Task>? ClipboardWriterForTesting { get; set; }

    /// <summary>Test seam: replaces the real clipboard reader.</summary>
    internal Func<Task<string?>>? ClipboardReaderForTesting { get; set; }

    /// <summary>Snapshot currently rendered; also the anchor identity for selection and search.</summary>
    public TerminalSnapshot? CurrentSnapshot => _snapshot;

    /// <summary>True when the current tab holds copyable selected output.</summary>
    public bool HasSelection => Session?.OutputSelection.HasText == true;

    static TerminalScreenControl()
    {
        AffectsRender<TerminalScreenControl>(SurfaceBrushProperty, TextBrushProperty, SelectionFillProperty);
    }

    public TerminalScreenControl()
    {
        Focusable = true; ClipToBounds = true;
        this.Bind(SurfaceBrushProperty, new DynamicResourceExtension("AppTerminalBackground"));
        this.Bind(TextBrushProperty, new DynamicResourceExtension("AppTerminalForeground"));
        this.Bind(SelectionFillProperty, new DynamicResourceExtension("AppTerminalSelection"));
        TextInput += (_, e) => { if (Session?.IsRunning == true && !string.IsNullOrEmpty(e.Text)) { Session.SendRaw(e.Text); e.Handled = true; } };
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerCaptureLost += (_, _) => SelectionChanged?.Invoke(this, EventArgs.Empty);
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
    private void OnBufferChanged() { _snapshot = Session?.Snapshot; InvalidateMeasure(); InvalidateVisual(); }
    protected override Size MeasureOverride(Size availableSize) => new(double.IsFinite(availableSize.Width) ? availableSize.Width : (Session?.Buffer.Columns ?? 100) * CellWidth + 20, Math.Max(54, (_snapshot?.Lines.Length ?? 3) * CellHeight + 16));
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(SurfaceBrush ?? Brush(TerminalBuffer.DefaultBackground), new Rect(Bounds.Size));
        if (_snapshot is null) return;
        var scroller = this.FindAncestorOfType<ScrollViewer>();
        var firstRow = scroller is null ? 0 : Math.Max(0, (int)((scroller.Offset.Y - 8) / CellHeight));
        var lastRow = scroller is null || scroller.Viewport.Height <= 0 ? _snapshot.Lines.Length : Math.Min(_snapshot.Lines.Length, firstRow + (int)Math.Ceiling(scroller.Viewport.Height / CellHeight) + 2);
        var selection = Session?.OutputSelection;
        var activeSelection = selection is { } candidate && candidate.IsOnSnapshot(_snapshot) && candidate.HasRange ? candidate : null;
        // Resolve the grapheme-expanded range once per frame; every row highlight reuses it.
        var resolvedSelection = activeSelection?.ResolvedRange;
        var search = Session?.Search;
        var searchMatch = search is not null && search.IsCurrent(_snapshot) ? search.Current : null;
        for (var row = firstRow; row < lastRow; row++)
        {
            var line = _snapshot.Lines[row];
            for (var col = 0; col < line.Length; col++)
            {
                var cell = line[col];
                if (cell.Background != TerminalBuffer.DefaultBackground)
                    context.FillRectangle(Brush(cell.Background), new Rect(OriginX + col * CellWidth, OriginY + row * CellHeight, CellWidth, CellHeight));
            }
            if (searchMatch is { } hit && hit.Row == row)
                context.FillRectangle(SearchBrush, CellRect(row, hit.StartColumn, hit.EndColumn));
            if (resolvedSelection is { } range && TryRowSegment(range, row, line.Length, out var from, out var to))
                context.FillRectangle(SelectionFill ?? SelectionBrush, CellRect(row, from, to));
            for (var col = 0; col < line.Length; col++)
            {
                var cell = line[col];
                if (string.IsNullOrEmpty(cell.Text) || cell.Text == " ") continue;
                var origin = new Point(OriginX + col * CellWidth, OriginY + row * CellHeight);
                var typeface = new Typeface(new FontFamily("Consolas, Cascadia Mono, Microsoft YaHei"), FontStyle.Normal, cell.Bold ? FontWeight.Bold : FontWeight.Normal);
                var foreground = cell.Foreground == TerminalBuffer.DefaultForeground ? TextBrush ?? Brush(cell.Foreground) : Brush(cell.Foreground);
                var text = new FormattedText(cell.Text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 13, foreground);
                context.DrawText(text, origin);
            }
        }
        if (_snapshot.CursorVisible && Session?.IsRunning == true)
        {
            var origin = new Point(OriginX + Math.Min(_snapshot.CursorColumn, (Session.Buffer.Columns - 1)) * CellWidth, OriginY + _snapshot.CursorLine * CellHeight + CellHeight - 2);
            context.FillRectangle(TextBrush ?? Brush(TerminalBuffer.DefaultForeground), new Rect(origin, new Size(CellWidth, 2)));
        }
    }

    private static Rect CellRect(int row, int fromColumn, int toColumn) =>
        new(OriginX + fromColumn * CellWidth, OriginY + row * CellHeight, Math.Max(CellWidth, (toColumn - fromColumn + 1) * CellWidth), CellHeight);

    private static bool TryRowSegment((TerminalCellPosition Start, TerminalCellPosition End) range, int row, int lineLength, out int from, out int to)
    {
        var (start, end) = range;
        from = 0; to = 0;
        if (row < start.Row || row > end.Row) return false;
        var last = Math.Max(0, lineLength - 1);
        from = Math.Clamp(row == start.Row ? start.Column : 0, 0, last);
        to = Math.Clamp(row == end.Row ? end.Column : last, from, last);
        return true;
    }

    /// <summary>Copy the frozen selection text. Works after the shell exits; a no-op without a selection.</summary>
    public Task CopySelectionAsync()
    {
        var text = Session?.OutputSelection.Text ?? string.Empty;
        return text.Length == 0 ? Task.CompletedTask : WriteClipboardAsync(text);
    }

    /// <summary>Read the clipboard and merge it into the current tab's command draft (never sent to the shell).</summary>
    public async Task PasteToDraftAsync()
    {
        // Bind the target tab before awaiting: a pending clipboard read must never be retargeted to
        // whichever tab happens to be selected by the time the read completes.
        if (Session is not { } tab) return;
        var text = await ReadClipboardAsync();
        if (string.IsNullOrEmpty(text) || tab.IsDisposed) return;
        tab.PasteToDraft(text);
        // Only hand focus to the command box when the initiating tab is still the visible one.
        if (ReferenceEquals(Session, tab)) PasteCompleted?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Focus this surface and write one text input in the same step. The dock routes characters here
    /// when a keystroke arrives while the output surface is not yet the focused element, so a focus
    /// change that lands one input event late can never drop the first character of a fast burst.
    /// Returns false (and writes nothing) when there is no running session or the text is empty.
    /// </summary>
    internal bool CaptureInput(string? text)
    {
        if (Session is not { IsRunning: true } tab) return false;
        Focus();
        if (string.IsNullOrEmpty(text)) return false;
        tab.SendRaw(text);
        return true;
    }

    /// <summary>Establish keyboard focus for this surface without writing anything.</summary>
    internal void FocusForInput() => Focus();

    private Task WriteClipboardAsync(string text)
    {
        if (ClipboardWriterForTesting is { } writer) return writer(text);
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        return clipboard is null ? Task.CompletedTask : ClipboardExtensions.SetTextAsync(clipboard, text);
    }

    private async Task<string?> ReadClipboardAsync()
    {
        if (ClipboardReaderForTesting is { } reader) return await reader();
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return null;
        try { return await clipboard.TryGetTextAsync(); }
        catch (Exception) { return null; }
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        Focus();
        if (Session is not { } tab || _snapshot is null) return;
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed) return;
        if (CellAt(point.Position) is not { } cell) return;
        tab.OutputSelection.Begin(_snapshot, cell);
        e.Pointer.Capture(this);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        // Capture is only taken by a left-button press, so a captured move is always a drag.
        if (!ReferenceEquals(e.Pointer.Captured, this) || Session is not { } tab || _snapshot is null) return;
        if (CellAt(e.GetCurrentPoint(this).Position) is not { } cell) return;
        if (!tab.OutputSelection.Extend(_snapshot, cell)) return;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!ReferenceEquals(e.Pointer.Captured, this)) return;
        if (Session is { } tab && _snapshot is not null && CellAt(e.GetCurrentPoint(this).Position) is { } cell)
            tab.OutputSelection.Extend(_snapshot, cell);
        e.Pointer.Capture(null);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    private TerminalCellPosition? CellAt(Point position)
    {
        if (_snapshot is null || _snapshot.Lines.Length == 0) return null;
        var row = Math.Clamp((int)Math.Floor((position.Y - OriginY) / CellHeight), 0, _snapshot.Lines.Length - 1);
        var column = Math.Clamp((int)Math.Floor((position.X - OriginX) / CellWidth), 0, Math.Max(0, _snapshot.Lines[row].Length - 1));
        return new TerminalCellPosition(row, column);
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
        var tab = Session;
        if (tab is null) return;
        var control = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        // Copy / paste / find stay available after the shell exits so old output is still readable.
        if (Matches("terminal.copy", control && shift && e.Key == Key.C)) { e.Handled = true; _ = CopySelectionAsync(); return; }
        if (Matches("terminal.paste", control && e.Key == Key.V || shift && e.Key == Key.Insert)) { e.Handled = true; _ = PasteToDraftAsync(); return; }
        if (Matches("terminal.find", control && e.Key == Key.F)) { e.Handled = true; FindRequested?.Invoke(this, EventArgs.Empty); return; }
        if (e.Key == Key.Escape && FindActive) { e.Handled = true; FindDismissRequested?.Invoke(this, EventArgs.Empty); return; }
        // With a selection Ctrl+C copies; without one it must still interrupt the running shell command.
        if (Matches("terminal.copy-or-interrupt", e.KeyModifiers == KeyModifiers.Control && e.Key == Key.C))
        { e.Handled = true; if (HasSelection) _ = CopySelectionAsync(); else tab.Interrupt(); return; }
        if (tab.IsRunning != true) return;
        string? text;
        if (control && e.Key >= Key.A && e.Key <= Key.Z) text = ((char)(e.Key - Key.A + 1)).ToString();
        else text = e.Key switch
        {
            Key.Enter => "\r", Key.Back => "\u007f", Key.Tab => "\t", Key.Escape => "\u001b",
            Key.Up => "\u001b[A", Key.Down => "\u001b[B", Key.Right => "\u001b[C", Key.Left => "\u001b[D",
            Key.Home => "\u001b[H", Key.End => "\u001b[F", Key.Delete => "\u001b[3~", Key.PageUp => "\u001b[5~", Key.PageDown => "\u001b[6~", _ => null,
        };
        if (text is null) return;
        tab.SendRaw(text); e.Handled = true;
        bool Matches(string id, bool standaloneDefault) => ShortcutMatches?.Invoke(id, e) ?? standaloneDefault;
    }
}
