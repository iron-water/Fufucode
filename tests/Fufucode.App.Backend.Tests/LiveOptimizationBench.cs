using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Fufucode.Core.Backend;

namespace Fufucode.App.Backend.Tests;

/// <summary>
/// 真实端点重复实验探针（不进默认套件）。
///
/// 三组实验：
///   A. faster / stronger 子代理：两种任务 × N 次重复，取中位数与范围。
///   B. 压缩约束召回：构造埋有硬约束的长会话，触发真实压缩后让模型回答依赖该约束的问题。
///   C. 工具调用效率：同一任务在压缩/不压缩下的工具调用数与失败数。
///
/// 用法：
///   dotnet run --project tests/Fufucode.App.Backend.Tests -- \
///       --live-bench <baseUrl> <model> [apiKey] [workspace] [repeats]
/// </summary>
internal static class LiveOptimizationBench
{
    public static async Task<int> RunAsync(string baseUrl, string model, string? apiKey,
        string? workspace, int repeats)
    {
        workspace ??= FindRepositoryRoot();
        if (workspace is null || !Directory.Exists(workspace))
        {
            Console.Error.WriteLine("找不到工作区，请显式传入路径。");
            return 1;
        }
        repeats = Math.Clamp(repeats, 1, 10);

        Console.WriteLine($"baseUrl : {baseUrl}");
        Console.WriteLine($"model   : {model}");
        Console.WriteLine($"workspace: {workspace}");
        Console.WriteLine($"repeats : {repeats}");
        Console.WriteLine();

        var client = new OpenAiCompatibleModelClient(baseUrl, apiKey ?? "local", model);
        var dispatcher = new ProductionToolDispatcher(workspace);
        var exitCode = 0;

        await RunSubagentBenchAsync(client, dispatcher, workspace, model, repeats);

        var compaction = await RunCompactionBenchAsync(client, dispatcher, model, repeats);
        if (!compaction) exitCode = 2;

        return exitCode;
    }

    // ── A. 子代理档位 ──

    private sealed record SubagentRun(
        SubagentTier Tier, string Task, bool Success, int Hits, int Total,
        int ToolCalls, int NestedCalls, long InputTokens, long OutputTokens,
        long DurationMs, bool Truncated, string? Error);

