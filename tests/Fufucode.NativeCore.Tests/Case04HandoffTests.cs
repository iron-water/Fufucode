using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Fufucode.App.Abstractions;
using Fufucode.Core.Backend;

namespace Fufucode.NativeCore.Tests;

// Integration: --case 04; real session with a gated fake engine.
internal static class Case04HandoffTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static Checks _checks = null!;
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Require(bool value, string message) => _checks.Check(value, message);

    public static async Task RunAsync(Checks checks)
    {
        _checks = checks;
        await ReentrantSendAsync(false);
        await ReentrantSendAsync(true);
        await TerminalStopAsync();
        await TerminalDisposeAsync();
    }

    private static async Task ReentrantSendAsync(bool throwingPromptSubscriber)
    {
        var engine = new GatedEngine();
        var session = new NativeCSharpChatSession(engine);
        var entered = Signal();
        var once = 0;
        Exception? callbackFailure = null;
        session.Event += (_, value) =>
        {
            if (value is not SessionEvent.ActivityChanged { Activity: SessionActivity.Idle }
                || Interlocked.Exchange(ref once, 1) != 0) return;
            try
            {
                // This call is deliberately synchronous, on A's event dispatch stack.
                session.SendAsync("C").GetAwaiter().GetResult();
                Require(session.QueuedCount == 2, "A must retain ownership; B and C remain queued during terminal notification");
                Require(engine.Order.SequenceEqual(new[] { "A" }), "No successor may enter the engine during A's terminal notification");
            }
            catch (Exception error) { callbackFailure = error; }
            finally { entered.TrySetResult(); }
        };
        if (throwingPromptSubscriber)
            session.PromptHandled += () => throw new InvalidOperationException("fixture subscriber");
        try
        {
            await session.SendAsync("A");
            await engine.A.Started.Task.WaitAsync(Timeout);
            await session.SendAsync("B");
            engine.A.Release.TrySetResult();
            await entered.Task.WaitAsync(Timeout);
            if (callbackFailure is not null) throw callbackFailure;
            await engine.B.Started.Task.WaitAsync(Timeout);
            Require(engine.Order.SequenceEqual(new[] { "A", "B" }), "FIFO: B must precede reentrant C");
            Require(session.QueuedCount == 1 && session.Activity == SessionActivity.Running,
                "A cleanup must not clear B ownership or lose C");
            engine.B.Release.TrySetResult();
            await engine.C.Started.Task.WaitAsync(Timeout);
            Require(engine.Order.SequenceEqual(new[] { "A", "B", "C" }), "FIFO: exactly A,B,C");
            var stopping = session.StopAsync();
            await engine.C.Cancelled.Task.WaitAsync(Timeout);
            Require(!stopping.IsCompleted, "Stop must await C cleanup, not A or B task");
            engine.C.Cleanup.TrySetResult();
            await stopping.WaitAsync(Timeout);
            Require(session.Activity == SessionActivity.Idle && session.QueuedCount == 0,
                "Stop must finish C and leave an empty idle session");
        }
        finally
        {
            engine.ReleaseAll();
            await session.DisposeAsync().AsTask().WaitAsync(Timeout);
        }
    }

    private static async Task TerminalStopAsync()
    {
        var engine = new GatedEngine();
        var session = new NativeCSharpChatSession(engine);
        var stopped = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleared = new ConcurrentQueue<string>();
        var once = 0;
        session.Event += (_, value) =>
        {
            if (value is SessionEvent.QueueCleared q)
                foreach (var text in q.ClearedTexts) cleared.Enqueue(text);
            if (value is SessionEvent.TurnSettled && Interlocked.Exchange(ref once, 1) == 0)
            {
                session.SendAsync("C").GetAwaiter().GetResult();
                // Do not synchronously wait for our own event dispatch to finish.
                stopped.TrySetResult(session.StopAsync());
            }
        };
        try
        {
            await session.SendAsync("A");
            await engine.A.Started.Task.WaitAsync(Timeout);
            await session.SendAsync("B");
            engine.A.Release.TrySetResult();
            var stop = await stopped.Task.WaitAsync(Timeout);
            await stop.WaitAsync(Timeout);
            Require(cleared.SequenceEqual(new[] { "B", "C" }), "Stop must report every queued message in FIFO order");
            Require(engine.Order.SequenceEqual(new[] { "A" }), "No dequeued successor may escape Stop");
            Require(session.QueuedCount == 0 && session.Activity == SessionActivity.Idle, "Stop fully settled");
        }
        finally
        {
            engine.ReleaseAll();
            await session.DisposeAsync().AsTask().WaitAsync(Timeout);
        }
    }

    private static async Task TerminalDisposeAsync()
    {
        var engine = new GatedEngine();
        var session = new NativeCSharpChatSession(engine);
        var disposed = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        var once = 0;
        session.Event += (_, value) =>
        {
            if (value is not SessionEvent.TurnSettled || Interlocked.Exchange(ref once, 1) != 0) return;
            session.SendAsync("C").GetAwaiter().GetResult();
            disposed.TrySetResult(session.DisposeAsync().AsTask());
        };
        try
        {
            await session.SendAsync("A");
            await engine.A.Started.Task.WaitAsync(Timeout);
            await session.SendAsync("B");
            engine.A.Release.TrySetResult();
            var disposal = await disposed.Task.WaitAsync(Timeout);
            await disposal.WaitAsync(Timeout);
            Require(engine.Order.SequenceEqual(new[] { "A" }), "Dispose must prevent B/C launch");
            Require(session.QueuedCount == 2, "Dispose retains existing queue semantics: neither B nor C silently dequeued");
            Require(session.Connection == SessionConnection.Stopped, "Dispose closes session");
        }
        finally
        {
            engine.ReleaseAll();
            await session.DisposeAsync().AsTask().WaitAsync(Timeout);
        }
    }

    private sealed class Turn
    {
        public TaskCompletionSource Started { get; } = Signal();
        public TaskCompletionSource Release { get; } = Signal();
        public TaskCompletionSource Cancelled { get; } = Signal();
        public TaskCompletionSource Cleanup { get; } = Signal();
    }

    private sealed class GatedEngine : IPureCSharpAgentEngine
    {
        public Turn A { get; } = new();
        public Turn B { get; } = new();
        public Turn C { get; } = new();
        public ConcurrentQueue<string> Order { get; } = new();
        public PureAgentState GetState() => new("fixture", 0, false);
        public void Reset() { }
        public void RestoreHistory(System.Collections.Generic.IReadOnlyList<PureAgentMessage> messages) { }
        public async Task<PureAgentTurnResult> RunTurnAsync(PureModelRequest request, CancellationToken cancellationToken = default)
        {
            var text = request.Messages.Last(m => m.Role == PureAgentRole.User).Content;
            var turn = text switch { "A" => A, "B" => B, "C" => C, _ => throw new InvalidOperationException(text) };
            Order.Enqueue(text);
            turn.Started.TrySetResult();
            try { await turn.Release.Task.WaitAsync(cancellationToken); }
            catch (OperationCanceledException)
            {
                turn.Cancelled.TrySetResult();
                await turn.Cleanup.Task;
                throw;
            }
            return new(PureAgentTurnStatus.Completed, PureModelResponse.FromText(text), []);
        }
        public void ReleaseAll()
        {
            foreach (var turn in new[] { A, B, C })
            {
                turn.Release.TrySetResult();
                turn.Cleanup.TrySetResult();
            }
        }
    }
}
