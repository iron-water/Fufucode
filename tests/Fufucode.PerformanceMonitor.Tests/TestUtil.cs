using System.Collections.Concurrent;
using Fufucode.Core.Diagnostics;

namespace Fufucode.PerformanceMonitor.Tests;

/// <summary>记录所有 span 与事件的测试接收端。</summary>
internal sealed class RecordingSink : IPerfSink, IPerfModeProvider
{
    public ConcurrentQueue<PerfSpanEnd> Ends { get; } = new();
    public ConcurrentQueue<PerfSpanStart> Starts { get; } = new();
    public ConcurrentQueue<PerfEvent> Events { get; } = new();
    public PerfMode Mode { get; set; } = PerfMode.Standard;

    public void OnSpanStart(in PerfSpanStart start) => Starts.Enqueue(start);

    public void OnSpanEnd(in PerfSpanEnd end) => Ends.Enqueue(end);

    public void OnEvent(in PerfEvent value) => Events.Enqueue(value);

    public void Flush(TimeSpan timeout) { }

    public PerfSpanEnd Single(string name) => Ends.Single(end => end.Name == name);

    public string? Tag(PerfSpanEnd end, string key) =>
        end.Tags?.FirstOrDefault(pair => pair.Key == key).Value;
}

internal static class TestUtil
{
    /// <summary>断言：失败即抛异常，由 Program 记为该套件失败。</summary>
    public static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL " + name);
        Console.WriteLine("PASS " + name);
    }

    public static string? Tag(PerfSpanEnd end, string key) =>
        end.Tags?.FirstOrDefault(pair => pair.Key == key).Value;

    /// <summary>把记录还原成结束快照（测试里喂给接收端）。</summary>
    public static PerfSpanEnd ToEnd(Fufucode.Plugins.PerformanceMonitor.SpanRecord record) =>
        new(new PerfSpanStart(record.TraceId, record.SpanId, record.ParentSpanId, record.Name, record.StartTicks),
            record.Status switch
            {
                "Error" => PerfSpanStatus.Error,
                "Cancelled" => PerfSpanStatus.Cancelled,
                _ => PerfSpanStatus.Ok,
            },
            record.Status == "Error" ? "failed" : null, record.Tags);

    /// <summary>用唯一的测试接收端包裹一段代码；结束前确保已卸下。</summary>
    public static RecordingSink WithSink(Action<RecordingSink> body, PerfMode mode = PerfMode.Standard)
    {
        var sink = new RecordingSink { Mode = mode };
        PerfTracer.Attach(sink, sink);
        try { body(sink); }
        finally { PerfTracer.Detach(sink); }
        return sink;
    }
}
