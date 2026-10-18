using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fufucode.Plugins.Terminal.Features.TerminalExperience;
using Fufucode.Plugins.Terminal.Infrastructure;
using Fufucode.Plugins.Terminal.ViewModels;

namespace Fufucode.Plugins.Terminal.Views;

/// <summary>Native fixed-cell terminal surface. It renders the PTY's interpreted screen and bounded scrollback.</summary>
public sealed class TerminalScreenControl : Control
{
    internal Func<string, KeyEventArgs, bool>? ShortcutMatches { get; set; }
    public const double OriginX = 10;
    public const double OriginY = 8;

    /// <summary>Glyph size in DIP; the fixed cell advances scale with it (monospace metrics).</summary>
    public double FontSizeDip
    {
        get => _fontSizeDip;
        set
        {
            var next = Math.Clamp(value, 9, 28);
            if (Math.Abs(next - _fontSizeDip) < 0.01) return;
            _fontSizeDip = next;
            InvalidateMeasure(); InvalidateVisual();
        }
    }
    private double _fontSizeDip = 13;
    /// <summary>Cell advance for the current font size (Consolas is 0.6 em wide).</summary>
    public double CellWidth => Math.Round(_fontSizeDip * 0.6, 2);
    public double CellHeight => Math.Round(_fontSizeDip * 1.385, 2);

    /// <summary>Cursor blink; the style comes from the shell's DECSCUSR, this only stops the animation.</summary>
    public bool BlinkCursor { get; set; } = true;

