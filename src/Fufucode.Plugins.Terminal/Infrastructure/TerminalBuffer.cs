using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Fufucode.Plugins.Terminal.Infrastructure;

public readonly record struct TerminalCell(
    string Text,
    int Foreground,
    int Background,
    bool Bold = false,
    bool Underline = false,
    bool Italic = false,
    bool Reverse = false,
    bool Dim = false,
    bool Conceal = false,
    bool Strike = false,
    string? Link = null);

public sealed record TerminalSnapshot(
    TerminalCell[][] Lines,
    int CursorColumn,
    int CursorLine,
    bool CursorVisible,
    bool AlternateScreen = false,
    string Title = "",
    bool BracketedPaste = false,
    bool MouseTracking = false,
    int CursorStyle = 0);

/// <summary>
/// Bounded VT screen plus scrollback. Escape sequences are interpreted, never shown as raw control text.
/// The alternate screen, the scroll region and the cursor/device reports are implemented so full-screen
/// applications (vim, less, htop, prompt frameworks) work instead of corrupting the scrollback.
/// </summary>
public sealed class TerminalBuffer
{
    public const int DefaultForeground = 0xDEE3EC;
    public const int DefaultBackground = 0x14171D;
    private static readonly int[] Palette = { 0x20242C, 0xE06C75, 0x98C379, 0xE5C07B, 0x61AFEF, 0xC678DD, 0x56B6C2, 0xD8DEE9, 0x6F7785, 0xFF8590, 0xB4E88D, 0xFFDA91, 0x85C9FF, 0xE7A0FF, 0x80DFE8, 0xFFFFFF };
    // Default palette for readers; OSC 4 overrides live in the per-buffer palette above.
    public static IReadOnlyList<int> DefaultPalette => Array.AsReadOnly(Palette);
    private const int DefaultScrollbackLimit = 1500;

    // Active screen. While an alternate screen runs, the primary screen lives in _mainScreen/_mainHistory.
    private List<TerminalCell[]> _screen = new();
    private List<TerminalCell[]> _history = new();
    private List<TerminalCell[]> _mainScreen = new();
    private List<TerminalCell[]> _mainHistory = new();

    private readonly StringBuilder _sequence = new();
    private int _parseState;                // 0 ground, 1 ESC, 2 CSI, 3 OSC, 4 OSC-after-ESC
    private int _column;                    // 0-based; stays on the last column while a wrap is pending
    private int _row;                       // 0-based
    private int _savedColumn, _savedRow;
    private int _top, _bottom;              // inclusive scroll region, 0-based
    private int _foreground = DefaultForeground, _background = DefaultBackground;
    private bool _bold, _underline, _italic, _reverse, _dim, _conceal, _strike;
    private bool _cursorVisible = true, _wrapPending, _autoWrap = true, _originMode, _insertMode, _newLineMode;
    private bool _bracketedPaste, _mouseTracking;
    private int _cursorStyle, _scrollbackLimit = DefaultScrollbackLimit;
    private readonly int[] _palette = (int[])Palette.Clone();
    private string _title = string.Empty;
    private string? _currentLink;
    private readonly StringBuilder _osc = new();
    private int _lastCellRow = -1, _lastCellColumn = -1;
    private char? _pendingSurrogate;
    private string _lastChar = string.Empty;
    private string _replySequence = string.Empty;   // CSI reply queued by the last escape sequence

    public TerminalBuffer(int columns = 100, int rows = 12)
    {
        Columns = Math.Clamp(columns, 10, 400);
        Rows = Math.Clamp(rows, 3, 160);
        for (var i = 0; i < Rows; i++) _screen.Add(BlankLine());
        _top = 0; _bottom = Rows - 1;
    }

    public int Columns { get; private set; }
    public int Rows { get; private set; }
    public bool AlternateScreen { get; private set; }
    public string Title => _title;
    public bool BracketedPaste => _bracketedPaste;
    public bool MouseTracking => _mouseTracking;
    /// <summary>DECSCUSR: 0/1 blinking block, 2 steady block, 3/4 underline, 5/6 bar.</summary>
    public int CursorStyle => _cursorStyle;
    public int ScrollbackLimit
    {
        get => _scrollbackLimit;
        set
        {
            _scrollbackLimit = Math.Clamp(value, 0, 100_000);
            foreach (var history in new[] { _history, _mainHistory })
                if (history.Count > _scrollbackLimit) history.RemoveRange(0, history.Count - _scrollbackLimit);
            Changed?.Invoke();
        }
    }
    public event Action? Changed;
    /// <summary>BEL (0x07) from the shell; the page decides whether to beep or flash.</summary>
    public event Action? Bell;

