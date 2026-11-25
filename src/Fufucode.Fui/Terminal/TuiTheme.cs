using System.Globalization;
using System.Text;

namespace Fufucode.Fui;

internal enum TuiColorDepth
{
    Ansi16,
    Ansi256,
    TrueColor
}

internal enum TuiPalette
{
    Dark,
    Light,
    Mono
}

// Ink describes meaning, not a color. This layer resolves it against the active
// palette and the capability the terminal has actually advertised.
internal sealed class TuiTheme
{
    private readonly string _requestedTheme;
    private readonly bool _noColor;
    private readonly string? _term;
    private readonly string? _colorTerm;
    private readonly string? _termProgram;
    private readonly bool _windowsTerminal;
    private readonly string? _colorFgBg;
    private readonly bool _stylingEnabled;
    private readonly string[] _styles;

    private TuiTheme(string? requestedTheme, bool noColor, string? term, string? colorTerm, string? termProgram,
        bool windowsTerminal, string? colorFgBg, int? reportedBackground, bool stylingEnabled)
    {
        _requestedTheme = NormalizeTheme(requestedTheme);
        _noColor = noColor;
        _term = term;
        _colorTerm = colorTerm;
        _termProgram = termProgram;
        _windowsTerminal = windowsTerminal;
        _colorFgBg = colorFgBg;
        _stylingEnabled = stylingEnabled;

        ColorDepth = DetectColorDepth(colorTerm, term, termProgram, windowsTerminal);
        Palette = ResolvePalette(_requestedTheme, noColor, colorFgBg, reportedBackground);
        Reset = !stylingEnabled ? "" : Palette == TuiPalette.Mono ? "\x1b[22m\x1b[24m\x1b[27m" : "\x1b[0m";

        var styles = new string[Enum.GetValues<Ink>().Length];
        foreach (var ink in Enum.GetValues<Ink>()) styles[(int)ink] = BuildStyle(ink);
        _styles = styles;
    }

    internal TuiPalette Palette { get; }
    internal TuiColorDepth ColorDepth { get; }
    internal bool ColorsEnabled => _stylingEnabled && Palette != TuiPalette.Mono;
    internal bool StylingEnabled => _stylingEnabled;
    internal bool NeedsBackgroundProbe => _stylingEnabled && Palette != TuiPalette.Mono && _requestedTheme == "auto";
    internal string RequestedTheme => _requestedTheme;
    internal string Reset { get; }
    internal const string BackgroundQuery = "\x1b]11;?\x1b\\";

    internal static TuiTheme FromEnvironment(string? requestedTheme = null, int? reportedBackground = null) => Resolve(
        requestedTheme ?? Environment.GetEnvironmentVariable("FUI_THEME"),
        Environment.GetEnvironmentVariable("NO_COLOR") is not null,
        Environment.GetEnvironmentVariable("TERM"),
        Environment.GetEnvironmentVariable("COLORTERM"),
        Environment.GetEnvironmentVariable("TERM_PROGRAM"),
        Environment.GetEnvironmentVariable("WT_SESSION") is not null,
        Environment.GetEnvironmentVariable("COLORFGBG"),
        reportedBackground);

    internal static TuiTheme Resolve(string? requestedTheme = null, bool noColor = false, string? term = null,
        string? colorTerm = null, string? termProgram = null, bool windowsTerminal = false, string? colorFgBg = null,
        int? reportedBackground = null, bool stylingEnabled = true) =>
        new(requestedTheme, noColor, term, colorTerm, termProgram, windowsTerminal, colorFgBg, reportedBackground,
            stylingEnabled);

    internal static TuiTheme Unstyled { get; } = Resolve(stylingEnabled: false);

    internal TuiTheme WithReportedBackground(int rgb) => Resolve(
        _requestedTheme, _noColor, _term, _colorTerm, _termProgram, _windowsTerminal, _colorFgBg, rgb & 0xFFFFFF,
        _stylingEnabled);

    internal string Style(Ink ink) => (uint)ink < (uint)_styles.Length ? _styles[(int)ink] : "";

