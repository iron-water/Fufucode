using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fufucode.App.Infrastructure;
using Fufucode.Core;

namespace Fufucode.App.ViewModels;

/// <summary>一行 diff 里的一段文本；Emphasis 表示词级 diff 的强调片段。</summary>
public sealed record DiffRun(string Text, IBrush? Foreground, bool Emphasis);

public enum DiffRowKind { Context, Added, Removed, Header, Gap }

/// <summary>
/// diff 视图的一行。正文行、hunk 头、省略的上下文提示行共用一个类型，
/// 这样列表只渲染一种行模板，滚动与选中语义一致。
/// </summary>
public sealed partial class DiffRowViewModel : ViewModelBase
{
    private DiffRowViewModel(DiffRowKind kind) => Kind = kind;

    /// <summary>行最小宽度，由 <see cref="DiffViewModel.RowWidth"/> 下发：增删底色整行铺满。</summary>
    [ObservableProperty] private double _minWidth;

    /// <summary>长行是否折行；窄栏折行，宽栏保持不折行以便横向对照。</summary>
    [ObservableProperty] private bool _wrap;

    public DiffRowKind Kind { get; }
    public string OldNumber { get; private init; } = string.Empty;
    public string NewNumber { get; private init; } = string.Empty;
    public string Marker { get; private init; } = string.Empty;
    public string Text { get; private init; } = string.Empty;
    public string HeaderText { get; private init; } = string.Empty;
    public IReadOnlyList<DiffRun> Runs { get; private init; } = [];

    public int HiddenLines { get; private init; }
    public int GapOldStart { get; private init; }
    public int GapOldCount { get; private init; }
    public int GapNewStart { get; private init; }
    public int GapNewCount { get; private init; }

    public bool IsAdded => Kind == DiffRowKind.Added;
    public bool IsRemoved => Kind == DiffRowKind.Removed;
    public bool IsContext => Kind == DiffRowKind.Context;
    public bool IsGap => Kind == DiffRowKind.Gap;
    public bool IsHeader => Kind == DiffRowKind.Header;
    public bool IsBody => Kind is DiffRowKind.Context or DiffRowKind.Added or DiffRowKind.Removed;
    public bool HasEmphasis => Runs.Any(run => run.Emphasis);
    /// <summary>标记列颜色与底色一致：+ 绿、− 红。</summary>
    public bool IsMarkerNeutral => Kind is DiffRowKind.Context or DiffRowKind.Header or DiffRowKind.Gap;
    public bool HasRuns => Runs.Count > 0;

    public string GapText => HiddenLines > 0 ? $"⋯ 展开 {HiddenLines} 行" : "⋯ 展开";

    /// <summary>
    /// hunk 头只保留行号区间与上下文标题：git 的 "@@" 记号对阅读没有帮助，
    /// 去掉后 "−10,6 +10,7 public sealed class Widget" 更像一句位置说明。
    /// </summary>
    public static DiffRowViewModel Header(string text) =>
        new(DiffRowKind.Header) { HeaderText = string.Join(' ', text.Replace("@@", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries)) };

    public static DiffRowViewModel Body(char kind, int oldNumber, int newNumber, string text,
        IReadOnlyList<DiffRun> runs) => new(kind switch
        {
            '+' => DiffRowKind.Added,
            '-' => DiffRowKind.Removed,
            _ => DiffRowKind.Context,
        })
        {
            OldNumber = kind == '+' || oldNumber <= 0 ? string.Empty : oldNumber.ToString(),
            NewNumber = kind == '-' || newNumber <= 0 ? string.Empty : newNumber.ToString(),
            Marker = kind switch { '+' => "+", '-' => "−", '\\' => "\\", _ => " " },
            Text = text,
            Runs = runs.Count > 0 ? runs : [new DiffRun(text, null, false)],
        };

    public static DiffRowViewModel Gap(int oldStart, int oldCount, int newStart, int newCount) =>
        new(DiffRowKind.Gap)
        {
            HiddenLines = Math.Max(oldCount, newCount),
            GapOldStart = oldStart,
            GapOldCount = oldCount,
            GapNewStart = newStart,
            GapNewCount = newCount,
        };
}

/// <summary>
/// 一个文件的 diff 呈现模型。
///
/// 与旧实现的关键差别：旧版把 hunk 拼成一段制表符分隔的字符串塞进只读 TextBox，
/// 没有行号、没有底色、也不能折叠。这里保留行结构：每行带新旧行号与底色，
/// 连续上下文超过 <see cref="ContextLines"/> 行就折叠成一行可点击的省略提示，
/// 大改动不会把阅读位置冲出屏幕。
/// </summary>
public sealed partial class DiffViewModel : ViewModelBase
{
    /// <summary>每个变化块上下保留的上下文行数。</summary>
    public const int ContextLines = 3;

    private readonly List<DiffRowViewModel> _all = new();
    /// <summary>每个省略行折叠掉的具体行，展开时原位替换。</summary>
    private readonly Dictionary<DiffRowViewModel, List<DiffRowViewModel>> _gaps = new();
    private IReadOnlyList<IReadOnlyList<SyntaxSpan>>? _oldSpans;
    private IReadOnlyList<IReadOnlyList<SyntaxSpan>>? _newSpans;

