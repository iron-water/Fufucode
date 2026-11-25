namespace Fufucode.Fui;

// Hyperlinks are metadata until the terminal painter runs. The visible text never carries OSC bytes,
// so wrapping and width calculations cannot split an escape sequence.
internal sealed class TuiHyperlinkPolicy
{
    private const string Osc8 = "\x1b]8;;";
    private const string St = "\x1b\\";

    private TuiHyperlinkPolicy(bool enabled) => Enabled = enabled;

    internal bool Enabled { get; }
    internal static TuiHyperlinkPolicy Disabled { get; } = new(false);

    internal static TuiHyperlinkPolicy FromEnvironment() => Resolve(
        Environment.GetEnvironmentVariable("FUI_HYPERLINKS"),
        Environment.GetEnvironmentVariable("NO_COLOR") is not null,
        DetectCapability(
            Environment.GetEnvironmentVariable("WT_SESSION"),
            Environment.GetEnvironmentVariable("TERM_PROGRAM"),
            Environment.GetEnvironmentVariable("TERM")));

    internal static TuiHyperlinkPolicy Resolve(string? mode, bool noColor, bool terminalSupportsLinks)
    {
        if (noColor) return Disabled;
        return mode?.Trim().ToLowerInvariant() switch
        {
            "off" => Disabled,
            "on" or "always" => new(true),
            _ => terminalSupportsLinks ? new(true) : Disabled
        };
    }

    internal static bool DetectCapability(string? windowsTerminal, string? termProgram, string? term)
    {
        if (!string.IsNullOrEmpty(windowsTerminal)) return true;
        var program = termProgram?.Trim().ToLowerInvariant();
        if (program is "windows_terminal" or "windows-terminal" or "vscode" or "wezterm" or "kitty" or "ghostty" or
            "iterm.app" or "iterm2" or "alacritty" or "warpterminal" or "rio" or "tabby" or "hyper") return true;
        var value = term?.Trim().ToLowerInvariant();
        return value is not null && (value.Contains("kitty", StringComparison.Ordinal) ||
            value.Contains("wezterm", StringComparison.Ordinal) ||
            value.Contains("ghostty", StringComparison.Ordinal));
    }

    internal string Wrap(string visible, string? target)
    {
        visible = TerminalText.Clean(visible);
        if (!Enabled || string.IsNullOrWhiteSpace(target) || HasControl(target)) return visible;
        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || HasControl(uri.AbsoluteUri)) return visible;
        return Osc8 + uri.AbsoluteUri + St + visible + Osc8 + St;
    }

    internal static bool IsSafeUrl(string? target)
    {
        if (string.IsNullOrWhiteSpace(target) || HasControl(target)) return false;
        return Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
    }

    private static bool HasControl(string value) =>
        value.Any(ch => char.IsControl(ch) || ch is '\u009b' or '\u009d' or '\u0090' or '\u0098' or '\u009e' or '\u009f');
}
