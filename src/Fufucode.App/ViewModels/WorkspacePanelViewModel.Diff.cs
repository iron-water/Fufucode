using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Fufucode.Core;

namespace Fufucode.App.ViewModels;

public sealed partial class WorkspacePanelViewModel
{
    private int _diffGeneration;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDiffModel))]
    private DiffViewModel? _diffModel;

    [ObservableProperty] private string _diffStatus = "选择文件查看差异";
    [ObservableProperty] private bool _isDiffLoading;
    [ObservableProperty] private bool _isDiffBinary;
    [ObservableProperty] private string _diffNote = string.Empty;
    [ObservableProperty] private bool _isWordWrapEnabled;

    partial void OnIsWordWrapEnabledChanged(bool value)
    {
        DiffModel?.Wrap = value;
        OnPropertyChanged(nameof(WordWrapText));
    }

    /// <summary>用户手动切过换行后，栏宽变化不再自动覆盖他的选择。</summary>
    public bool WordWrapUserOverride { get; private set; }
    public string WordWrapText => IsWordWrapEnabled ? "关闭自动换行" : "自动换行";

    internal void ToggleWordWrap()
    {
        WordWrapUserOverride = true;
        IsWordWrapEnabled = !IsWordWrapEnabled;
    }

    /// <summary>diff 行的最小宽度，由视图按滚动视口回填：底色才能整行铺满而不是只铺到文字末尾。</summary>
    [ObservableProperty] private double _diffRowWidth;

    /// <summary>当前文件的 HEAD/工作区全文，供词级高亮与上下文展开复用。</summary>
    internal DiffSides? DiffSides { get; private set; }

    public bool HasDiffModel => DiffModel is not null;
    public bool HasDiffNote => DiffNote.Length > 0;
    public bool HasDiffStatus => DiffStatus.Length > 0;

    partial void OnDiffNoteChanged(string value) => OnPropertyChanged(nameof(HasDiffNote));
    partial void OnDiffStatusChanged(string value) => OnPropertyChanged(nameof(HasDiffStatus));
    partial void OnDiffModelChanged(DiffViewModel? value)
    {
        if (value is not null)
        {
            value.RowWidth = DiffRowWidth;
            value.Wrap = IsWordWrapEnabled;
        }
    }

    partial void OnDiffRowWidthChanged(double value) => DiffModel?.RowWidth = value;

    /// <summary>
    /// 展开被折叠的上下文：两侧全文只读一次并缓存，
    /// 这样连续展开多段不需要反复读文件，也不会因为文件在磁盘上变了而错位。
    /// </summary>
    internal void ExpandGap(DiffViewModel model, DiffRowViewModel gap)
    {
        if (DiffSides is null) return;
        model.ExpandGap(gap, DiffSides);
    }

    /// <summary>把当前 diff 以纯文本复制出去：行号 + 标记 + 内容，和版面上看到的一致。</summary>
    internal async Task CopyDiffTextAsync()
    {
        if (DiffModel is null) return;
        var text = string.Join(Environment.NewLine, DiffModel.Rows
            .Where(row => row.IsBody)
            .Select(row => row.OldNumber.PadLeft(5) + " " + row.NewNumber.PadLeft(5) + " " + row.Marker + row.Text));
        try
        {
            await _clipboard.SetTextAsync(text);
            CopyFeedback = "已复制差异文本";
        }
        catch (Exception error) { CopyFeedback = "复制失败：" + error.Message; }
    }

    /// <summary>
    /// 逐行差异的纯文本投影（旧/新行号、标记、文本，制表符分隔）。
    /// 面板本身已经改成结构化行渲染；这里保留是因为 CLI/自测用同一份文本口径核对内容。
    /// </summary>
    public string DiffText => DiffModel is null ? string.Empty
        : string.Join(Environment.NewLine, DiffModel.Rows
            .Where(row => row.IsBody)
            .Select(row => row.OldNumber + "\t" + row.NewNumber + "\t" +
                (row.IsAdded ? "+" : row.IsRemoved ? "-" : " ") + row.Text));

    public bool HasDiff => DiffText.Length > 0;

    internal async Task RefreshSelectedDiffAsync()
    {
        var generation = ++_diffGeneration;
        var selected = SelectedChange;
        var identity = _contextIdentity;
        DiffModel = null;
        DiffSides = null;
        OnPropertyChanged(nameof(DiffText));
        OnPropertyChanged(nameof(HasDiff));
        IsDiffBinary = false;
        DiffNote = string.Empty;
        if (selected is null)
        {
            DiffStatus = "选择文件查看差异";
            IsDiffLoading = false;
            return;
        }

        DiffStatus = string.Empty;
        IsDiffLoading = true;
        var dark = IsDarkTheme();
        try
        {
            var root = ChangesRoot.Length > 0 ? ChangesRoot : ProjectPath;
            var diff = await DiffQuery.FileAsync(root, selected.Path);
            if (_disposed || generation != _diffGeneration || identity != _contextIdentity) return;

            if (diff.Error is not null)
            {
                DiffStatus = ErrorText(diff.Error);
                return;
            }
            if (diff.IsBinary == true)
            {
                IsDiffBinary = true;
                DiffStatus = "二进制文件，无法显示逐行差异";
                return;
            }

            // 两侧文本用于语法着色与上下文展开；拿不到就降级成纯色 diff，不影响行本身。
            var sides = await DiffQuery.SidesAsync(root, selected.Path);
            if (_disposed || generation != _diffGeneration || identity != _contextIdentity) return;

            var model = await Task.Run(() => DiffViewModel.Build(diff, sides, selected.Extension, dark));
            if (_disposed || generation != _diffGeneration || identity != _contextIdentity) return;

            DiffSides = sides;
            DiffModel = model;
            OnPropertyChanged(nameof(DiffText));
            OnPropertyChanged(nameof(HasDiff));
            DiffStatus = string.Empty;
            var notes = new System.Collections.Generic.List<string>();
            if (diff.Hunks.Count == 0) notes.Add("没有文本差异");
            if (diff.Truncated) notes.Add("差异过长，仅显示前一部分");
            if (sides.Truncated) notes.Add("文件过大，已跳过语法着色");
            if (SelectedScope == WorkspaceChangeScope.SessionWrites) notes.Add("显示的是 Git 当前工作树差异，不是会话前后的快照");
            DiffNote = string.Join(" · ", notes);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            if (generation == _diffGeneration && identity == _contextIdentity) DiffStatus = "差异读取失败：" + error.Message;
        }
        finally
        {
            if (generation == _diffGeneration && !_disposed) IsDiffLoading = false;
        }
    }
}
