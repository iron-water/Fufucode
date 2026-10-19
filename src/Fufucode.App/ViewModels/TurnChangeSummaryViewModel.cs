using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Fufucode.App.Infrastructure;
using Fufucode.Core.Backend;

namespace Fufucode.App.ViewModels;

/// <summary>单个文件的改动量。写入按新增行统计；编辑按替换前后的行数分别统计。</summary>
/// <summary>内联 diff 的一行，带语义标记供 XAML 上色。</summary>
public sealed class DiffLineViewModel
{
    public DiffLineViewModel(DiffLine line)
    {
        Text = (line.Kind == '+' ? "+ " : line.Kind == '-' ? "- " : "  ") + line.Text;
        IsAdded = line.Kind == '+';
        IsRemoved = line.Kind == '-';
        IsNote = line.Kind == '…';
    }

    public string Text { get; }
    public bool IsAdded { get; }
    public bool IsRemoved { get; }
    public bool IsNote { get; }
}

public sealed partial class TurnFileChangeViewModel : ViewModelBase
{
    private readonly List<string> _toolCallIds = new();

    public TurnFileChangeViewModel(string path, int additions, int deletions)
    {
        Path = path;
        Additions = additions;
        Deletions = deletions;
    }

    public string Path { get; }

    public int Additions { get; private set; }

    public int Deletions { get; private set; }

    public bool HasAdditions => Additions > 0;

    public bool HasDeletions => Deletions > 0;

    public string AdditionsText => "+" + Additions;

    public string DeletionsText => "-" + Deletions;

    internal void Merge(int additions, int deletions)
    {
        Additions += additions;
        Deletions += deletions;
    }

    internal void AddToolCall(string toolCallId)
    {
        if (!string.IsNullOrEmpty(toolCallId)) _toolCallIds.Add(toolCallId);
    }

    /// <summary>展开后展示的内联 diff；只在展开时计算一次。</summary>
    public ObservableCollection<DiffLineViewModel> DiffLines { get; } = new();

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _hasDiff;

    [ObservableProperty]
    private string _diffNote = string.Empty;

    public bool HasNote => DiffNote.Length > 0;

    /// <summary>折叠箭头用正向属性，避免运行期绑定不支持取反。</summary>
    public bool IsCollapsed => !IsExpanded;

    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(IsCollapsed));

    partial void OnDiffNoteChanged(string value) => OnPropertyChanged(nameof(HasNote));

    /// <summary>用第一次改动前的备份与当前磁盘内容做对比。</summary>
    public void ToggleDiff()
    {
        if (IsExpanded) { IsExpanded = false; return; }

        if (DiffLines.Count == 0) LoadDiff();
        if (DiffLines.Count == 0) return;
        IsExpanded = true;
    }

    private void LoadDiff()
    {
        DiffLines.Clear();
        // 同一文件可能被多次修改：用最早一次调用拿到"本轮之前"的原始内容。
        var first = _toolCallIds.FirstOrDefault();
        if (first is null)
        {
            DiffNote = "没有可用的改动前快照";
            return;
        }

        if (!FileChangeJournal.TryReadDiffInputs(first, out var before, out var after))
        {
            DiffNote = "没有可用的改动前快照";
            return;
        }

        var lines = LineDiff.Compute(before, after);
        var added = 0;
        var removed = 0;
        foreach (var line in lines)
        {
            DiffLines.Add(new DiffLineViewModel(line));
            if (line.Kind == '+') added++;
            else if (line.Kind == '-') removed++;
        }

        HasDiff = DiffLines.Count > 0;
        if (HasDiff && added == 0 && removed == 0) DiffNote = "内容无变化";
    }
}

/// <summary>
/// 回合结束时的变更总结卡（对齐 Codex）：标题是"已编辑 N 个文件"，
/// 列出每个文件的增减行数，默认只显示前三条，可展开。
///
/// 撤销走 <see cref="FileChangeJournal"/>：写入前保存的上一版内容，
/// 因此对 write_file 的整文件覆盖同样可以还原，而不是只能删掉新文件。
/// </summary>
public sealed partial class TurnChangeSummaryViewModel : ViewModelBase
{
    /// <summary>折叠时展示的文件行数，与 Codex 一致。</summary>
    private const int CollapsedRows = 3;

