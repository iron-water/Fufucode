using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fufucode.App.Infrastructure;

namespace Fufucode.App.Features.FilePreview;

/// <summary>只读源码：按可见行创建控件，复用 TextMate 着色，查找按实际匹配项导航。</summary>
public sealed class TextPreviewControl : UserControl
{
    // 极长的单行也不能一次创建几百万字符的排版和 Run；查找仍覆盖完整原文。
    public const int MaximumRenderedLineCharacters = 8192;
    public const int MaximumHighlightedCharacters = 512 * 1024;
    private readonly ListBox _list;
    private readonly string[] _lines;
    private readonly string _text;
    private readonly string? _extension;
    private readonly int[] _matchCounts;
    private readonly TextBlock _notice;
    private IReadOnlyList<IReadOnlyList<SyntaxSpan>>? _syntax;
    private string _query = string.Empty;
    private int _matchIndex = -1;
    private int _currentLine = -1;
    private int _currentColumn = -1;
    private int _highlightVersion;
    private int _navigationVersion;

    public TextPreviewControl(string text, string? extension = null)
    {
        _text = (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
        _lines = _text.Split('\n');
        _extension = extension;
        _matchCounts = new int[_lines.Length];
        _list = new ListBox
        {
            ItemsSource = Enumerable.Range(0, _lines.Length).ToArray(),
            ItemTemplate = new FuncDataTemplate<int>((line, _) => new PreviewLine(this, line)),
            ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel()),
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
        };
        _list.Styles.Add(new Style(selector => selector.OfType<ListBoxItem>())
        {
            Setters =
            {
                new Setter(PaddingProperty, new Thickness(0)),
                new Setter(MarginProperty, new Thickness(0)),
                new Setter(HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch),
                new Setter(MinHeightProperty, 0d),
            },
        });
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(_list, ScrollBarVisibility.Auto);
        _notice = new TextBlock { Margin = new Thickness(10, 4), TextWrapping = TextWrapping.Wrap };
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        Grid.SetRow(_list, 1);
        layout.Children.Add(_notice);
        layout.Children.Add(_list);
        Content = layout;
        UpdateNotice();
        // 在挂载时获取真实主题，避免构造器默认主题产生错误颜色；脱离视树的结果不再应用。
        AttachedToVisualTree += (_, _) =>
        {
            HighlightingTask = HighlightAsync();
            RevealCurrentMatch();
        };
        DetachedFromVisualTree += (_, _) => { _highlightVersion++; _navigationVersion++; };
        ActualThemeVariantChanged += (_, _) => HighlightingTask = HighlightAsync();
    }

    public bool IsWordWrapEnabled { get; private set; }
    public string FindStatus { get; private set; } = string.Empty;
    public int MatchCount { get; private set; }
    /// <summary>保持旧契约：当前匹配所在的零基行号。</summary>
    public int CurrentMatch => _currentLine;
    public int CurrentMatchIndex => _matchIndex;
    public int CurrentMatchColumn => _currentColumn;
    public int LineCount => _lines.Length;
    public bool HasSyntaxHighlighting => _syntax is not null;
    public Task HighlightingTask { get; private set; } = Task.CompletedTask;

    public void SetWordWrap(bool enabled)
    {
        IsWordWrapEnabled = enabled;
        ScrollViewer.SetHorizontalScrollBarVisibility(_list,
            enabled ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto);
        RefreshVisibleLines();
        _list.InvalidateMeasure();
        RevealCurrentMatch();
    }

