using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Fufucode.Core.Backend;

namespace Fufucode.App.Backend.Tests;

/// <summary>
/// 真实端点下的 faster / stronger 子代理对照探针（不进默认套件）。
///
/// 用途：同一任务、同一模型、同一工作区，分别以两个档位运行只读子代理，
/// 记录轮次、工具调用、耗时、token 与结论是否命中已知事实。
/// 这是判断 stronger 是否值得存在的唯一依据——配置名称不构成证据。
///
/// 用法：
///   dotnet run --project tests/Fufucode.App.Backend.Tests -- \
///       --live-subagent-bench http://127.0.0.1:3425/v1 devin/swe-2 [apiKey] [workspace]
/// </summary>
internal static class LiveSubagentProbe
{
    private sealed record Fact(string Name, string Pattern, string ExpectedHint);

    public static async Task<int> RunAsync(string baseUrl, string model, string? apiKey, string? workspace,
        string task = "facts")
    {
        workspace ??= FindRepositoryRoot();
        if (workspace is null || !Directory.Exists(workspace))
        {
            Console.Error.WriteLine("找不到可用的工作区；请显式传入路径。");
            return 1;
        }

        Console.WriteLine($"baseUrl  : {baseUrl}");
        Console.WriteLine($"model    : {model}");
        Console.WriteLine($"workspace: {workspace}");
        Console.WriteLine();

        // 三个跨文件事实：单点搜索即可找到，但一次问全需要多轮检索或多路并行。
        // facts 任务用精确取值；survey 任务用"是否覆盖该子系统"的可定位关键词。
        var facts = task == "survey"
            ? new[]
            {
                new Fact("工具调度", "工具", @"ProductionToolDispatcher|NativeAgentLoop"),
                new Fact("压缩持久化", "压缩", @"CompactIfNeededAsync|PersistHistory"),
                new Fact("模型客户端", "流式", @"OpenAiCompatibleModelClient|ChatCompletionSseParser"),
                new Fact("插件 MCP", "插件", @"PluginToolRegistry|McpManager"),
                new Fact("子代理隔离", "子代理", @"SubagentRunner|worktree"),
            }
            : new[]
            {
                new Fact("整读字符上限常量", @"FullReadCharLimit\s*=\s*([0-9*\s]+)", @"48\s*\*\s*1024"),
                new Fact("grep 上下文行上限", @"context_lines[\s\S]{0,200}?Math\.Clamp\(cl,\s*0,\s*(\d+)\)", @"\b5\b"),
                new Fact("Agent 轮次预算入口", @"maxIterations\s*=\s*(\d+)\)", @"maxIterations"),
            };

        var prompt = task == "survey"
            // 需要覆盖多个独立子系统的调查题：并行拆解才有收益，用于检验 stronger 的嵌套是否真的被使用。
            ? "审查本仓库 FUMO 内核的以下 5 个子系统，每个给出 1 条最重要的健壮性风险，并附 文件:行号 证据：\n" +
              "1) 工具调度与审批；2) 上下文压缩与会话持久化；3) 模型客户端与流式解析；\n" +
              "4) 插件与 MCP 生命周期；5) 子代理隔离。\n" +
              "不要修改任何文件；每条风险要说明触发条件与后果，没有证据的猜测不要写。"
            : "在本仓库中定位以下 3 个事实，每个都给出 文件路径:行号 与取值，不要修改任何文件：\n" +
              "1) ProductionToolDispatcher 中整文件读取的字符上限常量名与取值；\n" +
              "2) grep_search 的 context_lines 允许的最大值；\n" +
              "3) FUMO 会话默认最大 Agent 轮次。\n" +
              "只报告有证据的结论。";

        var client = new OpenAiCompatibleModelClient(baseUrl, apiKey ?? "local", model);
        var dispatcher = new ProductionToolDispatcher(workspace);

        var results = new List<(SubagentTier Tier, SubagentResult Result)>();
        foreach (var tier in new[] { SubagentTier.Faster, SubagentTier.Stronger })
        {
            var runner = new SubagentRunner(
                AgentRegistry.Discover(workspace), client, dispatcher, model, workspace,
                depth: 0, maxDepth: 1);
            runner.SetTier(tier);

            Console.WriteLine($"=== {tier.ToString().ToLowerInvariant()} ===");
            var started = Stopwatch.GetTimestamp();
            SubagentResult result;
            // 上游网关偶发 ECONNRESET；探针允许一次重试，避免把运输故障当作档位差异。
            for (var attempt = 1; ; attempt++)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(6));
                try
                {
                    result = await runner.RunAsync("explore", prompt, null, timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    result = new SubagentResult(false, "explore", SubagentMode.Read, string.Empty, 0, "probe timeout")
                    {
                        Tier = tier
                    };
                }

                var transportFailure = !result.Success &&
                                       result.Error?.Contains("ECONNRESET", StringComparison.OrdinalIgnoreCase) == true;
                if (!transportFailure || attempt >= 2) break;
                Console.WriteLine($"  (运输失败，重试第 {attempt + 1} 次)");
            }

            var elapsed = Stopwatch.GetElapsedTime(started);
            var hits = CountHits(facts, result.Content);
            var nestedCalls = result.ToolTrace?.Count(t => t.Tool == "subagent") ?? 0;

            Console.WriteLine($"ok        : {result.Success}");
            Console.WriteLine($"duration  : {elapsed.TotalSeconds:0.0}s");
            Console.WriteLine($"toolCalls : {result.ToolCalls} (嵌套派发 {nestedCalls})");
            Console.WriteLine($"tokens    : in={result.InputTokens} out={result.OutputTokens}");
            Console.WriteLine($"truncated : {result.Truncated}");
            Console.WriteLine(task == "survey"
                ? $"coverage  : {hits}/{facts.Length} 个子系统命中"
                : $"facts     : {hits}/{facts.Length}");
            if (result.Error is { } error) Console.WriteLine($"error     : {error}");
            Console.WriteLine("--- content ---");
            Console.WriteLine(Trim(result.Content, 1600));
            Console.WriteLine();
            results.Add((tier, result with { DurationMs = (long)elapsed.TotalMilliseconds }));
        }

        Console.WriteLine("=== 对照 ===");
        foreach (var (tier, result) in results)
        {
            var hits = CountHits(facts, result.Content);
            var nested = result.ToolTrace?.Count(t => t.Tool == "subagent") ?? 0;
            Console.WriteLine(
                $"{tier,-9} ok={result.Success} hits={hits}/{facts.Length} " +
                $"tools={result.ToolCalls} nested={nested} ms={result.DurationMs} " +
                $"truncated={result.Truncated}");
        }

        // 探针不设通过阈值：结论用于人工判断档位是否值得保留。
        return results.All(r => r.Result.Success) ? 0 : 2;
    }

    private static int CountHits(IReadOnlyList<Fact> facts, string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return 0;
        var hits = 0;
        foreach (var fact in facts)
        {
            try
            {
                if (System.Text.RegularExpressions.Regex.IsMatch(content, fact.ExpectedHint)) hits++;
            }
            catch (System.Text.RegularExpressions.RegexParseException)
            {
                // 探针的匹配模式写错时不应中断整轮对照。
            }
        }
        return hits;
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

    private static string Trim(string? value, int length)
    {
        if (string.IsNullOrEmpty(value)) return "<empty>";
        value = value.Trim();
        return value.Length <= length ? value : value[..length] + "…";
    }
}
