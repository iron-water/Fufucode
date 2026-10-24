using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;

namespace Fufucode.Plugins.PerformanceMonitor;

/// <summary>
/// 性能中心主控件：顶部仅保留 [ 概览 | 分析 ] 切换分段按钮与模型指示；
/// 无多余边栏与刷新/实时按钮，数据全自动后台感知与实时刷新。
/// </summary>
internal sealed class PerfCenterView : UserControl, IDisposable
{
    private readonly PerfRecorder _recorder;
    private readonly PerfOverviewView _overview = new();
    private readonly PerfAnalysisView _analysis = new();
    private readonly PerfCompanionNetworkView _companionView = new();
    private readonly ContentControl _content = new();

    // 顶部超紧凑切换按钮
    private readonly Button _btnOverview = new();
    private readonly Button _btnAnalysis = new();
    private readonly Button _btnCompanion = new();
    private readonly Border _modelBadge = new();
    private readonly TextBlock _modelText = new() { FontSize = 10 };

    private readonly DispatcherTimer _timer;
    private PerfSnapshot? _snapshot;
    private long _lastTotal = -1;
    private int _refreshing;
    private int _activeViewIndex; // 0 = 概览, 1 = 分析, 2 = 协同通路
    private bool _disposed;

    public PerfCenterView(PerfRecorder recorder)
    {
        _recorder = recorder;

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
            CornerRadius = new CornerRadius(8),
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

        // 状态指示：模型徽章 + 实时绿点
        _modelText.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("AppTextDim"));
        _modelBadge.CornerRadius = new CornerRadius(999);
        _modelBadge.Padding = new Thickness(8, 2);
        _modelBadge.VerticalAlignment = VerticalAlignment.Center;
        _modelBadge.Child = _modelText;
        _modelBadge.Bind(Border.BackgroundProperty, new DynamicResourceExtension("AppSubtleBackground"));

        var liveDot = new Ellipse
        {
            Width = 6,
            Height = 6,
            Fill = new SolidColorBrush(Color.Parse("#4ADE80")),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var liveText = new TextBlock
        {
            Text = "实时",
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
        };
        liveText.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("AppTextDim"));
        var liveBadge = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 5,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { liveDot, liveText },
        };

        var rightStatus = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _modelBadge, liveBadge },
        };

        var topBar = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            Height = 36,
            Margin = new Thickness(12, 4, 12, 4),
        };
        topBar.Children.Add(switcherPill);
        Grid.SetColumn(rightStatus, 2);
        topBar.Children.Add(rightStatus);

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
            if (_disposed || !IsEffectivelyVisible) return;
            if (_activeViewIndex == 2) _companionView.UpdateData();
            if (_recorder.Ring.TotalWritten == _lastTotal) return;
            await RefreshAsync(force: false);
        };
        _timer.Start();
        _ = RefreshAsync(force: true);
    }

    private void SetupSegmentButton(Button button, string label, int index)
    {
        button.Content = label;
        button.FontSize = 11;
        button.Padding = new Thickness(14, 4);
        button.BorderThickness = new Thickness(0);
        button.CornerRadius = new CornerRadius(6);
        button.Click += (_, _) => SwitchView(index);
    }

    private void SwitchView(int index)
    {
        _activeViewIndex = index;
        _content.Content = index switch
        {
            1 => _analysis,
            2 => _companionView,
            _ => _overview,
        };
        if (index == 2) _companionView.UpdateData();

        UpdateSegmentButtonStyle(_btnOverview, index == 0);
        UpdateSegmentButtonStyle(_btnAnalysis, index == 1);
        UpdateSegmentButtonStyle(_btnCompanion, index == 2);
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
            var total = _recorder.Ring.TotalWritten;
            if (!force && total == _lastTotal) return;
            var spans = _recorder.Ring.Snapshot();
            var events = _recorder.RecentEvents(4_000);
            var samples = _recorder.HostSamples(1_200);
            var snapshot = await Task.Run(() => PerfSnapshot.Build(spans, events, samples)).ConfigureAwait(false);
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

        var modelName = snapshot.ContextBreakdown.ModelName;
        _modelText.Text = string.IsNullOrWhiteSpace(modelName)
            ? (_recorder.CurrentSessionId ?? "当前会话")
            : modelName;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
    }
}
