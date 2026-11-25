using System.Globalization;

namespace Fufucode.Fui;

// DEC primary device attribute 4 advertises SIXEL; xterm CSI 16 t returns the pixel size of a cell.
internal sealed class SixelSupport(string? mode)
{
    private bool _enabled = string.Equals(mode, "sixel", StringComparison.OrdinalIgnoreCase);
    private ImageCells _cell = ImageCells.Default;
    internal bool Probe { get; } = !string.Equals(mode, "off", StringComparison.OrdinalIgnoreCase);
    internal ImageCells? Cell => _enabled ? _cell : null;
    internal const string Colors = "\x1b]10;?\x1b\\\x1b]11;?\x1b\\";
    internal const string Query = "\x1b[c\x1b[16t" + Colors;

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
            width is >= 2 and <= 96 && height is >= 4 and <= 144) _cell = _cell with { Width = width, Height = height };
        return true;
    }

    internal void ColorReply(string payload)
    {
        if (!Probe || payload.Length < 8 || payload[..3] is not ("10;" or "11;") ||
            !payload.AsSpan(3).StartsWith("rgb:", StringComparison.OrdinalIgnoreCase)) return;
        var channels = payload[7..].Split('/');
        if (channels.Length != 3) return;
        var rgb = 0;
        foreach (var channel in channels)
        {
            if (channel.Length is < 1 or > 4 || !int.TryParse(channel, NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture, out var value)) return;
            var max = (1 << (4 * channel.Length)) - 1;
            rgb = (rgb << 8) | ((value * 255 + max / 2) / max);
        }
        _cell = payload[1] == '0' ? _cell with { Foreground = rgb } : _cell with { Background = rgb };
    }
}
