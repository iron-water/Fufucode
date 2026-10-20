namespace Fufucode.Plugins.PerformanceMonitor;

public enum AnomalyKind
{
    /// <summary>固定阈值（超时、卡顿、失败）。</summary>
    Absolute,
    /// <summary>明显慢于该名称自身的 EWMA 基线。</summary>
    Relative,
}

public sealed record Anomaly(
    long TimestampTicks,
    string Rule,
    string SpanName,
    double ValueMs,
    double? BaselineMs,
    AnomalyKind Kind,
    string Detail,
    long TraceId,
    long SpanId,
    DateTimeOffset TimestampUtc);

/// <summary>
/// 异常检测：两层规则。
/// 1. 绝对规则：固定阈值（模型首字、流式帧间隔、命令超时、工具失败、UI 卡顿），不依赖历史；
/// 2. 相对规则：同一名称的 p95/单次耗时超过 EWMA 基线的倍数且样本足够，用来发现“这台机器上慢在哪”。
/// </summary>
public sealed class AnomalyRules
{
    private const int BurstWindowMs = 30_000;
    /// <summary>同一规则 + 同一 span 名在 30 秒内最多报告 5 次，避免刷屏。</summary>
    private const int BurstLimit = 5;

    private readonly object _gate = new();
    private readonly Dictionary<string, Queue<long>> _bursts = new(StringComparer.Ordinal);

    // 绝对阈值（毫秒）。
    public double ModelHttpMs { get; set; } = 30_000;
    public double ModelTtftMs { get; set; } = 10_000;
    public double StreamMaxGapMs { get; set; } = 2_000;
    public double StreamP95GapMs { get; set; } = 500;
    public double ToolMs { get; set; } = 60_000;
    public double ApprovalMs { get; set; } = 30_000;
    public double PersistMs { get; set; } = 2_000;
    public double UiBatchMs { get; set; } = 100;
    public double UiStallMs { get; set; } = 100;
    public double CompactMs { get; set; } = 30_000;
    // 相对阈值：单次耗时 / EWMA 基线。
    public double RelativeFactor { get; set; } = 3;
    public double RelativeFloorMs { get; set; } = 200;
    public int RelativeMinSamples { get; set; } = 20;

    /// <summary>对结束的 span 跑规则；返回命中的异常（可能为空）。</summary>
    public IReadOnlyList<Anomaly> Evaluate(SpanRecord record, double? baseline)
    {
        var result = new List<Anomaly>(2);
        var ms = record.DurationMs;
        void Hit(string rule, AnomalyKind kind, double value, double limit, string detail, double? baseMs = null)
        {
            if (!Allow($"{rule}|{record.Name}")) return;
            result.Add(new Anomaly(record.EndTicks, rule, record.Name, value, baseMs ?? limit, kind, detail,
                record.TraceId, record.SpanId, record.EndUtc));
        }

        switch (record.Name)
        {
            case "model.http":
                if (ms > ModelHttpMs) Hit("model.http.slow", AnomalyKind.Absolute, ms, ModelHttpMs, $"模型请求等待响应头 {ms:0} ms");
                break;
            case "model.stream":
                if (record.LongTag("ttftMs") is { } ttft && ttft > ModelTtftMs)
                    Hit("model.ttft.slow", AnomalyKind.Absolute, ttft, ModelTtftMs, $"首字延迟 {ttft} ms");
                if (record.LongTag("maxGapMs") is { } maxGap && maxGap > StreamMaxGapMs)
                    Hit("model.stream.stall", AnomalyKind.Absolute, maxGap, StreamMaxGapMs, $"流式最大帧间隔 {maxGap} ms");
                if (record.LongTag("p95GapMs") is { } p95Gap && p95Gap > StreamP95GapMs)
                    Hit("model.stream.jitter", AnomalyKind.Absolute, p95Gap, StreamP95GapMs, $"流式帧间隔 p95 {p95Gap} ms");
                break;
            case "tool.approval":
                if (ms > ApprovalMs) Hit("tool.approval.wait", AnomalyKind.Absolute, ms, ApprovalMs, $"审批等待 {ms:0} ms");
                break;
            case "persist.history":
                if (ms > PersistMs) Hit("persist.slow", AnomalyKind.Absolute, ms, PersistMs, $"会话持久化 {ms:0} ms");
                break;
            case "context.compact":
                if (ms > CompactMs) Hit("context.compact.slow", AnomalyKind.Absolute, ms, CompactMs, $"上下文压缩 {ms:0} ms");
                break;
            case "ui.batch":
                if (ms > UiBatchMs) Hit("ui.batch.slow", AnomalyKind.Absolute, ms, UiBatchMs, $"UI 批处理 {ms:0} ms");
                break;
        }

        if (record.Name.StartsWith("tool", StringComparison.Ordinal))
        {
            if (record.Status == "Error" /* cancelled 已在上游跳过 */)
                Hit("tool.error", AnomalyKind.Absolute, ms, 0, $"工具失败：{record.Error ?? "unknown"}");
            else if (record.Name == "tool" && ms > ToolMs)
                Hit("tool.slow", AnomalyKind.Absolute, ms, ToolMs, $"工具 {record.Tag("tool")} 执行 {ms:0} ms");
        }

        if (baseline is { } baseMs && baseMs > RelativeFloorMs && ms > baseMs * RelativeFactor &&
            record.Name is not ("turn" or "agent.iteration"))
            Hit("span.relative.slow", AnomalyKind.Relative, ms, 0, $"{ms:0} ms，基线 {baseMs:0} ms（{(ms / baseMs):0.0}×）", baseMs);

        return result;
    }

    /// <summary>游离事件（UI 卡顿、未处理异常）的规则。</summary>
    public Anomaly? EvaluateEvent(in Fufucode.Core.Diagnostics.PerfEvent value)
    {
        switch (value.Kind)
        {
            case Fufucode.Core.Diagnostics.PerfEventKind.UiStall:
                if (value.Value <= UiStallMs || !Allow($"ui.stall|{value.Name}")) return null;
                return new Anomaly(value.TimestampTicks, "ui.stall", value.Name, value.Value, UiStallMs,
                    AnomalyKind.Absolute, value.Detail ?? "界面线程长时间无响应", 0, 0, DateTimeOffset.UtcNow);
            case Fufucode.Core.Diagnostics.PerfEventKind.UnhandledError:
                if (!Allow($"error.unhandled|{value.Name}")) return null;
                return new Anomaly(value.TimestampTicks, "error.unhandled", value.Name, 0, null,
                    AnomalyKind.Absolute, value.Detail ?? "未处理异常", 0, 0, DateTimeOffset.UtcNow);
            default:
                return null;
        }
    }

    private bool Allow(string key)
    {
        var now = Environment.TickCount64;
        lock (_gate)
        {
            if (!_bursts.TryGetValue(key, out var queue)) _bursts[key] = queue = new Queue<long>();
            while (queue.Count > 0 && now - queue.Peek() > BurstWindowMs) queue.Dequeue();
            if (queue.Count >= BurstLimit) return false;
            queue.Enqueue(now);
            return true;
        }
    }
}
