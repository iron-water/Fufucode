using System.Globalization;
using System.Text;

namespace Fufucode.Fui;

internal sealed partial class Composer
{
    private const int MaxUndoDepth = 100;
    private const int MaxKillEntries = 60;
    private readonly StringBuilder _buffer = new();
    private readonly List<string> _history = [];
    // Snapshots are taken before a group's first edit, so one typed or pasted run undoes together.
    private readonly List<EditSnapshot> _undo = [];
    private readonly List<EditSnapshot> _redo = [];
    private readonly List<string> _killRing = [];
    private string? _snapshot = "";
    private int[]? _boundaries;
    private int _selectionAnchor;
    private int _historyIndex;
    private (string Text, int Cursor) _unfinished;
    private int? _preferredColumn;
    private EditGroup _editGroup;
    private KillDirection? _killDirection;
    private EditSnapshot? _yankBase, _yankAfter;
    private int _yankIndex;

    public string Text => _snapshot ??= _buffer.ToString();
    public int Cursor { get; private set; }
    public long Revision { get; private set; }
    public bool HasSelection => _selectionAnchor != Cursor;
    public int SelectionStart => HasSelection ? Math.Min(Cursor, _selectionAnchor) : -1;
    public int SelectionEnd => HasSelection ? Math.Max(Cursor, _selectionAnchor) : -1;
    public string SelectedText => HasSelection ? Text[SelectionStart..SelectionEnd] : "";
    public string? ModeLabel => VimEnabled ? VimMode == VimEditorMode.Insert ? "INSERT" : "NORMAL" : null;
    private IReadOnlyList<string>? _historyView;
    public IReadOnlyList<string> History => _historyView ??= _history.AsReadOnly();

    public void Edit(ConsoleKeyInfo key, int pageRows = 8)
    {
        var control = key.Modifiers.HasFlag(ConsoleModifiers.Control) && !key.Modifiers.HasFlag(ConsoleModifiers.Alt);
        var alt = key.Modifiers.HasFlag(ConsoleModifiers.Alt);
        var shift = key.Modifiers.HasFlag(ConsoleModifiers.Shift);
        if (key.Key is not (ConsoleKey.UpArrow or ConsoleKey.DownArrow or ConsoleKey.PageUp or ConsoleKey.PageDown))
            _preferredColumn = null;
        switch (key.Key)
        {
            case ConsoleKey.Z when alt && !control: Redo(); return;
            case ConsoleKey.Z when control && shift: Redo(); return;
            case ConsoleKey.Z when control: Undo(); return;
            case ConsoleKey.Y when control: Yank(); return;
            case ConsoleKey.Y when alt && !control: YankPop(); return;
            case ConsoleKey.LeftArrow:
                MoveHorizontal(-1, control, shift);
                return;
            case ConsoleKey.RightArrow:
                MoveHorizontal(1, control, shift);
                return;
            case ConsoleKey.Home:
                MoveTo(control ? 0 : LineStart(), shift);
                return;
            case ConsoleKey.End:
                MoveTo(control ? _buffer.Length : LineEnd(), shift);
                return;
            case ConsoleKey.UpArrow:
                Vertical(-1, shift);
                return;
            case ConsoleKey.DownArrow:
                Vertical(1, shift);
                return;
            case ConsoleKey.PageUp:
                Page(-1, pageRows, shift);
                return;
            case ConsoleKey.PageDown:
                Page(1, pageRows, shift);
                return;
            case ConsoleKey.Backspace:
                if (HasSelection) DeleteSelection(addToKillRing: false);
                else DeleteRange(control ? WordLeft() : Previous(Cursor), Cursor);
                return;
            case ConsoleKey.Delete:
                if (HasSelection) DeleteSelection(addToKillRing: false);
                else DeleteRange(Cursor, control ? WordRight() : Next(Cursor));
                return;
            case ConsoleKey.J when control:
            case ConsoleKey.Enter when key.Modifiers != 0:
                Insert("\n", literal: false);
                return;
            case ConsoleKey.A when control:
                SelectAll();
                return;
            case ConsoleKey.E when control:
                MoveTo(LineEnd(), shift);
                return;
            case ConsoleKey.U when control:
                if (HasSelection) DeleteSelection(addToKillRing: true, KillDirection.Backward);
                else Kill(LineStart(), Cursor, KillDirection.Backward);
                return;
            case ConsoleKey.K when control:
                if (HasSelection) DeleteSelection(addToKillRing: true, KillDirection.Forward);
                else Kill(Cursor, LineEnd(), KillDirection.Forward);
                return;
            case ConsoleKey.W when control:
                if (HasSelection) DeleteSelection(addToKillRing: true, KillDirection.Backward);
                else Kill(WordLeft(), Cursor, KillDirection.Backward);
                return;
        }

        // Key 0 is literal paste/IME input, never a command. VT paste newlines do not submit.
        if (key.Key == 0 && key.KeyChar is '\n' or '\t') Insert(key.KeyChar.ToString(), literal: true);
        else if (!control && !char.IsControl(key.KeyChar)) Insert(key.KeyChar.ToString(), literal: key.Key == 0);
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
        _undo.Clear();
        _redo.Clear();
        BreakEditGroup();
    }

