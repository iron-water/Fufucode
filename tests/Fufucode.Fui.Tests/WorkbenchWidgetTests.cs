using Fufucode.Fui;

namespace Fufucode.Fui.Tests;

internal static class WorkbenchWidgetTests
{
    internal static void Run()
    {
        Picker(); SessionTree(); ComposerActions(); Panel(); Reading(); MarkdownTests.Run();
    }

    private static void Picker()
    {
        FuiChoice[] choices = [new("d", "docs", "read ABC details"), new("p2", "ABC 2"), new("s", "has Abc"),
            new("exact", "abc"), new("p1", "Abc 1", "中文"), new("fz", "Away Beats Cold")];
        var p = new FuiPicker("分支", choices, "abc");
        Eq("exact,p2,p1,s,d,fz", string.Join(',', p.View.Items.Select(c => c.Value)), "exact/prefix/body/detail/fuzzy with stable equal ranks");
        p.Edit(Key(ConsoleKey.End), 4); Eq("fz", p.Current?.Value, "last");
        var same = p.View.Items;
        p.Edit(Key(ConsoleKey.PageUp), 4); Eq("p2", p.Current?.Value, "page");
        p.Edit(Key(ConsoleKey.DownArrow), 4); Eq("p1", p.Current?.Value, "down");
        Eq(true, ReferenceEquals(same, p.View.Items), "navigation reuses filtered results");
        p.Edit(Key(ConsoleKey.Home), 4); Eq("exact", p.Current?.Value, "first");
        Type(p, " 中文"); Eq("p1", p.Current?.Value, "space-separated cross-field match");
        Type(p, "👩‍💻"); Eq(0, p.View.Items.Count, "no result"); Eq(null, p.Current, "no invisible selection");
        p.Edit(Key(ConsoleKey.Backspace), 2); Eq("abc 中文", p.View.Query, "grapheme backspace");
        Eq("p1", p.Current?.Value, "restored match");
        p.Edit(Key(ConsoleKey.PageDown), int.MaxValue); Eq(0, p.View.Selected, "overflow safe");
        p.Edit(Key(ConsoleKey.Enter), 2); p.Edit(Key(ConsoleKey.Escape), 2); Eq("abc 中文", p.View.Query, "activation and cancel outside picker");
        var empty = new FuiPicker("文件", []); Eq(0, empty.View.Items.Count, "empty provider"); Eq("暂无选项", empty.View.Message, "empty provider is not a match");
        var list = Enumerable.Range(0, 15000).Select(i => new FuiChoice($"id-{i:00000}", $"Path-{i:00000}")).ToArray();
        var large = new FuiPicker("文件", list);
        Type(large, "id-14999"); Eq("id-14999", large.Current?.Value, "large list finds full-fidelity ID");
        var direct = new FuiPicker("会话", [new("真实-id", "相同"), new("另一个-id", "相同")]);
        direct.Edit(Key(ConsoleKey.DownArrow), 2); Eq("另一个-id", direct.Current!.Value, "identical display labels never replace selection identity");
    }

