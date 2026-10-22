using Fufucode.Core.Diagnostics;
using Fufucode.Core.Diagnostics.Perf;

namespace Fufucode.Plugins.PerformanceMonitor;

/// <summary>
/// 性能监控插件的数据接收端：脱敏、存入环形缓冲、写入 JSONL、聚合、跑异常规则。
/// 所有方法都从调用线程（模型流、工具、UI）同步调用，因此只做 O(1)/O(tags) 的工作，
/// 落盘交给 <see cref="PerfJsonlWriter"/> 的后台任务。
/// </summary>
public sealed class PerfRecorder : IPerfSink, IPerfModeProvider
{
    private readonly object _settingsGate = new();
    private PerfSettings _settings;
    private readonly SpanRing _ring;
    private readonly PerfAggregator _aggregator = new();
    private readonly AnomalyRules _rules = new();
    private PerfJsonlWriter? _writer;
    private readonly string _dataRoot;
    private readonly List<Anomaly> _anomalies = [];
    private readonly Queue<PerfEventRecord> _recentEvents = new();
    private readonly Queue<PerfHostSample> _hostSamples = new();
    private const int MaxAnomalies = 2_000;
    private const int MaxRecentEvents = 5_000;
    private const int MaxHostSamples = 2_400;
    private long _spanCount;
    private string? _currentSessionId;

    public PerfRecorder(PerfSettings settings, string dataRoot)
    {
        _dataRoot = dataRoot;
        _settings = settings.Normalize();
        Directory.CreateDirectory(dataRoot);
        _ring = new SpanRing(_settings.RingCapacity);
        _writer = _settings.RetentionDays > 0
            ? new PerfJsonlWriter(dataRoot, _settings.SegmentMb)
            : null;
        _writer?.Prune(DateTime.UtcNow.AddDays(-_settings.RetentionDays));
    }

    public PerfSettings Settings { get { lock (_settingsGate) return _settings; } }

    public PerfMode Mode => _settings.ModeValue;

    public bool CaptureCommandText => _settings.CaptureCommandText;

    public SpanRing Ring => _ring;

    public PerfAggregator Aggregator => _aggregator;

    public AnomalyRules Rules => _rules;

    public string DataRoot => _dataRoot;

    public long SpanCount => Interlocked.Read(ref _spanCount);

    public long Dropped => _writer?.Dropped ?? 0;

    public void SetCurrentSession(string? sessionId) => Volatile.Write(ref _currentSessionId, sessionId);

    public string? CurrentSessionId => Volatile.Read(ref _currentSessionId);

    /// <summary>宿主最近的进程/UI 样本，按时间顺序返回尾部 limit 条。</summary>
    public IReadOnlyList<PerfHostSample> HostSamples(int limit = 1_200)
    {
        lock (_hostSamples)
        {
            if (_hostSamples.Count == 0 || limit <= 0) return [];
            var all = _hostSamples.ToArray();
            var take = Math.Min(limit, all.Length);
            return all[^take..];
        }
    }

    public void AddHostSample(PerfHostSample sample)
    {
        lock (_hostSamples)
        {
            _hostSamples.Enqueue(sample);
            while (_hostSamples.Count > MaxHostSamples) _hostSamples.Dequeue();
        }
    }

    public IReadOnlyList<Anomaly> Anomalies()
    {
        lock (_anomalies)
        {
            var copy = _anomalies.ToArray();
            Array.Reverse(copy);
            return copy;
        }
    }

    public IReadOnlyList<PerfEventRecord> RecentEvents(int limit = 200)
    {
        lock (_recentEvents) return _recentEvents.Reverse().Take(limit).ToArray();
    }

    public void ApplySettings(PerfSettings settings)
    {
        var normalized = settings.Normalize();
        PerfSettings previous;
        lock (_settingsGate)
        {
            previous = _settings;
            _settings = normalized;
        }
        // 保留期关闭（0 = 仅内存）时释放落盘写入器；重新开启时重建。
        if (previous.RetentionDays != normalized.RetentionDays)
        {
            // 保留天数在“仅内存”与“落盘”之间切换：停旧写入器，按新设置重建。
            _writer?.Dispose();
            _writer = normalized.RetentionDays > 0 ? new PerfJsonlWriter(_dataRoot, normalized.SegmentMb) : null;
        }
        normalized.Save(_dataRoot);
        _aggregator.Clear();
        _writer?.Prune(DateTime.UtcNow.AddDays(-normalized.RetentionDays));
    }

