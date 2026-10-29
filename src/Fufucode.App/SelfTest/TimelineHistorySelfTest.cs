using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls.Documents;
using Fufucode.App.Abstractions;
using Fufucode.App.Infrastructure;
using Fufucode.App.ViewModels;
using Fufucode.App.Views;

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

        // 长历史只实例化最近一段；上翻时不从阅读位置上方抽走条目。
        var bulk = new ScriptedChatSession { Activity = SessionActivity.Idle };
        using (var timeline = new TimelineViewModel(bulk, SynchronousRefreshScheduler.Instance, new RecordingClipboardService()))
        {
            timeline.LoadHistory(Enumerable.Range(0, 30)
                .Select(index => new ChatMessage($"bulk-{index}", MessageRole.User, null,
                    [new TextBlock($"bulk {index}")], null, true))
                .ToArray());
            Check(timeline.Entries.Count == 30 && timeline.VisibleEntries.Count == 20 && timeline.HiddenEntryCount == 10,
                "长历史只渲染最近一段并保留全部数据");

            timeline.LoadEarlier();
            Check(timeline.VisibleEntries.Count == 30 && !timeline.HasHiddenEntries,
                "加载更早展开剩余条目");

            timeline.SetAutoScrollAtBottom(false);
            timeline.Entries.Add(new UserMessageViewModel { Id = "bulk-extra", Text = "extra" });
            Check(timeline.VisibleEntries.Count == 31,
                "上翻阅读时不从窗口顶部抽走内容");

            timeline.SetAutoScrollAtBottom(true);
            Check(timeline.VisibleEntries.Count == 20 && timeline.HiddenEntryCount == 11,
                "回到最新后重新裁到窗口上限");
        }

        var jump = new ScriptedChatSession { Activity = SessionActivity.Idle };
        using (var timeline = new TimelineViewModel(jump, SynchronousRefreshScheduler.Instance, new RecordingClipboardService()))
        {
            timeline.LoadHistory(Enumerable.Range(0, 30)
                .Select(index => new ChatMessage($"jump-{index}", MessageRole.User, null,
                    [new TextBlock($"jump {index}")], null, true))
                .ToArray());
            var hidden = timeline.Entries[0];
            Check(!timeline.VisibleEntries.Contains(hidden) &&
                  timeline.EnsureEntryVisible(hidden) &&
                  timeline.HiddenEntryCount == 0 &&
                  timeline.VisibleEntries.Count == 30,
                "跳转到窗口外条目时按需展开");
        }

        var group = new ToolGroupViewModel("group");
        group.Add(new ToolCallBlockViewModel { ToolName = "read_file", Status = ToolCallStatus.Completed });
        Check(group.VisibleTools.Count == 0, "折叠工具组不实例化子卡片");
        group.IsExpanded = true;
        Check(group.VisibleTools.Count == 1, "展开工具组时投影子卡片");
        group.IsExpanded = false;
        Check(group.VisibleTools.Count == 0, "收起工具组时释放子卡片");

        var output = new ToolOutputView
        {
            Output = string.Join('\n', Enumerable.Range(0, 100).Select(index => $"line {index}")),
            IsExpanded = false,
        };
        var collapsed = string.Join("\n", output.Inlines!.OfType<Run>().Select(run => run.Text));
        Check(collapsed.Contains("已折叠 70 行", StringComparison.Ordinal) &&
              !collapsed.Contains("line 50", StringComparison.Ordinal),
            "折叠工具输出只保留首尾行");
        output.IsExpanded = true;
        var expanded = string.Join("\n", output.Inlines!.OfType<Run>().Select(run => run.Text));
        Check(expanded.Contains("line 50", StringComparison.Ordinal) &&
              !expanded.Contains("已折叠", StringComparison.Ordinal),
            "展开工具输出恢复完整文本");

        return Task.CompletedTask;
    }
}
