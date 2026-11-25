using System.Text.RegularExpressions;
using Fufucode.Fui;

namespace Fufucode.Fui.Tests;

internal static class ThemeTests
{
    private static readonly Regex ColorSgr = new(
        @"\x1b\[(?:[0-9]+;)*(?:3[0-9]|4[0-9]|9[0-7]|10[0-7]|38|48|39|49)(?:;[0-9]+)*m",
        RegexOptions.CultureInvariant);

    internal static void Run()
    {
        PaletteSelection();
        EnvironmentWiring();
        ColorDepthSelection();
        MonoAndNoColor();
        SemanticInkStyles();
        BackgroundReplies();
        RenderingSafety();
    }

    private static void PaletteSelection()
    {
        Eq(TuiPalette.Dark, TuiTheme.Resolve("dark").Palette, "explicit dark theme");
        Eq(TuiPalette.Light, TuiTheme.Resolve("light").Palette, "explicit light theme");
        Eq(TuiPalette.Mono, TuiTheme.Resolve("mono").Palette, "explicit mono theme");
        Eq(TuiPalette.Dark, TuiTheme.Resolve("auto").Palette, "auto without evidence defaults dark");
        Require(TuiTheme.Resolve("auto").NeedsBackgroundProbe, "auto requests a background capability probe");
        Require(!TuiTheme.Resolve("dark").NeedsBackgroundProbe, "explicit theme avoids an unnecessary background probe");
        Require(!TuiTheme.Resolve("auto", noColor: true).NeedsBackgroundProbe,
            "NO_COLOR avoids an unnecessary background probe");
        Eq(TuiPalette.Light, TuiTheme.Resolve("auto", reportedBackground: 0xFFFFFF).Palette,
            "auto follows a light terminal background");
        Eq(TuiPalette.Dark, TuiTheme.Resolve("auto", reportedBackground: 0x101216).Palette,
            "auto follows a dark terminal background");
        Eq(TuiPalette.Light, TuiTheme.Resolve("auto", colorFgBg: "0;15").Palette, "COLORFGBG light fallback");
        Eq(TuiPalette.Dark, TuiTheme.Resolve("auto", colorFgBg: "15;0").Palette, "COLORFGBG dark fallback");
        Eq(TuiPalette.Dark, TuiTheme.Resolve("dark", reportedBackground: 0xFFFFFF).Palette,
            "explicit theme is not overridden by background");
        Eq(TuiPalette.Dark, TuiTheme.Resolve("\x1b[31m").Palette, "invalid theme falls back safely to auto dark");
    }

    private static void EnvironmentWiring()
    {
        var oldTheme = Environment.GetEnvironmentVariable("FUI_THEME");
        var oldNoColor = Environment.GetEnvironmentVariable("NO_COLOR");
        try
        {
            Environment.SetEnvironmentVariable("NO_COLOR", null);
            Environment.SetEnvironmentVariable("FUI_THEME", "light");
            Eq(TuiPalette.Light, TuiTheme.FromEnvironment().Palette, "FUI_THEME environment selection");
            Environment.SetEnvironmentVariable("NO_COLOR", "1");
            Eq(TuiPalette.Mono, TuiTheme.FromEnvironment().Palette, "NO_COLOR environment override");
        }
        finally
        {
            Environment.SetEnvironmentVariable("FUI_THEME", oldTheme);
            Environment.SetEnvironmentVariable("NO_COLOR", oldNoColor);
        }
    }