    /// <summary>
    /// Reply bytes requested by DSR/DA (for example <c>CSI 6n</c>). The session must write them back to the
    /// shell: prompt frameworks and full-screen applications wait for the answer. A reply is always queued for
    /// the rest of the sequence it belongs to, because ConPTY feeds one reply through its input path and the
    /// rest through its output painting pass.
    /// </summary>
    public string TakePendingResponse()
    {
        if (_replySequence.Length == 0) return string.Empty;
        var reply = _replySequence;
        _replySequence = string.Empty;
        return reply;
    }

    public void Append(string text)
    {
        if (text.Length == 0) return;
        foreach (var character in text)
        {
            switch (_parseState)
            {
                case 3: // OSC: capture up to BEL or ESC-backslash (title and hyperlinks).
                    if (character == '\a') { ApplyOsc(_osc.ToString()); _osc.Clear(); _parseState = 0; }
                    else if (character == '\u001b') _parseState = 4;
                    else if (_osc.Length < 4096) _osc.Append(character);
                    continue;
                case 4:
                    if (character == '\\') { ApplyOsc(_osc.ToString()); _osc.Clear(); _parseState = 0; }
                    else { _osc.Append('\u001b').Append(character); _parseState = 3; }
                    continue;
                case 1:
                    if (character == '[') { _parseState = 2; _sequence.Clear(); continue; }
                    if (character == ']') { _parseState = 3; _osc.Clear(); continue; }
                    ApplyEscape(character);
                    _parseState = 0;
                    continue;
                case 2:
                    if (character >= '@' && character <= '~')
                    {
                        _replySequence = string.Empty;
                        ApplyCsi(_sequence.ToString(), character);
                        _sequence.Clear();
                        _parseState = 0;
                    }
                    else if (_sequence.Length < 128) _sequence.Append(character);
                    else { _sequence.Clear(); _parseState = 0; }
                    continue;
            }

            if (character == '\u001b') { _parseState = 1; continue; }
            switch (character)
            {
                case '\r': _column = 0; _wrapPending = false; continue;
                case '\n':
                    if (_newLineMode) _column = 0;
                    LineFeed();
                    continue;
                case '\b':
                    _column = Math.Max(0, _column - 1); _wrapPending = false;
                    continue;
                case '\t':
                    _column = Math.Min(Columns - 1, ((_column / 8) + 1) * 8); _wrapPending = false;
                    continue;
                case '\u000e': _newLineMode = true; continue;  // SO
                case '\u000f': _newLineMode = false; continue; // SI
                case '\a': Bell?.Invoke(); continue;           // BEL
            }
            if (char.IsControl(character)) continue;
            if (char.IsHighSurrogate(character)) { _pendingSurrogate = character; continue; }
            var glyph = _pendingSurrogate is char high && char.IsLowSurrogate(character)
                ? new string(new[] { high, character })
                : character.ToString();
            _pendingSurrogate = null;
            Put(glyph);
        }
        Changed?.Invoke();
    }

    public void Resize(int columns, int rows)
    {
        columns = Math.Clamp(columns, 10, 400);
        rows = Math.Clamp(rows, 3, 160);
        if (columns == Columns && rows == Rows) return;
        ResizeScreen(_screen, _history, columns, rows);
        if (AlternateScreen) ResizeScreen(_mainScreen, _mainHistory, columns, rows);
        Columns = columns;
        Rows = rows;
        _row = Math.Clamp(_row, 0, Rows - 1);
        _column = Math.Clamp(_column, 0, Columns - 1);
        _top = 0; _bottom = Rows - 1; _wrapPending = false;
        Changed?.Invoke();
    }

