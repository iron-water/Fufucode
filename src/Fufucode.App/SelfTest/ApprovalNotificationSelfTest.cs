using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Fufucode.App.Abstractions;
using Fufucode.App.Integration;
using Fufucode.App.Infrastructure;
using Fufucode.App.ViewModels;
using Fufucode.Core;

namespace Fufucode.App.SelfTest;

/// <summary>
/// 系统通知审批按钮的无头验收：链接解析、规则落盘、横幅 XML 三个按钮。
/// 这些路径不依赖真实 Windows 通知中心，可全部在无头进程内断言。
/// </summary>
internal static class ApprovalNotificationSelfTest
{
    internal static async Task RunAsync(Action<string> pass, Action<string> fail)
    {
        void Check(bool value, string name) { if (value) pass("ApprovalNotification: " + name); else fail("ApprovalNotification: " + name); }

        var session = Guid.NewGuid().ToString();
        var root = Path.Combine(Path.GetTempPath(), "fufucode-approval-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        // 1) 链接：三种决定都能往返，且拒绝非法输入。
        foreach (var decision in new[] { ApprovalDecision.Allow, ApprovalDecision.AlwaysAllow, ApprovalDecision.Deny })
        {
            var link = ApprovalLink.Create("req-1", session, decision, root);
            Check(ApprovalLink.TryParse(link, out var parsed) && parsed.RequestId == "req-1" &&
                  parsed.SessionId == session && parsed.Decision == decision &&
                  parsed.ProjectPath == Path.GetFullPath(root), "round-trip " + decision);
        }
        // scheme 随渠道变化（正式 fufucode-approval / 测试 fufucode-test-approval），断言按当前渠道拼。
        var approvalPrefix = ApprovalLink.Scheme + "://approval/";
        Check(!ApprovalLink.TryParse(approvalPrefix + "req-1?session=" + session + "&decision=maybe", out _) &&
              !ApprovalLink.TryParse(approvalPrefix + "../x?session=" + session + "&decision=allow", out _) &&
              !ApprovalLink.TryParse(approvalPrefix + "req-1?session=not-a-guid&decision=allow", out _) &&
              !ApprovalLink.TryParse(approvalPrefix + "req-1?session=" + session + "&decision=allow&extra=1", out _) &&
              !ApprovalLink.TryParse("https://approval/req-1", out _),
            "malformed or spoofed approval links are rejected");
        Check(!ApprovalLink.TryParse(ApprovalLink.Create("req-1", session, ApprovalDecision.Allow)
              .Replace(approvalPrefix, ProductLinks.SessionScheme + "://session/"), out _),
            "an approval link is not mistaken for a session link");

        // 2) “始终允许”规则：按项目 + 工具名落盘，其它项目/其它工具不受影响。
        ApprovalPolicy.Configure(root);
        var request = ApprovalRequest("run_command");
        Check(!ApprovalPolicy.IsAlwaysAllowed(request, root), "no rule means the tool still asks");
        Check(ApprovalPolicy.Remember(request, root) && ApprovalPolicy.IsAlwaysAllowed(request, root),
            "always-allow persists for the project and tool");
        Check(ApprovalPolicy.IsAlwaysAllowed(request, root + Path.DirectorySeparatorChar),
            "a trailing separator does not create a second rule");
        Check(!ApprovalPolicy.IsAlwaysAllowed(ApprovalRequest("write_file"), root) &&
              !ApprovalPolicy.IsAlwaysAllowed(request, Path.Combine(root, "other")),
            "the rule does not widen to other tools or other projects");
        var rulesFile = Path.Combine(root, "approval-rules.json");
        Check(File.Exists(rulesFile), "rules are written to the app data root");
        // 换一个数据根重新加载同一份规则内容：规则仍按项目生效，说明落盘字段可回读。
        var reloadedRoot = Path.Combine(root, "reload");
        Directory.CreateDirectory(reloadedRoot);
        File.Copy(rulesFile, Path.Combine(reloadedRoot, "approval-rules.json"));
        ApprovalPolicy.Configure(reloadedRoot);
        Check(ApprovalPolicy.IsAlwaysAllowed(request, root), "rules survive a reload from disk");
        File.WriteAllText(Path.Combine(root, "approval-rules.json"), "{ not json");
        ApprovalPolicy.Configure(Path.Combine(root, "fresh"));
        Check(!ApprovalPolicy.IsAlwaysAllowed(request, root), "a corrupt rule file never silently allows tools");

        // 3) 系统通知：三个按钮各自带独立动作链接，且都在协议允许的长度内。
        var actions = new[]
        {
            new WindowsToastAction("允许", ApprovalLink.Create("req-1", session, ApprovalDecision.Allow)),
            new WindowsToastAction("始终允许", ApprovalLink.Create("req-1", session, ApprovalDecision.AlwaysAllow)),
            new WindowsToastAction("拒绝", ApprovalLink.Create("req-1", session, ApprovalDecision.Deny)),
        };
        var xml = WindowsToastService.CreateXml("有一项工具待批准", "允许执行 run_command？", "fufucode://session/x?project=C%3A",
            actions);
        var document = System.Xml.Linq.XDocument.Parse(xml);
        var buttons = document.Root?.Element("actions")?.Elements("action").ToArray() ?? [];
        Check(buttons.Length == 3 && buttons.Select(button => button.Attribute("content")?.Value).SequenceEqual(["允许", "始终允许", "拒绝"]),
            "the Windows toast carries allow / always-allow / deny");
        Check(buttons.All(button => button.Attribute("activationType")?.Value == "protocol") &&
              buttons.Select(button => button.Attribute("arguments")?.Value).All(value => ApprovalLink.TryParse(value, out _)),
            "each toast button activates through a valid protocol link");
        Check(WindowsToastService.CreateXml("标题", "正文", null).Contains("<actions", StringComparison.Ordinal) == false,
            "toasts without an approval decision carry no buttons");

        // 4) 端到端：待批工具 → 通知条目带工具名 → 按钮链接 → 宿主按请求 Id 应答。
        var stub = new StubExtensionUiSession();
        var stubProject = Path.Combine(root, "project");
        Directory.CreateDirectory(stubProject);
        ApprovalPolicy.Configure(Path.Combine(root, "e2e"));
        var dialogs = new ExtensionUiDialogHost(owner: null) { ProjectPath = stubProject };
        dialogs.AlwaysAllow = pending => ApprovalPolicy.IsAlwaysAllowed(pending, dialogs.ProjectPath);
        dialogs.Attach(stub);
        var toolRequest = ToolRequest("run_command");
        stub.RaiseRequest(toolRequest);

        var center = new NotificationCenterViewModel(new NotificationPreferencesViewModel(
            new NotificationPreferencesStore(Path.Combine(root, "notification-preferences.json"))));
        // 通知中心只接受与连接代数一致的请求：用请求自己的 generation 挂接。
        var e2eSession = Guid.NewGuid().ToString();
        center.Attach(e2eSession, toolRequest.Generation, stub);
        await center.FlushAsync();
        var notice = center.Items.FirstOrDefault(item => item.RequestId == toolRequest.Id);
        Check(notice is { IsToolApproval: true, ToolName: "run_command" } &&
              notice.Message.Contains("run_command", StringComparison.Ordinal),
            "a pending tool approval becomes a tool-specific notice");

        // 系统通知上的“始终允许”链接：解析后按请求 Id 应答，并落规则。
        var alwaysLink = ApprovalLink.Create(toolRequest.Id, notice!.SessionId, ApprovalDecision.AlwaysAllow, stubProject);
        Check(ApprovalLink.TryParse(alwaysLink, out var always) && always.Decision == ApprovalDecision.AlwaysAllow,
            "the always-allow button link parses");
        PiExtensionUiResponse? answered = null;
        stub.Responded = (_, response) => answered = response;
        Check(await dialogs.RespondByIdAsync(always.RequestId, always.Decision) && answered?.Confirmed == true,
            "always-allow answers the original request with an approval");
        Check(ApprovalPolicy.IsAlwaysAllowed(toolRequest, stubProject),
            "always-allow leaves a reusable rule for this project and tool");

        // 已应答/不存在的请求：不应答、不报成功。
        answered = null;
        Check(!await dialogs.RespondByIdAsync(always.RequestId, ApprovalDecision.Allow) && answered is null,
            "a stale or unknown request id is never answered");

        // 命中规则的第二个同类请求由宿主直接放行，不再打扰用户。
        var repeat = ToolRequest("run_command");
        stub.RaiseRequest(repeat);
        Check(answered is not null && answered.Confirmed == true && dialogs.AutoAnsweredCount >= 1,
            "an always-allowed tool is answered without prompting again");
        dialogs.Detach();

        // 拒绝按钮：同一请求得到明确否决。
        var denyStub = new StubExtensionUiSession();
        var denyHost = new ExtensionUiDialogHost(owner: null) { ProjectPath = stubProject };
        denyHost.Attach(denyStub);
        var denyRequest = ToolRequest("write_file");
        denyStub.RaiseRequest(denyRequest);
        PiExtensionUiResponse? denied = null;
        denyStub.Responded = (_, response) => denied = response;
        Check(await denyHost.RespondByIdAsync(denyRequest.Id, ApprovalDecision.Deny) && denied?.Confirmed == false,
            "deny answers the original request with a refusal");
        denyHost.Detach();
        center.Dispose();

        // 5) 应用级提示走 NoticeHub：不进时间线，只在边角提示与活动记录里出现。
        NoticeHub.Shared.Clear();
        var cornerShown = new List<Notice>();
        void OnCorner(Notice notice) => cornerShown.Add(notice);
        NoticeHub.Shared.CornerRequested += OnCorner;
        try
        {
            var timeline = new TimelineViewModel(new Mock.MockChatSession(), SynchronousRefreshScheduler.Instance,
                new RecordingClipboardService());
            timeline.AddNotification("粘贴内容较长（2,604 字符），已转为附件，发送时一并引用。");
            timeline.AddNotification("粘贴内容较长（2,604 字符），已转为附件，发送时一并引用。");
            Check(!timeline.Entries.OfType<ErrorEntryViewModel>().Any(),
                "application feedback no longer becomes a timeline card");
            Check(NoticeHub.Shared.History.Count == 1, "identical notices arriving together collapse into one record");
            Check(cornerShown.Count == 1, "a corner notice is raised once for the collapsed pair");

            NoticeHub.Shared.Publish("设置已保存", NoticeTier.Quiet);
            Check(NoticeHub.Shared.History.Count == 2 && cornerShown.Count == 1,
                "quiet notices are recorded without interrupting");

            // 子代理只在结束时提示一次；逐次工具调用/中间回复不产生任何通知。
            var scripted = new ScriptedChatSession();
            var subTimeline = new TimelineViewModel(scripted, SynchronousRefreshScheduler.Instance,
                new RecordingClipboardService());
            var before = NoticeHub.Shared.History.Count;
            scripted.Raise(new SessionEvent.SubagentUpdated("job-1", "explore", "tool_start", "grep_search"));
            scripted.Raise(new SessionEvent.SubagentUpdated("job-1", "explore", "tool_end", "grep_search"));
            scripted.Raise(new SessionEvent.SubagentUpdated("job-1", "explore", "assistant", "正在整理结果"));
            Check(NoticeHub.Shared.History.Count == before, "subagent tool-call chatter never raises a notice");
            scripted.Raise(new SessionEvent.SubagentUpdated("job-1", "explore", "completed", null));
            var completion = NoticeHub.Shared.History[0];
            Check(completion.Text.Contains("已完成", StringComparison.Ordinal) && completion.Kind == NoticeKind.Progress,
                "only the subagent completion is announced, as progress");
            Check(!subTimeline.Entries.OfType<ErrorEntryViewModel>().Any(),
                "the completion notice stays out of the conversation timeline");
            timeline.Dispose();
            subTimeline.Dispose();
        }
        finally { NoticeHub.Shared.CornerRequested -= OnCorner; }
        NoticeHub.Shared.Clear();

        // 6) 通知中心条目：可以逐条移除，概览计数随之更新。
        var dismissCenter = new NotificationCenterViewModel(new NotificationPreferencesViewModel(
            new NotificationPreferencesStore(Path.Combine(root, "notification-preferences-2.json"))));
        dismissCenter.Items.Add(new NotificationItemViewModel("dismiss-1", session, Guid.NewGuid(), 1,
            AppNotificationKind.Completed, "完成", "正文", null, true));
        Check(dismissCenter.TotalCount == 1, "notification center counts the new record");
        dismissCenter.DismissItemCommand.Execute(dismissCenter.Items[0]);
        Check(dismissCenter.TotalCount == 0 && !dismissCenter.HasItems, "a notification record can be removed from the list");
        dismissCenter.Dispose();
        await Task.CompletedTask;
    }

    private static ExtensionUiRequest ToolRequest(string tool) => new(Guid.NewGuid(), Guid.NewGuid().ToString("N"), "confirm",
        JsonSerializer.SerializeToElement(new { title = "工具执行确认", message = "允许执行？", toolName = tool, arguments = "{}" }),
        DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));

    private static ExtensionUiRequest ApprovalRequest(string tool) => new(Guid.NewGuid(), "req-" + tool, "confirm",
        JsonSerializer.SerializeToElement(new { title = "工具执行确认", message = "允许执行？", toolName = tool, arguments = "{}" }),
        DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));
}
