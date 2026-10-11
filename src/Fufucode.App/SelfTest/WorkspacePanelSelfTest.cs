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
    }
}
