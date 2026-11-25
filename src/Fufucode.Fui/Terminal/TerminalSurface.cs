using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Fufucode.Fui;

internal sealed class TerminalSurface : IDisposable
{
    private readonly TerminalLayout _layout = new();
    private readonly SixelSupport _graphics = new(Environment.GetEnvironmentVariable("FUI_GRAPHICS"));
    private readonly PasteKeys _keys;
    private readonly TuiHyperlinkPolicy _hyperlinks = TuiHyperlinkPolicy.FromEnvironment();
    private TuiTheme _theme = TuiTheme.FromEnvironment();
    private ImageCells? _paintedCell;
    private readonly object _gate = new();
    private TextWriter _output = Console.Out;
    private Encoding? _encoding;
    private WindowsModes? _modes;
    private TerminalFrame? _previous;
    private TuiTheme? _paintedTheme;
    private bool _alt, _changedCtrlC, _disposed;
    private bool _oldCursorVisible = true;

    internal const string MouseOnSequence = "\x1b[?1000h\x1b[?1006h";
    internal const string MouseOffSequence = "\x1b[?1006l\x1b[?1000l";

    public static bool IsSupported => MouseInput.CanEnableMouse(
        Console.IsInputRedirected, Console.IsOutputRedirected,
        OperatingSystem.IsWindows() && WindowsModes.Attached, OperatingSystem.IsWindows(),
        Environment.GetEnvironmentVariable("TERM"));

    public TerminalSurface()
    {
        _keys = new(_graphics.Reply, ColorReply);
        if (!IsSupported) throw new InvalidOperationException("FUI 需要交互式终端。");
        try
        {
            if (OperatingSystem.IsWindows())
            {
                _modes = new();
                _modes.Enable();
                _oldCursorVisible = Console.CursorVisible;
            }
            if (Console.OutputEncoding.CodePage != Encoding.UTF8.CodePage)
            {
                _encoding = Console.OutputEncoding;
                Console.OutputEncoding = new UTF8Encoding(false);
                _output = Console.Out;
            }
            if (!Console.TreatControlCAsInput)
            {
                Console.TreatControlCAsInput = true;
                _changedCtrlC = true;
            }
            AppDomain.CurrentDomain.ProcessExit += Exit;
            AppDomain.CurrentDomain.UnhandledException += Crash;
            _alt = true;
            Write("\x1b[?25s\x1b[?2004s\x1b[?1049h\x1b[?2004h\x1b[?25l" + MouseOnSequence);
            if (_graphics.Probe) Write(SixelSupport.Query);
            else if (_theme.NeedsBackgroundProbe) Write(TuiTheme.BackgroundQuery);
        }
        catch { Dispose(); throw; }
    }

    public int ViewportRows => _previous?.ViewportRows ?? 0;
    public int MaxScroll => _previous?.MaxScroll ?? 0;
    public int PickerRows => _previous?.PickerRows ?? 0;
    public bool CanApprove => _previous?.CanApprove == true;
    public int ScrollToLine(int transcriptIndex) => _layout.ScrollToLine(transcriptIndex);
    public bool SizeChanged => _previous is null || _paintedCell != _graphics.Cell || (Math.Max(1, Console.WindowWidth), Math.Max(1, Console.WindowHeight)) !=
        (_previous.Width, _previous.Rows.Length);