    // Replacing a draft is not a submission. Never leave a caret inside a surrogate pair or combining cluster.
    public void Replace(string text, int? cursor = null)
    {
        Set(text, cursor ?? text.Length);
        Cursor = Ceiling(Cursor);
        _historyIndex = _history.Count;
        _unfinished = ("", 0);
        _undo.Clear();
        _redo.Clear();
        BreakEditGroup();
    }

    public void InsertPastedText(string text)
    {
        if (text.Length == 0) return;
        BeginTyped(literal: true);
        InsertValue(text);
    }

    // Used by local completions. The replacement is one undo step even when it spans a selection.
    public void ReplaceRange(int start, int end, string text)
    {
        start = Ceiling(Math.Clamp(start, 0, _buffer.Length));
        end = Ceiling(Math.Clamp(end, start, _buffer.Length));
        if (start == end && text.Length == 0) return;
        BeginCommand();
        RemoveCore(start, end);
        InsertValue(text);
    }

    public void SetCursor(int position, bool extend = false)
    {
        position = Ceiling(Math.Clamp(position, 0, _buffer.Length));
        BreakEditGroup();
        if (extend)
        {
            if (!HasSelection) _selectionAnchor = Cursor;
        }
        else _selectionAnchor = position;
        Cursor = position;
    }

    public void SelectAll()
    {
        BreakEditGroup();
        _selectionAnchor = 0;
        Cursor = _buffer.Length;
    }

    public void ClearSelection()
    {
        if (HasSelection) BreakEditGroup();
        _selectionAnchor = Cursor;
    }

    public string? CutSelection()
    {
        if (!HasSelection) return null;
        var selected = SelectedText;
        DeleteSelection(addToKillRing: true, KillDirection.Forward);
        return selected;
    }

    private void Set(string text, int cursor)
    {
        Revision++;
        _buffer.Clear().Append(text);
        _snapshot = text;
        _boundaries = null;
        Cursor = Ceiling(Math.Clamp(cursor, 0, text.Length));
        _selectionAnchor = Cursor;
        _preferredColumn = null;
    }

    private void Insert(string value, bool literal)
    {
        if (value.Length == 0) return;
        BeginTyped(literal);
        InsertValue(value);
    }

    private void InsertValue(string value)
    {
        if (value.Length == 0) return;
        if (HasSelection)
        {
            var start = SelectionStart;
            var end = SelectionEnd;
            _buffer.Remove(start, end - start);
            Cursor = start;
            _selectionAnchor = start;
        }
        _buffer.Insert(Cursor, value);
        Cursor += value.Length;
        Changed();
        // A split surrogate or a base inserted before combining marks may leave the caret inside a cluster.
        if (Cursor < _buffer.Length && !char.IsHighSurrogate(value[^1])) Cursor = Ceiling(Cursor);
        _selectionAnchor = Cursor;
    }

    private void DeleteRange(int start, int end)
    {
        if (end <= start)
        {
            BreakEditGroup();
            return;
        }
        BeginCommand();
        Remove(start, end);
    }