    // The terminal surface is a fixed dark palette independent of the app theme (see WorkbenchStyles),
    // so the interaction highlights are tuned against that surface rather than against theme resources.
    private static readonly IBrush SelectionBrush = new SolidColorBrush(Color.FromArgb(0x5A, 0x61, 0xAF, 0xEF));
    private static readonly IBrush SearchBrush = new SolidColorBrush(Color.FromArgb(0x8C, 0xE5, 0xC0, 0x7B));
    private static readonly FontFamily MonoFont = new("Consolas, Cascadia Mono, Microsoft YaHei");
    private static readonly IBrush BellFlashBrush = new SolidColorBrush(Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF));
    private DateTime _bellFlashUntil;

    /// <summary>Visual bell: a brief surface flash, used when the shell sends BEL and the setting is “visual”.</summary>
    public void FlashBell()
    {
        _bellFlashUntil = DateTime.UtcNow.AddMilliseconds(140);
        InvalidateVisual();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(170) };
        timer.Tick += (_, _) => { timer.Stop(); InvalidateVisual(); };
        timer.Start();
    }

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

    /// <summary>Raised on Ctrl+Click over an OSC 8 hyperlink with its URI.</summary>
    public event EventHandler<string>? LinkActivated;

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

    protected override AutomationPeer OnCreateAutomationPeer() => new TerminalAutomationPeer(this);

    private sealed class TerminalAutomationPeer(TerminalScreenControl owner) : ControlAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => "Terminal";
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Pane;
        protected override string GetNameCore() =>
            owner.Session?.OutputSelection.Text is { Length: > 0 } selected ? selected : "终端输出";
        protected override string GetHelpTextCore() =>
            "终端输出面：拖拽选择文本，Ctrl+Shift+C 复制，Ctrl+F 查找。";
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
        PointerWheelChanged += OnPointerWheelChanged;
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
        if (_bellFlashUntil > DateTime.UtcNow)
            context.FillRectangle(BellFlashBrush, new Rect(Bounds.Size));
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
        var hoveredLink = Session?.HoveredLink;
        for (var row = firstRow; row < lastRow; row++)
        {
            var line = _snapshot.Lines[row];
            for (var col = 0; col < line.Length; col++)
            {
                var cell = line[col];
                var background = ResolveBackground(cell);
                if (background != TerminalBuffer.DefaultBackground)
                    context.FillRectangle(Brush(background), new Rect(OriginX + col * CellWidth, OriginY + row * CellHeight, CellWidth, CellHeight));
            }
            if (searchMatch is { } hit && hit.Row == row)
                context.FillRectangle(SearchBrush, CellRect(row, hit.StartColumn, hit.EndColumn));
            if (resolvedSelection is { } range && TryRowSegment(range, row, line.Length, out var from, out var to))
                context.FillRectangle(SelectionFill ?? SelectionBrush, CellRect(row, from, to));
            // Coalesce neighbouring cells with the same style into one text run. This keeps the
            // per-frame DrawText count proportional to colour changes instead of cell count.
            for (var col = 0; col < line.Length;)
            {
                var cell = line[col];
                if (string.IsNullOrEmpty(cell.Text) || cell.Text == " " || cell.Conceal) { col++; continue; }
                var origin = new Point(OriginX + col * CellWidth, OriginY + row * CellHeight);
                if (hoveredLink is not null && cell.Link == hoveredLink)
                {
                    // Hovered hyperlink: dim cell background + underline makes the target obvious without a cursor change.
                    context.FillRectangle(SelectionFill is { } fill ? fill : SelectionBrush,
                        new Rect(origin.X, origin.Y, Math.Max(CellWidth, cell.Text.Length * CellWidth), CellHeight));
                }
                if (IsWideGlyph(cell.Text))
                {
                    DrawRun(context, cell.Text, cell, origin, CellWidth);
                    col += 2; // wide glyph + its continuation cell
                    continue;
                }
                var start = col;
                var style = StyleKeyFor(cell);
                var builder = new StringBuilder(cell.Text);
                col++;
                while (col < line.Length && builder.Length < 256)
                {
                    var next = line[col];
                    if (string.IsNullOrEmpty(next.Text) || next.Text == " " || next.Conceal) break;
                    if (IsWideGlyph(next.Text) || !StyleKeyFor(next).Equals(style)) break;
                    builder.Append(next.Text);
                    col++;
                }
                DrawRun(context, builder.ToString(), cell, origin, (col - start) * CellWidth);
            }
        }
        if (_snapshot.CursorVisible && Session?.IsRunning == true && !selectionFocusHidesCursor)
        {
            var cursorColumn = Math.Min(_snapshot.CursorColumn, (Session.Buffer.Columns - 1));
            var cursorRect = new Rect(OriginX + cursorColumn * CellWidth, OriginY + _snapshot.CursorLine * CellHeight, CellWidth, CellHeight);
            var cursorBrush = TextBrush ?? Brush(TerminalBuffer.DefaultForeground);
            switch (_snapshot.CursorStyle)
            {
                case 3: case 4: // underline
                    context.FillRectangle(cursorBrush, new Rect(cursorRect.X, cursorRect.Bottom - 2, CellWidth, 2));
                    break;
                case 5: case 6: // vertical bar
                    context.FillRectangle(cursorBrush, new Rect(cursorRect.X, cursorRect.Y, 2, CellHeight));
                    break;
                case 2: // steady block: hollow block so text stays readable
                    context.DrawRectangle(null, new Pen(cursorBrush, 1), cursorRect.Deflate(0.5));
                    break;
                default: // blinking block, drawn hollow like the steady variant
                    context.DrawRectangle(null, new Pen(cursorBrush, 1), cursorRect.Deflate(0.5));
                    break;
            }
        }
    }

    private bool selectionFocusHidesCursor => Session?.OutputSelection.HasRange == true;

    private int ResolveBackground(TerminalCell cell)
    {
        if (cell.Reverse) return cell.Foreground == TerminalBuffer.DefaultForeground ? TerminalBuffer.DefaultForeground : cell.Foreground;
        return cell.Background;
    }

    private int ResolveColor(TerminalCell cell)
    {
        if (cell.Reverse)
            return cell.Background == TerminalBuffer.DefaultBackground ? TerminalBuffer.DefaultBackground : cell.Background;
        return cell.Foreground;
    }

    private IBrush ResolveForeground(TerminalCell cell)
    {
        var color = ResolveColor(cell);
        if (cell.Dim) return new SolidColorBrush(Color.FromArgb(0x9A, (byte)(color >> 16), (byte)(color >> 8), (byte)color));
        return color == TerminalBuffer.DefaultForeground ? TextBrush ?? Brush(color) : Brush(color);
    }

    private readonly record struct RunStyleKey(int Color, bool Bold, bool Italic, bool Underline, bool Strike, string? Link);
    private static RunStyleKey StyleKeyFor(TerminalCell cell) =>
        new(cell.Reverse ? (cell.Background == TerminalBuffer.DefaultBackground ? TerminalBuffer.DefaultBackground : cell.Background) : cell.Foreground,
            cell.Bold, cell.Italic, cell.Underline || cell.Link is not null, cell.Strike, cell.Link);

    private void DrawRun(DrawingContext context, string text, TerminalCell cell, Point origin, double width)
    {
        var typeface = TypefaceFor(cell);
        var foreground = ResolveForeground(cell);
        var formatted = TextFor(text, ResolveColor(cell), typeface, foreground);
        context.DrawText(formatted, origin);
        if (cell.Underline || cell.Link is not null)
            context.FillRectangle(foreground, new Rect(origin.X, origin.Y + CellHeight - 2, Math.Max(width, formatted.Width), 1));
        if (cell.Strike)
            context.FillRectangle(foreground, new Rect(origin.X, origin.Y + CellHeight / 2, Math.Max(width, formatted.Width), 1));
    }

    private static bool IsWideGlyph(string glyph)
    {
        if (glyph.Length == 0) return false;
        var codepoint = char.ConvertToUtf32(glyph, 0);
        return codepoint >= 0x1100 && (codepoint <= 0x115F || codepoint is >= 0x2E80 and <= 0xA4CF
            || codepoint is >= 0xAC00 and <= 0xD7A3 || codepoint is >= 0xF900 and <= 0xFAFF
            || codepoint is >= 0xFE10 and <= 0xFE6F || codepoint is >= 0xFF00 and <= 0xFF60
            || codepoint >= 0x1F300);
    }

    private readonly record struct TextKey(string Text, int Color, bool Bold, bool Italic, double Size);

    private readonly Dictionary<TextKey, FormattedText> _textCache = new();

    /// <summary>
    /// Cached <see cref="FormattedText"/> per (text, color, style, size). Static output and scrollback reuse
    /// the same objects across frames instead of allocating one per cell per repaint.
    /// </summary>
    private FormattedText TextFor(string text, int color, Typeface typeface, IBrush brush)
    {
        var key = new TextKey(text, color, typeface.Weight == FontWeight.Bold, typeface.Style == FontStyle.Italic, _fontSizeDip);
        if (_textCache.TryGetValue(key, out var cached)) return cached;
        if (_textCache.Count >= 4096) _textCache.Clear();
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, _fontSizeDip, brush);
        _textCache[key] = formatted;
        return formatted;
    }

    private Typeface TypefaceFor(TerminalCell cell) =>
        new(new FontFamily("Consolas, Cascadia Mono, Microsoft YaHei"),
            cell.Italic ? FontStyle.Italic : FontStyle.Normal,
            cell.Bold ? FontWeight.Bold : FontWeight.Normal);

    private Rect CellRect(int row, int fromColumn, int toColumn) =>
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

    /// <summary>Select the whole snapshot (screen + scrollback) for “全选”.</summary>
    public bool SelectAll()
    {
        if (Session is not { } tab || _snapshot is null) return false;
        if (!tab.OutputSelection.SelectAll(_snapshot)) return false;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
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
        // Applications that enabled mouse tracking receive the click; Shift keeps text selection available.
        if (_snapshot.MouseTracking && !e.KeyModifiers.HasFlag(KeyModifiers.Shift) &&
            TrySendMouse(point.Position, button: 0, release: false, wheel: 0))
        {
            e.Handled = true;
            return;
        }
        // Double/triple click select a word/row; dragging afterwards extends the same selection.
        if (e.ClickCount >= 2 && CellAt(point.Position) is { } clicked)
        {
            if (e.ClickCount >= 3) tab.OutputSelection.SelectRow(_snapshot, clicked.Row);
            else tab.OutputSelection.SelectWord(_snapshot, clicked);
            e.Pointer.Capture(this);
            SelectionChanged?.Invoke(this, EventArgs.Empty);
            InvalidateVisual();
            e.Handled = true;
            return;
        }
        if (LinkAt(point.Position) is { Length: > 0 } link)
        {
            // Ctrl+Click opens an OSC 8 hyperlink; a plain click keeps selecting text.
            if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) { LinkActivated?.Invoke(this, link); e.Handled = true; return; }
        }
        if (CellAt(point.Position) is not { } cell) return;
        tab.OutputSelection.Begin(_snapshot, cell);
        e.Pointer.Capture(this);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        var position = e.GetCurrentPoint(this).Position;
        UpdateHoverLink(position);
        // Capture is only taken by a left-button press, so a captured move is always a drag.
        if (!ReferenceEquals(e.Pointer.Captured, this) || Session is not { } tab || _snapshot is null) return;
        // Drag beyond the viewport scrolls one row per event so long scrollback is reachable while selecting.
        if (this.FindAncestorOfType<ScrollViewer>() is { } scroller)
        {
            var maximum = Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height);
            if (position.Y < OriginY) scroller.Offset = new Vector(0, Math.Max(0, scroller.Offset.Y - CellHeight));
            else if (position.Y > Bounds.Height - OriginY) scroller.Offset = new Vector(0, Math.Min(maximum, scroller.Offset.Y + CellHeight));
        }
        if (CellAt(position) is not { } cell) return;
        if (!tab.OutputSelection.Extend(_snapshot, cell)) return;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    /// <summary>Track the OSC 8 hyperlink under the pointer; the renderer highlights it.</summary>
    private void UpdateHoverLink(Point position)
    {
        var link = CellAt(position) is { } cell && _snapshot is not null ? _snapshot.Lines[cell.Row][cell.Column].Link : null;
        if (Session is not { } tab || string.Equals(tab.HoveredLink, link, StringComparison.Ordinal)) return;
        tab.HoveredLink = link;
        InvalidateVisual();
    }

    /// <summary>The OSC 8 link at a position, or null when the cell has none.</summary>
    public string? LinkAt(Point position) =>
        CellAt(position) is { } cell && _snapshot is not null ? _snapshot.Lines[cell.Row][cell.Column].Link : null;

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_snapshot?.MouseTracking == true && !e.KeyModifiers.HasFlag(KeyModifiers.Shift) &&
            TrySendMouse(e.GetCurrentPoint(this).Position, button: 0, release: true, wheel: 0))
        {
            e.Handled = true;
            return;
        }
        if (!ReferenceEquals(e.Pointer.Captured, this)) return;
        if (Session is { } tab && _snapshot is not null && CellAt(e.GetCurrentPoint(this).Position) is { } cell)
            tab.OutputSelection.Extend(_snapshot, cell);
        e.Pointer.Capture(null);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (Session is not { } tab || _snapshot?.MouseTracking != true || e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;
        // Wheel up = 64, wheel down = 65 in both X10 and SGR encodings.
        if (TrySendMouse(e.GetPosition(this), button: 0, release: false, wheel: e.Delta.Y > 0 ? 64 : 65)) e.Handled = true;
    }

    private bool TrySendMouse(Point position, int button, bool release, int wheel)
    {
        if (Session is not { IsRunning: true } tab || _snapshot is null) return false;
        var cell = CellAt(position);
        if (cell is null) return false;
        var x = Math.Clamp(cell.Value.Column + 1, 1, 999);
        var y = Math.Clamp(cell.Value.Row + 1, 1, 999);
        if (_snapshot.MouseMode == 1006)
        {
            var code = wheel != 0 ? wheel : button;
            tab.SendRaw($"\u001b[<{code};{x};{y}{(release ? 'm' : 'M')}");
        }
        else
        {
            var code = wheel != 0 ? wheel : release ? 3 : button;
            tab.SendRaw($"\u001b[M{(char)(32 + code)}{(char)(32 + x)}{(char)(32 + y)}");
        }
        return true;
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
        if (control && shift && e.Key == Key.A) { e.Handled = true; SelectAll(); return; }
        if (control && e.Key is Key.OemPlus or Key.Add) { e.Handled = true; FontSizeDip += 1; return; }
        if (control && e.Key is Key.OemMinus or Key.Subtract) { e.Handled = true; FontSizeDip -= 1; return; }
        if (control && e.Key is Key.D0 or Key.NumPad0) { e.Handled = true; FontSizeDip = 13; return; }
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
