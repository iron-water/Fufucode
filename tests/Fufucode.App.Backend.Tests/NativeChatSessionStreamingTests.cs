using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Fufucode.App.Abstractions;
using Fufucode.Core.Backend;
using Fufucode.App.Streaming;

namespace Fufucode.App.Backend.Tests;

/// <summary>
/// CS-3：NativeCSharpChatSession 针对真实 SSE 流式报文驱动的端到端事件流捕获。
/// 覆盖「无 Node 环境下发送消息 → 订阅端收到完整流式事件序列」的验收目标，
/// 提交路径为真实 OpenAiCompatibleModelClient（HTTP 传输层被脚本化回放真实 SSE 字节）
/// 与真实 ProductionToolDispatcher（真实文件系统）。
/// </summary>
internal static class NativeChatSessionStreamingTests
{
    /// <summary>
    /// 可选的真实端点探针（不进默认套件，需网络与凭据）：
    ///   dotnet run --project tests/Fufucode.App.Backend.Tests -- --live-session &lt;baseUrl&gt; [model] [apiKey]
    /// 对真实 OpenAI 兼容网关发起真实请求，验证 NativeCSharpChatSession 在真实模型下
    /// 派发完整的流式会话事件序列，并真实执行一次工具调用。
    /// </summary>
    public static async Task<int> RunLiveAsync(string baseUrl, string model, string? apiKey)
    {
        Console.WriteLine($"[live-session] endpoint={baseUrl} model={model}");
        var workspace = Path.Combine(Path.GetTempPath(), "cs3-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            const string marker = "CS3-LIVE-MARKER-4471";
            await File.WriteAllTextAsync(Path.Combine(workspace, "cs3-live.txt"), marker);

            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
            var client = new OpenAiCompatibleModelClient(baseUrl, apiKey, model, http);
            var dispatcher = new ProductionToolDispatcher(workspace);
            await using var session = new NativeCSharpChatSession(
                new PureCSharpAgentEngine(dispatcher, client, maxIterations: 4), modelId: model);
            var capture = EventCapture.Attach(session);

            var firstDeltaAt = TimeSpan.Zero;
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            session.Event += (_, e) =>
            {
                if (e is SessionEvent.TextAppended appended && capture.IsAssistant(appended.MessageId))
                {
                    if (firstDeltaAt == TimeSpan.Zero) firstDeltaAt = stopwatch.Elapsed;
                    Console.Write(appended.Delta);
                }
            };

            var prompt =
                "You have tools. Call read_file with path \"cs3-live.txt\", then reply with exactly the file content and nothing else.";
            await session.SendAsync(prompt);
            await capture.WaitForSettledAsync(TimeSpan.FromMinutes(3));
            Console.WriteLine();

            var ok = true;
            void Expect(bool condition, string name)
            {
                Console.WriteLine((condition ? "PASS " : "FAIL ") + "[live-session] " + name);
                if (!condition) ok = false;
            }

            var assistantDeltas = capture.AssistantTextDeltas;
            var settles = capture.Settles;
            var statusEvents = capture.RawEvents.OfType<SessionEvent.ToolCallStatusChanged>().ToList();

            Console.WriteLine($"[live-session] textDeltas={assistantDeltas.Count} " +
                              $"firstDeltaAfter={firstDeltaAt.TotalMilliseconds:F0}ms " +
                              $"total={stopwatch.ElapsedMilliseconds}ms " +
                              $"statusEvents={statusEvents.Count} settles={settles.Count}");
            foreach (var settle in settles)
            {
                Console.WriteLine($"[live-session]   settle outcome={settle.Outcome} summary={settle.Summary}");
            }
            foreach (var error in capture.Errors)
            {
                Console.WriteLine($"[live-session]   error kind={error.Kind} message={error.Message}");
            }
            Console.WriteLine($"[live-session]   eventSequence={string.Join(" -> ", capture.Sequence)}");

            Expect(assistantDeltas.Count > 0, "真实模型下至少派发一个 TextAppended");
            Expect(settles.Count == 1 && settles[0].Outcome == SessionOutcome.Completed,
                "真实模型下 TurnSettled 报 Completed");
            Expect(session.LastOutcome == SessionOutcome.Completed, "会话终态为 Completed");
            Expect(capture.Sequence.Contains(nameof(SessionEvent.MessageStarted)),
                "事件序列包含 MessageStarted");
            Expect(capture.Sequence.Contains(nameof(SessionEvent.BlockStarted)),
                "事件序列包含 BlockStarted");
            Expect(statusEvents.Any(s => s.Status == ToolCallStatus.Running),
                "真实工具开始执行时派发 Running 状态");
            Expect(statusEvents.Any(s => s.Status == ToolCallStatus.Completed),
                "真实工具执行完成后派发 Completed 状态");
            Expect(capture.AssistantFinishedMessages.Count >= 1,
                "助手消息通过 MessageFinished 收束");

            var toolBlocks = capture.AssistantFinishedBlocks
                .Where(b => b.FinalBlock is ToolCallBlock)
                .Select(b => (ToolCallBlock)b.FinalBlock)
                .ToList();
            Expect(toolBlocks.Any(b => b.Output?.Contains(marker) == true),
                "真实工具读取到工作区文件内容并在块终态中携带输出");
            Expect(string.Concat(assistantDeltas).Contains(marker),
                "真实模型最终文本包含工具读到的文件内容");

            Console.WriteLine(ok ? "[live-session] RESULT: PASS" : "[live-session] RESULT: FAIL");
            return ok ? 0 : 1;
        }
        catch (ModelClientException error)
        {
            Console.Error.WriteLine($"[live-session] FAILED kind={error.Kind} status={error.StatusCode} " +
                                    $"code={error.ErrorCode} message={error.Message}");
            return 2;
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    public static async Task RunAsync(Checks checks)
    {
        await TextOnlyStreamingSequenceAsync(checks);
        await ToolRoundStreamingSequenceAsync(checks);
        await CancellationSurfacesInterruptedAsync(checks);
        await StreamErrorSurfacesFailedAsync(checks);
        await EngineModeStillReplaysAsync(checks);
        await CounterexampleBatchReplayIsNotStreamingAsync(checks);
        await IncrementalDeliveryBeforeEofAsync(checks);
        await ToolSuccessIsNotTextClassificationAsync(checks);
        await ToolStopWaitsForCleanupAsync(checks);
        await MissingDispatcherAndLimitAsync(checks);
        await StreamDisposalAndSubscriberIsolationAsync(checks);
    }

    private static async Task IncrementalDeliveryBeforeEofAsync(Checks checks)
    {
        using var stream = new GatedSseStream(
            Sse.Frame("{\"choices\":[{\"index\":0,\"delta\":{\"content\":\"先到\"}}]}"),
            Sse.Frame("{\"choices\":[{\"index\":0,\"delta\":{\"content\":\"后到\"}}]}") + Sse.Done);
        var handler = new ScriptedHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(stream),
        });
        using var http = new HttpClient(handler);
        var engine = new PureCSharpAgentEngine(modelClient:
            new OpenAiCompatibleModelClient("https://fixture.invalid/v1", defaultModelId: "fixture-free", httpClient: http));
        await using var session = new NativeCSharpChatSession(engine);
        var capture = EventCapture.Attach(session);
        try
        {
            await session.SendAsync("流式").WaitAsync(TimeSpan.FromSeconds(2));
            await stream.Paused.Task.WaitAsync(TimeSpan.FromSeconds(10));
            checks.Equal("先到", capture.AssistantTextDeltas.Single(), "响应未结束时首个增量已到达订阅端");
            checks.Equal(0, capture.AssistantFinishedMessages.Count, "响应暂停期间尚无 MessageFinished");
            checks.Equal(0, capture.Settles.Count, "响应暂停期间尚无 TurnSettled");
            checks.Check(!session.CanSend && session.CanStop, "发送已接受但模型仍运行，停止可用且不允许并发发送");
        }
        finally
        {
            stream.Release.TrySetResult();
        }
        await capture.WaitForSettledAsync();
        checks.Equal("先到后到", string.Concat(capture.AssistantTextDeltas), "解除传输暂停后剩余增量按序到达");
        using var body = JsonDocument.Parse(handler.LastRequestBody);
        checks.Equal("fixture-free", body.RootElement.GetProperty("model").GetString(),
            "原有引擎构造路径使用客户端配置模型，不发送 csharp-native 显示名");
        checks.Check(engine.GetState().History?.Any(m => m.Role == PureAgentRole.Assistant && m.Content == "先到后到") == true,
            "流式仍由原引擎维护真实完整历史");
        AssertRebuilder(checks, capture, "先到后到");
    }

