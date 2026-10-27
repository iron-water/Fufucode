using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Fufucode.Core.Diagnostics.Perf;

namespace Fufucode.Plugins.PerformanceMonitor;

/// <summary>
/// 共享时间轴的轨道视图：每个 span 按真实起止时间落到轨道上，点击条选中并交给检查器。
/// 轮滚缩放，双击回到整轮；不生成子控件，几千个 span 也只走一次 Render。
/// </summary>
internal sealed class PerfTimelineView : Control
{
    private const double LabelWidth = 104;
    private const double RulerHeight = 18;
    private const double TrackHeight = 27;
    private const double Padding = 8;
    private static readonly Typeface Face = new(FontFamily.Default);
    private static readonly Typeface MonoFace = new(FontFamily.Parse("Cascadia Mono, Consolas"));

    public static readonly StyledProperty<IBrush?> TextBrushProperty =
        AvaloniaProperty.Register<PerfTimelineView, IBrush?>(nameof(TextBrush));
    public static readonly StyledProperty<IBrush?> MutedBrushProperty =
        AvaloniaProperty.Register<PerfTimelineView, IBrush?>(nameof(MutedBrush));
    public static readonly StyledProperty<IBrush?> GridBrushProperty =
        AvaloniaProperty.Register<PerfTimelineView, IBrush?>(nameof(GridBrush));
    public static readonly StyledProperty<IBrush?> AccentBrushProperty =
        AvaloniaProperty.Register<PerfTimelineView, IBrush?>(nameof(AccentBrush));

    private readonly List<(Rect Rect, SpanRecord Span)> _hits = [];
    private PerfTurnInfo? _turn;
    private IReadOnlyList<PerfHostSample> _samples = [];
    private long _selectedSpanId;
    private double _viewStart;
    private double _viewEnd = 1;

    public PerfTimelineView()
    {
        ClipToBounds = true;
        Height = RulerHeight + TrackHeight * 8 + Padding * 2;
        this.Bind(TextBrushProperty, new DynamicResourceExtension("AppTextSecondary"));
        this.Bind(MutedBrushProperty, new DynamicResourceExtension("AppTextDim"));
        this.Bind(GridBrushProperty, new DynamicResourceExtension("AppDividerSubtle"));
        this.Bind(AccentBrushProperty, new DynamicResourceExtension("AppAccentText"));
        PointerPressed += OnPointerPressed;
        PointerWheelChanged += OnPointerWheel;
    }

    public IBrush? TextBrush { get => GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }
    public IBrush? MutedBrush { get => GetValue(MutedBrushProperty); set => SetValue(MutedBrushProperty, value); }
    public IBrush? GridBrush { get => GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }
    public IBrush? AccentBrush { get => GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }

    public event Action<SpanRecord>? SpanSelected;

    public void SetTurn(PerfTurnInfo turn, IReadOnlyList<PerfHostSample> samples, long selectedSpanId)
    {
        var changed = _turn?.TraceId != turn.TraceId;
        _turn = turn;
        _samples = samples;
        _selectedSpanId = selectedSpanId;
        if (changed)
        {
            _viewStart = 0;
            _viewEnd = 1;
        }
        InvalidateVisual();
    }

    public void SelectSpan(long spanId)
    {
        _selectedSpanId = spanId;
        InvalidateVisual();
    }