    private void ResizeScreen(List<TerminalCell[]> screen, List<TerminalCell[]> history, int columns, int rows)
    {
        var limit = _scrollbackLimit;
        for (var i = 0; i < screen.Count; i++)
        {
            var line = screen[i];
            if (line.Length == columns) continue;
            if (columns < line.Length)
            {
                // Crop to the new width. A double-width glyph whose continuation cell is cut off is replaced
                // by a blank so the row never carries half a glyph.
                line = line[..columns];
                if (columns > 0 && line[columns - 1].Text.Length > 0 && IsWide(line[columns - 1].Text))
                    line[columns - 1] = BlankCell();
            }
            else
            {
                var grown = new TerminalCell[columns];
                Array.Copy(line, grown, line.Length);
                for (var c = line.Length; c < columns; c++) grown[c] = BlankCell();
                line = grown;
            }
            screen[i] = line;
        }
        while (screen.Count > rows)
        {
            history.Add(screen[0]);
            screen.RemoveAt(0);
            if (history.Count > limit) history.RemoveRange(0, history.Count - limit);
        }
        while (screen.Count < rows) screen.Add(BlankLine(columns));
    }

    // Scrollback rows stop changing when they leave the screen; only the live screen needs copies.
    public TerminalSnapshot Snapshot() => new(
        _history.Concat(_screen.Select(line => line.ToArray())).ToArray(),
        _column,
        _history.Count + _row,
        _cursorVisible,
        AlternateScreen,
        _title,
        _bracketedPaste,
        _mouseTracking,
        _cursorStyle);

    public string PlainText => string.Join("\n", _history.Concat(_screen).Select(line => string.Concat(line.Select(cell => cell.Text)).TrimEnd())).TrimEnd();

    private TerminalCell Styled(string text) =>
        new(text, _foreground, _background, _bold, _underline, _italic, _reverse, _dim, _conceal, _strike, _currentLink);

    private void Put(string glyph)
    {
        var wide = IsWide(glyph);
        if (_wrapPending)
        {
            _wrapPending = false;
            if (_autoWrap) { _column = 0; LineFeed(); }
        }
        if (wide && _column == Columns - 1)
        {
            // A double-width glyph cannot start on the last column: leave it blank and wrap first.
            _screen[_row][_column] = BlankCell();
            _lastChar = glyph;
            if (_autoWrap) { _column = 0; LineFeed(); }
            else return;
        }
        if (_insertMode)
        {
            var row = _screen[_row];
            var shift = wide ? 2 : 1;
            for (var c = Columns - 1; c >= _column + shift; c--) row[c] = row[c - shift];
            for (var c = _column; c < Math.Min(Columns, _column + shift); c++) row[c] = BlankCell();
        }
        else if (_column > 0 && _screen[_row][_column].Text.Length == 0)
        {
            // Overwriting the continuation cell of a wide glyph must not leave its owner half painted.
            _screen[_row][_column - 1] = BlankCell();
        }
        if (_lastCellRow == _row && _lastCellColumn == _column &&
            _pendingSurrogate is null && IsCombining(glyph))
        {
            // A combining mark extends the glyph already in this cell instead of taking its own.
            _screen[_row][_column] = _screen[_row][_column] with { Text = _screen[_row][_column].Text + glyph };
            return;
        }
        _screen[_row][_column] = Styled(glyph);
        if (wide && _column + 1 < Columns) _screen[_row][_column + 1] = Styled(string.Empty);
        _lastChar = glyph;
        _lastCellRow = _row;
        _lastCellColumn = _column;
        _column += wide ? 2 : 1;
        if (_column >= Columns)
        {
            _column = Columns - 1;
            if (_autoWrap) _wrapPending = true;
        }
    }

    /// <summary>Combining marks and joiners attach to the previous cell (approximate UAX#29 starter check).</summary>
    private static bool IsCombining(string glyph)
    {
        var codepoint = char.ConvertToUtf32(glyph, 0);
        return codepoint is 0x200D or 0xFE0E or 0xFE0F
            || codepoint is >= 0x0300 and <= 0x036F || codepoint is >= 0x0483 and <= 0x0489
            || codepoint is >= 0x0591 and <= 0x05BD || codepoint is >= 0x0610 and <= 0x061A
            || codepoint is >= 0x064B and <= 0x065F || codepoint is >= 0x0E31 and <= 0x0E3A
            || codepoint is >= 0x0E47 and <= 0x0E4E || codepoint is >= 0x1AB0 and <= 0x1AFF
            || codepoint is >= 0x1DC0 and <= 0x1DFF || codepoint is >= 0x20D0 and <= 0x20FF
            || codepoint is >= 0x302A and <= 0x302F || codepoint is >= 0x3099 and <= 0x309A
            || codepoint is >= 0xFE00 and <= 0xFE0F || codepoint is >= 0xFE20 and <= 0xFE2F
            || codepoint is >= 0xE0100 and <= 0xE01EF || codepoint is >= 0x1F3FB and <= 0x1F3FF;
    }

