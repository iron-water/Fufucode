using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Fufucode.Core;
using Fufucode.Core.Backend;

namespace Fufucode.App.Backend.Tests;

/// <summary>
/// 真实端点的 Agent Loop 端到端探针（不进默认套件）。
///
/// 用途：验证 FUMO 原生内核在本机网关（如 Magpie 127.0.0.1:3425）上真实完成
/// "模型请求 → 工具调度 → 结果回填 → 再次请求" 的多轮循环。
///
/// 用法：
///   dotnet run --project tests/Fufucode.App.Backend.Tests -- --live-agent-loop \
///       http://127.0.0.1:3425/v1 group/auto-glm-5-3-flash [apiKey]
/// </summary>
internal static class LiveAgentLoopProbe
{
    public static async Task<int> RunAsync(string baseUrl, string model, string? apiKey)
    {
        var workspace = Path.Combine(Path.GetTempPath(), "fumo-live-loop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(workspace, "hello.txt"), "FUMO_LOOP_MARKER_42");

            Console.WriteLine($"baseUrl : {baseUrl}");
            Console.WriteLine($"model   : {model}");
            Console.WriteLine($"workspace: {workspace}");
            Console.WriteLine();

            var client = new OpenAiCompatibleModelClient(baseUrl, apiKey ?? "local", model);
            var dispatcher = new ProductionToolDispatcher(workspace);
            var loop = new NativeAgentLoop(client, dispatcher, maxIterations: 6);

            var history = new List<PureAgentMessage>
            {
                new(PureAgentRole.User,
                    "读取 hello.txt 的内容，然后创建 result.txt，内容正好是 hello.txt 的内容加上 '-done'。完成后一句话报告。")
            };

            var tools = new[] { "read_file", "write_file", "edit_file", "list_dir", "grep_search", "glob_files", "run_command" };
            var toolDefs = ProductionToolDispatcher.GetBuiltinToolDefinitions();

            var result = await loop.ExecuteLoopAsync(history,
                tools, model,
                onMessageAppended: message => Console.WriteLine($"[{message.Role}] {Truncate(message.Content)}"),
                streamCallbacks: null,
                cancellationToken: CancellationToken.None,
                onToolStarting: call => Console.WriteLine($"  -> {call.Name} {Truncate(call.ArgumentsJson, 120)}"),
                onToolCompleted: (call, toolResult) =>
                    Console.WriteLine($"  <- {(toolResult.Success ? "ok" : "FAIL")} {Truncate(toolResult.Content, 120)}"),
                toolDefinitions: toolDefs);

            Console.WriteLine();
            Console.WriteLine($"loop status : {result.Status}");
            Console.WriteLine($"model calls : {loop.CompletedModelCalls}");
            Console.WriteLine($"tool results: {result.ToolResults.Count}");
            if (result.Error is { } error) Console.WriteLine($"error       : {error}");

            var resultPath = Path.Combine(workspace, "result.txt");
            var content = File.Exists(resultPath) ? await File.ReadAllTextAsync(resultPath) : null;
            Console.WriteLine($"result.txt  : {content ?? "<missing>"}");

            var turn1Passed = result.Status == PureAgentTurnStatus.Completed
                         && loop.CompletedModelCalls >= 2
                         && result.ToolResults.Count >= 2
                         && content == "FUMO_LOOP_MARKER_42-done";

            // 第二轮实测：验证 edit_file 精确差量替换
            Console.WriteLine("\n--- Turn 2: 测试 edit_file 精准差量替换 ---");
            history.Add(new PureAgentMessage(PureAgentRole.User,
                "使用 edit_file 工具把 result.txt 中的 '-done' 精确替换为 '-edited-by-deepseek'。完成后简述。"));

            var editResult = await loop.ExecuteLoopAsync(history,
                tools, model,
                onMessageAppended: message => Console.WriteLine($"[{message.Role}] {Truncate(message.Content)}"),
                streamCallbacks: null,
                cancellationToken: CancellationToken.None,
                onToolStarting: call => Console.WriteLine($"  -> {call.Name} {Truncate(call.ArgumentsJson, 120)}"),
                onToolCompleted: (call, toolResult) =>
                    Console.WriteLine($"  <- {(toolResult.Success ? "ok" : "FAIL")} {Truncate(toolResult.Content, 120)}"),
                toolDefinitions: toolDefs);

            var editedContent = File.Exists(resultPath) ? await File.ReadAllTextAsync(resultPath) : null;
            Console.WriteLine($"edited result.txt : {editedContent ?? "<missing>"}");

            var turn2Passed = editResult.Status == PureAgentTurnStatus.Completed
                              && editedContent == "FUMO_LOOP_MARKER_42-edited-by-deepseek";

            var passed = turn1Passed && turn2Passed;
            Console.WriteLine(passed ? "\nLIVE AGENT LOOP PASS (Turn 1 & 2 ALL GREEN)" : "\nLIVE AGENT LOOP FAIL");
            return passed ? 0 : 1;
        }
        finally
        {
            try { Directory.Delete(workspace, true); } catch { }
        }
    }

    private static string Truncate(string value, int length = 100) =>
        value.Length <= length ? value : value[..length] + "…";
}