    public ObservableCollection<DiffRowViewModel> Rows { get; } = new();

    /// <summary>行号列宽度，按最大行号的位数算，滚动时列宽不跳。</summary>
    [ObservableProperty] private double _numberColumnWidth = 34;

    /// <summary>滚动视口宽度；变化时同步给所有行，避免每行自己去问祖先控件要宽度。</summary>
    [ObservableProperty] private double _rowWidth;
    [ObservableProperty] private bool _wrap;
    [ObservableProperty] private bool _isEmpty = true;
    [ObservableProperty] private int _addedLines;
    [ObservableProperty] private int _removedLines;

    public bool HasRows => Rows.Count > 0;
    public string SummaryText => "+" + AddedLines + "  −" + RemovedLines;

    partial void OnRowWidthChanged(double value)
    {
        foreach (var row in Rows) row.MinWidth = value;
    }

    partial void OnWrapChanged(bool value)
    {
        foreach (var row in Rows) row.Wrap = value;
    }

    partial void OnAddedLinesChanged(int value) => OnPropertyChanged(nameof(SummaryText));
    partial void OnRemovedLinesChanged(int value) => OnPropertyChanged(nameof(SummaryText));

    /// <summary>
    /// 由 diff 与两侧文本构建。两侧文本可为空（未跟踪、已删除、读取失败），
    /// 此时词级高亮与语法着色自动降级，增减行本身不受影响。
    /// </summary>
    public static DiffViewModel Build(FileDiff diff, DiffSides? sides, string? extension, bool dark)
    {
        var model = new DiffViewModel();
        if (extension is { Length: > 0 })
        {
            model._oldSpans = SyntaxHighlighter.Highlight(sides?.OldText ?? string.Empty, extension, dark);
            model._newSpans = SyntaxHighlighter.Highlight(sides?.NewText ?? string.Empty, extension, dark);
        }
        model.Load(diff);
        return model;
    }

    private void Load(FileDiff diff)
    {
        _all.Clear();
        var maxNumber = 1;
        foreach (var hunk in diff.Hunks)
        {
            if (hunk.Header.Length > 0) _all.Add(DiffRowViewModel.Header(hunk.Header));
            AddHunk(hunk, ref maxNumber);
        }
        AddedLines = _all.Count(row => row.IsAdded);
        RemovedLines = _all.Count(row => row.IsRemoved);
        NumberColumnWidth = Math.Max(30, (maxNumber.ToString().Length * 7.4) + 14);
        Rebuild();
    }

    private void AddHunk(DiffHunk hunk, ref int maxNumber)
    {
        var oldLine = hunk.OldStart;
        var newLine = hunk.NewStart;
        var index = 0;
        while (index < hunk.Lines.Count)
        {
            if (hunk.Lines[index].Kind is '+' or '-')
            {
                var start = index;
                while (index < hunk.Lines.Count && hunk.Lines[index].Kind is '+' or '-') index++;
                AppendChangeBlock(hunk, start, index, ref oldLine, ref newLine, ref maxNumber);
                continue;
            }

            var line = hunk.Lines[index++];
            _all.Add(DiffRowViewModel.Body(line.Kind, oldLine, newLine, line.Text,
                RunsFor(line.Kind, line.Text, oldLine, newLine)));
            maxNumber = Math.Max(maxNumber, Math.Max(oldLine, newLine));
            if (line.Kind is ' ' or '-') oldLine++;
            if (line.Kind is ' ' or '+') newLine++;
        }
    }

    private void AppendChangeBlock(DiffHunk hunk, int start, int end, ref int oldLine, ref int newLine, ref int maxNumber)
    {
        var removed = new List<int>();
        var added = new List<int>();
        for (var i = start; i < end; i++)
            (hunk.Lines[i].Kind == '-' ? removed : added).Add(i);

        // 词级强调只对等量替换有意义；数量不等时整行都是改动，强调只会制造噪声。
        var emphasis = removed.Count == added.Count && removed.Count > 0
            ? WordDiff.Pair(hunk, removed, added)
            : null;

        for (var i = start; i < end; i++)
        {
            var line = hunk.Lines[i];
            var runs = emphasis is not null && emphasis.TryGetValue(i, out var range)
                ? EmphasisRuns(line.Text, range)
                : RunsFor(line.Kind, line.Text, oldLine, newLine);
            _all.Add(DiffRowViewModel.Body(line.Kind, oldLine, newLine, line.Text, runs));
            maxNumber = Math.Max(maxNumber, Math.Max(oldLine, newLine));
            if (line.Kind == '-') oldLine++;
            else newLine++;
        }
    }

