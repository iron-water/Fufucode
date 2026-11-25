namespace Fufucode.Fui;

internal enum VimEditorMode
{
    Insert,
    Normal
}

internal sealed partial class Composer
{
    private bool _vimEnabled;
    private VimEditorMode _vimMode = VimEditorMode.Insert;
    private char? _vimPending;

    public bool VimEnabled => _vimEnabled;
    public VimEditorMode VimMode => _vimMode;

    public void SetVimEnabled(bool enabled)
    {
        _vimEnabled = enabled;
        _vimMode = VimEditorMode.Insert;
        _vimPending = null;
        BreakEditGroup();
    }

    // This is intentionally a small Vim subset: Normal mode owns every ordinary key
    // so Enter, Tab, or an unknown character can never submit or edit by accident.
    public bool HandleVimKey(ConsoleKeyInfo key, int pageRows = 8)
    {
        if (!_vimEnabled) return false;
        if (_vimMode == VimEditorMode.Insert)
        {
            if (key.Key != ConsoleKey.Escape || key.Modifiers != 0) return false;
            _vimMode = VimEditorMode.Normal;
            _vimPending = null;
            BreakEditGroup();
            ClearSelection();
            return true;
        }

        if (key.Modifiers.HasFlag(ConsoleModifiers.Control) || key.Modifiers.HasFlag(ConsoleModifiers.Alt))
            return false;
        if (key.Key is ConsoleKey.F1 or ConsoleKey.PageUp or ConsoleKey.PageDown) return false;

        if (key.Key == ConsoleKey.Escape)
        {
            _vimPending = null;
            ClearSelection();
            return true;
        }

        if (_vimPending is { } pending)
        {
            _vimPending = null;
            if (pending == 'd' && key.KeyChar == 'd') DeleteVimLine();
            else if (pending == 'y' && key.KeyChar == 'y') YankVimLine();
            return true;
        }

        switch (key.KeyChar)
        {
            case 'h':
                SetCursor(Math.Max(LineStart(), Previous(Cursor)));
                return true;
            case 'l':
                SetCursor(Math.Min(LineEnd(), Next(Cursor)));
                return true;
            case 'j':
                MoveVimLine(1);
                return true;
            case 'k':
                MoveVimLine(-1);
                return true;
            case '0':
                SetCursor(LineStart());
                return true;
            case '$':
                SetCursor(LineEnd());
                return true;
            case 'w':
                SetCursor(WordRight());
                return true;
            case 'b':
                SetCursor(WordLeft());
                return true;
            case 'x':
                DeleteVimCharacter();
                return true;
            case 'd':
                _vimPending = 'd';
                return true;
            case 'y':
                _vimPending = 'y';
                return true;
            case 'p':
                PasteVimRegister();
                return true;
            case 'u':
                Undo();
                return true;
            case 'i':
                EnterVimInsert(moveRight: false);
                return true;
            case 'a':
                EnterVimInsert(moveRight: Cursor < LineEnd());
                return true;
            case 'o':
                SetCursor(LineEnd());
                BeginCommand();
                InsertValue("\n");
                EnterVimInsert(moveRight: false);
                return true;
            default:
                // Consume every unrecognized Normal key, including Enter and Tab.
                return true;
        }
    }

    private void EnterVimInsert(bool moveRight)
    {
        if (moveRight) SetCursor(Next(Cursor));
        _vimMode = VimEditorMode.Insert;
        _vimPending = null;
        ClearSelection();
        BreakEditGroup();
    }

    private void MoveVimLine(int direction)
    {
        var start = LineStart();
        var end = LineEnd();
        if (direction < 0 && start == 0 || direction > 0 && end == _buffer.Length) return;
        var column = TerminalText.Width(Text.AsSpan(start, Cursor - start));
        var targetStart = direction < 0
            ? start == 1 ? 0 : Text.LastIndexOf('\n', start - 2) + 1
            : end + 1;
        var targetEnd = direction < 0 ? start - 1 : Text.IndexOf('\n', targetStart);
        if (targetEnd < 0) targetEnd = _buffer.Length;
        SetCursor(targetStart + TerminalText.IndexAtColumn(
            Text.AsSpan(targetStart, targetEnd - targetStart), column));
    }

    private void DeleteVimCharacter()
    {
        var start = Cursor;
        var end = Cursor >= LineEnd() ? Cursor : Next(start);
        if (end <= start && start > LineStart())
        {
            end = start;
            start = Previous(start);
        }
        if (end <= start) return;
        var text = Text[start..end];
        BeginCommand();
        Remove(start, end);
        AddKill(text, KillDirection.Forward, accumulate: false);
    }

    private void DeleteVimLine()
    {
        var start = LineStart();
        var end = LineEnd();
        if (end < _buffer.Length) end++;
        else if (start > 0 && end == start)
        {
            start--;
        }
        if (end <= start) return;
        var text = Text[start..end];
        BeginCommand();
        Remove(start, end);
        AddKill(text, KillDirection.Forward, accumulate: false);
    }

    private void YankVimLine()
    {
        var start = LineStart();
        var end = LineEnd();
        if (end < _buffer.Length) end++;
        AddKill(Text[start..end], KillDirection.Forward, accumulate: false);
        _killDirection = null;
        BreakEditGroup();
    }

    private void PasteVimRegister()
    {
        if (_killRing.Count == 0) return;
        BeginCommand();
        InsertValue(_killRing[^1]);
    }
}
