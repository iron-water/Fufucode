using System;
using System.Collections.Generic;

namespace Fufucode.Core.Diagnostics;

/// <summary>
/// 性能埋点接收端。由可选插件（performance.monitor）提供；核心只定义契约，
/// <see cref="PerfTracer.Sink"/> 为空时所有埋点退化为一次 null 判断。
/// 实现必须非阻塞：Start/End 在调用线程上同步执行（模型流、工具、UI 线程都会调用）。
/// </summary>
public interface IPerfSink
{
    /// <summary>span 开始。只有当 <see cref="PerfMode.Detailed"/> 时才需要处理。</summary>
    void OnSpanStart(in PerfSpanStart start);

    /// <summary>span 结束；无论开始时的模式如何，只要开始时记录了就必须收到结束。</summary>
    void OnSpanEnd(in PerfSpanEnd end);

    /// <summary>游离事件 / 计数器（序列值、UI 卡顿、异常）。</summary>
    void OnEvent(in PerfEvent value);

    /// <summary>把缓冲数据刷到磁盘；超时后放弃剩余数据。</summary>
    void Flush(TimeSpan timeout);
}

public enum PerfMode
{
    Off = 0,
    Standard = 1,
    Detailed = 2,
}

/// <summary>核心跟踪器转发给 sink 的模式查询入口。</summary>
public interface IPerfModeProvider
{
    PerfMode Mode { get; }
}

/// <summary>span 起始快照。</summary>
public readonly struct PerfSpanStart
{
    public PerfSpanStart(long traceId, long spanId, long parentSpanId, string name, long timestampTicks)
    {
        TraceId = traceId;
        SpanId = spanId;
        ParentSpanId = parentSpanId;
        Name = name;
        TimestampTicks = timestampTicks;
    }

    public long TraceId { get; }
    public long SpanId { get; }
    /// <summary>0 表示根 span。</summary>
    public long ParentSpanId { get; }
    public string Name { get; }
    public long TimestampTicks { get; }
}

/// <summary>span 结束快照。</summary>
public readonly struct PerfSpanEnd
{
    public PerfSpanEnd(in PerfSpanStart start, PerfSpanStatus status, string? error,
        IReadOnlyList<KeyValuePair<string, string>>? tags, long endTimestampTicks = 0)
    {
        TraceId = start.TraceId;
        SpanId = start.SpanId;
        ParentSpanId = start.ParentSpanId;
        Name = start.Name;
        TimestampTicks = start.TimestampTicks;
        Status = status;
        Error = error;
        Tags = tags;
        EndTimestampTicks = endTimestampTicks;
    }

    /// <summary>结束时间戳（Stopwatch）；0 表示接收端按收到时刻计算。</summary>
    public long EndTimestampTicks { get; }

    public long TraceId { get; }
    public long SpanId { get; }
    public long ParentSpanId { get; }
    public string Name { get; }
    public long TimestampTicks { get; }
    public PerfSpanStatus Status { get; }
    /// <summary>脱敏后的错误类型/消息；成功时为 null。</summary>
    public string? Error { get; }
    public IReadOnlyList<KeyValuePair<string, string>>? Tags { get; }
}

public enum PerfSpanStatus
{
    Ok = 0,
    Error = 1,
    Cancelled = 2,
}

/// <summary>游离事件类别，供 sink 分桶与 UI 过滤。</summary>
public enum PerfEventKind
{
    Counter = 0,
    UiStall = 1,
    UnhandledError = 2,
    SessionEvent = 3,
    UiBatch = 4,
}

/// <summary>游离事件（不属于任何 span 的聚合值，例如每 2 秒的进程采样和每批 UI 延迟）。</summary>
public readonly struct PerfEvent
{
    public PerfEvent(PerfEventKind kind, string name, double value, long timestampTicks, string? detail = null)
    {
        Kind = kind;
        Name = name;
        Value = value;
        TimestampTicks = timestampTicks;
        Detail = detail;
    }

    public PerfEventKind Kind { get; }
    public string Name { get; }
    public double Value { get; }
    public long TimestampTicks { get; }
    public string? Detail { get; }
}