    public void ResetView()
    {
        _viewStart = 0;
        _viewEnd = 1;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        _hits.Clear();
        var width = Bounds.Width;
        if (width <= LabelWidth + Padding * 2 || _turn is null) return;
        var spans = _turn.Spans;
        if (spans.Count == 0)
        {
            DrawText(context, "该回合没有 span 数据。", Padding, Padding, 11, MutedBrush);
            return;
        }

        var start = spans.Min(span => span.StartTicks);
        var end = Math.Max(start + 1, spans.Max(span => span.EndTicks));
        var total = end - start;
        var plotLeft = LabelWidth;
        var plotRight = width - Padding;
        var plotWidth = Math.Max(1, plotRight - plotLeft);
        var top = Padding + RulerHeight;
        var bottom = top + TrackHeight * 8;

        DrawRuler(context, start, total, plotLeft, plotWidth);
        using (context.PushClip(new Rect(plotLeft, top, plotWidth, TrackHeight * 8)))
        {
            DrawTrackBackground(context, top, plotLeft, plotWidth, 8);
            DrawUiTrack(context, _turn, start, total, plotLeft, plotWidth, top);
            DrawEventTrack(context, _turn, start, total, plotLeft, plotWidth, top + TrackHeight);
            DrawFufuTrack(context, _turn, start, total, plotLeft, plotWidth, top + TrackHeight * 2);
            DrawModelTrack(context, _turn, start, total, plotLeft, plotWidth, top + TrackHeight * 3);
            DrawToolTrack(context, _turn, start, total, plotLeft, plotWidth, top + TrackHeight * 4);
            DrawApprovalTrack(context, _turn, start, total, plotLeft, plotWidth, top + TrackHeight * 5);
            DrawPersistTrack(context, _turn, start, total, plotLeft, plotWidth, top + TrackHeight * 6);
            DrawSystemTrack(context, _turn, start, total, plotLeft, plotWidth, top + TrackHeight * 7);
        }

        // 回合标题与耗时已由卡片头部显示，画布内不再重复。
        DrawTrackLabels(context, top);
    }

    private void DrawRuler(DrawingContext context, long start, long total, double left, double width)
    {
        for (var index = 0; index <= 4; index++)
        {
            var fraction = index / 4d;
            var x = left + width * fraction;
            var pen = new Pen(GridBrush ?? Brushes.Gray, 1);
            context.DrawLine(pen, new Point(x, Padding + 10), new Point(x, Height - Padding));
            var visible = _viewStart + (_viewEnd - _viewStart) * fraction;
            var label = PerfPalette.FormatCompactMs(total * 1000d / System.Diagnostics.Stopwatch.Frequency * visible);
            DrawText(context, label, x + 3, 2, 9, MutedBrush);
        }
    }

    private void DrawTrackBackground(DrawingContext context, double top, double left, double width, int count)
    {
        var pen = new Pen(GridBrush ?? Brushes.Gray, 1);
        for (var index = 0; index < count; index++)
        {
            var y = top + index * TrackHeight + TrackHeight - 1;
            context.DrawLine(pen, new Point(left, y), new Point(left + width, y));
        }
    }

    private void DrawTrackLabels(DrawingContext context, double top)
    {
        var labels = new[] { "UI 线程", "事件泵", "Fufu 渲染", "模型生成", "工具执行", "审批阻塞", "持久化", "宿主系统" };
        for (var index = 0; index < labels.Length; index++)
            DrawText(context, labels[index], Padding, top + index * TrackHeight + 7, 10, TextBrush);
    }

