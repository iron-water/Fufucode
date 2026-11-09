using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fufucode.App.Abstractions.Graph;
using Fufucode.App.Integration;
using Fufucode.App.ViewModels;
using Fufucode.Core;

namespace Fufucode.App.SelfTest;

internal static class NativeGraphSelfTest
{
    internal static async Task<int> RunAsync(MainWindow window, string evidenceDirectory, bool settingsOnly = false)
    {
        Directory.CreateDirectory(evidenceDirectory);
        var failures = new List<string>();
        var count = 0;
        void Check(bool pass, string label)
        {
            count++;
            Console.WriteLine((pass ? "PASS " : "FAIL ") + label);
            if (!pass) failures.Add(label);
        }
        for (var i = 0; i < 200 && window.GraphServiceForTesting is null; i++) await Task.Delay(50);
        var service = window.GraphServiceForTesting ?? throw new InvalidOperationException("Graph 项目未完成启动。");
        var vm = (MainViewModel)window.DataContext!;
        var original = service.ReadSnapshot();
        var metadata = new DesktopMetadataStore(new AppDataLayout(Program.SelfTestDataRoot));
        Check(vm.IsGraphPage, "真实项目直接进入 Graph");
        var builtInStatus = window.CustomizationForTesting.StatusOf("automation");
        Check(builtInStatus.State == "已启用" && builtInStatus.ActionId == "graph" &&
              builtInStatus.Detail.Contains("内建") && builtInStatus.PageId != "marketplace",
            "自动开发运行状态来自内建 Graph 而非插件安装");
        Check(vm.Session is UnopenedChatSession, "项目入口不创建聊天会话");
        Check(metadata.ListSessions(original.ProjectId).Count == 0, "原生会话索引仍为空");
        await service.ScanAsync();
        window.GraphModelForTesting!.Refresh();
        window.GraphModelForTesting.ExpandAll();
        var scanned = service.ReadSnapshot();
        Check(scanned.Modules.Count > 0, "真实目录生成模块");
        Check(scanned.Modules.Any(item => item.SourcePaths.Length > 0), "模块保留真实路径来源");
        Check(scanned.ProjectPath == original.ProjectPath, "扫描保持真实项目身份");
        var module = scanned.Modules.First();
        window.GraphModelForTesting.SelectModule(module.Id);
        await Settle();
        Check(window.PageAvailableForTesting("graph-properties") && window.PageAvailableForTesting("graph-tickets"), "属性和工单接入原有停靠系统");
        Check(window.GraphModelForTesting.SelectedModuleId == module.Id, "选中模块保持稳定身份");
        if (settingsOnly)
        {
            await VerifyBuiltinSettingsAsync(window, service, evidenceDirectory, Check);
            Check(metadata.ListSessions(original.ProjectId).Count == 0, "设置检查不创建聊天会话");
            var settingsReport = $"Native AutoDev settings: {count - failures.Count}/{count} passed\n" + string.Join("\n", failures);
            await File.WriteAllTextAsync(Path.Combine(evidenceDirectory, "native-result.txt"), settingsReport);
            Console.WriteLine(settingsReport);
            return failures.Count == 0 ? 0 : 1;
        }
        var modeButton = window.GetVisualDescendants().OfType<Button>().FirstOrDefault(item => AutomationProperties.GetAutomationId(item) == "action-workspace.mode");
        Check(modeButton is { IsVisible: true }, "顶栏原通知位置显示模式切换按钮");
        Screenshot(window, Path.Combine(evidenceDirectory, "sgline-graph.png"));
        Button? ModeButton() => window.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(item => AutomationProperties.GetAutomationId(item) == "action-workspace.mode");
        var switchMode = (CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)modeButton!.Command!;
        var startupTrust = new PersistentSessionManager(string.Empty, string.Empty, new AppDataLayout(Program.SelfTestDataRoot));
        startupTrust.SetProjectTrustDecision(scanned.ProjectPath, ProjectTrustDecision.DoNotTrust);
        await switchMode.ExecuteAsync(null);
        await Settle();
        Check(((MainViewModel)window.DataContext!).IsChatPage && ModeButton() is { IsEffectivelyVisible: true, IsEnabled: true }, "从 Graph 顶栏切到 Chat 后按钮仍可见可用");
        Application.Current!.TryGetResource("Icon.ArrowShuffle", window.ActualThemeVariant, out var switchIcon);
        Check(switchIcon is Geometry && ModeButton()?.Content is PathIcon renderedIcon && ReferenceEquals(renderedIcon.Data, switchIcon), "模式切换按钮显示交叉双箭头");
        Screenshot(window, Path.Combine(evidenceDirectory, "chat-mode-switch.png"));
        await switchMode.ExecuteAsync(null);
        await Settle();
        Check(((MainViewModel)window.DataContext!).IsGraphPage && ModeButton() is { IsEffectivelyVisible: true, IsEnabled: true }, "同一顶栏按钮可从 Chat 切回 Graph");
        var originalWidth = window.Width;
        var originalState = window.WindowState;
        window.WindowState = WindowState.Normal;
        window.Width = 900;
        await Settle();
        Check(ModeButton() is { IsEffectivelyVisible: true, IsEnabled: true }, "窄窗标题栏优先保留模式切换按钮");
        await switchMode.ExecuteAsync(null);
        await Settle();
        Check(((MainViewModel)window.DataContext!).IsChatPage && ModeButton() is { IsEffectivelyVisible: true, IsEnabled: true }, "窄窗切回 Chat 后模式入口不进入更多菜单");
        Screenshot(window, Path.Combine(evidenceDirectory, "chat-mode-switch-narrow.png"));
        await switchMode.ExecuteAsync(null);
        window.Width = originalWidth;
        window.WindowState = originalState;
        await Settle();
        var title = "验收：追溯 " + module.Name + " 的项目来源";
        var ticket = service.CreateTicket(title, "核对模块与实际目录的关联；不修改项目源码。", [module.Id], ["来源可以定位到实际项目目录"]);
        service.AddEntry(ticket.Id, module.Id, "observation", "原生验收已读取目录并保留模块来源。", scanned.ProjectPath);
        window.GraphModelForTesting.SelectTicket(ticket.Id);
        window.OpenGraphPanelForTesting("graph-tickets");
        await Settle();
        Check(!window.GetVisualDescendants().OfType<Control>().Any(item => AutomationProperties.GetAutomationId(item) is
            "GraphRecoveryChecked" or "GraphRecoveryResolution" or "GraphLinkSessionId" or "GraphLinkSession"),
            "原生工单不暴露机械恢复确认或SessionId关联控件");
        Check(service.ReadSnapshot().Tickets.Any(item => item.Id == ticket.Id && item.ModuleIds.Contains(module.Id)), "真实工单和模块关联持久化");
        Check(service.ReadSnapshot().Entries.Any(item => item.TicketId == ticket.Id), "工单往来保存原始来源");
        var organizationService = (Fufucode.Plugins.AutoDev.Graph.GraphProjectService)service;
        organizationService.SendOrganizationMessage("native-human-inbox", new GraphMessageDraft("human", true,
            "question", "原生收件箱测试：请核对真实模块来源", ticket.Id, module.Id, service.ReadSnapshot().Revision));
        organizationService.SendOrganizationMessage("native-internal-inbox", new GraphMessageDraft("project-lead", false,
            "note", "TEST-ONLY 内部协作不进入人类收件箱", ticket.Id, module.Id, service.ReadSnapshot().Revision));
        window.GraphModelForTesting.Refresh();
        await Settle();
        Check(window.GraphModelForTesting.ShowHumanInbox && window.GraphModelForTesting.HumanInbox.Count == 1,
            "原生默认收件箱只显示发给人的事项");
        Check(window.GraphModelForTesting.Snapshot.Organization?.Responsibilities.Single().Role == GraphExecutionRole.ProjectLead,
            "项目组织具有稳定负责人且无独立工单Agent");
        var incoming = window.GraphModelForTesting.HumanInbox.Single();
        window.GraphModelForTesting.SelectHumanMessage(incoming.Id);
        Check(organizationService.ReadOrganizationSnapshot().Messages.Single(m => m.Id == incoming.Id).Status == GraphMessageStatus.Received,
            "原生阅读收件消息不虚报已读应用或验收");
        window.GraphModelForTesting.ShowInternalWork = true;
        await Settle();
        Screenshot(window, Path.Combine(evidenceDirectory, "sgline-ticket.png"));
        var trust = new PersistentSessionManager(string.Empty, string.Empty, new AppDataLayout(Program.SelfTestDataRoot));
        trust.SetProjectTrustDecision(scanned.ProjectPath, ProjectTrustDecision.DoNotTrust);
        string ordinaryId;
        await using (var ordinary = await trust.CreateAsync(scanned.ProjectPath, "已有普通 Chat", backend: BackendId.NativeCSharp))
        {
            ordinaryId = ordinary.Session.Id;
            metadata.SetDraft(ordinaryId, "普通 Chat 尚未发送的草稿");
        }
        await window.OpenGraphChatForTestingAsync(module.Id, ticket.Id, service.BuildContext(module.Id, ticket.Id));
        await Settle();
        vm = (MainViewModel)window.DataContext!;
        Check(vm.IsChatPage && vm.Session is not UnopenedChatSession, "关联模块打开真实原生 Chat");
        Check(string.IsNullOrWhiteSpace(vm.Input.DraftText) && vm.Input.Attachments.Any(file => file.Context.Contains(module.Name, StringComparison.Ordinal) && File.Exists(file.Path)), "项目上下文使用原生附件卡片，保留空白输入区");
        var linkedSessionId = service.ReadSnapshot().Tickets.Single(item => item.Id == ticket.Id).SessionId;
        Check(linkedSessionId is { Length: > 0 }, "会话身份回写工单");
        Check(linkedSessionId != ordinaryId && metadata.GetSession(ordinaryId).Name == "已有普通 Chat", "模块讨论不挪用或改名已有普通 Chat");
        Check(metadata.GetDraft(ordinaryId) == "普通 Chat 尚未发送的草稿", "已有普通 Chat 草稿完整保留");
        Check(window.DockLayoutForTesting.Find("graph-minimap") is not null, "Chat 自动打开极简 Graph");
        Check(window.DockLayoutForTesting.Find("graph-history") is null && window.DockLayoutForTesting.Find("graph-structure") is null, "Chat 与 Graph 各自恢复停靠布局");
        Screenshot(window, Path.Combine(evidenceDirectory, "sgline-chat.png"));
        vm.Input.DraftText = "关联讨论尚未发送的修改";
        await window.NavigateForTestingAsync(AppPage.Graph);
        await Settle();
        Check(window.GraphModelForTesting.SelectedModuleId == module.Id, "切回 Graph 保留对象选择");
        Check(window.DockLayoutForTesting.Find("graph-history") is not null, "Graph 恢复自己的历史面板");
        await window.OpenGraphChatForTestingAsync(module.Id, ticket.Id, "不应覆盖旧草稿");
        vm = (MainViewModel)window.DataContext!;
        Check(service.ReadSnapshot().Tickets.Single(item => item.Id == ticket.Id).SessionId == linkedSessionId, "重新打开讨论沿用同一会话");
        Check(vm.Input.DraftText == "关联讨论尚未发送的修改", "恢复关联会话不覆盖用户草稿");
        await window.NavigateForTestingAsync(AppPage.Graph);
        await window.OpenGraphSideChatForTestingAsync();
        var sideId = window.GraphSideChatIdForTesting;
        Check(((MainViewModel)window.DataContext!).IsGraphPage && sideId is not null, "Graph 复用原侧边聊天并保留主图");
        window.GraphSideChatModelForTesting!.Input.DraftText = "侧边讨论保留的同一段草稿";
        await window.PromoteGraphSideChatForTestingAsync();
        vm = (MainViewModel)window.DataContext!;
        Check(vm.IsChatPage && window.GraphChatIdForTesting == sideId && window.GraphSideChatIdForTesting is null, "侧边讨论转到主 Chat 保持同一真实会话身份");
        Check(vm.Input.DraftText == "侧边讨论保留的同一段草稿", "侧边讨论转主 Chat 完整保留草稿");
        var preserved = AttachmentPresentation.Parse(metadata.GetDraft(linkedSessionId!) ?? "");
        Check(preserved.Body == "关联讨论尚未发送的修改" && preserved.Files.Any(file => file.Context.Contains(module.Name, StringComparison.Ordinal)), "转移讨论仍保存原主 Chat 草稿和上下文附件");
        await window.NavigateForTestingAsync(AppPage.Graph);
        await VerifyBuiltinSettingsAsync(window, service, evidenceDirectory, Check);
        Check(service.ReadSnapshot().Tickets.Any(item => item.Id == ticket.Id), "禁用不删除工单或历史");
        await window.NavigateForTestingAsync(AppPage.Graph);
        window.Width = 1100;
        window.Height = 760;
        await Settle();
        Screenshot(window, Path.Combine(evidenceDirectory, "sgline-graph-narrow.png"));
        var report = $"Native Graph: {count - failures.Count}/{count} passed\n" + string.Join("\n", failures);
        await File.WriteAllTextAsync(Path.Combine(evidenceDirectory, "native-result.txt"), report);
        Console.WriteLine(report);
        return failures.Count == 0 ? 0 : 1;
    }