    private static async Task ToolSuccessIsNotTextClassificationAsync(Checks checks)
    {
        var round = Sse.Frame(JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new { index = 0, delta = new { tool_calls = new object[]
                {
                    new { index = 4, id = "same-id", function = new { name = "read_file", arguments = "{\"pa" } },
                    new { index = 9, id = "other", function = new { name = "read_file", arguments = "{\"path\":\"b\"}" } },
                }}}
            }
        })) + Sse.Frame(JsonSerializer.Serialize(new
        {
            choices = new[] { new { index = 0, delta = new { tool_calls = new[]
            {
                new { index = 4, function = new { arguments = "th\":\"a\"}" } },
            }}}}
        })) + Sse.Done;
        var scripts = new Queue<string>(new[]
        {
            round,
            // 下一轮复用相同 stream index 和 ID，必须属于新的消息。
            Sse.Frame("{\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"same-id\",\"function\":{\"name\":\"read_file\",\"arguments\":\"{}\"}}]}}]}") + Sse.Done,
            Sse.Frame("{\"choices\":[{\"index\":0,\"delta\":{\"content\":\"工具轮完成\"}}]}") + Sse.Done,
        });
        var handler = new ScriptedHttpMessageHandler((_, _) =>
            ScriptedHttpMessageHandler.SseResponse(Sse.Split(scripts.Dequeue(), 1)));
        using var http = new HttpClient(handler);
        var dispatcher = new ResultDispatcher();
        var engine = new PureCSharpAgentEngine(dispatcher,
            new OpenAiCompatibleModelClient("https://fixture.invalid/v1", defaultModelId: "fixture-free", httpClient: http));
        await using var session = new NativeCSharpChatSession(engine);
        var capture = EventCapture.Attach(session);
        await session.SendAsync("工具测试");
        await capture.WaitForSettledAsync();
        var tools = capture.AssistantFinishedMessages
            .SelectMany(m => m.FinalMessage.Blocks).OfType<ToolCallBlock>().ToArray();
        checks.Equal(3, tools.Length, "稀疏索引、多工具和跨轮相同 ID 均收束为独立块");
        checks.Equal(ToolCallStatus.Completed, tools[0].Status, "Success=true 即使内容以未知工具开头也判定成功");
        checks.Equal(ToolCallStatus.Failed, tools[1].Status, "Success=false 即使内容为 ok 也判定失败");
        checks.Check(tools[1].IsErrorOutput, "失败工具的终态块保留 IsErrorOutput=true");
        checks.Equal("{\"path\":\"a\"}", tools[0].InputJson, "交错工具分片按稀疏索引正确拼装");
        checks.Equal(ToolCallStatus.Completed, tools[2].Status, "下一轮相同调用 ID 不串到上一轮");
        var starts = capture.RawEvents.OfType<SessionEvent.BlockStarted>()
            .Where(e => e.Kind == MessageBlockKind.ToolCall).ToArray();
        checks.Equal(0, starts[0].ContentIndex, "仅工具轮从 contentIndex=0 开始，无空洞");
        checks.Equal(1, starts[1].ContentIndex, "第二工具块保持连续索引");
        var statuses = capture.RawEvents.OfType<SessionEvent.ToolCallStatusChanged>().ToArray();
        checks.Equal(6, statuses.Length, "三个真实工具各有开始和终态状态通知");
        checks.Check(statuses[0].MessageId != statuses[4].MessageId, "跨轮重复 ID 的状态属于不同 MessageId");
        AssertRebuilder(checks, capture, "工具轮完成");
        var rebuilder = new MessageRebuilder();
        foreach (var value in capture.RawEvents) rebuilder.Apply(value);
        var rebuilt = rebuilder.Timeline.Where(e => e.Message is not null)
            .SelectMany(e => e.Message!.Blocks).OfType<ToolCallBlock>().ToArray();
        checks.Equal(3, rebuilt.Length, "真实桌面重建器保留三个工具块");
        checks.Equal(ToolCallStatus.Failed, rebuilt[1].Status, "桌面重建器保留真实失败状态");
        checks.Equal("ok", rebuilt[1].Output, "桌面重建器收到真实失败输出");
    }

    private static async Task ToolStopWaitsForCleanupAsync(Checks checks)
    {
        var frames = Sse.Frame("{\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"stop\",\"function\":{\"name\":\"read_file\",\"arguments\":\"{}\"}}]}}]}") + Sse.Done;
        var handler = new ScriptedHttpMessageHandler((_, _) =>
            ScriptedHttpMessageHandler.SseResponse(Sse.Split(frames, 3)));
        using var http = new HttpClient(handler);
        var dispatcher = new BlockingDispatcher();
        await using var session = new NativeCSharpChatSession(new PureCSharpAgentEngine(dispatcher,
            new OpenAiCompatibleModelClient("https://fixture.invalid/v1", defaultModelId: "fixture-free", httpClient: http)));
        var capture = EventCapture.Attach(session);
        await session.SendAsync("停止工具");
        await dispatcher.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task stopping = session.StopAsync();
        try
        {
            await dispatcher.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(10));
            checks.Check(!stopping.IsCompleted, "StopAsync 等待工具取消清理完成");
            checks.Check(!session.CanSend && session.Activity == SessionActivity.Cancelling, "取消未收敛时禁止新发送");
        }
        finally
        {
            dispatcher.AllowCleanup.TrySetResult();
        }
        await stopping.WaitAsync(TimeSpan.FromSeconds(10));
        await capture.WaitForSettledAsync();
        checks.Equal(SessionOutcome.Interrupted, session.LastOutcome, "真实 StopAsync 路径以 Interrupted 收束");
        var states = capture.RawEvents.OfType<SessionEvent.ToolCallStatusChanged>().Select(s => s.Status).ToArray();
        checks.Check(states.SequenceEqual(new[] { ToolCallStatus.Running, ToolCallStatus.Cancelled }),
            "被停止工具只有 Running→Cancelled，不伪造 Completed");
        checks.Check(session.CanSend && !session.CanStop, "停止收敛后恢复 Idle 可发送状态");
        checks.Equal(1, handler.RequestBodies.Count, "取消工具后不再请求下一轮模型");
        var tool = (ToolCallBlock)capture.AssistantFinishedMessages.Single().FinalMessage.Blocks.Single();
        checks.Equal(ToolCallStatus.Cancelled, tool.Status, "取消消息最终快照也保留 Cancelled");
    }

    private static async Task MissingDispatcherAndLimitAsync(Checks checks)
    {
        var frames = Sse.Frame("{\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"none\",\"function\":{\"name\":\"read_file\",\"arguments\":\"{}\"}}]}}]}") + Sse.Done;
        var handler = new ScriptedHttpMessageHandler((_, _) =>
            ScriptedHttpMessageHandler.SseResponse(Sse.Split(frames, 2)));
        using var http = new HttpClient(handler);
        // 显式 maxIterations 只用于让本测试可重复结束；它不代表生产会话默认有轮次上限。
        await using var session = new NativeCSharpChatSession(new PureCSharpAgentEngine(modelClient:
            new OpenAiCompatibleModelClient("https://fixture.invalid/v1", defaultModelId: "fixture-free", httpClient: http), maxIterations: 2));
        var capture = EventCapture.Attach(session);
        await session.SendAsync("无调度器");
        await capture.WaitForSettledAsync();
        checks.Check(capture.RawEvents.OfType<SessionEvent.ToolCallStatusChanged>()
            .All(s => s.Status == ToolCallStatus.Failed), "无调度器明确 Failed，不伪造 Running");
        checks.Check(capture.AssistantFinishedMessages.Count >= 1, "预算终态至少有一条完整助手消息");
        checks.Check(capture.AssistantTextDeltas.Any(text => text.Contains("外部任务轮次预算已耗尽")),
            "显式预算终态提示通过会话事件回放");
    }

    private static async Task StreamDisposalAndSubscriberIsolationAsync(Checks checks)
    {
        using var stream = new GatedSseStream(
            Sse.Frame("{\"choices\":[{\"index\":0,\"delta\":{\"content\":\"已到达\"}}]}"), Sse.Done);
        var handler = new ScriptedHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(stream),
        });
        using var http = new HttpClient(handler);
        await using var session = new NativeCSharpChatSession(new PureCSharpAgentEngine(modelClient:
            new OpenAiCompatibleModelClient("https://fixture.invalid/v1", defaultModelId: "fixture-free", httpClient: http)));
        // 先注册会抛异常的订阅者，后注册的真实订阅者仍应收到完整生命周期。
        session.Event += (_, _) => throw new InvalidOperationException("subscriber-only");
        var capture = EventCapture.Attach(session);
        await session.SendAsync("关闭");
        await stream.Paused.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        await capture.WaitForSettledAsync();
        checks.Equal("已到达", capture.AssistantTextDeltas.Single(), "订阅者异常不会吞掉其他订阅者的增量");
        checks.Equal(SessionOutcome.Interrupted, session.LastOutcome, "DisposeAsync 取消真实 SSE 读取并等待终态");
        checks.Equal(SessionConnection.Stopped, session.Connection, "释放后连接为 Stopped");
        checks.Check(!session.CanSend && !session.CanStop, "释放后发送和停止不可用");
        await checks.ThrowsAsync<ObjectDisposedException>(() => session.SendAsync("释放后发送"),
            "释放后发送被明确拒绝");
        checks.Equal(1, capture.Settles.Count, "订阅者异常和释放不会重复派发 TurnSettled");
        checks.Equal(0, capture.Errors.Count, "订阅者异常不伪装成 ModelFailed");
        checks.Check(capture.AssistantFinishedMessages.Single().FinalMessage.IsComplete, "释放时部分消息正常收束");
    }

    private static void AssertRebuilder(Checks checks, EventCapture capture, string expected)
    {
        var rebuilder = new MessageRebuilder();
        foreach (var value in capture.RawEvents) rebuilder.Apply(value);
        var messages = rebuilder.Timeline.Where(e => e.Message?.Role == MessageRole.Assistant)
            .Select(e => e.Message!).ToArray();
        checks.Check(messages.Length > 0 && messages.All(m => m.IsComplete),
            "真实桌面 MessageRebuilder 消费事件后全部助手消息收束");
        checks.Equal(expected, ((TextBlock)messages[^1].Blocks.Single()).Text,
            "真实桌面 MessageRebuilder 收到最终文本且未重复追加");
    }

    private sealed class ResultDispatcher : IPureToolDispatcher
    {
        public Task<PureToolResult> DispatchAsync(PureToolCall call, CancellationToken cancellationToken = default)
            => Task.FromResult(new PureToolResult(call.Id, call.Id != "other",
                call.Id == "other" ? "ok" : "未知工具：这是文件中的普通文字"));
    }

    private sealed class BlockingDispatcher : IPureToolDispatcher
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowCleanup { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<PureToolResult> DispatchAsync(PureToolCall call, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                throw new InvalidOperationException("不可达");
            }
            catch (OperationCanceledException)
            {
                CancellationObserved.TrySetResult();
                await AllowCleanup.Task;
                throw;
            }
        }
    }

    private sealed class GatedSseStream(string first, string remainder) : Stream
    {
        private readonly byte[] _first = Encoding.UTF8.GetBytes(first);
        private readonly byte[] _remainder = Encoding.UTF8.GetBytes(remainder);
        private int _stage;
        public TaskCompletionSource Paused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_stage == 0)
            {
                _stage++;
                _first.CopyTo(buffer);
                return _first.Length;
            }
            if (_stage == 1)
            {
                Paused.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
                _stage++;
                _remainder.CopyTo(buffer);
                return _remainder.Length;
            }
            return 0;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// 纯文本轮：MessageStarted → BlockStarted(Text) → TextAppended×N → BlockFinished → MessageFinished → TurnSettled。
    /// 断言每个增量在整轮结束前就到达订阅端（打字机流式的关键判据）。
    /// </summary>
    private static async Task TextOnlyStreamingSequenceAsync(Checks checks)
    {
        var turn =
            Sse.Frame("{\"choices\":[{\"index\":0,\"delta\":{\"content\":\"你好\"}}]}") +
            Sse.Frame("{\"choices\":[{\"index\":0,\"delta\":{\"content\":\"，世界\"}}]}") +
            Sse.Frame("{\"choices\":[{\"index\":0,\"delta\":{\"content\":\"！\"},\"finish_reason\":\"stop\"}]}") +
            Sse.Done;

        var handler = new ScriptedHttpMessageHandler((_, _) =>
            ScriptedHttpMessageHandler.SseResponse(Sse.Split(turn, 5)));
        var client = new OpenAiCompatibleModelClient("https://gateway.example.com/v1", "sk-test", defaultModelId: "fixture-model",
            httpClient: new HttpClient(handler));

        var session = new NativeCSharpChatSession(client, toolDispatcher: null, maxIterations: 3);
        var capture = EventCapture.Attach(session);
        checks.Check(session.IsStreaming, "流式客户端的会话报告 IsStreaming=true");

        await session.SendAsync("打个招呼");
        await capture.WaitForSettledAsync();

        var sequence = capture.Sequence;

        // 用户回放必须严格在助手流之前完成且可订阅
        checks.Equal(nameof(SessionEvent.ActivityChanged), sequence[0], "发送开始先派发 ActivityChanged(Running)");
        checks.Equal(nameof(SessionEvent.MessageStarted), sequence[1], "紧随其后为用户消息 MessageStarted");
        checks.Check(capture.RawEvents.OfType<SessionEvent.TextAppended>()
            .Any(t => t.MessageId == capture.FirstUserMessageId), "用户消息通过 TextAppended 回放");
        checks.Equal(nameof(SessionEvent.TurnSettled), sequence[^1], "事件最终由 TurnSettled 收束");
        checks.Equal(nameof(SessionEvent.ActivityChanged), sequence[^2], "收束前派发 ActivityChanged(Idle)");

        // 助手消息结构
        var assistantStartIndex = IndexOfAssistantSequence(capture);
        checks.Check(assistantStartIndex >= 0, "助手消息以 MessageStarted(Assistant) 开始");
        checks.Equal(nameof(SessionEvent.BlockStarted), sequence[assistantStartIndex + 1],
            "助手消息紧跟 BlockStarted");
        checks.Equal(nameof(SessionEvent.TextAppended), sequence[assistantStartIndex + 2],
            "BlockStarted 后立即为 TextAppended（未等到整轮结束）");

        var textDeltas = capture.AssistantTextDeltas;
        checks.Equal(3, textDeltas.Count, "收到 3 个文本增量（真实分片而非整段回放）");
        checks.Equal("你好", textDeltas[0], "首增量按序到达");
        checks.Equal("，世界", textDeltas[1], "第二增量按序到达");
        checks.Equal("！", textDeltas[2], "第三增量按序到达");

        // 事件顺序：TextAppended ×3 全部先于 BlockFinished / MessageFinished
        var firstDelta = sequence.IndexOf(nameof(SessionEvent.TextAppended), assistantStartIndex);
        var blockFinished = sequence.IndexOf(nameof(SessionEvent.BlockFinished), assistantStartIndex);
        var messageFinished = sequence.IndexOf(nameof(SessionEvent.MessageFinished), assistantStartIndex);
        checks.Check(firstDelta < blockFinished, "首个文本增量早于 BlockFinished");
        checks.Check(blockFinished < messageFinished, "BlockFinished 早于 MessageFinished");

        // 块结束内容与整体替换
        var finishedBlock = capture.AssistantFinishedBlocks.LastOrDefault(b => b.FinalBlock is TextBlock);
        checks.Check(finishedBlock is not null, "助手文本块通过 BlockFinished 收束");
        checks.Equal("你好，世界！", ((TextBlock)finishedBlock!.FinalBlock).Text, "块终态文本为增量顺序拼接结果");

        var finalMessage = capture.AssistantFinishedMessages.Single();
        checks.Equal("你好，世界！", ((TextBlock)finalMessage.FinalMessage.Blocks[0]).Text,
            "MessageFinished 携带整体替换的最终文本");

        checks.Equal(SessionOutcome.Completed, session.LastOutcome, "会话终态为 Completed");
        checks.Equal(SessionActivity.Idle, session.Activity, "会话回到 Idle");
    }

    /// <summary>
    /// 工具轮：助手消息先流式文本+工具分片，工具执行前后派发状态事件，
    /// 工具结果回填后再次请求模型产出终态文本。断言 Running → Completed 全序列。
    /// </summary>
    private static async Task ToolRoundStreamingSequenceAsync(Checks checks)
    {
        var turn1 =
            Sse.Frame("{\"choices\":[{\"index\":0,\"delta\":{\"content\":\"我来读取文件\"}}]}") +
            Sse.Frame("{\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_a\",\"function\":{\"name\":\"read_file\",\"arguments\":\"{\\\"pa\"}}]}}]}") +
            Sse.Frame("{\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"th\\\":\\\"note.txt\\\"}\"}}]},\"finish_reason\":\"tool_calls\"}]}") +
            Sse.Done;
        var turn2 =
            Sse.Frame("{\"choices\":[{\"index\":0,\"delta\":{\"content\":\"文件内容是 cs3-payload\"}}]}") +
            Sse.Frame("{\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}") +
            Sse.Done;

        var scripts = new Queue<string>(new[] { turn1, turn2 });
        var handler = new ScriptedHttpMessageHandler((_, _) =>
            ScriptedHttpMessageHandler.SseResponse(Sse.Split(scripts.Count > 0 ? scripts.Dequeue() : Sse.Done, 7)));
        var client = new OpenAiCompatibleModelClient("https://gateway.example.com/v1", "sk-test", defaultModelId: "fixture-model",
            httpClient: new HttpClient(handler));

        var workspace = Path.Combine(Path.GetTempPath(), "cs3-session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(workspace, "note.txt"), "cs3-payload");
            var dispatcher = new ProductionToolDispatcher(workspace);
            var session = new NativeCSharpChatSession(client, dispatcher, maxIterations: 4);
            var capture = EventCapture.Attach(session);

            await session.SendAsync("读取 note.txt");
            await capture.WaitForSettledAsync();

            // 工具参数分片必须实时到达（ToolCallAppended）
            var toolFragments = capture.RawEvents.OfType<SessionEvent.ToolCallAppended>()
                .Select(t => t.JsonFragment).ToList();
            checks.Equal(2, toolFragments.Count, "工具参数分片按 2 次 ToolCallAppended 实时派发");
            checks.Equal("{\"pa", toolFragments[0], "首片携带 arguments 前缀");
            checks.Equal("th\":\"note.txt\"}", toolFragments[1], "末片只携带剩余 arguments 增量");

            // 工具状态：Running（执行中）→ Completed（执行完成），按真实到达顺序断言
            var statuses = capture.RawEvents.OfType<SessionEvent.ToolCallStatusChanged>().ToList();
            checks.Check(statuses.Any(s => s.Status == ToolCallStatus.Running),
                "工具开始执行时派发 Running 状态");
            checks.Check(statuses.Any(s => s.Status == ToolCallStatus.Completed),
                "工具执行完成后派发 Completed 状态");
            var runningIndex = statuses.FindIndex(s => s.Status == ToolCallStatus.Running);
            var completedIndex = statuses.FindIndex(s => s.Status == ToolCallStatus.Completed);
            checks.Check(runningIndex >= 0 && completedIndex > runningIndex,
                "Running 状态早于 Completed 状态到达");

            // 工具块终态包含完整参数（分片顺序拼接结果）与执行输出
            var toolBlocks = capture.AssistantFinishedBlocks
                .Where(b => b.FinalBlock is ToolCallBlock)
                .Select(b => (ToolCallBlock)b.FinalBlock)
                .ToList();
            checks.Equal(1, toolBlocks.Count, "工具调用块在 BlockFinished 中收束为 1 个");
            checks.Equal("read_file", toolBlocks[0].ToolName, "工具块记录工具名");
            checks.Equal("{\"path\":\"note.txt\"}", toolBlocks[0].InputJson, "工具块参数为分片拼接后的完整 JSON");
            checks.Equal(ToolCallStatus.Completed, toolBlocks[0].Status, "工具块终态为 Completed");
            checks.Check(toolBlocks[0].Output?.Contains("cs3-payload") == true, "工具块携带真实执行输出");

            // 终态文本轮
            var assistantTextDeltas = capture.AssistantTextDeltas;
            checks.Equal("我来读取文件", assistantTextDeltas[0], "工具轮文本增量到达");
            checks.Equal("文件内容是 cs3-payload", assistantTextDeltas[^1], "终态文本由第二轮流式分片聚合");

            // 两条助手消息（工具轮 + 终态轮）各自有 MessageFinished
            var assistantMessages = capture.AssistantFinishedMessages;
            checks.Equal(2, assistantMessages.Count, "工具轮与终态轮各产生 1 条 MessageFinished");
            checks.Equal(2, handler.RequestBodies.Count, "会话驱动了 2 次真实 HTTP 请求");
            checks.Equal(SessionOutcome.Completed, session.LastOutcome, "工具轮会话终态为 Completed");
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    /// <summary>中途取消：TurnSettled 报 Interrupted，未悬挂助手消息。</summary>
    private static async Task CancellationSurfacesInterruptedAsync(Checks checks)
    {
        using var cts = new CancellationTokenSource();
        var client = new CancelMidStreamSessionClient(cts);
        var session = new NativeCSharpChatSession(client, null, maxIterations: 3);
        var capture = EventCapture.Attach(session);

        await session.SendAsync("流中途停止", cts.Token);
        await capture.WaitForSettledAsync();

        checks.Equal(nameof(SessionEvent.TurnSettled), capture.Sequence[^1], "取消后仍以 TurnSettled 收束");
        checks.Equal(SessionOutcome.Interrupted, session.LastOutcome, "取消的会话终态为 Interrupted");
        var settle = capture.Settles.Single();
        checks.Equal(SessionOutcome.Interrupted, settle.Outcome, "TurnSettled 携带 Interrupted");

        // 取消前已到达的增量仍应送达；且助手消息不得悬挂（必须有 MessageFinished）
        checks.Check(capture.AssistantTextDeltas.Count > 0,
            "取消前已到达的文本增量仍送达订阅端");
        checks.Check(capture.AssistantFinishedMessages.Count > 0,
            "取消路径上已开放的助手消息被兜底收束（不悬挂）");
    }

    /// <summary>流内错误：TurnSettled 报 Failed，并有 SessionError(ModelFailed)。</summary>
    private static async Task StreamErrorSurfacesFailedAsync(Checks checks)
    {
        var frames =
            Sse.Frame("{\"choices\":[{\"index\":0,\"delta\":{\"content\":\"partial\"}}]}") +
            Sse.Frame("{\"error\":{\"message\":\"upstream 500\",\"code\":\"server_error\"}}") +
            Sse.Done;
        var handler = new ScriptedHttpMessageHandler((_, _) =>
            ScriptedHttpMessageHandler.SseResponse(Sse.Split(frames, 6)));
        var client = new OpenAiCompatibleModelClient("https://gateway.example.com/v1", "sk-test", defaultModelId: "fixture-model",
            httpClient: new HttpClient(handler));

        var session = new NativeCSharpChatSession(client, null, maxIterations: 2);
        var capture = EventCapture.Attach(session);

        await session.SendAsync("触发流错误");
        await capture.WaitForSettledAsync();

        checks.Equal(SessionOutcome.Failed, session.LastOutcome, "流内错误使会话终态为 Failed");
        checks.Check(capture.Errors.Any(e => e.Kind == SessionErrorKind.ModelFailed), "派发 SessionError(ModelFailed)");
        checks.Check(capture.Settles.Single().Summary?.Contains("upstream 500") == true,
            "TurnSettled 概要携带服务端错误消息");
        checks.Check(capture.AssistantTextDeltas.Contains("partial"),
            "失败前已到达的增量仍送达");
        checks.Check(capture.AssistantFinishedMessages.Count > 0,
            "失败路径上已开放的助手消息被兜底收束");
    }

    /// <summary>引擎模式：无真实分片，按轮次结束整段回放；IsStreaming 为 false。</summary>
    private static async Task EngineModeStillReplaysAsync(Checks checks)
    {
        var engine = new FakePureEngine { Model = _ => PureModelResponse.FromText("引擎回放文本") };
        var session = new NativeCSharpChatSession(engine);
        var capture = EventCapture.Attach(session);
        checks.Check(!session.IsStreaming, "引擎模式报告 IsStreaming=false");

        await session.SendAsync("你好");
        await capture.WaitForSettledAsync();

        var assistantDeltas = capture.AssistantTextDeltas;
        checks.Equal(1, assistantDeltas.Count, "引擎模式整段回放为单个文本块");
        checks.Equal("引擎回放文本", assistantDeltas[0], "引擎模式回放模型文本");
        checks.Equal(nameof(SessionEvent.TurnSettled), capture.Sequence[^1], "引擎模式仍以 TurnSettled 收束");
        checks.Equal(SessionOutcome.Completed, session.LastOutcome, "引擎模式终态为 Completed");
    }

    /// <summary>
    /// 红/绿对照反例：若把「整段文本/全部工具参数一次性投递」当作流式，
    /// 则会得到单个增量与单个分片；生产实现必须给出多于一个真实分片。
    /// 同一报文下反例只产生 1 个增量，证明「增量计数」断言有鉴别力。
    /// </summary>
    private static async Task CounterexampleBatchReplayIsNotStreamingAsync(Checks checks)
    {
        var turn =
            Sse.Frame("{\"choices\":[{\"index\":0,\"delta\":{\"content\":\"AAA\"}}]}") +
            Sse.Frame("{\"choices\":[{\"index\":0,\"delta\":{\"content\":\"BBB\"}}]}") +
            Sse.Frame("{\"choices\":[{\"index\":0,\"delta\":{\"content\":\"CCC\"},\"finish_reason\":\"stop\"}]}") +
            Sse.Done;
        var handler = new ScriptedHttpMessageHandler((_, _) =>
            ScriptedHttpMessageHandler.SseResponse(Sse.Split(turn, 4)));
        var client = new OpenAiCompatibleModelClient("https://gateway.example.com/v1", "sk-test", defaultModelId: "fixture-model",
            httpClient: new HttpClient(handler));

        // 反例实现：不接增量回调，直接把聚合结果当作单个增量投递（常见「伪流式」写法）。
        var batchDeltas = new List<string>();
        var completion = await client.StreamAsync(new PureModelRequest(
            new[] { new PureAgentMessage(PureAgentRole.User, "hi") }, Array.Empty<string>()),
            callbacks: null);
        var aggregated = completion.ToResponse().Text;
        if (!string.IsNullOrEmpty(aggregated)) batchDeltas.Add(aggregated);
        checks.Equal(1, batchDeltas.Count,
            "反例（红）：不接增量回调时只能拿到 1 个整段增量");
        checks.Equal("AAABBBCCC", batchDeltas[0], "反例（红）：整段文本为聚合结果");

        // 生产实现：同一报文经会话层必须拆出多个真实增量
        var scripts = new Queue<string>(new[] { turn });
        var streamingHandler = new ScriptedHttpMessageHandler((_, _) =>
            ScriptedHttpMessageHandler.SseResponse(Sse.Split(scripts.Count > 0 ? scripts.Dequeue() : Sse.Done, 4)));
        var streamingClient = new OpenAiCompatibleModelClient("https://gateway.example.com/v1", "sk-test", defaultModelId: "fixture-model",
            httpClient: new HttpClient(streamingHandler));
        var session = new NativeCSharpChatSession(streamingClient, null, maxIterations: 2);
        var capture = EventCapture.Attach(session);
        await session.SendAsync("hi");
        await capture.WaitForSettledAsync();
        var produced = capture.AssistantTextDeltas;

        checks.Equal(3, produced.Count, "生产实现（绿）：同一报文拆出 3 个真实增量");
        checks.Check(produced.Count > batchDeltas.Count,
            "生产实现（绿）：增量数严格多于整段投递，断言确实有鉴别力");
    }

    /// <summary>定位第一条助手 MessageStarted 在序列中的下标。</summary>
    private static int IndexOfAssistantSequence(EventCapture capture)
    {
        for (var i = 0; i < capture.RawEvents.Count; i++)
        {
            if (capture.RawEvents[i] is SessionEvent.MessageStarted started &&
                started.Role == MessageRole.Assistant)
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>取消夹具：派发一个文本分片后取消，模拟用户点击停止。</summary>
    private sealed class CancelMidStreamSessionClient(CancellationTokenSource cts) : IPureModelClient, IStreamingModelClient
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

    /// <summary>
    /// 事件流捕获器：订阅 <see cref="IChatSession.Event"/> 并按到达顺序保存全部原始事件，
    /// 同时提供按助手消息过滤的常用视图，供断言「实时到达」与「块/消息终态」。
    /// </summary>
    private sealed class EventCapture
    {
        private readonly List<SessionEvent> _raw = new();
        private readonly HashSet<string> _assistantMessageIds = new();
        private readonly TaskCompletionSource _settled = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsAssistant(string id) => _assistantMessageIds.Contains(id);
        public Task WaitForSettledAsync(TimeSpan? timeout = null)
            => _settled.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(15));

        public IReadOnlyList<SessionEvent> RawEvents => _raw;

        /// <summary>按到达顺序记录的事件类型名（用于顺序断言）。</summary>
        public List<string> Sequence => _raw.Select(e => e.GetType().Name).ToList();

        public string FirstUserMessageId =>
            _raw.OfType<SessionEvent.MessageStarted>().First(m => m.Role == MessageRole.User).MessageId;

        /// <summary>助手消息的文本增量（按到达顺序）。</summary>
        public List<string> AssistantTextDeltas => _raw.OfType<SessionEvent.TextAppended>()
            .Where(t => _assistantMessageIds.Contains(t.MessageId))
            .Select(t => t.Delta)
            .ToList();

        /// <summary>助手消息的块终态（按到达顺序）。</summary>
        public List<SessionEvent.BlockFinished> AssistantFinishedBlocks => _raw
            .OfType<SessionEvent.BlockFinished>()
            .Where(b => _assistantMessageIds.Contains(b.MessageId))
            .ToList();

        /// <summary>助手消息的最终快照（按到达顺序）。</summary>
        public List<SessionEvent.MessageFinished> AssistantFinishedMessages => _raw
            .OfType<SessionEvent.MessageFinished>()
            .Where(m => _assistantMessageIds.Contains(m.MessageId))
            .ToList();

        public List<SessionEvent.TurnSettled> Settles => _raw.OfType<SessionEvent.TurnSettled>().ToList();
        public List<SessionEvent.SessionError> Errors => _raw.OfType<SessionEvent.SessionError>().ToList();

        public static EventCapture Attach(IChatSession session)
        {
            var capture = new EventCapture();
            session.Event += (_, e) => capture.OnEvent(e);
            return capture;
        }

        private void OnEvent(SessionEvent e)
        {
            if (e is SessionEvent.MessageStarted started && started.Role == MessageRole.Assistant)
            {
                _assistantMessageIds.Add(started.MessageId);
            }
            _raw.Add(e);
            if (e is SessionEvent.TurnSettled) _settled.TrySetResult();
        }
    }
}
