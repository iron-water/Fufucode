namespace Fufucode.Fui.Tests;

internal static class FormulaTests
{
    internal static void Run()
    {
        var support = new SixelSupport(null);
        var keys = new PasteKeys(support.Reply, support.ColorReply);
        foreach (var ch in "\x1b[?62;4;22c\x1b[6;20;10t") keys.Feed(new(ch, 0, false, false, false), 0);
        keys.FlushExpired(100);
        Require(support.Cell == new ImageCells(10, 20) && keys.Read() is null, "terminal graphics replies are not draft input");
        var wtSupport = new SixelSupport(null);
        var wtKeys = new PasteKeys(wtSupport.Reply, wtSupport.ColorReply);
        foreach (var ch in "\x1b[?61;6;7;14;21;22;23;24;28;32;42;52;4c\x1b[6;20;10t\x1b]10;rgb:cccc/cccc/cccc\x1b\\\x1b]11;rgb:0c0c/0c0c/0c0c\x1b\\")
            wtKeys.Feed(new(ch, 0, false, false, false), 0);
        wtKeys.FlushExpired(100);
        Require(wtSupport.Cell == new ImageCells(10, 20, 0xCCCCCC, 0x0C0C0C) && wtKeys.Read() is null,
            "long Windows Terminal DA1 reply with trailing ;4 is not truncated");
        foreach (var ch in "\x1b]10;rgb:fafa/ecec/dddd\x1b\\\x1b]11;rgb:18/20/32\a") keys.Feed(new(ch, 0, false, false, false), 200);
        keys.FlushExpired(300);
        Require(support.Cell == new ImageCells(10, 20, 0xFAECDD, 0x182032) && keys.Read() is null, "theme reports set both colors without injecting text");

        var layout = new TerminalLayout();
        var source = """
            # Results
            text $E=mc^2$ end
            \[
            \frac{-b\pm\sqrt{b^2-4ac}}{2a}
            \]
            ```math
            \begin{pmatrix}a&b\\c&d\end{pmatrix}
            ```
            literal `$\alpha$`, cost $5 and $10, \$quoted
            ```cs
            var s = "$not_math$";
            ```
            bad $\unknown{a}$ and open $x^
            """;
        TuiView View(string text) => new("", "fixture", "fixture", "就绪", [new("assistant", text)], "draft", 5, 0, null);
        var plain = layout.Arrange(View(source), 100, 32);
        Require(plain.Rows.Any(row => row.Text.Contains(@"\frac{")) && plain.Rows.All(row => row.Images is null), "literal source in a text terminal");
        var picture = layout.Arrange(View(source), 100, 32, support.Cell);
        var lines = picture.Rows;
        Require(lines.Count(row => row.Images?.Count > 0) >= 5 &&
            lines.All(row => !row.Text.Contains(@"\frac") && !row.Text.Contains("pmatrix") && !row.Text.Contains("mc^")),
            "inline, display fraction and fenced matrix become raster images");
        Require(lines.Any(row => row.Text.Contains("text ") && row.Text.Contains(" end") && row.Images?.Count == 1), "inline prose and image share a row");
        Require(lines.Any(row => row.Text.Contains("`$\\alpha$`, cost $5 and $10")) &&
            lines.Any(row => row.Ink == Ink.Code && row.Text.Contains("$not_math$")) &&
            lines.Any(row => row.Text.Contains(@"\unknown{a}") && row.Text.Contains("$x^")), "code, prices and incomplete or unsupported TeX remain literal");
        var unchanged = layout.Arrange(View(source), 100, 32, support.Cell);
        Require(TerminalSurface.Diff(picture, unchanged, true) == "", "unchanged image stays in place without retransmission");
        var nested = "$" + new string('{', 800) + "x" + new string('}', 800) + "$";
        Require(layout.Arrange(View(nested), 24, 6, support.Cell).Rows.All(row => row.Images is null), "oversize math remains text");
        var live = layout.Arrange(View(@"answer $\frac{a}"), 70, 12, support.Cell);
        Require(live.Rows.All(row => row.Images is null), "incomplete stream stays readable");
        var final = layout.Arrange(View(@"answer $\frac{a}{b}$"), 70, 12, support.Cell);
        Require(TerminalSurface.Diff(live, final, true).Contains("\x1bP0;1;0q"), "completed stream emits SIXEL");
        var cleared = layout.Arrange(View("next message"), 70, 12, support.Cell);
        Require(TerminalSurface.Diff(final, cleared, true).Contains("\x1b[2K") && cleared.Rows.All(row => row.Images is null),
            "replacing an image removes its strips");
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidDataException(message);
    }
}
