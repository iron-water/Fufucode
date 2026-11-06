using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fufucode.App.Abstractions.Graph;
using Fufucode.App.Features.Graph;
using Fufucode.App.Graph.Ui.Tests;

AppBuilder.Configure<Application>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true }).SetupWithoutStarting();
var app = Application.Current!;
app.Styles.Add(new FluentTheme());
SetResources(false);
var passed = 0;
try
{
    if (args.Contains("--recovery-only")) RecoveryOperations();
    else
    {
        DomainBoundaries();
        NativeInteractions();
        OrganizationInbox();
        DevelopmentBindings();
        InternalCompletion();
        FormBindings();
        EmptyAndLifetime();
        ReceiptPresentation();
        RecoveryOperations();
    }
    Console.WriteLine($"PASS {passed} Graph UI checks; fake service only; no model/network calls");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    return 1;
}

void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException("FAIL " + name);
    passed++; Console.WriteLine("PASS " + name);
}
void Pump() => Dispatcher.UIThread.RunJobs();
void SendKey(Window window, Key key, RawInputModifiers modifiers = RawInputModifiers.None)
{
    window.KeyPress(key, modifiers, PhysicalKey.None, null);
    window.KeyRelease(key, modifiers, PhysicalKey.None, null);
    Pump();
}
void Click(Window window, Point point, RawInputModifiers modifiers = RawInputModifiers.None)
{ window.MouseMove(point); window.MouseDown(point, MouseButton.Left, modifiers); window.MouseUp(point, MouseButton.Left, modifiers); Pump(); }
void Press(Button button) { button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(); }
T Find<T>(Control root, string id) where T : Control => root.GetVisualDescendants().OfType<T>()
    .First(control => AutomationProperties.GetAutomationId(control) == id);

void DomainBoundaries()
{
    using var service = new FakeGraphService();
    using var vm = new GraphWorkspaceViewModel(service);
    var requested = new List<string>();
    vm.RevealPanelRequested += requested.Add;
    Check(service.Subscriptions == 1 && vm.Snapshot.Modules.Count == 5, "service snapshot initializes without demo data");
    vm.SelectModule("a"); vm.SelectModule("b", true);
    Check(vm.SelectedModuleIds.Count == 2 && requested.Last() == "graph-properties", "selection requests existing properties panel");
    vm.ExpandSelected();
    Check(vm.Scene.Nodes.Any(n => n.Module.Id == "a1") && vm.Scene.Nodes.Any(n => n.Module.Id == "b"), "multi expand retains selected leaf");
    Check(vm.Scene.Nodes.Any(n => n.Module.Id == "leaf" && n.IsReference), "cross layer reference preserves real id");
    Check(vm.Scene.Edges.Any(e => e.Relation.Id == "r3" && e.IsUpperLevel && e.Source == vm.Scene.Groups.First(g => g.Module.Id == "a").Bounds),
        "upper level relation attaches to group boundary");
    Check(vm.Scene.Edges.Select(e => e.Relation.Kind).Contains("dataflow") && vm.AvailableRelationKinds.Any(k => k.Id == "Import"),
        "manual and scanner relation types remain intact");
    vm.SelectModules(new[] { "b", "a" });
    vm.MainViewport.Restore(new(37, 52, 1.4, true));
    vm.MiniMapViewport.Restore(new(7, 8, .3, true));
    var liveViewport = vm.MainViewport.Capture();
    vm.BeginModule("a"); vm.ModuleDraft.Name = "Unsaved Alpha";
    vm.PurposeDraft = "Unsaved purpose";
    vm.SelectTicket("t1"); vm.Correspondence.Text = "mail draft";
    var oldModule = vm.Modules.First(m => m.Id == "b");
    var resets = 0;
    vm.Modules.CollectionChanged += (_, e) => { if (e.Action == NotifyCollectionChangedAction.Reset) resets++; };
    var notificationsOnUi = true;
    vm.PropertyChanged += (_, _) => notificationsOnUi &= Dispatcher.UIThread.CheckAccess();
    var reads = service.ReadCalls;
    Task.Run(() =>
    {
        service.Current = service.Current with { Purpose = "External purpose", Modules = service.Current.Modules.Select(m =>
            m.Id == "b" ? m with { Summary = "changed in background" } : m).ToArray() };
        for (var i = 0; i < 20; i++) service.RaiseChanged();
    }).GetAwaiter().GetResult();
    Check(vm.Snapshot.Purpose != "External purpose", "background Changed waits for UI dispatcher");
    Pump();
    Check(notificationsOnUi && service.ReadCalls == reads + 1 && resets == 0, "Changed events coalesce into incremental UI refresh");
    Check(vm.SelectedModuleIds.Count == 2 && vm.ModuleDraft.Name == "Unsaved Alpha" && vm.PurposeDraft == "Unsaved purpose" &&
        vm.Correspondence.Text == "mail draft" && vm.MainViewport.Capture() == liveViewport, "refresh preserves selection drafts and viewport");
    vm.ShowRevision(1);
    Check(vm.IsHistorical && vm.MissingSelectedModule && vm.SelectionLabel == "尚未建立" && vm.MissingSelectedTicket,
        "missing historical identities stay selected and explicit");
    Check(vm.Snapshot.Title == "Old project" && vm.Modules.First(m => m.Id == "root").Name == "Old workspace" && vm.Tickets.Count == 0,
        "history graph names properties and tickets use one frozen snapshot");
    var mutations = service.Mutations;
    Check(!vm.SavePurpose() && !vm.SaveModule() && !vm.SaveRelation() && !vm.CreateTicket() && !vm.SendEntry() &&
        !vm.AddEvidence("build", "proof", true, "build.log") && !vm.AcceptTicket() && !vm.VetoTicket() && !vm.LinkSession("session") &&
        !vm.ScanAsync().GetAwaiter().GetResult() && !vm.RunTicketAsync().GetAwaiter().GetResult() &&
        !vm.StopTicketAsync().GetAwaiter().GetResult() && !vm.SetAutonomyAsync(true).GetAwaiter().GetResult() &&
        !vm.StopAllAsync().GetAwaiter().GetResult() && service.Mutations == mutations, "all historical mutation paths are guarded");
    string? context = null;
    vm.OpenChatRequested += (_, _, value) => context = value;
    vm.OpenChat("a", "t1");
    Check(context == "snapshot context" && service.ContextRevision == 1, "chat receives context from displayed revision");
    vm.ShowRevision(2);
    Check(vm.SelectedModule?.Name == "Old Alpha", "historical rename updates selection properties");
    vm.MoveHistory(1);
    Check(!vm.IsHistorical && vm.SelectedModule?.Name == "Alpha" && vm.MainViewport.Capture() == liveViewport,
        "history forward restores current snapshot and viewport");
    vm.SelectTicket("t2"); vm.Correspondence.Text = "second draft";
    vm.SelectTicket("t1");
    Check(vm.Correspondence.Text == "mail draft" && service.AcceptCalls == 0, "reading and switching mail preserves drafts without acceptance");
    service.Reject = true;
    Check(!vm.SaveModule() && vm.Error?.Contains("authority rejected") == true && vm.ModuleDraft.Name == "Unsaved Alpha", "rejected save stays failed and keeps draft");
    service.Reject = false;
    Check(vm.SaveModule() && vm.SelectedModule?.Name == "Unsaved Alpha", "module save consumes service result");
    service.AsyncReject = true;
    Check(!vm.RunTicketAsync().GetAwaiter().GetResult() && vm.Error?.Contains("run failed") == true, "async run failure never reports success");
    service.AsyncReject = false;
    Check(vm.RunTicketAsync().GetAwaiter().GetResult() && vm.StopTicketAsync().GetAwaiter().GetResult() && service.StopCalls == 1,
        "run and stop delegate to authority");
    vm.SelectModule("a"); vm.SelectTicket("t1");
    vm.Correspondence.Text = "send me";
    Check(vm.SendEntry() && service.AddedEntries.Last().TicketId == "t1" && service.AddedEntries.Last().ModuleId == "a" && vm.Correspondence.Text == "",
        "mail correspondence has selected ticket and module identity");
    Check(vm.AddEvidence("build", "compiled", true, "build.log") && service.AddedEntries.Last().Text.Contains("\"criterion\":\"build\""),
        "acceptance evidence binds criterion and pass result");
    vm.Correspondence.AcceptanceEvidence = "build.log";
    Check(vm.AcceptTicket() && vm.SelectedTicketStatus == "已验收" && service.AcceptCalls == 1, "acceptance is explicit and authoritative");
    vm.GoTop(); vm.MainViewport.Restore(new(10, 20, .8, true));
    var projectViewport = vm.MainViewport.Capture();
    service.SetProject("other", "C:/other"); Pump();
    Check(vm.SelectedModuleId == null && vm.PurposeDraft != "Unsaved purpose", "project switch isolates local state");
    vm.MainViewport.Pan(70, 80);
    service.SetProject("project", "C:/fake-project"); Pump();
    Check(vm.SelectedModuleId == "a" && vm.PurposeDraft == "Unsaved purpose" && vm.MainViewport.Capture() == projectViewport,
        "project return restores prior selection purpose and viewport");
    vm.Dispose();
    Check(service.Subscriptions == 0 && !service.Disposed, "Dispose unsubscribes without disposing shared service");
    var disposedReads = service.ReadCalls; service.RaiseChanged(); Pump();
    Check(service.ReadCalls == disposedReads, "disposed VM ignores queued authority refresh");
}

