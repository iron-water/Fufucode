using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Fufucode.App.Infrastructure;

public readonly record struct TerminalCell(string Text, int Foreground, int Background, bool Bold = false);
public sealed record TerminalSnapshot(TerminalCell[][] Lines, int CursorColumn, int CursorLine, bool CursorVisible);

/// <summary>Bounded VT screen plus scrollback. Basic shell escape sequences are interpreted, never shown as raw control text.</summary>
public sealed class TerminalBuffer
{
    public const int DefaultForeground = 0xDEE3EC;
    public const int DefaultBackground = 0x14171D;
    private static readonly int[] Palette = { 0x20242C, 0xE06C75, 0x98C379, 0xE5C07B, 0x61AFEF, 0xC678DD, 0x56B6C2, 0xD8DEE9, 0x6F7785, 0xFF8590, 0xB4E88D, 0xFFDA91, 0x85C9FF, 0xE7A0FF, 0x80DFE8, 0xFFFFFF };
    private readonly List<TerminalCell[]> _screen = new();
    private readonly List<TerminalCell[]> _history = new();
    private readonly StringBuilder _sequence = new();
    private int _parseState, _column, _row, _savedColumn, _savedRow;
    private int _foreground = DefaultForeground, _background = DefaultBackground;
    private bool _bold, _cursorVisible = true;
    private char? _pendingSurrogate;

    public TerminalBuffer(int columns = 100, int rows = 12)
    {
        Columns = Math.Clamp(columns, 10, 400);
        Rows = Math.Clamp(rows, 3, 160);
        for (var i = 0; i < Rows; i++) _screen.Add(BlankLine());
    }

    public int Columns { get; private set; }
    public int Rows { get; private set; }
    public event Action? Changed;

    public void Append(string text)
    {
        foreach (var character in text)
        {
            if (_parseState == 3) // OSC: ignore until BEL or ESC-backslash.
            {
                if (character == '\a') _parseState = 0;
                else if (character == '\u001b') _parseState = 4;
                continue;
            }
            if (_parseState == 4) { _parseState = character == '\\' ? 0 : 3; continue; }
            if (_parseState == 1)
            {
                _parseState = character switch { '[' => 2, ']' => 3, _ => 0 };
                _sequence.Clear();
                if (character == '7') { _savedColumn = _column; _savedRow = _row; }
                if (character == '8') { _column = Math.Clamp(_savedColumn, 0, Columns - 1); _row = Math.Clamp(_savedRow, 0, Rows - 1); }
                continue;
            }
            if (_parseState == 2)
            {
                if (character >= '@' && character <= '~')
                {
                    ApplyCsi(_sequence.ToString(), character);
                    _sequence.Clear(); _parseState = 0;
                }
                else if (_sequence.Length < 128) _sequence.Append(character);
                else { _sequence.Clear(); _parseState = 0; }
                continue;
            }
            if (character == '\u001b') { _parseState = 1; continue; }
            switch (character)
            {
                case '\r': _column = 0; continue;
                case '\n': NewLine(); continue;
                case '\b': _column = Math.Max(0, _column - 1); continue;
                case '\t': _column = Math.Min(Columns - 1, ((_column / 8) + 1) * 8); continue;
            }
            if (char.IsControl(character)) continue;
            if (char.IsHighSurrogate(character)) { _pendingSurrogate = character; continue; }
            var value = _pendingSurrogate is char high && char.IsLowSurrogate(character)
                ? new string(new[] { high, character }) : character.ToString();
            _pendingSurrogate = null;
            Put(value);
        }
        Changed?.Invoke();
    }

    public void Resize(int columns, int rows)
    {
        columns = Math.Clamp(columns, 10, 400); rows = Math.Clamp(rows, 3, 160);
        if (columns == Columns && rows == Rows) return;
        var oldColumns = Columns; Columns = columns;
        for (var i = 0; i < _screen.Count; i++)
        {
            var line = BlankLine();
            Array.Copy(_screen[i], line, Math.Min(oldColumns, columns));
            _screen[i] = line;
        }
        while (_screen.Count > rows)
        {
            AddHistory(_screen[0]); _screen.RemoveAt(0); _row = Math.Max(0, _row - 1);
        }
        while (_screen.Count < rows) _screen.Add(BlankLine());
        Rows = rows; _column = Math.Clamp(_column, 0, columns - 1); _row = Math.Clamp(_row, 0, rows - 1);
        Changed?.Invoke();
    }