    private static void SessionTree()
    {
        FuiChoice[] branches =
        [
            new("deep", "折叠的后代", ParentSessionId: "current"),
            new("sibling", "同名", ParentSessionId: "root"),
            new("current", "当前会话", ParentSessionId: "branch"),
            new("branch", "同名", ParentSessionId: "root"),
            new("root", "旧会话"), new("orphan", "孤儿", ParentSessionId: "absent")
        ];
        var tree = new FuiPicker("会话分支树", branches, currentTreeSession: "current");
        string Visible() => string.Join(',', tree.View.Items.Select(c => c.Value));
        void Press(ConsoleKey key) => tree.Edit(Key(key), 4);
        Eq("root,sibling,branch,current,orphan", Visible(), "ancestors expanded in sibling order; orphan becomes another root");
        Eq("current", tree.Current?.Value, "current session initially selected");
        Eq(new FuiTreePosition(2, false), tree.Current!.TreePosition, "current node's children start folded");
        var frame = new TerminalLayout().Arrange(Model([]) with { Overlay = tree.View }, 72, 18);
        Eq(true, frame.Rows.Any(r => r.Ink == Ink.Selection && r.Text.Contains("    ▸ 当前会话")) &&
            frame.Rows.Any(r => r.Text.Contains("▾ 旧会话")), "disclosures, indentation and current focus are drawn");
        Press(ConsoleKey.LeftArrow); Eq("branch", tree.Current?.Value, "left from a collapsed node selects its parent");
        Press(ConsoleKey.LeftArrow); Eq("root,sibling,branch,orphan", Visible(), "left folds only the selected subtree");
        Type(tree, "折叠的"); Eq("deep", tree.Current?.Value, "search finds descendants of folded ancestors");
        tree.Edit(Key(ConsoleKey.U, true), 4);
        Eq("branch", tree.Current?.Value, "clearing search restores prior tree focus");
        Eq("root,sibling,branch,orphan", Visible(), "search leaves expansion intact");
        Press(ConsoleKey.RightArrow); Eq("branch", tree.Current?.Value, "right expands in place");
        Press(ConsoleKey.RightArrow); Eq("current", tree.Current?.Value, "right from open node selects first child");
        Press(ConsoleKey.RightArrow); Press(ConsoleKey.RightArrow);
        Eq(new FuiTreePosition(3, null), tree.Current!.TreePosition, "right reaches leaf");
        Press(ConsoleKey.RightArrow); Eq("deep", tree.Current?.Value, "right on leaf stays in place");
        Press(ConsoleKey.LeftArrow); Eq("current", tree.Current?.Value, "left from leaf selects parent");
        Type(tree, "同名"); Eq("sibling,branch", Visible(), "duplicate names retain separate IDs in global search");
        Press(ConsoleKey.DownArrow); Eq("branch", tree.Current?.Value, "duplicate names select by real ID");

        var broken = new FuiPicker("树", [
            new("leaf", "leaf", ParentSessionId: "b"), new("b", "b", ParentSessionId: "a"),
            new("a", "a", ParentSessionId: "c"), new("c", "c", ParentSessionId: "b"),
            new("self", "self", ParentSessionId: "self")
        ], currentTreeSession: "c");
        Eq("a,b,leaf,c,self", string.Join(',', broken.View.Items.Select(c => c.Value)), "cycle repaired at its lowest ID; every node reachable once");
        Eq(new FuiTreePosition(2, null), broken.Current!.TreePosition, "selection in past cyclic metadata");

        const int count = 16000;
        var chain = Enumerable.Range(0, count).Reverse().Select(i =>
            new FuiChoice(i.ToString(), "节点" + i, ParentSessionId: i == 0 ? null : (i - 1).ToString())).ToArray();
        var deep = new FuiPicker("树", chain, currentTreeSession: (count - 1).ToString());
        Eq(count, deep.View.Items.Count, "deep chain is expanded iteratively");
        Eq(new FuiTreePosition(count - 1, null), deep.Current!.TreePosition, "deepest real depth retained");
        frame = new TerminalLayout().Arrange(Model([]) with { Overlay = deep.View }, 50, 9);
        Eq(true, frame.Rows.Any(r => r.Ink == Ink.Selection && r.Text.Contains("节点15999")), "bounded indentation keeps deeply nested labels visible");
    }

    private static void ComposerActions()
    {
        var editor = new Composer();
        editor.Replace("old"); editor.Take(); editor.Replace("中👩‍💻e\u0301", 2);
        Eq(6, editor.Cursor, "replace cannot split joined emoji"); Eq("old", editor.History.Single(), "replace does not submit");
        editor.Replace("ok", -5); Eq(0, editor.Cursor, "lower coordinate clamp"); editor.Replace("ok", 20); Eq(2, editor.Cursor, "upper clamp");
        editor.Replace("keep\nfirst 中 end\nlast", 12); editor.Edit(Key(ConsoleKey.U, true));
        Eq("keep\n end\nlast", editor.Text, "kill to the line start only");
        editor.Edit(Key(ConsoleKey.K, true)); Eq("keep\n\nlast", editor.Text, "kill to the line end only");
        editor.Replace("prefix 中x"); editor.Edit(Key(ConsoleKey.W, true)); Eq("prefix ", editor.Text, "kill previous word");
        editor.Clear(); Eq(1, editor.History.Count, "history stays after clear");
    }

