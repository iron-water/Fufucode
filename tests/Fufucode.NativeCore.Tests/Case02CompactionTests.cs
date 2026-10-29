using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Fufucode.Core.Backend;

namespace Fufucode.NativeCore.Tests;

/// <summary>
/// 压缩覆盖边界回归（任务 02，B 语义）：
/// - 摘要输入逐条截断 content 6000 / arguments 800，并显式标记 “truncated N chars”；
/// - 截断后能计入预算的完整工具组视为已摘要并允许删除；
/// - 累计预算不足时，其后未渲染组原样保留；尾组始终逐字保留；
/// - 空摘要/摘要异常不删除；无任何组可覆盖时返回 false，overflow 不循环；
/// - tool_call/tool_result 配对不被破坏。
/// 集成锚点：在 Program.cs 的 LongHorizonTests 调用后加入
///   await CompactionCoverageTests.RunAsync(checks);
/// </summary>
internal static class Case02CompactionTests
{
    private static readonly MethodInfo CompactMethod = typeof(PureCSharpAgentEngine)
        .GetMethod("CompactIfNeededAsync", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("CompactIfNeededAsync 缺失，压缩实现已改名");
    private static readonly FieldInfo HistoryField = typeof(PureCSharpAgentEngine)
        .GetField("_history", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("_history 字段缺失");
    private static readonly FieldInfo ResolvedField = typeof(PureCSharpAgentEngine)
        .GetField("_contextResolved", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("_contextResolved 字段缺失");
    private static readonly FieldInfo ContextTokensField = typeof(PureCSharpAgentEngine)
        .GetField("_resolvedContextTokens", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("_resolvedContextTokens 字段缺失");
    private static readonly FieldInfo DeferredField = typeof(PureCSharpAgentEngine)
        .GetField("_compactionDeferred", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("_compactionDeferred 字段缺失");

    private const int SmallContext = 1_000; // 摘要预算 = Clamp(750, 30_000, 240_000) = 30_000 字符。
    private const int SummaryContentChars = 6_000;

    public static async Task RunAsync(Checks checks)
    {
        await CoveredPrefixOnlyAsync(checks);
        await TruncatedOversizedGroupIsCoveredAsync(checks);
        await NoSummaryMeansNoDeletionAsync(checks);
        await CountStableButCharsShrinkAsync(checks);
        await OverflowRecoveryUsesCompactedProgressAsync(checks);
        await NoProgressOverflowDoesNotLoopAsync(checks);
    }

    private static async Task CoveredPrefixOnlyAsync(Checks checks)
    {
        var client = new CapturingClient(() => "## 1. 用户目标\n继续");
        var engine = newEngine(client);
        engine.RestoreHistory(BuildHistory(40, 7_000));
        var before = History(engine).Count;

        var changed = await CompactAsync(engine);

        checks.Check(changed, "预算不足时前缀部分可压缩");
        checks.Equal(1, client.Requests.Count, "只发起一次摘要请求");
        var transcript = client.Requests[0].Messages
            .Last(message => message.Role == PureAgentRole.User).Content;
        var covered = Markers(transcript);
        checks.Check(covered.Count > 0, "摘要请求包含至少一个完整工具组");
        var maxCovered = covered.Max();
        var next = maxCovered + 1;
        var coveredText = ToolContent(maxCovered, 7_000);
        checks.Check(coveredText.Length > SummaryContentChars, "测试组自身超过单条截断上限");
        var omitted = coveredText.Length - SummaryContentChars;
        checks.Check(transcript.Contains($"[truncated {omitted} chars]", StringComparison.Ordinal),
            "摘要转录显式标记截断字符数");
        checks.Check(!transcript.Contains($"|END-{maxCovered}", StringComparison.Ordinal),
            "被截断的尾部未进入摘要");

        var live = History(engine);
        checks.Check(live.Count < before, "已摘要前缀被替换为摘要，未摘要后缀保留");
        checks.Check(!live.Any(message => Content(message).Contains($"MARKER-{maxCovered}-", StringComparison.Ordinal)),
            "最后一个已摘要组被移除");
        checks.Check(live.Any(message => Content(message).Contains($"MARKER-{next}-", StringComparison.Ordinal)),
            "累计预算耗尽后的第一个组保留");
        checks.Check(live.Any(message => Content(message) == ToolContent(next, 7_000)),
            "预算耗尽后的组逐字保留");
        checks.Check(live.Any(message => Content(message) == ToolContent(39, 7_000)),
            "最新尾组逐字保留，不截断");
        checks.Check(live.Any(message => Content(message).Contains("[上下文摘要", StringComparison.Ordinal)),
            "压缩后历史包含摘要消息");
        AssertToolPairing(live, checks);
    }

    private static async Task TruncatedOversizedGroupIsCoveredAsync(Checks checks)
    {
        var client = new CapturingClient(() => "## 1. 用户目标\n继续");
        var engine = newEngine(client);
        var big = "BIG-HEAD-" + new string('b', 200_000) + "|BIG-END";
        var history = new List<PureAgentMessage>
        {
            new(PureAgentRole.System, "系统"),
            new(PureAgentRole.User, "pinned constraint"),
            new(PureAgentRole.Assistant, "", ToolCalls: new[]
            {
                new PureToolCall("big", "read_file", "{\"path\":\"big.cs\"}"),
            }),
            new(PureAgentRole.Tool, big, ToolCallId: "big"),
        };
        // 总条数必须高于 keepRecent+1，超大组才会进入预算评估；尾组保持短小且逐字保留。
        for (var i = 0; i < 3; i++)
        {
            var id = $"tail-{i}";
            history.Add(new PureAgentMessage(PureAgentRole.Assistant, "", ToolCalls: new[]
            {
                new PureToolCall(id, "read_file", $"{{\"path\":\"tail{i}.cs\"}}"),
            }));
            history.Add(new PureAgentMessage(PureAgentRole.Tool, $"TAIL-END-{i}", ToolCallId: id));
        }
        engine.RestoreHistory(history);
        var beforeChars = Chars(History(engine));

        var changed = await CompactAsync(engine);

        checks.Check(changed, "超大工具组截断后计入预算并可压缩");
        checks.Equal(1, client.Requests.Count, "超大组只发起一次摘要请求");
        var transcript = client.Requests[0].Messages
            .Last(message => message.Role == PureAgentRole.User).Content;
        checks.Check(transcript.Contains("BIG-HEAD", StringComparison.Ordinal),
            "超大组头部进入摘要");
        var omitted = big.Length - SummaryContentChars;
        checks.Check(transcript.Contains($"[truncated {omitted} chars]", StringComparison.Ordinal),
            "超大组显式标记省略字符数");
        checks.Check(!transcript.Contains("|BIG-END", StringComparison.Ordinal),
            "超大组尾部未进入摘要");

        var live = History(engine);
        checks.Check(!live.Any(message => Content(message).Contains("BIG-HEAD", StringComparison.Ordinal)),
            "超大组已摘要后从历史移除");
        checks.Check(live.Any(message => Content(message) == "TAIL-END-0"),
            "最新尾组逐字保留");
        checks.Check(live.Any(message => Content(message) == "pinned constraint"),
            "pinned 用户约束保留");
        checks.Check(Chars(live) < beforeChars, "超大组压缩后字符量缩小");
        AssertToolPairing(live, checks);
    }

    private static async Task NoSummaryMeansNoDeletionAsync(Checks checks)
    {
        var empty = new CapturingClient(() => "   ");
        var emptyEngine = newEngine(empty);
        emptyEngine.RestoreHistory(BuildHistory(12, 7_000));
        var beforeEmpty = History(emptyEngine).Count;
        checks.Check(!await CompactAsync(emptyEngine), "空摘要返回未压缩");
        checks.Equal(beforeEmpty, History(emptyEngine).Count, "空摘要不删除任何消息");
        checks.Check(!History(emptyEngine).Any(message =>
                Content(message).Contains("[上下文", StringComparison.Ordinal)),
            "空摘要不伪造摘要占位");

        var failing = new CapturingClient(() => throw new InvalidOperationException("summary down"));
        var failingEngine = newEngine(failing);
        failingEngine.RestoreHistory(BuildHistory(12, 7_000));
        var beforeFailure = History(failingEngine).Count;
        checks.Check(!await CompactAsync(failingEngine), "摘要失败返回未压缩");
        checks.Equal(beforeFailure, History(failingEngine).Count, "摘要失败保留全部历史以便重试");
        checks.Check(!History(failingEngine).Any(message =>
                Content(message).Contains("[上下文已压缩", StringComparison.Ordinal)),
            "摘要失败不写入“已压缩”伪状态");
    }

    private static async Task CountStableButCharsShrinkAsync(Checks checks)
    {
        var client = new CapturingClient(() => "## 1. 用户目标\n继续");
        var engine = newEngine(client);
        engine.RestoreHistory(BuildCountStableHistory());
        var live = History(engine);
        var beforeCount = live.Count;
        var beforeChars = Chars(live);

        var changed = await CompactAsync(engine);

        checks.Check(changed, "有效压缩不以消息条数变化为前提");
        checks.Equal(beforeCount, live.Count, "摘要/台账/pinned 插入可使消息条数不变");
        checks.Check(Chars(live) < beforeChars, "压缩后字符量确实缩小");
        checks.Check(!live.Any(message => Content(message).Contains("COVERED-BIG", StringComparison.Ordinal)),
            "被摘要的大工具结果已移除");
        checks.Check(live.Any(message => Content(message).Contains("保留的尾组", StringComparison.Ordinal)),
            "最近完整组仍保留");
        AssertToolPairing(live, checks);
    }

    private static async Task OverflowRecoveryUsesCompactedProgressAsync(Checks checks)
    {
        var client = new OverflowThenSucceedClient();
        var engine = new PureCSharpAgentEngine(modelClient: client);
        // 模拟“本轮已压缩过”的状态，让第一次模型请求直接走真实 overflow 恢复分支。
        ResolvedField.SetValue(engine, true);
        ContextTokensField.SetValue(engine, (long?)SmallContext);
        DeferredField.SetValue(engine, true);
        engine.RestoreHistory(BuildCountStableHistory());
        var beforeChars = Chars(History(engine));

        var result = await engine.RunTurnAsync(new PureModelRequest(
            new[] { new PureAgentMessage(PureAgentRole.User, "继续") },
            Array.Empty<string>(), "test-model"));

        checks.Equal(PureAgentTurnStatus.Completed, result.Status,
            "overflow 恢复后同一回合并未判死");
        checks.Equal(1, client.SummaryCalls, "overflow 恢复只压缩一次");
        checks.Check(Chars(History(engine)) < beforeChars, "恢复入口消费了真实压缩进展");
        checks.Check(!History(engine).Any(message =>
                Content(message).Contains("COVERED-BIG", StringComparison.Ordinal)),
            "恢复后未摘要尾组仍在、已摘要大结果已移除");
    }

    private static async Task NoProgressOverflowDoesNotLoopAsync(Checks checks)
    {
        var client = new AlwaysOverflowClient();
        var engine = new PureCSharpAgentEngine(modelClient: client);
        ResolvedField.SetValue(engine, true);
        ContextTokensField.SetValue(engine, (long?)SmallContext);
        DeferredField.SetValue(engine, true);
        engine.RestoreHistory(BuildNoProgressHistory());

        var result = await engine.RunTurnAsync(new PureModelRequest(
            new[] { new PureAgentMessage(PureAgentRole.User, "继续") },
            Array.Empty<string>(), "test-model"));

        checks.Equal(PureAgentTurnStatus.Failed, result.Status,
            "无压缩进展时 overflow 不循环，回合失败收束");
        checks.Equal(1, client.ModelCalls, "模型请求只发一次，不反复摘要重试");
        checks.Equal(0, client.SummaryCalls, "无可完整渲染的整组时不调用摘要模型");
        checks.Check(History(engine).Any(message =>
                message.Role == PureAgentRole.Assistant && message.ToolCalls is { Count: 50 }),
            "无进展后超大工具组仍完整保留");
        checks.Check(History(engine).Any(message =>
                Content(message) == "TAIL-NOPROGRESS"), "无进展后尾组仍逐字保留");
    }

    private static PureCSharpAgentEngine newEngine(IPureModelClient client)
    {
        var engine = new PureCSharpAgentEngine(modelClient: client);
        ResolvedField.SetValue(engine, true);
        ContextTokensField.SetValue(engine, (long?)SmallContext);
        return engine;
    }

    private static async Task<bool> CompactAsync(PureCSharpAgentEngine engine)
    {
        var task = (Task<bool>)CompactMethod.Invoke(engine, new object?[]
        {
            History(engine), "test-model", CancellationToken.None, true, false,
        })!;
        return await task.ConfigureAwait(false);
    }

    private static List<PureAgentMessage> History(PureCSharpAgentEngine engine) =>
        (List<PureAgentMessage>)HistoryField.GetValue(engine)!;

    private static long Chars(List<PureAgentMessage> history) =>
        history.Sum(NativeHistoryGroups.SizeOf);

    private static string ToolContent(int index, int chars) =>
        $"MARKER-{index}-{new string('x', chars)}|END-{index}";

    private static List<PureAgentMessage> BuildHistory(int groups, int contentChars)
    {
        var history = new List<PureAgentMessage>
        {
            new(PureAgentRole.System, "系统"),
            new(PureAgentRole.User, "坚持只用标准库"),
        };
        for (var i = 0; i < groups; i++)
        {
            var id = $"call-{i}";
            history.Add(new PureAgentMessage(PureAgentRole.Assistant, "", ToolCalls: new[]
            {
                new PureToolCall(id, "read_file", $"{{\"path\":\"f{i}.cs\"}}"),
            }));
            history.Add(new PureAgentMessage(PureAgentRole.Tool,
                ToolContent(i, contentChars), ToolCallId: id));
        }
        return history;
    }

    /// <summary>
    /// 覆盖 5 条消息（3 条 pinned user + 完整工具组），插入摘要+台账+3 条 pinned 也是 5 条：
    /// 条数不变但字符量显著缩小；总条数须高于 keepRecent+1 才会进入压缩。
    /// </summary>
    private static List<PureAgentMessage> BuildCountStableHistory()
    {
        var history = new List<PureAgentMessage> { new(PureAgentRole.System, "系统") };
        for (var i = 0; i < 3; i++)
            history.Add(new PureAgentMessage(PureAgentRole.User,
                $"PIN-{i}-" + new string((char)('a' + i), 300)));
        history.Add(new PureAgentMessage(PureAgentRole.Assistant, "", ToolCalls: new[]
        {
            new PureToolCall("covered", "read_file", "{\"path\":\"big.cs\"}"),
        }));
        history.Add(new PureAgentMessage(PureAgentRole.Tool,
            "COVERED-BIG-" + new string('t', 2_000), ToolCallId: "covered"));
        for (var i = 0; i < 3; i++)
        {
            var id = $"tail-{i}";
            history.Add(new PureAgentMessage(PureAgentRole.Assistant, "", ToolCalls: new[]
            {
                new PureToolCall(id, "read_file", $"{{\"path\":\"tail{i}.cs\"}}"),
            }));
            history.Add(new PureAgentMessage(PureAgentRole.Tool,
                $"保留的尾组-{i}", ToolCallId: id));
        }
        return history;
    }

    /// <summary>单个工具组的渲染成本超过 30k 摘要预算：50 个 800 字符参数。</summary>
    private static List<PureAgentMessage> BuildNoProgressHistory()
    {
        var history = new List<PureAgentMessage> { new(PureAgentRole.System, "系统") };
        var arguments = "{\"path\":\"" + new string('p', 780) + "\"}";
        var calls = Enumerable.Range(0, 50)
            .Select(i => new PureToolCall($"huge-{i}", "read_file", arguments))
            .ToArray();
        history.Add(new PureAgentMessage(PureAgentRole.Assistant, "", ToolCalls: calls));
        foreach (var call in calls)
            history.Add(new PureAgentMessage(PureAgentRole.Tool, "ok", ToolCallId: call.Id));
        history.Add(new PureAgentMessage(PureAgentRole.Assistant, "", ToolCalls: new[]
        {
            new PureToolCall("tail", "read_file", "{\"path\":\"tail.cs\"}"),
        }));
        history.Add(new PureAgentMessage(PureAgentRole.Tool, "TAIL-NOPROGRESS", ToolCallId: "tail"));
        return history;
    }

    private static HashSet<int> Markers(string transcript) =>
        Regex.Matches(transcript, @"MARKER-(\d+)-")
            .Select(match => int.Parse(match.Groups[1].Value))
            .ToHashSet();

    private static string Content(PureAgentMessage message) => message.Content ?? string.Empty;

    private static void AssertToolPairing(List<PureAgentMessage> history, Checks checks)
    {
        var calls = history
            .Where(message => message.Role == PureAgentRole.Assistant && message.ToolCalls is { Count: > 0 })
            .SelectMany(message => message.ToolCalls!)
            .Select(call => call.Id)
            .ToHashSet(StringComparer.Ordinal);
        var results = history
            .Where(message => message.Role == PureAgentRole.Tool && message.ToolCallId is not null)
            .Select(message => message.ToolCallId!)
            .ToList();
        checks.Check(results.All(calls.Contains), "保留的工具结果都有对应 tool_call");
        checks.Check(results.Count == calls.Count, "保留的 tool_call 与 tool_result 一一配对");
    }

    private sealed class CapturingClient : IPureModelClient
    {
        private readonly Func<string> _reply;
        public CapturingClient(Func<string> reply) => _reply = reply;
        public List<PureModelRequest> Requests { get; } = new();

        public Task<PureModelResponse> GenerateAsync(PureModelRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(PureModelResponse.FromText(_reply()));
        }
    }

    private sealed class AlwaysOverflowClient : IPureModelClient
    {
        public int ModelCalls { get; private set; }
        public int SummaryCalls { get; private set; }

        public Task<PureModelResponse> GenerateAsync(PureModelRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Messages.Any(message => message.Role == PureAgentRole.System &&
                    message.Content.Contains("上下文压缩器", StringComparison.Ordinal)))
            {
                SummaryCalls++;
                return Task.FromResult(PureModelResponse.FromText("不应被调用"));
            }
            ModelCalls++;
            throw new ModelClientException(ModelClientErrorKind.InvalidRequest,
                "maximum context length exceeded", 400, "context_length_exceeded");
        }
    }

    private sealed class OverflowThenSucceedClient : IPureModelClient
    {
        private int _calls;

        public int SummaryCalls { get; private set; }

        public Task<PureModelResponse> GenerateAsync(PureModelRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Messages.Any(message => message.Role == PureAgentRole.System &&
                    message.Content.Contains("上下文压缩器", StringComparison.Ordinal)))
            {
                SummaryCalls++;
                return Task.FromResult(PureModelResponse.FromText("## 1. 用户目标\n继续"));
            }
            _calls++;
            if (_calls == 1)
                throw new ModelClientException(ModelClientErrorKind.InvalidRequest,
                    "maximum context length exceeded", 400, "context_length_exceeded");
            return Task.FromResult(PureModelResponse.FromText("完成"));
        }
    }
}
