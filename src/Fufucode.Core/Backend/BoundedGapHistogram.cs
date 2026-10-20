namespace Fufucode.Core.Backend;

/// <summary>
/// 流式帧间隔的有界直方图：只保留最近 <see cref="Capacity"/> 个间隔，用于 p95 统计。
/// 环形覆盖，不分配、不排序；容量之外的旧值自然淘汰。
/// </summary>
internal sealed class BoundedGapHistogram
{
    private const int Capacity = 512;
    private readonly long[] _values = new long[Capacity];
    private int _next;
    private int _count;

    public int Count => _count;

    /// <summary>上一帧的时间点（毫秒）；由调用方在每次读到帧后更新。</summary>
    public long LastMs { get; set; }

    public void Add(long gapMs)
    {
        _values[_next] = gapMs;
        _next = (_next + 1) % Capacity;
        if (_count < Capacity) _count++;
    }

    /// <summary>近似百分位；空直方图返回 0。复制后排序，容量固定所以成本有界。</summary>
    public long Percentile(int percentile)
    {
        if (_count == 0) return 0;
        var copy = new long[_count];
        System.Array.Copy(_values, copy, _count);
        System.Array.Sort(copy);
        var index = (int)System.Math.Round((percentile / 100d) * (_count - 1), System.MidpointRounding.AwayFromZero);
        return copy[System.Math.Clamp(index, 0, _count - 1)];
    }
}