    private void LineFeed()
    {
        _wrapPending = false;
        if (_row == _bottom) { ScrollUpRegion(1); return; }
        // A cursor below the scroll region continues toward the bottom of the screen instead of scrolling.
        if (_row < Rows - 1) _row++;
    }

    private void NewLine() { _column = 0; LineFeed(); }

    // ---------- screen primitives ----------

    private TerminalCell[] BlankLine() => BlankLine(Columns);
    private static TerminalCell[] BlankLine(int columns) => Enumerable.Repeat(BlankCell(), columns).ToArray();
    private static TerminalCell BlankCell() => new(" ", DefaultForeground, DefaultBackground);
    private TerminalCell ErasedCell() => new(" ", _foreground, _background, _bold, _underline, _italic, _reverse, _dim, _conceal, _strike, _currentLink);

    /// <summary>Scroll the scroll region up; a full-region scroll enters the bounded history.</summary>
    private void ScrollUpRegion(int count)
    {
        for (var n = 0; n < count; n++)
        {
            var removed = _screen[_top];
            _screen.RemoveAt(_top);
            if (_top == 0 && _bottom == Rows - 1)
            {
                _history.Add(removed);
                if (_history.Count > _scrollbackLimit) _history.RemoveRange(0, _history.Count - _scrollbackLimit);
            }
            _screen.Insert(_bottom, BlankLine());
        }
    }

    private void ScrollDownRegion(int count)
    {
        for (var n = 0; n < count; n++)
        {
            _screen.RemoveAt(_bottom);
            _screen.Insert(_top, BlankLine());
        }
    }

    private void InsertLines(int count)
    {
        if (_row < _top || _row > _bottom) return;
        count = Math.Clamp(count, 0, _bottom - _row + 1);
        for (var n = 0; n < count; n++)
        {
            _screen.RemoveAt(_bottom);
            _screen.Insert(_row, BlankLine());
        }
    }

    private void DeleteLines(int count)
    {
        if (_row < _top || _row > _bottom) return;
        count = Math.Clamp(count, 0, _bottom - _row + 1);
        for (var n = 0; n < count; n++)
        {
            _screen.RemoveAt(_row);
            _screen.Insert(_bottom, BlankLine());
        }
    }

    private void EnterAlternateScreen(bool clear)
    {
        if (AlternateScreen) return;
        (_screen, _mainScreen) = (_mainScreen, _screen);
        (_history, _mainHistory) = (_mainHistory, _history);
        _screen.Clear();
        for (var i = 0; i < Rows; i++) _screen.Add(BlankLine());
        if (clear) _history.Clear();
        AlternateScreen = true;
        _wrapPending = false;
    }

    private void LeaveAlternateScreen()
    {
        if (!AlternateScreen) return;
        (_screen, _mainScreen) = (_mainScreen, _screen);
        (_history, _mainHistory) = (_mainHistory, _history);
        AlternateScreen = false;
        _top = 0; _bottom = Rows - 1;
        _row = Math.Clamp(_row, 0, Rows - 1);
        _column = Math.Clamp(_column, 0, Columns - 1);
        _wrapPending = false;
    }

    // ---------- escape and CSI ----------

    private void ApplyEscape(char command)
    {
        switch (command)
        {
            case '7': SaveCursor(); break;
            case '8': RestoreCursor(); break;
            case 'D': LineFeed(); break;
            case 'E': NewLine(); break;
            case 'M':
                if (_row == _top) ScrollDownRegion(1);
                else _row = Math.Max(0, _row - 1);
                _wrapPending = false;
                break;
            case 'c': Reset(); break;
            case '(': case ')': case '*': case '+': break; // charset designation: UTF-8 only
            case '=': case '>': break;                     // keypad modes: no screen effect
        }
    }