    private static TuiView Model(IReadOnlyList<TuiLine> lines) => new("/demo/中文", "S1", "example", "就绪", lines, "/ses", 4, 0, null);

    private static void Panel()
    {
        var layout = new TerminalLayout();
        var options = Enumerable.Range(0, 150).Select(i => new FuiChoice($"id-{i}", $"会话 {i}", $"详情 {i}\x1b]52;c;unsafe\a")).ToArray();
        var model = Model([new("info", "对话内容")]) with { Overlay = new("选择器\x1b[2J", "查询", options, 138) };
        var wide = layout.Arrange(model, 60, 17);
        Eq(true, wide.Rows.Any(r => r.Ink == Ink.Selection && r.Text.Contains("会话 138")), "selected row on screen even late in list");
        Eq(true, wide.Rows.Any(r => r.Ink == Ink.PanelTitle && r.Text.Contains("查询") && r.Text.Contains("139/150")), "query/count header");
        Eq(true, wide.Rows.Any(r => r.Ink == Ink.Quiet && r.Text.Contains("详情 138")), "detail for selected item");
        Eq(true, wide.PickerRows > 1 && wide.PickerRows < options.Length, "bounded item viewport");
        Eq(true, wide.Rows.Any(r => r.Text.Contains("对话内容")) && wide.Rows[^1].Text.EndsWith("/ses"), "transcript and input retained");
        var split = layout.Arrange(model, 100, 17);
        Eq(true, split.Rows.Any(r => r.Ink == Ink.PanelTitle && r.Text.Contains("│") && r.Text.Contains("详情")), "wide picker exposes a detail pane");
        Eq(true, split.Rows.Any(r => r.Text.Contains("│") && r.Text.Contains("会话 138")), "selected row is visible in the split list");
        Eq(true, split.Rows.Any(r => r.Text.Contains("详情 138")), "selected detail is visible in the split pane");
        Eq(true, split.Rows.All(r => TerminalText.Width(r.Text) < 100), "split picker never overflows the viewport");
        Eq(false, wide.Rows.Any(r => r.Text.Contains("unsafe") || r.Text.Contains('\x1b')), "metadata cleaned before style");
        var focus2 = layout.Arrange(model with { Overlay = model.Overlay! with { Selected = 139 } }, 60, 17);
        var paint = TerminalSurface.Diff(wide, focus2, true);
        Eq(false, paint.Contains("\x1b[2J") || paint.Contains("/ses"), "changing selection need not repaint the composer/full screen");
        foreach (var (w,h) in new[]{(1,1),(8,2),(20,4),(34,6),(100,35)})
        {
            var small = layout.Arrange(model, w, h);
            Eq(h, small.Rows.Length, "resize height");
            Eq(true, small.Rows.All(r => TerminalText.Width(r.Text)<w && !r.Text.Any(char.IsControl)), "safe viewport width");
            Eq(Ink.Composer, small.Rows[^1].Ink, "composer stays anchored even at one row");
            if (h >= 2) Eq(true, small.Rows.Any(r => r.Ink == Ink.Selection), "selected candidate remains visible on tiny screens");
        }
        var empty = layout.Arrange(model with { Overlay = new("查找", "none", [], 10, "没有结果") }, 40, 9);
        Eq(true, empty.Rows.Any(r => r.Text.Contains("没有结果")), "empty panel message");
        var approval = layout.Arrange(model with { Approval="真实请求" }, 60, 17);
        Eq(true, approval.Rows.Any(r=>r.Ink==Ink.Approval) && approval.Rows.All(r=>r.Ink is not (Ink.PanelTitle or Ink.Selection)) && approval.PickerRows==0, "approval preempts picker");
    }

