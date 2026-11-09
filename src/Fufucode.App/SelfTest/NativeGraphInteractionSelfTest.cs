using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fufucode.App.Abstractions.Graph;
using Fufucode.App.Features.Graph;
using Fufucode.App.Infrastructure;
using Fufucode.App.Integration;
using Fufucode.App.ViewModels;
using Fufucode.Core;
using Fufucode.Plugins.AutoDev.Graph;

namespace Fufucode.App.SelfTest;

/// <summary>Drives routed pointer/key events and real native dock/floating layouts. No model requests.</summary>
internal static class NativeGraphInteractionSelfTest
{
    internal static async Task<int> RunAsync(MainWindow window, string evidence)
    {
        Directory.CreateDirectory(evidence);
        var count = 0;
        void Check(bool value, string label)
        {
            Console.WriteLine((value ? "PASS " : "FAIL ") + label);
            if (!value) throw new InvalidOperationException(label);
            count++;
        }
        for (var i = 0; i < 200 && window.GraphServiceForTesting == null; i++) await Task.Delay(50);
        var service = (GraphProjectService)window.GraphServiceForTesting!;
        var vm = window.GraphModelForTesting!;
        var project = service.ReadSnapshot();
        var manager = new PersistentSessionManager("", "", new AppDataLayout(Program.SelfTestDataRoot));
        manager.SetProjectTrustDecision(project.ProjectPath, ProjectTrustDecision.DoNotTrust);
        var first = service.UpsertModule(null, null, "架构规划", "尚未实现的项目职责", []);
        service.UpsertModule(null, first.Id, "执行边界", "独立模块", []);
        service.UpsertModule(null, first.Id, "通信边界", "独立模块", []);
        service.UpsertModule(null, null, "远端服务", "不依赖本地文件", []);
        vm.Refresh(); vm.GoTop();
        window.WindowState = WindowState.Normal;
        window.Width = 1500; window.Height = 950;
        await Settle();
        var allowed = new[] { "overview", "notifications", "sidechat", "graph-properties", "graph-tickets", "graph-history" };
        Check(window.CustomizationForTesting.Pages.Select(page => page.Id).Order().SequenceEqual(allowed.Order()), "Graph exposes exactly six dock pages");
        Check(window.CustomizationForTesting.Pages.Single(page => page.Id == "overview").Title == "会话管理" &&
            window.CustomizationForTesting.Pages.Single(page => page.Id == "sidechat").Title == "侧聊" &&
            window.CustomizationForTesting.Pages.Single(page => page.Id == "graph-tickets").Title == "邮件", "shared page names follow the requested vocabulary");
        Check(!(await window.ActionsForTesting.ExecuteAsync("graph.structure")).Executed, "retired structure action cannot execute");
        using (var palette = new CommandPaletteViewModel(window.ActionsForTesting))
            Check(palette.Items.All(item => item.Id is not ("files" or "changes" or "browser" or "library" or "terminal" or "graph.minimap")),
                "Graph command candidates also follow its dock-page whitelist");
        window.DockLayoutForTesting.Pages[DockZone.Left].Add("graph-structure");
        window.DockLayoutForTesting.PinnedPages.Add("graph-structure");
        window.DockLayoutForTesting.Floating.Add(new() { Id = "retired-structure", Pages = ["graph-structure"], Selected = "graph-structure" });
        window.RefreshModeLayoutForTesting();
        Check(!window.DockLayoutForTesting.AllPageIds().Contains("graph-structure") && !window.DockLayoutForTesting.PinnedPages.Contains("graph-structure"), "stale structure tabs pins and floating layouts cannot revive");
        window.DockLayoutForTesting.Close("graph-properties");
        window.RefreshModeLayoutForTesting();
        await Settle();
        var canvas = MainCanvas(window);
        canvas.Fit();
        using var pointer = new Pointer(501, PointerType.Mouse, true);
        Point Center(string id) { var rect = vm.Scene.FindBounds(id)!.Value; return new(rect.Center.X * vm.MainViewport.Scale + vm.MainViewport.X, rect.Center.Y * vm.MainViewport.Scale + vm.MainViewport.Y); }
        var revealed = new List<string>();
        void Revealed(string page) => revealed.Add(page);
        vm.RevealPanelRequested += Revealed;
        Press(canvas, pointer, Center(first.Id));
        Release(canvas, pointer, Center(first.Id));
        await Settle();
        Screenshot(window, Path.Combine(evidence, "graph-initial.png"));
        Check(vm.SelectedModuleId == first.Id && revealed.Count == 0 && window.DockLayoutForTesting.Find("graph-properties") == null, "main single click selects without opening properties or navigating");
        var toolbar = window.GetVisualDescendants().OfType<GraphToolbar>().Single();
        Check(ToolbarFits(toolbar), "wide grouped Graph toolbar stays within its own container");
        Click(Find<Button>(toolbar, "GraphShowProperties")); await Settle();
        Check(window.DockLayoutForTesting.Find("graph-properties") != null, "toolbar properties explicitly opens the selected module");
        window.DockLayoutForTesting.Close("graph-properties"); window.RefreshModeLayoutForTesting(); await Settle();
        Press(canvas, pointer, Center(first.Id), right: true);
        Check(canvas.ContextMenu!.ItemsSource!.Cast<MenuItem>().Any(item => item.Header?.ToString() == "属性"), "main module context menu offers explicit properties");
        var contextProperty = canvas.ContextMenu.ItemsSource!.Cast<MenuItem>().Single(item => item.Header?.ToString() == "属性");
        contextProperty.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); await Settle();
        Check(window.DockLayoutForTesting.Find("graph-properties") != null, "context properties uses the existing dock action");
        window.DockLayoutForTesting.Close("graph-properties"); window.RefreshModeLayoutForTesting(); await Settle();
        canvas.Fit();
        var before = vm.MainViewport.Capture();
        Wheel(canvas, pointer, new Point(canvas.Bounds.Width / 2, canvas.Bounds.Height / 2), new Vector(.5, -.25));
        Check(Near(vm.MainViewport.Scale, before.Scale) && Near(vm.MainViewport.X - before.X, 24) && Near(vm.MainViewport.Y - before.Y, -12), "two-axis wheel delta pans without implicit zoom");
        var anchor = new Point(canvas.Bounds.Width / 2, canvas.Bounds.Height / 2);
        var world = vm.MainViewport.ToWorld(anchor);
        Wheel(canvas, pointer, anchor, new Vector(0, 1), KeyModifiers.Control);
        Check(vm.MainViewport.Scale > before.Scale && Near(vm.MainViewport.ToWorld(anchor).X, world.X) && Near(vm.MainViewport.ToWorld(anchor).Y, world.Y), "Ctrl wheel zoom keeps its pointer anchor");
        var scale = vm.MainViewport.Scale;
        canvas.RaiseEvent(new PointerDeltaEventArgs(InputElement.PointerTouchPadGestureMagnifyEvent, canvas, pointer, window, canvas.TranslatePoint(anchor, window)!.Value,
            0, PointerPointProperties.None, KeyModifiers.None, new Vector(0, .1)));
        Check(vm.MainViewport.Scale > scale, "native touchpad magnify event explicitly zooms");
        foreach (var delta in new[] { new Vector(1e6, 0), new Vector(-1e6, 0), new Vector(0, 1e6), new Vector(0, -1e6), new Vector(1e6, 1e6), new Vector(-1e6, -1e6) })
        {
            vm.MainViewport.Pan(delta.X, delta.Y);
            Check(ModuleVisible(canvas, vm), "extreme pan keeps a useful module area inside the actual canvas " + delta);
        }
        canvas.Fit();
        var background = new Point(8, 8);
        Move(canvas, pointer, background);
        Check(canvas.Cursor?.ToString() == "Hand", "draggable background uses the hand cursor");
        Press(canvas, pointer, background);
        Check(canvas.Cursor?.ToString() == "SizeAll", "active pan uses the platform move cursor");
        Move(canvas, pointer, background + new Vector(30, 20), held: true);
        Release(canvas, pointer, background + new Vector(30, 20));
        Check(pointer.Captured == null && canvas.Cursor?.ToString() != "SizeAll", "pointer release restores the idle cursor and capture");
        Press(canvas, pointer, background); pointer.Capture(null);
        Check(canvas.Cursor?.ToString() != "SizeAll", "capture loss restores the cursor");
        Press(canvas, pointer, background);
        Find<Button>(toolbar, "GraphShowProperties").Focus(); await Settle();
        Check(pointer.Captured == null && canvas.Cursor?.ToString() != "SizeAll", "focus loss ends active pan and restores the cursor");
        canvas.Fit();
        Press(canvas, pointer, Center(first.Id), clicks: 2);
        Check(vm.Depth > 0 && ModuleVisible(canvas, vm), "double click still drills into module hierarchy");
        canvas.Focus(); canvas.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Home });
        Check(vm.Depth == 0 && ModuleVisible(canvas, vm), "keyboard top-level navigation preserves visible content");
        vm.MoveHistory(-1); await Settle();
        Check(vm.IsHistorical && ModuleVisible(canvas, vm) && !Find<Button>(toolbar, "GraphToolbarNewModule").IsEnabled,
            "historical navigation remains visible and disables edits");
        vm.ShowRevision(null); await Settle();
        window.Width = 900; window.Height = 800; await Settle();
        Check(window.Bounds.Width < 1000 && ToolbarFits(toolbar) && toolbar.Bounds.Height < 160 && ModuleVisible(canvas, vm), "900px native layout keeps a compact toolbar and recoverable graph");
        Screenshot(window, Path.Combine(evidence, "graph-toolbar-900.png"));
        window.Width = 1500; window.Height = 950; await Settle(); canvas.Fit();
        Screenshot(window, Path.Combine(evidence, "graph-toolbar-wide.png"));
        vm.RevealPanelRequested -= Revealed;
        await window.NavigateForTestingAsync(AppPage.Chat); await Settle();
        window.OpenDockPage("graph-minimap", DockZone.Right); await Settle();
        var mini = window.GetVisualDescendants().OfType<GraphCanvas>().Single(control => AutomationProperties.GetAutomationId(control) == "GraphMiniMapCanvas");
        mini.Fit(); revealed.Clear(); vm.RevealPanelRequested += Revealed;
        var node = vm.Scene.Nodes.Single(node => node.Module.Id == first.Id);
        var miniPoint = new Point(node.Bounds.Center.X * vm.MiniMapViewport.Scale + vm.MiniMapViewport.X,
            node.Bounds.Center.Y * vm.MiniMapViewport.Scale + vm.MiniMapViewport.Y);
        Press(mini, pointer, miniPoint); Release(mini, pointer, miniPoint); await Settle();
        Check(((MainViewModel)window.DataContext!).IsChatPage && revealed.Count == 0 && vm.SelectedModuleId == node.Module.Id &&
            window.DockLayoutForTesting.Find("graph-properties") == null, "mini single click preserves Chat and sidebars while sharing module selection");
        Press(mini, pointer, miniPoint, clicks: 2); await Settle();
        Check(((MainViewModel)window.DataContext!).IsChatPage && vm.Depth > 0 && revealed.Count == 0,
            "mini double click retains drill-down without navigating away from Chat");
        vm.GoTop(); await Settle();
        var mainViewport = vm.MainViewport.Capture();
        vm.MiniMapViewport.Pan(-1e6, 1e6);
        Check(ModuleVisible(mini, vm, true) && vm.MainViewport.Capture() == mainViewport, "mini viewport is independently constrained and never moves the main viewport");
        Click(Find<Button>(window, "GraphMiniOpenMain")); await Settle();
        Check(((MainViewModel)window.DataContext!).IsGraphPage, "only explicit open-in-main navigates from mini to Graph");
        vm.RevealPanelRequested -= Revealed;

        for (var i = 0; i < 24; i++)
            service.SendOrganizationMessage("ui-mail-" + i, new GraphMessageDraft("human", true, "question",
                $"方案核对 {i + 1}\n请核对架构边界与实施范围。\n" + string.Join("\n", Enumerable.Repeat("保留真实来源与决定。", 8)),
                null, first.Id, service.ReadSnapshot().Revision));
        service.SendOrganizationMessage("ui-internal", new GraphMessageDraft("project-lead", false, "note", "TEST-ONLY internal correspondence", null, null, service.ReadSnapshot().Revision));
        vm.Refresh(); window.OpenDockPage("graph-tickets", DockZone.Right); await Settle();
        for (var i = 0; i < 40 && !window.GetVisualDescendants().OfType<GraphTicketsView>().Any(); i++) await Task.Delay(50);
        if (!window.GetVisualDescendants().OfType<GraphTicketsView>().Any())
        {
            Screenshot(window, Path.Combine(evidence, "mail-entry-failure.png"));
            throw new InvalidOperationException($"Mail not attached: page={((MainViewModel)window.DataContext!).CurrentPage}; available={window.PageAvailableForTesting("graph-tickets")}; pending={window.GraphEntryPendingForTesting}; dock={window.DockLayoutForTesting.Find("graph-tickets")}; selected={window.DockLayoutForTesting.Selected[DockZone.Right]}; pages={string.Join(',', window.DockLayoutForTesting.AllPageIds())}");
        }
        var mail = window.GetVisualDescendants().OfType<GraphTicketsView>().Single();
        Check(vm.HumanInbox.Count == 24 && !mail.IsWideForTesting && mail.IsListVisibleForTesting && !mail.IsDetailVisibleForTesting,
            "docked narrow mail defaults to the human-only list");
        Check(!mail.GetVisualDescendants().OfType<Control>().Any(control => AutomationProperties.GetAutomationId(control) is "GraphNewTicket" or "GraphValidateCandidate" or "GraphAcceptTicket"),
            "mail does not embed internal ticket management forms");
        var messages = vm.HumanInbox.ToArray();
        var states = service.ReadOrganizationSnapshot().Messages.ToDictionary(message => message.Id, message => message.Status);
        var listScroll = mail.ListScrollForTesting ?? throw new InvalidOperationException("Mail list scroll was not loaded");
        listScroll.Offset = new Vector(0, 200); await Settle();
        var offset = listScroll.Offset.Y;
        Check(offset > 0, "mail list scroll has a real nonzero reading position");
        var visibleMail = mail.GetVisualDescendants().OfType<Button>().First(button =>
            (AutomationProperties.GetAutomationId(button) ?? "").StartsWith("GraphInbox-") &&
            button.TranslatePoint(default, mail.InboxForTesting) is { } point && point.Y >= 0 &&
            point.Y + button.Bounds.Height <= mail.InboxForTesting.Bounds.Height);
        var selectedId = AutomationProperties.GetAutomationId(visibleMail)!["GraphInbox-".Length..];
        Click(visibleMail); await Settle();
        Check(mail.IsDetailVisibleForTesting && !mail.IsListVisibleForTesting, "clicking mail opens a single-column detail in a dock");
        mail.DraftForTesting.Text = "TEST-ONLY 尚未发送的回复";
        Screenshot(window, Path.Combine(evidence, "mail-narrow-detail.png"));
        Click(Find<Button>(mail, "GraphMailBack")); await Settle();
        Check(mail.IsListVisibleForTesting && !mail.IsDetailVisibleForTesting && Near(listScroll.Offset.Y, offset), "mail back restores the list reading position");
        mail.OpenHumanMessage(selectedId); await Settle();
        Check(mail.DraftForTesting.Text == "TEST-ONLY 尚未发送的回复", "mail back and reopen preserve the reply draft");
        mail.OpenHumanMessage(messages.First(message => message.Id != selectedId).Id); await Settle();
        mail.DraftForTesting.Text = "TEST-ONLY 另一封草稿";
        mail.OpenHumanMessage(selectedId); await Settle();
        Check(mail.DraftForTesting.Text == "TEST-ONLY 尚未发送的回复", "reply drafts belong to individual messages");
        Check(service.ReadOrganizationSnapshot().Messages.All(message => states.GetValueOrDefault(message.Id) == message.Status), "reading and drafting do not change authoritative message handling status");
        var floatingId = window.FloatDockPage("graph-tickets") ?? throw new InvalidOperationException("Mail did not float");
        var floating = window.FloatingDockWindowForTesting(floatingId)!;
        floating.WindowState = WindowState.Normal; floating.Width = 1100; floating.Height = 760; await Settle();
        Check(mail.IsWideForTesting && mail.IsListVisibleForTesting && mail.IsDetailVisibleForTesting, "wide native floating mail uses list and detail side by side");
        var splitter = Find<GridSplitter>(mail, "GraphMailSplitter");
        Check(splitter.IsEffectivelyVisible && splitter.ResizeDirection == GridResizeDirection.Columns, "wide mail has an adjustable vertical splitter");
        var panes = (Grid)splitter.Parent!;
        panes.ColumnDefinitions[0].Width = new GridLength(360);
        await Settle();
        splitter.Focus();
        splitter.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Right });
        await Settle();
        var adjustedWidth = panes.ColumnDefinitions[0].ActualWidth;
        Check(adjustedWidth > 360, "mail splitter responds to native keyboard adjustment");
        Screenshot(floating, Path.Combine(evidence, "mail-wide.png"));
        floating.Width = 520; await Settle();
        Check(!mail.IsWideForTesting && !mail.IsListVisibleForTesting && mail.IsDetailVisibleForTesting && mail.DraftForTesting.Text == "TEST-ONLY 尚未发送的回复",
            "narrow floating mail degrades to one column without losing its draft");
        Click(Find<Button>(mail, "GraphMailBack")); await Settle();
        Screenshot(floating, Path.Combine(evidence, "mail-floating-list-520.png"));
        floating.Width = 1100; await Settle();
        Check(Math.Abs(panes.ColumnDefinitions[0].ActualWidth - adjustedWidth) < 2 && mail.SelectedMessageIdForTesting == selectedId,
            "wide layout restores adjusted list width and selected mail");
        vm.MoveHistory(-1); await Settle();
        Check(!Find<Button>(mail, "GraphSendOrganizationReply").IsEnabled, "historical mail cannot send replies or decisions");
        vm.ShowRevision(null); await Settle();
        Check(mail.DraftForTesting.Text == "TEST-ONLY 尚未发送的回复", "returning from history restores the current-message draft");
        floating.Close(); await Settle();
        await window.NavigateForTestingAsync(AppPage.Chat); await Settle();
        Check(window.CustomizationForTesting.Pages.All(page => page.Id is not ("graph-properties" or "graph-tickets" or "graph-history" or "graph-structure")) &&
            window.CustomizationForTesting.Pages.Any(page => page.Id == "graph-minimap") && window.CustomizationForTesting.Pages.Single(page => page.Id == "sidechat").Title == "侧聊",
            "Chat keeps its allowed bridge and global side-chat name without Graph-only pages");
        await window.NavigateForTestingAsync(AppPage.Graph);
        var emptyPath = Path.Combine(Path.GetDirectoryName(project.ProjectPath)!, "empty-graph-project");
        Directory.CreateDirectory(emptyPath);
        await window.SwitchProjectForTestingAsync(emptyPath); await Settle();
        var emptyModel = window.GraphModelForTesting!;
        var emptyCanvas = MainCanvas(window);
        emptyCanvas.Fit(); emptyModel.MainViewport.Pan(1e6, -1e6); emptyCanvas.Zoom(1.2);
        Check(emptyModel.Scene.Nodes.Count == 0 && double.IsFinite(emptyModel.MainViewport.X) && double.IsFinite(emptyModel.MainViewport.Y),
            "empty graph accepts pan and zoom without visibility constraints or failures");
        manager.SetProjectTrustDecision(emptyPath, ProjectTrustDecision.DoNotTrust);
        window.OpenDockPage("sidechat", DockZone.Right); await Settle();
        var side = window.PageForTesting("sidechat");
        Click(side.GetVisualDescendants().OfType<Button>().Single(button => button.Content as string == "新建侧聊"));
        for (var i = 0; i < 200 && window.GraphSideChatIdForTesting == null; i++) await Task.Delay(50);
        await Settle();
        Check(window.GraphSideChatIdForTesting != null && ((MainViewModel)window.DataContext!).IsGraphPage,
            "shared side-chat start button opens Graph side Chat without replacing the main graph");
        var report = $"Native Graph interaction: {count}/{count} passed; synthetic routed pointer/gesture events in real native windows; no touchpad hardware test";
        File.WriteAllText(Path.Combine(evidence, "graph-interaction-result.txt"), report);
        Console.WriteLine(report);
        return 0;
    }

    private static GraphCanvas MainCanvas(MainWindow window) => window.GetVisualDescendants().OfType<GraphCanvas>()
        .Single(control => AutomationProperties.GetAutomationId(control) == "GraphCanvas");
    private static T Find<T>(Control root, string id) where T : Control => root.GetVisualDescendants().OfType<T>()
        .Single(control => AutomationProperties.GetAutomationId(control) == id);
    private static bool Near(double a, double b) => Math.Abs(a - b) < .01;
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Press(GraphCanvas canvas, Pointer pointer, Point point, int clicks = 1, bool right = false)
    {
        var root = TopLevel.GetTopLevel(canvas)!;
        canvas.RaiseEvent(new PointerPressedEventArgs(canvas, pointer, root, canvas.TranslatePoint(point, root)!.Value, 0,
            new PointerPointProperties(right ? RawInputModifiers.RightMouseButton : RawInputModifiers.LeftMouseButton,
                right ? PointerUpdateKind.RightButtonPressed : PointerUpdateKind.LeftButtonPressed), KeyModifiers.None, clicks));
    }
    private static void Move(GraphCanvas canvas, Pointer pointer, Point point, bool held = false)
    {
        var root = TopLevel.GetTopLevel(canvas)!;
        canvas.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent, canvas, pointer, root, canvas.TranslatePoint(point, root)!.Value, 0,
            held ? new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other) : PointerPointProperties.None, KeyModifiers.None));
    }
    private static void Release(GraphCanvas canvas, Pointer pointer, Point point)
    {
        var root = TopLevel.GetTopLevel(canvas)!;
        canvas.RaiseEvent(new PointerReleasedEventArgs(canvas, pointer, root, canvas.TranslatePoint(point, root)!.Value, 0,
            PointerPointProperties.None, KeyModifiers.None, MouseButton.Left));
    }
    private static void Wheel(GraphCanvas canvas, Pointer pointer, Point point, Vector delta, KeyModifiers modifiers = KeyModifiers.None)
    {
        var root = TopLevel.GetTopLevel(canvas)!;
        canvas.RaiseEvent(new PointerWheelEventArgs(canvas, pointer, root, canvas.TranslatePoint(point, root)!.Value, 0,
            PointerPointProperties.None, modifiers, delta));
    }
    private static bool ModuleVisible(GraphCanvas canvas, GraphWorkspaceViewModel vm, bool mini = false)
    {
        if (vm.Scene.Nodes.Count == 0) return true;
        var view = mini ? vm.MiniMapViewport : vm.MainViewport;
        return vm.Scene.Nodes.Any(node =>
        {
            var box = new Rect(node.Bounds.X * view.Scale + view.X, node.Bounds.Y * view.Scale + view.Y,
                node.Bounds.Width * view.Scale, node.Bounds.Height * view.Scale);
            var visible = box.Intersect(new Rect(canvas.Bounds.Size));
            return visible.Width + .01 >= Math.Min(48, Math.Min(box.Width, canvas.Bounds.Width)) &&
                visible.Height + .01 >= Math.Min(32, Math.Min(box.Height, canvas.Bounds.Height));
        });
    }
    private static bool ToolbarFits(GraphToolbar toolbar) => toolbar.GetVisualDescendants().OfType<Button>().Where(button => button.IsEffectivelyVisible).All(button =>
    {
        var point = button.TranslatePoint(default, toolbar);
        return point is { } p && p.X >= 0 && p.Y >= 0 && p.X + button.Bounds.Width <= toolbar.Bounds.Width + 1 &&
            p.Y + button.Bounds.Height <= toolbar.Bounds.Height + 1;
    });
    private static async Task Settle()
    {
        await Task.Delay(260);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
    }
    private static void Screenshot(Window window, string path)
    {
        window.UpdateLayout();
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height));
        bitmap.Render(window); bitmap.Save(path, PngBitmapEncoderOptions.Default);
    }
}
