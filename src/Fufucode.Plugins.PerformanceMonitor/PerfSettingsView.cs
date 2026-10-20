using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Fufucode.Core.Diagnostics.Perf;

namespace Fufucode.Plugins.PerformanceMonitor;

/// <summary>
/// 设置页内容：采集模式、命令文本开关、保留天数、缓冲容量、统计概览与操作按钮。
/// 插件自管配置，宿主不感知字段。
/// </summary>
internal sealed class PerfSettingsView : UserControl
{
    private readonly PerfRecorder _recorder;
    private readonly Func<string> _export;
    private readonly ComboBox _mode;
    private readonly CheckBox _captureCommands;
    private readonly NumericUpDown _retention;
    private readonly NumericUpDown _capacity;
    private readonly TextBlock _status;
    private bool _loading;
    private readonly Action _openDirectory;
    private readonly TextBlock _history = new() { Classes = { "caption" }, TextWrapping = TextWrapping.Wrap };

    public PerfSettingsView(PerfRecorder recorder, Func<string> export, Action openDirectory)
    {
        _recorder = recorder;
        _export = export;
        _openDirectory = openDirectory;
        var settings = recorder.Settings;
        _mode = new ComboBox
        {
            ItemsSource = new[] { "关闭（off）", "标准（standard）", "详细（detailed）" },
            SelectedIndex = settings.Mode switch { "off" => 0, "detailed" => 2, _ => 1 },
            MinWidth = 180,
        };
        _captureCommands = new CheckBox { Content = "记录脱敏后的命令文本", IsChecked = settings.CaptureCommandText };
        _retention = new NumericUpDown { Minimum = 0, Maximum = 365, Value = settings.RetentionDays, MinWidth = 120 };
        _capacity = new NumericUpDown
        {
            Minimum = 10_000, Maximum = 1_000_000, Increment = 10_000, Value = settings.RingCapacity, MinWidth = 140,
        };
        _status = new TextBlock { TextWrapping = TextWrapping.Wrap, Classes = { "caption" } };

        _loading = true;
        _mode.SelectionChanged += (_, _) => Save();
        _captureCommands.IsCheckedChanged += (_, _) => Save();
        _retention.ValueChanged += (_, _) => Save();
        _capacity.ValueChanged += (_, _) => Save();
        _loading = false;

        Content = new StackPanel
        {
            Spacing = 12,
            Margin = new Thickness(4, 8, 4, 8),
            Children =
            {
                Heading("性能监控"),
                Note("采集每个回合的模型请求（TTFB/TTFT、流式帧间隔）、工具与命令执行、审批等待、上下文压缩、会话持久化，" +
                     "以及界面线程卡顿与未处理异常。数据脱敏后写入数据目录，在停靠区“性能”页查看。"),
                Row("采集模式", _mode, "标准模式记录每个回合的模型/工具/UI 批处理；详细模式额外记录 span 开始事件与更细的帧数据。"),
                Row("命令文本", _captureCommands, "关闭后只记录“运行过命令”与退出码，不记录参数内容。"),
                Row("数据保留（天）", _retention, "0 表示只在内存中保留，不写入磁盘。"),
                Row("内存缓冲（条）", _capacity, "超出后覆盖最旧记录；不会无界增长。"),
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children =
                    {
                        Button("打开数据目录", () => _openDirectory()),
                        Button("导出诊断包", () => { _status.Text = _export(); RefreshStatus(); }),
                        Button("清空内存", () => { _recorder.Clear(); RefreshStatus(); }),
                    },
                },
                _status,
                Heading("最近回合（插件启用后逐回合累积）"),
                _history,
            },
        };
        RefreshStatus();
        RefreshHistory();
    }

    /// <summary>读取插件落盘的回合样本，展示最近 8 个回合的耗时与 token。</summary>
    public void RefreshHistory()
    {
        var dir = Path.Combine(_recorder.DataRoot);
        var lines = Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, "host-*.jsonl").OrderByDescending(f => f)
                .SelectMany(File.ReadLines).Where(l => l.Contains("\"kind\":0")).Take(8).ToList()
            : new List<string>();
        if (lines.Count == 0) { _history.Text = "还没有完成的回合。"; return; }
        _history.Text = string.Join(Environment.NewLine, lines.Select(line =>
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var ms = root.GetProperty("value").GetDouble();
                var input = root.TryGetProperty("inputTokens", out var i) && i.ValueKind == JsonValueKind.Number ? i.GetInt64() : (long?)null;
                var output = root.TryGetProperty("outputTokens", out var o) && o.ValueKind == JsonValueKind.Number ? o.GetInt64() : (long?)null;
                return $"{root.GetProperty("timestampUtc").GetDateTimeOffset().ToLocalTime():HH:mm:ss}  " +
                       $"{(ms >= 1000 ? $"{ms / 1000:0.00} s" : $"{ms:0} ms")}  {root.GetProperty("name").GetString()}  " +
                       $"{(input is null ? "token 未知" : $"输入 {input} · 输出 {output}")}";
            }
            catch (JsonException) { return null; }
        }).Where(line => line is not null));
    }

    private void Save()
    {
        if (_loading) return;
        var mode = _mode.SelectedIndex switch { 0 => "off", 2 => "detailed", _ => "standard" };
        _recorder.ApplySettings(_recorder.Settings with
        {
            Mode = mode,
            CaptureCommandText = _captureCommands.IsChecked == true,
            RetentionDays = (int)(_retention.Value ?? 7),
            RingCapacity = (int)(_capacity.Value ?? 200_000),
        });
        RefreshStatus();
    }

    public void RefreshStatus()
    {
        var settings = _recorder.Settings;
        _status.Text = $"已记录 span {_recorder.Ring.TotalWritten:N0} 条 · 内存 {_recorder.Ring.Count:N0}/{_recorder.Ring.Capacity:N0}" +
            $" · 覆盖 {_recorder.Ring.Overwritten:N0} · 丢弃 {_recorder.Dropped:N0} · 异常 {_recorder.Anomalies().Count:N0}" +
            $" · 数据目录 {(settings.RetentionDays == 0 ? "（仅内存）" : _recorder.DataRoot)}";
    }

    private static Control Heading(string text) => new TextBlock { Text = text, Classes = { "bodyStrong" }, FontSize = 15 };

    private static Control Note(string text) => new TextBlock
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Classes = { "caption" },
        Opacity = 0.8,
    };

    private static Control Row(string label, Control control, string hint) => new StackPanel
    {
        Spacing = 4,
        Children =
        {
            new TextBlock { Text = label, Classes = { "bodyStrong" } },
            control,
            new TextBlock { Text = hint, TextWrapping = TextWrapping.Wrap, Classes = { "caption" }, Opacity = 0.7 },
        },
    };

    private static Button Button(string text, Action action)
    {
        var button = new Button { Content = text };
        button.Click += (_, _) => action();
        return button;
    }
}
