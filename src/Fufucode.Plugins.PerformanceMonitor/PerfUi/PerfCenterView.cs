using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;

namespace Fufucode.Plugins.PerformanceMonitor;

/// <summary>
/// 性能中心主控件：顶部仅保留 [ 概览 | 分析 | 协同通路 ] 分段按钮；
/// 无多余边栏、模型徽章与刷新/实时控件，数据全自动感知并持续刷新。
/// </summary>
internal sealed class PerfCenterView : UserControl, IDisposable
{
    private readonly PerfSource _source;
    private readonly PerfOverviewView _overview = new();
    private readonly PerfAnalysisView _analysis = new();
    private readonly PerfCompanionNetworkView _companionView = new();
    private readonly ContentControl _content = new();

    // 顶部超紧凑切换按钮
    private readonly Button _btnOverview = new();
    private readonly Button _btnAnalysis = new();
    private readonly Button _btnCompanion = new();

    private readonly DispatcherTimer _timer;
    private PerfSnapshot? _snapshot;
    private long _lastTotal = -1;
    private int _refreshing;
    private int _activeViewIndex; // 0 = 概览, 1 = 分析, 2 = 协同通路
    private bool _disposed;

    /// <summary>无头/自测诊断：数据链路各段的实际计数，不参与产品界面。</summary>
    public string Diagnostic =>
        $"spans={_source.SnapshotSpans().Count}; total={_source.TotalWritten}; active={_source.ActiveSpanCount}; " +
        $"lastTotal={_lastTotal}; session={_source.SessionContext?.SessionId ?? "-"}; " +
        $"snapshot={_snapshot?.AllSpans.Count ?? -1}; turns={_snapshot?.TurnCount ?? -1}; " +
        $"sink={Fufucode.Core.Diagnostics.PerfTracer.Sink?.GetType().Name ?? "-"}; " +
        $"sameType={Fufucode.Core.Diagnostics.PerfTracer.Sink is PerfRecorder}; " +
        $"timer={_timer.IsEnabled}; disposed={_disposed}";

    /// <summary>无头验证用：切换到指定视图（0 概览 / 1 分析 / 2 协同通路）。</summary>
    public void ShowViewForTesting(int index) => SwitchView(index);