    internal static bool TryParseBackgroundReply(string payload, out int rgb)
    {
        rgb = 0;
        if (!payload.StartsWith("11;", StringComparison.Ordinal)) return false;

        var value = payload.AsSpan(3).Trim();
        if (value.StartsWith("rgb:", StringComparison.OrdinalIgnoreCase))
        {
            var channels = value[4..];
            var separators = 0;
            foreach (var ch in channels)
                if (ch == '/') separators++;
            if (separators != 2) return false;

            var parts = channels.ToString().Split('/');
            if (parts.Length != 3 || !TryHexChannel(parts[0], out var red) ||
                !TryHexChannel(parts[1], out var green) || !TryHexChannel(parts[2], out var blue)) return false;
            rgb = (red << 16) | (green << 8) | blue;
            return true;
        }

        if (value.Length == 7 && value[0] == '#' && TryHexByte(value.Slice(1, 2), out var red24) &&
            TryHexByte(value.Slice(3, 2), out var green24) && TryHexByte(value.Slice(5, 2), out var blue24))
        {
            rgb = (red24 << 16) | (green24 << 8) | blue24;
            return true;
        }
        return false;
    }

    private string BuildStyle(Ink ink)
    {
        if (!_stylingEnabled || ink == Ink.Plain) return "";

        var parameters = new StringBuilder(24);
        if (IsBold(ink)) AppendParameter(parameters, "1");
        if (IsDim(ink)) AppendParameter(parameters, "2");
        if (ink == Ink.Link) AppendParameter(parameters, "4");
        if (ink == Ink.Selection) AppendParameter(parameters, "7");

        if (ColorsEnabled)
        {
            var color = ColorFor(SemanticFor(ink), Palette);
            var colorParameter = ColorDepth switch
            {
                TuiColorDepth.TrueColor => $"38;2;{color.Red};{color.Green};{color.Blue}",
                TuiColorDepth.Ansi256 => $"38;5;{color.Ansi256}",
                _ => color.Ansi16.ToString(CultureInfo.InvariantCulture)
            };
            AppendParameter(parameters, colorParameter);
        }

        return parameters.Length == 0 ? "" : "\x1b[" + parameters + "m";
    }

    private static void AppendParameter(StringBuilder builder, string value)
    {
        if (builder.Length > 0) builder.Append(';');
        builder.Append(value);
    }

    private static bool IsBold(Ink ink) => ink is Ink.Header or Ink.User or Ink.Error or Ink.Approval or
        Ink.Composer or Ink.PanelTitle or Ink.Heading or Ink.SyntaxKeyword or Ink.SyntaxType or Ink.DiffAdded or
        Ink.DiffRemoved;

    private static bool IsDim(Ink ink) => ink is Ink.Quiet or Ink.Status or Ink.CodeFence or Ink.Thinking or Ink.Info;

    private static SemanticColor SemanticFor(Ink ink) => ink switch
    {
        Ink.Header => SemanticColor.Accent,
        Ink.User => SemanticColor.User,
        Ink.Assistant => SemanticColor.Assistant,
        Ink.Tool => SemanticColor.Tool,
        Ink.Quiet => SemanticColor.Quiet,
        Ink.Error => SemanticColor.Error,
        Ink.Status => SemanticColor.Status,
        Ink.Approval => SemanticColor.Approval,
        Ink.Composer => SemanticColor.Composer,
        Ink.PanelTitle => SemanticColor.PanelTitle,
        Ink.Selection => SemanticColor.Selection,
        Ink.Heading => SemanticColor.Heading,
        Ink.Code => SemanticColor.Code,
        Ink.CodeFence => SemanticColor.CodeFence,
        Ink.Thinking => SemanticColor.Thinking,
        Ink.Info => SemanticColor.Info,
        Ink.SyntaxKeyword => SemanticColor.SyntaxKeyword,
        Ink.SyntaxType => SemanticColor.SyntaxType,
        Ink.SyntaxString => SemanticColor.SyntaxString,
        Ink.SyntaxNumber => SemanticColor.SyntaxNumber,
        Ink.SyntaxComment => SemanticColor.SyntaxComment,
        Ink.SyntaxFunction => SemanticColor.SyntaxFunction,
        Ink.SyntaxProperty => SemanticColor.SyntaxProperty,
        Ink.SyntaxOperator => SemanticColor.SyntaxOperator,
        Ink.DiffAdded => SemanticColor.DiffAdded,
        Ink.DiffRemoved => SemanticColor.DiffRemoved,
        Ink.DiffMeta => SemanticColor.DiffMeta,
        Ink.Link => SemanticColor.Link,
        _ => SemanticColor.Assistant
    };

