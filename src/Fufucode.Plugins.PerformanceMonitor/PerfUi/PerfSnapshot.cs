using System.Diagnostics;
using Avalonia.Media;
using Fufucode.Core.Diagnostics;
using Fufucode.Core.Diagnostics.Perf;

namespace Fufucode.Plugins.PerformanceMonitor;

/// <summary>一次刷新用到的只读数据快照。构建在后台线程，UI 只读取不可变列表。</summary>
internal sealed class PerfSnapshot
{
    public IReadOnlyList<SpanRecord> AllSpans { get; init; } = [];
    public IReadOnlyList<PerfEventRecord> Events { get; init; } = [];
    public IReadOnlyList<PerfHostSample> HostSamples { get; init; } = [];
    public IReadOnlyList<PerfTurnInfo> Turns { get; init; } = [];
    public PerfTurnInfo? Startup { get; init; }
    public IReadOnlyList<PerfToolUsage> Tools { get; init; } = [];
    public PerfContextInfo Context { get; init; } = new();
    public PerfContextBreakdown ContextBreakdown { get; init; } = new();
    public string SessionLabel { get; init; } = "当前会话";

    public long TotalModelMs { get; init; }
    public long TotalToolMs { get; init; }
    public long TotalApprovalMs { get; init; }
    public int ModelCalls { get; init; }
    public int ToolCalls { get; init; }
    public int ErrorCount { get; init; }
    public int StallCount { get; init; }
    public int TurnCount { get; init; }
    public TimeSpan WallDuration { get; init; }
    public long TotalTokens { get; init; }
    public long CachedTokens { get; init; }
    public double CacheHitRate { get; init; }
    public double AverageTurnMs { get; init; }

    public PerfTurnInfo? LatestTurn => Turns.Count > 0 ? Turns[^1] : null;
    public PerfTurnInfo? LatestSession => LatestTurn ?? Startup;

    public PerfTurnInfo? FindTurn(long traceId)
    {
        foreach (var turn in Turns) if (turn.TraceId == traceId) return turn;
        return Startup?.TraceId == traceId ? Startup : null;
    }

