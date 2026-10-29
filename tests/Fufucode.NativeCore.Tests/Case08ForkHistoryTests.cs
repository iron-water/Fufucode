using Fufucode.Core.Backend;
using Fufucode.Plugins.Subagents;

namespace Fufucode.NativeCore.Tests;

/// <summary>Case 08: fork history excludes incomplete parent subagent groups and honors group budgets.</summary>
internal static class Case08ForkHistoryTests
{
    public static async Task RunAsync(Checks checks)
    {
        var root = TestTemp.Create("08");
        var userHome = Path.Combine(root, "user-home");
        Directory.CreateDirectory(userHome);
        var agents = AgentRegistry.Discover(root, userHome);
        var dispatcher = new NoopDispatcher();
        try
        {
            var parentToolCall = new PureToolCall("sub_parent_live", "subagent",
                "{\"agent\":\"explore\",\"prompt\":\"仍在跑\"}");
            var doneCall = new PureToolCall("done_1", "read_file", "{\"path\":\"a.txt\"}");
            var history = new List<PureAgentMessage>
            {
                new(PureAgentRole.System, "父系统提示"),
                new(PureAgentRole.User, "父用户问题"),
                new(PureAgentRole.Assistant, string.Empty, ToolCalls: new[] { doneCall }),
                new(PureAgentRole.Tool, "a.txt 内容", doneCall.Id),
                new(PureAgentRole.Assistant, "父助手结论"),
                new(PureAgentRole.Assistant, string.Empty, ToolCalls: new[] { parentToolCall }),
            };
            var snapshot = history.ToArray();
            var client = new CapturingClient("子代理完成");
            var runner = new SubagentRunner(agents, client, dispatcher, "m", root, historyProvider: () => history);
            var result = await runner.RunAsync("explore", "继续分析", null, CancellationToken.None, fork: true);

            checks.Check(result.Success, "fork 子代理运行成功");
            var first = client.Requests.Single();
            var messages = first.Messages;
            var firstToolIndex = -1;
            for (var i = 0; i < messages.Count; i++)
                if (messages[i].Role == PureAgentRole.Tool) { firstToolIndex = i; break; }

            checks.Check(!messages.Any(m => m.ToolCalls?.Any(c => c.Id == parentToolCall.Id) == true),
                "未完成的父 subagent tool_call 不进入子请求");
            checks.Check(!messages.Any(m => m.ToolCallId == parentToolCall.Id),
                "子请求中没有父 subagent 调用的孤儿/伪造 result");
            checks.Check(firstToolIndex < 0 || messages[firstToolIndex].ToolCallId == doneCall.Id,
                "子请求中 tool 组以完整配对开始");
            checks.Check(messages.Any(m => m.Content == "a.txt 内容"), "已完成的历史 tool 组被保留");
            checks.Check(messages.Any(m => m.Content == "父助手结论"), "父历史文本被保留");
            checks.Check(messages.Count(m => m.Role == PureAgentRole.System) == 1 &&
                         !messages.Any(m => m.Content == "父系统提示"), "父 System 消息不进入子历史");
            checks.Check(messages[^1].Role == PureAgentRole.User && messages[^1].Content == "继续分析",
                "子代理 prompt 位于首次请求末尾");
            checks.Equal(1 + 4 + 1, first.Messages.Count, "首次请求消息数 = 子 System + 父完整组 + 子 User");
            checks.Check(history.Count == snapshot.Length &&
                         history.Select((m, i) => ReferenceEquals(m, snapshot[i])).All(x => x),
                "父历史列表未被修改（条数与引用顺序不变）");

            var twoCalls = new[]
            {
                new PureToolCall("pair_a", "read_file", "{\"path\":\"a.txt\"}"),
                new PureToolCall("pair_b", "read_file", "{\"path\":\"b.txt\"}"),
            };
            var partialHistory = new List<PureAgentMessage>
            {
                new(PureAgentRole.User, "旧问题"),
                new(PureAgentRole.Assistant, "旧结论"),
                new(PureAgentRole.Assistant, string.Empty, ToolCalls: twoCalls),
                new(PureAgentRole.Tool, "只有 a 的结果", "pair_a"),
            };
            var partialClient = new CapturingClient("ok");
            await new SubagentRunner(agents, partialClient, dispatcher, "m", root,
                    historyProvider: () => partialHistory)
                .RunAsync("explore", "检查", null, CancellationToken.None, fork: true);
            var partialMessages = partialClient.Requests.Single().Messages;
            checks.Check(!partialMessages.Any(m => m.ToolCallId == "pair_a"), "组内部分结果不进入子请求");
            checks.Check(!partialMessages.Any(m => m.ToolCalls?.Any(c => c.Id == "pair_b") == true),
                "不完整的 assistant tool_call 整体被丢弃");
            checks.Check(partialMessages.Any(m => m.Content == "旧结论"), "不完整组之前的完整历史仍保留");

            var many = new List<PureAgentMessage>();
            for (var i = 0; i < 35; i++) many.Add(new PureAgentMessage(PureAgentRole.User, "m" + i));
            var manyClient = new CapturingClient("ok");
            await new SubagentRunner(agents, manyClient, dispatcher, "m", root, historyProvider: () => many)
                .RunAsync("explore", "x", null, CancellationToken.None, fork: true);
            var manyKept = manyClient.Requests.Single().Messages
                .Where(m => m.Role != PureAgentRole.System && m.Content != "x").ToArray();
            checks.Check(manyKept.Length == 30 && manyKept[0].Content == "m5" && manyKept[^1].Content == "m34",
                "超过 30 条时从最旧完整组开始丢弃");

            var bigCall = new PureToolCall("big_1", "read_file",
                "{\"path\":\"" + new string('x', 60_000) + "\"}");
            var bigHistory = new List<PureAgentMessage>
            {
                new(PureAgentRole.User, "最旧小消息"),
                new(PureAgentRole.Assistant, string.Empty, ToolCalls: new[] { bigCall }),
                new(PureAgentRole.Tool, new string('y', 60_000), bigCall.Id),
                new(PureAgentRole.User, "最新小消息"),
            };
            var bigClient = new CapturingClient("ok");
            await new SubagentRunner(agents, bigClient, dispatcher, "m", root, historyProvider: () => bigHistory)
                .RunAsync("explore", "x", null, CancellationToken.None, fork: true);
            var bigKept = bigClient.Requests.Single().Messages;
            checks.Check(bigKept.Any(m => m.Content == "最新小消息"), "预算内最新消息保留");
            checks.Check(bigKept.All(m => m.Content != "最旧小消息"),
                "遇到超预算组后停止继续向前截取（沿用原预算规则）");
            checks.Check(!bigKept.Any(m => m.ToolCallId == "big_1") &&
                         !bigKept.Any(m => m.ToolCalls?.Any(c => c.Id == "big_1") == true),
                "超预算 tool 组整体丢弃，不留下半组");
        }
        finally
        {
            TestTemp.Delete(root);
        }
    }

    private sealed class CapturingClient(string text) : IPureModelClient
    {
        public List<PureModelRequest> Requests { get; } = new();

        public Task<PureModelResponse> GenerateAsync(PureModelRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(new PureModelRequest(request.Messages.ToArray(), request.ToolNames.ToArray(),
                request.ModelId, request.ToolDefinitions, request.ReasoningEffort));
            return Task.FromResult(PureModelResponse.FromText(text));
        }
    }

    private sealed class NoopDispatcher : IPureToolDispatcher
    {
        public Task<PureToolResult> DispatchAsync(PureToolCall call,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new PureToolResult(call.Id, true, "noop"));
    }
}