    /// <summary>按语法着色切分一行；没有语法或没有两侧文本时退回单色。</summary>
    private IReadOnlyList<DiffRun> RunsFor(char kind, string text, int oldNumber, int newNumber)
    {
        var spans = kind switch
        {
            '-' => SpanAt(_oldSpans, oldNumber),
            '+' => SpanAt(_newSpans, newNumber),
            _ => SpanAt(_newSpans, newNumber) ?? SpanAt(_oldSpans, oldNumber),
        };
        if (spans is null || spans.Count == 0) return [];
        return spans.Select(span => new DiffRun(span.Text, ColorBrush.For(span.Color), false)).ToArray();
    }

    private static IReadOnlyList<SyntaxSpan>? SpanAt(IReadOnlyList<IReadOnlyList<SyntaxSpan>>? spans, int lineNumber) =>
        spans is not null && lineNumber - 1 >= 0 && lineNumber - 1 < spans.Count ? spans[lineNumber - 1] : null;

    private static IReadOnlyList<DiffRun> EmphasisRuns(string text, (int Start, int Length) range)
    {
        var runs = new List<DiffRun>(3);
        if (range.Start > 0) runs.Add(new DiffRun(text[..range.Start], null, false));
        runs.Add(new DiffRun(text.Substring(range.Start, range.Length), null, true));
        var tail = range.Start + range.Length;
        if (tail < text.Length) runs.Add(new DiffRun(text[tail..], null, false));
        return runs;
    }

    /// <summary>
    /// 折叠连续上下文：只在变化块两侧保留 <see cref="ContextLines"/> 行，
    /// 中间换成一行省略提示；用户点过的那段保持展开。
    /// 两侧文本读不到时无法提供展开内容，省略行只做提示。
    /// </summary>
    private void Rebuild()
    {
        Rows.Clear();
        _gaps.Clear();
        for (var i = 0; i < _all.Count; i++)
        {
            var row = _all[i];
            if (!row.IsContext) { Rows.Add(row); continue; }

            var runStart = i;
            while (i < _all.Count && _all[i].IsContext) i++;
            var runEnd = i;            // 不含
            i--;                        // 外层循环会自增

            var count = runEnd - runStart;
            // hunk 内的上下文本来就只有 git 给的行数（默认 3），只有把上下文调大后
            // 才会出现需要再折叠的长段；展开过的段整体保留。
            const int gapCap = ContextLines;
            if (count <= gapCap * 2 + 1)
            {
                for (var k = runStart; k < runEnd; k++) Rows.Add(_all[k]);
                continue;
            }
            for (var k = runStart; k < runStart + gapCap; k++) Rows.Add(_all[k]);
            // 省略段起点在第一行可见上下文之后，行号取"被藏起来的第一行"。
            var hiddenFirst = _all[runStart + gapCap];
            var hiddenCount = count - gapCap * 2;
            var gap = DiffRowViewModel.Gap(
                ParseNumber(hiddenFirst.OldNumber, 1), hiddenCount,
                ParseNumber(hiddenFirst.NewNumber, 1), hiddenCount);
            _gaps[gap] = _all.GetRange(runStart + gapCap, hiddenCount);
            Rows.Add(gap);
            for (var k = runEnd - gapCap; k < runEnd; k++) Rows.Add(_all[k]);
        }
        foreach (var row in Rows)
        {
            row.MinWidth = RowWidth;
            row.Wrap = Wrap;
        }
        IsEmpty = Rows.Count == 0;
        OnPropertyChanged(nameof(HasRows));
    }

    /// <summary>把一行省略提示原位换成它折叠掉的那些行；两侧文本不可用时什么也不做。</summary>
    public bool ExpandGap(DiffRowViewModel gap, DiffSides sides)
    {
        if (!_gaps.TryGetValue(gap, out var hidden)) return false;
        var index = Rows.IndexOf(gap);
        if (index < 0) return false;
        var oldLines = Split(sides.OldText);
        var newLines = Split(sides.NewText);
        if (oldLines is null && newLines is null) return false;

        // 折叠掉的行本来就有行号，直接按行号回填两侧文本，不用重新推演 hunk。
        var expanded = new List<DiffRowViewModel>(hidden.Count);
        foreach (var row in hidden)
        {
            var oldNumber = ParseNumber(row.OldNumber, 0);
            var newNumber = ParseNumber(row.NewNumber, 0);
            var text = row.Text;
            if (oldLines is not null && oldNumber > 0 && oldNumber - 1 < oldLines.Count) text = oldLines[oldNumber - 1];
            else if (newLines is not null && newNumber > 0 && newNumber - 1 < newLines.Count) text = newLines[newNumber - 1];
            expanded.Add(DiffRowViewModel.Body(' ', oldNumber, newNumber, text,
                RunsFor(' ', text, oldNumber, newNumber)));
        }

        Rows.RemoveAt(index);
        for (var i = 0; i < expanded.Count; i++) Rows.Insert(index + i, expanded[i]);
        _gaps.Remove(gap);
        return true;
    }

    private static int ParseNumber(string text, int fallback) =>
        int.TryParse(text, out var value) ? value : fallback;

    private static IReadOnlyList<string>? Split(string? text) =>
        text is null ? null : text.Replace("\r\n", "\n").Split('\n');
}