void NativeInteractions()
{
    using var service = new FakeGraphService();
    using var vm = new GraphWorkspaceViewModel(service);
    using var page = new GraphPageView(vm);
    var after = new Button { Content = "after" };
    var layout = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
    layout.Children.Add(page); Grid.SetRow(after, 1); layout.Children.Add(after);
    var window = new Window { Width = 1100, Height = 720, Content = layout }; window.Show(); window.Activate(); Pump();
    var canvas = page.GetVisualDescendants().OfType<GraphCanvas>().Single();
    Point At(string id) { var center = vm.Scene.FindBounds(id)!.Value.Center;
        var local = new Point(center.X * vm.MainViewport.Scale + vm.MainViewport.X, center.Y * vm.MainViewport.Scale + vm.MainViewport.Y);
        return canvas.TranslatePoint(local, window)!.Value; }
    try
    {
        Check(canvas.Bounds.Width > 500 && canvas.Bounds.Height > 300 && vm.MainViewport.Positioned, "native central canvas lays out and fits");
        Click(window, At("root"));
        Check(vm.SelectedModuleId == "root" && vm.Depth == 0, "single native pointer click selects without drilling");
        Click(window, At("leaf"), RawInputModifiers.Control);
        Check(vm.SelectedModuleIds.Count == 2, "native Ctrl click supports multi selection");
        vm.GoTop();
        var root = At("root");
        window.MouseDown(root, MouseButton.Left); window.MouseUp(root, MouseButton.Left);
        window.MouseDown(root, MouseButton.Left); window.MouseUp(root, MouseButton.Left); Pump();
        Check(vm.Depth == 1 && vm.Scene.Nodes.Any(n => n.Module.Id == "a"), "native double click drills into selected module");
        canvas.Focus(NavigationMethod.Pointer); SendKey(window, Key.Home);
        Check(vm.Depth == 0 && vm.Scene.Nodes.Any(n => n.Module.Id == "leaf"), "Home returns top level");
        SendKey(window, Key.Down);
        Check(vm.Depth == 1 && vm.Scene.Nodes.Any(n => n.Module.Id == "leaf"), "Down traverses depth while retaining leaf");
        SendKey(window, Key.End); var deepest = vm.Depth; SendKey(window, Key.Down);
        Check(vm.Scene.Nodes.Any(n => n.Module.Id == "a1") && vm.Depth == deepest, "End reaches deepest and further Down is bounded");
        SendKey(window, Key.Up); Check(vm.Depth == deepest - 1, "Up returns one depth");
        SendKey(window, Key.Left); Check(vm.IsHistorical && vm.Snapshot.Revision == 2, "Left reads previous revision");
        SendKey(window, Key.Right); Check(!vm.IsHistorical, "Right returns current revision");
        vm.GoTop(); canvas.Focus(NavigationMethod.Pointer); SendKey(window, Key.Tab);
        Check(canvas.IsSpatialFocusMode, "Tab on canvas enters spatial focus mode");
        var focus = canvas.FocusedModuleId;
        SendKey(window, Key.Right);
        Check(canvas.FocusedModuleId != focus && vm.Depth == 0 && !vm.IsHistorical, "spatial Right moves node focus without changing history");
        SendKey(window, Key.Enter);
        Check(vm.SelectedModuleId == canvas.FocusedModuleId, "spatial Enter selects focused node");
        SendKey(window, Key.Escape); Check(!canvas.IsSpatialFocusMode && canvas.IsFocused, "Escape exits spatial mode");
        SendKey(window, Key.Tab); SendKey(window, Key.Tab);
        Check(!canvas.IsSpatialFocusMode && !canvas.IsFocused, "second Tab leaves canvas without focus trap");
        canvas.Focus(NavigationMethod.Tab);
        Check(canvas.IsSpatialFocusMode, "Tab navigation into canvas activates spatial focus");
        SendKey(window, Key.Tab, RawInputModifiers.Shift);
        Check(!canvas.IsSpatialFocusMode && !canvas.IsFocused, "Shift Tab leaves canvas");
        vm.GoTop(); canvas.Focus(NavigationMethod.Pointer);
        var original = vm.MainViewport.Capture();
        var blank = canvas.TranslatePoint(new Point(10, 10), window)!.Value;
        window.MouseDown(blank, MouseButton.Left); window.MouseMove(blank + new Vector(25, 20));
        window.MouseUp(blank + new Vector(25, 20), MouseButton.Left); Pump();
        Check(vm.MainViewport.X != original.X && vm.MainViewport.Y != original.Y, "native drag pans viewport");
        var beforeScale = vm.MainViewport.Scale;
        window.MouseWheel(blank, new Vector(0, 1)); Pump();
        Check(vm.MainViewport.Scale > beforeScale, "native wheel zooms viewport");
        Press(Find<Button>(page, "GraphFit"));
        Check(vm.MainViewport.Scale <= 1.25, "fit button restores visible content");
        Press(Find<Button>(page, "GraphToggleList"));
        Check(vm.IsListView && !canvas.IsVisible && Find<Button>(page, "GraphModule-root").IsVisible,
            "list view provides real module names and selectable controls");
        Press(Find<Button>(page, "GraphToggleList"));
        using var mini = new GraphMiniMapView(vm);
        var miniWindow = new Window { Width = 350, Height = 300, Content = mini }; miniWindow.Show(); Pump();
        try
        {
            var mainState = vm.MainViewport.Capture();
            vm.MiniMapViewport.Pan(20, 30); vm.MiniMapViewport.Zoom(1.3, new Point(30, 40));
            Check(vm.MainViewport.Capture() == mainState && vm.MiniMapViewport.Capture() != mainState,
                "mini map shares scene and keeps independent viewport");
            var miniCanvas = mini.GetVisualDescendants().OfType<GraphCanvas>().Single();
            miniCanvas.Fit();
            var center = vm.Scene.Nodes.First().Bounds.Center;
            var local = new Point(center.X * vm.MiniMapViewport.Scale + vm.MiniMapViewport.X,
                center.Y * vm.MiniMapViewport.Scale + vm.MiniMapViewport.Y);
            var requested = new List<string>(); vm.RevealPanelRequested += requested.Add;
            Click(miniWindow, miniCanvas.TranslatePoint(local, miniWindow)!.Value);
            Check(requested.Contains("graph") && vm.SelectedModuleId == vm.Scene.Nodes.First().Module.Id,
                "mini map click returns to central Graph identity");
        }
        finally { miniWindow.Close(); }
        foreach (var dark in new[] { true, false })
        {
            app.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light; SetResources(dark); Pump();
            Check(canvas.Ink != null && canvas.PageSurface != null, dark ? "dark semantic resources resolve" : "light semantic resources resolve");
        }
    }
    finally { window.Close(); }
}