    /// <summary>OSC payload handlers. Only title and hyperlink carry visible state; the rest is discarded.</summary>
    private void ApplyOsc(string payload)
    {
        var separator = payload.IndexOf(';');
        var command = separator < 0 ? payload : payload[..separator];
        var body = separator < 0 ? string.Empty : payload[(separator + 1)..];
        switch (command)
        {
            case "0":
            case "2":
                _title = SanitizeTitle(body);
                break;
            case "1":
                if (_title.Length == 0) _title = SanitizeTitle(body);
                break;
            case "8":
                // OSC 8 ; params ; URI ST text OSC 8 ; ; ST — an empty URI ends the hyperlink.
                _currentLink = string.IsNullOrEmpty(body) ? null : body;
                break;
            case "4":
                // OSC 4 ; index ; color — override a palette entry (the first 16 colors).
                var parts = body.Split(';');
                if (parts.Length >= 2 && int.TryParse(parts[0], out var index) && index is >= 0 and < 16 && TryParseColor(parts[1], out var value))
                    _palette[index] = value;
                break;
            case "9": case "777": break; // notifications: no visual state in the terminal itself
        }
    }

    private static string SanitizeTitle(string text)
    {
        var builder = new StringBuilder(Math.Min(text.Length, 120));
        foreach (var character in text)
        {
            if (char.IsControl(character)) continue;
            builder.Append(character);
            if (builder.Length >= 120) break;
        }
        return builder.ToString().Trim();
    }