    public void OnSpanStart(in PerfSpanStart start)
    {
        // 详细模式目前不单独记录开始事件：结束记录已包含开始时间戳，足以还原瀑布图。
    }

    public void OnSpanEnd(in PerfSpanEnd end)
    {
        // 关闭模式：只保留计数，不记录、不聚合、不写盘。
        if (Mode == PerfMode.Off) return;
        Interlocked.Increment(ref _spanCount);
        var now = end.EndTimestampTicks > 0 ? end.EndTimestampTicks : System.Diagnostics.Stopwatch.GetTimestamp();
        var endUtc = DateTimeOffset.UtcNow;
        var tags = SanitizeTags(end.Tags);
        var record = new SpanRecord
        {
            TraceId = end.TraceId,
            SpanId = end.SpanId,
            ParentSpanId = end.ParentSpanId,
            Name = end.Name,
            StartTicks = end.TimestampTicks,
            EndTicks = now,
            EndUtc = endUtc,
            Status = end.Status switch
            {
                PerfSpanStatus.Error => "Error",
                PerfSpanStatus.Cancelled => "Cancelled",
                _ => "Ok",
            },
            Error = end.Error is null ? null : Redactor.Text(end.Error),
            Tags = tags,
            SessionId = _currentSessionId,
        };
        _ring.Add(record);
        _writer?.Enqueue(record);
        _aggregator.Add(record);
        foreach (var anomaly in _rules.Evaluate(record, _aggregator.Baseline(record.Name)))
            AddAnomaly(anomaly);
    }

    public void OnEvent(in PerfEvent value)
    {
        var detail = value.Detail is null ? null : Redactor.Text(value.Detail);
        var record = new PerfEventRecord(value.TimestampTicks, DateTimeOffset.UtcNow,
            value.Kind switch
            {
                PerfEventKind.UiStall => "ui.stall",
                PerfEventKind.UnhandledError => "error.unhandled",
                PerfEventKind.SessionEvent => "session",
                PerfEventKind.UiBatch => "ui.batch",
                _ => "counter",
            },
            value.Name, Math.Round(value.Value, 3), detail);
        lock (_recentEvents)
        {
            _recentEvents.Enqueue(record);
            while (_recentEvents.Count > MaxRecentEvents) _recentEvents.Dequeue();
        }
        _writer?.Enqueue(record);
        if (_rules.EvaluateEvent(value) is { } anomaly) AddAnomaly(anomaly);
    }

    public void Flush(TimeSpan timeout) => _writer?.Flush(timeout);

    public void Clear()
    {
        _ring.Clear();
        _aggregator.Clear();
        lock (_anomalies) _anomalies.Clear();
        lock (_recentEvents) _recentEvents.Clear();
        lock (_hostSamples) _hostSamples.Clear();
    }

    public void Dispose()
    {
        _writer?.Flush(TimeSpan.FromSeconds(3));
        _writer?.Dispose();
    }

    private List<KeyValuePair<string, string>> SanitizeTags(IReadOnlyList<KeyValuePair<string, string>>? tags)
    {
        if (tags is not { Count: > 0 }) return [];
        var captureCommand = _settings.CaptureCommandText;
        var result = new List<KeyValuePair<string, string>>(tags.Count);
        foreach (var (key, value) in tags)
        {
            if (key is "command" or "stdoutHead" or "stderrHead" && !captureCommand)
            {
                result.Add(new(key, "[已关闭命令文本记录]"));
                continue;
            }
            result.Add(new(key, Redactor.Text(value)));
        }
        return result;
    }

    private void AddAnomaly(Anomaly anomaly)
    {
        lock (_anomalies)
        {
            _anomalies.Add(anomaly);
            if (_anomalies.Count > MaxAnomalies) _anomalies.RemoveRange(0, _anomalies.Count - MaxAnomalies);
        }
    }
}
