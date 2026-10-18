using System.Collections.Generic;

namespace Fufucode.Plugins.Terminal.Features.TerminalExperience;

/// <summary>
/// Bounded, per-tab command history for the command draft box.
/// Only commands the user explicitly submitted are recorded; the list lives in memory for the
/// lifetime of one tab (never persisted, never shared across tabs or projects) and holds at most
/// <see cref="Capacity"/> entries. Browsing keeps the unsent draft so pressing Down returns to it.
/// </summary>
public sealed class TerminalCommandHistory
{
    /// <summary>Upper bound of remembered commands; the oldest entry is dropped past this.</summary>
    public const int Capacity = 100;

    private readonly List<string> _entries = new();
    private int _cursor = -1;   // -1 == the live draft, otherwise an index into _entries
    private string _draft = string.Empty;

    public IReadOnlyList<string> Entries => _entries;
    public bool IsBrowsing => _cursor >= 0;

    /// <summary>Record one explicitly submitted non-empty command. A repeat of the newest entry is collapsed.</summary>
    public void Record(string? command)
    {
        var trimmed = command?.Trim() ?? string.Empty;
        Reset();
        if (trimmed.Length == 0) return;
        if (_entries.Count > 0 && _entries[^1] == trimmed) return;
        _entries.Add(trimmed);
        if (_entries.Count > Capacity) _entries.RemoveRange(0, _entries.Count - Capacity);
    }

    /// <summary>Return to the draft without touching the entries.</summary>
    public void Reset()
    {
        _cursor = -1;
        _draft = string.Empty;
    }

    /// <summary>
    /// Step to the older entry. The first call captures <paramref name="currentDraft"/> so <see cref="Next"/>
    /// can restore it. Returns null when there is no history at all; at the oldest entry it stays put.
    /// </summary>
    public string? Previous(string? currentDraft)
    {
        if (_entries.Count == 0) return null;
        if (_cursor < 0) { _draft = currentDraft ?? string.Empty; _cursor = _entries.Count - 1; }
        else if (_cursor > 0) _cursor--;
        return _entries[_cursor];
    }

    /// <summary>Step to the newer entry, finally restoring the draft captured by <see cref="Previous"/>.</summary>
    public string? Next()
    {
        if (_entries.Count == 0 || _cursor < 0) return null;
        if (_cursor >= _entries.Count - 1) { _cursor = -1; return _draft; }
        _cursor++;
        return _entries[_cursor];
    }
}
