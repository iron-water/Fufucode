using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Fufucode.App.Abstractions;
using Fufucode.Core.Backend;

namespace Fufucode.App.Backend.Tests;

/// <summary>
/// 长程能力回归：回合内压缩、上下文超限恢复、瞬时失败重试、
/// 以及流式请求的 usage 声明。这些是长任务能否跑完的关键路径，
/// 不能用“最后再压缩一次”或“失败即终态”冒充完成。
/// </summary>
internal static class LongHorizonTests
{
    public static async Task RunAsync(Checks checks)
    {
        await MidLoopCompactionAsync(checks);
        await ContextOverflowRecoveryAsync(checks);
        await NonStreamingRetryAsync(checks);
        await StreamingRetryBeforeDeltaAsync(checks);
        await MidStreamRestartAsync(checks);
        await StreamErrorRetryAsync(checks);
        await NonStreamingFallbackAsync(checks);
        await HttpClientTimeoutIsRetryableAsync(checks);
        await StreamUsageFallbackAsync(checks);
        ContextOverflowClassification(checks);
        await NoFixedCharThresholdAsync(checks);
        await ProjectInstructionsAutoLoadedAsync(checks);
        RestoreRepairsToolPairing(checks);
    }

    private static async Task ProjectInstructionsAutoLoadedAsync(Checks checks)
    {
        var root = Path.Combine(Path.GetTempPath(), "fumo-agents-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "AGENTS.md"),
                "# 项目规则\n所有路径使用相对路径。\n测试命令：flutter test。");
            var engine = new PureCSharpAgentEngine(modelClient: new CapturingTextClient("ok"));
            await using var session = new NativeCSharpChatSession(
                engine, "agents-test", "m", workspaceRoot: root);
            var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            session.Event += (_, e) => { if (e is SessionEvent.TurnSettled) settled.TrySetResult(); };
            await session.SendAsync("hi");
            await settled.Task.WaitAsync(TimeSpan.FromSeconds(10));

            var system = engine.GetState().History!.First(m => m.Role == PureAgentRole.System).Content;
            checks.Check(system.Contains("自动加载自 AGENTS.md", StringComparison.Ordinal),
                "系统提示自动标注 AGENTS.md 来源");
            checks.Check(system.Contains("所有路径使用相对路径", StringComparison.Ordinal),
                "系统提示包含项目约定正文，模型不必先读一遍");

            File.WriteAllText(Path.Combine(root, "AGENTS.md"), new string('x', 40_000));
            var engine2 = new PureCSharpAgentEngine(modelClient: new CapturingTextClient("ok"));
            await using var session2 = new NativeCSharpChatSession(
                engine2, "agents-test-2", "m", workspaceRoot: root);
            var settled2 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            session2.Event += (_, e) => { if (e is SessionEvent.TurnSettled) settled2.TrySetResult(); };
            await session2.SendAsync("hi");
            await settled2.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var system2 = engine2.GetState().History!.First(m => m.Role == PureAgentRole.System).Content;
            checks.Check(system2.Contains("项目约定过长", StringComparison.Ordinal),
                "超大项目约定被截断并标注");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task NoFixedCharThresholdAsync(Checks checks)
    {
        // 窗口未知时不能用 400k 字符这种拍脑袋阈值裁剪正常历史；
        // 只有达到物理内存保护线才压缩。
        var engine = new PureCSharpAgentEngine(modelClient: new FixedTextClient("ok"));
        var history = new List<PureAgentMessage> { new(PureAgentRole.System, "系统") };
        for (var i = 0; i < 20; i++)
            history.Add(new PureAgentMessage(PureAgentRole.Assistant, new string('x', 20_000)));
        engine.RestoreHistory(history);

        await engine.RunTurnAsync(new PureModelRequest(
            new[] { new PureAgentMessage(PureAgentRole.User, "继续") }, Array.Empty<string>(), "m"));

        var after = engine.GetState().History!;
        checks.Check(!after.Any(m => m.Content.Contains("[上下文摘要", StringComparison.Ordinal)),
            "窗口未知时 400k 字符历史不会被固定阈值误压缩");
    }

    private static void RestoreRepairsToolPairing(Checks checks)
    {
        var engine = new PureCSharpAgentEngine(modelClient: new FixedTextClient("x"));
        var assistant = new PureAgentMessage(PureAgentRole.Assistant, "",
            ToolCalls: new[]
            {
                new PureToolCall("a", "read_file", "{\"path\":\"a\"}"),
                new PureToolCall("b", "read_file", "{\"path\":\"b\"}"),
            });
        engine.RestoreHistory(new[]
        {
            new PureAgentMessage(PureAgentRole.User, "读取两个文件"),
            assistant,
            new PureAgentMessage(PureAgentRole.Tool, "a 的内容", ToolCallId: "a"),
            new PureAgentMessage(PureAgentRole.Tool, "孤立结果", ToolCallId: "ghost"),
        });

        var repaired = engine.GetState().History!;
        checks.Check(repaired.Count(m => m.Role == PureAgentRole.Tool) == 2,
            "恢复历史移除孤立 tool_result 并补齐缺失结果");
        checks.Check(repaired.Any(m => m.ToolCallId == "b" &&
                                       m.Content.Contains("canceled before completion", StringComparison.Ordinal)),
            "缺失的 tool_call 得到可识别的合成结果");
        checks.Check(!repaired.Any(m => m.ToolCallId == "ghost"), "没有对应调用的工具结果被丢弃");

        var valid = new List<PureAgentMessage>
        {
            new(PureAgentRole.User, "读取文件"),
            assistant,
            new PureAgentMessage(PureAgentRole.Tool, "a", ToolCallId: "a"),
            new PureAgentMessage(PureAgentRole.Tool, "b", ToolCallId: "b"),
        };
        engine.RestoreHistory(valid);
        checks.Equal(4, engine.GetState().History!.Count, "配对完整的历史保持原样");
    }

    private sealed class CompactionScriptClient : IPureModelClient
    {
        private int _toolRounds;
        private readonly int _roundsBeforeFinal;

        public CompactionScriptClient(int roundsBeforeFinal) => _roundsBeforeFinal = roundsBeforeFinal;

        public int Requests { get; private set; }
        public int SummaryCalls { get; private set; }
        public bool SawCompactedHistory { get; private set; }

        public Task<PureModelResponse> GenerateAsync(PureModelRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests++;
            if (request.Messages.Any(m => m.Role == PureAgentRole.System &&
                                          m.Content.Contains("上下文压缩器", StringComparison.Ordinal)))
            {
                SummaryCalls++;
                return Task.FromResult(PureModelResponse.FromText("## 1. 用户目标\n继续"));
            }

            if (request.Messages.Any(m => m.Content.Contains("[上下文摘要", StringComparison.Ordinal)))
                SawCompactedHistory = true;

            if (_toolRounds++ < _roundsBeforeFinal)
            {
                return Task.FromResult(PureModelResponse.FromToolCalls(
                    new[] { new PureToolCall($"c{_toolRounds}", "run_command", "{\"command\":\"echo x\"}") }));
            }
            return Task.FromResult(PureModelResponse.FromText("完成"));
        }
    }

    private sealed class LargeOutputDispatcher : IPureToolDispatcher
    {
        public Task<PureToolResult> DispatchAsync(PureToolCall call,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new PureToolResult(call.Id, true, new string('x', 20_000)));
    }

    private static async Task MidLoopCompactionAsync(Checks checks)
    {
        var client = new CompactionScriptClient(roundsBeforeFinal: 6);
        var engine = new PureCSharpAgentEngine(new LargeOutputDispatcher(), client)
        {
            // 故意给一个很小的真实窗口：字符兜底阈值 = 1000 * 3。
            ContextLengthResolver = (_, _) => Task.FromResult<long?>(1_000),
        };

        var result = await engine.RunTurnAsync(new PureModelRequest(
            new[] { new PureAgentMessage(PureAgentRole.User, "执行一个需要多轮工具的长任务。") },
            new[] { "run_command" }, "test-model"));

        checks.Equal(PureAgentTurnStatus.Completed, result.Status, "长任务在同一回合内完成");
        checks.Check(client.SummaryCalls >= 1, "回合内超过窗口时触发压缩，而不是等到回合结束");
        checks.Check(client.SawCompactedHistory, "压缩后的请求确实包含摘要消息");

        // 压缩发生在工具轮之间：保留窗口不能留下没有 tool_call 的孤立结果。
        var history = engine.GetState().History!.ToList();
        var before = history.Count;
        PureCSharpAgentEngine.RepairToolCallPairing(history);
        checks.Equal(before, history.Count, "回合内压缩后 tool_call 与 tool_result 仍完整配对");
    }

    private sealed class OverflowThenSucceedClient : IPureModelClient
    {
        private int _calls;
        public int Calls => _calls;
        public int SummaryCalls { get; private set; }

        public Task<PureModelResponse> GenerateAsync(PureModelRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _calls++;
            if (request.Messages.Any(m => m.Role == PureAgentRole.System &&
                                          m.Content.Contains("上下文压缩器", StringComparison.Ordinal)))
            {
                SummaryCalls++;
                return Task.FromResult(PureModelResponse.FromText("## 1. 用户目标\n继续"));
            }
            if (_calls == 1)
            {
                throw new ModelClientException(ModelClientErrorKind.InvalidRequest,
                    "maximum context length exceeded", 400, "context_length_exceeded");
            }
            return Task.FromResult(PureModelResponse.FromText("恢复后完成"));
        }
    }

    private static async Task ContextOverflowRecoveryAsync(Checks checks)
    {
        var client = new OverflowThenSucceedClient();
        var engine = new PureCSharpAgentEngine(modelClient: client);
        var history = new List<PureAgentMessage>
        {
            new(PureAgentRole.System, "你是编码 Agent。"),
            new(PureAgentRole.User, "这是一个很长的任务。"),
        };
        for (var i = 0; i < 8; i++)
            history.Add(new PureAgentMessage(PureAgentRole.Assistant, $"第 {i} 步输出。"));
        engine.RestoreHistory(history);

        var result = await engine.RunTurnAsync(new PureModelRequest(
            new[] { new PureAgentMessage(PureAgentRole.User, "继续") },
            Array.Empty<string>(), "test-model"));

        checks.Equal(PureAgentTurnStatus.Completed, result.Status, "上下文超限后可压缩恢复并继续");
        checks.Equal(1, client.SummaryCalls, "超限恢复只压缩一次");
        checks.Check(client.Calls >= 3, "超限错误后确实重试了模型请求");
    }

    private static OpenAiCompatibleModelClient ClientWith(
        Func<HttpRequestMessage, string, HttpResponseMessage> responder,
        out ScriptedHttpMessageHandler handler)
    {
        handler = new ScriptedHttpMessageHandler(responder);
        return new OpenAiCompatibleModelClient("http://unit.test/v1", "k", "m",
            new HttpClient(handler));
    }

    private static async Task NonStreamingRetryAsync(Checks checks)
    {
        var calls = 0;
        var client = ClientWith((_, _) =>
        {
            calls++;
            return calls < 3
                ? ScriptedHttpMessageHandler.JsonError(HttpStatusCode.InternalServerError,
                    "{\"error\":{\"message\":\"boom\"}}")
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}",
                        Encoding.UTF8, "application/json")
                };
        }, out _);

        var response = await client.GenerateAsync(new PureModelRequest(
            new[] { new PureAgentMessage(PureAgentRole.User, "hi") }, Array.Empty<string>(), "m"));

        checks.Equal(3, calls, "5xx 在未产生副作用时按策略重试");
        checks.Equal("ok", response.Text, "重试成功后返回真实内容");
    }

    private static async Task StreamingRetryBeforeDeltaAsync(Checks checks)
    {
        var calls = 0;
        string? body = null;
        var client = ClientWith((_, requestBody) =>
        {
            calls++;
            body = requestBody;
            if (calls == 1)
                return ScriptedHttpMessageHandler.JsonError(HttpStatusCode.ServiceUnavailable,
                    "{\"error\":{\"message\":\"unavailable\"}}");
            var frame = "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"OK\"}}]}\n\n";
            return ScriptedHttpMessageHandler.SseResponse(
                Sse.Utf8(frame + Sse.Done));
        }, out _);

        var deltas = new List<string>();
        var response = await client.GenerateStreamingAsync(new PureModelRequest(
                new[] { new PureAgentMessage(PureAgentRole.User, "hi") }, Array.Empty<string>(), "m"),
            new ModelStreamCallbacks { OnTextDelta = delta => deltas.Add(delta) });

        checks.Equal(2, calls, "流式请求在首个增量前失败时可安全重试");
        checks.Equal("OK", response.Text, "流式重试后内容正确");
        checks.Check(deltas.Count == 1, "重试不会重复派发文本");
        checks.Check(body?.Contains("\"include_usage\":true", StringComparison.Ordinal) == true,
            "流式请求声明 include_usage，用量统计不靠猜");
    }

    /// <summary>先送出已缓冲的数据，随后模拟连接被重置。</summary>
    private sealed class ThrowingByteStream(byte[] first) : System.IO.Stream
    {
        private bool _sent;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_sent) throw new System.IO.IOException("connection reset by peer");
            _sent = true;
            first.AsMemory(0, Math.Min(first.Length, buffer.Length)).CopyTo(buffer);
            return ValueTask.FromResult(Math.Min(first.Length, buffer.Length));
        }

        public override void Flush() { }
        public override long Seek(long offset, System.IO.SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static async Task MidStreamRestartAsync(Checks checks)
    {
        var calls = 0;
        var client = ClientWith((_, _) =>
        {
            calls++;
            if (calls == 1)
            {
                var first = "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"PART\"}}]}\n\n";
                var content = new StreamContent(new ThrowingByteStream(Encoding.UTF8.GetBytes(first)));
                content.Headers.ContentType =
                    new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            }
            var full = "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"FULL\"}}]}\n\n";
            return ScriptedHttpMessageHandler.SseResponse(Sse.Utf8(full + Sse.Done));
        }, out _);

        var deltas = new List<string>();
        var restarts = 0;
        var response = await client.GenerateStreamingAsync(new PureModelRequest(
                new[] { new PureAgentMessage(PureAgentRole.User, "hi") }, Array.Empty<string>(), "m"),
            new ModelStreamCallbacks
            {
                OnTextDelta = delta => deltas.Add(delta),
                OnRestart = () => restarts++,
            });

        checks.Equal(2, calls, "流中断后重放同一请求");
        checks.Equal(1, restarts, "重放前通知 UI 丢弃半成品");
        checks.Equal("FULL", response.Text, "最终响应只包含重试后的完整内容");
        checks.Check(deltas.SequenceEqual(new[] { "PART", "FULL" }),
            "增量按“旧片段 → 重试片段”顺序派发，UI 可据此重置");
    }

    private static async Task StreamErrorRetryAsync(Checks checks)
    {
        var calls = 0;
        var client = ClientWith((_, _) =>
        {
            calls++;
            if (calls == 1)
            {
                var errorFrame =
                    "data: {\"error\":{\"message\":\"the reply broke off: ECONNRESET\"}}\n\n";
                return ScriptedHttpMessageHandler.SseResponse(Sse.Utf8(errorFrame));
            }
            var frame = "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"OK\"}}]}\n\n";
            return ScriptedHttpMessageHandler.SseResponse(Sse.Utf8(frame + Sse.Done));
        }, out _);

        var response = await client.GenerateStreamingAsync(new PureModelRequest(
            new[] { new PureAgentMessage(PureAgentRole.User, "hi") }, Array.Empty<string>(), "m"));

        checks.Equal(2, calls, "网关以 SSE error 帧报告上游断流时同样重试");
        checks.Equal("OK", response.Text, "SSE error 重试后返回真实内容");
    }

    private static async Task NonStreamingFallbackAsync(Checks checks)
    {
        var calls = 0;
        string? lastBody = null;
        var client = ClientWith((_, requestBody) =>
        {
            calls++;
            lastBody = requestBody;
            if (calls <= 3)
                return ScriptedHttpMessageHandler.JsonError(HttpStatusCode.BadGateway,
                    "{\"error\":{\"message\":\"upstream reset\"}}");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"choices\":[{\"message\":{\"content\":\"fallback ok\"},\"finish_reason\":\"stop\"}]}",
                    Encoding.UTF8, "application/json")
            };
        }, out _);

        var deltas = new List<string>();
        var response = await client.GenerateStreamingAsync(new PureModelRequest(
                new[] { new PureAgentMessage(PureAgentRole.User, "hi") }, Array.Empty<string>(), "m"),
            new ModelStreamCallbacks { OnTextDelta = delta => deltas.Add(delta) });

        checks.Equal(4, calls, "SSE 连续失败后改用一次性通道，而不是把长任务判死");
        checks.Check(lastBody?.Contains("\"stream\":false", StringComparison.Ordinal) == true,
            "降级请求确实是非流式");
        checks.Equal("fallback ok", response.Text, "降级路径返回真实模型结果");
        checks.Check(deltas.SequenceEqual(new[] { "fallback ok" }),
            "降级结果整块回放给 UI，不伪造分片");
    }

    private sealed class DelayingHandler(TimeSpan delay) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"late\"}}]}",
                    Encoding.UTF8, "application/json")
            };
        }
    }

    private static async Task HttpClientTimeoutIsRetryableAsync(Checks checks)
    {
        var http = new HttpClient(new DelayingHandler(TimeSpan.FromMilliseconds(400)))
        {
            Timeout = TimeSpan.FromMilliseconds(50),
        };
        var client = new OpenAiCompatibleModelClient("http://unit.test/v1", "k", "m", http)
        {
            MaxAttempts = 2,
        };

        var error = await checks.CapturesAsync<ModelClientException>(() =>
            client.GenerateAsync(new PureModelRequest(
                new[] { new PureAgentMessage(PureAgentRole.User, "hi") }, Array.Empty<string>(), "m")));

        checks.Check(error is not null && error.Kind == ModelClientErrorKind.Transport,
            "注入 HttpClient 的超时归类为可重试的传输失败，而不是用户取消");
    }

    private static async Task StreamUsageFallbackAsync(Checks checks)
    {
        var calls = 0;
        string? secondBody = null;
        var client = ClientWith((_, requestBody) =>
        {
            calls++;
            if (calls == 1)
            {
                checks.Check(requestBody.Contains("\"include_usage\":true", StringComparison.Ordinal),
                    "首次流式请求声明流式 usage");
                return ScriptedHttpMessageHandler.JsonError(HttpStatusCode.BadRequest,
                    "{\"error\":{\"message\":\"unknown field stream_options\"}}");
            }
            secondBody = requestBody;
            var frame = "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"OK\"}}]}\n\n";
            return ScriptedHttpMessageHandler.SseResponse(Sse.Utf8(frame + Sse.Done));
        }, out _);

        var response = await client.GenerateStreamingAsync(new PureModelRequest(
            new[] { new PureAgentMessage(PureAgentRole.User, "hi") }, Array.Empty<string>(), "m"));

        checks.Equal(2, calls, "网关拒绝 stream_options 时自动降级重试");
        checks.Check(secondBody?.Contains("stream_options", StringComparison.Ordinal) == false,
            "降级后请求不再携带 stream_options");
        checks.Equal("OK", response.Text, "降级后流式内容仍正常");
    }

    private static void ContextOverflowClassification(Checks checks)
    {
        checks.Check(NativeAgentLoop.IsContextOverflow(new ModelClientException(
                ModelClientErrorKind.InvalidRequest, "This model's maximum context length is 8192 tokens")),
            "识别 maximum context length 错误");
        checks.Check(NativeAgentLoop.IsContextOverflow(new ModelClientException(
                ModelClientErrorKind.InvalidRequest, "上下文长度超出限制")),
            "识别中文上下文超限错误");
        checks.Check(!NativeAgentLoop.IsContextOverflow(new ModelClientException(
                ModelClientErrorKind.InvalidRequest, "invalid tool schema")),
            "普通 400 不会被误判为上下文超限");
    }
}