    private void DrawFufuTrack(DrawingContext context, PerfTurnInfo turn, long start, long total, double left, double width, double top)
    {
        var fufuSpans = turn.Spans.Where(s => s.Name.StartsWith("fufu.", StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var span in fufuSpans)
            DrawSpan(context, span, start, total, left, width, top, PerfPalette.ApprovalBrush);

        // 实时渲染指标属于当前进程；历史回合不应冒用当前 FPS 与累计掉帧。
        var hasRender = turn.Outcome == "running" &&
                        Fufucode.Core.Diagnostics.Perf.FufuRenderDiagnostics.HasSamples &&
                        Fufucode.Core.Diagnostics.Perf.FufuRenderDiagnostics.IsFresh(TimeSpan.FromSeconds(5));
        var dropped = hasRender ? Fufucode.Core.Diagnostics.Perf.FufuRenderDiagnostics.DroppedFrames : 0;
        // 探针在 FUFU 进程内；本进程无样本时标记未采集，不把默认帧率当成实测值。
        var label = hasRender
            ? $"Live2D {Fufucode.Core.Diagnostics.Perf.FufuRenderDiagnostics.CurrentFps:0.0} FPS · " +
              $"{Fufucode.Core.Diagnostics.Perf.FufuRenderDiagnostics.LastFrameTimeMs:0.1} ms"
            : "渲染帧率未采集";
        DrawText(context, label, left + 4, top + 7, 9, MutedBrush, clipped: true);

        if (dropped > 0)
        {
            var pen = new Pen(PerfPalette.ErrorBrush, 1.5);
            context.DrawLine(pen, new Point(left + width - 12, top + 4), new Point(left + width - 12, top + TrackHeight - 4));
        }
    }

    private void DrawUiTrack(DrawingContext context, PerfTurnInfo turn, long start, long total, double left, double width, double top)
    {
        foreach (var span in turn.UiSpans)
            DrawSpan(context, span, start, total, left, width, top, PerfPalette.UiBrush);
        foreach (var span in turn.EventSpans)
        {
            if (span.Name == "ui.stall")
            {
                var x = X(span.StartTicks, start, total, left, width);
                var rect = new Rect(x - 1, top + 3, 3, TrackHeight - 7);
                context.DrawRectangle(PerfPalette.ErrorBrush, null, rect, 1, 1);
                _hits.Add((new Rect(x - 4, top, 9, TrackHeight), span));
            }
            else if (span.Name == "ui.batch")
            {
                DrawSpan(context, span, start, total, left, width, top, PerfPalette.UiBrush, 0.65);
            }
        }
    }

    private void DrawEventTrack(DrawingContext context, PerfTurnInfo turn, long start, long total, double left, double width, double top)
    {
        foreach (var item in turn.Events)
        {
            if (item.Kind != "session") continue;
            var x = X(item.TimestampTicks, start, total, left, width);
            context.DrawRectangle(PerfPalette.UiBrush, null, new Rect(x, top + 9, 2, 9), 1, 1);
        }
    }

    private void DrawModelTrack(DrawingContext context, PerfTurnInfo turn, long start, long total, double left, double width, double top)
    {
        foreach (var span in turn.ModelSpans)
        {
            var rect = DrawSpan(context, span, start, total, left, width, top, PerfPalette.ModelBrush);
            if (rect is not { } bar) continue;
            var child = turn.Spans.FirstOrDefault(item => item.Name == "model.stream" &&
                item.StartTicks >= span.StartTicks && item.EndTicks <= span.EndTicks + 1);
            var ttft = child?.LongTag("ttftMs") ?? 0;
            if (ttft <= 0) continue;
            var ttftWidth = Math.Min(bar.Width, bar.Width * ttft / Math.Max(1, span.DurationMs));
            if (ttftWidth <= 0.5) continue;
            // 用主题文本色画首字分界，不写死白色，亮色皮肤下同样可见。
            var markerPen = new Pen(TextBrush ?? Brushes.White, 1);
            using (context.PushOpacity(0.45))
                context.DrawLine(markerPen, new Point(bar.X + ttftWidth, bar.Y + 2),
                    new Point(bar.X + ttftWidth, bar.Y + bar.Height - 2));
        }
    }

    private void DrawToolTrack(DrawingContext context, PerfTurnInfo turn, long start, long total, double left, double width, double top)
    {
        foreach (var span in turn.ToolSpans)
        {
            var rect = DrawSpan(context, span, start, total, left, width, top, PerfPalette.ToolBrush);
            if (rect is not { } bar || bar.Width < 44) continue;
            var name = PerfPalette.ToolDisplayName(span.Tag("tool"));
            DrawText(context, name, bar.X + 4, top + 7, 10, Brushes.Black, clipped: true);
        }
    }

    private void DrawApprovalTrack(DrawingContext context, PerfTurnInfo turn, long start, long total, double left, double width, double top)
    {
        foreach (var span in turn.ApprovalSpans)
            DrawSpan(context, span, start, total, left, width, top, PerfPalette.ApprovalBrush);
    }

    private void DrawPersistTrack(DrawingContext context, PerfTurnInfo turn, long start, long total, double left, double width, double top)
    {
        foreach (var span in turn.PersistSpans)
        {
            var brush = span.Name.StartsWith("context.", StringComparison.Ordinal)
                ? PerfPalette.ContextBrush
                : PerfPalette.PersistBrush;
            DrawSpan(context, span, start, total, left, width, top, brush);
        }
    }

    private void DrawSystemTrack(DrawingContext context, PerfTurnInfo turn, long start, long total, double left, double width, double top)
    {
        var samples = _samples
            .Where(sample => sample.Kind == PerfHostSampleKind.Runtime &&
                             sample.Name == "process.cpuPercent" &&
                             sample.TimestampUtc >= turn.StartUtc &&
                             sample.TimestampUtc <= turn.StartUtc.AddMilliseconds(turn.DurationMs + 200))
            .ToArray();
        if (samples.Length < 2)
        {
            DrawText(context, "无系统样本", left + 4, top + 8, 9, MutedBrush, clipped: true);
            return;
        }

        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            var first = true;
            foreach (var sample in samples)
            {
                var fraction = (sample.TimestampUtc - turn.StartUtc).TotalMilliseconds /
                               Math.Max(1, turn.DurationMs);
                var x = left + Math.Clamp(fraction, 0, 1) * width;
                var y = top + TrackHeight - 5 - Math.Clamp(sample.Value / 100d, 0, 1) * (TrackHeight - 10);
                if (first)
                {
                    path.BeginFigure(new Point(x, y), false);
                    first = false;
                }
                else path.LineTo(new Point(x, y));
            }
        }
        context.DrawGeometry(null, new Pen(PerfPalette.SystemBrush, 1.2), geometry);
    }