    private void DeleteSelection(bool addToKillRing, KillDirection direction = KillDirection.Forward)
    {
        if (!HasSelection) return;
        var start = SelectionStart;
        var end = SelectionEnd;
        var text = Text[start..end];
        BeginCommand();
        Remove(start, end);
        if (addToKillRing) AddKill(text, direction, accumulate: false);
    }

    private void Remove(int start, int end)
    {
        if (end <= start) return;
        RemoveCore(start, end);
        Changed();
        Cursor = Ceiling(start);
        _selectionAnchor = Cursor;
    }

    private void RemoveCore(int start, int end)
    {
        if (end <= start) return;
        _buffer.Remove(start, end - start);
        Cursor = start;
        _selectionAnchor = start;
        _boundaries = null;
    }

    private void Kill(int start, int end, KillDirection direction)
    {
        if (end <= start)
        {
            BreakEditGroup();
            return;
        }
        var text = Text[start..end];
        var accumulate = _killDirection == direction;
        BeginCommand();
        Remove(start, end);
        AddKill(text, direction, accumulate);
    }

    private void AddKill(string text, KillDirection direction, bool accumulate)
    {
        if (text.Length == 0) return;
        if (accumulate && _killRing.Count != 0)
            _killRing[^1] = direction == KillDirection.Backward ? text + _killRing[^1] : _killRing[^1] + text;
        else
        {
            if (_killRing.Count == MaxKillEntries) _killRing.RemoveAt(0);
            _killRing.Add(text);
        }
        _killDirection = direction;
    }

    private void Yank()
    {
        if (_killRing.Count == 0)
        {
            BreakEditGroup();
            return;
        }
        _yankIndex = _killRing.Count - 1;
        _yankBase = CurrentSnapshot();
        PushUndo();
        _editGroup = EditGroup.Yank;
        _killDirection = null;
        InsertValue(_killRing[_yankIndex]);
        _yankAfter = CurrentSnapshot();
    }

    private void YankPop()
    {
        if (_editGroup != EditGroup.Yank || _yankBase is not { } baseState ||
            _yankAfter is not { } afterState || _killRing.Count <= 1 || !Matches(afterState))
        {
            BreakEditGroup();
            return;
        }

        PushUndo();
        ApplySnapshot(baseState);
        _yankIndex = _yankIndex == 0 ? _killRing.Count - 1 : _yankIndex - 1;
        InsertValue(_killRing[_yankIndex]);
        _editGroup = EditGroup.Yank;
        _killDirection = null;
        _yankAfter = CurrentSnapshot();
    }

    private void Undo()
    {
        BreakEditGroup();
        if (_undo.Count == 0) return;
        var snapshot = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        PushRedo(CurrentSnapshot());
        ApplySnapshot(snapshot);
    }

    private void Redo()
    {
        BreakEditGroup();
        if (_redo.Count == 0) return;
        var snapshot = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        PushUndo(CurrentSnapshot(), clearRedo: false);
        ApplySnapshot(snapshot);
    }

    private void BeginTyped(bool literal)
    {
        // PasteKeys emits a bracketed paste as a contiguous key-0 run.
        var group = literal ? EditGroup.Literal : EditGroup.Typing;
        if (_editGroup != group)
        {
            PushUndo();
            _editGroup = group;
        }
        _killDirection = null;
        ClearYank();
    }

    private void BeginCommand()
    {
        PushUndo();
        _editGroup = EditGroup.Command;
        _killDirection = null;
        ClearYank();
    }

    private void BreakEditGroup()
    {
        _editGroup = EditGroup.None;
        _killDirection = null;
        ClearYank();
    }

    private void ClearYank()
    {
        _yankBase = null;
        _yankAfter = null;
        _yankIndex = 0;
    }

    private void PushUndo()
    {
        PushUndo(CurrentSnapshot());
    }

    private void PushUndo(EditSnapshot snapshot, bool clearRedo = true)
    {
        if (_undo.Count == MaxUndoDepth) _undo.RemoveAt(0);
        _undo.Add(snapshot);
        if (clearRedo) _redo.Clear();
    }

    private void PushRedo(EditSnapshot snapshot)
    {
        if (_redo.Count == MaxUndoDepth) _redo.RemoveAt(0);
        _redo.Add(snapshot);
    }

