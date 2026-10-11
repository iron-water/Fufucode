using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Fufucode.Core;

namespace Fufucode.App.ViewModels;

public sealed partial class WorkspacePanelViewModel
{
    private int _diffGeneration;
    [ObservableProperty] private string _diffStatus = "选择文件查看差异";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDiff))]
    private string _diffText = "";
    public bool HasDiff => DiffText.Length > 0;

    internal async Task RefreshSelectedDiffAsync()
    {
        var generation = ++_diffGeneration;
        var selected = SelectedChange;
        var identity = _contextIdentity;
        DiffText = "";
        DiffStatus = selected is null ? "选择文件查看差异" : "正在读取差异…";
        if (selected is null) return;
        try
        {
            var root = ChangesRoot.Length > 0 ? ChangesRoot : ProjectPath;
            var diff = await DiffQuery.FileAsync(root, selected.Path);
            if (_disposed || generation != _diffGeneration || identity != _contextIdentity) return;
            if (diff.Error is not null) { DiffStatus = "差异不可用：" + diff.Error; return; }
            if (diff.IsBinary == true) { DiffStatus = "二进制文件，无法显示逐行差异"; return; }
            var text = new StringBuilder();
            foreach (var hunk in diff.Hunks)
            {
                text.AppendLine(hunk.Header);
                var oldLine = hunk.OldStart; var newLine = hunk.NewStart;
                foreach (var line in hunk.Lines)
                {
                    text.Append(line.Kind == '+' ? "" : oldLine.ToString()).Append('\t')
                        .Append(line.Kind == '-' ? "" : newLine.ToString()).Append('\t')
                        .Append(line.Kind).AppendLine(line.Text);
                    if (line.Kind is ' ' or '-') oldLine++;
                    if (line.Kind is ' ' or '+') newLine++;
                }
            }
            DiffText = text.ToString();
            var lines = diff.Hunks.SelectMany(h => h.Lines).ToArray();
            DiffStatus = (SelectedScope == WorkspaceChangeScope.SessionWrites ? "Git 当前工作树差异 · 非会话前后快照 · " : "") +
                (lines.Length == 0 ? "没有文本差异" : $"+{lines.Count(l => l.Kind == '+')} / -{lines.Count(l => l.Kind == '-')}") +
                (diff.Truncated ? " · 显示已截断" : "");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            if (generation == _diffGeneration && identity == _contextIdentity) DiffStatus = "差异读取失败：" + error.Message;
        }
    }
}
