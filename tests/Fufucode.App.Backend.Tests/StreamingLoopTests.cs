using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Fufucode.Core.Backend;

namespace Fufucode.App.Backend.Tests;

/// <summary>
/// NativeAgentLoop 在流式客户端下的多轮调度稳定性：
/// 每轮走 SSE 流式路径、工具结果回填后再次请求模型、终态收敛与有界停止。
/// </summary>
internal static class StreamingLoopTests
{
    public static async Task RunAsync(Checks checks)
    {
        await MultiRoundToolLoopAsync(checks);
        await MultiToolParallelRoundAsync(checks);
        await BoundedStopAsync(checks);
        await StreamingFailureSurfaceAsync(checks);
        await CallbackAbsenceIsSafeAsync(checks);
        await RealClientEndToEndLoopAsync(checks);
        await CancellationMidStreamAsync(checks);
    }

    /// <summary>第 1 轮流式返回工具调用分片；第 2 轮流式返回终态文本。历史必须完整回填。</summary>
    private static async Task MultiRoundToolLoopAsync(Checks checks)
    {
        var client = new SseReplayModelClient()
            .EnqueueTurn(
                Sse.Frame("{\"choices\":[{\"index\":0,\"delta\":{\"content\":\"先读取文件\"}}]}"),
                Sse.Frame("{\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"c1\",\"function\":{\"name\":\"read_file\",\"arguments\":\"{\\\"pa\"}}]}}]}"),
                Sse.Frame("{\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"th\\\":\\\"note.txt\\\"}\"}}]},\"finish_reason\":\"tool_calls\"}]}"),
                Sse.Done)
            .EnqueueTurn(
                Sse.Frame("{\"choices\":[{\"index\":0,\"delta\":{\"content\":\"文件内容为 \"}}]}"),
                Sse.Frame("{\"choices\":[{\"index\":0,\"delta\":{\"content\":\"streamed-loop-ok\"}}]}"),
                Sse.Frame("{\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}"),
                Sse.Done);

        var workspace = Path.Combine(Path.GetTempPath(), "cs2-loop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(workspace, "note.txt"), "streamed-loop-ok");
            var dispatcher = new ProductionToolDispatcher(workspace);
            var loop = new NativeAgentLoop(client, dispatcher, maxIterations: 5);

            var history = new List<PureAgentMessage>
            {
                new(PureAgentRole.System, "你是助手。"),
                new(PureAgentRole.User, "请读取 note.txt。"),
            };

            var callbacks = client.RecordingCallbacks();
            var appended = new List<PureAgentMessage>();
            var result = await loop.ExecuteLoopAsync(
                history,
                new[] { "read_file" },
                "deepseek-chat",
                appended.Add,
                callbacks);

            checks.Equal(PureAgentTurnStatus.Completed, result.Status, "流式多轮循环达到 Completed");
            checks.Equal(0, client.NonStreamingCalls, "循环全程未回退到一次性请求");
            checks.Equal(2, client.StreamingCalls, "循环执行了 2 次流式模型请求");
            checks.Equal(2, loop.CompletedModelCalls, "循环记录 2 次完成的模型请求");
            checks.Check(loop.StreamingEnabled, "循环识别客户端支持流式并启用流式路径");

            checks.Equal(1, result.ToolResults.Count, "第 1 轮工具调用被真实调度");
            checks.Check(result.ToolResults[0].Success, "工具执行成功");
            checks.Equal("streamed-loop-ok", result.ToolResults[0].Content, "工具读取到的内容来自流式拼装的参数");
            checks.Equal("文件内容为 streamed-loop-ok", result.Response.Text, "第 2 轮终态文本由流式分片聚合");

            // 流式分片回调证据：第 1 轮工具分片 + 第 2 轮文本分片都真实派发
            checks.Equal(3, client.TextDeltas.Count, "跨两轮共派发 3 个文本分片（第 1 轮 1 个 + 第 2 轮 2 个）");
            checks.Equal("先读取文件", client.TextDeltas[0], "第 1 轮文本分片先于工具调用");
            checks.Equal("文件内容为 ", client.TextDeltas[1], "第 2 轮文本分片");
            checks.Equal("streamed-loop-ok", client.TextDeltas[2], "第 2 轮后续文本分片按序派发");
            checks.Equal(2, client.ToolChunkEvents.Count, "工具参数分片按 2 次回调派发（与报文分片数一致）");
            checks.Equal("c1|read_file|{\"pa", client.ToolChunkEvents[0], "首片携带 id/name 与 arguments 前缀");
            checks.Equal("||th\":\"note.txt\"}", client.ToolChunkEvents[1], "末片只携带 arguments 增量");
            checks.Equal(2, client.ToolChunkIndexedEvents.Count, "带索引回调与约定回调分片数一致");

            // 历史结构：System, User, Assistant(工具轮), Tool 回填, Assistant(终态)
            checks.Equal(5, history.Count, "循环结束后历史包含 5 条消息");
            checks.Equal(PureAgentRole.Assistant, history[2].Role, "第 3 条为带工具调用的 Assistant");
            checks.Equal(1, history[2].ToolCalls?.Count ?? 0, "Assistant 消息携带 1 个工具调用");
            checks.Equal("{\"path\":\"note.txt\"}", history[2].ToolCalls![0].ArgumentsJson, "历史中的工具参数为完整拼装结果");
            checks.Equal(PureAgentRole.Tool, history[3].Role, "第 4 条为工具结果回填");
            checks.Equal("c1", history[3].ToolCallId, "工具结果通过 tool_call_id 精确关联");
            // 宿主只收到「循环本轮新增」的 3 条（System/User 由调用方预先放入历史，不属于新增）。
            checks.Equal(3, appended.Count, "循环新增的 3 条消息都通过回调通知宿主");
            checks.Equal(PureAgentRole.Assistant, appended[0].Role, "首个通知为带工具调用的 Assistant");
            checks.Equal(PureAgentRole.Tool, appended[1].Role, "第二个通知为工具回填结果");
            checks.Equal("文件内容为 streamed-loop-ok", appended[2].Content, "第三个通知为流式聚合的终态回复");

            // 第 2 次请求必须携带工具结果，证明不是只调用一次就返回
            var secondRequest = client.Requests[1];
            checks.Check(secondRequest.Messages.Any(m => m.Role == PureAgentRole.Tool &&
                m.Content == "streamed-loop-ok"), "第 2 次模型请求携带真实工具结果");
            checks.Equal("deepseek-chat", secondRequest.ModelId, "模型标识贯穿多轮请求");
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    /// <summary>单轮内多个工具调用并行分片，全部回填后进入下一轮。</summary>
    private static async Task MultiToolParallelRoundAsync(Checks checks)
    {
        var client = new SseReplayModelClient()
            .EnqueueTurn(
                Sse.Frame("{\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"a1\",\"function\":{\"name\":\"list_dir\",\"arguments\":\"{\\\"path\\\":\\\".\\\"}\"}}]}}]}"),
                Sse.Frame("{\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":1,\"id\":\"b2\",\"function\":{\"name\":\"read_file\",\"arguments\":\"{\\\"path\\\":\\\"a.txt\\\"}\"}}]}}]}"),
                Sse.Done)
            .EnqueueTurn(Sse.Frame("{\"choices\":[{\"index\":0,\"delta\":{\"content\":\"两个工具都完成\"}}]}"), Sse.Done);

        var workspace = Path.Combine(Path.GetTempPath(), "cs2-loop2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(workspace, "a.txt"), "alpha");
            var dispatcher = new ProductionToolDispatcher(workspace);
            var loop = new NativeAgentLoop(client, dispatcher, maxIterations: 5);
            var history = new List<PureAgentMessage> { new(PureAgentRole.User, "都看一下") };

            var result = await loop.ExecuteLoopAsync(history, new[] { "list_dir", "read_file" }, null,
                null, client.RecordingCallbacks());

            checks.Equal(PureAgentTurnStatus.Completed, result.Status, "多工具轮次收敛到 Completed");
            checks.Equal(2, result.ToolResults.Count, "两个工具调用都被调度");
            // 历史顺序：User(0) → Assistant(1, 两个工具调用) → Tool(2) → Tool(3) → Assistant(4 终态)
            checks.Equal("list_dir", history[1].ToolCalls![0].Name, "首个工具名来自分片拼装");
            checks.Equal("read_file", history[1].ToolCalls![1].Name, "第二个工具名来自另一分片");
            checks.Equal("a1", history[2].ToolCallId, "首个工具结果按 id 关联");
            checks.Equal("b2", history[3].ToolCallId, "第二个工具结果按 id 关联");
            checks.Equal("alpha", result.ToolResults[1].Content, "第二个工具读取到真实文件内容");
            checks.Check(result.ToolResults[0].Content.Contains("a.txt"), "目录列表中包含目标文件");
            checks.Equal(5, history.Count, "两条工具回填后共 5 条历史消息");
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    /// <summary>模型每轮都请求工具：循环必须在最大轮次处安全停止，不无限自转。</summary>
    private static async Task BoundedStopAsync(Checks checks)
    {
        var client = new SseReplayModelClient();
        for (var i = 0; i < 6; i++)
        {
            client.EnqueueTurn(
                Sse.Frame($"{{\"choices\":[{{\"index\":0,\"delta\":{{\"tool_calls\":[{{\"index\":0,\"id\":\"loop{i}\",\"function\":{{\"name\":\"list_dir\",\"arguments\":\"{{}}\"}}}}]}}}}]}}"),
                Sse.Done);
        }

        var workspace = Path.Combine(Path.GetTempPath(), "cs2-bounded-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            var dispatcher = new ProductionToolDispatcher(workspace);
            var loop = new NativeAgentLoop(client, dispatcher, maxIterations: 3);
            var history = new List<PureAgentMessage> { new(PureAgentRole.User, "一直调用工具") };

            var result = await loop.ExecuteLoopAsync(history, new[] { "list_dir" }, null);

            checks.Equal(PureAgentTurnStatus.Completed, result.Status, "达到最大轮次后按安全终止返回 Completed");
            checks.Equal(3, client.StreamingCalls, "最多只请求 3 轮，不再继续自转");
            checks.Equal(3, result.ToolResults.Count, "每轮工具结果都被记录");
            checks.Check(result.Response.Text!.Contains("最大轮次限制"), "终止响应明确说明轮次上限");
            checks.Equal(PureAgentRole.Assistant, history[^1].Role, "历史末条为终端提示的 Assistant 消息");
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    /// <summary>流式失败必须回落为 Failed 终态并携带原因，而不是抛给宿主或静默成功。</summary>
    private static async Task StreamingFailureSurfaceAsync(Checks checks)
    {
        var frames =
            Sse.Frame("{\"choices\":[{\"index\":0,\"delta\":{\"content\":\"partial\"}}]}") +
            Sse.Frame("{\"error\":{\"message\":\"upstream 500\",\"code\":\"server_error\"}}") +
            Sse.Done;
        var handler = new ScriptedHttpMessageHandler((_, _) =>
            ScriptedHttpMessageHandler.SseResponse(Sse.Split(frames, 6)));
        var client = new OpenAiCompatibleModelClient("https://api.example.com/v1", "sk-test", defaultModelId: "fixture-model",
            httpClient: new HttpClient(handler));

        var loop = new NativeAgentLoop(client, new ProductionToolDispatcher(Path.GetTempPath()), maxIterations: 3);
        var history = new List<PureAgentMessage> { new(PureAgentRole.User, "触发流错误") };
        var deltas = new List<string>();

        var result = await loop.ExecuteLoopAsync(history, Array.Empty<string>(), null, null,
            new ModelStreamCallbacks { OnTextDelta = deltas.Add });

        checks.Equal(PureAgentTurnStatus.Failed, result.Status, "流内错误使循环进入 Failed 终态");
        checks.Check(result.Error?.Contains("upstream 500") == true, "失败结果携带服务端 error.message");
        checks.Equal(0, loop.CompletedModelCalls, "失败的模型请求不计入完成次数");
        checks.Equal(1, deltas.Count, "失败前已派发的分片仍送达回调");
        checks.Equal(0, result.ToolResults.Count, "失败时没有工具结果");
    }

    /// <summary>流式回调为 null 时不能崩溃（宿主可能只关心最终答复）。</summary>
    private static async Task CallbackAbsenceIsSafeAsync(Checks checks)
    {
        var client = new SseReplayModelClient()
            .EnqueueTurn(Sse.Frame("{\"choices\":[{\"index\":0,\"delta\":{\"content\":\"无回调也正常\"}}]}"), Sse.Done);
        var loop = new NativeAgentLoop(client, null, maxIterations: 2);
        var history = new List<PureAgentMessage> { new(PureAgentRole.User, "hi") };

        var result = await loop.ExecuteLoopAsync(history, Array.Empty<string>(), null, null, null);
        checks.Equal(PureAgentTurnStatus.Completed, result.Status, "无流式回调时循环正常完成");
        checks.Equal("无回调也正常", result.Response.Text, "无回调时仍返回聚合文本");
    }

    /// <summary>
    /// 真实 OpenAiCompatibleModelClient（脚本化 HTTP 回放真实 SSE 字节）驱动完整 Agent Loop：
    /// 这是「真实客户端 + 真实调度器 + 多轮回填」的端到端证据，仅替换网络传输层。
    /// </summary>
    private static async Task RealClientEndToEndLoopAsync(Checks checks)
    {
        var turn1 =
            Sse.Frame("{\"choices\":[{\"index\":0,\"delta\":{\"content\":\"我来读文件\"}}]}") +
            Sse.Frame("{\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"e2e\",\"function\":{\"name\":\"read_file\",\"arguments\":\"{\\\"path\\\":\\\"e2e.txt\\\"}\"}}]},\"finish_reason\":\"tool_calls\"}]}") +
            Sse.Done;
        var turn2 =
            Sse.Frame("{\"choices\":[{\"index\":0,\"delta\":{\"content\":\"内容是 e2e-payload\"}}]}") +
            Sse.Frame("{\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}") +
            Sse.Done;

        var scripts = new Queue<string>(new[] { turn1, turn2 });
        var requestCount = 0;
        var handler = new ScriptedHttpMessageHandler((_, _) =>
        {
            var current = scripts.Count > 0 ? scripts.Dequeue() : Sse.Done;
            requestCount++;
            return ScriptedHttpMessageHandler.SseResponse(Sse.Split(current, 13));
        });

        var client = new OpenAiCompatibleModelClient("https://gateway.example.com/v1", "sk-live", defaultModelId: "fixture-model",
            httpClient: new HttpClient(handler));

        var workspace = Path.Combine(Path.GetTempPath(), "cs2-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(workspace, "e2e.txt"), "e2e-payload");
            var dispatcher = new ProductionToolDispatcher(workspace);
            var loop = new NativeAgentLoop(client, dispatcher, maxIterations: 4);
            var history = new List<PureAgentMessage> { new(PureAgentRole.User, "读取 e2e.txt") };

            var deltas = new List<string>();
            var result = await loop.ExecuteLoopAsync(history, new[] { "read_file" }, "gpt-4o", null,
                new ModelStreamCallbacks { OnTextDelta = deltas.Add });

            checks.Equal(PureAgentTurnStatus.Completed, result.Status, "真实客户端端到端循环 Completed");
            checks.Equal(2, requestCount, "端到端循环发出 2 次 HTTP 请求");
            checks.Equal(1, result.ToolResults.Count, "端到端循环执行 1 次真实工具");
            checks.Equal("e2e-payload", result.ToolResults[0].Content, "工具读取到脚本化工作区中的真实文件");
            checks.Equal("内容是 e2e-payload", result.Response.Text, "终态文本由第二轮流式分片聚合");
            checks.Equal(2, deltas.Count, "两轮各派发一个文本分片");

            // 每次请求都必须带 stream=true，且首次请求包含工具 schema
            checks.Equal(2, handler.RequestBodies.Count, "处理器记录到 2 个请求体");
            using var first = System.Text.Json.JsonDocument.Parse(handler.RequestBodies[0]);
            checks.Check(first.RootElement.GetProperty("stream").GetBoolean(), "首次请求 stream=true");
            checks.Equal("gpt-4o", first.RootElement.GetProperty("model").GetString(), "首次请求使用指定模型");
            using var second = System.Text.Json.JsonDocument.Parse(handler.RequestBodies[1]);
            checks.Check(second.RootElement.GetProperty("stream").GetBoolean(), "回填后的第二轮请求仍为 stream=true");
            checks.Check(second.RootElement.GetProperty("messages").GetArrayLength() >= 3,
                "第二轮请求携带工具回填后的完整上下文");
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    /// <summary>流式读取中途取消：必须抛 OperationCanceledException，不被包装成普通失败。</summary>
    private static async Task CancellationMidStreamAsync(Checks checks)
    {
        using var cts = new CancellationTokenSource();
        var client = new CancelMidStreamClient(cts);
        var loop = new NativeAgentLoop(client, null, maxIterations: 3);
        var history = new List<PureAgentMessage> { new(PureAgentRole.User, "hi") };

        var cancelled = false;
        try
        {
            await loop.ExecuteLoopAsync(history, Array.Empty<string>(), null, null, null, cts.Token);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }

        checks.Check(cancelled, "流式读取中途取消向上抛出 OperationCanceledException");
        checks.Equal(0, loop.CompletedModelCalls, "取消的模型请求不计入完成次数");
    }

    /// <summary>取消夹具：派发一个文本分片后取消令牌，模拟用户在流中途点击停止。</summary>
    private sealed class CancelMidStreamClient(CancellationTokenSource cts) : IPureModelClient, IStreamingModelClient
    {
        public Task<PureModelResponse> GenerateAsync(PureModelRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("本夹具仅提供流式路径。");

        public async Task<PureModelResponse> GenerateStreamingAsync(
            PureModelRequest request,
            ModelStreamCallbacks? callbacks = null,
            CancellationToken cancellationToken = default)
        {
            var frames =
                Sse.Frame("{\"choices\":[{\"index\":0,\"delta\":{\"content\":\"第一段\"}}]}") +
                Sse.Frame("{\"choices\":[{\"index\":0,\"delta\":{\"content\":\"第二段\"}}]}") +
                Sse.Done;
            var stream = new ScriptedByteStream(Sse.Split(frames, 4));
            var parser = new ChatCompletionSseParser();
            var seen = 0;

            await foreach (var sse in ServerSentEvents.ReadAsync(stream, cancellationToken))
            {
                if (sse.IsDone) break;
                parser.TryApply(sse, callbacks);
                if (++seen == 1) cts.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }

            return parser.Completion.ToResponse();
        }
    }
}
