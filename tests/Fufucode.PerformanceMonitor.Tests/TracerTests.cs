using Fufucode.Core.Diagnostics;
using Fufucode.Core.Diagnostics.Perf;
using static Fufucode.PerformanceMonitor.Tests.TestUtil;

namespace Fufucode.PerformanceMonitor.Tests;

internal static class TracerTests
{
    public static void Run()
    {
        // 未启用：Step 返回默认句柄，Dispose 无副作用，不抛异常。
        Check(!PerfTracer.Enabled, "tracer disabled by default");
        var noop = PerfTracer.Step("noop", "k", "v");
        noop.Tag("x", 1);
        noop.Fail("ignored");
        noop.Dispose();
        Check(true, "no-op span is safe");

        // 基本父子关系：同步嵌套。
        var sink = WithSink(_ =>
        {
            using var parent = PerfTracer.Step("parent");
            Check(parent.TraceId != 0, "root span gets trace id");
            using var child = PerfTracer.Step("child", "tool", "read_file");
            Check(child.TraceId == parent.TraceId, "child shares trace id");
        });
        var parentEnd = sink.Single("parent");
        var childEnd = sink.Single("child");
        Check(parentEnd.ParentSpanId == 0, "root span has no parent");
        Check(childEnd.TraceId == parentEnd.TraceId, "trace id propagated to end");
        Check(Tag(childEnd, "tool") == "read_file", "start tag recorded");

        // 跨 await 传播。
        var asyncSink = WithSink(_ =>
        {
            using var outer = PerfTracer.Step("async-outer");
            ChildAsync().GetAwaiter().GetResult();
        });
        Check(asyncSink.Single("async-child").ParentSpanId == asyncSink.Single("async-outer").SpanId,
            "context flows across await");

        // Task.WhenAll：并行子任务共享父，且互不串父。
        var parallel = WithSink(_ =>
        {
            using var batch = PerfTracer.Step("parallel-batch");
            var tasks = Enumerable.Range(0, 8).Select(i => Task.Run(() =>
            {
                using var item = PerfTracer.Step("parallel-item", "i", PerfTracer.Invariant(i));
                Thread.Sleep(2);
            }));
            Task.WaitAll(tasks.ToArray());
        });
        var batchEnd = parallel.Single("parallel-batch");
        var items = parallel.Ends.Where(e => e.Name == "parallel-item").ToArray();
        Check(items.Length == 8, "all parallel spans recorded");
        Check(items.All(e => e.ParentSpanId == batchEnd.SpanId && e.TraceId == batchEnd.TraceId),
            "parallel spans keep batch as parent");
        Check(items.Select(e => Tag(e, "i")).Distinct().Count() == 8, "parallel spans keep their own tags");

        // 顶层 Task.Run 开启新的根 span。
        var detached = WithSink(_ => Task.Run(() =>
        {
            using var root = PerfTracer.Step("detached-root");
        }).Wait());
        Check(detached.Single("detached-root").ParentSpanId == 0, "new task without ambient context is a root");

        // tag 上限与截断（长值在前，避免被数量上限挡掉）。
        var tagged = WithSink(_ =>
        {
            using var span = PerfTracer.Step("tagged");
            span.Tag("long", new string('x', 1000));
            for (var i = 0; i < 40; i++) span.Tag("k" + i, "v");
        });
        var taggedEnd = tagged.Single("tagged");
        Check(taggedEnd.Tags!.Count == PerfTracer.MaxTagsPerSpan, "tag count capped");
        Check(Tag(taggedEnd, "long")?.Length == PerfTracer.MaxTagLength, "tag value truncated");

        // 错误与取消状态。
        var failed = WithSink(_ =>
        {
            using var span = PerfTracer.Step("failed");
            span.Fail("boom");
            span.Cancel();
        });
        Check(failed.Single("failed").Status == PerfSpanStatus.Error, "error wins over cancel");
        var cancelled = WithSink(_ =>
        {
            using var span = PerfTracer.Step("cancelled");
            span.Cancel();
        });
        Check(cancelled.Single("cancelled").Status == PerfSpanStatus.Cancelled, "cancel recorded");

        // 双重 Attach 被拒绝；Detach 只移除同一实例。
        var first = new RecordingSink();
        var second = new RecordingSink();
        PerfTracer.Attach(first);
        try
        {
            var threw = false;
            try { PerfTracer.Attach(second); }
            catch (InvalidOperationException) { threw = true; }
            Check(threw, "second attach rejected");
            Check(!PerfTracer.Detach(second), "detach with other sink is a no-op");
            Check(PerfTracer.Detach(first), "detach own sink");
            Check(!PerfTracer.Enabled, "disabled after detach");
        }
        finally
        {
            PerfTracer.Detach(first);
        }

        // 详细模式才有开始事件。
        var detailed = WithSink(_ =>
        {
            using var span = PerfTracer.Step("detailed-span");
        }, PerfMode.Detailed);
        Check(detailed.Starts.Count == 1, "detailed mode records span starts");
        var standard = WithSink(_ =>
        {
            using var span = PerfTracer.Step("standard-span");
        });
        Check(standard.Starts.IsEmpty, "standard mode skips span starts");

        // 已完成的 span 可直接投递真实起止时间（启动回放使用）。
        var emitted = WithSink(_ =>
        {
            var start = System.Diagnostics.Stopwatch.GetTimestamp();
            var end = start + System.Diagnostics.Stopwatch.Frequency / 10; // 100 ms
            PerfTracer.EmitCompleted(42, 43, 0, "replayed", start, end,
                tags: [new KeyValuePair<string, string>("detail", "startup")]);
        });
        var replayEnd = emitted.Single("replayed");
        Check(replayEnd.EndTimestampTicks > replayEnd.TimestampTicks, "emitted span keeps explicit end ticks");
        Check(Tag(replayEnd, "detail") == "startup", "emitted span keeps tags");

        // 事件与计数器。
        var events = WithSink(_ => PerfTracer.Record(PerfEventKind.Counter, "process.cpuPercent", 12.5, "d"));
        Check(events.Events.Single().Value == 12.5 && events.Events.Single().Name == "process.cpuPercent",
            "record event reaches sink");
    }

    private static async Task ChildAsync()
    {
        await Task.Yield();
        await Task.Delay(5);
        using var span = PerfTracer.Step("async-child");
    }
}