    private static ColorChoice ColorFor(SemanticColor semantic, TuiPalette palette) => palette switch
    {
        TuiPalette.Dark => semantic switch
        {
            SemanticColor.Accent => new(94, 39, 88, 166, 255),
            SemanticColor.User => new(96, 45, 78, 201, 245),
            SemanticColor.Assistant => new(97, 255, 216, 222, 233),
            SemanticColor.Tool => new(94, 33, 79, 140, 255),
            SemanticColor.Quiet => new(90, 245, 139, 148, 158),
            SemanticColor.Error => new(91, 203, 255, 107, 107),
            SemanticColor.Status => new(90, 244, 125, 133, 144),
            SemanticColor.Approval => new(93, 221, 227, 179, 65),
            SemanticColor.Composer => new(97, 255, 230, 237, 243),
            SemanticColor.PanelTitle => new(96, 51, 86, 212, 221),
            SemanticColor.Selection => new(97, 255, 240, 246, 252),
            SemanticColor.Heading => new(93, 221, 227, 179, 65),
            SemanticColor.Code => new(96, 45, 86, 182, 194),
            SemanticColor.CodeFence => new(90, 66, 106, 143, 150),
            SemanticColor.Thinking => new(95, 139, 180, 142, 173),
            SemanticColor.Info => new(94, 110, 127, 156, 179),
            SemanticColor.SyntaxKeyword => new(95, 177, 199, 121, 255),
            SemanticColor.SyntaxType => new(96, 80, 121, 192, 255),
            SemanticColor.SyntaxString => new(92, 114, 185, 115, 120),
            SemanticColor.SyntaxNumber => new(93, 209, 154, 102, 64),
            SemanticColor.SyntaxComment => new(90, 98, 112, 128, 138),
            SemanticColor.SyntaxFunction => new(94, 86, 156, 214, 255),
            SemanticColor.SyntaxProperty => new(96, 97, 175, 239, 255),
            SemanticColor.SyntaxOperator => new(90, 180, 181, 188, 198),
            SemanticColor.DiffAdded => new(92, 126, 198, 153, 116),
            SemanticColor.DiffRemoved => new(91, 224, 108, 117, 130),
            SemanticColor.DiffMeta => new(90, 140, 170, 190, 210),
            SemanticColor.Link => new(94, 110, 166, 255, 246),
            _ => new(97, 255, 216, 222, 233)
        },
        TuiPalette.Light => semantic switch
        {
            SemanticColor.Accent => new(34, 25, 11, 92, 173),
            SemanticColor.User => new(36, 30, 9, 105, 218),
            SemanticColor.Assistant => new(30, 235, 36, 41, 47),
            SemanticColor.Tool => new(34, 25, 5, 80, 174),
            SemanticColor.Quiet => new(90, 243, 110, 119, 129),
            SemanticColor.Error => new(31, 124, 207, 34, 46),
            SemanticColor.Status => new(90, 240, 87, 96, 106),
            SemanticColor.Approval => new(33, 130, 154, 103, 0),
            SemanticColor.Composer => new(30, 235, 36, 41, 47),
            SemanticColor.PanelTitle => new(36, 30, 15, 107, 116),
            SemanticColor.Selection => new(30, 235, 31, 35, 40),
            SemanticColor.Heading => new(33, 130, 154, 103, 0),
            SemanticColor.Code => new(36, 30, 15, 107, 116),
            SemanticColor.CodeFence => new(90, 66, 87, 96, 106),
            SemanticColor.Thinking => new(35, 90, 130, 80, 223),
            SemanticColor.Info => new(34, 67, 49, 91, 125),
            SemanticColor.SyntaxKeyword => new(35, 128, 44, 112, 180),
            SemanticColor.SyntaxType => new(36, 74, 9, 105, 218),
            SemanticColor.SyntaxString => new(32, 107, 31, 122, 79),
            SemanticColor.SyntaxNumber => new(33, 166, 148, 86, 0),
            SemanticColor.SyntaxComment => new(90, 112, 120, 128, 140),
            SemanticColor.SyntaxFunction => new(34, 68, 25, 90, 180),
            SemanticColor.SyntaxProperty => new(36, 67, 8, 105, 150),
            SemanticColor.SyntaxOperator => new(90, 72, 85, 99, 112),
            SemanticColor.DiffAdded => new(32, 29, 20, 115, 60),
            SemanticColor.DiffRemoved => new(31, 160, 175, 40, 40),
            SemanticColor.DiffMeta => new(90, 90, 100, 110, 124),
            SemanticColor.Link => new(34, 25, 90, 180, 242),
            _ => new(30, 235, 36, 41, 47)
        },
        _ => new(39, 255, 255, 255, 255)
    };

