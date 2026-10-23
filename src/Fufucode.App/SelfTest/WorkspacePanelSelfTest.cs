using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Fufucode.App.Infrastructure;
using Fufucode.App.ViewModels;
using Fufucode.Core;

namespace Fufucode.App.SelfTest;

/// <summary>Run on the headless UI thread: A8 semantics, asynchronous context guard, and tab/path behavior.</summary>
public static class WorkspacePanelSelfTest
{
    public static async Task RunAsync(Action<string> pass, Action<string> fail)
    {
        void Check(bool value, string description)
        {
            if (value) pass("W5-R2 工作视图：" + description);
            else fail("W5-R2 工作视图：" + description);
        }

        var root = Path.Combine(Path.GetTempPath(), "fufucode-workspace-panel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var directory = Directory.CreateDirectory(Path.Combine(root, "folder"));
            var filePath = Path.Combine(directory.FullName, "可读文件.txt");
            File.WriteAllText(filePath, "fixture");
            var clipboard = new RecordingClipboardService();
            using var vm = new WorkspacePanelViewModel(clipboard);
            vm.AttachMock(root);
            await vm.RefreshAsync();
            Check(vm.IsMock && vm.Changes.Count == 4 && vm.ChangesError is null, "mock 通过 A8 DTO 同一投影呈现四个文件");
            var newRow = vm.Changes.Single(x => x.IsNew);
            var deletedRow = vm.Changes.Single(x => x.IsDeleted);
            var unknown = vm.Changes.Single(x => x.Source.Kind == "written");
            Check(newRow.Source.IsNew == true && deletedRow.IsBinary && deletedRow.Source.IsDeleted == true,
                "new / deleted / binary 来自 DTO 实证");
            Check(unknown.Source.IsNew is null && unknown.Source.IsBinary is null &&
                unknown.UnknownFlags.Contains("new ?") && unknown.UnknownFlags.Contains("binary ?"), "null 保持未知，不转 false 或 0");
            vm.OpenChangesCommand.Execute(null);
            vm.SelectedChange = newRow;
            var changesTab = vm.SelectedTab!;
            var panelId = changesTab.PanelId;
            vm.CloseTabCommand.Execute(changesTab);
            vm.OpenChangesCommand.Execute(null);
            Check(ReferenceEquals(vm.SelectedTab, changesTab) && vm.SelectedTab.PanelId == panelId &&
                ReferenceEquals(vm.SelectedChange, newRow), "关闭并重开 tab 保留身份与已选文件");
            await vm.CopyChangePathCommand.ExecuteAsync(null);
            Check(clipboard.LastCopied == newRow.FullPath, "Changes 复制的是完整路径");

            vm.ApplyReport(new("workspace", root, false, [], "not-a-git-worktree"));
            Check(vm.HasChangesError && !vm.IsChangesEmpty && !vm.HasChanges, "Git 不可用与空成功结果分开");
            vm.ApplyReport(new("workspace", root, true, [], null));
            Check(vm.IsChangesEmpty && !vm.HasChangesError, "真实空列表显示空状态");
            Check(vm.IsChangeListEmpty && vm.ChangeGroups.Count == 0, "空变更列表同时清空分组");

            // 目录分组、搜索与 ± 汇总
            vm.ApplyReport(new("workspace", root, true,
            [
                new("src/Workbench.cs", null, "modified", " ", "M", true, false, [], 12, 3),
                new("src/PanelLayout.cs", null, "added", "A", " ", true, false, [], 40, 0),
                new("notes/draft.md", null, "written", null, null, true, false, [], null, null),
            ], null));
            var srcGroup = vm.ChangeGroups.Single(g => g.Folder == "src");
            Check(vm.ChangeGroups.Count == 2 && srcGroup.Files.Count == 2 &&
                  srcGroup.Files.All(f => f.Folder == "src") && vm.AddedTotalText == "+52" &&
                  vm.RemovedTotalText == "−3" && vm.HasTotals,
                "变更列表按目录分组，汇总只累加真实行数");
            Check(vm.Changes.Single(f => f.Path == "notes/draft.md").HasLineCounts == false,
                "行数未知的文件不显示 ± 0");
            vm.ChangeFilter = "panel";
            Check(vm.ChangeGroups.Count == 1 && vm.ChangeGroups[0].Files.Single().Name == "PanelLayout.cs",
                "筛选按路径片段过滤并重算分组");
            vm.ChangeFilter = "不存在的文件";
            Check(vm.ChangeGroups.Count == 0 && vm.HasFilteredOut, "筛选无结果时不冒充工作区为空");
            vm.ChangeFilter = "";

            vm.OpenFilesCommand.Execute(null);
            await vm.RefreshFilesAsync();
            await vm.BrowseFileCommand.ExecuteAsync(vm.Files.Single(x => x.IsDirectory));
            Check(vm.Files.Count == 1 && vm.CanGoUp && vm.CurrentDirectory == directory.FullName, "Files 逐层浏览实际项目目录");
            await vm.BrowseFileCommand.ExecuteAsync(vm.Files.Single());
            await vm.CopyFilePathCommand.ExecuteAsync(null);
            Check(clipboard.LastCopied == filePath && vm.SelectedFile?.Name == "可读文件.txt", "Files 选择与路径复制保留 Unicode");
            await vm.GoUpCommand.ExecuteAsync(null);
            Check(vm.CurrentDirectory == root && !vm.CanGoUp, "上一级停在项目根");

            var stale = new TaskCompletionSource<ChangeRangeReport>();
            vm.AttachContext(root, "old-session", _ => stale.Task, _ => stale.Task);
            var oldRefresh = vm.RefreshChangesAsync();
            vm.AttachContext(root, "new-session", _ => Task.FromResult(new ChangeRangeReport("workspace", root, true, [], null)),
                _ => Task.FromResult(new ChangeRangeReport("session-writes", root, true, [], null)));
            await vm.RefreshChangesAsync();
            stale.SetResult(WorkspacePanelViewModel.CreateMockReport(root, WorkspaceChangeScope.Workspace));
            await oldRefresh;
            Check(vm.SessionId == "new-session" && vm.Changes.Count == 0 && vm.IsChangesEmpty, "旧会话的延迟查询不能污染新上下文");

            var called = string.Empty;
            vm.AttachContext(root, "scope-test", _ => { called = "workspace"; return Task.FromResult(new ChangeRangeReport("workspace", root, true, [], null)); },
                _ => { called = "session-writes"; return Task.FromResult(new ChangeRangeReport("session-writes", root, true, [], null)); });
            vm.SessionScopeCommand.Execute(null);
            await vm.RefreshChangesAsync();
            Check(called == "session-writes" && vm.IsSessionScope && vm.ScopeDescription.Contains("write/edit"),
                "会话口径消费独立查询，范围说明不夸大 bash 副作用");

            for (var index = 0; index < 301; index++) File.WriteAllText(Path.Combine(root, $"bounded-{index:D3}.txt"), "fixture");
            await vm.RefreshFilesAsync();
            Check(vm.Files.Count == 300 && vm.IsDirectoryTruncated, "单层目录加载有 300 项上限并显示截断说明");
        }
        finally
        {
            Directory.Delete(root, true);
        }

        DiffRenderingChecks(pass, fail);
    }

