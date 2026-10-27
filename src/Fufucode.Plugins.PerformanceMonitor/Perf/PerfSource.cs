using Fufucode.Core.Diagnostics;
using Fufucode.Core.Diagnostics.Perf;

namespace Fufucode.Plugins.PerformanceMonitor;

/// <summary>
/// 页面统一的数据来源：本实例持有接收端时直接读本地记录器；否则经 Core 契约读另一个
/// 加载上下文里的接收端。两条路径产出同一种 <see cref="SpanRecord"/>，上层无需区分。
/// </summary>
internal sealed class PerfSource
{
    private readonly PerfRecorder? _local;
    private readonly IPerfDataSource? _remote;

    public PerfSource(PerfRecorder local, IPerfDataSource? remote)
    {
        _local = local;
        _remote = remote;
    }

    private IPerfDataSource Data => _remote ?? _local!;

    public long TotalWritten => Data.TotalSpansWritten;

    public int ActiveSpanCount => Data.ActiveSpanCount;

    public PerfSessionContext? SessionContext => Data.SessionContext;

    public List<SpanRecord> SnapshotSpans() => Convert(Data.SnapshotSpans());

    public List<SpanRecord> ActiveSpans() => Convert(Data.ActiveSpans());

    public IReadOnlyList<PerfEventRecord> RecentEvents(int limit) =>
        Data.RecentEvents(limit).Select(item => new PerfEventRecord(
            item.TimestampTicks, item.TimestampUtc, item.Kind, item.Name, item.Value, item.Detail)).ToArray();

    public IReadOnlyList<PerfHostSample> HostSamples(int limit) => Data.HostSamples(limit);

    private static List<SpanRecord> Convert(IReadOnlyList<PerfSpanData> source)
    {
        var result = new List<SpanRecord>(source.Count);
        foreach (var item in source)
        {
            result.Add(new SpanRecord
            {
                TraceId = item.TraceId,
                SpanId = item.SpanId,
                ParentSpanId = item.ParentSpanId,
                Name = item.Name,
                StartTicks = item.StartTicks,
                EndTicks = item.EndTicks,
                EndUtc = item.EndUtc,
                Status = item.Status,
                Error = item.Error,
                Tags = item.Tags,
                SessionId = item.SessionId,
            });
        }
        return result;
    }
}