    // Scrollback rows stop changing when they leave the screen; only the live screen needs copies.
    public TerminalSnapshot Snapshot() => new(_history.Concat(_screen.Select(line => line.ToArray())).ToArray(), _column, _history.Count + _row, _cursorVisible);
    public string PlainText => string.Join("\n", _history.Concat(_screen).Select(line => string.Concat(line.Select(cell => cell.Text)).TrimEnd())).TrimEnd();

    private void Put(string text)
    {
        var codepoint = char.ConvertToUtf32(text, 0);
        var wide = codepoint >= 0x1100 && (codepoint <= 0x115F || codepoint is >= 0x2E80 and <= 0xA4CF || codepoint is >= 0xAC00 and <= 0xD7A3 || codepoint is >= 0xF900 and <= 0xFAFF || codepoint is >= 0xFE10 and <= 0xFE6F || codepoint is >= 0xFF00 and <= 0xFF60 || codepoint >= 0x1F300);
        if (_column >= Columns || (wide && _column >= Columns - 1)) { _column = 0; NewLine(); }
        _screen[_row][_column++] = new TerminalCell(text, _foreground, _background, _bold);
        if (wide && _column < Columns) _screen[_row][_column++] = new TerminalCell(string.Empty, _foreground, _background, _bold);
    }

    private void NewLine()
    {
        if (++_row < Rows) return;
        AddHistory(_screen[0]); _screen.RemoveAt(0); _screen.Add(BlankLine()); _row = Rows - 1;
    }
    private void AddHistory(TerminalCell[] line)
    {
        _history.Add(line);
        if (_history.Count > 1500) _history.RemoveRange(0, _history.Count - 1500);
    }
    private TerminalCell[] BlankLine() => Enumerable.Repeat(new TerminalCell(" ", DefaultForeground, DefaultBackground), Columns).ToArray();
    private TerminalCell ErasedCell() => new(" ", _foreground, _background);

    private void ApplyCsi(string sequence, char command)
    {
        if (sequence.StartsWith('?'))
        {
            if (sequence == "?25") _cursorVisible = command == 'h';
            // Alternate-screen modes and mouse tracking are intentionally outside the basic shell renderer.
            return;
        }
        var values = sequence.Split(';').Select(value => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : 0).ToArray();
        int Value(int index, int fallback = 1) => index >= values.Length || values[index] == 0 ? fallback : values[index];
        switch (command)
        {
            case 'A': _row = Math.Max(0, _row - Value(0)); break;
            case 'B': _row = Math.Min(Rows - 1, _row + Value(0)); break;
            case 'C': _column = Math.Min(Columns - 1, _column + Value(0)); break;
            case 'D': _column = Math.Max(0, _column - Value(0)); break;
            case 'E': _row = Math.Min(Rows - 1, _row + Value(0)); _column = 0; break;
            case 'F': _row = Math.Max(0, _row - Value(0)); _column = 0; break;
            case 'G': _column = Math.Clamp(Value(0) - 1, 0, Columns - 1); break;
            case 'H': case 'f': _row = Math.Clamp(Value(0) - 1, 0, Rows - 1); _column = Math.Clamp(Value(1) - 1, 0, Columns - 1); break;
            case 'J':
                if (values[0] == 3) _history.Clear();
                else if (values[0] == 2) { for (var i = 0; i < Rows; i++) _screen[i] = BlankLine(); }
                else if (values[0] == 0)
                {
                    EraseLine(_row, _column, Columns);
                    for (var i = _row + 1; i < Rows; i++) _screen[i] = BlankLine();
                }
                else if (values[0] == 1)
                {
                    for (var i = 0; i < _row; i++) _screen[i] = BlankLine();
                    EraseLine(_row, 0, Math.Min(Columns, _column + 1));
                }
                break;
            case 'K':
                EraseLine(_row, values[0] == 0 ? Math.Min(_column, Columns - 1) : 0, values[0] == 1 ? Math.Min(Columns, _column + 1) : Columns); break;
            case 's': _savedColumn = _column; _savedRow = _row; break;
            case 'u': _column = Math.Clamp(_savedColumn, 0, Columns - 1); _row = Math.Clamp(_savedRow, 0, Rows - 1); break;
            case 'm': ApplyColors(values); break;
        }
    }
    private void EraseLine(int row, int start, int end) { for (var i = start; i < end; i++) _screen[row][i] = ErasedCell(); }
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