void OrganizationInbox()
{
    using var service = new FakeGraphService();
    var human = new GraphOrganizationMessage(1, "human-message", "lead", new("human", true, "question", "Human question", "t1", "a", 3), GraphMessageStatus.Delivered, "Awaiting human");
    var internalMessage = human with { Id = "internal-message", Content = human.Content with { RecipientId = "worker", HumanRecipient = false, Text = "Internal log" } };
    var organization = new GraphOrganizationSnapshot("project", 7,
        new[] { new GraphResponsibility("lead", GraphExecutionRole.ProjectLead, new[] { "a" }, "t1") },
        new[] { new GraphWorkDependency("t2", "t1") }, new[] { human, internalMessage }, Array.Empty<GraphCompletionBarrier>());
    service.Current = service.Current with { Organization = organization };
    service.History[2] = service.History[2] with { Organization = organization with { Version = 2, Messages = new[] { human with { Status = GraphMessageStatus.Received } } } };
    using var vm = new GraphWorkspaceViewModel(service);
    using var view = new GraphTicketsView(vm);
    var window = new Window { Width = 700, Height = 900, Content = view }; window.Show(); Pump();
    try
    {
        Check(vm.ShowHumanInbox && vm.HumanInbox.Count == 1 && vm.HumanInbox[0].Id == human.Id,
            "default human inbox excludes internal organization messages");
        Check(!view.GetVisualDescendants().OfType<Button>().Any(b => AutomationProperties.GetAutomationId(b) == "GraphMail-t1"),
            "default inbox does not materialize internal ticket rows");
        var mutations = service.Mutations;
        Press(Find<Button>(view, "GraphInbox-human-message"));
        Check(vm.SelectedTicketId == "t1" && vm.SelectedModuleId == "a" && service.Mutations == mutations && service.AcceptCalls == 0 &&
            vm.SelectedHumanMessage?.Status == GraphMessageStatus.Delivered, "opening inbox selects context without advancing or accepting");
        var texts = view.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToArray();
        Check(texts.Contains("Human question") && texts.Contains("Delivered") && texts.Contains("lead"), "inbox renders sender content and real lifecycle status");
        string? module = "unset", ticket = "unset";
        vm.OpenChatRequested += (m, t, _) => { module = m; ticket = t; };
        Press(Find<Button>(view, "GraphProjectLeadChat"));
        Check(module == null && ticket == null && service.ContextModule == null && service.ContextTicket == null,
            "project lead chat remains project scoped with active selection");
        Find<TextBox>(view, "GraphOrganizationReply").Text = "Human answer";
        Press(Find<Button>(view, "GraphSendOrganizationReply"));
        Check(vm.Error != null && vm.OrganizationReply == "Human answer" && service.Mutations == mutations,
            "fake without organization capability fails visibly and preserves reply draft");
        var toggle = view.GetVisualDescendants().OfType<CheckBox>().Single(c => Equals(c.Content, "内部工作"));
        toggle.IsChecked = true; Pump();
        Check(vm.ShowInternalWork && Find<Button>(view, "GraphMail-t1") != null, "native internal switch restores legacy ticket rows");
        toggle.IsChecked = false; Pump();
        var resets = 0;
        vm.HumanInbox.CollectionChanged += (_, e) => { if (e.Action == NotifyCollectionChangedAction.Reset) resets++; };
        service.Current = service.Current with { Organization = organization with { Version = 8 } }; service.RaiseChanged(); Pump();
        Check(resets == 0 && vm.OrganizationResponsibilities.Count == 1 && vm.WorkDependencies.Count == 1,
            "organization collections refresh incrementally without reset");
        vm.ShowRevision(2); Pump();
        Check(vm.OrganizationVersion == "组织 v2" && vm.HumanInbox.Single().Status == GraphMessageStatus.Received,
            "history organization uses frozen snapshot rather than live authority");
        Check(!Find<Button>(view, "GraphSendOrganizationReply").IsEnabled && !vm.ReplyToHumanMessage() && service.Mutations == mutations,
            "history reply control and command both reject writes");
        vm.ShowRevision(1); Pump();
        Check(vm.HumanInbox.Count == 0 && vm.OrganizationResponsibilities.Count == 0 && vm.WorkDependencies.Count == 0,
            "legacy null organization never falls back to live organization");
        var capable = System.Reflection.DispatchProxy.Create<IOrganizationUiFake, OrganizationUiProxy>();
        var proxy = (OrganizationUiProxy)(object)capable;
        proxy.Inner = service;
        using var replyVm = new GraphWorkspaceViewModel(capable);
        replyVm.SelectHumanMessage(human.Id);
        Check(!replyVm.ReplyToHumanMessage() && proxy.Sends == 0, "blank human reply cannot invoke capable fake");
        replyVm.OrganizationReply = "  Explicit human answer  ";
        Check(replyVm.ReplyToHumanMessage() && proxy.Sends == 1 && proxy.Identity == null &&
            proxy.Draft is { RecipientId: "lead", HumanRecipient: false, Text: "Explicit human answer", TicketId: "t1", ModuleId: "a", Revision: 3 } &&
            replyVm.OrganizationReply == "" && service.AcceptCalls == 0 && service.Current.Organization!.Messages[0].Status == GraphMessageStatus.Delivered,
            "capable fake receives host human reply to original sender without model impersonation or implicit apply");
    }
    finally { window.Close(); }
}

