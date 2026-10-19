using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Fufucode.App.Infrastructure;

public readonly record struct TerminalCell(string Text, int Foreground, int Background, bool Bold = false);

public sealed record TerminalSnapshot(
    TerminalCell[][] Lines,
    int CursorColumn,
    int CursorLine,
    bool CursorVisible,
    bool AlternateScreen = false);

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
    private const int ScrollbackLimit = 1500;

    // Active screen. While an alternate screen runs, the primary screen lives in _mainScreen/_mainHistory.
    private List<TerminalCell[]> _screen = new();
    private List<TerminalCell[]> _history = new();
    private List<TerminalCell[]> _mainScreen = new();
    private List<TerminalCell[]> _mainHistory = new();

    private readonly StringBuilder _sequence = new();
    private readonly Queue<string> _replies = new();
    private int _parseState;                // 0 ground, 1 ESC, 2 CSI, 3 OSC, 4 OSC-after-ESC
    private int _column;                    // 0-based; stays on the last column while a wrap is pending
    private int _row;                       // 0-based
    private int _savedColumn, _savedRow;
    private int _top, _bottom;              // inclusive scroll region, 0-based
    private int _foreground = DefaultForeground, _background = DefaultBackground;
    private bool _bold, _cursorVisible = true, _wrapPending, _autoWrap = true, _originMode, _insertMode, _newLineMode;
    private char? _pendingSurrogate;
    private string _lastChar = string.Empty;

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
    public event Action? Changed;

    /// <summary>
    /// Reply bytes requested by DSR/DA (for example <c>CSI 6n</c>). The session must write them back to the
    /// shell: prompt frameworks and full-screen applications wait for the answer.
    /// </summary>
    public string TakePendingResponse()
    {
        if (_replies.Count == 0) return string.Empty;
        var builder = new StringBuilder();
        while (_replies.TryDequeue(out var reply)) builder.Append(reply);
        return builder.ToString();
    }

    public void Append(string text)
    {
        if (text.Length == 0) return;
        foreach (var character in text)
        {
            switch (_parseState)
            {
                case 3: // OSC: discard until BEL or ESC-backslash.
                    if (character == '\a') _parseState = 0;
                    else if (character == '\u001b') _parseState = 4;
                    continue;
                case 4:
                    _parseState = character == '\\' ? 0 : 3;
                    continue;
                case 1:
                    if (character == '[') { _parseState = 2; _sequence.Clear(); continue; }
                    if (character == ']') { _parseState = 3; continue; }
                    ApplyEscape(character);
                    _parseState = 0;
                    continue;
                case 2:
                    if (character >= '@' && character <= '~')
                    {
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

    private static void ResizeScreen(List<TerminalCell[]> screen, List<TerminalCell[]> history, int columns, int rows)
    {
        for (var i = 0; i < screen.Count; i++)
        {
            var line = screen[i];
            if (line.Length == columns) continue;
            if (columns < line.Length)
            {
                // Trailing blank cells go first so shell history keeps its text.
                var end = line.Length;
                while (end > columns && IsBlank(line[end - 1])) end--;
                var keep = Math.Max(columns, end);
                if (keep < line.Length) line = line[..keep];
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
            if (history.Count > ScrollbackLimit) history.RemoveRange(0, history.Count - ScrollbackLimit);
        }
        while (screen.Count < rows) screen.Add(BlankLine(columns));
    }

    // Scrollback rows stop changing when they leave the screen; only the live screen needs copies.
    public TerminalSnapshot Snapshot() => new(
        _history.Concat(_screen.Select(line => line.ToArray())).ToArray(),
        _column,
        _history.Count + _row,
        _cursorVisible,
        AlternateScreen);

    public string PlainText => string.Join("\n", _history.Concat(_screen).Select(line => string.Concat(line.Select(cell => cell.Text)).TrimEnd())).TrimEnd();

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
        _screen[_row][_column] = new TerminalCell(glyph, _foreground, _background, _bold);
        if (wide && _column + 1 < Columns) _screen[_row][_column + 1] = new TerminalCell(string.Empty, _foreground, _background, _bold);
        _lastChar = glyph;
        _column += wide ? 2 : 1;
        if (_column >= Columns)
        {
            _column = Columns - 1;
            if (_autoWrap) _wrapPending = true;
        }
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
    private TerminalCell ErasedCell() => new(" ", _foreground, _background);

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
                if (_history.Count > ScrollbackLimit) _history.RemoveRange(0, _history.Count - ScrollbackLimit);
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
        if (_row > _bottom) return;
        count = Math.Clamp(count, 0, _bottom - _row + 1);
        for (var n = 0; n < count; n++)
        {
            _screen.RemoveAt(_bottom);
            _screen.Insert(_row, BlankLine());
        }
    }

    private void DeleteLines(int count)
    {
        if (_row > _bottom) return;
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
        }
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
        for (var i = 0; i < Rows; i++) _screen[i] = BlankLine();
        _top = 0; _bottom = Rows - 1; _row = 0; _column = 0; _wrapPending = false;
    }

    private void ApplyCsi(string sequence, char command)
    {
        if (sequence.StartsWith('?')) { ApplyPrivate(sequence, command); return; }
        if (sequence.StartsWith('>')) { if (command == 'c') _replies.Enqueue("\u001b[>0;276;0c"); return; }

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
            case 'd': _row = Math.Clamp(Value(0) - 1, 0, Rows - 1); _wrapPending = false; break;
            case 'H': case 'f':
                _row = Math.Clamp(Value(0) - 1, 0, Rows - 1);
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
                if (values.Length > 0 && values[0] == 5) _replies.Enqueue("\u001b[0n");
                else if (values.Length > 0 && values[0] == 6) _replies.Enqueue($"\u001b[{_row + 1};{_column + 1}R");
                break;
            case 'c':
                if (values.Length == 0 || values[0] == 0) _replies.Enqueue("\u001b[?1;2c");
                break;
        }
    }

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
                    else LeaveAlternateScreen();
                    break;
                // 1/3/12 (cursor keys, blink), 1000-1006/1015 (mouse), 2004 (bracketed paste), 1004 (focus):
                // they change input behaviour, not the rendered screen, so the screen state stays untouched.
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

    private static bool IsBlank(TerminalCell cell) => string.IsNullOrWhiteSpace(cell.Text) && cell.Background == DefaultBackground;

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
            if (value == 0) { _foreground = DefaultForeground; _background = DefaultBackground; _bold = false; }
            else if (value == 1) _bold = true;
            else if (value == 22) _bold = false;
            else if (value == 39) _foreground = DefaultForeground;
            else if (value == 49) _background = DefaultBackground;
            else if (value is >= 30 and <= 37) _foreground = Palette[value - 30];
            else if (value is >= 90 and <= 97) _foreground = Palette[value - 90 + 8];
            else if (value is >= 40 and <= 47) _background = Palette[value - 40];
            else if (value is >= 100 and <= 107) _background = Palette[value - 100 + 8];
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
    private static int PaletteColor(int index)
    {
        if (index < 16) return Palette[index];
        if (index >= 232) { var grey = 8 + (index - 232) * 10; return (grey << 16) | (grey << 8) | grey; }
        var cube = index - 16;
        int Channel(int coordinate) => coordinate == 0 ? 0 : coordinate * 40 + 55;
        return (Channel(cube / 36) << 16) | (Channel((cube / 6) % 6) << 8) | Channel(cube % 6);
    }
}
