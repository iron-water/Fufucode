using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fufucode.App.Infrastructure;
using Fufucode.App.Integration;
using Fufucode.App.ViewModels;
using Fufucode.Core;

namespace Fufucode.App.SelfTest;

internal static class NativeWorkModesSelfTest
{
    internal static async Task<int> RunAsync(MainWindow window, string evidence)
    {
        Directory.CreateDirectory(evidence);
        var checks = 0;
        void Check(bool value, string label)
        {
            Console.WriteLine((value ? "PASS " : "FAIL ") + label);
            if (!value) throw new InvalidOperationException(label);
            checks++;
        }
        await Until(() => window.GraphServiceForTesting is not null);
        var service = window.GraphServiceForTesting!;
        var original = service.ReadSnapshot();
        var layout = new AppDataLayout(Program.SelfTestDataRoot);
        var metadata = new DesktopMetadataStore(layout);
        var manager = new PersistentSessionManager(string.Empty, string.Empty, layout);
        manager.SetProjectTrustDecision(original.ProjectPath, ProjectTrustDecision.DoNotTrust);
        var scratch = ScratchWorkspace.Ensure(layout);
        manager.SetProjectTrustDecision(scratch, ProjectTrustDecision.DoNotTrust);
        Check(original.Modules.Count == 0 && original.Runs.Count == 0, "opening Graph does not scan or start a model");
        Check(metadata.ListSessions(original.ProjectId).Count == 0, "Graph project opens without a Chat");
        var module = service.UpsertModule(null, null, "未来架构模块", "TEST-ONLY planned module", []);
        window.GraphModelForTesting!.Refresh();
        window.GraphModelForTesting.SelectModule(module.Id);
        var graphOnly = new[] { "graph-properties", "graph-tickets", "graph-history" };
        Check(graphOnly.All(window.PageAvailableForTesting) && !window.PageAvailableForTesting("graph-structure"),
            "Graph exposes its dedicated panels and retires the structure page");
        Check(ProjectMenu(window, original.ProjectId).Contains("打开图") && !ProjectMenu(window, original.ProjectId).Contains("新建会话"),
            "Graph project menu opens its unique graph, never creates a Chat");
        Check(!BackendSelectorVisible(window), "Graph project manager hides the Chat backend selector");
        window.OpenDockPage("graph-properties", DockZone.Right);
        window.DockLayoutForTesting.PinnedPages.Add("graph-properties");
        var floating = window.FloatDockPage("graph-properties");
        await Settle();
        Check(floating is not null && window.FloatingDockWindowForTesting(floating) is not null, "Graph property panel can float in Graph mode");
        await window.ActionsForTesting.ExecuteAsync("session.new");
        Check(window.GraphEntryPendingForTesting, "Graph-only startup can open an unbound graph before any Chat exists");
        await window.NavigateForTestingAsync(AppPage.Chat);
        await Settle();
        Check(!window.GraphEntryPendingForTesting && window.PageAvailableForTesting("files") && window.PageAvailableForTesting("graph-minimap"),
            "first Chat bootstrap clears the unbound Graph state and restores Chat capabilities");
        var vm = (MainViewModel)window.DataContext!;
        Check(vm.IsChatPage && window.CurrentSessionId is not null, "switch to Chat opens an actual persistent session without sending");
        var sessionId = window.CurrentSessionId;
        vm.Input.DraftText = "TEST-ONLY 保留原聊天草稿";
        Check(graphOnly.All(id => !window.PageAvailableForTesting(id)), "Chat has no Graph-only page capability");
        Check(window.CustomizationForTesting.Pages.All(page => !graphOnly.Contains(page.Id)), "Chat panel candidates exclude every Graph-only page");
        Check(floating is not null && window.FloatingDockWindowForTesting(floating) is null, "mode switch removes the old floating Graph host");
        foreach (var id in graphOnly)
        {
            window.OpenDockPage(id, DockZone.Right);
            window.MoveDockPage(id, DockZone.Left);
            Check(window.FloatDockPage(id) is null && !window.DockLayoutForTesting.AllPageIds().Contains(id), "direct open/move/float cannot bypass Chat isolation: " + id);
        }
        Check(!(await window.ActionsForTesting.ExecuteAsync("graph.properties")).Executed &&
              !(await window.ActionsForTesting.ExecuteAsync("graph.resume-lead")).Executed,
            "actions and shortcut dispatch reject Graph-only operations in Chat");
        using (var palette = new CommandPaletteViewModel(window.ActionsForTesting))
            Check(palette.Items.All(item => item.Id is not ("graph.properties" or "graph.tickets" or "graph.structure" or "graph.history" or "graph.resume-lead")),
                "Chat command palette omits Graph-only actions rather than listing disabled entries");
        window.DockLayoutForTesting.Pages[DockZone.Right].Add("graph-properties");
        window.DockLayoutForTesting.PinnedPages.Add("graph-properties");
        window.DockLayoutForTesting.Floating.Add(new() { Id = "stale-graph", Pages = ["graph-history"], Selected = "graph-history" });
        window.RefreshModeLayoutForTesting();
        Check(!window.DockLayoutForTesting.AllPageIds().Any(graphOnly.Contains) && !window.DockLayoutForTesting.PinnedPages.Contains("graph-properties"),
            "restored stale, pinned and floating layout entries cannot reintroduce Graph panels");
        window.OpenDockPage("graph-minimap", DockZone.Right);
        Check(window.PageAvailableForTesting("graph-minimap") && window.DockLayoutForTesting.Find("graph-minimap") is not null,
            "Chat retains the allowed mini Graph bridge");
        Check(ProjectMenu(window, original.ProjectId).Contains("新建会话") && !ProjectMenu(window, original.ProjectId).Contains("新建图"),
            "Chat project menu retains new-session semantics");
        Check(BackendSelectorVisible(window), "Chat project manager retains its backend selector");
        Screenshot(window, Path.Combine(evidence, "chat-mode.png"));
        await window.NavigateForTestingAsync(AppPage.Settings);
        Check(window.CustomizationForTesting.Pages.All(page => !graphOnly.Contains(page.Id)), "shared settings inherit the originating Chat mode");
        await window.NavigateForTestingAsync(AppPage.Graph);
        await Settle();
        Check(ReferenceEquals(service, window.GraphServiceForTesting) && window.GraphModelForTesting.SelectedModuleId == module.Id,
            "mode return preserves the unique graph and selection");
        Check(window.DockLayoutForTesting.FindFloating("graph-properties")?.Id == floating, "Graph restores its legal floating and pinned layout");
        Check(window.PageAvailableForTesting("sidechat"), "Graph retains the shared side Chat bridge");
        Check(window.ActionsForTesting.GetDisabledReason("session.rename") is not null, "Graph hides unrelated main Chat actions");
        var projectCount = metadata.ListProjects().Count;
        var revision = service.ReadSnapshot().Revision;
        await window.ActionsForTesting.ExecuteAsync("session.new");
        await Settle();
        Check(window.GraphEntryPendingForTesting && ((MainViewModel)window.DataContext!).IsGraphPage, "Graph plus opens an unbound blank graph");
        Check(metadata.ListProjects().Count == projectCount && service.ReadSnapshot().Revision == revision,
            "blank entry creates no project or graph data");
        Check(window.CustomizationForTesting.Pages.All(page => page.Id is "overview" or "notifications" or "perf"),
            "blank graph exposes no previous project's panels or panel candidates");
        window.OpenDockPage("files", DockZone.Left);
        window.OpenDockPage("changes", DockZone.Right);
        Check(window.DockLayoutForTesting.Find("files") is null && window.DockLayoutForTesting.Find("changes") is null &&
              !(await window.ActionsForTesting.ExecuteAsync("session.side")).Executed,
            "blank graph cannot reopen previous project files, changes or side Chat");
        Check(!BackendSelectorVisible(window), "blank graph hides the Chat backend selector");
        Screenshot(window, Path.Combine(evidence, "blank-graph.png"));
        await window.CancelGraphEntryForTestingAsync();
        Check(!window.GraphEntryPendingForTesting && ReferenceEquals(service, window.GraphServiceForTesting), "cancel blank graph restores the existing graph");
        Check(window.DockLayoutForTesting.FindFloating("graph-properties")?.Id == floating,
            "cancel blank graph restores the previous Graph layout");
        var otherPath = Path.Combine(Path.GetDirectoryName(original.ProjectPath)!, "other-project");
        Directory.CreateDirectory(otherPath);
        var other = metadata.GetOrCreateProject(otherPath);
        manager.SetProjectTrustDecision(otherPath, ProjectTrustDecision.DoNotTrust);
        window.RenderOverview();
        Check(ProjectMenu(window, other.Id).Contains("新建图"), "project without a graph offers create graph");
        await window.ActionsForTesting.ExecuteAsync("session.new");
        await window.SwitchProjectForTestingAsync(otherPath);
        await Settle();
        var otherService = window.GraphServiceForTesting!;
        Check(!window.GraphEntryPendingForTesting && otherService.ReadSnapshot().ProjectId == other.Id, "project selection binds the blank graph to the shared project identity");
        Check(metadata.ListSessions(other.Id).Count == 0 && otherService.ReadSnapshot().Modules.Count == 0 && otherService.ReadSnapshot().Runs.Count == 0,
            "binding a project creates no Chat, scan or model work");
        await window.SwitchProjectForTestingAsync(otherPath);
        Check(ReferenceEquals(otherService, window.GraphServiceForTesting) && ProjectMenu(window, other.Id).Contains("打开图"),
            "reopening the same project restores one graph");
        await window.SwitchProjectForTestingAsync(original.ProjectPath);
        Check(ReferenceEquals(service, window.GraphServiceForTesting) && service.ReadSnapshot().Modules.Any(item => item.Id == module.Id),
            "shared project switching preserves prior graph data");
        await window.NavigateForTestingAsync(AppPage.Chat);
        vm = (MainViewModel)window.DataContext!;
        Check(window.CurrentSessionId == sessionId && vm.Input.DraftText == "TEST-ONLY 保留原聊天草稿", "project and mode switching restore the same Chat and draft");
        await window.ActionsForTesting.ExecuteAsync("session.new");
        var firstNew = window.CurrentSessionId;
        Check(((MainViewModel)window.DataContext!).IsChatPage && firstNew != sessionId, "Chat plus still creates a new blank Chat");
        await window.ActionsForTesting.ExecuteAsync("session.new");
        Check(window.CurrentSessionId != firstNew, "one project can contain multiple Chats");
        await window.NavigateForTestingAsync(AppPage.Graph);
        Check(window.GraphEntryPendingForTesting, "unassigned Chat switches to an unbound Graph instead of graphing the internal scratch directory");
        await window.CancelGraphEntryForTestingAsync();
        await window.SwitchProjectForTestingAsync(original.ProjectPath);
        await window.NavigateForTestingAsync(AppPage.Graph);
        window.WindowState = WindowState.Normal;
        window.Width = 900;
        await Settle();
        Check(window.Bounds.Width < 1100, "native viewport is actually narrow");
        Check(window.GetVisualDescendants().OfType<Button>().Any(button => AutomationProperties.GetAutomationId(button) == "action-workspace.mode" && button.IsEffectivelyVisible),
            "narrow window keeps the crossing-arrows mode switch accessible");
        Screenshot(window, Path.Combine(evidence, "graph-narrow.png"));
        await window.NavigateForTestingAsync(AppPage.Settings);
        vm = (MainViewModel)window.DataContext!;
        vm.Settings.SelectCategory("automation");
        await Settle();
        var settings = (Control)vm.Settings.GraphSettingsContent!;
        var enabled = settings.GetVisualDescendants().OfType<ToggleSwitch>().Single(control => AutomationProperties.GetAutomationId(control) == "graph-setting-enabled");
        var apply = settings.GetVisualDescendants().OfType<Button>().Single(control => AutomationProperties.GetAutomationId(control) == "graph-settings-apply");
        enabled.IsChecked = false;
        apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => !window.GraphServiceForTesting!.ReadOptions().Enabled);
        await Settle();
        Check(!window.PageAvailableForTesting("graph-minimap") && graphOnly.All(id => !window.PageAvailableForTesting(id)),
            "disabling Graph withdraws all dedicated panels and bridges");
        enabled.IsChecked = true;
        apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => window.GraphServiceForTesting!.ReadOptions().Enabled);
        await window.NavigateForTestingAsync(AppPage.Graph);
        Check(graphOnly.All(window.PageAvailableForTesting), "reenabling Graph restores capabilities in Graph mode");
        var report = $"Native workspace modes: {checks}/{checks} passed; zero model requests";
        File.WriteAllText(Path.Combine(evidence, "workspace-modes-result.txt"), report);
        Console.WriteLine(report);
        return 0;
    }

    private static string[] ProjectMenu(MainWindow window, string projectId)
    {
        window.OpenDockPage("overview", DockZone.Left);
        window.RenderOverview();
        return window.GetVisualDescendants().OfType<Button>()
            .Single(button => AutomationProperties.GetAutomationId(button) == "project.row." + projectId)
            .ContextMenu!.Items.OfType<MenuItem>().Select(item => item.Header?.ToString() ?? "").ToArray();
    }
    private static bool BackendSelectorVisible(MainWindow window) => window.GetVisualDescendants().OfType<Button>()
        .Any(button => button.Name == "NewSessionBar" && button.IsEffectivelyVisible);

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 300 && !condition(); i++) await Task.Delay(50);
        if (!condition()) throw new TimeoutException("Native workspace did not become ready.");
    }
    private static async Task Settle()
    {
        await Task.Delay(220);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
    }
    private static void Screenshot(Window window, string path)
    {
        window.UpdateLayout();
        using var bitmap = new RenderTargetBitmap(new PixelSize(Math.Max(1, (int)window.Bounds.Width), Math.Max(1, (int)window.Bounds.Height)));
        bitmap.Render(window);
        bitmap.Save(path, PngBitmapEncoderOptions.Default);
    }
}