void DevelopmentBindings()
{
    using var service = new FakeGraphService();
    var manifest = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "graph-ui-fixture", "manifest.json"));
    service.Current = service.Current with { Entries = service.Current.Entries.Append(new GraphEntry("candidate", "t1", null,
        "run-result", "executor", System.Text.Json.JsonSerializer.Serialize(new { candidateManifestPath = manifest }), null, 50)).ToArray() };
    using var vm = new GraphWorkspaceViewModel(service);
    vm.ShowInternalWork = true; vm.SelectTicket("t1");
    using var view = new GraphTicketsView(vm);
    var window = new Window { Width = 700, Height = 900, Content = view }; window.Show(); Pump();
    try
    {
        Check(vm.CandidateManifestPath == manifest, "development candidate derives from selected executor run result");
        var expander = view.GetVisualDescendants().OfType<Expander>().Single(e => Equals(e.Header, "候选验证与集成"));
        expander.IsExpanded = true; Pump();
        Find<TextBox>(view, "GraphValidationExecutable").Text = "relative.exe";
        Check(!vm.ValidateCandidateAsync().GetAwaiter().GetResult() && vm.Error != null, "validation refuses nonabsolute executable before host dispatch");
        var exe = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "trusted-fixture.exe"));
        Find<TextBox>(view, "GraphValidationExecutable").Text = exe;
        Find<TextBox>(view, "GraphValidationArguments").Text = "build\nargument with spaces\n\nlast";
        Find<TextBox>(view, "GraphValidationTimeout").Text = "12";
        var validations = 0; var integrations = 0;
        vm.ValidateCandidateRequested += (ticket, candidate, executable, arguments, timeout, _) =>
        {
            Check(ticket == "t1" && candidate == manifest && executable == exe && timeout == TimeSpan.FromSeconds(12) &&
                arguments.SequenceEqual(new[] { "build", "argument with spaces", "", "last" }),
                "native validation form dispatches exact candidate executable individual arguments and timeout");
            validations++; return Task.FromResult("Passed; requires post-integration validation");
        };
        vm.IntegrateCandidateRequested += (ticket, _) => { integrations++; return Task.FromResult("Integrated; target revalidation required"); };
        Press(Find<Button>(view, "GraphValidateCandidate"));
        Check(validations == 1 && vm.DevelopmentResult.Contains("Passed") && service.AcceptCalls == 0 && service.Mutations == 0,
            "validation displays host result without product acceptance or graph mutation");
        Press(Find<Button>(view, "GraphIntegrateCandidate"));
        Check(integrations == 1 && vm.DevelopmentResult.Contains("revalidation required") && service.AcceptCalls == 0,
            "integration dispatch remains separate and displays target revalidation requirement");
        service.RaiseChanged(); Pump();
        Check(vm.ValidationExecutable == exe && vm.ValidationArguments.Contains("argument with spaces"), "refresh preserves validation form draft");
        vm.SelectTicket("t2"); Pump();
        Check(vm.CandidateManifestPath == null && vm.DevelopmentResult == "", "development candidate and result are ticket scoped");
        Check(!vm.ValidateCandidateAsync().GetAwaiter().GetResult() && validations == 1, "ticket without candidate cannot dispatch validation");
        vm.SelectTicket("t1"); vm.ShowRevision(2); Pump();
        Check(!expander.IsEnabled && !vm.ValidateCandidateAsync().GetAwaiter().GetResult() && !vm.IntegrateCandidateAsync().GetAwaiter().GetResult() &&
            validations == 1 && integrations == 1, "historical development UI and commands reject both writes");
    }
    finally { window.Close(); }
}