    private static void ColorDepthSelection()
    {
        Eq(TuiColorDepth.TrueColor, TuiTheme.Resolve(colorTerm: "truecolor").ColorDepth, "COLORTERM truecolor");
        Eq(TuiColorDepth.TrueColor, TuiTheme.Resolve(colorTerm: "24bit").ColorDepth, "COLORTERM 24bit");
        Eq(TuiColorDepth.TrueColor, TuiTheme.Resolve(windowsTerminal: true).ColorDepth, "Windows Terminal capability");
        Eq(TuiColorDepth.TrueColor, TuiTheme.Resolve(termProgram: "wezterm").ColorDepth, "known truecolor terminal");
        Eq(TuiColorDepth.Ansi256, TuiTheme.Resolve(term: "xterm-256color").ColorDepth, "advertised 256 colors");
        Eq(TuiColorDepth.Ansi16, TuiTheme.Resolve(term: "vt100").ColorDepth, "unknown terminal uses compatible ANSI");

        var unknown = TuiTheme.Resolve(term: "vt100").Style(Ink.Error);
        var ansi256 = TuiTheme.Resolve(term: "xterm-256color").Style(Ink.Error);
        var trueColor = TuiTheme.Resolve(colorTerm: "truecolor").Style(Ink.Error);
        Require(!unknown.Contains("38;5;", StringComparison.Ordinal) &&
            !unknown.Contains("38;2;", StringComparison.Ordinal), "unknown capability does not assume 256/truecolor");
        Require(ansi256.Contains("38;5;", StringComparison.Ordinal), "256-color style uses a 256-color SGR");
        Require(trueColor.Contains("38;2;", StringComparison.Ordinal), "truecolor style uses a 24-bit SGR");
    }

    private static void MonoAndNoColor()
    {
        var mono = TuiTheme.Resolve("mono", colorTerm: "truecolor");
        var noColor = TuiTheme.Resolve("light", noColor: true, colorTerm: "truecolor");
        Eq(TuiPalette.Mono, noColor.Palette, "NO_COLOR selects the non-color palette");
        Require(mono.Style(Ink.Approval).Contains("[1m", StringComparison.Ordinal), "mono keeps bold semantics");
        Require(mono.Style(Ink.Thinking).Contains("[2m", StringComparison.Ordinal), "mono keeps dim semantics");
        Require(mono.Style(Ink.Selection).Contains("[7m", StringComparison.Ordinal), "mono keeps reverse selection");

        foreach (var theme in new[] { mono, noColor })
        foreach (var ink in Enum.GetValues<Ink>())
            Require(!ColorSgr.IsMatch(theme.Style(ink)), $"mono style for {ink} contains no color SGR");

        var frame = Frame();
        var output = TerminalSurface.Diff(null, frame, noColor);
        Require(!ColorSgr.IsMatch(output), "NO_COLOR output contains no color SGR");
        Require(Regex.Matches(output, @"\x1b\[([0-9;]+)m", RegexOptions.CultureInvariant)
            .Select(match => match.Groups[1].Value.Split(';'))
            .SelectMany(parameters => parameters)
            .All(parameter => parameter is "1" or "2" or "7" or "22" or "27"),
            "NO_COLOR output contains only non-color intensity/reverse SGR");
        Require(output.Contains("! F2 允许 · F3 拒绝", StringComparison.Ordinal),
            "NO_COLOR leaves approval text untouched");
    }

    private static void SemanticInkStyles()
    {
        var dark = TuiTheme.Resolve("dark", colorTerm: "truecolor");
        var light = TuiTheme.Resolve("light", colorTerm: "truecolor");
        foreach (var ink in Enum.GetValues<Ink>())
        {
            var style = dark.Style(ink);
            if (ink == Ink.Plain)
            {
                Eq("", style, "plain ink has no injected style");
                continue;
            }
            Require(style.Contains("38;2;", StringComparison.Ordinal), $"dark has a semantic color for {ink}");
            Require(Regex.IsMatch(style, @"^(?:\x1b\[[0-9;]+m)+$", RegexOptions.CultureInvariant),
                $"style for {ink} contains only generated SGR");
        }
        Require(dark.Style(Ink.Error) != light.Style(Ink.Error), "light and dark error colors differ");
        Require(dark.Style(Ink.Approval) != light.Style(Ink.Approval), "light and dark approval colors differ");
    }