    public string Find(string? query, bool forward = true)
    {
        var nextQuery = query ?? string.Empty;
        var changed = !string.Equals(_query, nextQuery, StringComparison.Ordinal);
        if (changed)
        {
            _query = nextQuery;
            _matchIndex = _currentLine = _currentColumn = -1;
            MatchCount = 0;
            Array.Clear(_matchCounts);
            if (_query.Length > 0)
            {
                for (var line = 0; line < _lines.Length; line++)
                {
                    var start = 0;
                    int column;
                    while ((column = _lines[line].IndexOf(_query, start, StringComparison.OrdinalIgnoreCase)) >= 0)
                    {
                        _matchCounts[line]++;
                        start = column + _query.Length;
                    }
                    MatchCount += _matchCounts[line];
                }
            }
        }
        _navigationVersion++;
        if (_query.Length == 0 || MatchCount == 0)
        {
            _matchIndex = _currentLine = _currentColumn = -1;
            FindStatus = _query.Length == 0 ? string.Empty : "未找到";
            RefreshVisibleLines();
            return FindStatus;
        }

        _matchIndex = _matchIndex < 0
            ? (forward ? 0 : MatchCount - 1)
            : (_matchIndex + (forward ? 1 : MatchCount - 1)) % MatchCount;
        var withinLine = _matchIndex;
        _currentLine = 0;
        while (withinLine >= _matchCounts[_currentLine])
            withinLine -= _matchCounts[_currentLine++];
        var position = 0;
        for (var match = 0; match <= withinLine; match++)
        {
            _currentColumn = _lines[_currentLine].IndexOf(_query, position, StringComparison.OrdinalIgnoreCase);
            position = _currentColumn + _query.Length;
        }
        FindStatus = $"{_matchIndex + 1}/{MatchCount} · 第 {_currentLine + 1} 行";
        RefreshVisibleLines();
        RevealCurrentMatch();
        return FindStatus;
    }