void InternalCompletion()
{
    using var service = new FakeGraphService();
    using var vm = new GraphWorkspaceViewModel(service);
    vm.SelectTicket("t1");
    var calls = 0;
    vm.CompleteInternalRequested += (ticket, criteria, continuation, reason, token) =>
    {
        Check(ticket == "t1" && criteria.SequenceEqual(new[] { "build", "review" }) && continuation == "active_goal" &&
            reason == "Continue next planned work" && !token.IsCancellationRequested,
            "internal completion dispatches exact selected ticket covered criteria continuation and reason");
        calls++; return Task.FromResult("Internal work complete; product acceptance remains human");
    };
    Check(!vm.CompleteInternalAsync().GetAwaiter().GetResult() && calls == 0, "internal completion rejects empty coverage and reason");
    vm.CoveredCriteria = "build";
    Check(!vm.CompleteInternalAsync().GetAwaiter().GetResult() && calls == 0, "internal completion rejects missing continuation reason");
    vm.CoveredCriteria = " \r\n "; vm.ContinuationReason = "Continue next planned work";
    Check(!vm.CompleteInternalAsync().GetAwaiter().GetResult() && calls == 0, "internal completion rejects whitespace-only coverage");
    vm.CoveredCriteria = "  build\r\n\r\n review  ";
    Check(vm.CompleteInternalAsync().GetAwaiter().GetResult() && calls == 1 && vm.DevelopmentResult.Contains("product acceptance remains human") &&
        service.AcceptCalls == 0 && service.Mutations == 0 && service.Current.Tickets.Single(t => t.Id == "t1").Status == "Review",
        "internal completion displays host result without automatic product acceptance or fake graph mutation");
    vm.ShowRevision(2);
    Check(!vm.CompleteInternalAsync().GetAwaiter().GetResult() && calls == 1 && service.AcceptCalls == 0,
        "historical internal completion cannot invoke host or accept product");
}

void FormBindings()
{
    using var service = new FakeGraphService();
    using var vm = new GraphWorkspaceViewModel(service);
    using var properties = new GraphPropertiesView(vm);
    using var tickets = new GraphTicketsView(vm);
    using var structure = new GraphStructureView(vm);
    using var history = new GraphHistoryView(vm);
    var columns = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*,*") };
    var views = new Control[] { properties, tickets, structure, history };
    for (var i = 0; i < views.Length; i++) { Grid.SetColumn(views[i], i); columns.Children.Add(views[i]); }
    var window = new Window { Width = 1500, Height = 900, Content = columns }; window.Show(); Pump();
    try
    {
        var purpose = Find<TextBox>(properties, "GraphPurpose");
        Check(purpose.Text == "Project purpose", "native TextBox reads workspace binding");
        purpose.Text = "Edited purpose"; Press(Find<Button>(properties, "GraphSavePurpose"));
        Check(service.Current.Purpose == "Edited purpose", "purpose form writes through authority");
        Press(Find<Button>(properties, "GraphNewModule"));
        var name = Find<TextBox>(properties, "GraphModuleName"); name.Text = "New module";
        Find<TextBox>(properties, "GraphModuleSummary").Text = "Summary from form";
        Press(Find<Button>(properties, "GraphSaveModule"));
        Check(service.Current.Modules.Any(m => m.Id == "created" && m.Name == "New module"), "module TextBox form creates module");
        Press(Find<Button>(properties, "GraphEditModule"));
        name.Text = "Draft remains";
        service.RaiseChanged(); Pump();
        Check(name.Text == "Draft remains", "background refresh does not clobber editor text");
        vm.SelectModule("a"); vm.BeginRelation(); Pump();
        vm.RelationDraft.TargetId = "b"; vm.RelationDraft.Kind = "dataflow";
        Find<TextBox>(properties, "GraphRelationLabel").Text = "flow label";
        Press(Find<Button>(properties, "GraphSaveRelation"));
        Check(service.Current.Relations.Any(r => r.SourceId == "a" && r.TargetId == "b" && r.Kind == "dataflow" && r.Label == "flow label"),
            "relation form submits typed endpoints and label");
        vm.ShowInternalWork = true; Pump();
        Press(Find<Button>(tickets, "GraphNewTicket"));
        Find<TextBox>(tickets, "GraphTicketTitle").Text = "Form ticket";
        Find<TextBox>(tickets, "GraphTicketGoal").Text = "Form goal";
        Find<TextBox>(tickets, "GraphTicketAcceptance").Text = "Criterion one\nCriterion two";
        Press(Find<Button>(tickets, "GraphCreateTicket"));
        Check(service.Current.Tickets.Last().Title == "Form ticket" && service.Current.Tickets.Last().ModuleIds.Contains("a"),
            "mail create form retains associated module selection");
        Press(Find<Button>(tickets, "GraphMail-t1"));
        Check(vm.SelectedTicketId == "t1" && service.AcceptCalls == 0, "native mail row opens thread without acceptance");
        Find<TextBox>(tickets, "GraphReplyText").Text = "Reply from form";
        Press(Find<Button>(tickets, "GraphSendEntry"));
        Check(service.AddedEntries.Last().Text == "Reply from form" && service.AddedEntries.Last().Kind == "message",
            "native mail reply sends real correspondence");
        vm.Correspondence.EvidenceCriterion = "build"; vm.Correspondence.EvidencePassed = true;
        Find<TextBox>(tickets, "GraphEvidenceText").Text = "compiled in test";
        Find<TextBox>(tickets, "GraphEvidenceReference").Text = "build.log";
        Press(Find<Button>(tickets, "GraphAddEvidence"));
        Check(service.AddedEntries.Last().Reference == "build.log" && service.AddedEntries.Last().Kind == "evidence",
            "native evidence form adds linked evidence");
        service.Reject = true;
        Find<TextBox>(tickets, "GraphAcceptanceEvidence").Text = "build.log";
        Press(Find<Button>(tickets, "GraphAcceptTicket"));
        Check(vm.Error?.Contains("authority rejected") == true && service.AcceptCalls == 0 && vm.SelectedTicket?.Status == "Review",
            "acceptance rejection is visible and status stays authoritative");
        service.Reject = false;
        vm.ShowRevision(1); Pump();
        Check(!Find<Button>(properties, "GraphSavePurpose").IsEnabled &&
            !Find<Button>(properties, "GraphSaveModule").IsEnabled && !Find<Button>(tickets, "GraphCreateTicket").IsEnabled,
            "historical forms disable write controls");
        var texts = properties.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToArray();
        Check(texts.Contains("尚未建立"), "missing historical object renders explicit text");
        vm.ShowRevision(null); Pump();
        var tree = structure.GetVisualDescendants().OfType<TreeView>().Single();
        Check(tree.Items.Count > 0, "structure panel builds native hierarchy from snapshot");
        var slider = history.GetVisualDescendants().OfType<Slider>().Single(); slider.Value = 0; Pump();
        Check(vm.Snapshot.Revision == 1 && vm.IsHistorical, "history native slider selects the same frozen snapshot");
    }
    finally { window.Close(); }
}