    public static PerfSnapshot Build(IReadOnlyList<SpanRecord> spans,
        IReadOnlyList<PerfEventRecord> events, IReadOnlyList<PerfHostSample> samples,
        PerfSessionContext? session = null)
    {
        var startupTraces = spans.Where(span => span.Name == "startup").Select(span => span.TraceId).ToHashSet();
        // 只有整体上确实有人在写会话标识时才按会话过滤；否则说明标识链路尚未接通，
        // 此时显示未标记数据好过把整页过滤成空。不同会话的数据仍然不会混在一起。
        if (session?.SessionId is { Length: > 0 } sessionId &&
            spans.Any(span => !string.IsNullOrEmpty(span.SessionId)))
        {
            spans = spans.Where(span => string.Equals(span.SessionId, sessionId, StringComparison.Ordinal) ||
                                        startupTraces.Contains(span.TraceId)).ToArray();
        }
        var usageSpans = UsageSpans(spans);
        var turns = new List<PerfTurnInfo>();
        PerfTurnInfo? startup = null;
        var syntheticId = long.MinValue + 1;

        foreach (var group in spans.GroupBy(span => span.TraceId))
        {
            var ordered = group.OrderBy(span => span.StartTicks).ThenBy(span => span.SpanId).ToList();
            if (ordered.Count == 0) continue;

            var isStartup = ordered.Any(span => span.Name == "startup" ||
                span.Name.StartsWith("startup.", StringComparison.Ordinal));
            var root = ordered.FirstOrDefault(span => span.Name == "startup")
                ?? ordered.FirstOrDefault(span => span.Name == "turn" && span.ParentSpanId == 0)
                ?? ordered.FirstOrDefault(span => span.Name == "turn")
                ?? ordered.FirstOrDefault(span => span.ParentSpanId == 0)
                ?? ordered[0];

            var firstTicks = ordered.Min(span => span.StartTicks);
            var lastTicks = ordered.Max(span => span.EndTicks);
            var last = ordered.MaxBy(span => span.EndTicks)!;
            var endUtc = last.EndUtc;
            var durationMs = Math.Max(0, (lastTicks - firstTicks) * 1000d / Stopwatch.Frequency);
            var startUtc = endUtc - TimeSpan.FromMilliseconds(durationMs);
            var turnEvents = events
                .Where(item => item.TimestampUtc >= startUtc.AddMilliseconds(-50) &&
                               item.TimestampUtc <= endUtc.AddMilliseconds(50))
                .ToArray();

            // 把阻塞/批事件补成树里的节点，保证“每一次阻塞”都出现在 Span 树中。
            var eventSpans = new List<SpanRecord>();
            foreach (var item in turnEvents)
            {
                // 只把真正的阻塞与异常补成树节点；ui.batch 是聚合计数，放进树里只会制造噪声。
                var name = item.Kind switch
                {
                    "ui.stall" => "ui.stall",
                    "error.unhandled" => "error.unhandled",
                    _ => null,
                };
                if (name is null) continue;
                var duration = name == "error.unhandled" ? 0 : Math.Max(0, item.Value);
                var endTicks = item.TimestampTicks;
                var startTicks = endTicks - (long)(duration * Stopwatch.Frequency / 1000d);
                eventSpans.Add(new SpanRecord
                {
                    TraceId = group.Key,
                    SpanId = syntheticId++,
                    ParentSpanId = root.SpanId,
                    Name = name,
                    StartTicks = startTicks,
                    EndTicks = Math.Max(startTicks + 1, endTicks),
                    EndUtc = item.TimestampUtc,
                    Status = name == "error.unhandled" ? "Error" : "Ok",
                    Error = name == "error.unhandled" ? item.Detail : null,
                    Tags =
                    [
                        new KeyValuePair<string, string>("event", item.Name),
                        new KeyValuePair<string, string>("detail", item.Detail ?? string.Empty),
                    ],
                });
            }

            var all = ordered.Concat(eventSpans).OrderBy(span => span.StartTicks).ThenBy(span => span.SpanId).ToList();
            var modelSpans = all.Where(span => span.Name == "model.request").ToList();
            if (modelSpans.Count == 0) modelSpans = all.Where(span => span.Name == "model.stream").ToList();
            if (modelSpans.Count == 0) modelSpans = all.Where(span => span.Name == "model.http").ToList();
            var toolSpans = all.Where(span => span.Name == "tool").ToList();
            if (toolSpans.Count == 0) toolSpans = all.Where(span => span.Name == "tool.process").ToList();
            var approvalSpans = all.Where(span => span.Name == "tool.approval").ToList();
            var persistSpans = all.Where(span => span.Name.StartsWith("persist.", StringComparison.Ordinal) ||
                                                 span.Name.StartsWith("context.", StringComparison.Ordinal)).ToList();
            var uiSpans = all.Where(span => span.Name.StartsWith("ui.", StringComparison.Ordinal) &&
                                            span.Name is not ("ui.stall" or "ui.batch")).ToList();

            var prompt = 0L;
            var completion = 0L;
            var cached = 0L;
            foreach (var span in usageSpans.Where(span => span.TraceId == group.Key))
            {
                prompt += span.LongTag("promptTokens") ?? 0;
                completion += span.LongTag("completionTokens") ?? 0;
                cached += span.LongTag("cachedTokens") ?? 0;
            }

            var significant = modelSpans.Concat(toolSpans).Concat(approvalSpans)
                .OrderBy(span => span.StartTicks).Take(8).ToArray();

            var isTurn = root.Name == "turn";
            var info = new PerfTurnInfo
            {
                TraceId = group.Key,
                Root = root,
                IsStartup = isStartup,
                StartUtc = startUtc,
                DurationMs = durationMs,
                Spans = all,
                ModelSpans = modelSpans,
                ToolSpans = toolSpans,
                ApprovalSpans = approvalSpans,
                PersistSpans = persistSpans,
                UiSpans = uiSpans,
                EventSpans = eventSpans,
                Events = turnEvents,
                SignificantChildren = significant,
                ModelCalls = modelSpans.Count,
                ToolCalls = toolSpans.Count,
                Errors = all.Count(span => span.Status == "Error"),
                ModelMs = modelSpans.Sum(span => span.DurationMs),
                ToolMs = toolSpans.Sum(span => span.DurationMs),
                ApprovalMs = approvalSpans.Sum(span => span.DurationMs),
                PersistMs = persistSpans.Sum(span => span.DurationMs),
                PromptTokens = prompt,
                CompletionTokens = completion,
                CachedTokens = cached,
                Outcome = root.Status == "Running" || !isTurn ? "running" :
                    root.Tag("outcome") ?? (root.Status == "Error" ? "error" : "ok"),
                Title = isStartup ? "启动" : (root.Tag("model") ?? (isTurn ? "回合" : "进行中回合")),
                TimeLabel = startUtc.ToLocalTime().ToString("HH:mm:ss"),
            };

            if (isStartup)
            {
                if (startup is null || info.StartUtc > startup.StartUtc) startup = info;
            }
            else if (ordered.Any(span => span.Name == "turn" || span.Name.StartsWith("model.", StringComparison.Ordinal)
                                                   || span.Name.StartsWith("tool", StringComparison.Ordinal)))
            {
                turns.Add(info);
            }
        }

        turns.Sort((a, b) => a.StartUtc.CompareTo(b.StartUtc));
        foreach (var turn in turns)
        {
            // 概览时间线只保留每轮最长的几个模型/工具节点，避免一个回合铺满整页。
            turn.SignificantChildren = turn.ModelSpans.Concat(turn.ToolSpans).Concat(turn.ApprovalSpans)
                .OrderBy(span => span.StartTicks).Take(8).ToArray();
        }

        var allSpans = spans;
        var latestUsage = usageSpans
            .Where(span => span.LongTag("promptTokens") is >= 0)
            .OrderBy(span => span.EndTicks)
            .LastOrDefault();
        var context = latestUsage is null
            ? new PerfContextInfo
            {
                PromptTokens = session?.PromptTokens ?? 0,
                CompletionTokens = session?.CompletionTokens ?? 0,
                CachedTokens = session?.CachedTokens ?? 0,
            }
            : new PerfContextInfo
            {
                PromptTokens = latestUsage.LongTag("promptTokens") ?? 0,
                CachedTokens = latestUsage.LongTag("cachedTokens") ?? 0,
                CompletionTokens = latestUsage.LongTag("completionTokens") ?? 0,
            };

        var tools = spans.Where(span => span.Name == "tool")
            .GroupBy(span => span.Tag("tool") ?? "unknown")
            .Select(group =>
            {
                var items = group.ToList();
                var command = spans.FirstOrDefault(span => span.Name == "tool.process" &&
                    string.Equals(span.Tag("tool"), group.Key, StringComparison.Ordinal) &&
                    !string.IsNullOrWhiteSpace(span.Tag("command")))?.Tag("command");
                return new PerfToolUsage
                {
                    RawName = group.Key,
                    Name = PerfPalette.ToolDisplayName(group.Key),
                    Count = items.Count,
                    TotalMs = items.Sum(span => span.DurationMs),
                    MaxMs = items.Max(span => span.DurationMs),
                    Errors = items.Count(span => span.Status == "Error"),
                    SampleCommand = command,
                };
            })
            .OrderByDescending(item => item.Count)
            .ThenByDescending(item => item.TotalMs)
            .ToList();

        var promptTotal = 0L;
        var completionTotal = 0L;
        var cachedTotal = 0L;
        foreach (var span in usageSpans)
        {
            promptTotal += span.LongTag("promptTokens") ?? 0;
            completionTotal += span.LongTag("completionTokens") ?? 0;
            cachedTotal += span.LongTag("cachedTokens") ?? 0;
        }

        var start = turns.Count > 0 ? turns.Min(turn => turn.StartUtc) : DateTimeOffset.MinValue;
        if (startup is not null && (start == DateTimeOffset.MinValue || startup.StartUtc < start)) start = startup.StartUtc;
        var end = spans.Count > 0 ? spans.Max(span => span.EndUtc) : start;
        if (start == DateTimeOffset.MinValue) start = end;

        var breakdown = BuildBreakdown(spans, turns, context, session, latestUsage);

        return new PerfSnapshot
        {
            AllSpans = allSpans,
            Events = events,
            HostSamples = samples,
            Turns = turns,
            Startup = startup,
            Tools = tools,
            Context = context,
            ContextBreakdown = breakdown,
            SessionLabel = session?.SessionName ?? session?.SessionId ?? "当前会话",
            TotalModelMs = (long)turns.Sum(turn => turn.ModelMs),
            TotalToolMs = (long)turns.Sum(turn => turn.ToolMs),
            TotalApprovalMs = (long)turns.Sum(turn => turn.ApprovalMs),
            ModelCalls = turns.Sum(turn => turn.ModelCalls),
            ToolCalls = turns.Sum(turn => turn.ToolCalls),
            ErrorCount = turns.Sum(turn => turn.Errors) + (startup?.Errors ?? 0),
            StallCount = events.Count(item => item.Kind == "ui.stall"),
            TurnCount = turns.Count,
            WallDuration = end > start ? end - start : TimeSpan.Zero,
            TotalTokens = promptTotal + completionTotal,
            CachedTokens = cachedTotal,
            CacheHitRate = promptTotal > 0 ? Math.Clamp(cachedTotal / (double)promptTotal, 0, 1) : 0,
            AverageTurnMs = turns.Count > 0 ? turns.Average(turn => turn.DurationMs) : 0,
        };
    }

