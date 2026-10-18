using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Fufucode.Core.Backend;

namespace Fufucode.App.Backend.Tests;

/// <summary>
/// 可选的「真实端点」探针（不进默认套件，避免网络依赖与不确定耗时）：
///   dotnet run --project tests/Fufucode.App.Backend.Tests -- --live-sse &lt;baseUrl&gt; [model] [apiKey]
/// 对真实 OpenAI 兼容网关发起 stream=true 请求，验证增量分片真正到达回调，
/// 并驱动一次完整的多轮 Agent Loop（真实工具 + 结果回填）。
/// 本探针只读/只写临时目录，不改动仓库文件。
/// </summary>
internal static class LiveSseProbe
{
    public static async Task<int> RunAsync(string baseUrl, string model, string? apiKey)
    {
        Console.WriteLine($"[live-sse] endpoint={baseUrl} model={model}");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        var client = new OpenAiCompatibleModelClient(baseUrl, apiKey, model, http);

        var deltas = new List<string>();
        var chunks = new List<string>();
        var firstDeltaAt = TimeSpan.Zero;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var callbacks = new ModelStreamCallbacks
        {
            OnTextDelta = delta =>
            {
                if (deltas.Count == 0) firstDeltaAt = stopwatch.Elapsed;
                deltas.Add(delta);
                Console.Write(delta);
            },
            OnToolCallChunk = (id, name, args) => chunks.Add($"{id}|{name}|{args}"),
        };

        // 1. 纯文本流式（不提供工具，避免模型自发调用工具）
        var request = new PureModelRequest(
            new[]
            {
                new PureAgentMessage(PureAgentRole.System, "You are a terse assistant. Reply in one short sentence."),
                new PureAgentMessage(PureAgentRole.User, "Say hello and name the number 7."),
            },
            Array.Empty<string>(),
            model);

        StreamedCompletion completion;
        try
        {
            completion = await client.StreamAsync(request, callbacks);
        }
        catch (ModelClientException error)
        {
            Console.Error.WriteLine($"[live-sse] FAILED kind={error.Kind} status={error.StatusCode} " +
                                    $"code={error.ErrorCode} message={error.Message}");
            return 2;
        }

        Console.WriteLine();
        var text = completion.Primary?.Text ?? "";
        Console.WriteLine($"[live-sse] textDeliveries={deltas.Count} " +
                          $"textLength={text.Length} sawDone={completion.SawDone} " +
                          $"firstDeltaAfter={firstDeltaAt.TotalMilliseconds:F0}ms " +
                          $"total={stopwatch.ElapsedMilliseconds}ms model={completion.Model}");

        var ok = true;
        void Expect(bool condition, string name)
        {
            Console.WriteLine((condition ? "PASS " : "FAIL ") + "[live-sse] " + name);
            if (!condition) ok = false;
        }

        Expect(deltas.Count > 0, "真实端点回传了至少一个 SSE 增量");
        Expect(text.Length > 0, "聚合文本非空");
        Expect(completion.SawDone, "真实端点回传 data: [DONE]");
        Expect(deltas.Count >= 1 && string.Concat(deltas) == text, "增量顺序拼接等于聚合文本");

        // 2. 真实多轮 Agent Loop：要求模型读一个真实文件，然后基于工具结果回答
        var workspace = Path.Combine(Path.GetTempPath(), "cs2-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            const string marker = "CS2-LIVE-MARKER-9182";
            await File.WriteAllTextAsync(Path.Combine(workspace, "cs2-live.txt"), marker);

            var dispatcher = new ProductionToolDispatcher(workspace);
            var loop = new NativeAgentLoop(client, dispatcher, maxIterations: 4);
            var history = new List<PureAgentMessage>
            {
                new(PureAgentRole.System,
                    "You are an agent with tools. You must call read_file to read cs2-live.txt. " +
                    "Then reply with exactly the file content and nothing else."),
                new(PureAgentRole.User, "Read the file cs2-live.txt and tell me its exact content."),
            };

            var loopDeltas = new List<string>();
            var loopResult = await loop.ExecuteLoopAsync(
                history,
                new[] { "read_file", "list_dir" },
                model,
                null,
                new ModelStreamCallbacks { OnTextDelta = loopDeltas.Add });

            Console.WriteLine($"[live-sse] loop status={loopResult.Status} " +
                              $"modelCalls={loop.CompletedModelCalls} toolResults={loopResult.ToolResults.Count}");
            foreach (var tool in loopResult.ToolResults)
            {
                Console.WriteLine($"[live-sse]   tool {tool.Id} success={tool.Success} " +
                                  $"content={tool.Content.Replace('\n', ' ')}");
            }
            Console.WriteLine($"[live-sse] loop final text: {loopResult.Response.Text}");

            Expect(loopResult.Status is PureAgentTurnStatus.Completed, "真实多轮循环走到终态");
            Expect(loop.CompletedModelCalls >= 2, "真实多轮循环至少请求模型 2 次（含工具回填后）");
            Expect(loopResult.ToolResults.Count >= 1, "真实端点成功发起工具调用并被调度");
            Expect(System.Linq.Enumerable.Any(loopResult.ToolResults, r => r.Success && r.Content.Contains(marker)),
                "工具真实读到工作区文件内容");
            Expect(loopDeltas.Count > 0, "多轮循环中的流式增量同样到达回调");
            Expect(history.Exists(m => m.Role == PureAgentRole.Tool),
                "工具结果以 Tool 角色回填进上下文");
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }

        // 3. 错误映射探针：故意打错的端点，验证真实 HTTP 失败被结构化（而非静默）
        using var badHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var badClient = new OpenAiCompatibleModelClient(baseUrl + "/definitely-not-a-real-path", "invalid-key-test",
            model, badHttp);
        try
        {
            await badClient.GenerateStreamingAsync(new PureModelRequest(
                new[] { new PureAgentMessage(PureAgentRole.User, "hi") }, Array.Empty<string>(), model));
            Expect(false, "错误端点应当抛出 ModelClientException");
        }
        catch (ModelClientException error)
        {
            Console.WriteLine($"[live-sse] error mapping: kind={error.Kind} status={error.StatusCode} code={error.ErrorCode}");
            Expect(error.StatusCode is not null, "真实失败被映射为带状态码的结构化异常");
        }

        Console.WriteLine(ok ? "[live-sse] RESULT: PASS" : "[live-sse] RESULT: FAIL");
        return ok ? 0 : 1;
    }
}