void EmptyAndLifetime()
{
    using var service = new FakeGraphService();
    var initial = service.Current;
    service.Current = initial with { Modules = Array.Empty<GraphModule>(), Relations = Array.Empty<GraphRelation>(),
        Tickets = Array.Empty<GraphTicket>(), Entries = Array.Empty<GraphEntry>(), Revisions = Array.Empty<GraphRevision>() };
    using var vm = new GraphWorkspaceViewModel(service);
    using var page = new GraphPageView(vm);
    using var properties = new GraphPropertiesView(vm);
    var root = new Grid { ColumnDefinitions = new ColumnDefinitions("*,320") };
    root.Children.Add(page); Grid.SetColumn(properties, 1); root.Children.Add(properties);
    var window = new Window { Width = 1000, Height = 680, Content = root }; window.Show(); Pump();
    try
    {
        Check(vm.Scene.Nodes.Count == 0 && page.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "暂无模块" && t.IsVisible),
            "empty authority snapshot renders actual empty state");
        Check(!Find<TextBox>(properties, "GraphModuleName").IsEffectivelyVisible,
            "module editor begins closed");
        Task.Run(() => service.Replace(initial)).GetAwaiter().GetResult(); Pump();
        Check(vm.Scene.Nodes.Count > 0 && page.GetVisualDescendants().OfType<TextBlock>().All(t => t.Text != "暂无模块" || !t.IsVisible),
            "background scan result removes empty overlay and updates scene binding");
        vm.BeginRelation("r3"); vm.RelationDraft.Label = "relationship draft";
        vm.ShowRevision(1); vm.ShowRevision(null); vm.BeginRelation("r3");
        Check(vm.RelationDraft.Label == "relationship draft" && vm.RelationDraft.Kind == "Import" && vm.RelationDraft.SourceId == "a" &&
            vm.RelationDraft.TargetId == "b", "history return preserves relation draft endpoints and scanner type");
        vm.SelectModule("a"); vm.BeginModule("a"); vm.ModuleDraft.Name = "module draft";
        vm.ShowRevision(1); vm.ShowRevision(null); vm.BeginModule("a");
        Check(vm.ModuleDraft.Name == "module draft", "history return preserves module editor draft");
        vm.GoBottom(); vm.IsListView = true; Pump();
        Check(vm.VisibleModules.Any(m => m.Id == "root") && vm.VisibleModules.Any(m => m.Id == "a1"),
            "expanded list includes group names and descendant leaves");
        vm.IsListView = false;
        var saved = vm.MainViewport.Capture(); root.Children.Remove(page); Pump();
        service.Replace(service.Current with { Title = "Changed while hidden" }); Pump();
        root.Children.Add(page); Pump();
        Check(vm.MainViewport.Capture() == saved && Find<GraphCanvas>(page, "GraphCanvas").IsVisible,
            "page detach and return retains viewport and current scene");
        vm.SelectTicket("t1"); service.PendingRun = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = vm.RunTicketAsync();
        Check(!running.IsCompleted && vm.IsBusy, "pending authority operation exposes busy state");
        vm.Dispose();
        Check(service.RunToken.IsCancellationRequested && service.Subscriptions == 0 && !service.Disposed,
            "Dispose cancels owned pending operation and releases subscription");
        var limit = System.Diagnostics.Stopwatch.StartNew();
        while (!running.IsCompleted && limit.ElapsedMilliseconds < 2000) { Pump(); Thread.Yield(); }
        Check(running.IsCompleted && !running.GetAwaiter().GetResult() && !vm.RunTicketAsync().GetAwaiter().GetResult(),
            "cancelled and disposed runs return failure without restarting service");
    }
    finally { window.Close(); }
}

