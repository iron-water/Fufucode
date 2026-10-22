using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;

namespace Fufucode.Plugins.PerformanceMonitor;

/// <summary>性能中心：左侧两个视图，概览讲人话，分析讲全链路；刷新在后台构建快照。</summary>
internal sealed class PerfCenterView : UserControl, IDisposable
{
    private readonly PerfRecorder _recorder;
    private readonly PerfOverviewView _overview = new();
    private readonly PerfAnalysisView _analysis = new();
    private readonly ContentControl _content = new();
    private readonly ListBox _nav = new()
    {
        ItemsSource = new[] { "概览", "分析" },
        SelectedIndex = 0,
        Background = Brushes.Transparent,
        BorderThickness = new Thickness(0),
        Padding = new Thickness(0),
    };
    private readonly TextBlock _title = new() { FontSize = 18, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _subtitle = new() { FontSize = 10.5 };
    private readonly TextBlock _status = new() { FontSize = 9.5, TextWrapping = TextWrapping.Wrap };
    private readonly CheckBox _live = new() { Content = "实时", IsChecked = true };
    private readonly DispatcherTimer _timer;
    private PerfSnapshot? _snapshot;
    private long _lastTotal = -1;
    private int _refreshing;
    private bool _disposed;

    public PerfCenterView(PerfRecorder recorder)
    {
        _recorder = recorder;
        _title.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("AppTextPrimary"));
        _subtitle.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("AppTextDim"));
        _status.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("AppTextDim"));

        _nav.SelectionChanged += (_, _) => SwitchView();
        _overview.TurnSelected += traceId =>
        {
            _nav.SelectedIndex = 1;
            _analysis.ShowTrace(traceId);
        };
        _analysis.RefreshRequested += () => Refresh(force: true);

        var titleBlock = new StackPanel { Spacing = 3, Children = { _title, _subtitle } };
        var refresh = new Button
        {
            Content = "刷新",
            FontSize = 10,
            Padding = new Thickness(9, 5),
        };
        refresh.Click += (_, _) => Refresh(force: true);
        _live.Margin = new Thickness(8, 0, 0, 0);
        _live.FontSize = 10.5;
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { refresh, _live },
        };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(16, 12, 16, 10) };
        header.Children.Add(titleBlock);
        Grid.SetColumn(actions, 1);
        header.Children.Add(actions);
        var headerBorder = new Border { Child = header, BorderThickness = new Thickness(0, 0, 0, 1) };
        headerBorder.Bind(Border.BorderBrushProperty, new DynamicResourceExtension("AppDividerSubtle"));

        var navTitle = new TextBlock { Text = "视图", FontSize = 9.5, Margin = new Thickness(8, 4, 8, 4) };
        navTitle.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("AppTextDim"));
        var navPanel = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        navPanel.Children.Add(navTitle);
        Grid.SetRow(_nav, 1);
        navPanel.Children.Add(_nav);
        _status.Margin = new Thickness(8, 8, 8, 4);
        Grid.SetRow(_status, 2);
        navPanel.Children.Add(_status);
        var navBorder = new Border
        {
            Width = 136,
            Padding = new Thickness(6, 8, 6, 8),
            Child = navPanel,
            BorderThickness = new Thickness(0, 0, 1, 0),
        };
        navBorder.Bind(Border.BorderBrushProperty, new DynamicResourceExtension("AppDividerSubtle"));

        _content.Margin = new Thickness(14, 12, 14, 14);
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        body.Children.Add(navBorder);
        Grid.SetColumn(_content, 1);
        body.Children.Add(_content);
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        root.Children.Add(headerBorder);
        Grid.SetRow(body, 1);
        root.Children.Add(body);
        Content = root;
        SwitchView();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _timer.Tick += async (_, _) =>
        {
            if (_disposed || _live.IsChecked != true || !IsEffectivelyVisible) return;
            if (_recorder.Ring.TotalWritten == _lastTotal) return;
            await RefreshAsync(force: false);
        };
        _timer.Start();
        _ = RefreshAsync(force: true);
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
            await Dispatcher.UIThread.InvokeAsync(() => _status.Text = "刷新失败：" + error.Message);
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
        var settings = _recorder.Settings;
        var mode = settings.Mode switch { "off" => "关闭", "detailed" => "详细", _ => "标准" };
        _status.Text = $"{mode} · {_recorder.Ring.Count:N0}/{_recorder.Ring.Capacity:N0} span · " +
                       $"丢弃 {_recorder.Dropped:N0} · 异常 {_recorder.Anomalies().Count:N0}";
        UpdateHeader();
    }

    private void SwitchView()
    {
        var index = Math.Max(0, _nav.SelectedIndex);
        _content.Content = index == 0 ? _overview : _analysis;
        UpdateHeader();
    }

    private void UpdateHeader()
    {
        var snapshot = _snapshot;
        if (_nav.SelectedIndex == 0)
        {
            _title.Text = "会话概览";
            _subtitle.Text = snapshot is null
                ? "正在读取性能数据…"
                : $"{_recorder.CurrentSessionId ?? "当前会话"} · {PerfPalette.FormatDuration(snapshot.WallDuration)} · " +
                  $"{snapshot.TurnCount} 轮 · {snapshot.TotalTokens:N0} token";
        }
        else
        {
            _title.Text = "分析";
            _subtitle.Text = snapshot is null
                ? "正在读取性能数据…"
                : $"启动 → 现在 · {snapshot.AllSpans.Count:N0} 个 span · {snapshot.StallCount} 次阻塞 · " +
                  $"{snapshot.ErrorCount} 个错误";
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
    }
}
