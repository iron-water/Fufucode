using System;
using System.Linq;
using System.Threading.Tasks;
using Fufucode.App.Abstractions;
using Fufucode.App.Infrastructure;
using Fufucode.App.ViewModels;

namespace Fufucode.App.SelfTest;

public static class TimelineHistorySelfTest
{
    public static Task RunAsync(Action<string> pass, Action<string> fail)
    {
        void Check(bool condition, string name)
        {
            if (condition) pass("历史回放：" + name);
            else fail("历史回放：" + name);
        }

        ChatMessage[] History() =>
        [
            new("user", MessageRole.User, null, [new TextBlock("fixture")], null, true),
            new("assistant", MessageRole.Assistant, "fixture-model",
                [new ThinkingBlock("fixture reasoning"), new TextBlock("fixture answer")], null, true),
        ];

        foreach (var outcome in new SessionOutcome?[] { null, SessionOutcome.Completed, SessionOutcome.Failed, SessionOutcome.Interrupted })
        {
            var session = new ScriptedChatSession { Activity = SessionActivity.Idle, LastOutcome = outcome };
            using var timeline = new TimelineViewModel(session, SynchronousRefreshScheduler.Instance, new RecordingClipboardService());
            timeline.LoadHistory(History());
            var turn = timeline.Entries.OfType<AssistantMessageViewModel>().Single();
            var expected = outcome switch
            {
                SessionOutcome.Failed => TurnPhase.Failed,
                SessionOutcome.Interrupted => TurnPhase.Cancelled,
                _ => TurnPhase.Completed,
            };
            Check(turn.IsComplete && !turn.IsStreaming && turn.Phase == expected,
                $"空闲会话恢复末回合为终态（{outcome?.ToString() ?? "unknown"}）");
            var thinking = turn.Blocks.OfType<ThinkingBlockViewModel>().Single();
            Check(!thinking.IsLive && thinking.CanToggle && thinking.Text == "fixture reasoning",
                "已恢复思考块停止生成且可折叠");
            Check(turn.DurationMs is null, "不把回放耗时当成任务耗时");
            Check(session.Sent.Count == 0, "恢复历史没有发起新模型请求");
        }

        var running = new ScriptedChatSession { Activity = SessionActivity.Running };
        using (var timeline = new TimelineViewModel(running, SynchronousRefreshScheduler.Instance, new RecordingClipboardService()))
        {
            timeline.LoadHistory(History());
            var turn = timeline.Entries.OfType<AssistantMessageViewModel>().Single();
            Check(turn.IsStreaming && !turn.IsComplete, "运行中重接时间线保留活动回合");
            running.Raise(new SessionEvent.MessageStarted("next", MessageRole.Assistant, "fixture-model", 1));
            running.Raise(new SessionEvent.MessageFinished("next",
                new ChatMessage("next", MessageRole.Assistant, "fixture-model", [new TextBlock("next answer")], null, true), null));
            Check(timeline.Entries.OfType<AssistantMessageViewModel>().Count() == 1 &&
                  turn.Blocks.OfType<TextBlockViewModel>().Any(b => b.Text == "next answer"),
                "后续消息继续并入同一活动回合");
            running.Raise(new SessionEvent.TurnSettled(SessionOutcome.Completed, null));
            Check(turn.IsComplete && !turn.IsStreaming, "实际终态到达后活动回合正常收束");
        }
        return Task.CompletedTask;
    }
}