void ReceiptPresentation()
{
    using var service = new FakeGraphService();
    var payload = "{\"title\":\"\\u5DE5\\u5355\",\"goal\":\"\\u76EE\\u6807\",\"moduleIds\":[\"a\"]}";
    var source = new GraphEntry("source-record", null, "a", "source", "scanner", "{\"hash\":\"retained-hash\",\"detail\":\"internal-source\"}", "src/Alpha.cs", 1);
    var created = new GraphEntry("ticket-created", "t1", null, "ticket-created", "human", payload, null, 2);
    service.Current = service.Current with { ProjectStatus = "NeedsPurpose", Entries = new[] { source, created },
        Tickets = service.Current.Tickets.Select(t => t with { Status = "Proposed" }).ToArray(),
        Modules = service.Current.Modules.Select(m => m.Id == "a" ? m with { Summary = "Directory: source" } : m).ToArray(),
        Revisions = new[] { new GraphRevision(1, "Scanned real project sources", 1), new GraphRevision(2, "Created work ticket", 2),
            new GraphRevision(3, "Added project correspondence", 3) } };
    using var vm = new GraphWorkspaceViewModel(service);
    Check(vm.ProjectStatus == "待设置目的" && GraphLabels.Status("Unknown") == "状态未知" &&
        GraphLabels.Status("future-state") == "future-state", "known statuses localize while unknown states stay authoritative");
    Check(GraphEntryPresentation.Summary(created) == "工单已建立" + Environment.NewLine + "工单" + Environment.NewLine + "目标" && created.Text == payload,
        "ticket creation presents decoded title and goal without changing raw receipt");
    Check(GraphLabels.EntryKind("source") == "来源" && GraphLabels.Actor("human") == "你" &&
        GraphLabels.ModuleSummary("Directory: source") == "目录：source" && GraphLabels.ModuleSummary("File: Alpha.cs") == "文件：Alpha.cs",
        "actors kinds and file summaries use user language");
    using var tickets = new GraphTicketsView(vm);
    using var history = new GraphHistoryView(vm);
    using var properties = new GraphPropertiesView(vm);
    var columns = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*") };
    columns.Children.Add(tickets); Grid.SetColumn(history, 1); columns.Children.Add(history); Grid.SetColumn(properties, 2); columns.Children.Add(properties);
    var window = new Window { Width = 1300, Height = 900, Content = columns }; window.Show(); Pump();
    try
    {
        vm.ShowInternalWork = true;
        vm.SelectTicket("t1"); vm.SelectModule("a"); Pump();
        var visibleText = tickets.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? "").ToArray();
        Check(visibleText.Any(t => t.Contains("工单已建立") && t.Contains("目标")) && visibleText.All(t => !t.Contains("\\u5DE5") && !t.Contains("ticket-created") && !t.Contains("human")),
            "internal mail view renders readable correspondence without internal JSON or actor ids");
        var raw = tickets.GetVisualDescendants().OfType<Expander>().First(e => e.Header as string == "原始记录");
        Check(!raw.IsExpanded && raw.Content == null, "raw receipt stays collapsed and unmaterialized by default");
        raw.IsExpanded = true; Pump();
        Check(raw.GetVisualDescendants().OfType<TextBox>().Any(t => t.Text == payload), "raw receipt expands to unchanged source text");
        var sources = Find<Expander>(history, "GraphHistoryRawSources");
        Check(!sources.IsExpanded && sources.Content == null && !history.GetVisualDescendants().OfType<TextBlock>().Any(t => t.IsEffectivelyVisible &&
            (t.Text?.Contains("retained-hash") == true || t.Text?.Contains("internal-source") == true)),
            "history does not materialize scanner source records by default");
        Check(history.GetVisualDescendants().OfType<Button>().Any(b => b.Content is string value && value.Contains("项目扫描完成")),
            "history revision summary uses human readable change");
        sources.IsExpanded = true; Pump();
        var sourceRecord = sources.GetVisualDescendants().OfType<Expander>().Single(); sourceRecord.IsExpanded = true; Pump();
        Check(sourceRecord.GetVisualDescendants().OfType<TextBox>().Any(t => t.Text == source.Text), "scanner provenance stays inspectable on explicit expansion");
        Check(properties.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "目录：source") &&
            vm.SelectedModule?.Summary == "Directory: source", "module property display localizes prefix and retains snapshot truth");
    }
    finally { window.Close(); }
}

