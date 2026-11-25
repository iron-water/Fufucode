namespace Fufucode.Fui;

// DEC primary device attribute 4 advertises SIXEL; xterm CSI 16 t returns the pixel size of a cell.
internal sealed class SixelSupport(string? mode)
{
    private bool _enabled = string.Equals(mode, "sixel", StringComparison.OrdinalIgnoreCase);
    private ImageCells _cell = ImageCells.Default;
    internal bool Probe { get; } = !string.Equals(mode, "off", StringComparison.OrdinalIgnoreCase);
    internal ImageCells? Cell => _enabled ? _cell : null;
    internal const string Query = "\x1b[c\x1b[16t";

    internal bool Reply(char command, string parameters)
    {
        if (command == 'c' && parameters.StartsWith('?'))
        {
            if (Probe && parameters[1..].Split(';').Contains("4")) _enabled = true;
            return true;
        }
        if (command != 't') return false;
        var fields = parameters.Split(';');
        if (fields is ["6", var h, var w] && int.TryParse(h, out var height) && int.TryParse(w, out var width) &&
            width is >= 2 and <= 96 && height is >= 4 and <= 144) _cell = new(width, height);
        return true;
    }
}
