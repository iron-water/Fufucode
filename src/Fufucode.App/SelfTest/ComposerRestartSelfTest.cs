using System;
using System.Threading.Tasks;
using Avalonia.Threading;
using Fufucode.App.Abstractions;
using Fufucode.App.ViewModels;

namespace Fufucode.App.SelfTest;

/// <summary>
/// 输入区同位三态：发送 / 停止 / 重启。
///
/// restart 只在「上一回合非正常收束且没有新草稿」时出现；点下去是把最后一条用户消息
/// 重投同一会话（历史里已有半成品上下文），不是新开会话，也不伪造"已续跑"。
/// </summary>
public static class ComposerRestartSelfTest
{
    public static async Task RunAsync(Action<string> pass, Action<string> fail)
    {
        void Check(bool ok, string label)
        {
            if (ok) pass("输入区 restart：" + label);
            else fail("输入区 restart：" + label);
        }

        var session = new ScriptedChatSession
        {
            Activity = SessionActivity.Idle,
            LastOutcome = SessionOutcome.Interrupted,
            LastUserPrompt = "继续做 A3 版本历史",
        };
        var input = new InputViewModel(session);
        await Dispatcher.Yield(DispatcherPriority.Background);

        Check(input.CanRestart && input.ShowRestartButton && !input.ShowSendButton && !input.ShowStopButton,
              "中断收束且无新草稿：同位显示 restart，发送让位");

        // 正常完成不该出现 restart，否则等于鼓励模型重跑已成功的回合。
        session.LastOutcome = SessionOutcome.Completed;
        session.Raise(new SessionEvent.TurnSettled(SessionOutcome.Completed, "已完成"));
        await Dispatcher.Yield(DispatcherPriority.Background);
        Check(!input.CanRestart && input.ShowSendButton, "正常完成后恢复为发送");

        session.LastOutcome = SessionOutcome.Failed;
        session.Raise(new SessionEvent.TurnSettled(SessionOutcome.Failed, "模型请求失败"));
        await Dispatcher.Yield(DispatcherPriority.Background);
        Check(input.CanRestart, "失败收束后 restart 重新出现");

        // 用户已经在新输入内容时，发送优先于重启，不能抢位置。
        input.DraftText = "我换个做法";
        Check(!input.ShowRestartButton && input.ShowSendButton, "有草稿时让位给发送");
        input.DraftText = string.Empty;

        // 运行中只有停止：三态互斥，不能同时显示两个按钮。
        session.Activity = SessionActivity.Running;
        session.Raise(new SessionEvent.ActivityChanged(SessionActivity.Running));
        await Dispatcher.Yield(DispatcherPriority.Background);
        Check(input.ShowStopButton && !input.ShowRestartButton && !input.ShowSendButton,
              "运行中只显示停止");

        // 真正按下 restart：重投最后一条用户消息，而不是新内容。
        session.Activity = SessionActivity.Idle;
        session.Raise(new SessionEvent.ActivityChanged(SessionActivity.Idle));
        await Dispatcher.Yield(DispatcherPriority.Background);
        input.RestartCommand.Execute(null);
        await Dispatcher.Yield(DispatcherPriority.Background);
        Check(session.Sent.Count == 1 && session.Sent[0] == "继续做 A3 版本历史",
              "restart 重发最后一条用户消息原文");

        // 后端没有这个可选能力（取不到最后一条）时，restart 不出现，发送照常。
        var blind = new ScriptedChatSession
        {
            Activity = SessionActivity.Idle,
            LastOutcome = SessionOutcome.Interrupted,
            LastUserPrompt = null,
        };
        var blindInput = new InputViewModel(blind);
        await Dispatcher.Yield(DispatcherPriority.Background);
        Check(!blindInput.CanRestart && blindInput.ShowSendButton, "无可重发内容时不显示 restart");
    }
}
