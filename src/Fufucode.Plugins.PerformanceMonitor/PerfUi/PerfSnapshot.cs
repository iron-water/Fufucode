using System.Diagnostics;
using Avalonia.Media;
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
        IReadOnlyList<PerfEventRecord> events, IReadOnlyList<PerfHostSample> samples)
    {
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

            var startUtc = root.EndUtc - TimeSpan.FromMilliseconds(root.DurationMs);
            var durationMs = Math.Max(0, root.DurationMs);
            var endUtc = startUtc + TimeSpan.FromMilliseconds(durationMs);
            var turnEvents = events
                .Where(item => item.TimestampUtc >= startUtc.AddMilliseconds(-50) &&
                               item.TimestampUtc <= endUtc.AddMilliseconds(50))
                .ToArray();

            // 把阻塞/批事件补成树里的节点，保证“每一次阻塞”都出现在 Span 树中。
            var eventSpans = new List<SpanRecord>();
            foreach (var item in turnEvents)
            {
                var name = item.Kind switch
                {
                    "ui.stall" => "ui.stall",
                    "ui.batch" => "ui.batch",
                    "error.unhandled" => "error.unhandled",
                    _ => null,
                };
                if (name is null) continue;
                if (name == "ui.batch" && item.Value <= 0) continue;
                var duration = name == "error.unhandled" ? 0 : Math.Max(0, item.Value);
                var startTicks = item.TimestampTicks;
                var endTicks = startTicks + (long)(duration * Stopwatch.Frequency / 1000d);
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
            foreach (var span in all)
            {
                prompt += span.LongTag("promptTokens") ?? 0;
                completion += span.LongTag("completionTokens") ?? 0;
                cached += span.LongTag("cachedTokens") ?? 0;
            }

            var significant = modelSpans.Concat(toolSpans).Concat(approvalSpans)
                .OrderBy(span => span.StartTicks).Take(8).ToArray();

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
                Outcome = root.Tag("outcome") ?? (root.Status == "Error" ? "error" : "ok"),
                Title = isStartup ? "启动" : (root.Tag("model") ?? "回合"),
                TimeLabel = startUtc.ToLocalTime().ToString("HH:mm:ss"),
            };

            if (isStartup)
            {
                if (startup is null || info.StartUtc > startup.StartUtc) startup = info;
            }
            else
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
        var latestUsage = spans
            .Where(span => span.LongTag("promptTokens") is > 0)
            .OrderBy(span => span.EndTicks)
            .LastOrDefault();
        var context = latestUsage is null
            ? new PerfContextInfo()
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
        foreach (var span in spans)
        {
            promptTotal += span.LongTag("promptTokens") ?? 0;
            completionTotal += span.LongTag("completionTokens") ?? 0;
            cachedTotal += span.LongTag("cachedTokens") ?? 0;
        }

        var start = turns.Count > 0 ? turns.Min(turn => turn.StartUtc) : DateTimeOffset.MinValue;
        if (startup is not null && (start == DateTimeOffset.MinValue || startup.StartUtc < start)) start = startup.StartUtc;
        var end = spans.Count > 0 ? spans.Max(span => span.EndUtc) : start;
        if (start == DateTimeOffset.MinValue) start = end;

        var breakdown = BuildBreakdown(spans, turns, context, promptTotal, cachedTotal);

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

    private static PerfContextBreakdown BuildBreakdown(
        IReadOnlyList<SpanRecord> spans,
        IReadOnlyList<PerfTurnInfo> turns,
        PerfContextInfo context,
        long promptTotal,
        long cachedTotal)
    {
        var modelName = turns.LastOrDefault()?.Root?.Tag("model")
            ?? spans.FirstOrDefault(s => s.Tag("model") != null)?.Tag("model")
            ?? "默认模型";

        var totalContext = context.PromptTokens > 0 ? context.PromptTokens : (promptTotal > 0 ? promptTotal : 0);
        var cached = context.CachedTokens > 0 ? context.CachedTokens : cachedTotal;
        var uncached = Math.Max(0, totalContext - cached);
        var hitRate = totalContext > 0 ? Math.Clamp(cached / (double)totalContext, 0, 1) : 0;

        if (totalContext == 0 && spans.Count == 0)
        {
            return new PerfContextBreakdown
            {
                TotalTokens = 0,
                ModelName = modelName,
            };
        }

        var rawItems = new List<PerfContextItem>();

        // 1. 系统提示词 (System Prompt)
        var buildSpan = spans.LastOrDefault(s => s.Name == "context.build");
        var sysChars = buildSpan?.LongTag("sysChars");
        var sysTokens = sysChars is > 0 ? (long)(sysChars.Value / 3.5) : (totalContext > 0 ? Math.Max(300, (long)(totalContext * 0.12)) : 3200);
        rawItems.Add(new PerfContextItem
        {
            Name = "系统提示词",
            CategoryKey = "system",
            CategoryName = "系统提示词",
            Tokens = sysTokens,
            Tag = "system",
            Color = PerfPalette.CtxSystemPrompt,
        });

        // 2. 工具定义 (Tools)
        var toolSpans = spans.Where(s => s.Name == "tool" || s.Name == "tool.process").ToList();
        var toolGroups = toolSpans.GroupBy(s => s.Tag("tool") ?? "tool").ToList();
        var toolDefTokens = 0L;
        foreach (var group in toolGroups.Take(8))
        {
            var tName = PerfPalette.ToolDisplayName(group.Key);
            var tTokens = Math.Max(120, group.Count() * 180);
            toolDefTokens += tTokens;
            rawItems.Add(new PerfContextItem
            {
                Name = tName,
                CategoryKey = "tools",
                CategoryName = "工具",
                Tokens = tTokens,
                Tag = group.Key.StartsWith("mcp", StringComparison.OrdinalIgnoreCase) ? "MCP" : "工具",
                Color = PerfPalette.CtxTools,
            });
        }
        if (toolDefTokens == 0)
        {
            rawItems.Add(new PerfContextItem
            {
                Name = "工具契约定义",
                CategoryKey = "tools",
                CategoryName = "工具",
                Tokens = totalContext > 0 ? Math.Max(400, (long)(totalContext * 0.15)) : 2400,
                Tag = "工具",
                Color = PerfPalette.CtxTools,
            });
        }

        // 3. 记忆 / 规则 (Memory)
        var planSpans = spans.Where(s => s.Name.Contains("plan") || s.Tag("tool") == "update_plan").ToList();
        if (planSpans.Count > 0 || totalContext > 10_000)
        {
            rawItems.Add(new PerfContextItem
            {
                Name = "规划与约束记忆",
                CategoryKey = "memory",
                CategoryName = "记忆",
                Tokens = totalContext > 0 ? Math.Max(200, (long)(totalContext * 0.05)) : 1500,
                Tag = "memory",
                Color = PerfPalette.CtxMemory,
            });
        }

        // 4. 文件 (Files)
        var readSpans = toolSpans.Where(s => s.Tag("tool") is "read_file" or "read" or "read_multiple_files").ToList();
        if (readSpans.Count > 0)
        {
            var fileGroups = readSpans.GroupBy(s => s.Tag("file") ?? s.Tag("command") ?? s.Tag("cwd") ?? "文件").ToList();
            foreach (var fg in fileGroups.Take(6))
            {
                var fName = System.IO.Path.GetFileName(fg.Key);
                if (string.IsNullOrWhiteSpace(fName)) fName = fg.Key;
                var fTokens = Math.Max(80, fg.Sum(s => (s.LongTag("outputChars") ?? 1500) / 3));
                rawItems.Add(new PerfContextItem
                {
                    Name = fName,
                    CategoryKey = "files",
                    CategoryName = "文件",
                    Tokens = fTokens,
                    Tag = "read",
                    Color = PerfPalette.CtxFiles,
                });
            }
        }
        else if (totalContext > 20_000)
        {
            rawItems.Add(new PerfContextItem
            {
                Name = "工作区参考文件",
                CategoryKey = "files",
                CategoryName = "文件",
                Tokens = (long)(totalContext * 0.25),
                Tag = "read",
                Color = PerfPalette.CtxFiles,
            });
        }

        // 5. 工具结果 (Tool Results)
        var cmdSpans = spans.Where(s => s.Name == "tool.process" || (s.Name == "tool" && s.Tag("tool") is "run_command" or "bash")).ToList();
        if (cmdSpans.Count > 0)
        {
            var cmdTokens = Math.Max(200, cmdSpans.Sum(s => (s.LongTag("stdoutBytes") ?? 2000) / 3));
            rawItems.Add(new PerfContextItem
            {
                Name = "Bash",
                CategoryKey = "toolResults",
                CategoryName = "工具结果",
                Tokens = cmdTokens,
                Tag = $"×{cmdSpans.Count} result",
                Color = PerfPalette.CtxToolResults,
            });
        }
        var grepSpans = toolSpans.Where(s => s.Tag("tool") is "grep" or "grep_search" or "glob_files" or "list_dir").ToList();
        if (grepSpans.Count > 0)
        {
            var grepTokens = Math.Max(150, grepSpans.Sum(s => (s.LongTag("outputChars") ?? 1200) / 3));
            rawItems.Add(new PerfContextItem
            {
                Name = "Grep / 搜索",
                CategoryKey = "toolResults",
                CategoryName = "工具结果",
                Tokens = grepTokens,
                Tag = $"×{grepSpans.Count} result",
                Color = PerfPalette.CtxToolResults,
            });
        }

        // 6. 会话内容 (Conversation)
        if (turns.Count > 0)
        {
            for (var i = 0; i < turns.Count; i++)
            {
                var t = turns[i];
                var turnTokens = Math.Max(100, t.PromptTokens > 0 ? t.PromptTokens / Math.Max(1, turns.Count) : (long)(t.DurationMs * 0.8));
                rawItems.Add(new PerfContextItem
                {
                    Name = $"第 {i + 1} 轮",
                    CategoryKey = "conversation",
                    CategoryName = "会话内容",
                    Tokens = turnTokens,
                    Tag = "会话内容",
                    Color = PerfPalette.CtxConversation,
                });
            }
        }
        else if (totalContext > 0)
        {
            rawItems.Add(new PerfContextItem
            {
                Name = "当前会话消息",
                CategoryKey = "conversation",
                CategoryName = "会话内容",
                Tokens = Math.Max(200, (long)(totalContext * 0.2)),
                Tag = "会话内容",
                Color = PerfPalette.CtxConversation,
            });
        }

        var rawSum = rawItems.Sum(i => i.Tokens);
        if (totalContext > 0 && rawSum > 0)
        {
            var ratio = totalContext / (double)rawSum;
            foreach (var item in rawItems)
            {
                item.Tokens = Math.Max(1, (long)Math.Round(item.Tokens * ratio));
                item.Percent = item.Tokens / (double)totalContext;
            }
        }
        else
        {
            totalContext = rawSum;
            foreach (var item in rawItems)
            {
                item.Percent = rawSum > 0 ? item.Tokens / (double)rawSum : 0;
            }
        }

        rawItems.Sort((a, b) => b.Tokens.CompareTo(a.Tokens));

        var categories = rawItems
            .GroupBy(i => i.CategoryKey)
            .Select(g =>
            {
                var catItems = g.ToList();
                var catSum = catItems.Sum(i => i.Tokens);
                return new PerfContextCategory
                {
                    Key = g.Key,
                    Name = catItems[0].CategoryName,
                    Color = catItems[0].Color,
                    Items = catItems,
                    TotalTokens = catSum,
                    Percent = totalContext > 0 ? catSum / (double)totalContext : 0,
                };
            })
            .OrderByDescending(c => c.TotalTokens)
            .ToList();

        return new PerfContextBreakdown
        {
            TotalTokens = totalContext,
            CachedTokens = cached,
            UncachedTokens = uncached,
            CacheHitRate = hitRate,
            ModelName = modelName,
            Categories = categories,
            AllItems = rawItems,
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
