using System.Globalization;
using System.Text;

namespace Fufucode.Tui;

internal sealed class Composer
{
    private readonly StringBuilder _buffer = new();
    private readonly List<string> _history = [];
    private string? _snapshot = "";
    private int[]? _boundaries;
    private int _historyIndex;
    private (string Text, int Cursor) _unfinished;
    private int? _preferredColumn;

    public string Text => _snapshot ??= _buffer.ToString();
    public int Cursor { get; private set; }

    public void Edit(ConsoleKeyInfo key)
    {
        var control = key.Modifiers.HasFlag(ConsoleModifiers.Control) && !key.Modifiers.HasFlag(ConsoleModifiers.Alt);
        if (key.Key is not (ConsoleKey.UpArrow or ConsoleKey.DownArrow)) _preferredColumn = null;
        switch (key.Key)
        {
            case ConsoleKey.LeftArrow: Cursor = control ? WordLeft() : Previous(Cursor); return;
            case ConsoleKey.RightArrow: Cursor = control ? WordRight() : Next(Cursor); return;
            case ConsoleKey.Home: Cursor = control ? 0 : LineStart(); return;
            case ConsoleKey.End: Cursor = control ? _buffer.Length : LineEnd(); return;
            case ConsoleKey.UpArrow: Vertical(-1); return;
            case ConsoleKey.DownArrow: Vertical(1); return;
            case ConsoleKey.Backspace: Remove(control ? WordLeft() : Previous(Cursor), Cursor); return;
            case ConsoleKey.Delete: Remove(Cursor, control ? WordRight() : Next(Cursor)); return;
            case ConsoleKey.J when control:
            case ConsoleKey.Enter when key.Modifiers != 0: Insert('\n'); return;
            case ConsoleKey.A when control: Cursor = LineStart(); return;
            case ConsoleKey.E when control: Cursor = LineEnd(); return;
        }

        // Key 0 is literal paste/IME input, never a command. VT paste newlines do not submit.
        if (key.Key == 0 && key.KeyChar is '\n' or '\t') Insert(key.KeyChar);
        else if (!control && !char.IsControl(key.KeyChar)) Insert(key.KeyChar);
    }

    public string Take()
    {
        var text = Text;
        if (!string.IsNullOrWhiteSpace(text) && (_history.Count == 0 || _history[^1] != text))
        {
            if (_history.Count == 100) _history.RemoveAt(0);
            _history.Add(text);
        }
        Clear();
        return text;
    }

    public void Clear()
    {
        Set("", 0);
        _historyIndex = _history.Count;
        _unfinished = ("", 0);
    }

    private void Set(string text, int cursor)
    {
        _buffer.Clear().Append(text);
        _snapshot = text;
        _boundaries = null;
        Cursor = cursor;
        _preferredColumn = null;
    }

    private void Insert(char value)
    {
        _buffer.Insert(Cursor++, value);
        Changed();
        // Inserting a base before existing combining marks can create a new cluster across the caret.
        if (Cursor < _buffer.Length && !char.IsHighSurrogate(value)) Cursor = Ceiling(Cursor);
    }

    private void Remove(int start, int end)
    {
        if (end <= start) return;
        _buffer.Remove(start, end - start);
        Changed();
        Cursor = Ceiling(start);
    }

    private void Changed()
    {
        _snapshot = null;
        _boundaries = null;
        _historyIndex = _history.Count;
    }

    private int[] Boundaries => _boundaries ??= StringInfo.ParseCombiningCharacters(Text);

    private int Previous(int position)
    {
        if (position <= 0) return 0;
        var i = Array.BinarySearch(Boundaries, position);
        return Boundaries[Math.Max(0, (i < 0 ? ~i : i) - 1)];
    }

    private int Ceiling(int position)
    {
        if (position >= _buffer.Length) return _buffer.Length;
        var i = Array.BinarySearch(Boundaries, position);
        if (i >= 0) return position;
        return ~i < Boundaries.Length ? Boundaries[~i] : _buffer.Length;
    }

    private int Next(int position) => position >= _buffer.Length ? _buffer.Length : Ceiling(position + 1);

    private int WordLeft()
    {
        var i = Cursor;
        while (i > 0 && char.IsWhiteSpace(_buffer[Previous(i)])) i = Previous(i);
        if (i == 0) return 0;
        var letters = char.IsLetterOrDigit(_buffer[Previous(i)]) || _buffer[Previous(i)] == '_';
        while (i > 0)
        {
            var previous = Previous(i);
            var c = _buffer[previous];
            if (char.IsWhiteSpace(c) || (char.IsLetterOrDigit(c) || c == '_') != letters) break;
            i = previous;
        }
        return i;
    }

    private int WordRight()
    {
        var i = Cursor;
        if (i == _buffer.Length) return i;
        var letters = char.IsLetterOrDigit(_buffer[i]) || _buffer[i] == '_';
        while (i < _buffer.Length && !char.IsWhiteSpace(_buffer[i]) &&
            (char.IsLetterOrDigit(_buffer[i]) || _buffer[i] == '_') == letters) i = Next(i);
        while (i < _buffer.Length && char.IsWhiteSpace(_buffer[i])) i = Next(i);
        return i;
    }

    private int LineStart() => Cursor == 0 ? 0 : Text.LastIndexOf('\n', Cursor - 1) + 1;
    private int LineEnd()
    {
        var next = Text.IndexOf('\n', Cursor);
        return next < 0 ? _buffer.Length : next;
    }

    private void Vertical(int direction)
    {
        var start = LineStart();
        var end = LineEnd();
        if ((direction < 0 && start > 0) || (direction > 0 && end < _buffer.Length))
        {
            _preferredColumn ??= TerminalText.Width(Text.AsSpan(start, Cursor - start));
            var lineStart = direction < 0 ? (start == 1 ? 0 : Text.LastIndexOf('\n', start - 2) + 1) : end + 1;
            var lineEnd = direction < 0 ? start - 1 : Text.IndexOf('\n', lineStart);
            if (lineEnd < 0) lineEnd = _buffer.Length;
            Cursor = lineStart + TerminalText.IndexAtColumn(Text.AsSpan(lineStart, lineEnd - lineStart), _preferredColumn.Value);
            return;
        }

        var index = Math.Clamp(_historyIndex + direction, 0, _history.Count);
        if (index == _historyIndex) return;
        if (_historyIndex == _history.Count) _unfinished = (Text, Cursor);
        _historyIndex = index;
        if (index == _history.Count) Set(_unfinished.Text, _unfinished.Cursor);
        else Set(_history[index], _history[index].Length);
    }
}