void RecoveryOperations()
{
    using var service = new FakeGraphService();
    var veto = new GraphEntry("veto", "t1", null, "Veto", "human",
        "{\"status\":\"Active\",\"target\":\"ticket:t1\"}", null, 1);
    service.Current = service.Current with { Tickets = service.Current.Tickets.Select(t => t.Id == "t1"
        ? t with { Status = "NeedsReconciliation" } : t).ToArray(), Entries = service.Current.Entries.Append(veto).ToArray() };
    using var vm = new GraphWorkspaceViewModel(service);
    using var tickets = new GraphTicketsView(vm);
    var window = new Window { Width = 560, Height = 850, Content = tickets }; window.Show(); Pump();
    try
    {
        vm.ShowInternalWork = true;
        vm.SelectTicket("t1"); Pump();
        var recovery = Find<Expander>(tickets, "GraphRecovery");
        Check(!recovery.IsExpanded && recovery.Header as string == "人工恢复", "recovery operations default to a collapsed decision section");
        Check(vm.HasActiveTicketVeto && vm.HasTicketRecoveryActions && !GraphEntryPresentation.IsActiveTicketVeto(veto, "t2"),
            "active veto visibility follows authority decision and exact ticket target");
        recovery.IsExpanded = true; Pump();
        var retry = Find<Button>(tickets, "GraphAllowRetry");
        var lift = Find<Button>(tickets, "GraphLiftVeto");
        var resolution = Find<TextBox>(tickets, "GraphRecoveryResolution");
        var reason = Find<TextBox>(tickets, "GraphLiftVetoReason");
        var effects = Find<CheckBox>(tickets, "GraphRecoveryChecked");
        Check(vm.NeedsTicketReconciliation && resolution.IsEffectivelyVisible && retry.Content as string == "允许重试" && !retry.IsEnabled,
            "unknown result state exposes recovery and requires side effect check");
        Check(!vm.ReconcileTicket() && service.ReconcileCalls == 0 && vm.Error?.Contains("副作用") == true,
            "unchecked side effects never reach recovery authority");
        effects.IsChecked = true; Pump();
        Check(retry.IsEnabled && !vm.ReconcileTicket() && service.ReconcileCalls == 0 && vm.Error?.Contains("说明不能为空") == true,
            "recovery requires nonempty resolution before command");
        resolution.Text = "已核查文件写入并撤回未完成改动"; reason.Text = "已确认原否决原因解除"; Pump();
        vm.SelectTicket("t2"); Pump();
        Check(!vm.NeedsTicketReconciliation && !resolution.IsEffectivelyVisible && !recovery.IsVisible,
            "ordinary ticket hides unknown result recovery entry");
        vm.Correspondence.RecoveryResolution = "other ticket draft";
        Check(!vm.ReconcileTicket() && service.ReconcileCalls == 0, "ordinary ticket cannot invoke recovery through VM");
        vm.SelectTicket("t1"); Pump();
        Check(resolution.Text == "已核查文件写入并撤回未完成改动" && reason.Text == "已确认原否决原因解除" && effects.IsChecked == true,
            "recovery reason resolution and side effect check remain scoped by ticket");
        service.Reject = true; Press(retry);
        Check(service.ReconcileCalls == 0 && vm.Error?.Contains("authority rejected") == true && resolution.Text == "已核查文件写入并撤回未完成改动" &&
            vm.SelectedTicket?.Status == "NeedsReconciliation", "authority recovery rejection keeps state and draft without success");
        service.Reject = false;
        Press(retry);
        Check(service.ReconcileCalls == 1 && service.RecoveryTicketId == "t1" && service.RecoveryResolution == "已核查文件写入并撤回未完成改动" &&
            vm.SelectedTicket?.Status == "Authorized", "allow retry submits selected ticket and exact human resolution");
        Check(!vm.NeedsTicketReconciliation && !resolution.IsEffectivelyVisible && service.RunCalls == 0 && service.AutonomyCalls == 0,
            "recovery refresh hides entry and never starts execution or queue advancement");
        service.Replace(service.Current with { Tickets = service.Current.Tickets.Select(t => t.Id == "t1"
            ? t with { Status = "ReplanRequired" } : t).ToArray() }); Pump();
        reason.Text = "   "; Pump();
        Check(!vm.LiftVeto() && service.LiftVetoCalls == 0 && vm.Error?.Contains("解除理由不能为空") == true,
            "lifting veto requires nonempty human reason");
        reason.Text = "已核实可恢复原工作"; service.Reject = true; Press(lift);
        Check(service.LiftVetoCalls == 0 && vm.Error?.Contains("authority rejected") == true && reason.Text == "已核实可恢复原工作",
            "veto authority failure remains visible and retains reason");
        service.Reject = false; Press(lift);
        Check(service.LiftVetoCalls == 1 && service.RecoveryTicketId == "t1" && service.LiftVetoReason == "已核实可恢复原工作" &&
            service.RunCalls == 0 && service.AutonomyCalls == 0 && vm.SelectedTicket?.Status == "ReplanRequired",
            "lifting veto submits reason and leaves authoritative replanning state without running");
        Check(!vm.HasActiveTicketVeto && !recovery.IsVisible && service.Current.Entries.First(e => e.Id == veto.Id).Text.Contains("Lifted"),
            "lifted decision hides recovery action while retaining original veto identity");
        vm.ShowRevision(2); Pump(); var mutations = service.Mutations;
        Check(!lift.IsEnabled && !retry.IsEnabled && !vm.LiftVeto() && !vm.ReconcileTicket() && service.Mutations == mutations,
            "historical recovery buttons and direct VM mutations stay read only");
        vm.ShowRevision(null); Pump();
        Check(vm.Correspondence.LiftVetoReason == "已核实可恢复原工作" && vm.Correspondence.RecoveryResolution == "已核查文件写入并撤回未完成改动",
            "recovery drafts survive historical round trip");
    }
    finally { window.Close(); }
}

void SetResources(bool dark)
{
    app.Resources["AppTextPrimary"] = new SolidColorBrush(dark ? Color.Parse("#FFFFFF") : Color.Parse("#1A1A1A"));
    app.Resources["AppTextSecondary"] = new SolidColorBrush(dark ? Color.Parse("#D0D0D0") : Color.Parse("#555555"));
    app.Resources["AppCardBackground"] = new SolidColorBrush(dark ? Color.Parse("#2C2C2C") : Colors.White);
    app.Resources["AppBorderBrush"] = new SolidColorBrush(dark ? Color.Parse("#777777") : Color.Parse("#777777"));
    app.Resources["AppAccent"] = new SolidColorBrush(Color.Parse(dark ? "#60CD8B" : "#107C41"));
    app.Resources["AppListSelected"] = new SolidColorBrush(Color.Parse(dark ? "#405048" : "#E5EEE8"));
    app.Resources["AppMicaPageBackground"] = new SolidColorBrush(Color.Parse(dark ? "#1C1C1C" : "#F3F3F3"));
    app.Resources["AppCriticalForeground"] = new SolidColorBrush(Color.Parse(dark ? "#FF9999" : "#B00020"));
}

public interface IOrganizationUiFake : IGraphProjectService, IGraphOrganizationService { }
public class OrganizationUiProxy : System.Reflection.DispatchProxy
{
    internal IGraphProjectService Inner = null!;
    internal int Sends;
    internal GraphMessageDraft? Draft;
    internal GraphExecutionIdentity? Identity;
    protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? arguments)
    {
        if (method!.Name == nameof(IGraphOrganizationService.SendOrganizationMessage))
        {
            Sends++; Draft = (GraphMessageDraft)arguments![1]!; Identity = (GraphExecutionIdentity?)arguments[2];
            return Inner.ReadSnapshot().Organization!;
        }
        return method.Invoke(Inner, arguments);
    }
}
