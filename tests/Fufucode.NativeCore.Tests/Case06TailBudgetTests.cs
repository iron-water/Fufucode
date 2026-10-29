using System.Reflection;
using Fufucode.Core.Backend;

namespace Fufucode.NativeCore.Tests;

/// <summary>
/// 迁移目标：tests/Fufucode.NativeCore.Tests（入口追加 --case 06）。
/// 直接调用真实 PureCSharpAgentEngine.CompactIfNeededAsync（private，反射），
/// 不复制尾选择算法。上下文窗口未知时 keepRecent=8、keepRecentChars=120_000，
/// force=true 绕过物理内存阈值。
/// 语义：被摘要的超大组按 6000/800 截断，标记 `…[truncated N chars]` 明确省略量；
/// 保留尾窗口逐字完整，不出现任何截断标记。
/// </summary>
internal static class Case06TailBudgetTests
{
    public static async Task RunAsync(Checks checks)
    {
        // 1) 短 assistant 后跟大 tool 结果：旧逻辑只把 assistant 计入预算，
        //    会把 200k 工具结果整段保留；新逻辑必须按组字符数把它压进摘要。
        var regression = Filler(10);
        regression.AddRange(ToolGroup("ok", 200_000));
        regression.Add(Latest("latest"));
        var client1 = new FakeModelClient();
        var engine = new PureCSharpAgentEngine(modelClient: client1);
        checks.Equal(true, await CompactAsync(engine, regression), "06 short assistant + huge tool compacts");
        checks.Equal(false, regression.Any(m => m.Content is { Length: >= 200_000 }), "06 huge tool result is summarized");
        checks.Equal("latest", regression[^1].Content, "06 latest short assistant kept");
        checks.Check(SummaryContains(client1, "…[truncated 194000 chars]"),
            "06 content truncation marker reports exact omitted chars");

        // 2) 整个后缀预算：100k 工具组可整体并入，再加更早组会超 120k；
        //    裁剪必须落在组起始点，且工具调用与其全部结果一起保留。
        var suffixBudget = Filler(8);
        suffixBudget.AddRange(ToolGroup("big", 50_000, toolCount: 2));
        suffixBudget.Add(Latest("latest"));
        var engine2 = new PureCSharpAgentEngine(modelClient: new FakeModelClient());
        checks.Equal(true, await CompactAsync(engine2, suffixBudget), "06 suffix budget compacts");
        checks.Equal(10, suffixBudget.Count, "06 note+ledger+8 kept messages");
        checks.Equal(2, suffixBudget.Count(m => m.Content is { Length: >= 50_000 }), "06 both tool results of kept group survive");
        checks.Equal(PureAgentRole.Assistant, suffixBudget[2].Role, "06 cut starts at assistant tool_call, not a tool result");

        // 3) 最低保留策略：最新完整组自身 160k 已超预算，仍整组保留、不拆工具配对。
        var minimum = Filler(8);
        minimum.AddRange(ToolGroup("huge", 80_000, toolCount: 2));
        var engine3 = new PureCSharpAgentEngine(modelClient: new FakeModelClient());
        checks.Equal(true, await CompactAsync(engine3, minimum), "06 minimum retention compacts earlier groups");
        checks.Equal(5, minimum.Count, "06 note+ledger+whole latest tool group");
        checks.Equal(2, minimum.Count(m => m.Content is { Length: 80_000 }), "06 oversized newest group kept whole");
        checks.Equal(2, minimum.Count(m => m.Content == new string('t', 80_000)),
            "06 kept tail tool content is verbatim");
        checks.Check(minimum.All(m => m.Content?.Contains("truncated", StringComparison.Ordinal) != true),
            "06 kept tail has no truncation marker");

        // 4) 工具参数同样截断并报告省略量。
        var arguments = Filler(4);
        arguments.Add(new PureAgentMessage(PureAgentRole.Assistant, "a",
            ToolCalls: new[] { new PureToolCall("c0", "read", new string('a', 5_000)) }));
        arguments.Add(new PureAgentMessage(PureAgentRole.Tool, "r", "c0"));
        arguments.AddRange(Filler(10));
        arguments.Add(Latest("latest"));
        var client4 = new FakeModelClient();
        var engine4 = new PureCSharpAgentEngine(modelClient: client4);
        checks.Equal(true, await CompactAsync(engine4, arguments), "06 oversized arguments compact");
        checks.Check(SummaryContains(client4, "…[truncated 4200 chars]"),
            "06 argument truncation marker reports exact omitted chars");

        Console.WriteLine("PASS 06 real CompactIfNeededAsync: 4 scenarios, no API calls");
    }

    private static bool SummaryContains(FakeModelClient client, string marker) =>
        client.LastRequest?.Messages.Any(message =>
            message.Content?.Contains(marker, StringComparison.Ordinal) == true) == true;

    private static async Task<bool> CompactAsync(PureCSharpAgentEngine engine, List<PureAgentMessage> history)
    {
        // 反射仅用于调用 private 生产实现；若将来提取 internal static 选择器，可直接替换。
        var method = typeof(PureCSharpAgentEngine).GetMethod("CompactIfNeededAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException("PureCSharpAgentEngine.CompactIfNeededAsync");
        var task = (Task<bool>)method.Invoke(engine,
            new object?[] { history, null, CancellationToken.None, true, false })!;
        return await task.ConfigureAwait(false);
    }

    private static List<PureAgentMessage> Filler(int count)
    {
        var history = new List<PureAgentMessage>();
        for (var i = 0; i < count; i++) history.Add(Latest("filler" + i));
        return history;
    }

    private static PureAgentMessage Latest(string text) => new(PureAgentRole.Assistant, text);

    private static List<PureAgentMessage> ToolGroup(string assistantText, int toolChars, int toolCount = 1)
    {
        var calls = Enumerable.Range(0, toolCount)
            .Select(i => new PureToolCall("c" + i, "read", "{}"))
            .ToArray();
        var messages = new List<PureAgentMessage>
        {
            new(PureAgentRole.Assistant, assistantText, ToolCalls: calls)
        };
        foreach (var call in calls)
            messages.Add(new PureAgentMessage(PureAgentRole.Tool, new string('t', toolChars), call.Id));
        return messages;
    }

    private sealed class FakeModelClient : IPureModelClient
    {
        public PureModelRequest? LastRequest { get; private set; }

        public Task<PureModelResponse> GenerateAsync(PureModelRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastRequest = request;
            return Task.FromResult(PureModelResponse.FromText("压缩摘要"));
        }
    }
}
