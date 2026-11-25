using System.Diagnostics;
using Fufucode.Fui;

namespace Fufucode.Fui.Tests;

internal static class FuzzyPickerTests
{
    internal static void Run()
    {
        Ranking();
        ContinuityAndCombination();
        StableAndEmpty();
        Unicode();
        LargeList();
        Identity();
        TreeFocus();
    }

    private static void Ranking()
    {
        FuiChoice[] choices =
        [
            new("abbreviation", "Alpha Beta Charlie"),
            new("detail", "anything", "abc"),
            new("contains", "xabcz"),
            new("boundary", "has abc token"),
            new("prefix", "abc tail"),
            new("exact", "abc")
        ];
        Eq("exact,prefix,boundary,contains,detail,abbreviation", Values(new("", choices, "abc")),
            "exact/prefix/word-boundary/contains/detail/abbreviation order");

        choices =
        [
            new("contains", "xchatx"),
            new("camel", "NativeChatService"),
            new("prefix", "chat panel")
        ];
        Eq("prefix,camel,contains", Values(new("", choices, "chat")),
            "camel boundary is stronger than an interior fragment but below a field prefix");
        var typed = new FuiPicker("", choices);
        Type(typed, "chat");
        Eq("prefix,camel,contains", Values(typed), "incremental search preserves word and camel ranking");
        Eq("camel", Values(new("", [choices[1]], "ncs")), "camel initials match as an abbreviation");

        choices =
        [
            new("late", new string('x', 40) + "abc"),
            new("early", new string('x', 30) + "abc")
        ];
        Eq("early,late", Values(new("", choices, "abc")), "earlier interior fragments rank first");
    }

    private static void ContinuityAndCombination()
    {
        FuiChoice[] choices =
        [
            new("scattered", "xaxbxc"),
            new("gapped-initials", "Alpha Xray Bravo Charlie"),
            new("compact-initials", "Alpha Bravo Charlie"),
            new("contains", "xxabcxx"),
            new("boundary", "x abc")
        ];
        Eq("boundary,contains,compact-initials,gapped-initials,scattered", Values(new("", choices, "abc")),
            "contiguous fragments precede compact initials, gapped initials and scattered subsequences");

        choices =
        [
            new("detail-only", "other", "native special audit"),
            new("special-session", "Native runtime", "audit log"),
            new("missing-field", "Native runtime", "other")
        ];
        Eq("special-session,detail-only", Values(new("", choices, "native special audit")),
            "one query can combine label, value and detail matches");
    }

    private static void StableAndEmpty()
    {
        FuiChoice[] choices = [new("third", "third"), new("first", "first"), new("second", "second")];
        var picker = new FuiPicker("", choices);
        Eq("third,first,second", Values(picker), "empty query preserves provider order");

        FuiChoice[] ties = [new("first", "match one"), new("second", "match two"), new("third", "match three")];
        var stable = new FuiPicker("", ties, "match");
        Eq("first,second,third", Values(stable), "stable ties preserve provider order");
        Eq(true, ReferenceEquals(stable.View.Items, stable.View.Items), "repeated navigation reuses ranked matches");
        stable.Edit(Key(ConsoleKey.U, true), 4);
        Eq("first,second,third", Values(stable), "clearing a narrow query restores provider order");
    }

    private static void Unicode()
    {
        FuiChoice[] choices =
        [
            new("component", "团队👩会议"),
            new("emoji", "团队👩‍💻会议"),
            new("composed", "café"),
            new("decomposed", "cafe\u0301"),
            new("han", "中文团队")
        ];
        Eq("emoji", Values(new("", choices, "👩‍💻")), "joined emoji is one search grapheme");
        Eq("component", Values(new("", choices, "👩")), "a component emoji never splits a joined emoji grapheme");
        Eq("han", Values(new("", choices, "中文")), "Chinese text matches by grapheme");
        Eq("composed,decomposed", Values(new("", choices, "é")), "canonical composed and decomposed accents match");
        Eq("composed,decomposed", Values(new("", choices, "e\u0301")), "decomposed query matches both accent forms");

        var editor = new FuiPicker("", choices, "é");
        editor.Edit(Key(ConsoleKey.Backspace), 4);
        Eq("", editor.View.Query, "backspace removes one combined grapheme");
    }

    private static void LargeList()
    {
        var choices = Enumerable.Range(0, 15000)
            .Select(i => new FuiChoice($"id-{i:00000}", $"Path-{i:00000}")).ToArray();
        var picker = new FuiPicker("", choices);
        var watch = Stopwatch.StartNew();
        Type(picker, "id-14999");
        Eq("id-14999", picker.Current?.Value, "15k candidates retain the full selection ID");
        Eq(true, watch.Elapsed < TimeSpan.FromSeconds(10), $"15k search completes within the guard ({watch.ElapsedMilliseconds} ms)");
    }

    private static void Identity()
    {
        FuiChoice[] choices = [new("真实-id", "相同"), new("另一个-id", "相同")];
        var picker = new FuiPicker("", choices, "相同");
        Eq("真实-id", picker.Current?.Value, "first exact duplicate starts selected");
        picker.Edit(Key(ConsoleKey.DownArrow), 4);
        Eq("另一个-id", picker.Current?.Value, "equal labels keep distinct IDs");
        Eq(true, ReferenceEquals(choices[1], picker.Current), "selection remains the original provider object");
        picker.Edit(Key(ConsoleKey.End), 4);
        Eq("另一个-id", picker.Current?.Value, "End retains filtered navigation");
        picker.Edit(Key(ConsoleKey.Home), 4);
        Eq("真实-id", picker.Current?.Value, "Home retains filtered navigation");
    }

    private static void TreeFocus()
    {
        FuiChoice[] branches =
        [
            new("child", "子节点", ParentSessionId: "branch"),
            new("branch", "分支"),
            new("sibling", "兄弟")
        ];
        var picker = new FuiPicker("树", branches, currentTreeSession: "child");
        Eq("child", picker.Current?.Value, "current tree row starts focused");
        Type(picker, "兄弟");
        Eq("sibling", picker.Current?.Value, "tree search can rank a different row");
        picker.Edit(Key(ConsoleKey.U, true), 4);
        Eq("child", picker.Current?.Value, "clearing tree search restores previous focus");
        Eq("branch,child,sibling", string.Join(',', picker.View.Items.Select(c => c.Value)),
            "clearing search does not collapse the tree");
    }

    private static string Values(FuiPicker picker) => string.Join(',', picker.View.Items.Select(c => c.Value));
    private static ConsoleKeyInfo Key(ConsoleKey key, bool ctrl = false) => new('\0', key, false, false, ctrl);

    private static void Type(FuiPicker picker, string text)
    {
        foreach (var c in text)
        {
            picker.Edit(new(c, (ConsoleKey)0, false, false, false), 4);
            _ = picker.View;
        }
    }

    private static void Eq<T>(T expected, T actual, string name)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{name}: expected [{expected}], got [{actual}]");
    }
}
