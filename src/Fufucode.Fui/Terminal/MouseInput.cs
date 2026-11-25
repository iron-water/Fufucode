namespace Fufucode.Fui;

internal enum MouseWheel
{
    None,
    Up,
    Down
}

// Column/row are zero-based frame coordinates, matching a rendered TerminalFrame.
internal readonly record struct MouseEvent(
    int Button, int Column, int Row, bool Release, MouseWheel Wheel, bool Motion, bool LeftClick);

internal enum MouseHitKind
{
    None,
    PickerItem,
    ApprovalAllow,
    ApprovalReject,
    Composer
}

internal readonly record struct MouseHit(MouseHitKind Kind, int Index = -1)
{
    internal static readonly MouseHit None = new(MouseHitKind.None);
    internal bool IsApproval => Kind is MouseHitKind.ApprovalAllow or MouseHitKind.ApprovalReject;
}

// The layout publishes only the regions that were actually painted. A stale or
// out-of-window coordinate therefore cannot become an approval or selection.
internal sealed record MouseRegions(
    int PickerTop = -1,
    int PickerStart = 0,
    int PickerVisible = 0,
    int PickerColumns = 0,
    int ApprovalRow = -1,
    int ApprovalAllowStart = -1,
    int ApprovalAllowEnd = -1,
    int ApprovalRejectStart = -1,
    int ApprovalRejectEnd = -1,
    int ComposerTop = -1,
    int ComposerRows = 0,
    int ComposerPromptWidth = 0,
    string[]? ComposerLines = null,
    int[]? ComposerStarts = null);

internal static class MouseInput
{
    internal const ConsoleKey RoutedKey = ConsoleKey.F24;
    private static readonly object PendingGate = new();
    private static MouseEvent? _pendingEvent;
    private static MouseHit _pendingHit = MouseHit.None;

    internal static bool TryParse(string sequence, out MouseEvent mouse)
    {
        mouse = default;
        if (sequence.Length < 7 || !sequence.StartsWith("\x1b[<", StringComparison.Ordinal) ||
            sequence[^1] is not ('M' or 'm')) return false;

        var parts = sequence[3..^1].Split(';');
        if (parts.Length != 3 || !TryDigit(parts[0], out var button) ||
            !TryDigit(parts[1], out var column) || !TryDigit(parts[2], out var row) ||
            column == 0 || row == 0) return false;

        var wheel = button & 64;
        var direction = wheel == 0 || (button & 2) != 0
            ? MouseWheel.None
            : (button & 1) == 0 ? MouseWheel.Up : MouseWheel.Down;
        var motion = wheel == 0 && (button & 32) != 0;
        var release = sequence[^1] == 'm';
        mouse = new(button, column - 1, row - 1, release, direction, motion,
            !release && wheel == 0 && !motion && (button & 3) == 0);
        return true;
    }

    internal static ConsoleKeyInfo? WheelKey(MouseEvent mouse) => mouse.Wheel switch
    {
        MouseWheel.Up => new('\0', ConsoleKey.PageUp, false, false, false),
        MouseWheel.Down => new('\0', ConsoleKey.PageDown, false, false, false),
        _ => null
    };

    internal static MouseHit HitTest(MouseRegions? regions, MouseEvent mouse)
    {
        if (regions is null || !mouse.LeftClick || mouse.Release || mouse.Motion ||
            mouse.Column < 0 || mouse.Row < 0) return MouseHit.None;

        if (mouse.Row == regions.ApprovalRow && regions.ApprovalRow >= 0)
        {
            if (regions.ApprovalAllowStart >= 0 && mouse.Column >= regions.ApprovalAllowStart &&
                mouse.Column < regions.ApprovalAllowEnd)
                return new(MouseHitKind.ApprovalAllow);
            if (regions.ApprovalRejectStart >= 0 && mouse.Column >= regions.ApprovalRejectStart &&
                mouse.Column < regions.ApprovalRejectEnd)
                return new(MouseHitKind.ApprovalReject);
        }

        if (regions.PickerTop < 0 || regions.PickerVisible <= 0 || regions.PickerColumns <= 0 ||
            mouse.Row <= regions.PickerTop || mouse.Column >= regions.PickerColumns)
        {
            // A picker owns its visible rows; the composer below it remains clickable.
        }
        else
        {
            var visibleRow = mouse.Row - regions.PickerTop - 1;
            if (visibleRow < regions.PickerVisible) return new(MouseHitKind.PickerItem, regions.PickerStart + visibleRow);
        }

        if (regions.ComposerTop < 0 || regions.ComposerRows <= 0 || regions.ComposerLines is null ||
            regions.ComposerStarts is null || mouse.Row < regions.ComposerTop ||
            mouse.Row >= regions.ComposerTop + regions.ComposerRows) return MouseHit.None;
        var line = mouse.Row - regions.ComposerTop;
        if (line >= regions.ComposerLines.Length || line >= regions.ComposerStarts.Length) return MouseHit.None;
        var column = mouse.Column;
        if (column < regions.ComposerPromptWidth) return new(MouseHitKind.Composer, regions.ComposerStarts[line]);
        column -= regions.ComposerPromptWidth;
        var source = regions.ComposerLines[line];
        var offset = TerminalText.IndexAtColumn(source, Math.Max(0, column));
        return new(MouseHitKind.Composer, regions.ComposerStarts[line] + offset);
    }

    internal static bool IsRoutedKey(ConsoleKey key) => key == RoutedKey;
    internal static ConsoleKeyInfo RoutedKeyInfo => new('\0', RoutedKey, false, false, false);

    internal static void Stage(MouseEvent mouse, MouseHit hit)
    {
        lock (PendingGate)
        {
            _pendingEvent = mouse;
            _pendingHit = hit;
        }
    }

    internal static bool TryTake(out MouseEvent mouse, out MouseHit hit)
    {
        lock (PendingGate)
        {
            if (_pendingEvent is not { } pending)
            {
                mouse = default;
                hit = MouseHit.None;
                return false;
            }
            mouse = pending;
            hit = _pendingHit;
            _pendingEvent = null;
            _pendingHit = MouseHit.None;
            return true;
        }
    }

    internal static void ClearPending()
    {
        lock (PendingGate)
        {
            _pendingEvent = null;
            _pendingHit = MouseHit.None;
        }
    }

    // Capability gate shared by TerminalSurface and tests. Redirected streams and
    // dumb/unknown Windows console handles must not emit mouse modes.
    internal static bool CanEnableMouse(bool inputRedirected, bool outputRedirected,
        bool windowsAttached, bool windows, string? term) =>
        !inputRedirected && !outputRedirected &&
        (windows ? windowsAttached : !string.Equals(term, "dumb", StringComparison.OrdinalIgnoreCase));

    private static bool TryDigit(string text, out int value)
    {
        value = 0;
        if (text.Length == 0) return false;
        foreach (var c in text)
            if (c is < '0' or > '9') return false;
        return int.TryParse(text, out value);
    }
}
