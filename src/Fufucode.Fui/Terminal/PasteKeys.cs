using System.Text;

namespace Fufucode.Fui;

// Decode bracketed paste before converting VT function keys; native ConsoleKey events retain host modifiers.
// A pasted CR, LF, escape sequence or function key is never handed to the command dispatcher as a key press.
internal sealed class PasteKeys
{
    private const string Begin = "\x1b[200~", End = "\x1b[201~";
    private readonly List<ConsoleKeyInfo> _candidate = new(6);
    private readonly Queue<(ConsoleKeyInfo Key, string? Text)> _ready = new();
    private readonly StringBuilder _vt = new();
    private long _vtSince;
    private int _matched;
    private long _since;
    private StringBuilder? _paste;
    private string _literal = "";
    private int _offset;

    internal void Feed(ConsoleKeyInfo key, long now)
    {
        _vtSince = now;
        var expected = _paste is null ? Begin : End;
        var c = key.Key == ConsoleKey.Escape ? '\x1b' : key.KeyChar;
        // Some Unix Console implementations return the initial ESC [ as one Alt+[ event.
        var altCsi = c == '[' && key.Modifiers == ConsoleModifiers.Alt;
        var count = altCsi ? 2 : 1;
        var match = altCsi ? _matched == 0 : c == expected[_matched];
        if (match)
        {
            _candidate.Add(key);
            _since = now;
            _matched += count;
            if (_matched < expected.Length) return;
            _matched = 0;
            _candidate.Clear();
            if (_paste is null) _paste = new();
            else
            {
                var text = TerminalText.Clean(_paste.ToString(), 0, out _, expandTabs: false);
                if (text.Length != 0) _ready.Enqueue((default, text));
                _paste = null;
            }
            return;
        }

        if (_matched > 0) { FlushCandidate(); Feed(key, now); }
        else Release(key);
    }

    internal void FlushExpired(long now)
    {
        if (_matched > 0 && _paste is null && now - _since >= 50) FlushCandidate();
        if (_vt.Length > 0 && now - _vtSince >= 50)
        {
            if (_vt.Length == 1) _ready.Enqueue((new('\x1b', ConsoleKey.Escape, false, false, false), null));
            _vt.Clear();
        }
    }

    private void FlushCandidate()
    {
        foreach (var key in _candidate) Release(key);
        _candidate.Clear();
        _matched = 0;
    }

    private void Release(ConsoleKeyInfo key)
    {
        if (_paste is null) DecodeKey(key);
        else if (key.KeyChar != '\0') _paste.Append(key.KeyChar);
        else if (key.Key == ConsoleKey.Enter) _paste.Append('\r');
        else if (key.Key == ConsoleKey.Tab) _paste.Append('\t');
    }

    private void DecodeKey(ConsoleKeyInfo key)
    {
        if (key.Key != 0)
        {
            _vt.Clear();
            _ready.Enqueue((key, null));
            return;
        }
        var c = key.KeyChar;
        if (c == '\x1b') { _vt.Clear().Append(c); return; }
        if (_vt.Length == 0) { _ready.Enqueue((CharacterKey(c, alt: false), null)); return; }
        if (_vt.Length == 1)
        {
            if (c is '[' or 'O') { _vt.Append(c); return; }
            _vt.Clear();
            _ready.Enqueue((CharacterKey(c, alt: true), null));
            return;
        }
        if (c is >= ' ' and <= '?') { if (_vt.Length < 32) _vt.Append(c); return; }
        var parameters = _vt.ToString(2, _vt.Length - 2);
        _vt.Clear();
        // A terminal response or an unknown CSI is not a shortcut (especially not approval).
        var args = parameters.Split(';');
        if (args.Length > 2 || (args[0].Length != 0 && !int.TryParse(args[0], out _))) return;
        var modifier = 1;
        if (args.Length > 1 && (!int.TryParse(args[1], out modifier) || modifier is < 1 or > 8)) return;
        var number = args[0].Length == 0 ? 0 : int.Parse(args[0]);
        ConsoleKey? code = c switch
        {
            'A' => ConsoleKey.UpArrow, 'B' => ConsoleKey.DownArrow, 'C' => ConsoleKey.RightArrow, 'D' => ConsoleKey.LeftArrow,
            'H' => ConsoleKey.Home, 'F' => ConsoleKey.End, 'P' => ConsoleKey.F1, 'Q' => ConsoleKey.F2, 'R' => ConsoleKey.F3,
            '~' => number switch { 1 or 7 => ConsoleKey.Home, 3 => ConsoleKey.Delete, 4 or 8 => ConsoleKey.End,
                5 => ConsoleKey.PageUp, 6 => ConsoleKey.PageDown, 11 => ConsoleKey.F1, 12 => ConsoleKey.F2, 13 => ConsoleKey.F3, _ => null },
            _ => null
        };
        if (code is null || (c != '~' && number > 1)) return;
        _ready.Enqueue((new('\0', code.Value, ((modifier - 1) & 1) != 0, ((modifier - 1) & 2) != 0, ((modifier - 1) & 4) != 0), null));
    }

    private static ConsoleKeyInfo CharacterKey(char c, bool alt)
    {
        var key = c switch { '\r' => ConsoleKey.Enter, '\t' => ConsoleKey.Tab, '\b' or '\x7f' => ConsoleKey.Backspace,
            >= '\u0001' and <= '\u001a' => ConsoleKey.A + (c - 1), >= 'a' and <= 'z' => ConsoleKey.A + (c - 'a'),
            >= 'A' and <= 'Z' => ConsoleKey.A + (c - 'A'), _ => (ConsoleKey)0 };
        return new(c, key, c is >= 'A' and <= 'Z', alt, c is >= '\u0001' and <= '\u001a' && c is not ('\r' or '\t' or '\b'));
    }

    internal ConsoleKeyInfo? Read()
    {
        if (_offset < _literal.Length) return Literal(_literal[_offset++]);
        if (!_ready.TryDequeue(out var item)) { _literal = ""; _offset = 0; return null; }
        if (item.Text is null) return item.Key;
        _literal = item.Text;
        _offset = 1;
        return Literal(_literal[0]);
    }

    private static ConsoleKeyInfo Literal(char c) => new(c, (ConsoleKey)0, false, false, false);
}