    private EditSnapshot CurrentSnapshot() => new(Text, Cursor, _selectionAnchor);

    private bool Matches(EditSnapshot snapshot) =>
        Cursor == snapshot.Cursor && string.Equals(Text, snapshot.Text, StringComparison.Ordinal);

    private void ApplySnapshot(EditSnapshot snapshot)
    {
        Set(snapshot.Text, snapshot.Cursor);
        Cursor = Ceiling(Cursor);
        _selectionAnchor = Ceiling(Math.Clamp(snapshot.SelectionAnchor, 0, _buffer.Length));
        _historyIndex = _history.Count;
    }

    private void Changed()
    {
        Revision++;
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

    private void MoveHorizontal(int direction, bool word, bool extend)
    {
        if (!extend && HasSelection)
        {
            BreakEditGroup();
            Cursor = direction < 0 ? SelectionStart : SelectionEnd;
            _selectionAnchor = Cursor;
            _preferredColumn = null;
            return;
        }
        var next = word
            ? direction < 0 ? WordLeft() : WordRight()
            : direction < 0 ? Previous(Cursor) : Next(Cursor);
        MoveTo(next, extend);
    }

    private void MoveTo(int position, bool extend)
    {
        BreakEditGroup();
        position = Ceiling(Math.Clamp(position, 0, _buffer.Length));
        if (extend)
        {
            if (!HasSelection) _selectionAnchor = Cursor;
        }
        else _selectionAnchor = position;
        Cursor = position;
        _preferredColumn = null;
    }

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

    private void Vertical(int direction, bool extend)
    {
        BreakEditGroup();
        var start = LineStart();
        var end = LineEnd();
        if ((direction < 0 && start > 0) || (direction > 0 && end < _buffer.Length))
        {
            var hadSelection = HasSelection;
            var origin = Cursor;
            MoveVerticalText(direction);
            if (extend)
            {
                if (!hadSelection) _selectionAnchor = origin;
            }
            else _selectionAnchor = Cursor;
            return;
        }
        if (extend) return;

        var index = Math.Clamp(_historyIndex + direction, 0, _history.Count);
        if (index == _historyIndex) return;
        if (_historyIndex == _history.Count) _unfinished = (Text, Cursor);
        _undo.Clear();
        _redo.Clear();
        _historyIndex = index;
        if (index == _history.Count) Set(_unfinished.Text, _unfinished.Cursor);
        else Set(_history[index], _history[index].Length);
        Cursor = Ceiling(Cursor);
    }

    // Called by the controller only after Shift is known to be active.
    private void Page(int direction, int pageRows, bool extend)
    {
        if (!extend) return;
        BreakEditGroup();
        var hadSelection = HasSelection;
        var origin = Cursor;
        if (!hadSelection) _selectionAnchor = origin;
        _preferredColumn ??= TerminalText.Width(Text.AsSpan(LineStart(), Cursor - LineStart()));
        var remaining = Math.Clamp(pageRows, 1, 50);
        while (remaining-- > 0)
        {
            var start = LineStart();
            var end = LineEnd();
            if (direction < 0 && start == 0 || direction > 0 && end == _buffer.Length) break;
            MoveVerticalText(direction);
        }
    }

    private void MoveVerticalText(int direction)
    {
        var start = LineStart();
        var end = LineEnd();
        _preferredColumn ??= TerminalText.Width(Text.AsSpan(start, Cursor - start));
        var lineStart = direction < 0 ? (start == 1 ? 0 : Text.LastIndexOf('\n', start - 2) + 1) : end + 1;
        var lineEnd = direction < 0 ? start - 1 : Text.IndexOf('\n', lineStart);
        if (lineEnd < 0) lineEnd = _buffer.Length;
        var target = lineStart + TerminalText.IndexAtColumn(Text.AsSpan(lineStart, lineEnd - lineStart), _preferredColumn.Value);
        Cursor = Ceiling(target);
    }

    private readonly record struct EditSnapshot(string Text, int Cursor, int SelectionAnchor);

    private enum EditGroup
    {
        None,
        Typing,
        Literal,
        Command,
        Yank
    }

    private enum KillDirection
    {
        Backward,
        Forward
    }
}
