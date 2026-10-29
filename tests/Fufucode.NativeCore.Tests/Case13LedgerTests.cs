using System.Text.Json;
using Fufucode.Core.Backend;

namespace Fufucode.NativeCore.Tests;

/// <summary>
/// Case 13: real PureCSharpAgentEngine RunTurnAsync drives compaction; the deterministic ledger
/// must bind exit codes by ToolCallId per batch, never borrowing a sibling's or a later round's code.
/// </summary>
internal static class Case13LedgerTests
{
    private const string Denied = "<tool_use_error>用户拒绝了本次工具调用。</tool_use_error>";
    private const string StartFailed = "<tool_use_error>启动失败: executable not found</tool_use_error>";

    public static async Task RunAsync(Checks checks)
    {
        await RunScenarioAsync(checks, "拒绝A成功B",
            firstBatch: new[] { Call("deny", "A"), Call("ok", "B") },
            secondBatch: new[] { Call("warm-1", "warm-1") },
            results: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["A"] = Denied,
                ["B"] = "ExitCode: 0\ncommand completed",
            },
            expectedLines: new[] { "- `A` → ExitCode 未知", "- `B` → ExitCode 0" },
            forbiddenLines: new[] { "- `A` → ExitCode 0" });

        await RunScenarioAsync(checks, "启动失败A成功B",
            firstBatch: new[] { Call("a", "A"), Call("b", "B") },
            secondBatch: new[] { Call("warm-2", "warm-2") },
            results: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["A"] = StartFailed,
                ["B"] = "ExitCode: 0\ncommand completed",
            },
            expectedLines: new[] { "- `A` → ExitCode 未知", "- `B` → ExitCode 0" },
            forbiddenLines: new[] { "- `A` → ExitCode 0" });

        await RunScenarioAsync(checks, "多轮重复ToolCallId",
            firstBatch: new[] { Call("dup", "old") },
            secondBatch: new[] { Call("dup", "new") },
            results: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["old"] = "ExitCode: 3\nold failed",
                ["new"] = Denied,
            },
            expectedLines: new[] { "- `old` → ExitCode 3", "- `new` → ExitCode 未知" },
            forbiddenLines: new[] { "- `old` → ExitCode 未知", "- `new` → ExitCode 3", "- `new` → ExitCode 0" });
    }

    private static async Task RunScenarioAsync(
        Checks checks,
        string name,
        (string Id, string Command)[] firstBatch,
        (string Id, string Command)[] secondBatch,
        IReadOnlyDictionary<string, string> results,
        string[] expectedLines,
        string[] forbiddenLines)
    {
        var fillerBatch = Enumerable.Range(1, 5)
            .Select(i => Call($"fill-{i}", $"fill-{i}"))
            .ToArray();

        var client = new ScriptedClient(new[]
        {
            ToToolCalls(firstBatch),
            ToToolCalls(secondBatch),
            ToToolCalls(fillerBatch),
        });
        var dispatcher = new ScriptedDispatcher(results);
        var engine = new PureCSharpAgentEngine(dispatcher, client)
        {
            ContextLengthResolver = (_, _) => Task.FromResult<long?>(1_000),
        };

        var result = await engine.RunTurnAsync(new PureModelRequest(
            new[]
            {
                new PureAgentMessage(PureAgentRole.System, "你是编码 Agent。"),
                new PureAgentMessage(PureAgentRole.User, "执行一个需要多轮工具的长任务。"),
            },
            new[] { "run_command" }, "integration-model"));

        var ledger = client.Ledgers.LastOrDefault()
                     ?? engine.GetState().History?
                         .FirstOrDefault(m => m.Content.Contains("[压缩时保留的状态台账]", StringComparison.Ordinal))
                         ?.Content;

        checks.Check(result.Status == PureAgentTurnStatus.Completed, $"{name}: 回合未完成（{result.Status}）");
        checks.Check(client.SummaryCalls >= 1, $"{name}: 未触发压缩摘要调用");
        checks.Check(ledger is not null, $"{name}: 压缩后台账未进入任何模型请求或历史");
        if (ledger is null) return;

        foreach (var line in expectedLines)
            checks.Check(ledger.Split('\n').Select(l => l.TrimEnd('\r')).Contains(line, StringComparer.Ordinal),
                $"{name}: 台账缺少 `{line}`");
        foreach (var line in forbiddenLines)
            checks.Check(!ledger.Split('\n').Select(l => l.TrimEnd('\r')).Contains(line, StringComparer.Ordinal),
                $"{name}: 台账出现不应有的 `{line}`");
        checks.Check(engine.GetState().History!.Any(m =>
                m.Role == PureAgentRole.Assistant &&
                m.Content.Contains("[压缩时保留的状态台账]", StringComparison.Ordinal)),
            $"{name}: 真实引擎历史中没有保留台账消息");
    }

    private static PureToolCall[] ToToolCalls((string Id, string Command)[] calls) =>
        calls.Select(c => new PureToolCall(c.Id, "run_command",
            JsonSerializer.Serialize(new { command = c.Command }))).ToArray();

    private static (string Id, string Command) Call(string id, string command) => (id, command);

    private sealed class ScriptedClient(IReadOnlyList<PureToolCall[]> rounds) : IPureModelClient
    {
        private int _normalCalls;

        public int SummaryCalls { get; private set; }
        public List<string> Ledgers { get; } = new();

        public Task<PureModelResponse> GenerateAsync(PureModelRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Messages.Any(m => m.Role == PureAgentRole.System &&
                                          m.Content.Contains("上下文压缩器", StringComparison.Ordinal)))
            {
                SummaryCalls++;
                return Task.FromResult(PureModelResponse.FromText("## 1. 用户目标\n继续"));
            }

            foreach (var message in request.Messages)
            {
                if (message.Role == PureAgentRole.Assistant &&
                    message.Content.Contains("[压缩时保留的状态台账]", StringComparison.Ordinal) &&
                    !Ledgers.Contains(message.Content, StringComparer.Ordinal))
                    Ledgers.Add(message.Content);
            }

            var round = _normalCalls++;
            return Task.FromResult(round < rounds.Count
                ? PureModelResponse.FromToolCalls(rounds[round])
                : PureModelResponse.FromText("完成"));
        }
    }

    private sealed class ScriptedDispatcher(IReadOnlyDictionary<string, string> results) : IPureToolDispatcher
    {
        public Task<PureToolResult> DispatchAsync(PureToolCall call,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var command = ReadCommand(call.ArgumentsJson);
            var content = results.TryGetValue(command, out var scripted)
                ? scripted
                : "ExitCode: 0\n" + new string('x', 20_000);
            var success = !content.StartsWith("<tool_use_error>", StringComparison.Ordinal);
            return Task.FromResult(new PureToolResult(call.Id, success, content));
        }

        private static string ReadCommand(string argumentsJson)
        {
            using var document = JsonDocument.Parse(argumentsJson);
            return document.RootElement.GetProperty("command").GetString() ?? string.Empty;
        }
    }
}
