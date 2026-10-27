using System;
using System.Collections.Generic;
using Fufucode.Core.Diagnostics.Perf;

namespace Fufucode.Core.Diagnostics;

/// <summary>
/// 只读数据快照。插件可能被多个管理器加载进不同的 AssemblyLoadContext，
/// 界面必须通过本契约读取真正的接收端，而不能假定同类型的本地记录器收到了数据。
/// </summary>
public sealed record PerfSpanData(
    long TraceId,
    long SpanId,
    long ParentSpanId,
    string Name,
    long StartTicks,
    long EndTicks,
    DateTimeOffset EndUtc,
    string Status,
    string? Error,
    IReadOnlyList<KeyValuePair<string, string>> Tags,
    string? SessionId);

public sealed record PerfEventData(
    long TimestampTicks,
    DateTimeOffset TimestampUtc,
    string Kind,
    string Name,
    double Value,
    string? Detail);

public interface IPerfDataSource
{
    long TotalSpansWritten { get; }
    int ActiveSpanCount { get; }
    PerfSessionContext? SessionContext { get; }
    IReadOnlyList<PerfSpanData> SnapshotSpans();
    IReadOnlyList<PerfSpanData> ActiveSpans();
    IReadOnlyList<PerfEventData> RecentEvents(int limit);
    IReadOnlyList<PerfHostSample> HostSamples(int limit);
}