    private static IReadOnlyList<SpanRecord> UsageSpans(IReadOnlyList<SpanRecord> spans)
    {
        // 环形缓冲与实时视图理论上不会重复，但同 id 仍以最后一条为准，避免构建快照时抛异常。
        var byId = spans.GroupBy(span => span.SpanId).ToDictionary(group => group.Key, group => group.Last());
        var candidates = spans.Where(span => span.Name.StartsWith("model.", StringComparison.Ordinal) &&
            (span.LongTag("promptTokens") is not null || span.LongTag("completionTokens") is not null)).ToArray();
        var covered = new HashSet<long>();
        foreach (var span in candidates)
        {
            var parent = span.ParentSpanId;
            var seen = new HashSet<long>();
            while (parent != 0 && seen.Add(parent) && byId.TryGetValue(parent, out var ancestor))
            {
                covered.Add(parent);
                parent = ancestor.ParentSpanId;
            }
        }
        return candidates.Where(span => !covered.Contains(span.SpanId)).ToArray();
    }

    private static PerfContextBreakdown BuildBreakdown(
        IReadOnlyList<SpanRecord> spans,
        IReadOnlyList<PerfTurnInfo> turns,
        PerfContextInfo context,
        PerfSessionContext? session,
        SpanRecord? latestUsage)
    {
        var modelName = session?.Model
            ?? turns.LastOrDefault()?.Root?.Tag("model")
            ?? spans.LastOrDefault(span => !string.IsNullOrWhiteSpace(span.Tag("model")))?.Tag("model")
            ?? "模型未知";
        var estimate = spans.Where(span => span.Name == "context.snapshot").MaxBy(span => span.StartTicks);
        var parts = estimate is null ? session?.Parts ?? [] : new PerfContextPart[]
        {
            new("system", "系统提示词", estimate.LongTag("systemTokens") ?? 0),
            new("tools", "工具定义", estimate.LongTag("toolTokens") ?? 0),
            new("chat", "会话文本", estimate.LongTag("chatTokens") ?? 0),
            new("results", "工具结果", estimate.LongTag("resultTokens") ?? 0),
        };
        var capacity = spans.Where(span => span.Name == "context.window")
            .MaxBy(span => span.StartTicks)?.LongTag("tokens");
        var estimatedTotal = parts.Sum(part => part.Tokens);
        var awaitingUsage = estimate is not null && (latestUsage is null || estimate.StartTicks > latestUsage.StartTicks);
        var measured = !awaitingUsage && context.PromptTokens > 0 ? context.PromptTokens : 0;
        var total = measured > 0 ? measured : estimatedTotal;
        // 分类只有文本估算依据。缺少类别数据的精确 usage 保持“未分类”，不按比例捏造。
        var items = new List<PerfContextItem>();
        if (estimatedTotal > 0)
        {
            foreach (var part in parts.Where(part => part.Tokens > 0))
            {
                var color = part.Key switch
                {
                    "system" => PerfPalette.CtxSystemPrompt,
                    "tools" => PerfPalette.CtxTools,
                    "results" => PerfPalette.CtxToolResults,
                    _ => PerfPalette.CtxConversation,
                };
                items.Add(new PerfContextItem
                {
                    Name = part.Name, CategoryKey = part.Key, CategoryName = part.Name,
                    Tokens = part.Tokens, Tag = "文本估算", Color = color,
                    Percent = part.Tokens / (double)estimatedTotal,
                });
            }
        }
        else if (total > 0)
        {
            items.Add(new PerfContextItem
            {
                Name = "未分类上下文", CategoryKey = "unknown", CategoryName = "未分类",
                Tokens = total, Percent = 1, Tag = "模型用量", Color = PerfPalette.CtxSkills,
            });
        }
        items.Sort((a, b) => b.Tokens.CompareTo(a.Tokens));
        var categories = items.GroupBy(item => item.CategoryKey).Select(group => new PerfContextCategory
        {
            Key = group.Key, Name = group.First().CategoryName, Color = group.First().Color,
            Items = group.ToArray(), TotalTokens = group.Sum(item => item.Tokens),
            Percent = group.Sum(item => item.Percent),
        }).ToArray();
        var cached = awaitingUsage ? 0 : Math.Min(context.CachedTokens, total);
        return new PerfContextBreakdown
        {
            TotalTokens = total,
            CapacityTokens = capacity,
            EstimatedTokens = estimatedTotal,
            CategoriesAreEstimated = estimatedTotal > 0,
            IsEstimated = measured == 0,
            IsPartial = estimate is null && session?.IsPartial == true,
            CachedTokens = cached,
            UncachedTokens = Math.Max(0, total - cached),
            CacheHitRate = total > 0 ? cached / (double)total : 0,
            ModelName = modelName,
            Categories = categories,
            AllItems = items,
        };
    }
}