    public PerfCenterView(PerfSource source)
    {
        _source = source;

        _overview.TurnSelected += traceId =>
        {
            SwitchView(1);
            _analysis.ShowTrace(traceId);
        };

        // 分段按钮
        SetupSegmentButton(_btnOverview, "概览", 0);
        SetupSegmentButton(_btnAnalysis, "分析", 1);
        SetupSegmentButton(_btnCompanion, "协同通路", 2);

        var switcherPill = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(2),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 2,
                Children = { _btnOverview, _btnAnalysis, _btnCompanion },
            },
        };
        switcherPill.Bind(Border.BackgroundProperty, new DynamicResourceExtension("AppSubtleBackground"));
        switcherPill.Bind(Border.BorderBrushProperty, new DynamicResourceExtension("AppDividerSubtle"));
        switcherPill.BorderThickness = new Thickness(1);

        // 顶栏只保留分段切换：模型与实时状态由概览/分析页自身呈现，不重复占一行。
        var topBar = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Height = 36,
            Margin = new Thickness(12, 4, 12, 4),
        };
        topBar.Children.Add(switcherPill);

        var topBorder = new Border
        {
            Child = topBar,
            BorderThickness = new Thickness(0, 0, 0, 1),
        };
        topBorder.Bind(Border.BorderBrushProperty, new DynamicResourceExtension("AppDividerSubtle"));

        var root = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
        };
        root.Children.Add(topBorder);
        Grid.SetRow(_content, 1);
        root.Children.Add(_content);
        Content = root;

        SwitchView(0);

        // 自动静默定时轮询：数据变动自动重刷，无需人工操作
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
        _timer.Tick += async (_, _) =>
        {
            if (_disposed) return;
            // 隐藏/浮动窗口也可能接收数据：按数据变化刷新，不因不可见而永久停在空快照。
            var changed = _source.TotalWritten != _lastTotal || _source.ActiveSpanCount > 0;
            if (_activeViewIndex == 2 && (changed || IsEffectivelyVisible)) _companionView.UpdateData();
            if (changed) await RefreshAsync(force: false);
        };
        _timer.Start();
        _ = RefreshAsync(force: true);
    }

    private void SetupSegmentButton(Button button, string label, int index)
    {
        button.Content = label;
        button.FontSize = 12;
        button.Padding = new Thickness(12, 3);
        button.BorderThickness = new Thickness(0);
        button.CornerRadius = new CornerRadius(4);
        button.Click += (_, _) => SwitchView(index);
    }

    private void SwitchView(int index)
    {
        _activeViewIndex = index;
        Control targetView = index switch
        {
            1 => _analysis,
            2 => _companionView,
            _ => _overview,
        };
        _content.Content = targetView;
        if (index == 2) _companionView.UpdateData();

        UpdateSegmentButtonStyle(_btnOverview, index == 0);
        UpdateSegmentButtonStyle(_btnAnalysis, index == 1);
        UpdateSegmentButtonStyle(_btnCompanion, index == 2);

        AnimateViewTransition(targetView);
    }

    private static void AnimateViewTransition(Control control)
    {
        control.Opacity = 0;
        var translate = new TranslateTransform(0, 4);
        control.RenderTransform = translate;
        control.Transitions = new Avalonia.Animation.Transitions
        {
            new Avalonia.Animation.DoubleTransition
            {
                Property = Visual.OpacityProperty,
                Duration = TimeSpan.FromMilliseconds(150),
                Easing = new Avalonia.Animation.Easings.ExponentialEaseOut(),
            },
            new Avalonia.Animation.DoubleTransition
            {
                Property = TranslateTransform.YProperty,
                Duration = TimeSpan.FromMilliseconds(150),
                Easing = new Avalonia.Animation.Easings.ExponentialEaseOut(),
            },
        };
        Dispatcher.UIThread.Post(() =>
        {
            control.Opacity = 1;
            translate.Y = 0;
        }, DispatcherPriority.Render);
    }

    private static void UpdateSegmentButtonStyle(Button button, bool active)
    {
        button.FontWeight = active ? FontWeight.SemiBold : FontWeight.Normal;
        if (active)
        {
            button.Bind(Button.BackgroundProperty, new DynamicResourceExtension("AppCardBackground"));
            button.Bind(Button.ForegroundProperty, new DynamicResourceExtension("AppTextPrimary"));
        }
        else
        {
            button.Background = Brushes.Transparent;
            button.Bind(Button.ForegroundProperty, new DynamicResourceExtension("AppTextDim"));
        }
    }

    public void Refresh(bool force)
    {
        if (_disposed) return;
        _ = RefreshAsync(force);
    }

    public void NotifySettingsChanged() => Refresh(force: true);

    private async Task RefreshAsync(bool force)
    {
        if (_disposed || Interlocked.Exchange(ref _refreshing, 1) != 0) return;
        try
        {
            var total = _source.TotalWritten;
            // 回合进行中还没有 span 结束：只要仍有活动 span，就必须继续刷新并显示实时耗时。
            var activeCount = _source.ActiveSpanCount;
            if (!force && total == _lastTotal && activeCount == 0) return;
            var spans = _source.SnapshotSpans();
            if (activeCount > 0)
            {
                var completed = spans.Select(span => span.SpanId).ToHashSet();
                spans.AddRange(_source.ActiveSpans().Where(span => !completed.Contains(span.SpanId)));
            }
            var events = _source.RecentEvents(4_000);
            var samples = _source.HostSamples(1_200);
            var context = _source.SessionContext;
            var snapshot = await Task.Run(() => PerfSnapshot.Build(spans, events, samples, context)).ConfigureAwait(false);
            if (_disposed) return;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_disposed) return;
                _snapshot = snapshot;
                _lastTotal = total;
                Apply(snapshot);
            });
        }
        catch (Exception error)
        {
            System.Diagnostics.Trace.TraceWarning("Perf snapshot build failed: {0}", error.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }

    private void Apply(PerfSnapshot snapshot)
    {
        _overview.Render(snapshot);
        _analysis.SetSnapshot(snapshot);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _overview.StopAnimations();
    }
}