    private static void BackgroundReplies()
    {
        Require(TuiTheme.TryParseBackgroundReply("11;rgb:ffff/ffff/ffff", out var white) && white == 0xFFFFFF,
            "parse 16-bit OSC 11 background");
        Require(TuiTheme.TryParseBackgroundReply("11;#101216", out var dark) && dark == 0x101216,
            "parse hex OSC 11 background");
        Require(!TuiTheme.TryParseBackgroundReply("10;rgb:ffff/ffff/ffff", out _),
            "ignore foreground reports for theme selection");
        Require(!TuiTheme.TryParseBackgroundReply("11;rgb:00/00/00\x1b[31m", out _),
            "reject control characters in background reports");
        Eq(TuiPalette.Light, TuiTheme.Resolve("auto").WithReportedBackground(0xFFFFFF).Palette,
            "runtime background reply switches auto to light");
        Eq(TuiPalette.Dark, TuiTheme.Resolve("dark").WithReportedBackground(0xFFFFFF).Palette,
            "runtime background reply leaves explicit dark alone");
    }

    private static void RenderingSafety()
    {
        var frame = Frame();
        var dark = TuiTheme.Resolve("dark", colorTerm: "truecolor");
        var light = TuiTheme.Resolve("light", colorTerm: "truecolor");
        var mono = TuiTheme.Resolve("mono", term: "xterm-256color");
        var outputs = new[]
        {
            TerminalSurface.Diff(null, frame, dark),
            TerminalSurface.Diff(null, frame, light),
            TerminalSurface.Diff(null, frame, mono)
        };
        var visible = outputs.Select(RemoveSgr).ToArray();
        Eq(visible[0], visible[1], "dark/light switching does not change screen text");
        Eq(visible[0], visible[2], "mono switching does not change screen text");
        Require(!outputs[0].Contains("\x1b[31m", StringComparison.Ordinal),
            "user control text is not treated as theme markup");
        Require(visible[0].Contains("用户内容 不得穿透", StringComparison.Ordinal), "safe user text remains visible");
        Require(frame.Rows.Select(row => row.Text).SequenceEqual(Frame().Rows.Select(row => row.Text)),
            "theme selection never mutates the terminal frame");
        Require(visible.All(text => text.Contains("╭─ FUMO", StringComparison.Ordinal) &&
            text.Contains("! F2 允许 · F3 拒绝", StringComparison.Ordinal)), "card and approval characters stay intact");

        const string image = "\x1bP0;1;0q#0;2;0;0;0#1~~\x1b\\";
        Require(outputs.All(output => output.Contains(image, StringComparison.Ordinal)),
            "SIXEL/LaTeX image payload is never recolored or rewritten");
    }

    private static TerminalFrame Frame()
    {
        const string image = "\x1bP0;1;0q#0;2;0;0;0#1~~\x1b\\";
        var userText = TerminalText.Clean("用户内容 \x1b[31m不得穿透");
        ScreenRow[] rows =
        [
            new("FUI  项目 · model", Ink.Header),
            new("╭─ FUMO 你 ─╮", Ink.User),
            new($"│ {userText} │", Ink.Assistant),
            new("│ 公式图片占据这一格 │", Ink.Assistant, [new ImageStrip(2, image)]),
            new("工具 read · 完成", Ink.Tool),
            new("! F2 允许 · F3 拒绝", Ink.Approval),
            new("├─ 运行中  Enter 发送 ┤", Ink.Status),
            new("│ › 草稿", Ink.Composer)
        ];
        return new TerminalFrame(32, rows, 7, 5, rows.Length, 0);
    }

    private static string RemoveSgr(string output) =>
        Regex.Replace(output, @"\x1b\[[0-9;]*m", "", RegexOptions.CultureInvariant);

    private static void Require(bool value, string context)
    {
        if (!value) throw new Exception(context);
    }

    private static void Eq<T>(T expected, T actual, string context)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"{context}: expected [{expected}], got [{actual}]");
    }
}
