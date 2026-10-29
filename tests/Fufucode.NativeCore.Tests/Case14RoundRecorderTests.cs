using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Fufucode.App.Abstractions;
using Fufucode.Core.Backend;

namespace Fufucode.NativeCore.Tests;

// Case 14: real NativeCSharpChatSession, engine and agent loop; only model transport/tools are fake.
internal static class Case14RoundRecorderTests
{
    public static async Task RunAsync(Checks checks)
    {
        await CompletedAsync(checks);
        await InterruptedAsync(checks);
        await RestartedAsync(checks);
        await FailedAsync(checks);
    }

    private sealed class Capture(NativeCSharpChatSession session)
    {
        public ConcurrentQueue<SessionEvent> Events { get; } = new();
        public TaskCompletionSource<SessionOutcome> Settled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Capture Attach() => AttachCore();

        private Capture AttachCore()
        {
            session.Event += (_, e) =>
            {
                Events.Enqueue(e);
                if (e is SessionEvent.TurnSettled s) Settled.TrySetResult(s.Outcome);
            };
            return this;
        }

        public SessionEvent.MessageFinished[] Messages => Events.OfType<SessionEvent.MessageFinished>()
            .Where(e => e.FinalMessage.Role == MessageRole.Assistant).ToArray();

        public void VerifyBoundaries(Checks checks)
        {
            var all = Events.ToArray();
            foreach (var message in Messages)
            {
                var blocks = all.OfType<SessionEvent.BlockFinished>()
                    .Where(b => b.MessageId == message.MessageId).ToArray();
                checks.Equal(message.FinalMessage.Blocks.Count, blocks.Length,
                    "real session final block count");
                for (var i = 0; i < blocks.Length; i++)
                    checks.Check(blocks[i].ContentIndex == i &&
                                 Equals(blocks[i].FinalBlock, message.FinalMessage.Blocks[i]) &&
                                 Array.IndexOf(all, blocks[i]) < Array.IndexOf(all, message),
                        "real session ordered complete final blocks");
            }
        }
    }