    private static async Task VerifyBuiltinSettingsAsync(MainWindow window, IGraphProjectService service,
        string evidenceDirectory, Action<bool, string> check)
    {
        await window.NavigateForTestingAsync(AppPage.Settings);
        var vm = (MainViewModel)window.DataContext!;
        vm.Settings.SelectCategory("marketplace");
        await Settle();
        check(vm.Settings.MarketplacePlugins.All(item => item.Id != "automation.auto-dev"),
            "真实设置页插件列表不包含内建 AutoDev");
        Screenshot(window, Path.Combine(evidenceDirectory, "marketplace-without-autodev.png"));
        vm.Settings.SelectCategory("automation");
        await Settle();
        check(vm.Settings.ShowGraphSettings && vm.Settings.GraphSettingsContent is Control, "共用原设置页中的 Graph 设置");
        var settings = (Control)vm.Settings.GraphSettingsContent!;
        var titleBlocks = settings.GetVisualDescendants().OfType<TextBlock>().Where(item => item.Classes.Contains("settingsRowTitle")).ToArray();
        Application.Current!.TryGetResource("MsRed", window.ActualThemeVariant, out var sharedRed);
        check(sharedRed is ISolidColorBrush red && titleBlocks.Length > 0 &&
              titleBlocks.All(item => (item.Foreground as ISolidColorBrush)?.Color == red.Color),
            "AutoDev 设置项文字使用共享微软红色");
        Screenshot(window, Path.Combine(evidenceDirectory, "sgline-settings.png"));
        var enabled = settings.GetVisualDescendants().OfType<ToggleSwitch>().Single(item => AutomationProperties.GetAutomationId(item) == "graph-setting-enabled");
        var apply = settings.GetVisualDescendants().OfType<Button>().Single(item => AutomationProperties.GetAutomationId(item) == "graph-settings-apply");
        enabled.IsChecked = false;
        apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        for (var i = 0; i < 100 && service.ReadOptions().Enabled; i++) await Task.Delay(50);
        await Settle();
        check(!service.ReadOptions().Enabled && !window.PageAvailableForTesting("graph-tickets"), "禁用 Graph 撤下专属面板");
        var disabledStatus = window.CustomizationForTesting.StatusOf("automation");
        check(disabledStatus.State == "已停用" && disabledStatus.PageId == "automation" &&
              disabledStatus.ActionId is null && !disabledStatus.Detail.Contains("插件"),
            "停用 Graph 的状态回到内建设置，不引导安装插件");
        enabled.IsChecked = true;
        apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        for (var i = 0; i < 100 && !service.ReadOptions().Enabled; i++) await Task.Delay(50);
        await Settle();
        check(service.ReadOptions().Enabled && !window.PageAvailableForTesting("graph-tickets"),
            "重新启用恢复服务，Chat 来源的设置仍隔离 Graph 专属面板");
        check(window.CustomizationForTesting.StatusOf("automation").ActionId == "graph",
            "重新启用后的状态指向内建 Graph");
        await window.NavigateForTestingAsync(AppPage.Graph);
        await Settle();
        check(window.PageAvailableForTesting("graph-tickets"), "进入 Graph 后恢复专属面板和已有数据");
    }

    private static async Task Settle()
    {
        await Task.Delay(180);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
    }
    private static void Screenshot(MainWindow window, string path)
    {
        using var image = new RenderTargetBitmap(new PixelSize(Math.Max(1, (int)window.Bounds.Width), Math.Max(1, (int)window.Bounds.Height)));
        image.Render(window);
        image.Save(path, PngBitmapEncoderOptions.Default);
    }
}