    private readonly List<string> _toolCallIds = new();

    public ObservableCollection<TurnFileChangeViewModel> Files { get; } = new();

    public ObservableCollection<TurnFileChangeViewModel> VisibleFiles { get; } = new();

    public bool HasChanges => Files.Count > 0;

    public string HeaderText => "已编辑 " + Files.Count + " 个文件";

    public bool HasHiddenFiles => Files.Count > CollapsedRows;

    public string MoreLabel => !HasHiddenFiles
        ? string.Empty
        : IsExpanded
            ? "收起"
            : "再显示 " + (Files.Count - CollapsedRows) + " 个文件";

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private string _undoFeedback = string.Empty;

    [ObservableProperty]
    private bool _isUndone;

    public bool CanUndo => _toolCallIds.Count > 0 && !IsUndone;

    public bool HasUndoFeedback => UndoFeedback.Length > 0;

    partial void OnIsExpandedChanged(bool value) => RefreshVisibleFiles();

    partial void OnUndoFeedbackChanged(string value) => OnPropertyChanged(nameof(HasUndoFeedback));

    partial void OnIsUndoneChanged(bool value) => OnPropertyChanged(nameof(CanUndo));

    internal void Add(string toolCallId, string path, int additions, int deletions)
    {
        if (!string.IsNullOrWhiteSpace(toolCallId)) _toolCallIds.Add(toolCallId);

        var existing = Files.FirstOrDefault(f => string.Equals(f.Path, path, StringComparison.Ordinal));
        if (existing is null)
        {
            var created = new TurnFileChangeViewModel(path, additions, deletions);
            created.AddToolCall(toolCallId);
            Files.Add(created);
        }
        else
        {
            existing.Merge(additions, deletions);
            existing.AddToolCall(toolCallId);
        }

        NotifyShapeChanged();
    }

    internal void Clear()
    {
        Files.Clear();
        VisibleFiles.Clear();
        _toolCallIds.Clear();
        UndoFeedback = string.Empty;
        IsUndone = false;
        IsExpanded = false;
        NotifyShapeChanged();
    }

    internal void ToggleExpanded()
    {
        if (!HasHiddenFiles) return;
        IsExpanded = !IsExpanded;
    }

    /// <summary>把本回合改过的文件恢复到各自在首次写入前的状态。</summary>
    internal void Undo()
    {
        if (!CanUndo) return;
        var restored = FileChangeJournal.Undo(_toolCallIds);
        IsUndone = true;
        UndoFeedback = restored > 0 ? "已撤销 " + restored + " 个文件" : "没有可恢复的备份";
    }

    private void NotifyShapeChanged()
    {
        OnPropertyChanged(nameof(HasChanges));
        OnPropertyChanged(nameof(HeaderText));
        OnPropertyChanged(nameof(HasHiddenFiles));
        OnPropertyChanged(nameof(MoreLabel));
        OnPropertyChanged(nameof(CanUndo));
        RefreshVisibleFiles();
    }

    private void RefreshVisibleFiles()
    {
        VisibleFiles.Clear();
        var take = IsExpanded || !HasHiddenFiles ? Files.Count : CollapsedRows;
        foreach (var file in Files.Take(take)) VisibleFiles.Add(file);
        OnPropertyChanged(nameof(MoreLabel));
    }

    /// <summary>
    /// 从工具入参推算改动行数。
    /// 编辑：old_string 的行数算删除、new_string 的行数算新增；
    /// 写入：content 的行数算新增（覆盖前的行数不在入参里，故不猜测删除）。
    /// </summary>
    internal static (int Additions, int Deletions) EstimateStats(JsonElement arguments, bool isWrite)
    {
        static int Lines(JsonElement root, params string[] properties)
        {
            foreach (var property in properties)
            {
                if (!root.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String) continue;
                var text = value.GetString();
                if (string.IsNullOrEmpty(text)) return 0;
                var count = 1;
                foreach (var ch in text) if (ch == '\n') count++;
                return count;
            }
            return 0;
        }

        // 写入类工具的入参字段也因后端而异；按常见命名依次尝试。
        return isWrite
            ? (Lines(arguments, "content", "text", "file_text"), 0)
            : (Lines(arguments, "new_string", "new_str", "replacement"),
               Lines(arguments, "old_string", "old_str", "original"));
    }
}