    private sealed class Model(Func<int, ModelStreamCallbacks, CancellationToken, Task<PureModelResponse>> script)
        : IPureModelClient, IStreamingModelClient
    {
        private int _calls;
        public Task<PureModelResponse> GenerateAsync(PureModelRequest request,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Non-streaming fallback is forbidden in this fixture");
        public Task<PureModelResponse> GenerateStreamingAsync(PureModelRequest request,
            ModelStreamCallbacks? callbacks = null, CancellationToken cancellationToken = default) =>
            script(++_calls, callbacks!, cancellationToken);
    }

    private sealed class Tools(Func<PureToolCall, CancellationToken, Task<PureToolResult>> run)
        : IPureToolDispatcher
    {
        public Task<PureToolResult> DispatchAsync(PureToolCall call,
            CancellationToken cancellationToken = default) => run(call, cancellationToken);
    }

    private static Capture Attach(NativeCSharpChatSession session) => new Capture(session).Attach();

    private static async Task CompletedAsync(Checks checks)
    {
        var model = new Model((round, cb, ct) =>
        {
            if (round == 1)
            {
                cb.OnThinkingDelta!("think"); cb.OnThinkingDelta!("ing");
                cb.OnTextDelta!("before"); cb.OnTextDelta!(" tool");
                cb.OnToolCallChunkIndexed!(7, "temporary", "read_file", "{\"path\":");
                cb.OnToolCallChunkIndexed!(7, null, null, "\"a\"}");
                cb.OnUsage!(new PureUsage(11, 12, CacheReadTokens: 3, CacheWriteTokens: 4));
                return Task.FromResult(new PureModelResponse("before tool",
                    [new("final-id", "read_file", "{\"path\":\"a\"}")]));
            }
            if (round != 2) throw new InvalidOperationException("unexpected model round");
            cb.OnTextDelta!("all "); cb.OnTextDelta!("done");
            return Task.FromResult(PureModelResponse.FromText("all done"));
        });
        var tools = new Tools((call, ct) => Task.FromResult(new PureToolResult(call.Id, true, "payload")));
        await using var session = new NativeCSharpChatSession(model, tools);
        var capture = Attach(session);
        await session.SendAsync("fixture");
        checks.Equal(SessionOutcome.Completed,
            await capture.Settled.Task.WaitAsync(TimeSpan.FromSeconds(20)), "real session completed");
        await session.StopAsync();
        var messages = capture.Messages;
        checks.Equal(2, messages.Length, "tool round and final text have separate snapshots");
        var blocks = messages[0].FinalMessage.Blocks;
        checks.Check(blocks.Count == 3 &&
                     blocks[0] is ThinkingBlock { Text: "thinking" } &&
                     blocks[1] is TextBlock { Text: "before tool" } &&
                     blocks[2] is ToolCallBlock
                     {
                         ToolCallId: "final-id", InputJson: "{\"path\":\"a\"}",
                         Status: ToolCallStatus.Completed, Output: "payload"
                     },
            "real session preserves thinking text final tool ID arguments output and order");
        checks.Check(messages[0].CumulativeUsage is
            { InputTokens: 11, OutputTokens: 12, CacheReadTokens: 3, CacheWriteTokens: 4 },
            "real session tool round usage");
        checks.Check(messages[1].FinalMessage.Blocks.Single() is TextBlock { Text: "all done" },
            "real session full final text");
        capture.VerifyBoundaries(checks);
    }

    private static async Task InterruptedAsync(Checks checks)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var model = new Model((round, cb, ct) =>
        {
            cb.OnTextDelta!("partial"); cb.OnThinkingDelta!("reason");
            cb.OnToolCallChunkIndexed!(2, "cancel", "read_file", "{}");
            cb.OnUsage!(new PureUsage(21, 22));
            return Task.FromResult(new PureModelResponse("partial", [new("cancel", "read_file", "{}")]));
        });
        var tools = new Tools(async (call, ct) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        });
        await using var session = new NativeCSharpChatSession(model, tools);
        var capture = Attach(session);
        await session.SendAsync("fixture");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(20));
        checks.Equal(SessionOutcome.Interrupted, await capture.Settled.Task,
            "real session cancellation outcome");
        var message = capture.Messages.Single();
        checks.Check(message.FinalMessage.Blocks.Count == 3 &&
                     message.FinalMessage.Blocks[0] is TextBlock { Text: "partial" } &&
                     message.FinalMessage.Blocks[1] is ThinkingBlock { Text: "reason" } &&
                     message.FinalMessage.Blocks[2] is ToolCallBlock
                     { ToolCallId: "cancel", InputJson: "{}", Status: ToolCallStatus.Cancelled },
            "real session cancelled snapshot complete");
        checks.Check(message.CumulativeUsage is { InputTokens: 21, OutputTokens: 22 },
            "real session cancellation usage");
        capture.VerifyBoundaries(checks);
    }

    private static async Task RestartedAsync(Checks checks)
    {
        var model = new Model((round, cb, ct) =>
        {
            cb.OnTextDelta!("discarded"); cb.OnThinkingDelta!("old reason");
            cb.OnToolCallChunkIndexed!(0, "old", "read_file", "{\"partial\":");
            cb.OnUsage!(new PureUsage(31, 32)); cb.OnRestart!();
            cb.OnTextDelta!("fresh"); cb.OnTextDelta!(" text"); cb.OnUsage!(new PureUsage(41, 42));
            return Task.FromResult(PureModelResponse.FromText("fresh text"));
        });
        await using var session = new NativeCSharpChatSession(model);
        var capture = Attach(session);
        await session.SendAsync("fixture");
        checks.Equal(SessionOutcome.Completed,
            await capture.Settled.Task.WaitAsync(TimeSpan.FromSeconds(20)), "real session restart completes");
        await session.StopAsync();
        var messages = capture.Messages;
        checks.Check(messages.Length == 2 && messages[0].MessageId != messages[1].MessageId,
            "real session restart separates IDs");
        checks.Check(messages[0].FinalMessage.Blocks[0] is TextBlock
                     { Text: "discarded\n\n[流式连接中断，正在重试…]\n\n" } &&
                     messages[0].FinalMessage.Blocks[1] is ThinkingBlock { Text: "old reason" } &&
                     messages[0].FinalMessage.Blocks[2] is ToolCallBlock { InputJson: "{\"partial\":" },
            "real session restart materializes partial blocks");
        checks.Check(messages[1].FinalMessage.Blocks.Single() is TextBlock { Text: "fresh text" } &&
                     messages[0].CumulativeUsage is { InputTokens: 31, OutputTokens: 32 } &&
                     messages[1].CumulativeUsage is { InputTokens: 41, OutputTokens: 42 },
            "real session restart buffers and usage isolated");
        capture.VerifyBoundaries(checks);
    }

    private static async Task FailedAsync(Checks checks)
    {
        var model = new Model((round, cb, ct) =>
        {
            cb.OnTextDelta!("failed partial");
            cb.OnToolCallChunkIndexed!(0, "failed", "read_file", "{\"x\":");
            throw new InvalidOperationException("fixture model failure");
        });
        await using var session = new NativeCSharpChatSession(model);
        var capture = Attach(session);
        await session.SendAsync("fixture");
        checks.Equal(SessionOutcome.Failed,
            await capture.Settled.Task.WaitAsync(TimeSpan.FromSeconds(20)), "real session failure outcome");
        await session.StopAsync();
        var blocks = capture.Messages.Single().FinalMessage.Blocks;
        checks.Check(blocks.Count == 2 &&
                     blocks[0] is TextBlock { Text: "failed partial" } &&
                     blocks[1] is ToolCallBlock { InputJson: "{\"x\":", Status: ToolCallStatus.Failed },
            "real session failed snapshot complete");
        capture.VerifyBoundaries(checks);
    }
}
