using System.Diagnostics;
using Fufucode.Core.Diagnostics.Perf;
using Fufucode.Plugins.PerformanceMonitor;
using static Fufucode.PerformanceMonitor.Tests.TestUtil;

namespace Fufucode.PerformanceMonitor.Tests;

/// <summary>概览/分析共用的快照与 Span 树构建：层级、Token、阻塞合成节点、过滤。</summary>
internal static class SnapshotTests
{
    public static void Run()
    {
        var now = DateTimeOffset.UtcNow;
        var trace = 42L;
        var rootTicks = 1_000_000L;
        var frequency = Stopwatch.Frequency;

        SpanRecord Span(string name, long startMs, double durationMs, string status = "Ok",
            long parent = 0, long traceId = 0, params (string Key, string Value)[] tags)
        {
            var start = rootTicks + (long)(startMs / 1000d * frequency);
            return new SpanRecord
            {
                TraceId = traceId == 0 ? trace : traceId,
                SpanId = Random.Shared.NextInt64(1, long.MaxValue),
                ParentSpanId = parent,
                Name = name,
                StartTicks = start,
                EndTicks = start + (long)(durationMs / 1000d * frequency),
                EndUtc = now.AddMilliseconds(startMs + durationMs),
                Status = status,
                Tags = tags.Select(pair => new KeyValuePair<string, string>(pair.Key, pair.Value)).ToArray(),
            };
        }

        var turn = Span("turn", 0, 12_000, tags: [("model", "claude-sonnet-5-5"), ("outcome", "ok")]);
        var model = Span("model.request", 100, 3_200, parent: turn.SpanId, tags: [("tools", "8")]);
        var stream = Span("model.stream", 200, 2_600, parent: model.SpanId,
            tags: [("ttftMs", "640"), ("promptTokens", "1000"), ("completionTokens", "200"), ("cachedTokens", "700")]);
        var tool = Span("tool", 3_400, 1_200, parent: turn.SpanId, tags: [("tool", "run_command")]);
        var process = Span("tool.process", 3_450, 842, parent: tool.SpanId,
            tags: [("tool", "run_command"), ("command", "npm test"), ("exitCode", "0"), ("stdoutBytes", "12400")]);
        var approval = Span("tool.approval", 3_400, 400, parent: tool.SpanId, tags: [("tool", "run_command")]);
        var persist = Span("persist.history", 4_800, 180, parent: turn.SpanId);
        var startup = Span("startup", -10_000, 9_400, traceId: trace + 1, tags: [("milestones", "8")]);
        var startupChild = Span("process.main", -10_000, 3_200, parent: startup.SpanId, traceId: trace + 1);

        var events = new[]
        {
            new PerfEventRecord(rootTicks + 3000, now.AddMilliseconds(3000), "ui.stall", "dispatcher.background", 220, "blocked"),
        };

        var snapshot = PerfSnapshot.Build([turn, model, stream, tool, process, approval, persist, startup, startupChild],
            events, []);
        Check(snapshot.TurnCount == 1, "snapshot counts turns");
        Check(snapshot.Startup is not null && Math.Abs(snapshot.Startup.DurationMs - 9_400) < 1, "snapshot finds startup");
        Check(Math.Abs(snapshot.TotalModelMs - 3_200) < 1, "model duration is not double counted");
        Check(Math.Abs(snapshot.TotalToolMs - 1_200) < 1, "tool duration");
        Check(snapshot.ModelCalls == 1 && snapshot.ToolCalls == 1, "model/tool counts");
        Check(snapshot.TotalTokens == 1_200, "prompt + completion tokens");
        Check(snapshot.CachedTokens == 700, "cached tokens");
        Check(Math.Abs(snapshot.CacheHitRate - 0.7) < 0.001, "cache hit rate");
        Check(snapshot.StallCount == 1, "stall count");
        Check(snapshot.Tools.Count == 1 && snapshot.Tools[0].Name == "Bash", "tool display name");
        Check(snapshot.Tools[0].SampleCommand == "npm test", "tool sample command");
        Check(snapshot.Context.PromptTokens == 1000 && snapshot.Context.CachedTokens == 700, "context uses latest usage");

        // 上下文分解（Treemap / 内容列表）
        var bd = snapshot.ContextBreakdown;
        Check(bd.TotalTokens == 1000, "context breakdown total tokens matches prompt");
        Check(bd.CachedTokens == 700, "context breakdown cached tokens");
        Check(bd.Categories.Count == 1 && bd.Categories[0].Key == "unknown",
            "usage without category evidence remains unclassified");
        Check(bd.AllItems.Sum(i => i.Tokens) == 1000, "unclassified amount preserves reported usage");

        var empty = PerfSnapshot.Build([], [], []);
        Check(empty.ContextBreakdown.TotalTokens == 0 && empty.ContextBreakdown.AllItems.Count == 0,
            "empty monitor invents no categories or token amounts");
        Check(empty.ContextBreakdown.ModelName == "模型未知", "unknown model has neutral label");
        var build = Span("context.snapshot", 50, 10, parent: turn.SpanId,
            tags: [("estimatedPromptTokens", "1800"), ("systemTokens", "500"), ("chatTokens", "1000"), ("toolTokens", "300")]);
        var estimated = PerfSnapshot.Build([turn, build, model, stream], [], []);
        Check(estimated.TotalTokens == 1200, "context estimate does not inflate reported token usage");
        Check(estimated.ContextBreakdown.TotalTokens == 1000 && estimated.ContextBreakdown.EstimatedTokens == 1800,
            "measured prompt and category estimates stay distinct");
        Check(estimated.ContextBreakdown.AllItems.Sum(item => item.Tokens) == 1800,
            "classification is not rescaled to fabricate precise values");
        var duplicate = Span("model.request", 100, 3200, parent: turn.SpanId,
            tags: [("promptTokens", "1000"), ("completionTokens", "200")]);
        var duplicateChild = Span("model.stream", 200, 2600, parent: duplicate.SpanId,
            tags: [("promptTokens", "1000"), ("completionTokens", "200")]);
        var repeated = PerfSnapshot.Build([turn, duplicate, duplicateChild, build], [], []);
        Check(repeated.TotalTokens == 1200, "ancestor usage cannot double count its child");
        var active = Span("turn", 0, 5000, status: "Running");
        var running = PerfSnapshot.Build([active, Span("context.snapshot", 10, 1, parent: active.SpanId,
            tags: [("systemTokens", "8"), ("chatTokens", "12")])], [], []);
        Check(running.LatestTurn?.Outcome == "running" && running.ContextBreakdown.TotalTokens == 20,
            "running turn uses actual request text estimate before first response");

        var turnInfo = snapshot.Turns[0];
        Check(turnInfo.EventSpans.Count == 1 && turnInfo.EventSpans[0].Name == "ui.stall", "stall becomes a tree node");
        Check(turnInfo.Spans.Any(span => span.Name == "ui.stall"), "stall included in turn spans");

        var roots = PerfTreeBuilder.Build(turnInfo, null);
        Check(roots.Count == 1, "one tree root");
        Check(roots[0].DisplayName.StartsWith("轮次", StringComparison.Ordinal), "turn root label");
        var flat = Flatten(roots[0]).ToArray();
        Check(flat.Any(node => node.Span.Name == "tool.process"), "tool process visible in tree");
        Check(flat.Any(node => node.Span.Name == "ui.stall"), "stall visible in tree");
        Check(flat.Any(node => node.Span.Name == "model.stream"), "expanded model stream visible");

        var filtered = PerfTreeBuilder.Build(turnInfo, span =>
            span.Name.Contains("process", StringComparison.OrdinalIgnoreCase) ||
            (span.Tag("command")?.Contains("npm test", StringComparison.OrdinalIgnoreCase) ?? false));
        var filteredFlat = Flatten(filtered[0]).ToArray();
        Check(filteredFlat.Any(node => node.Span.Name == "tool.process"), "filter keeps command match");
        Check(filteredFlat.All(node => node.Span.Name is "turn" or "tool" or "tool.process"),
            "filter keeps only match plus ancestors");
    }

    private static IEnumerable<PerfSpanNode> Flatten(PerfSpanNode node)
    {
        yield return node;
        foreach (var child in node.Children)
            foreach (var descendant in Flatten(child))
                yield return descendant;
    }
}
