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
    internal static async Task<int> RunAsync(MainWindow window, string evidenceDirectory)
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
        var modeButton = window.GetVisualDescendants().OfType<Button>().FirstOrDefault(item => AutomationProperties.GetAutomationId(item) == "action-workspace.mode");
        Check(modeButton is { IsVisible: true }, "顶栏原通知位置显示模式切换按钮");
        Screenshot(window, Path.Combine(evidenceDirectory, "sgline-graph.png"));
        var title = "验收：追溯 " + module.Name + " 的项目来源";
        var ticket = service.CreateTicket(title, "核对模块与实际目录的关联；不修改项目源码。", [module.Id], ["来源可以定位到实际项目目录"]);
        service.AddEntry(ticket.Id, module.Id, "observation", "原生验收已读取目录并保留模块来源。", scanned.ProjectPath);
        window.GraphModelForTesting.SelectTicket(ticket.Id);
        window.OpenGraphPanelForTesting("graph-tickets");
        await Settle();
        Check(service.ReadSnapshot().Tickets.Any(item => item.Id == ticket.Id && item.ModuleIds.Contains(module.Id)), "真实工单和模块关联持久化");
        Check(service.ReadSnapshot().Entries.Any(item => item.TicketId == ticket.Id), "工单往来保存原始来源");
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
        await window.NavigateForTestingAsync(AppPage.Settings);
        vm.Settings.SelectCategory("automation");
        await Settle();
        Check(vm.Settings.ShowGraphSettings && vm.Settings.GraphSettingsContent is Control, "共用原设置页中的 Graph 设置");
        var settings = (Control)vm.Settings.GraphSettingsContent!;
        var titleBlocks = settings.GetVisualDescendants().OfType<TextBlock>().Where(item => item.Classes.Contains("settingsRowTitle")).ToArray();
        Check(titleBlocks.Length > 0 && titleBlocks.All(item => (item.Foreground as ISolidColorBrush)?.Color == Color.Parse("#F25022")), "AutoDev 设置项文字使用共享微软红色");
        Screenshot(window, Path.Combine(evidenceDirectory, "sgline-settings.png"));
        var enabled = settings.GetVisualDescendants().OfType<ToggleSwitch>().Single(item => AutomationProperties.GetAutomationId(item) == "graph-setting-enabled");
        var apply = settings.GetVisualDescendants().OfType<Button>().Single(item => AutomationProperties.GetAutomationId(item) == "graph-settings-apply");
        enabled.IsChecked = false;
        apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        for (var i = 0; i < 100 && service.ReadOptions().Enabled; i++) await Task.Delay(50);
        await Settle();
        Check(!service.ReadOptions().Enabled && !window.PageAvailableForTesting("graph-tickets"), "禁用 Graph 撤下专属面板");
        enabled.IsChecked = true;
        apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        for (var i = 0; i < 100 && !service.ReadOptions().Enabled; i++) await Task.Delay(50);
        await Settle();
        Check(service.ReadOptions().Enabled && window.PageAvailableForTesting("graph-tickets"), "重新启用恢复能力和已有数据");
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
