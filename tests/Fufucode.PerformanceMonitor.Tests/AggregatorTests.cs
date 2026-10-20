using Fufucode.Plugins.PerformanceMonitor;
using static Fufucode.PerformanceMonitor.Tests.TestUtil;

namespace Fufucode.PerformanceMonitor.Tests;

internal static class AggregatorTests
{
    public static void Run()
    {
        var aggregator = new PerfAggregator();
        for (var i = 1; i <= 100; i++) aggregator.Add(Record("tool", i));
        var stats = aggregator.Get("tool")!;
        Check(stats.Samples == 100, "aggregator counts samples");
        Check(Math.Abs(stats.P50 - 50) <= 1, "p50 is near the median");
        Check(stats.P95 >= 94 && stats.P95 <= 96, "p95 is near the 95th value");
        Check(stats.Max == 100, "max recorded");
        Check(stats.Min == 1, "min recorded");
        Console.WriteLine($"EWMA={stats.Ewma:0.###} Mean={stats.Mean:0.###}");
        // EWMA 对 1..100 的线性爬升滞后约 9 个样本（α=0.1），≈91。
        Check(stats.Ewma > 85 && stats.Ewma < 95, "EWMA tracks the end of the ramp");

        // 窗口外样本淘汰：容量 512，加入 5000 条后样本数封顶。
        for (var i = 0; i < 5000; i++) aggregator.Add(Record("many", 1));
        Check(aggregator.Get("many")!.Samples == PerfAggregator.WindowSize, "window capped at WindowSize");

        // 取消的 span 不进入统计。
        aggregator.Add(Record("cancelled", 10, status: "Cancelled"));
        Check(aggregator.Get("cancelled") is null, "cancelled spans excluded");

        Check(aggregator.Baseline("tool") is not null, "baseline available after enough samples");
        var fresh = new PerfAggregator();
        for (var i = 0; i < 10; i++) fresh.Add(Record("few", 5));
        Check(fresh.Baseline("few") is null, "baseline withheld before 20 samples");

        // 环形缓冲覆盖最旧记录。
        var ring = new SpanRing(3);
        ring.Add(Record("a", 1));
        ring.Add(Record("b", 2));
        ring.Add(Record("c", 3));
        ring.Add(Record("d", 4));
        var snapshot = ring.Snapshot();
        Check(snapshot.Count == 3 && snapshot[0].Name == "b" && snapshot[2].Name == "d", "ring overwrites oldest");
        Check(ring.Overwritten == 1 && ring.TotalWritten == 4, "ring counters");
        ring.Clear();
        Check(ring.Count == 0 && ring.TotalWritten == 0, "ring clear resets counters");
    }

    internal static SpanRecord Record(string name, double ms, string status = "Ok", params (string, string)[] tags)
    {
        var frequency = System.Diagnostics.Stopwatch.Frequency;
        return new SpanRecord
        {
            TraceId = 1,
            SpanId = Random.Shared.NextInt64(1, long.MaxValue),
            ParentSpanId = 0,
            Name = name,
            StartTicks = 1000,
            EndTicks = 1000 + (long)(ms / 1000d * frequency),
            EndUtc = DateTimeOffset.UtcNow,
            Status = status,
            Tags = tags.Select(t => new KeyValuePair<string, string>(t.Item1, t.Item2)).ToArray(),
        };
    }
}

internal static class AnomalyRuleTests
{
    public static void Run()
    {
        var rules = new AnomalyRules();
        Anomaly? Single(SpanRecord record, double? baseline = null) =>
            rules.Evaluate(record, baseline).FirstOrDefault();

        // 模型首字延迟。
        var slowTtft = AggregatorTests.Record("model.stream", 12_000, "Ok", ("ttftMs", "12000"), ("maxGapMs", "10"));
        Check(Single(slowTtft)?.Rule == "model.ttft.slow", "slow ttft detected");
        var fastTtft = AggregatorTests.Record("model.stream", 500, "Ok", ("ttftMs", "300"), ("maxGapMs", "20"));
        Check(Single(fastTtft) is null, "fast stream has no anomaly");

        // 流式卡顿：最大帧间隔。
        var stall = AggregatorTests.Record("model.stream", 4_000, "Ok", ("ttftMs", "100"), ("maxGapMs", "3500"));
        Check(rules.Evaluate(stall, null).Any(a => a.Rule == "model.stream.stall"), "stream stall detected");

        // 工具失败与慢工具。
        var toolError = AggregatorTests.Record("tool", 5, status: "Error", tags: ("tool", "read_file"));
        Check(rules.Evaluate(toolError, null).Any(a => a.Rule == "tool.error"), "tool error detected");
        var slowTool = AggregatorTests.Record("tool", 90_000, tags: ("tool", "run_command"));
        Check(rules.Evaluate(slowTool, null).Any(a => a.Rule == "tool.slow"), "slow tool detected");

        // 正常工具不触发相对规则（基线相近）。
        var normal = AggregatorTests.Record("tool", 300, tags: ("tool", "read_file"));
        Check(rules.Evaluate(normal, 280) is { Count: 0 }, "normal tool passes baseline");

        // 相对规则：超基线 3 倍且基线高于下限。
        var relative = AggregatorTests.Record("tool", 2_000, tags: ("tool", "read_file"));
        Check(rules.Evaluate(relative, 400).Any(a => a.Rule == "span.relative.slow"), "relative slow detected");
        // 基线低于下限时不触发相对规则。
        Check(rules.Evaluate(AggregatorTests.Record("tool", 300), 50).All(a => a.Rule != "span.relative.slow"),
            "relative rule ignores tiny baselines");

        // 相对规则不套用在回合级 span 上。
        Check(rules.Evaluate(AggregatorTests.Record("turn", 20_000), 1_000) is { Count: 0 },
            "relative rule skips turn spans");

        // 审批等待。
        Check(rules.Evaluate(AggregatorTests.Record("tool.approval", 45_000), null).Any(a => a.Rule == "tool.approval.wait"),
            "long approval wait detected");

        // UI 卡顿事件。
        var stallEvent = new Fufucode.Core.Diagnostics.PerfEvent(Fufucode.Core.Diagnostics.PerfEventKind.UiStall,
            "dispatcher.background", 350, System.Diagnostics.Stopwatch.GetTimestamp(), "blocked");
        Check(rules.EvaluateEvent(stallEvent)?.Rule == "ui.stall", "ui stall event detected");
        var tiny = new Fufucode.Core.Diagnostics.PerfEvent(Fufucode.Core.Diagnostics.PerfEventKind.UiStall,
            "dispatcher.background", 50, System.Diagnostics.Stopwatch.GetTimestamp(), "fast");
        Check(rules.EvaluateEvent(tiny) is null, "short dispatch not reported");

        // 突发限流：同一规则 30 秒内最多 5 次。
        var burstRules = new AnomalyRules();
        var count = Enumerable.Range(0, 20)
            .Sum(_ => burstRules.Evaluate(AggregatorTests.Record("tool", 90_000, tags: ("tool", "run_command")), null).Count);
        Check(count == 5, "burst limiter caps repeated anomalies");
    }
}