    public void Draw(TuiView view)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var width = Console.WindowWidth;
            var height = Console.WindowHeight;
            if (_previous is { } last && (last.Width != width || last.Rows.Length != height))
            {
                if (_graphics.Probe) Write("\x1b[16t" + SixelSupport.Colors);
                else if (_theme.NeedsBackgroundProbe) Write(TuiTheme.BackgroundQuery);
            }
            _paintedCell = _graphics.Cell;
            var frame = _layout.Arrange(view, width, height, _paintedCell);
            var repaint = !ReferenceEquals(_paintedTheme, _theme);
            var paint = Diff(repaint ? null : _previous, frame, _theme, _hyperlinks);
            if (paint.Length > 0) Write(paint);
            _previous = frame;
            _paintedTheme = _theme;
        }
    }

    public ConsoleKeyInfo? PollKey()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (ReadMouse() is { } mouseKey) return mouseKey;
        if (_keys.Read() is { } queued) return queued;
        // No blocking ReadKey on idle. Bound a marker-only or large paste read so the controller can drain model events.
        for (var i = 0; i < 512 && Console.KeyAvailable; i++)
        {
            _keys.Feed(Console.ReadKey(intercept: true), Environment.TickCount64);
            if (ReadMouse() is { } fedMouseKey) return fedMouseKey;
            if (_keys.Read() is { } key) return key;
        }
        _keys.FlushExpired(Environment.TickCount64);
        if (ReadMouse() is { } delayedMouseKey) return delayedMouseKey;
        return _keys.Read();
    }

    internal static string Diff(TerminalFrame? old, TerminalFrame next, bool styling) =>
        Diff(old, next, styling ? TuiTheme.Resolve("dark", colorTerm: "truecolor") : TuiTheme.Unstyled,
            TuiHyperlinkPolicy.Disabled);

    internal static string Diff(TerminalFrame? old, TerminalFrame next, TuiTheme theme,
        TuiHyperlinkPolicy? hyperlinks = null)
    {
        hyperlinks ??= TuiHyperlinkPolicy.Disabled;
        var reset = old is null || old.Width != next.Width || old.Rows.Length != next.Rows.Length;
        var paint = new StringBuilder();
        if (reset) paint.Append("\x1b[2J");
        for (var i = 0; i < next.Rows.Length; i++)
        {
            var row = next.Rows[i];
            if (!reset && row == old!.Rows[i]) continue;
            if (reset && row.Text.Length == 0 && row.Images is null) continue;
            paint.Append("\x1b[").Append(i + 1).Append(";1H");
            if (!reset && old!.Rows[i].Images is { Count: > 0 }) paint.Append("\x1b[2K");
            PaintText(paint, row, theme, hyperlinks);
            if (!reset) paint.Append("\x1b[K"); // remove remnants when a line contracts
            if (row.Images is not null)
                foreach (var image in row.Images)
                    paint.Append("\x1b[").Append(i + 1).Append(';').Append(row.ImageIndent + image.Column + 1)
                        .Append('H').Append(image.Sixel);
        }
        var paintedRows = paint.Length != 0;
        if (!paintedRows && old is not null && old.CursorRow == next.CursorRow && old.CursorColumn == next.CursorColumn) return "";
        if (next.CursorRow >= 0)
            paint.Append("\x1b[").Append(next.CursorRow + 1).Append(';').Append(next.CursorColumn + 1).Append('H');
        if (reset || old is null || (old.CursorRow < 0) != (next.CursorRow < 0))
            paint.Append(next.CursorRow >= 0 ? "\x1b[?25h" : "\x1b[?25l");
        // One host write; terminals supporting synchronized output don't expose intermediate cursor moves.
        return paintedRows ? "\x1b[?2026h" + paint + "\x1b[?2026l" : paint.ToString();
    }

    private static void PaintText(StringBuilder paint, ScreenRow row, TuiTheme theme, TuiHyperlinkPolicy hyperlinks)
    {
        if (row.Spans is not { Count: > 0 } spans ||
            !string.Equals(spans.Text, row.Text, StringComparison.Ordinal))
        {
            PaintSpan(paint, row.Text, row.Ink, null, theme, hyperlinks);
            return;
        }
        foreach (var span in spans) PaintSpan(paint, span.Text, span.Ink, span.Link, theme, hyperlinks);
    }

    private static void PaintSpan(StringBuilder paint, string text, Ink ink, string? link, TuiTheme theme,
        TuiHyperlinkPolicy hyperlinks)
    {
        var style = theme.StylingEnabled && ink != Ink.Plain ? theme.Style(ink) : "";
        if (style.Length > 0) paint.Append(style);
        paint.Append(hyperlinks.Wrap(text, link));
        if (style.Length > 0) paint.Append(theme.Reset);
    }

    private ConsoleKeyInfo? ReadMouse()
    {
        if (!_keys.TryReadMouse(out var mouse)) return null;
        if (MouseInput.WheelKey(mouse) is { } wheel) return wheel;
        if (!mouse.LeftClick || mouse.Release || mouse.Motion) return null;
        var hit = MouseInput.HitTest(_previous?.Mouse, mouse);
        if (hit.Kind == MouseHitKind.None) return null;
        MouseInput.Stage(mouse, hit);
        return MouseInput.RoutedKeyInfo;
    }

    internal static string RestoreSequence(bool cursorVisible, TuiTheme theme) =>
        "\x1b[?2026l" + theme.Reset + "\x1b[?2004l" + MouseOffSequence + "\x1b[?1049l" +
        (cursorVisible ? "\x1b[?25h" : "\x1b[?25l") + "\x1b[?25r\x1b[?2004r";

    internal static string RestoreSequence(bool cursorVisible) => RestoreSequence(cursorVisible, TuiTheme.Unstyled);

    private void ColorReply(string payload)
    {
        _graphics.ColorReply(payload);
        if (TuiTheme.TryParseBackgroundReply(payload, out var rgb))
            _theme = _theme.WithReportedBackground(rgb);
    }

    private void Write(string value) { _output.Write(value); _output.Flush(); }
    private void Exit(object? sender, EventArgs args) => Dispose();
    private void Crash(object? sender, UnhandledExceptionEventArgs args) => Dispose();

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            AppDomain.CurrentDomain.ProcessExit -= Exit;
            AppDomain.CurrentDomain.UnhandledException -= Crash;
            // Each restoration is independent: a disconnected stdout must not leave input modes changed.
            if (_alt) TryRestore(() => Write(RestoreSequence(_oldCursorVisible, _theme)));
            if (_changedCtrlC) TryRestore(() => Console.TreatControlCAsInput = false);
            if (_encoding is not null) TryRestore(() => Console.OutputEncoding = _encoding);
            _modes?.Restore();
        }
    }

    private static void TryRestore(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException) { }
    }

    private sealed class WindowsModes
    {
        private readonly nint _input = GetStdHandle(-10), _output = GetStdHandle(-11);
        private uint _oldInput, _oldOutput;
        private bool _saved;

        internal static bool Attached => GetConsoleMode(GetStdHandle(-10), out _) && GetConsoleMode(GetStdHandle(-11), out _);

        internal void Enable()
        {
            if (!GetConsoleMode(_input, out _oldInput) || !GetConsoleMode(_output, out _oldOutput)) throw new Win32Exception();
            _saved = true;
            const uint processedOutput = 1, vtOutput = 4, lineInput = 2, echoInput = 4, quickEdit = 64, extendedFlags = 128, vtInput = 512;
            if (!SetConsoleMode(_output, _oldOutput | processedOutput | vtOutput) ||
                !SetConsoleMode(_input, (_oldInput | extendedFlags | vtInput) & ~(lineInput | echoInput | quickEdit))) throw new Win32Exception();
        }

        internal void Restore()
        {
            if (!_saved) return;
            SetConsoleMode(_input, _oldInput);
            SetConsoleMode(_output, _oldOutput);
        }

        [DllImport("kernel32.dll", SetLastError = true)] private static extern nint GetStdHandle(int handle);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetConsoleMode(nint handle, out uint mode);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetConsoleMode(nint handle, uint mode);
    }
}