    private async Task HighlightAsync()
    {
        var version = ++_highlightVersion;
        _syntax = null;
        RefreshVisibleLines();
        if (_extension is null || _text.Length > MaximumHighlightedCharacters ||
            _lines.Length > SyntaxHighlighter.MaximumHighlightLines ||
            _lines.Any(line => line.Length > MaximumRenderedLineCharacters)) return;
        var dark = ActualThemeVariant != ThemeVariant.Light;
        var spans = await Task.Run(() => SyntaxHighlighter.Highlight(_text, _extension, dark)).ConfigureAwait(false);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (version != _highlightVersion) return;
            _syntax = spans;
            RefreshVisibleLines();
        });
    }

    private void UpdateNotice()
    {
        var notes = new List<string>();
        if (_text.Length > MaximumHighlightedCharacters || _lines.Length > SyntaxHighlighter.MaximumHighlightLines)
            notes.Add("大文本按可见行显示，已暂停语法着色。");
        if (_lines.Any(line => line.Length > MaximumRenderedLineCharacters))
            notes.Add($"超长行每次显示最多 {MaximumRenderedLineCharacters:N0} 字符；查找仍覆盖整行并显示命中位置。");
        _notice.Text = string.Join(" ", notes);
        _notice.IsVisible = notes.Count > 0;
    }

    private void RefreshVisibleLines()
    {
        foreach (var row in _list.GetVisualDescendants().OfType<PreviewLine>()) row.Refresh();
    }

    private void RevealCurrentMatch()
    {
        if (_currentLine < 0) return;
        var version = ++_navigationVersion;
        _list.ScrollIntoView(_currentLine);
        Dispatcher.UIThread.Post(() =>
        {
            if (version != _navigationVersion || _currentLine < 0) return;
            if (_list.ContainerFromIndex(_currentLine) is not { } container) return;
            var row = container.GetVisualDescendants().OfType<PreviewLine>().FirstOrDefault();
            if (row is null) return;
            row.Refresh();
            // 先实现目标行，再用排版坐标定位同一长行里的第 N 个匹配（含换行后的纵向位置）。
            row.UpdateLayout();
            var column = _currentColumn - row.DisplayStart + (row.DisplayStart > 0 ? 1 : 0);
            var position = row.Code.TextLayout.HitTestTextPosition(column);
            row.Code.BringIntoView(position);
        }, DispatcherPriority.Loaded);
    }

    private sealed class PreviewLine : Grid
    {
        private readonly TextPreviewControl _owner;
        private readonly int _line;
        public TextBlock Code { get; }
        public int DisplayStart { get; private set; }

        public PreviewLine(TextPreviewControl owner, int line)
        {
            _owner = owner;
            _line = line;
            ColumnDefinitions = new ColumnDefinitions("Auto,*");
            Margin = new Thickness(10, 0);
            var number = new TextBlock
            {
                Text = (line + 1).ToString(),
                Width = Math.Max(36, owner.LineCount.ToString().Length * 9),
                Foreground = Brushes.Gray,
                FontFamily = new FontFamily("Cascadia Mono,Consolas"),
                VerticalAlignment = VerticalAlignment.Top,
                Padding = new Thickness(0, 0, 12, 0),
            };
            Code = new TextBlock
            {
                FontFamily = new FontFamily("Cascadia Mono,Consolas"),
                MinHeight = 20,
                FontSize = 13,
            };
            SetColumn(Code, 1);
            Children.Add(number);
            Children.Add(Code);
            AttachedToVisualTree += (_, _) => Refresh();
            Refresh();
        }

        public void Refresh()
        {
            Code.TextWrapping = _owner.IsWordWrapEnabled ? TextWrapping.Wrap : TextWrapping.NoWrap;
            var source = _owner._lines[_line];
            var active = _owner._currentLine == _line;
            DisplayStart = active && source.Length > MaximumRenderedLineCharacters
                ? Math.Clamp(_owner._currentColumn - MaximumRenderedLineCharacters / 2, 0,
                    source.Length - MaximumRenderedLineCharacters)
                : 0;
            var end = Math.Min(source.Length, DisplayStart + MaximumRenderedLineCharacters);
            // 从行首沿同样的非重叠规则定位，匹配横跨语法 token 或显示窗口时仍完整着色。
            var matches = new List<int>();
            if (_owner._query.Length > 0)
            {
                var from = 0;
                int found;
                while ((found = source.IndexOf(_owner._query, from, StringComparison.OrdinalIgnoreCase)) >= 0 && found < end)
                {
                    if (found + _owner._query.Length > DisplayStart) matches.Add(found);
                    from = found + _owner._query.Length;
                }
            }
            var matchCursor = 0;
            var inlines = Code.Inlines!;
            inlines.Clear();
            if (DisplayStart > 0) inlines.Add(new Run("…"));
            var spans = _owner._syntax is { } syntax && _line < syntax.Count ? syntax[_line] : null;
            if (spans is { Count: > 0 })
            {
                var offset = 0;
                foreach (var span in spans)
                {
                    var spanEnd = offset + span.Text.Length;
                    Append(Math.Max(offset, DisplayStart), Math.Min(spanEnd, end), span.Color, span.Italic);
                    offset = spanEnd;
                    if (offset >= end) break;
                }
            }
            else Append(DisplayStart, end, null, false);
            if (end < source.Length) inlines.Add(new Run("…"));

            void Append(int start, int stop, Color? color, bool italic)
            {
                if (stop <= start) return;
                var cursor = start;
                while (matchCursor < matches.Count)
                {
                    var found = matches[matchCursor];
                    if (found + _owner._query.Length <= cursor) { matchCursor++; continue; }
                    if (found >= stop) break;
                    if (found > cursor) Add(cursor, found, false, false);
                    var matchStart = Math.Max(cursor, found);
                    var matchEnd = Math.Min(stop, found + _owner._query.Length);
                    Add(matchStart, matchEnd, true, active && found == _owner._currentColumn);
                    cursor = matchEnd;
                    if (cursor == found + _owner._query.Length) matchCursor++;
                    if (cursor == stop) break;
                }
                if (cursor < stop) Add(cursor, stop, false, false);

                void Add(int from, int to, bool match, bool current)
                {
                    var run = new Run(source[from..to]);
                    if (color is { } foreground) run.Foreground = new SolidColorBrush(foreground);
                    if (italic) run.FontStyle = FontStyle.Italic;
                    if (match)
                    {
                        run.Background = current ? Brushes.DarkOrange : Brushes.Gold;
                        run.Foreground = Brushes.Black;
                        if (current) run.FontWeight = FontWeight.Bold;
                    }
                    inlines.Add(run);
                }
            }
        }
    }
}