internal sealed class PerfContextItem
{
    public required string Name { get; init; }
    public required string CategoryKey { get; init; }
    public required string CategoryName { get; init; }
    public required long Tokens { get; set; }
    public string? Tag { get; init; }
    public Color Color { get; init; }
    public double Percent { get; set; }
}

internal sealed class PerfContextCategory
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public required Color Color { get; init; }
    public required IReadOnlyList<PerfContextItem> Items { get; init; }
    public long TotalTokens { get; set; }
    public double Percent { get; set; }
}

internal sealed class PerfContextBreakdown
{
    public long TotalTokens { get; init; }
    public long? CapacityTokens { get; init; }
    public long EstimatedTokens { get; init; }
    public bool IsEstimated { get; init; }
    public bool CategoriesAreEstimated { get; init; }
    public bool IsPartial { get; init; }
    public long CachedTokens { get; init; }
    public long UncachedTokens { get; init; }
    public double CacheHitRate { get; init; }
    public string ModelName { get; init; } = string.Empty;
    public IReadOnlyList<PerfContextCategory> Categories { get; init; } = [];
    public IReadOnlyList<PerfContextItem> AllItems { get; init; } = [];
}

internal sealed class PerfTurnInfo
{
    public long TraceId { get; init; }
    public SpanRecord? Root { get; init; }
    public bool IsStartup { get; init; }
    public DateTimeOffset StartUtc { get; init; }
    public double DurationMs { get; init; }
    public IReadOnlyList<SpanRecord> Spans { get; init; } = [];
    public IReadOnlyList<SpanRecord> ModelSpans { get; init; } = [];
    public IReadOnlyList<SpanRecord> ToolSpans { get; init; } = [];
    public IReadOnlyList<SpanRecord> ApprovalSpans { get; init; } = [];
    public IReadOnlyList<SpanRecord> PersistSpans { get; init; } = [];
    public IReadOnlyList<SpanRecord> UiSpans { get; init; } = [];
    public IReadOnlyList<SpanRecord> EventSpans { get; init; } = [];
    public IReadOnlyList<PerfEventRecord> Events { get; init; } = [];
    public IReadOnlyList<SpanRecord> SignificantChildren { get; set; } = [];
    public int ModelCalls { get; init; }
    public int ToolCalls { get; init; }
    public int Errors { get; init; }
    public double ModelMs { get; init; }
    public double ToolMs { get; init; }
    public double ApprovalMs { get; init; }
    public double PersistMs { get; init; }
    public long PromptTokens { get; init; }
    public long CompletionTokens { get; init; }
    public long CachedTokens { get; init; }
    public string Outcome { get; init; } = "ok";
    public string Title { get; init; } = "回合";
    public string TimeLabel { get; init; } = string.Empty;
    public string Label => IsStartup ? $"启动 · {PerfPalette.FormatMs(DurationMs)}" :
        $"{TimeLabel} · {PerfPalette.FormatMs(DurationMs)}";
}

internal sealed class PerfToolUsage
{
    public required string RawName { get; init; }
    public required string Name { get; init; }
    public int Count { get; init; }
    public double TotalMs { get; init; }
    public double MaxMs { get; init; }
    public int Errors { get; init; }
    public string? SampleCommand { get; init; }
    public double AverageMs => Count > 0 ? TotalMs / Count : 0;
}

internal sealed class PerfContextInfo
{
    public long PromptTokens { get; init; }
    public long CachedTokens { get; init; }
    public long CompletionTokens { get; init; }
    public long UncachedTokens => Math.Max(0, PromptTokens - CachedTokens);
    public bool HasData => PromptTokens > 0 || CachedTokens > 0 || CompletionTokens > 0;
}