    private static async Task RunSubagentBenchAsync(OpenAiCompatibleModelClient client,
        ProductionToolDispatcher dispatcher, string workspace, string model, int repeats)
    {
        var tasks = new (string Name, string Prompt, string[] Hints)[]
        {
            ("facts",
                "在本仓库中定位以下 3 个事实，每个给出 文件路径:行号 与取值，不要修改任何文件：\n" +
                "1) ProductionToolDispatcher 中整文件读取的字符上限常量名与取值；\n" +
                "2) grep_search 的 context_lines 允许的最大值；\n" +
                "3) FUMO 会话默认最大 Agent 轮次。\n只报告有证据的结论。",
                new[] { @"48\s*\*\s*1024", @"Math\.Clamp\(cl,\s*0,\s*5\)", @"maxIterations\s*=\s*50" }),
            ("survey",
                "审查本仓库 FUMO 内核的以下 5 个子系统，每个给出 1 条最重要的健壮性风险并附 文件:行号：\n" +
                "1) 工具调度与审批；2) 上下文压缩与持久化；3) 模型客户端与流式解析；\n" +
                "4) 插件与 MCP 生命周期；5) 子代理隔离。\n不要修改文件；每条说明触发条件与后果，不要写没有证据的猜测。",
                new[] { @"ProductionToolDispatcher", @"CompactIfNeededAsync", @"OpenAiCompatibleModelClient",
                        @"PluginToolRegistry|McpManager", @"SubagentRunner" }),
        };

        var results = new List<SubagentRun>();
        foreach (var task in tasks)
        {
            foreach (var tier in new[] { SubagentTier.Faster, SubagentTier.Stronger })
            {
                for (var run = 1; run <= repeats; run++)
                {
                    var runner = new SubagentRunner(AgentRegistry.Discover(workspace), client, dispatcher,
                        model, workspace, depth: 0, maxDepth: 1);
                    runner.SetTier(tier);

                    var started = Stopwatch.GetTimestamp();
                    SubagentResult result;
                    for (var attempt = 1; ; attempt++)
                    {
                        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));
                        try
                        {
                            result = await runner.RunAsync("explore", task.Prompt, null, timeout.Token);
                        }
                        catch (OperationCanceledException)
                        {
                            result = new SubagentResult(false, "explore", SubagentMode.Read, "", 0, "timeout")
                            {
                                Tier = tier
                            };
                        }

                        var transport = !result.Success && IsTransportFailure(result.Error);
                        if (!transport || attempt >= 2) break;
                    }

                    var elapsed = Stopwatch.GetElapsedTime(started);
                    var hits = CountHits(task.Hints, result.Content);
                    var nested = result.ToolTrace?.Count(t => t.Tool == "subagent") ?? 0;
                    var record = new SubagentRun(tier, task.Name, result.Success, hits, task.Hints.Length,
                        result.ToolCalls, nested, result.InputTokens ?? 0, result.OutputTokens ?? 0,
                        (long)elapsed.TotalMilliseconds, result.Truncated, result.Error);
                    results.Add(record);

                    Console.WriteLine(
                        $"[{task.Name}/{tier.ToString().ToLowerInvariant()} #{run}] " +
                        $"ok={record.Success} hits={record.Hits}/{record.Total} tools={record.ToolCalls} " +
                        $"nested={record.NestedCalls} in={record.InputTokens} out={record.OutputTokens} " +
                        $"ms={record.DurationMs} truncated={record.Truncated}" +
                        (record.Error is { } e ? $" error={Trim(e, 120)}" : ""));
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine("=== 子代理汇总（中位数 / 范围）===");
        foreach (var task in tasks)
        {
            foreach (var tier in new[] { SubagentTier.Faster, SubagentTier.Stronger })
            {
                var set = results.Where(r => r.Task == task.Name && r.Tier == tier).ToArray();
                if (set.Length == 0) continue;
                var ok = set.Count(r => r.Success);
                var hits = set.Select(r => r.Hits).OrderBy(v => v).ToArray();
                var tools = set.Select(r => r.ToolCalls).OrderBy(v => v).ToArray();
                var ms = set.Select(r => r.DurationMs).OrderBy(v => v).ToArray();
                Console.WriteLine(
                    $"{task.Name,-7} {tier,-9} ok={ok}/{set.Length} " +
                    $"hits med={Median(hits)} range={hits[0]}..{hits[^1]} " +
                    $"tools med={Median(tools)} range={tools[0]}..{tools[^1]} " +
                    $"ms med={Median(ms)} range={ms[0]}..{ms[^1]} " +
                    $"nested={set.Sum(r => r.NestedCalls)} truncated={set.Count(r => r.Truncated)}");
            }
        }
        Console.WriteLine();
    }

    // ── B. 压缩约束召回 ──

    private static async Task<bool> RunCompactionBenchAsync(OpenAiCompatibleModelClient client,
        ProductionToolDispatcher dispatcher, string model, int repeats)
    {
        Console.WriteLine("=== 压缩约束召回（每轮：触发真实压缩后提问）===");
        const string constraint = "禁止使用任何第三方库，所有路径必须用相对路径，测试文件必须放在 test/ 目录下";
        var passed = 0;
        for (var run = 1; run <= repeats; run++)
        {
            var engine = new PureCSharpAgentEngine(dispatcher, client)
            {
                // 用真实窗口语义触发压缩（30k tokens），不再依赖已移除的 400k 字符阈值。
                ContextLengthResolver = (_, _) => Task.FromResult<long?>(30_000),
            };
            var history = new List<PureAgentMessage>
            {
                new(PureAgentRole.System, "你是编码 Agent。"),
                new(PureAgentRole.User,
                    $"实现一个图引擎。硬约束：{constraint}。完成后再报告。"),
            };
            // 制造足够体量触发 400k 字符阈值，且让约束远离最近窗口。
            for (var i = 0; i < 30; i++)
            {
                history.Add(new PureAgentMessage(PureAgentRole.Assistant,
                    $"第 {i} 步：读取模块 {i} 并确认接口。",
                    ToolCalls: new[]
                    {
                        new PureToolCall($"c{i}", "read_file", $"{{\"path\":\"src/module_{i}.cs\"}}")
                    }));
                history.Add(new PureAgentMessage(PureAgentRole.Tool, new string('x', 15_000), ToolCallId: $"c{i}"));
            }
            engine.RestoreHistory(history);

            var started = Stopwatch.GetTimestamp();
            var turn = await engine.RunTurnAsync(new PureModelRequest(
                new[]
                {
                    new PureAgentMessage(PureAgentRole.User,
                        "在继续之前，先用一句话复述本任务的全部硬约束，然后说明你下一步要做什么。"),
                },
                Array.Empty<string>(), model));

            var elapsed = Stopwatch.GetElapsedTime(started);
            var answer = turn.Response.Text ?? string.Empty;
            var compacted = engine.GetState().History!;
            var constraintsKept = new[]
            {
                answer.Contains("第三方", StringComparison.Ordinal),
                answer.Contains("相对路径", StringComparison.Ordinal),
                answer.Contains("test/", StringComparison.Ordinal) || answer.Contains("test 目录", StringComparison.Ordinal),
            }.Count(kept => kept);

            var compactedOnce = compacted.Any(m => m.Content.Contains("[上下文摘要", StringComparison.Ordinal));
            var ledgerKept = compacted.Any(m => m.Content.Contains("[压缩时保留的状态台账]", StringComparison.Ordinal));
            var pinnedKept = compacted.Any(m => m.Content.Contains("禁止使用任何第三方库", StringComparison.Ordinal));

            var ok = constraintsKept == 3;
            if (ok) passed++;
            Console.WriteLine(
                $"#{run} ok={ok} recall={constraintsKept}/3 compacted={compactedOnce} " +
                $"pinned={pinnedKept} ledger={ledgerKept} " +
                $"in={turn.Usage?.InputTokens} out={turn.Usage?.OutputTokens} " +
                $"ms={(long)elapsed.TotalMilliseconds}");
            Console.WriteLine($"    答复：{Trim(answer, 300)}");
        }

        Console.WriteLine($"约束召回：{passed}/{repeats} 次完整保留 3 条约束");
        Console.WriteLine();
        return passed == repeats;
    }

    // ── 工具 ──

    private static bool IsTransportFailure(string? error) =>
        error is not null &&
        (error.Contains("ECONNRESET", StringComparison.OrdinalIgnoreCase) ||
         error.Contains("socket", StringComparison.OrdinalIgnoreCase) ||
         error.Contains("certificate", StringComparison.OrdinalIgnoreCase) ||
         error.Contains("502", StringComparison.Ordinal));

    private static int CountHits(IReadOnlyList<string> patterns, string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return 0;
        var hits = 0;
        foreach (var pattern in patterns)
        {
            try
            {
                if (Regex.IsMatch(content, pattern)) hits++;
            }
            catch (ArgumentException) { }
        }
        return hits;
    }

    private static long Median(long[] values) => values.Length == 0 ? 0 : values[values.Length / 2];
    private static int Median(int[] values) => values.Length == 0 ? 0 : values[values.Length / 2];

    private static string Trim(string? value, int length)
    {
        if (string.IsNullOrEmpty(value)) return "<empty>";
        value = value.Replace('\n', ' ').Trim();
        return value.Length <= length ? value : value[..length] + "…";
    }

    private static string? FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Fufucode.sln"))) return directory.FullName;
            directory = directory.Parent;
        }
        return null;
    }
}
