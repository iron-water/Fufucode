using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Fufucode.Core.Diagnostics;

namespace Fufucode.Plugins.PerformanceMonitor;

/// <summary>
/// 停靠区“性能”页：概览、视图（瀑布 / 慢调用 / 异常）、记录。不实现宿主 IPluginWorkspacePage，
/// 由宿主通过 PluginPageView 适配；本类只依赖 Avalonia 与 Core 契约。
/// </summary>
public sealed class PerfDockPage : IDisposable
{
    private readonly PerfRecorder _recorder;
    private readonly DispatcherTimer _timer;
    private readonly ComboBox _view;
    private readonly ComboBox _statusFilter;
    private readonly ComboBox _tracePicker;
    private readonly TextBox _search;
    private readonly CheckBox _live;
    private readonly TextBlock _statusLine;
    private readonly TextBlock _summary;
    private readonly PerfBarChart _chart;
    private readonly ListBox _list;
    private readonly ScrollViewer _waterfallScroll;
    private readonly PerfWaterfall _waterfall;
    private readonly TextBox _detail;
    private readonly StackPanel _viewHost;
    private long _lastRefresh;
    private bool _dirty = true;
    private bool _disposed;

    public PerfDockPage(PerfRecorder recorder, string dataRoot)
    {
        DataRoot = dataRoot;
        _recorder = recorder;
        _view = new ComboBox
        {
            ItemsSource = new[] { "瀑布图", "慢调用", "异常", "记录" },
            SelectedIndex = 0,
            MinWidth = 110,
        };
        _statusFilter = new ComboBox
        {
            ItemsSource = new[] { "全部", "失败/取消", "仅失败" },
            SelectedIndex = 0,
            MinWidth = 100,
        };
        _tracePicker = new ComboBox { MinWidth = 260, PlaceholderText = "选择回合（trace）" };
        _search = new TextBox { PlaceholderText = "过滤工具 / span 名称", MinWidth = 180 };
        _live = new CheckBox { Content = "实时", IsChecked = true };
        _statusLine = new TextBlock { Classes = { "caption" }, TextWrapping = TextWrapping.Wrap };
        _summary = new TextBlock { TextWrapping = TextWrapping.Wrap };
        _chart = new PerfBarChart();
        _list = new ListBox { MinHeight = 200 };
        _waterfall = new PerfWaterfall();
        _waterfallScroll = new ScrollViewer
        {
            Content = _waterfall,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            MinHeight = 220,
        };
        _detail = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            MinHeight = 140,
            Classes = { "mono" },
        };
        _viewHost = new StackPanel { Spacing = 8 };

        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                _view, _tracePicker, _statusFilter, _search, _live,
                Button("刷新", () => Refresh(force: true)),
                Button("清空", () => { _recorder.Clear(); Refresh(force: true); }),
            },
        };

        Content = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,*"),
            Children =
            {
                Place(toolbar, 0),
                Place(_statusLine, 1),
                Place(new Grid
                {
                    RowDefinitions = new RowDefinitions("Auto,*,Auto"),
                    Children =
                    {
                        Place(_viewHost, 0),
                        Place(_waterfallScroll, 1),
                        Place(_list, 1),
                        Place(_detail, 2),
                    },
                }, 2),
            },
        };
        _statusLine.Margin = new Thickness(0, 4, 0, 8);

        _view.SelectionChanged += (_, _) => { _dirty = true; Refresh(force: true); };
        _statusFilter.SelectionChanged += (_, _) => { _dirty = true; Refresh(force: true); };
        _search.TextChanged += (_, _) => { _dirty = true; };
        _tracePicker.SelectionChanged += (_, _) => { _dirty = true; Refresh(force: true); };
        _waterfall.DetailRequested += span => _detail.Text = DescribeSpan(span);
        _list.SelectionChanged += (_, _) => { _detail.Text = Describe(_list.SelectedItem); };

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) =>
        {
            if (!_live.IsChecked.GetValueOrDefault()) return;
            // 页面不可见（隐身停放）时不重建控件；可见性由宿主停靠区决定。
            if (!Content.IsEffectivelyVisible) return;
            // 实时模式按“有新数据”刷新，避免无变化时反复重建列表。
            if (_recorder.Ring.TotalWritten != _lastRefresh || _recorder.Anomalies().Count != _lastAnomalyCount)
            {
                _dirty = true;
                Refresh(force: false);
            }
        };
        _timer.Start();
        Refresh(force: true);
    }

    private int _lastAnomalyCount = -1;

    public Control Content { get; }

    public string DataRoot { get; }

    public void NotifySettingsChanged() => Refresh(force: true);

    /// <summary>宿主拉到新数据后调用；UI 线程上执行。</summary>
    public void Refresh(bool force)
    {
        if (_disposed) return;
        if (!force && !_dirty) return;
        _dirty = false;
        _lastRefresh = _recorder.Ring.TotalWritten;
        _lastAnomalyCount = _recorder.Anomalies().Count;

        var spans = _recorder.Ring.Snapshot();
        UpdateStatus(spans);
        switch (_view.SelectedIndex)
        {
            case 0: ShowWaterfall(spans); break;
            case 1: ShowSlow(spans); break;
            case 2: ShowAnomalies(); break;
            default: ShowLog(); break;
        }
    }

    private void UpdateStatus(List<SpanRecord> spans)
    {
        var settings = _recorder.Settings;
        var mode = settings.Mode switch { "off" => "关闭", "detailed" => "详细", _ => "标准" };
        _statusLine.Text = $"模式 {mode} · 内存 {_recorder.Ring.Count:N0}/{_recorder.Ring.Capacity:N0} span · " +
            $"覆盖 {_recorder.Ring.Overwritten:N0} · 落盘丢弃 {_recorder.Dropped:N0} · 异常 {_recorder.Anomalies().Count:N0} · " +
            $"{(settings.RetentionDays == 0 ? "仅内存" : $"保留 {settings.RetentionDays} 天")}";
        _viewHost.Children.Clear();
        _viewHost.Children.Add(BuildSummary(spans));
        _viewHost.Children.Add(_chart);
    }

    private Control BuildSummary(List<SpanRecord> spans)
    {
        var turns = spans.Where(s => s.Name == "turn").ToList();
        var last = turns.LastOrDefault();
        var models = spans.Where(s => s.Name == "model.http").ToList();
        var tools = spans.Where(s => s.Name == "tool").GroupBy(s => s.Tag("tool") ?? "?").ToList();
        var stalls = _recorder.RecentEvents(500).Count(e => e.Kind == "ui.stall");
        var startup = spans.Where(s => s.Name == "startup").OrderBy(s => s.EndTicks).LastOrDefault();
        var startupText = startup is null
            ? "启动耗时：尚未记录（每次启动写 `<dataRoot>/perf/startup-*.jsonl`）"
            : $"最近启动：{PerfBarChart.FormatMs(startup.DurationMs)} · {startup.Tags.FirstOrDefault(t => t.Key == "milestones").Value ?? "?"} 个里程碑 · " +
              $"在瀑布视图选择“启动”查看逐段耗时";
        var summary = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock
                {
                    Text = last is null ? "尚无完成的回合。" :
                        $"最近回合 {last.EndUtc.ToLocalTime():HH:mm:ss} · 总耗时 {PerfBarChart.FormatMs(last.DurationMs)} · " +
                        $"模型 {models.Count(m => m.TraceId == last.TraceId)} 次 · 工具 {last.LongTag("toolCalls") ?? 0} 次 · 结果 {last.Tag("outcome")}",
                    Classes = { "bodyStrong" },
                },
                new TextBlock { Text = startupText, Classes = { "caption" } },
                new TextBlock
                {
                    Text = $"模型请求 {models.Count} 次 · 工具执行 {tools.Sum(g => g.Count())} 次 · UI 卡顿事件 {stalls} · " +
                        $"失败 span {spans.Count(s => s.Status == "Error")}",
                    Classes = { "caption" },
                },
            },
        };
        var chartItems = new List<(string, double, Color)>();
        if (last is not null)
        {
            foreach (var child in spans.Where(s => s.ParentSpanId == last.SpanId)
                         .GroupBy(s => s.Name).OrderByDescending(g => g.Sum(s => s.DurationMs)).Take(8))
                chartItems.Add((child.Key, child.Sum(s => s.DurationMs), CategoryColor(child.First().Category)));
        }
        else
        {
            foreach (var group in spans.GroupBy(s => s.Name).OrderByDescending(g => g.Sum(s => s.DurationMs)).Take(8))
                chartItems.Add((group.Key, group.Sum(s => s.DurationMs), CategoryColor(group.First().Category)));
        }
        _chart.SetData(chartItems);
        return summary;
    }

    private void ShowWaterfall(List<SpanRecord> spans)
    {
        _list.IsVisible = false;
        _waterfallScroll.IsVisible = true;
        // 回合（turn）与启动（startup）共用时间轴；启动标注类别后混入列表。
        var traceGroups = spans.GroupBy(s => s.TraceId);
        var startupTraces = traceGroups
            .Where(g => g.Any(s => s.Name == "startup" || s.Name.StartsWith("startup.", StringComparison.Ordinal)))
            .Select(g => (TraceId: g.Key, Start: g.Min(s => s.StartTicks),
                          Turn: g.FirstOrDefault(s => s.Name == "startup"), Label: "启动"))
            .OrderByDescending(t => t.Start)
            .Take(50);
        var turnTraces = traceGroups
            .Select(g => (TraceId: g.Key, Start: g.Min(s => s.StartTicks), Turn: g.FirstOrDefault(s => s.Name == "turn")))
            .Where(t => t.Turn is not null)
            .OrderByDescending(t => t.Start)
            .Take(200)
            .Select(t => (TraceId: t.TraceId, Start: t.Start, Turn: t.Turn, Label: "回合"));
        var traces = startupTraces.Concat(turnTraces).ToList();
        var previous = _tracePicker.SelectedIndex;
        var selectedTrace = previous >= 0 && previous < _tracePicker.ItemsSource?.Cast<object>().Count()
            ? ((TraceItem)_tracePicker.ItemsSource!.Cast<object>().ElementAt(previous)).TraceId
            : traces.FirstOrDefault().TraceId;
        var items = traces.Select(t => (object)new TraceItem(t.TraceId, t.Start, t.Turn, t.Label)).ToArray();
        _tracePicker.ItemsSource = items;
        var index = Array.FindIndex(items, item => ((TraceItem)item).TraceId == selectedTrace);
        _tracePicker.SelectedIndex = index >= 0 ? index : (items.Length > 0 ? 0 : -1);

        var current = _tracePicker.SelectedItem as TraceItem;
        var traceSpans = current is null ? [] : spans.Where(s => s.TraceId == current.TraceId).ToList();
        ApplyFilters(ref traceSpans);
        _waterfall.SetSpans(traceSpans, current?.TraceId ?? 0);
    }

    private void ShowSlow(List<SpanRecord> spans)
    {
        _detail.Text = string.Empty;
        _list.IsVisible = true;
        _waterfallScroll.IsVisible = false;
        ApplyFilters(ref spans);
        var rows = spans
            .Where(s => s.Name is not ("turn" or "agent.iteration"))
            .OrderByDescending(s => s.DurationMs)
            .Take(500)
            .Select(s => (object)new RowItem(s))
            .ToArray();
        _detail.Text = rows.Length == 0 ? string.Empty : _detail.Text;
        _list.ItemsSource = rows;
    }

    private void ShowAnomalies()
    {
        _detail.Text = string.Empty;
        _list.IsVisible = true;
        _waterfallScroll.IsVisible = false;
        var rows = _recorder.Anomalies()
            .Where(a => Matches(a.SpanName))
            .Take(500)
            .Select(a => (object)new RowItem(Anomaly: a))
            .ToArray();
        _list.ItemsSource = rows;
    }

    private void ShowLog()
    {
        _list.IsVisible = false;
        _waterfallScroll.IsVisible = false;
        _detail.Text = string.Join('\n', _recorder.RecentEvents(300)
            .Where(e => Matches(e.Name))
            .Select(e => $"{e.TimestampUtc.ToLocalTime():HH:mm:ss.fff}  {e.Kind,-16} {e.Name,-24} {e.Value,10:0.0}  {e.Detail}"));
    }

    private void ApplyFilters(ref List<SpanRecord> spans)
    {
        var query = _search.Text?.Trim();
        if (!string.IsNullOrEmpty(query))
            spans = spans.Where(s => s.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                     (s.Tag("tool")?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                                     (s.Tag("command")?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();
        spans = _statusFilter.SelectedIndex switch
        {
            1 => spans.Where(s => s.Status != "Ok").ToList(),
            2 => spans.Where(s => s.Status == "Error").ToList(),
            _ => spans,
        };
    }

    private bool Matches(string name)
    {
        var query = _search.Text?.Trim();
        return string.IsNullOrEmpty(query) || name.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private string Describe(object? item) => item switch
    {
        RowItem { Span: { } span } => DescribeSpan(span),
        RowItem { Anomaly: { } anomaly } => DescribeAnomaly(anomaly),
        _ => string.Empty,
    };

    internal static string DescribeSpan(SpanRecord span)
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine($"{span.Name}  ·  {PerfBarChart.FormatMs(span.DurationMs)}  ·  {span.Status}  ·  trace {span.TraceId:x}");
        if (span.ParentSpanId != 0) builder.AppendLine($"父 span：{span.ParentSpanId:x}   结束：{span.EndUtc.ToLocalTime():O}");
        if (span.Error is { } error) builder.AppendLine($"错误：{error}");
        foreach (var (key, value) in span.Tags) builder.AppendLine($"{key,-18}{value}");
        return builder.ToString().TrimEnd();
    }

    internal static string DescribeAnomaly(Anomaly anomaly)
    {
        var baseline = anomaly.BaselineMs is { } baseMs ? $"基线 {baseMs:0} ms，当前 {anomaly.ValueMs:0} ms" : "";
        return $"{anomaly.Rule}  ({anomaly.Kind})\n{anomaly.SpanName}\n{anomaly.Detail}\n{baseline}\n时间：{anomaly.TimestampUtc.ToLocalTime():O}";
    }


    internal static Color CategoryColor(string category) => category switch
    {
        "model" => Color.FromRgb(0x4C, 0xC2, 0xFF),
        "tool" => Color.FromRgb(0x6C, 0xCB, 0x5F),
        "ui" => Color.FromRgb(0xB4, 0x8C, 0xFF),
        "error" => Color.FromRgb(0xE8, 0x5A, 0x5A),
        "persist" => Color.FromRgb(0xFF, 0x9E, 0x4C),
        "subagent" => Color.FromRgb(0xFF, 0xC8, 0x3D),
        "mcp" => Color.FromRgb(0x33, 0x99, 0xFF),
        _ => Color.FromRgb(0x8A, 0x8A, 0x8E),
    };

    private static Control Place(Control control, int row)
    {
        Grid.SetRow(control, row);
        return control;
    }

    private static Button Button(string text, Action action)
    {
        var button = new Button { Content = text };
        button.Click += (_, _) => action();
        return button;
    }

    private sealed record TraceItem(long TraceId, long StartTicks, SpanRecord? Turn, string Label)
    {
        public override string ToString()
        {
            var time = Turn is { } turnRecord
                ? turnRecord.EndUtc - TimeSpan.FromMilliseconds(turnRecord.DurationMs)
                : DateTimeOffset.UtcNow;
            var localTime = time.ToLocalTime().ToString("HH:mm:ss");
            var title = Turn is null ? Label : Label == "回合" ? (Turn.Tag("model") ?? "回合") : "启动";
            var suffix = Turn is null ? "" : $" · {PerfBarChart.FormatMs(Turn.DurationMs)} · {Turn.Tag("outcome")}";
            return $"{localTime} · {title}{suffix}";
        }
    }

    private sealed record RowItem(SpanRecord? Span = null, Anomaly? Anomaly = null)
    {
        public override string ToString() => Span is { } span
            ? $"{span.EndUtc.ToLocalTime():HH:mm:ss.fff}  {span.Status,-9} {PerfBarChart.FormatMs(span.DurationMs),10}  {span.Name,-18} " +
              string.Join(' ', span.Tags.Where(t => t.Key is "tool" or "command" or "exitCode" or "ttftMs")
                  .Select(t => $"{t.Key}={t.Value}"))
            : Anomaly is { } anomaly
                ? $"{anomaly.TimestampUtc.ToLocalTime():HH:mm:ss.fff}  {anomaly.Rule,-21} {anomaly.SpanName,-16} {anomaly.Detail}"
                : string.Empty;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
    }
}