    private static TuiPalette ResolvePalette(string requestedTheme, bool noColor, string? colorFgBg,
        int? reportedBackground)
    {
        if (noColor || requestedTheme == "mono") return TuiPalette.Mono;
        if (requestedTheme == "light") return TuiPalette.Light;
        if (requestedTheme == "dark") return TuiPalette.Dark;
        if (reportedBackground is { } rgb) return IsLight(rgb) ? TuiPalette.Light : TuiPalette.Dark;
        if (TryColorFgBg(colorFgBg, out var colorFgBgLight))
            return colorFgBgLight ? TuiPalette.Light : TuiPalette.Dark;
        return TuiPalette.Dark;
    }

    private static string NormalizeTheme(string? requestedTheme) => requestedTheme?.Trim().ToLowerInvariant() switch
    {
        "dark" => "dark",
        "light" => "light",
        "mono" => "mono",
        _ => "auto"
    };

    private static TuiColorDepth DetectColorDepth(string? colorTerm, string? term, string? termProgram,
        bool windowsTerminal)
    {
        var colorTermValue = colorTerm?.Trim().ToLowerInvariant();
        if (colorTermValue is "truecolor" or "24bit") return TuiColorDepth.TrueColor;
        if (windowsTerminal || IsTrueColorProgram(termProgram?.Trim().ToLowerInvariant()))
            return TuiColorDepth.TrueColor;

        var termValue = term?.Trim().ToLowerInvariant() ?? "";
        if (termValue.Contains("truecolor", StringComparison.Ordinal) ||
            termValue.Contains("24bit", StringComparison.Ordinal) ||
            termValue.Contains("direct", StringComparison.Ordinal)) return TuiColorDepth.TrueColor;
        if (termValue.Contains("256color", StringComparison.Ordinal) ||
            termValue.Contains("256-colour", StringComparison.Ordinal)) return TuiColorDepth.Ansi256;
        return TuiColorDepth.Ansi16;
    }

    private static bool IsTrueColorProgram(string? termProgram) => termProgram is "kitty" or "ghostty" or "wezterm" or
        "iterm.app" or "iterm2" or "vscode" or "alacritty" or "warpterminal" or "orca" or "otty" or "rio" or "tern" or
        "monstar" or "rex" or "windows_terminal" or "hyper";

    private static bool TryColorFgBg(string? value, out bool light)
    {
        light = false;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var parts = value.Split(';');
        var background = parts.LastOrDefault(static part => part.Length > 0);
        if (!int.TryParse(background, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) ||
            index is < 0 or > 15) return false;
        light = index >= 8;
        return true;
    }

    private static bool IsLight(int rgb)
    {
        var red = (rgb >> 16) & 0xFF;
        var green = (rgb >> 8) & 0xFF;
        var blue = rgb & 0xFF;
        return (299 * red + 587 * green + 114 * blue) / 1000 >= 128;
    }

    private static bool TryHexChannel(string text, out byte value)
    {
        value = 0;
        if (text.Length is < 1 or > 4) return false;
        var parsed = 0;
        foreach (var ch in text)
        {
            var digit = HexDigit(ch);
            if (digit < 0) return false;
            parsed = (parsed << 4) | digit;
        }
        var maximum = (1 << (4 * text.Length)) - 1;
        value = (byte)((parsed * 255 + maximum / 2) / maximum);
        return true;
    }

    private static bool TryHexByte(ReadOnlySpan<char> text, out byte value)
    {
        value = 0;
        if (text.Length != 2) return false;
        var high = HexDigit(text[0]);
        var low = HexDigit(text[1]);
        if (high < 0 || low < 0) return false;
        value = (byte)((high << 4) | low);
        return true;
    }

    private static int HexDigit(char ch) => ch switch
    {
        >= '0' and <= '9' => ch - '0',
        >= 'a' and <= 'f' => ch - 'a' + 10,
        >= 'A' and <= 'F' => ch - 'A' + 10,
        _ => -1
    };

    private enum SemanticColor
    {
        Accent,
        User,
        Assistant,
        Tool,
        Quiet,
        Error,
        Status,
        Approval,
        Composer,
        PanelTitle,
        Selection,
        Heading,
        Code,
        CodeFence,
        Thinking,
        Info,
        SyntaxKeyword,
        SyntaxType,
        SyntaxString,
        SyntaxNumber,
        SyntaxComment,
        SyntaxFunction,
        SyntaxProperty,
        SyntaxOperator,
        DiffAdded,
        DiffRemoved,
        DiffMeta,
        Link
    }

    private readonly record struct ColorChoice(byte Ansi16, byte Ansi256, byte Red, byte Green, byte Blue);
}