    private static void Reading()
    {
        var layout = new TerminalLayout();
        const string markdown = "# 标题\n正文\n~~~cs\n    var s = \"# literal\";\n# is not heading\n~~~\n## 结束";
        var lines = new TuiLine[]{new("assistant",markdown), new("tool","read [完成]\nONLY-IN-TOOL\x1b]0;UNTRUSTED\a"), new("thinking","ONLY-IN-THOUGHT")};
        var view = Model(lines);
        var normal = layout.Arrange(view, 100, 18);
        Eq(2, normal.Rows.Count(r=>r.Ink==Ink.Heading), "headings outside code");
        Eq(2, normal.Rows.Count(r=>r.Ink==Ink.CodeFence), "two fences");
        Eq(true, normal.Rows.Any(r=>r.Ink == Ink.Code && r.Text.Contains("    var s")) && normal.Rows.Any(r=>r.Ink == Ink.Code && r.Text.Contains("# is not heading")), "indent and literal hashes in code survive");
        Eq(false, normal.Rows.Any(r=>r.Text.Contains("ONLY-IN-TOOL")), "tool body folded by default");
        Eq(true, normal.Rows.Any(r=>r.Text.Contains("ONLY-IN-THOUGHT")), "thinking on by default");
        var expanded = layout.Arrange(view with { ExpandTools=true, ShowThinking=false }, 100, 18);
        Eq(true, expanded.Rows.Any(r=>r.Text.Contains("ONLY-IN-TOOL")) && expanded.Rows.All(r=>!r.Text.Contains("UNTRUSTED") && !r.Text.Contains("ONLY-IN-THOUGHT")), "expanded output sanitized; hide thinking without removing source");
        Eq(markdown, lines[0].Text, "original assistant transcript never modified");
        Eq(true, normal.Rows.SequenceEqual(layout.Arrange(view, 100, 18).Rows), "restoring both modes restores all rows");

        var live = new TerminalLayout(); var text = "";
        foreach (var part in new[]{"#", " title", "\n`", "`", "`\ntext", "\n#", " still code", "\n```", "x", "\n```\n##", " done", "\x1b]0;", "gone\a", "\n    code", "\n~~~cs\n", new string('x',200),"\n~~~"})
        {
            text+=part; var snap=Model([new("assistant",text)]);
            var a=live.Arrange(snap, 20, 40); var b=new TerminalLayout().Arrange(snap, 20, 40);
            Eq(true, a.Rows.SequenceEqual(b.Rows), "streaming Markdown equals a freshly reopened transcript");
        }
        var many=Enumerable.Range(0,30).Select(i=>new TuiLine(i==12?"thinking":"info", $"row-{i:00}")).ToArray();
        var transcript=Model(many) with { ShowThinking=false };
        live.Arrange(transcript,50,8);
        var jumped=live.Arrange(transcript with {Scroll=live.ScrollToLine(10)},50,8);
        Eq(true,jumped.Rows[1].Text.Contains("row-10"),"jump to original message after wrapping");
        jumped=live.Arrange(transcript with{Scroll=live.ScrollToLine(12)},50,8);
        Eq(true,jumped.Rows[1].Text.Contains("row-13"),"hidden message maps to the next visible row");
        Eq(0,live.ScrollToLine(int.MaxValue),"jump to bottom clamped");
    }

    private static ConsoleKeyInfo Key(ConsoleKey key,bool ctrl=false)=>new('\0',key,false,false,ctrl);
    private static void Type(FuiPicker p,string text) { foreach(var c in text) p.Edit(new(c,(ConsoleKey)0,false,false,false),4); }
    private static void Eq<T>(T expected,T actual,string name)
    {
        if (!EqualityComparer<T>.Default.Equals(expected,actual)) throw new InvalidOperationException($"{name}: expected [{expected}], got [{actual}]");
    }
}
