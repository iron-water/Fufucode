using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Fufucode.App.Abstractions;
using Fufucode.App.Infrastructure;
using Fufucode.App.ViewModels;
using Fufucode.Core;

namespace Fufucode.App.SelfTest;

/// <summary>Run on the headless dispatcher: global action, A8 snapshot, preference and reliable notification behavior.</summary>
public static class CrossGlobalSelfTest
{
    public static async Task RunAsync(Action<string> pass, Action<string> fail)
    {
        void Check(bool condition, string description)
        {
            if (condition) pass("W5-R3 跨区全局：" + description);
            else fail("W5-R3 跨区全局：" + description);
        }

        var actionRuns = 0;
        var gated = true;
        var registry = new AppActionRegistry();
        registry.Register(new("files", "打开文件", "工作视图", "Ctrl+P", "浏览当前项目文件",
            () => { actionRuns++; return Task.CompletedTask; }, () => gated ? "项目尚未就绪" : null));
        registry.Register(new("settings", "打开设置", "导航", "", "外观与运行偏好", () => Task.CompletedTask));
        using (var palette = new CommandPaletteViewModel(registry))
        {
            var closeCount = 0;
            palette.CloseRequested += () => closeCount++;
            palette.Open();
            Check(palette.Items.Count == 2 && palette.Items.Count(item => item.ShowGroupHeader) == 2,
                "可用动作分组，禁用动作仍保留说明");
            palette.SearchText = "Ctrl+P";
            Check(palette.Items.Count == 1 && palette.SelectedItem?.Id == "files" &&
                palette.SelectedItem.DisabledReason == "项目尚未就绪", "搜索匹配实际快捷键且可读禁用原因");
            await palette.ExecuteSelectedCommand.ExecuteAsync(null);
            Check(actionRuns == 0 && palette.IsOpen && palette.Feedback == "项目尚未就绪" &&
                !registry.GetCommand("files").CanExecute(null), "面板和共享按钮能力门一致，禁用动作不执行");
            gated = false;
            registry.RefreshCapabilities();
            await palette.ExecuteSelectedCommand.ExecuteAsync(null);
            Check(actionRuns == 1 && !palette.IsOpen && closeCount == 1, "Enter对应命令执行后关闭并请求焦点返回");
            await registry.GetCommand("files").ExecuteAsync(null);
            var result = await registry.ExecuteAsync("files");
            Check(actionRuns == 3 && result.Executed, "按钮、快捷键和面板共用注册执行路径");
            palette.Open();
            var first = palette.SelectedItem;
            palette.MoveSelection(-1);
            palette.MoveSelection(1);
            Check(ReferenceEquals(first, palette.SelectedItem), "上下选择可往返，选择对象与行对应");
            palette.SearchText = "no-match";
            Check(palette.HasNoMatches && palette.SelectedItem is null, "无匹配为空态，不伪执行");
        }

        var fixtureRoot = Path.Combine(Path.GetTempPath(), "fufucode-cross-global-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtureRoot);
        try
        {
            var store = new NotificationPreferencesStore(Path.Combine(fixtureRoot, "notification-preferences.json"));
            var preferences = new NotificationPreferencesViewModel(store);
            preferences.SelectedCompletionPolicyIndex = 2;
            preferences.NotifyApprovals = false;
            var reloaded = new NotificationPreferencesViewModel(store);
            Check(reloaded.CompletionPolicy == CompletionNotificationPolicy.Always && !reloaded.NotifyApprovals &&
                preferences.SaveStatusText.Contains("已保存") && !preferences.HasSaveError, "偏好真实落盘后重建保持策略和审批开关");
            var blockedParent = Path.Combine(fixtureRoot, "blocked");
            File.WriteAllText(blockedParent, "fixture");
            var failedPreferences = new NotificationPreferencesViewModel(new NotificationPreferencesStore(
                Path.Combine(blockedParent, "preferences.json")));
            failedPreferences.SelectedCompletionPolicyIndex = 0;
            Check(failedPreferences.SelectedCompletionPolicyIndex == 1 && failedPreferences.HasSaveError &&
                failedPreferences.SaveStatusText.Contains("回退"), "偏好保存失败保留原值与失败说明");
        }
        finally { Directory.Delete(fixtureRoot, true); }

        var user = new PiMessage("user", [], null, null, null, default);
        var measuredZero = new PiMessage("assistant", [], "stop", null, new PiUsage(0, 0, 0, 0, 0, default), default);
        var large = new PiMessage("assistant", [], "stop", null, new PiUsage(4294967296, 12, null, 0, 4294967308, default), default);
        var missing = new PiMessage("assistant", [], "stop", null, null, default);
        var complete = SessionUsageQuery.Query([user, measuredZero]);
        var incomplete = SessionUsageQuery.Query([user, measuredZero, user, large, missing]);
        using (var usage = new UsageDetailsViewModel())
        {
            var queriedScope = "";
            usage.AttachContext("usage-one",
                _ => { queriedScope = "live"; return Task.FromResult(new UsageQuerySnapshot(incomplete,
                    "live-session-current-branch", false, DateTimeOffset.Now)); },
                _ => { queriedScope = "recorded"; return Task.FromResult(new UsageQuerySnapshot(incomplete,
                    "recorded-session-all-branches", false, DateTimeOffset.Now)); });
            await usage.RefreshAsync();
            var originalSubtotal = usage.Counters[0].Counter.KnownSubtotal;
            await usage.RefreshAsync();
            Check(usage.Counters[0].IsUnknown && originalSubtotal == 4294967296 &&
                usage.Counters[0].Counter.KnownSubtotal == originalSubtotal &&
                usage.Counters[0].KnownSubtotalText.Contains("不完整"), "unknown不伪零，Int64小计保留且重复查询不累加");
            usage.CurrentTurnCommand.Execute(null);
            await usage.RefreshAsync();
            Check(usage.SourceScope == "live-turn-current-branch" && usage.Counters[0].Counter.UnknownMessageCount == 1,
                "本轮只查询当前分支最近用户锚定轮次");
            usage.AllRecordedCommand.Execute(null);
            await usage.RefreshAsync();
            Check(queriedScope == "recorded" && usage.SourceScope == "recorded-session-all-branches" &&
                usage.Turns.Count == 2, "全部记录使用独立查询与范围标签");
            usage.ApplySnapshot(new(complete, "recorded-session-all-branches", true, DateTimeOffset.Now), UsageDetailScope.AllRecorded);
            Check(usage.Counters[0].ValueText == "暂无完整数据" && usage.Counters[0].Counter.Value == 0 &&
                usage.Counters[0].KnownSubtotalText.Contains("不完整") && usage.CompletenessText.Contains("尾部"),
                "partialTail只有已知前缀，不显示完整总数");
            usage.ApplySnapshot(new(complete, "live-session-current-branch", false, DateTimeOffset.Now), UsageDetailScope.CurrentSession);
            Check(usage.Counters.All(counter => counter.ValueText == "0" && !counter.IsUnknown), "确实测得的0保留为0");
            var cached = SessionUsageQuery.Query([user, new PiMessage("assistant", [], "stop", null,
                new PiUsage(20, 10, 60, 20, 110, default), default)]);
            usage.ApplySnapshot(new(cached, "live-session-current-branch", false, DateTimeOffset.Now), UsageDetailScope.CurrentSession);
            Check(usage.CacheShareText.Contains("60") && usage.Turns.Count == 1 && usage.Turns[0].Totals?.CacheRead.Value == 60,
                "production usage exposes measured cache share and per-turn input/output/cache breakdown");
            usage.ApplySnapshot(new(incomplete, "live-session-current-branch", false, DateTimeOffset.Now), UsageDetailScope.CurrentSession);
            Check(usage.CacheShareText.Contains("未知"), "production usage does not infer cache percentage from missing fields");
            var stale = new TaskCompletionSource<UsageQuerySnapshot>();
            usage.SelectedScopeIndex = 0;
            usage.AttachContext("old", _ => stale.Task, _ => stale.Task);
            var oldQuery = usage.RefreshAsync();
            usage.AttachContext("new", _ => Task.FromResult(new UsageQuerySnapshot(complete,
                "live-session-current-branch", false, DateTimeOffset.Now)),
                _ => Task.FromResult(new UsageQuerySnapshot(complete, "recorded-session-all-branches", false, DateTimeOffset.Now)));
            await usage.RefreshAsync();
            stale.SetResult(new(incomplete, "stale-old-session", false, DateTimeOffset.Now));
            await oldQuery;
            Check(usage.SessionId == "new" && usage.SourceScope == "live-session-current-branch" &&
                usage.Counters[0].ValueText == "0", "旧会话延迟查询不能覆盖新会话");
        }

        var noticePreferences = new NotificationPreferencesViewModel();
        using (var center = new NotificationCenterViewModel(noticePreferences))
        {
            var session = new ScriptedChatSession();
            var generation = Guid.NewGuid();
            center.Attach("session-one", generation, session);
            center.BeginSubmission();
            session.Raise(new SessionEvent.ActivityChanged(SessionActivity.Idle));
            session.Raise(new SessionEvent.MessageFinished("m", new ChatMessage("m", MessageRole.Assistant,
                null, [], null, true), null));
            await center.FlushAsync();
            Check(center.Items.Count == 0, "Idle和最终文本不产生完成通知");
            session.Raise(new SessionEvent.TurnSettled(SessionOutcome.Completed, "private summary ignored"));
            session.Raise(new SessionEvent.TurnSettled(SessionOutcome.Completed, null));
            await center.FlushAsync();
            Check(center.Items.Count == 1 && center.Items[0].Kind == AppNotificationKind.Completed &&
                center.LatestPrompt is null, "可靠终态同run只提醒一次，焦点内默认局部反馈");
            var originalRow = center.Items[0];
            center.SetWindowFocused(false);
            center.BeginSubmission();
            session.Raise(new SessionEvent.TurnSettled(SessionOutcome.Completed, null));
            await center.FlushAsync();
            Check(center.Items.Count == 2 && center.LatestPrompt?.IsProactive == true, "失焦策略提供页内主动提示");
            center.SetWindowFocused(true);
            noticePreferences.SelectedCompletionPolicyIndex = 0;
            center.SetWindowFocused(false);
            center.BeginSubmission();
            session.Raise(new SessionEvent.TurnSettled(SessionOutcome.Failed, "secret input is not retained"));
            session.Raise(new SessionEvent.SessionError(SessionErrorKind.ProcessExited, "detail", null));
            await center.FlushAsync();
            Check(center.Items.Count == 3 && center.Items[0].IsFailed && center.LatestPrompt is null &&
                !center.Items[0].Message.Contains("secret"), "不提醒策略保留失败标记，fault和失败终态不双发且不存敏感参数");
            center.BeginSubmission();
            session.Raise(new SessionEvent.TurnSettled(SessionOutcome.Unknown, null));
            session.Raise(new SessionEvent.TurnSettled(SessionOutcome.Completed, null));
            center.BeginSubmission();
            session.Raise(new SessionEvent.TurnSettled(SessionOutcome.Interrupted, null));
            await center.FlushAsync();
            Check(center.Items.Count == 3, "Unknown和Interrupted均不发成功通知");
            var rejected = center.BeginSubmission();
            center.CancelSubmission(rejected);
            session.Raise(new SessionEvent.TurnSettled(SessionOutcome.Completed, null));
            await center.FlushAsync();
            Check(center.Items.Count == 3, "拒绝的空闲提交不冒充已执行任务");
            using var payload = JsonDocument.Parse("{}");
            var request = new ExtensionUiRequest(generation, "approval-one", "confirm", payload.RootElement.Clone(),
                DateTimeOffset.Now, TimeSpan.FromMinutes(1));
            center.ObserveApproval(request);
            center.ObserveApproval(request);
            await center.FlushAsync();
            Check(center.PendingActionCount == 1 && center.Items.Count == 4 && center.Items[0].IsApproval,
                "待审批有独立类别、可操作标记并按请求去重");
            noticePreferences.NotifyApprovals = false;
            center.DismissPromptCommand.Execute(null);
            center.ObserveApproval(request with { Id = "approval-muted" });
            await center.FlushAsync();
            Check(center.PendingActionCount == 2 && center.LatestPrompt is null, "关闭审批主动提醒不隐藏待处理事项");
            center.ObserveApproval(request with { Generation = Guid.NewGuid(), Id = "stale-approval" });
            await center.FlushAsync();
            Check(center.Items.Count == 5, "旧连接审批不污染当前代次");
            var other = new ScriptedChatSession();
            center.Attach("session-two", Guid.NewGuid(), other);
            session.Raise(new SessionEvent.TurnSettled(SessionOutcome.Completed, null));
            await center.FlushAsync();
            Check(center.Items.Count == 5 && center.PendingActionCount == 0, "切会话退订旧事件并撤下过期操作标记");
            string? routed = null;
            center.SessionRequested += id => routed = id;
            center.ActivateCommand.Execute(originalRow);
            Check(routed == "session-one" && originalRow.IsRead, "点击按通知所属会话路由，当前会话不会覆盖归属");
            noticePreferences.SelectedCompletionPolicyIndex = 2;
            center.SetWindowFocused(true);
            center.BeginSubmission();
            other.Raise(new SessionEvent.TurnSettled(SessionOutcome.Completed, null));
            await center.FlushAsync();
            Check(center.LatestPrompt?.SessionId == "session-two", "总是策略在焦点内仍可显示主动反馈");
            other.Raise(new SessionEvent.ConnectionChanged(SessionConnection.Faulted));
            other.Raise(new SessionEvent.SessionError(SessionErrorKind.ProcessExited, "process detail", null));
            await center.FlushAsync();
            Check(center.Items.Count == 7 && center.Items[0].IsFailed && center.Items[1].IsCompleted,
                "成功终态之后独立连接故障单独显示一次，保留原任务成功记录");
        }
    }
}