    private Rect? DrawSpan(DrawingContext context, SpanRecord span, long start, long total,
        double left, double width, double top, IBrush brush, double opacity = 1)
    {
        var startFraction = (span.StartTicks - start) / (double)total;
        var endFraction = (span.EndTicks - start) / (double)total;
        if (endFraction < _viewStart || startFraction > _viewEnd) return null;
        var x1 = X(span.StartTicks, start, total, left, width);
        var x2 = X(span.EndTicks, start, total, left, width);
        var rect = new Rect(Math.Max(left, x1), top + 5, Math.Max(1.5, Math.Min(left + width, x2) - Math.Max(left, x1)),
            TrackHeight - 12);
        if (rect.Width <= 0) return null;
        using (context.PushOpacity(opacity))
            context.DrawRectangle(brush, null, rect, 3, 3);
        if (span.SpanId == _selectedSpanId)
            context.DrawRectangle(null, new Pen(AccentBrush ?? Brushes.White, 1.2), rect.Inflate(1), 3, 3);
        _hits.Add((rect.Inflate(2), span));
        return rect;
    }

    private double X(long ticks, long start, long total, double left, double width)
    {
        var fraction = (ticks - start) / (double)total;
        var visible = (fraction - _viewStart) / Math.Max(0.0001, _viewEnd - _viewStart);
        return left + Math.Clamp(visible, 0, 1) * width;
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ResetView();
            return;
        }
        var position = e.GetPosition(this);
        for (var index = _hits.Count - 1; index >= 0; index--)
        {
            if (!_hits[index].Rect.Contains(position)) continue;
            var span = _hits[index].Span;
            _selectedSpanId = span.SpanId;
            SpanSelected?.Invoke(span);
            InvalidateVisual();
            return;
        }
    }

    private void OnPointerWheel(object? sender, PointerWheelEventArgs e)
    {
        if (_turn is null) return;
        var position = e.GetPosition(this);
        var plotLeft = LabelWidth;
        var plotWidth = Math.Max(1, Bounds.Width - LabelWidth - Padding);
        var cursor = Math.Clamp((position.X - plotLeft) / plotWidth, 0, 1);
        var at = _viewStart + cursor * (_viewEnd - _viewStart);
        var factor = e.Delta.Y > 0 ? 0.82 : 1.22;
        var span = Math.Clamp((_viewEnd - _viewStart) * factor, 0.02, 1);
        var nextStart = Math.Clamp(at - cursor * span, 0, 1 - span);
        _viewStart = nextStart;
        _viewEnd = nextStart + span;
        e.Handled = true;
        InvalidateVisual();
    }

    private void DrawText(DrawingContext context, string text, double x, double y, double size,
        IBrush? brush, bool clipped = false, bool mono = false)
    {
        if (string.IsNullOrEmpty(text)) return;
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            mono ? MonoFace : Face, size, brush ?? Brushes.Gray);
        context.DrawText(formatted, new Point(x, y));
    }
}
