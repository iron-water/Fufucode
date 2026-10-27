using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;

namespace Fufucode.Plugins.PerformanceMonitor;

/// <summary>从启动到现在的压缩总览：每一轮一段，点击跳到该轮。</summary>
internal sealed class PerfOverviewStrip : Control
{
    private const double Top = 4;
    private const double BarHeight = 26;
    private static readonly Typeface Face = new(FontFamily.Default);
    private readonly List<(Rect Rect, PerfTurnInfo Turn)> _segments = [];
    private IReadOnlyList<PerfTurnInfo> _turns = [];
    private long _selectedTraceId;

    public static readonly StyledProperty<IBrush?> MutedBrushProperty =
        AvaloniaProperty.Register<PerfOverviewStrip, IBrush?>(nameof(MutedBrush));
    public static readonly StyledProperty<IBrush?> GridBrushProperty =
        AvaloniaProperty.Register<PerfOverviewStrip, IBrush?>(nameof(GridBrush));
    public static readonly StyledProperty<IBrush?> AccentBrushProperty =
        AvaloniaProperty.Register<PerfOverviewStrip, IBrush?>(nameof(AccentBrush));

    public PerfOverviewStrip()
    {
        Height = 58;
        ClipToBounds = true;
        this.Bind(MutedBrushProperty, new DynamicResourceExtension("AppTextDim"));
        this.Bind(GridBrushProperty, new DynamicResourceExtension("AppDividerSubtle"));
        this.Bind(AccentBrushProperty, new DynamicResourceExtension("AppAccentText"));
        PointerPressed += OnPointerPressed;
    }

    public IBrush? MutedBrush { get => GetValue(MutedBrushProperty); set => SetValue(MutedBrushProperty, value); }
    public IBrush? GridBrush { get => GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }
    public IBrush? AccentBrush { get => GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }

    public event Action<long>? TurnSelected;

    public void SetData(IReadOnlyList<PerfTurnInfo> turns, long selectedTraceId)
    {
        _turns = turns;
        _selectedTraceId = selectedTraceId;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        _segments.Clear();
        var width = Bounds.Width;
        if (width <= 2) return;

        var track = new Rect(0, Top, width, BarHeight);
        context.DrawRectangle(GridBrush ?? Brushes.Gray, new Pen(GridBrush ?? Brushes.Gray, 1), track, 7, 7);

        if (_turns.Count == 0)
        {
            DrawText(context, "尚无性能数据", 8, Top + 7, 10);
            return;
        }

        var start = _turns.Min(turn => turn.StartUtc);
        var end = _turns.Max(turn => turn.StartUtc.AddMilliseconds(turn.DurationMs));
        var total = Math.Max(1, (end - start).TotalMilliseconds);
        foreach (var turn in _turns)
        {
            var x = (turn.StartUtc - start).TotalMilliseconds / total * width;
            var w = Math.Max(3, turn.DurationMs / total * width);
            var rect = new Rect(x, Top + 4, Math.Max(2, w - 1.5), BarHeight - 8);
            // 回合底轨 + 真实模型/工具片段：单轮时也能看出时间花在哪，而不是一条纯色条。
            context.DrawRectangle(GridBrush ?? Brushes.Gray, null, rect, 3, 3);
            var failed = turn.Outcome is "error" or "failed" or "Error" or "Failed";
            var turnStart = turn.Spans.Count > 0 ? turn.Spans.Min(span => span.StartTicks) : 0;
            var turnTicks = turn.Spans.Count > 0
                ? Math.Max(1, turn.Spans.Max(span => span.EndTicks) - turnStart)
                : 1;
            DrawSpans(context, turn.ModelSpans, turnStart, turnTicks, rect, PerfPalette.ModelBrush, 4, 6);
            DrawSpans(context, turn.ToolSpans, turnStart, turnTicks, rect, PerfPalette.ToolBrush, 14, 6);
            if (failed)
                context.DrawRectangle(PerfPalette.ErrorBrush, null,
                    new Rect(rect.X, rect.Bottom - 3, rect.Width, 2), 1, 1);
            if (turn.Spans.Count == 0)
                context.DrawRectangle(new SolidColorBrush(PerfPalette.Ui), null, rect.Deflate(1), 2, 2);
            if (turn.TraceId == _selectedTraceId)
                context.DrawRectangle(null, new Pen(AccentBrush ?? Brushes.White, 1.2), rect.Inflate(1), 3, 3);
            _segments.Add((new Rect(x - 2, Top, Math.Max(4, w + 3), BarHeight), turn));
        }

        for (var index = 0; index <= 4; index++)
        {
            var fraction = index / 4d;
            var time = start.AddMilliseconds(total * fraction).ToLocalTime();
            var label = (end - start).TotalHours >= 2 ? time.ToString("HH:mm") : time.ToString("HH:mm:ss");
            var text = new FormattedText(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, 9,
                MutedBrush ?? Brushes.Gray);
            var tx = Math.Clamp(width * fraction - text.Width / 2, 0, Math.Max(0, width - text.Width));
            context.DrawText(text, new Point(tx, Top + BarHeight + 5));
        }
    }

    private static void DrawSpans(DrawingContext context, IReadOnlyList<SpanRecord> spans, long turnStart,
        long turnTicks, Rect rect, IBrush brush, double topOffset, double height)
    {
        foreach (var span in spans)
        {
            var offset = (span.StartTicks - turnStart) / (double)turnTicks;
            var width = Math.Max(1.5, (span.EndTicks - span.StartTicks) / (double)turnTicks * rect.Width);
            var x = rect.X + Math.Clamp(offset, 0, 0.98) * rect.Width;
            var clipped = Math.Min(width, rect.Right - x);
            if (clipped <= 0.5) continue;
            context.DrawRectangle(brush, null,
                new Rect(x, rect.Y + topOffset, clipped, height), 2, 2);
        }
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var x = e.GetPosition(this).X;
        foreach (var (rect, turn) in _segments)
        {
            if (!rect.Contains(new Point(x, Top + BarHeight / 2))) continue;
            _selectedTraceId = turn.TraceId;
            TurnSelected?.Invoke(turn.TraceId);
            InvalidateVisual();
            return;
        }
    }

    private void DrawText(DrawingContext context, string text, double x, double y, double size)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, size,
            MutedBrush ?? Brushes.Gray);
        context.DrawText(formatted, new Point(x, y));
    }
}
