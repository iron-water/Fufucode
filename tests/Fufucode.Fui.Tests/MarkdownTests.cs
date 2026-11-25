using Fufucode.Fui;

namespace Fufucode.Fui.Tests;

internal static class MarkdownTests
{
    internal static void Run()
    {
        Tables();
        LinksAndBlocks();
        Fences();
        Streaming();
        Resize();
        LayoutIntegration();
    }

    private static void Tables()
    {
        var two = Render("| A | B |\n| --- | --- |\n| x | y |", 5);
        Eq("A │ B\n──┼──\nx │ y", string.Join('\n', two.Select(row => row.Text)), "two-column table");
        Eq(Ink.Heading, two[0].Ink, "table header style");
        Eq(Ink.Quiet, two[1].Ink, "table rule style");

        var three = Render("| A | B | C |\n| --- | --- | --- |\n| x | y | z |", 9);
        Eq("A │ B │ C\n──┼───┼──\nx │ y │ z", string.Join('\n', three.Select(row => row.Text)), "three-column table");

        var aligned = Render("| 名 | 值 | 备注 |\n| :--- | :---: | ---: |\n| 中文 | 🙂 | x |", 16);
        Eq("名   │ 值 │ 备注", aligned[0].Text, "CJK and emoji display widths");
        Eq("中文 │ 🙂 │    x", aligned[2].Text, "left, center and right alignment");
        Eq(true, aligned.All(row => TerminalText.Width(row.Text) == TerminalText.Width(aligned[0].Text)), "equal row width");
        Eq(true, aligned.All(row => TerminalText.Width(row.Text) <= 16), "table stays within the viewport");

        var clipped = Render("| A | B | C |\n| --- | --- | --- |\n| " + new string('x', 80) +
            " | | x\u0001y |", 20);
        Eq(true, clipped.All(row => TerminalText.Width(row.Text) <= 20), "overwide table stays bounded");
        Eq(true, clipped.Any(row => row.Text.Contains('…')), "overwide cell is clipped visibly");
        Eq(true, clipped.All(row => !row.Text.Any(char.IsControl)), "cell controls are removed");
        Eq(true, clipped[2].Text.Split('│').Length == 3, "empty cell keeps its column");
    }

    private static void LinksAndBlocks()
    {
        var rows = Render("> quote [q](https://q)\n---\n- item [i](https://i)\n1. first\n" +
            "[x](https://x) `[raw](https://raw)`", 80);
        Eq("│ quote q (https://q)", rows[0].Text, "blockquote link hint");
        Eq(Ink.Quiet, rows[0].Ink, "blockquote is quiet");
        Eq(true, rows[1].Text.All(ch => ch == '─'), "horizontal rule");
        Eq("• item i (https://i)", rows[2].Text, "unordered list prefix");
        Eq("1. first", rows[3].Text, "ordered list prefix");
        Eq("x (https://x) `[raw](https://raw)`", rows[4].Text, "link hint leaves inline code literal");
    }

    private static void Fences()
    {
        var rows = Render("```\n| A | B |\n[x](https://example.com) | y", 80);
        Eq(true, rows.Any(row => row.Text.Contains("| A | B |")), "table pipes stay literal in an open fence");
        Eq(true, rows.Any(row => row.Text.Contains("[x](https://example.com) | y")), "fence link stays literal");
        Eq(false, rows.Any(row => row.Text.Contains("│") || row.Text.Contains("x (")), "fence content is not parsed");
    }

    private static void Streaming()
    {
        const string prefix = "| H1 | H2 |\n| --- | --- |\n| aa | bb |\n";
        var markdown = new MarkdownLines();
        markdown.Project(prefix, 24);
        var stable = Rows(markdown).Take(3).ToArray();
        var partial = "";
        foreach (var part in new[] { "| partial", " cell", " | more" })
        {
            partial += part;
            markdown.Project(prefix + partial, 24);
            var current = Rows(markdown);
            Eq(true, current.Take(3).SequenceEqual(stable), "an unfinished table row does not move painted rows");
            Eq(true, current.Any(row => row.Text.Contains("partial")), "the unfinished row remains visible");
        }

        var tiny = Render("| A |\n| --- |\n| unfinished", 1);
        Eq(true, tiny.All(row => TerminalText.Width(row.Text) <= 1), "a one-column streaming table cannot overflow");
    }

    private static void Resize()
    {
        const string source = "| 中文 | value |\n| --- | --- |\n| 很长的内容 | something |";
        var wide = Render(source, 40);
        var narrow = Render(source, 14);
        Eq(true, wide.All(row => TerminalText.Width(row.Text) <= 40), "wide table bounded");
        Eq(true, narrow.All(row => TerminalText.Width(row.Text) <= 14), "narrow table bounded");
        Eq(false, wide[0].Text == narrow[0].Text, "resize recomputes column widths");
    }

    private static void LayoutIntegration()
    {
        const string source = "| 名称 | 结果 |\n| :--- | ---: |\n| 中文 | 🙂 |";
        var view = new TuiView("/demo", "S1", "model", "就绪", [new("assistant", source)], "", 0, 0, null);
        var frame = new TerminalLayout().Arrange(view, 60, 20);
        var table = frame.Rows.Where(row => row.Text.Contains('│') || row.Text.Contains('┼')).ToArray();
        Eq(true, table.Length >= 3, "table reaches the terminal frame");
        Eq(true, table.All(row => TerminalText.Width(row.Text) < 60), "table remains inside the viewport");
    }

    private static MarkdownLines.Row[] Render(string source, int width)
    {
        var markdown = new MarkdownLines();
        markdown.Project(source, width);
        return Rows(markdown);
    }

    private static MarkdownLines.Row[] Rows(MarkdownLines markdown) =>
        Enumerable.Range(0, markdown.Count).Select(index => markdown[index]).ToArray();

    private static void Eq<T>(T expected, T actual, string context)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"{context}: expected [{expected}], got [{actual}]");
    }
}
