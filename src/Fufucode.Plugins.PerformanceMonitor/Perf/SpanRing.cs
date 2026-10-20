namespace Fufucode.Plugins.PerformanceMonitor;

/// <summary>
/// 内存中的一条 span 记录。所有字段在构造时固化，读线程（UI）无锁读取；
/// 运行中的 span 由调用方在结束时才加入环形缓冲。
/// </summary>
public sealed class SpanRecord
{
    public required long TraceId { get; init; }
    public required long SpanId { get; init; }
    public required long ParentSpanId { get; init; }
    public required string Name { get; init; }
    public required long StartTicks { get; init; }
    /// <summary>结束时间（<see cref="System.Diagnostics.Stopwatch"/> 时间戳）。</summary>
    public required long EndTicks { get; init; }
    /// <summary>结束时间对应的墙钟时刻，供展示与导出。</summary>
    public required DateTimeOffset EndUtc { get; init; }
    /// <summary>Ok / Error / Cancelled。</summary>
    public required string Status { get; init; }
    public string? Error { get; init; }
    /// <summary>已脱敏的 tag，按插入顺序。</summary>
    public required IReadOnlyList<KeyValuePair<string, string>> Tags { get; init; }
    /// <summary>会话 id（由宿主事件补充）；未知时为 null。</summary>
    public string? SessionId { get; set; }

    public double DurationMs => (EndTicks - StartTicks) * 1000d / System.Diagnostics.Stopwatch.Frequency;

    public string? Tag(string key)
    {
        foreach (var (k, v) in Tags) if (string.Equals(k, key, StringComparison.Ordinal)) return v;
        return null;
    }

    public long? LongTag(string key) =>
        long.TryParse(Tag(key), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value : null;

    /// <summary>分类：model / tool / ui / context / persist / subagent / mcp / error / turn / other。</summary>
    public string Category
    {
        get
        {
            if (Name.StartsWith("model", StringComparison.Ordinal)) return "model";
            if (Name.StartsWith("tool", StringComparison.Ordinal)) return "tool";
            if (Name.StartsWith("ui", StringComparison.Ordinal)) return "ui";
            if (Name.StartsWith("context", StringComparison.Ordinal)) return "context";
            if (Name.StartsWith("persist", StringComparison.Ordinal)) return "persist";
            if (Name.StartsWith("subagent", StringComparison.Ordinal)) return "subagent";
            if (Name.StartsWith("mcp", StringComparison.Ordinal)) return "mcp";
            if (Name.StartsWith("error", StringComparison.Ordinal)) return "error";
            if (Name.StartsWith("turn", StringComparison.Ordinal) || Name.StartsWith("agent", StringComparison.Ordinal)) return "turn";
            return "other";
        }
    }
}

/// <summary>
/// 有界环形缓冲：满了覆盖最旧记录（性能监控宁可丢历史样本，也不让内存无界增长）。
/// 写入方为任意后台线程，读取方为 UI；通过锁保证一致性，写入只做一次拷贝。
/// </summary>
public sealed class SpanRing
{
    private readonly object _gate = new();
    private readonly SpanRecord?[] _items;
    private int _next;
    private int _count;
    private long _totalWritten;
    private long _overwritten;

    public SpanRing(int capacity)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _items = new SpanRecord?[capacity];
    }

    public int Capacity => _items.Length;

    public int Count { get { lock (_gate) return _count; } }

    public long TotalWritten { get { lock (_gate) return _totalWritten; } }

    public long Overwritten { get { lock (_gate) return _overwritten; } }

    public void Add(SpanRecord record)
    {
        lock (_gate)
        {
            if (_count == _items.Length) _overwritten++;
            _items[_next] = record;
            _next = (_next + 1) % _items.Length;
            if (_count < _items.Length) _count++;
            _totalWritten++;
        }
    }

    /// <summary>按写入顺序快照（最旧 → 最新）；可选只取最近 n 条。</summary>
    public List<SpanRecord> Snapshot(int? limit = null)
    {
        lock (_gate)
        {
            var take = Math.Min(_count, limit ?? _count);
            var result = new List<SpanRecord>(take);
            var start = (_next - take + _items.Length) % _items.Length;
            for (var i = 0; i < take; i++)
                if (_items[(start + i) % _items.Length] is { } item) result.Add(item);
            return result;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            Array.Clear(_items);
            _next = 0;
            _count = 0;
            _overwritten = 0;
            _totalWritten = 0;
        }
    }
}
