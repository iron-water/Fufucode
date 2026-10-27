using Fufucode.Core.Diagnostics;
using Fufucode.Core.Diagnostics.Perf;
using Fufucode.PerformanceMonitor.Tests;

var only = args.FirstOrDefault(arg => arg.StartsWith("--", StringComparison.Ordinal));

var tests = new (string Id, Action Run)[]
{
    ("tracer", TracerTests.Run),
    ("aggregator", AggregatorTests.Run),
    ("rules", AnomalyRuleTests.Run),
    ("redactor", RedactorTests.Run),
    ("jsonl", JsonlTests.Run),
    ("recorder", RecorderTests.Run),
    ("plugin", PluginTests.Run),
    ("snapshot", SnapshotTests.Run),
    ("host", HostCollectorTests.Run),
    ("native", NativePipelineTests.Run),
};

var failures = 0;
foreach (var (id, run) in tests)
{
    if (only is not null && only != "--" + id) continue;
    try
    {
        run();
        Console.WriteLine($"DONE {id}");
    }
    catch (Exception error)
    {
        failures++;
        Console.WriteLine($"FAILED {id}: {error}");
    }
}
Console.WriteLine(failures == 0 ? "ALL PASSED" : $"{failures} SUITE(S) FAILED");
return failures == 0 ? 0 : 1;

namespace Fufucode.PerformanceMonitor.Tests
{
    // 各测试套件按需引用这些类型，保持 using 集中。
    internal static class TestImports
    {
        public static Type[] Marker => [typeof(PerfTracer), typeof(PerfMode)];
    }
}
