using System.Text.Json;
using Fufucode.Core.Automation.Rebirth;
using Fufucode.Core.Backend;
using Fufucode.Core.Plugins;
using Fufucode.Plugins.AutoDev;

// AutoDev rebirth kernel acceptance harness. It uses a fresh temporary SQLite database,
// never starts a model or an executor, and exercises every public command/read surface.

var failures = new List<string>();
var passed = 0;
void Check(bool condition, string name)
{
    if (condition) { passed++; Console.WriteLine("PASS " + name); return; }
    failures.Add(name); Console.WriteLine("FAIL " + name);
}
void Throws<T>(Action action, string name) where T : Exception
{
    try { action(); failures.Add(name); Console.WriteLine("FAIL " + name + " (no throw)"); }
    catch (T) { passed++; Console.WriteLine("PASS " + name); }
    catch (Exception error) { failures.Add(name); Console.WriteLine("FAIL " + name + " (" + error.GetType().Name + ")"); }
}
string TempDb()
{
    var dir = Path.Combine(Path.GetTempPath(), "fufucode-rebirth-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    return Path.Combine(dir, "kernel.db");
}
long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

try
{
    using var kernel = new RebirthKernel(TempDb());
    var now = Now();
    var brief = new RebirthBrief(
        "让会话切换稳定",
        ["切换 200 次不白屏、不丢输入"],
        ["不重写会话架构", "不改变数据格式"],
        ["不允许全局 UI 锁"],
        ["切换 200 次无白屏", "焦点回到正确位置"],
        ["src/App/Session/**", "tests/session/**"],
        600,
        RebirthAutonomy.WithinMandate);
    var project = kernel.CreateProject("cmd-project", "会话切换稳定性", brief, "human", now, "project-1");
    Check(project.Status == RebirthProjectStatus.Active && project.BriefVersion == 1 && project.Revision == 1,
        "创建项目写入 Active 状态、Brief v1 和 revision 1");

    var replay = kernel.CreateProject("cmd-project", "会话切换稳定性", brief, "human", now, "project-1");
    Check(replay.Revision == project.Revision, "重复 commandId 是回放，不重复创建项目");

    Throws<InvalidOperationException>(() => kernel.CreateProject("cmd-project", "另一个项目", brief, "human", now, "project-1"),
        "同一 commandId 不同输入被拒绝");

    var t1 = kernel.CreateTicket("cmd-t1", "project-1", "诊断与切换契约",
        "复现并冻结切换契约", ["200 次无白屏", "焦点恢复"], "generation-token", "ticket-lead", now);
    var t2 = kernel.CreateTicket("cmd-t2", "project-1", "全局锁方案",
        "用全局锁实现切换", ["200 次无白屏"], "global-lock", "ticket-lead", now);
    Check(t1.Status == RebirthTicketStatus.Authorized && t2.Status == RebirthTicketStatus.Authorized,
        "WithinMandate 下模型创建的工单直接进入 Authorized，不需要逐项批准");

    var veto = kernel.RecordVeto("cmd-veto", "project-1", null, "route:global-lock",
        "路线约束", "不要全局锁，不能牺牲切换速度", "human", now);
    Check(veto.TicketsReplanned == 1 && veto.Decision.Status == "Active",
        "否决路线只重规划匹配的工单，并留下 Active 约束");
    var afterVeto = kernel.ReadSnapshot("project-1");
    Check(afterVeto.Tickets.Single(t => t.Id == t2.Id).Status == RebirthTicketStatus.ReplanRequired &&
          afterVeto.Tickets.Single(t => t.Id == t1.Id).Status == RebirthTicketStatus.Authorized,
        "被否决路线进入 ReplanRequired，其他工单继续");

    Throws<InvalidOperationException>(() => kernel.StartRun("cmd-t2-run", t2.Id, "session", "fumo", "C:/wt", now),
        "被否决路线不能启动 Run");

    var r1 = kernel.StartRun("cmd-r1", t1.Id, "session", "fumo", "C:/wt", now);
    Check(r1.Status == RebirthRunStatus.Running, "工单启动 Run 后进入 Running");
    Throws<InvalidOperationException>(() => kernel.StartRun("cmd-r1-second", t1.Id, "session", "fumo", "C:/wt", now),
        "同一工单不能同时有两个活动 Run");

    var completed = kernel.CompleteRun("cmd-r1-complete", r1.Id, RebirthRunOutcome.Completed, "exit-0", "build + tests", now);
    Check(completed.Status == RebirthRunStatus.Completed, "完成 Run 写入真实终态并结束");
    Check(kernel.ReadSnapshot("project-1").Tickets.Single(t => t.Id == t1.Id).Status == RebirthTicketStatus.Review,
        "Run 成功只进入 Review，不自动完成工单");

    Throws<InvalidOperationException>(() => kernel.CompleteTicket("cmd-t1-complete-missing", t1.Id, now),
        "缺少验收证据时不能完成工单");

    var e1 = kernel.AddEvidence("cmd-e1", t1.Id, r1.Id, RebirthEvidenceKind.Test,
        "200 次无白屏", "tests/session/rapid-switch.log", true, "test-runner", now);
    Check(e1.Passed && e1.Valid, "测试证据写入并绑定 Run");
    var e2 = kernel.AddEvidence("cmd-e2", t1.Id, r1.Id, RebirthEvidenceKind.Test,
        "焦点恢复", "tests/session/focus.log", true, "test-runner", now);
    Check(e2.Passed, "第二条验收证据独立写入");
    var doneT1 = kernel.CompleteTicket("cmd-t1-complete", t1.Id, now);
    Check(doneT1.Status == RebirthTicketStatus.Done, "所有验收条件均有通过证据后工单完成");
    Throws<InvalidOperationException>(() => kernel.CompleteProject("cmd-project-complete-early", "project-1", now),
        "仍有未完成工单时不能完成项目");

    var query = kernel.RecordHumanInput("cmd-input-query", "project-1", t2.Id, "human",
        RebirthInputKind.Suggestion, "也许可以用代际令牌", now);
    Check(query.State == RebirthInputState.Delivered && query.DeliveredUtc == now && query.ReadUtc is null,
        "人的建议先记录为已投递，不伪造已读");
    var read = kernel.MarkInputRead("cmd-input-read", query.Id, now);
    Check(read.State == RebirthInputState.Read && read.ReadUtc == now, "执行者读取后更新为 Read");
    var applied = kernel.ApplyInput("cmd-input-apply", query.Id, now);
    Check(applied.State == RebirthInputState.Applied && applied.AppliedUtc == now, "输入被应用后更新为 Applied");

    var lifted = kernel.LiftVeto("cmd-lift", "project-1", veto.Decision.Id, "human", now);
    Check(lifted.Status == "Lifted", "人的否决可以显式解除，并保留历史");
    Throws<InvalidOperationException>(() => kernel.StartRun("cmd-t2-run-still", t2.Id, "session", "fumo", "C:/wt", now),
        "解除否决但工单仍在 ReplanRequired 时不能直接启动");
    var resumed = kernel.ResumeTicket("cmd-resume-t2", t2.Id, "ticket-lead", now);
    Check(resumed.Status == RebirthTicketStatus.Authorized, "重规划完成后显式恢复工单");

    var r2 = kernel.StartRun("cmd-r2", t2.Id, "session", "fumo", "C:/wt", now);
    kernel.CompleteRun("cmd-r2-complete", r2.Id, RebirthRunOutcome.Completed, "exit-0", "ok", now);
    kernel.AddEvidence("cmd-e3", t2.Id, r2.Id, RebirthEvidenceKind.Test,
        "200 次无白屏", "tests/session/global-lock.log", true, "test-runner", now);
    Check(kernel.CompleteTicket("cmd-t2-complete", t2.Id, now).Status == RebirthTicketStatus.Done,
        "恢复后的工单可以继续完成");

    var t3 = kernel.CreateTicket("cmd-t3", "project-1", "体验验收",
        "骨架屏和焦点验收", ["骨架屏可见", "焦点正确"], "ui-polish", "ticket-lead", now);
    var attention = kernel.CreateAttention("cmd-attention", t3.Id, "极快切换时允许 100ms 骨架屏吗？", now);
    Check(attention.Status == RebirthAttentionStatus.Open, "结构化 Attention 处于 Open");
    Check(kernel.ReadSnapshot("project-1").Tickets.Single(t => t.Id == t3.Id).Status == RebirthTicketStatus.WaitingPerson,
        "需要人回答时工单进入 WaitingPerson");
    Throws<InvalidOperationException>(() => kernel.StartRun("cmd-t3-run-blocked", t3.Id, "session", "fumo", "C:/wt", now),
        "等待人的工单不能偷偷启动 Run");
    var answer = kernel.AnswerAttention("cmd-answer", attention.Id, "可以，但不能丢输入", now);
    Check(answer.Status == RebirthAttentionStatus.Answered, "人的回答记录为 Answered");
    Check(kernel.ReadSnapshot("project-1").Tickets.Single(t => t.Id == t3.Id).Status == RebirthTicketStatus.Active,
        "回答后工单恢复 Active");

    var r3 = kernel.StartRun("cmd-r3", t3.Id, "session", "fumo", "C:/wt", now);
    kernel.CompleteRun("cmd-r3-complete", r3.Id, RebirthRunOutcome.Completed, "exit-0", "ok", now);
    kernel.AddEvidence("cmd-e4", t3.Id, r3.Id, RebirthEvidenceKind.Review,
        "骨架屏可见", "review/skeleton.md", true, "reviewer", now);
    Throws<InvalidOperationException>(() => kernel.CompleteTicket("cmd-t3-complete-missing", t3.Id, now),
        "多验收条件缺一条证据仍不能完成");
    kernel.AddEvidence("cmd-e5", t3.Id, r3.Id, RebirthEvidenceKind.Test,
        "焦点正确", "tests/session/focus2.log", true, "test-runner", now);
    Check(kernel.CompleteTicket("cmd-t3-complete", t3.Id, now).Status == RebirthTicketStatus.Done,
        "补足全部证据后工单完成");

    var t4 = kernel.CreateTicket("cmd-t4", "project-1", "未知终态恢复", "验证未知 Run 不自动重试", ["人工核对"], "recovery", "ticket-lead", now);
    var r4 = kernel.StartRun("cmd-r4", t4.Id, "session", "fumo", "C:/wt", now);
    kernel.CompleteRun("cmd-r4-complete", r4.Id, RebirthRunOutcome.Unknown, "unknown", "host restart", now);
    Check(kernel.ReadSnapshot("project-1").Tickets.Single(t => t.Id == t4.Id).Status == RebirthTicketStatus.NeedsReconciliation,
        "未知 Run 进入 NeedsReconciliation，不自动完成");
    Throws<InvalidOperationException>(() => kernel.StartRun("cmd-r4-retry", t4.Id, "session", "fumo", "C:/wt", now),
        "未知终态不能自动启动新 Run");
    Check(kernel.ReconcileTicket("cmd-reconcile-t4", t4.Id, "检查现场后从干净基线继续", now).Status == RebirthTicketStatus.Authorized,
        "未知终态需要显式对账后才能继续");
    var stopInput = kernel.RecordHumanInput("cmd-input-stop", "project-1", t4.Id, "human",
        RebirthInputKind.Stop, "停止这个工单", now);
    Check(stopInput.State == RebirthInputState.Applied && stopInput.AppliedUtc == now,
        "人的停止输入直接成为 Applied，并撤回工单");

    var t5 = kernel.CreateTicket("cmd-t5", "project-1", "人工接管", "验证接管", ["接管后不自动派发"], "manual", "ticket-lead", now);
    kernel.StartRun("cmd-r5", t5.Id, "session", "fumo", "C:/wt", now);
    var takeover = kernel.TakeoverTicket("cmd-takeover", t5.Id, "human", now);
    Check(takeover.Status == RebirthTicketStatus.HumanOwned, "接管后工单进入 HumanOwned");
    Throws<InvalidOperationException>(() => kernel.StartRun("cmd-r5-blocked", t5.Id, "session", "fumo", "C:/wt", now),
        "人工拥有的工单不能被模型继续启动");
    kernel.StopTicket("cmd-t5-stop", t5.Id, "人工接管后取消", "human", now);

    var updatedBrief = brief with { Constraints = ["不允许全局 UI 锁", "不能改变数据格式"] };
    var briefV2 = kernel.UpdateBrief("cmd-brief-v2", "project-1", 1, updatedBrief, "human", now);
    Check(briefV2.Version == 2, "Brief 更新产生新版本");
    Throws<InvalidOperationException>(() => kernel.UpdateBrief("cmd-brief-stale", "project-1", 1, brief, "human", now),
        "过期 Brief 版本被拒绝");

    var final = kernel.CompleteProject("cmd-project-complete", "project-1", now);
    Check(final.Status == RebirthProjectStatus.Completed, "全部工单完成/取消后项目完成");
    var snap = kernel.ReadSnapshot("project-1");
    Check(snap.Tickets.Count == 5 && snap.Brief.Version == 2, "快照返回真实工单和当前 Brief");
    Check(snap.Decisions.Any(d => d.Kind == RebirthDecisionKind.Veto && d.Status == "Lifted"),
        "否决历史保留，不因解除而消失");
    Check(snap.Inputs.Count >= 2 && snap.Attention.Single().Status == RebirthAttentionStatus.Answered,
        "输入和 Attention 历史完整可读");
}
catch (Exception error)
{
    failures.Add("uncaught exception: " + error);
    Console.WriteLine("FAIL uncaught exception: " + error);
}

    // ---- 工具接口：AutoDevToolHandler 走真实内核，不返回假“已完成” ----
    try
    {
        var root = Path.Combine(Path.GetTempPath(), "fufucode-rebirth-tools-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var context = new PluginPackageContext("automation.auto-dev", root,
            Path.Combine(root, "workspace"), null, root);
        var tools = new AutoDevToolHandler(context);
        var status0 = await tools.ExecuteAsync(new PureToolCall("tool-status-0", "autodev_status", "{}"));
        Check(status0.Success && status0.Content.Contains("uninitialized", StringComparison.Ordinal),
            "工具状态在未建项目时明确返回 uninitialized");

        var plan = await tools.ExecuteAsync(new PureToolCall("tool-plan", "autodev_plan",
            "{\"goal\":\"稳定会话切换\",\"steps\":[\"验证切换焦点\"]}"));
        Check(plan.Success && plan.Content.Contains("tickets", StringComparison.Ordinal),
            "autodev_plan 通过工具写入真实项目与工单");
        using var planDoc = JsonDocument.Parse(plan.Content);
        var ticketId = planDoc.RootElement.GetProperty("tickets")[0].GetProperty("id").GetString()!;

        var veto = await tools.ExecuteAsync(new PureToolCall("tool-veto", "autodev_veto",
            "{\"target\":\"route:default\",\"scope\":\"路线\",\"text\":\"不要默认路线\"}"));
        Check(veto.Success && veto.Content.Contains("ticketsReplanned", StringComparison.Ordinal),
            "autodev_veto 通过工具写入真实否决");
        using var vetoDoc = JsonDocument.Parse(veto.Content);
        var decisionId = vetoDoc.RootElement.GetProperty("decision").GetProperty("id").GetString()!;

        var lift = await tools.ExecuteAsync(new PureToolCall("tool-lift", "autodev_lift_veto",
            "{\"decisionId\":\"" + decisionId + "\"}"));
        Check(lift.Success && lift.Content.Contains("Lifted", StringComparison.Ordinal), "autodev_lift_veto 解除真实否决");

        var resume = await tools.ExecuteAsync(new PureToolCall("tool-resume", "autodev_resume",
            "{\"ticketId\":\"" + ticketId + "\"}"));
        Check(resume.Success, "autodev_resume 恢复重规划工单");

        var run = await tools.ExecuteAsync(new PureToolCall("tool-run", "autodev_run_start",
            "{\"ticketId\":\"" + ticketId + "\",\"executor\":\"session\",\"model\":\"fumo\",\"workspace\":\"" +
            Path.Combine(root, "workspace").Replace("\\", "\\\\") + "\"}"));
        Check(run.Success && run.Content.Contains("Running", StringComparison.Ordinal), "autodev_run_start 返回真实 Running");
        using var runDoc = JsonDocument.Parse(run.Content);
        var runId = runDoc.RootElement.GetProperty("id").GetString()!;

        var finish = await tools.ExecuteAsync(new PureToolCall("tool-finish", "autodev_run_finish",
            "{\"runId\":\"" + runId + "\",\"outcome\":\"completed\",\"terminalCode\":\"exit-0\",\"detail\":\"ok\"}"));
        Check(finish.Success && finish.Content.Contains("Completed", StringComparison.Ordinal), "autodev_run_finish 写入真实终态");

        var evidence = await tools.ExecuteAsync(new PureToolCall("tool-evidence", "autodev_evidence",
            "{\"ticketId\":\"" + ticketId + "\",\"runId\":\"" + runId +
            "\",\"kind\":\"test\",\"criterion\":\"验证切换焦点\",\"reference\":\"tmp/test.log\",\"passed\":true,\"verifier\":\"tests\"}"));
        Check(evidence.Success && evidence.Content.Contains("true", StringComparison.Ordinal), "autodev_evidence 写入真实通过证据");

        var complete = await tools.ExecuteAsync(new PureToolCall("tool-complete", "autodev_complete_ticket",
            "{\"ticketId\":\"" + ticketId + "\"}"));
        Check(complete.Success && complete.Content.Contains("Done", StringComparison.Ordinal), "autodev_complete_ticket 只有证据齐全才完成");

        var status1 = await tools.ExecuteAsync(new PureToolCall("tool-status-1", "autodev_status", "{}"));
        Check(status1.Success && status1.Content.Contains("Done", StringComparison.Ordinal), "autodev_status 读到完成工单");
    }
    catch (Exception error)
    {
        failures.Add("tool interface: " + error);
        Console.WriteLine("FAIL tool interface: " + error);
    }

Console.WriteLine($"\n通过 {passed} 项，失败 {failures.Count} 项。");
if (failures.Count > 0)
{
    foreach (var failure in failures) Console.WriteLine(" - " + failure);
    Environment.ExitCode = 1;
}
