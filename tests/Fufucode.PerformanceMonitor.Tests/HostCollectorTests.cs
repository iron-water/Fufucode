using System.Diagnostics;
using Fufucode.App.Abstractions;
using Fufucode.App.Features.Performance;
using Fufucode.Core.Diagnostics;
using Fufucode.Plugins.PerformanceMonitor;
using static Fufucode.PerformanceMonitor.Tests.TestUtil;

namespace Fufucode.PerformanceMonitor.Tests;

internal static class HostCollectorTests
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "fufucode-perf-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var collector = new PerfHostCollector(root);
            var session = new TestSession();
            var history = new[]
            {
                new ChatMessage("u1", MessageRole.User, null, [new TextBlock("中文 hello")], null, true),
                new ChatMessage("a1", MessageRole.Assistant, "configured-model", [new TextBlock("answer")],
                    new UsageInfo { InputTokens = 500, OutputTokens = 12, CacheReadTokens = 100 }, true),
            };
            collector.AttachSession(session, "相同标题", history: history, sessionId: "session-one");
            using var recorder = new PerfRecorder(new PerfSettings { RetentionDays = 0 }, Path.Combine(root, "recorder"));
            PerfTracer.Attach(recorder, recorder);
            try
            {
                collector.SetSinkActive(true);
                Check(recorder.SessionContext?.SessionId == "session-one", "late enable publishes stable session identity");
                Check(recorder.SessionContext?.PromptTokens == 500, "late enable recovers usage from actual transcript");
                Check(recorder.SessionContext!.Parts.Sum(part => part.Tokens) > 0, "transcript text estimate available");
                Check(recorder.Ring.Count == 0, "history does not invent model/tool timings");

                session.Emit(new SessionEvent.MessageStarted("u2", MessageRole.User, null, 1));
                session.Emit(new SessionEvent.TextAppended("u2", 0, "abcd中文"));
                session.Emit(new SessionEvent.MessageFinished("u2",
                    new ChatMessage("u2", MessageRole.User, null, [new TextBlock("abcd中文")], null, true), null));
                Check(recorder.ActiveSpans().Any(span => span.Name == "turn"), "in-progress external turn is visible");
                session.Emit(new SessionEvent.MessageStarted("a2", MessageRole.Assistant, "configured-model", 1));
                session.Emit(new SessionEvent.BlockFinished("a2", 0,
                    new ToolCallBlock("call", "read_file", "{}", ToolCallStatus.Pending, null, null, false)));
                session.Emit(new SessionEvent.ToolCallStatusChanged("a2", 0, "call", ToolCallStatus.Running, null));
                Check(recorder.Ring.Snapshot().Any(span => span.Name == "model.request"), "generation closes before tool execution");
                session.Emit(new SessionEvent.ToolCallStatusChanged("a2", 0, "call", ToolCallStatus.Completed, null, 50));
                var usage = new UsageInfo { InputTokens = 100, OutputTokens = 20, CacheReadTokens = 40 };
                session.Emit(new SessionEvent.MessageFinished("a2",
                    new ChatMessage("a2", MessageRole.Assistant, "configured-model", [new TextBlock("done")], usage, true), usage));
                session.Emit(new SessionEvent.TurnSettled(SessionOutcome.Completed, "done"));
                var spans = recorder.Ring.Snapshot();
                Check(spans.Count(span => span.Name == "tool") == 1, "one tool execution observed");
                Check(spans.All(span => span.Name != "tool.process"), "generic tools do not fabricate process exit codes");
                Check(spans.All(span => span.Tag("ttftMs") is null), "external events do not fabricate TTFT");
                var snapshot = PerfSnapshot.Build(spans, [], [], recorder.SessionContext);
                Check(snapshot.TotalTokens == 120, "external usage counted once");
                Check(recorder.ActiveSpans().Count == 0, "settled external turn releases active spans");

                collector.AttachSession(new TestSession(), "相同标题", sessionId: "session-two");
                var second = PerfSnapshot.Build(spans, [], [], recorder.SessionContext);
                Check(second.TurnCount == 0 && second.ContextBreakdown.TotalTokens == 0,
                    "same session title cannot leak previous session data");
            }
            finally { PerfTracer.Detach(recorder); }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class TestSession : IChatSession
    {
        public SessionConnection Connection => SessionConnection.Connected;
        public SessionActivity Activity => SessionActivity.Idle;
        public SessionOutcome? LastOutcome => null;
        public string? CurrentModel => "configured-model";
        public bool CanSend => true;
        public bool CanStop => false;
        public int QueuedCount => 0;
        public event EventHandler<SessionEvent>? Event;
        public Task SendAsync(string text, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Emit(SessionEvent value) => Event?.Invoke(this, value);
    }
}