    private static bool TryParseColor(string text, out int color)
    {
        color = 0;
        var value = text.Trim();
        if (value.StartsWith("rgb:", StringComparison.OrdinalIgnoreCase))
        {
            var channels = value[4..].Split('/');
            if (channels.Length != 3) return false;
            var rgb = 0;
            for (var i = 0; i < 3; i++)
            {
                if (!int.TryParse(channels[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var channel)) return false;
                rgb = (rgb << 8) | Math.Clamp(channel, 0, 255);
            }
            color = rgb;
            return true;
        }
        if (value.StartsWith('#') || value.Length == 6)
        {
            return int.TryParse(value.TrimStart('#'), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out color);
        }
        return false;
    }

    private void SaveCursor()
    {
        _savedColumn = _column; _savedRow = _row;
    }

    private void RestoreCursor()
    {
        _column = Math.Clamp(_savedColumn, 0, Columns - 1);
        _row = Math.Clamp(_savedRow, 0, Rows - 1);
        _wrapPending = false;
    }

    private void Reset()
    {
        LeaveAlternateScreen();
        _foreground = DefaultForeground; _background = DefaultBackground; _bold = false;
        _cursorVisible = true; _autoWrap = true; _originMode = false; _insertMode = false; _newLineMode = false;
        ResetAttributes();
        _bracketedPaste = false; _mouseTracking = false; _cursorStyle = 0; _currentLink = null;
        Array.Copy(Palette, _palette, Palette.Length);
        for (var i = 0; i < Rows; i++) _screen[i] = BlankLine();
        _top = 0; _bottom = Rows - 1; _row = 0; _column = 0; _wrapPending = false;
    }

    private void ResetAttributes()
    {
        _foreground = DefaultForeground; _background = DefaultBackground;
        _bold = _underline = _italic = _reverse = _dim = _conceal = _strike = false;
    }

    private void ApplyCsi(string sequence, char command)
    {
        if (sequence.StartsWith('?')) { ApplyPrivate(sequence, command); return; }
        if (sequence.StartsWith('>')) { if (command == 'c') _replySequence = "\u001b[>0;276;0c"; return; }

        var values = sequence.Length == 0 ? Array.Empty<int>() : sequence.Split(';')
            .Select(value => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : 0)
            .ToArray();
        int Value(int index, int fallback = 1) => index >= values.Length || values[index] == 0 ? fallback : values[index];

        switch (command)
        {
            case 'A': MoveCursor(-Value(0), 0); break;
            case 'B': case 'e': MoveCursor(Value(0), 0); break;
            case 'C': case 'a': MoveCursor(0, Value(0)); break;
            case 'D': MoveCursor(0, -Value(0)); break;
            case 'E': MoveCursor(Value(0), 0); _column = 0; break;
            case 'F': MoveCursor(-Value(0), 0); _column = 0; break;
            case 'G': case '`': _column = Math.Clamp(Value(0) - 1, 0, Columns - 1); _wrapPending = false; break;
            case 'd':
                _row = OriginRow(Value(0) - 1);
                _wrapPending = false;
                break;
            case 'H': case 'f':
                _row = OriginRow(Value(0) - 1);
                _column = Math.Clamp(Value(1) - 1, 0, Columns - 1);
                _wrapPending = false;
                break;
            case 'J': EraseInDisplay(values.Length > 0 ? values[0] : 0); break;
            case 'K': EraseInLine(values.Length > 0 ? values[0] : 0); break;
            case 'X': EraseChars(Value(0)); break;
            case '@': InsertChars(Value(0)); break;
            case 'P': DeleteChars(Value(0)); break;
            case 'L': InsertLines(Value(0)); _column = 0; _wrapPending = false; break;
            case 'M': DeleteLines(Value(0)); _column = 0; _wrapPending = false; break;
            case 'S': ScrollUpRegion(Value(0)); break;
            case 'T': ScrollDownRegion(Value(0)); break;
            case 'b': RepeatLastChar(Value(0)); break;
            case 'r':
                {
                    var top = Math.Clamp(Value(0) - 1, 0, Rows - 1);
                    var bottom = Math.Clamp(Value(1, Rows) - 1, 0, Rows - 1);
                    if (top < bottom)
                    {
                        _top = top; _bottom = bottom;
                        _row = _originMode ? top : 0;
                        _column = 0;
                    }
                    _wrapPending = false;
                    break;
                }
            case 's': SaveCursor(); break;
            case 'u': RestoreCursor(); break;
            case 'h': foreach (var value in values) if (value == 4) _insertMode = true; break;
            case 'l': foreach (var value in values) if (value == 4) _insertMode = false; break;
            case 'm': ApplyColors(values); break;
            case 'n':
                if (values.Length > 0 && values[0] == 5) _replySequence = "\u001b[0n";
                else if (values.Length > 0 && values[0] == 6) _replySequence = $"\u001b[{_row + 1};{_column + 1}R";
                break;
            case 'q':
                // DECSCUSR: 0..6; out-of-range values fall back to the default block cursor.
                if (values.Length > 0 && values[0] is >= 0 and <= 6) _cursorStyle = values[0];
                break;
            case 'c':
                if (values.Length == 0 || values[0] == 0) _replySequence = "\u001b[?1;2c";
                break;
        }
    }

    /// <summary>Map a 1-based row from CUP/VPA through DEC origin mode.</summary>
    private int OriginRow(int zeroBased) =>
        _originMode ? Math.Clamp(_top + zeroBased, _top, _bottom) : Math.Clamp(zeroBased, 0, Rows - 1);

    private void MoveCursor(int rowDelta, int columnDelta)
    {
        var minRow = _originMode ? _top : 0;
        var maxRow = _originMode ? _bottom : Rows - 1;
        _row = Math.Clamp(_row + rowDelta, minRow, maxRow);
        _column = Math.Clamp(_column + columnDelta, 0, Columns - 1);
        _wrapPending = false;
    }

    private void ApplyPrivate(string sequence, char command)
    {
        if (command is not ('h' or 'l')) return;
        var set = command == 'h';
        foreach (var raw in sequence[1..].Split(';'))
        {
            if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var mode)) continue;
            switch (mode)
            {
                case 25: _cursorVisible = set; break;
                case 7: _autoWrap = set; break;
                case 6:
                    _originMode = set;
                    _row = set ? _top : 0;
                    _column = 0;
                    _wrapPending = false;
                    break;
                case 47: case 1047:
                    if (set) EnterAlternateScreen(clear: false); else LeaveAlternateScreen();
                    break;
                case 1049:
                    if (set) { SaveCursor(); EnterAlternateScreen(clear: true); }
                    else { LeaveAlternateScreen(); RestoreCursor(); }
                    break;
                case 2004: _bracketedPaste = set; break;
                case 1000: case 1002: case 1003: case 1006: _mouseTracking = set; break;
                case 1: case 3: case 12: case 1004: case 1005: case 1015:
                    break; // cursor-key/blink modes and focus events change input, not the rendered screen
            }
        }
    }

    private void EraseInDisplay(int mode)
    {
        switch (mode)
        {
            case 2: case 3:
                for (var i = 0; i < Rows; i++) _screen[i] = BlankLine();
                if (mode == 3) _history.Clear();
                break;
            case 0:
                EraseLine(_row, _column, Columns);
                for (var i = _row + 1; i < Rows; i++) _screen[i] = BlankLine();
                break;
            case 1:
                for (var i = 0; i < _row; i++) _screen[i] = BlankLine();
                EraseLine(_row, 0, Math.Min(Columns, _column + 1));
                break;
        }
        _wrapPending = false;
    }

    private void EraseInLine(int mode)
    {
        switch (mode)
        {
            case 0: EraseLine(_row, _column, Columns); break;
            case 1: EraseLine(_row, 0, Math.Min(Columns, _column + 1)); break;
            default: EraseLine(_row, 0, Columns); break;
        }
        _wrapPending = false;
    }

    private void EraseChars(int count)
    {
        var row = _screen[_row];
        for (var i = _column; i < Math.Min(Columns, _column + count); i++) row[i] = ErasedCell();
        _wrapPending = false;
    }

    private void InsertChars(int count)
    {
        var row = _screen[_row];
        count = Math.Min(count, Columns - _column);
        for (var i = Columns - 1; i >= _column + count; i--) row[i] = row[i - count];
        for (var i = _column; i < _column + count; i++) row[i] = BlankCell();
        _wrapPending = false;
    }

    private void DeleteChars(int count)
    {
        var row = _screen[_row];
        count = Math.Min(count, Columns - _column);
        for (var i = _column; i < Columns - count; i++) row[i] = row[i + count];
        for (var i = Columns - count; i < Columns; i++) row[i] = BlankCell();
        _wrapPending = false;
    }

    private void EraseLine(int row, int start, int end)
    {
        var line = _screen[row];
        for (var i = Math.Max(0, start); i < Math.Min(Columns, end); i++) line[i] = ErasedCell();
    }

    private void RepeatLastChar(int count)
    {
        if (_lastChar.Length == 0) return;
        for (var i = 0; i < count; i++) Put(_lastChar);
    }

    private static bool IsWide(string glyph)
    {
        var codepoint = char.ConvertToUtf32(glyph, 0);
        return codepoint >= 0x1100 && (codepoint <= 0x115F || codepoint is >= 0x2E80 and <= 0xA4CF
            || codepoint is >= 0xAC00 and <= 0xD7A3 || codepoint is >= 0xF900 and <= 0xFAFF
            || codepoint is >= 0xFE10 and <= 0xFE6F || codepoint is >= 0xFF00 and <= 0xFF60
            || codepoint >= 0x1F300);
    }
    private void ApplyColors(int[] values)
    {
        for (var i = 0; i < values.Length; i++)
        {
            var value = values[i];
            if (value == 0) ResetAttributes();
            else if (value == 1) _bold = true;
            else if (value == 22) { _bold = false; _dim = false; }
            else if (value == 2) _dim = true;
            else if (value == 3) _italic = true;
            else if (value == 23) _italic = false;
            else if (value == 4) _underline = true;
            else if (value == 21) _underline = true; // double underline: rendered as underline
            else if (value == 24) _underline = false;
            else if (value == 5 || value == 6) { } // blink: no animation for text, cursor blink is separate
            else if (value == 7) _reverse = true;
            else if (value == 27) _reverse = false;
            else if (value == 8) _conceal = true;
            else if (value == 28) _conceal = false;
            else if (value == 9) _strike = true;
            else if (value == 29) _strike = false;
            else if (value == 39) _foreground = DefaultForeground;
            else if (value == 49) _background = DefaultBackground;
            else if (value is >= 30 and <= 37) _foreground = _palette[value - 30];
            else if (value is >= 90 and <= 97) _foreground = _palette[value - 90 + 8];
            else if (value is >= 40 and <= 47) _background = _palette[value - 40];
            else if (value is >= 100 and <= 107) _background = _palette[value - 100 + 8];
            else if ((value == 38 || value == 48) && i + 2 < values.Length)
            {
                int color;
                if (values[i + 1] == 2 && i + 4 < values.Length)
                {
                    color = (Math.Clamp(values[i + 2], 0, 255) << 16) | (Math.Clamp(values[i + 3], 0, 255) << 8) | Math.Clamp(values[i + 4], 0, 255); i += 4;
                }
                else if (values[i + 1] == 5) { color = PaletteColor(Math.Clamp(values[i + 2], 0, 255)); i += 2; }
                else continue;
                if (value == 38) _foreground = color; else _background = color;
            }
        }
    }
    private int PaletteColor(int index)
    {
        if (index < 16) return _palette[index];
        if (index >= 232) { var grey = 8 + (index - 232) * 10; return (grey << 16) | (grey << 8) | grey; }
        var cube = index - 16;
        int Channel(int coordinate) => coordinate == 0 ? 0 : coordinate * 40 + 55;
        return (Channel(cube / 36) << 16) | (Channel((cube / 6) % 6) << 8) | Channel(cube % 6);
    }
}