    /// <summary>逐行 diff 的呈现语义：行号、增删标记、词级强调、上下文折叠与展开、语法着色降级。</summary>
    private static void DiffRenderingChecks(Action<string> pass, Action<string> fail)
    {
        void Check(bool value, string description)
        {
            if (value) pass("W5-R2 变更 diff：" + description);
            else fail("W5-R2 变更 diff：" + description);
        }

        // 行级强调：等量替换时只标出真正变化的词，而不是整行。
        Check(WordDiff.Emphasis("    var total = a + b;", "    var total = a + c;", out var oldRange, out var newRange) &&
              "    var total = a + b;".Substring(oldRange.Start, oldRange.Length) == "b" &&
              "    var total = a + c;".Substring(newRange.Start, newRange.Length) == "c",
            "等量替换只强调变化的词");
        Check(WordDiff.Emphasis("total = amount + 1;", "total = amount + 2;", out var oldWord, out var newWord) &&
              "total = amount + 1;".Substring(oldWord.Start, oldWord.Length) == "1" &&
              "total = amount + 2;".Substring(newWord.Start, newWord.Length) == "2",
            "强调区间指向真正改动的记号而不是行首");
        Check(!WordDiff.Emphasis("same line", "same line", out _, out _),
            "完全相同的行不产生强调");
        Check(string.Concat(WordDiff.Tokenize("a.b(c, d);").Select(t => t.Text)) == "a.b(c, d);",
            "词级切分可无损还原原文");

        var hunk = new DiffHunk("@@ -1,3 +1,3 @@", 1, 3, 1, 3,
        [
            new Core.DiffLine(' ', "context one"),
            new Core.DiffLine('-', "value = 1"),
            new Core.DiffLine('+', "value = 2"),
            new Core.DiffLine(' ', "context two"),
        ]);
        var diff = new FileDiff("a.txt", null, false, false, [hunk], null);
        var sides = new DiffSides("a.txt", "context one\nvalue = 1\ncontext two", "context one\nvalue = 2\ncontext two", false, null);
        var model = DiffViewModel.Build(diff, sides, "txt", dark: true);

        Check(model.AddedLines == 1 && model.RemovedLines == 1 && model.SummaryText == "+1  −1",
            "增减行数来自 diff 本身");
        var removed = model.Rows.Single(r => r.IsRemoved);
        var added = model.Rows.Single(r => r.IsAdded);
        Check(removed.OldNumber == "2" && removed.NewNumber.Length == 0 && removed.Marker == "−",
            "删除行只有旧行号");
        Check(added.NewNumber == "2" && added.OldNumber.Length == 0 && added.Marker == "+",
            "新增行只有新行号");
        var contextRows = model.Rows.Where(r => r.IsContext).ToArray();
        Check(contextRows.Length == 2 && contextRows[0].OldNumber == "1" && contextRows[0].NewNumber == "1" &&
              contextRows[1].OldNumber == "3" && contextRows[1].NewNumber == "3",
            "上下文行两侧行号都有且递增");
        Check(model.Rows.First().IsHeader && model.Rows.First().HeaderText == "-1,3 +1,3",
            "hunk 头作为独立行呈现且去掉 @ 记号");

        // 折叠：长上下文只保留两端，中间给一行可展开提示。
        var longSide = string.Join("\n", Enumerable.Range(1, 40).Select(i => "line " + i));
        var longHunk = new DiffHunk("@@ -1,40 +1,40 @@", 1, 40, 1, 40,
        [
            .. Enumerable.Range(1, 15).Select(i => new Core.DiffLine(' ', "line " + i)),
            new Core.DiffLine('+', "line 16 changed"),
            .. Enumerable.Range(17, 24).Select(i => new Core.DiffLine(' ', "line " + i)),
        ]);
        var longModel = DiffViewModel.Build(new FileDiff("b.txt", null, false, false, [longHunk], null),
            new DiffSides("b.txt", longSide, longSide, Truncated: false, Error: null), "txt", dark: true);
        var gap = longModel.Rows.FirstOrDefault(r => r.IsGap);
        Check(gap is not null && longModel.Rows.Count < 40 && gap.HiddenLines > 0,
            "长上下文折叠成一行省略提示");
        var beforeCount = longModel.Rows.Count;
        Check(gap is not null && longModel.ExpandGap(gap, new DiffSides("b.txt", longSide, longSide, Truncated: false, Error: null)) &&
              longModel.Rows.Count > beforeCount,
            "点击省略提示展开真实上下文");

        // 两侧文本缺失（未跟踪/读取失败）时仍要能出 diff，只是没有语法色与展开。
        var bare = DiffViewModel.Build(diff, null, "cs", dark: true);
        Check(bare.AddedLines == 1 && bare.Rows.Count > 0 && bare.Rows.Any(r => r.IsBody),
            "缺少两侧文本时保留行结构，不因无法着色丢掉 diff");

        // 语法着色：认识的扩展名出多色片段，不认识的整体退化为单色。
        var colored = SyntaxHighlighter.Highlight("public class Foo { }", "cs", dark: true);
        Check(colored is { Count: 1 } && colored[0].Any(span => span.Color is not null),
            "C# 代码行产出带颜色的片段");
        Check(SyntaxHighlighter.Highlight("plain", "unknownext", dark: true) is null,
            "未收录的扩展名不着色，由调用方降级");
        Check(SyntaxHighlighter.Highlight("a", "cs", dark: false) is { } light &&
              !(light[0].Any(span => span.Color is not null) &&
                colored![0].Where(s => s.Color is not null).Select(s => s.Color!.Value).SequenceEqual(
                    light[0].Where(s => s.Color is not null).Select(s => s.Color!.Value))),
            "深浅主题使用不同调色板");
    }
}
