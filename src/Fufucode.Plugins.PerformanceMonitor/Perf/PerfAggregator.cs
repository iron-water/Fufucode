namespace Fufucode.Plugins.PerformanceMonitor;

/// <summary>
/// 按 span 名称维护滑动窗口样本与 EWMA 基线。
/// 相对规则用基线判断“比平时慢多少”，绝对规则用固定阈值兜底；两者都不依赖具体机器。
/// </summary>
public sealed class PerfAggregator
{
    /// <summary>每个名称保留的最近样本数（滑动窗口）。</summary>
    public const int WindowSize = 512;

    private readonly object _gate = new();
    private readonly Dictionary<string, Series> _series = new(StringComparer.Ordinal);

    public void Add(SpanRecord record)
    {
        if (record.Status == "Cancelled") return;
        lock (_gate)
        {
            if (!_series.TryGetValue(record.Name, out var series))
                _series[record.Name] = series = new Series();
            series.Add(record.DurationMs);
        }
    }

    /// <summary>某个名称的统计；没有样本时返回 null。</summary>
    public Stats? Get(string name)
    {
        lock (_gate) return _series.TryGetValue(name, out var series) ? series.Snapshot(name) : null;
    }

    public IReadOnlyList<Stats> All()
    {
        lock (_gate)
        {
            var result = new List<Stats>(_series.Count);
            foreach (var (name, series) in _series)
                if (series.Snapshot(name) is { } stats) result.Add(stats);
            result.Sort((a, b) => b.P95.CompareTo(a.P95));
            return result;
        }
    }

    /// <summary>相对异常判断需要的基线（EWMA；样本不足时返回 null）。</summary>
    public double? Baseline(string name)
    {
        lock (_gate) return _series.TryGetValue(name, out var series) && series.Count >= 20 ? series.Ewma : null;
    }

    public void Clear()
    {
        lock (_gate) _series.Clear();
    }

    private sealed class Series
    {
        private readonly double[] _window = new double[WindowSize];
        private int _next;
        public int Count { get; private set; }
        public double Ewma { get; private set; } = double.NaN;

        public void Add(double ms)
        {
            _window[_next] = ms;
            _next = (_next + 1) % WindowSize;
            if (Count < WindowSize) Count++;
            Ewma = double.IsNaN(Ewma) ? ms : Ewma * 0.9 + ms * 0.1;
        }

        public Stats? Snapshot(string name)
        {
            if (Count == 0) return null;
            var copy = new double[Count];
            Array.Copy(_window, copy, Count);
            Array.Sort(copy);
            return new Stats(name, Count,
                Percentile(copy, 50), Percentile(copy, 95), Percentile(copy, 99), copy[^1], copy[0],
                copy.Average(), Ewma);
        }

        private static double Percentile(double[] sorted, int percentile)
        {
            var index = (int)Math.Round(percentile / 100d * (sorted.Length - 1), MidpointRounding.AwayFromZero);
            return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
        }
    }
}

public sealed record Stats(string Name, int Samples, double P50, double P95, double P99, double Max, double Min,
    double Mean, double Ewma);
