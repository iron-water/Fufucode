using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using Fufucode.App.Features.Automation;
using Fufucode.App.Integration;
using Fufucode.Core.Automation;
using Fufucode.Core.Automation.Harnesses;
using Fufucode.Core.Automation.Hosting;
using Microsoft.Data.Sqlite;

namespace Fufucode.App.SelfTest;

internal static partial class AutomationControlSelfTest
{
    internal static async Task ProtocolAsync(string[] args)
    {
        var prompt = await Console.In.ReadToEndAsync(); var modelAt = Array.IndexOf(args, "--model");
        if (modelAt < 0 || args[modelAt + 1] != "devin/swe-2") throw new Exception("Explicit cheapest model required");
        if (Path.GetFileName(Environment.CurrentDirectory) == "tree-b")
        { File.WriteAllText("b-delivery.txt", "fixture code\n"); await Git(Environment.CurrentDirectory, "add", "b-delivery.txt"); await Git(Environment.CurrentDirectory, "commit", "-q", "-m", "fixture code delivery"); }
        Console.WriteLine(JsonSerializer.Serialize(new { type = "thread.started", thread_id = "native-" + Path.GetFileName(Environment.CurrentDirectory) }));
        if (prompt == "hold")
        {
            var until = DateTime.UtcNow.AddSeconds(30);
            while (!File.Exists(args[1] + ".release")) { if (DateTime.UtcNow > until) throw new TimeoutException(); await Task.Delay(20); }
        }
        Console.WriteLine("{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\",\"text\":\"native UI fixture evidence\"}}");
        Console.WriteLine("{\"type\":\"turn.completed\"}");
    }
    internal static async Task RunAsync(Action<string> pass, Action<string> fail)
    {
        void Check(bool value, string name) { if (value) pass("C5: " + name); else fail("C5: " + name); }
        var root = Path.Combine(Program.SelfTestDataRoot!, "automation"); Directory.CreateDirectory(root);
        var db = Path.Combine(root, "control.db");
        try { using var missing = AutomationStore.AttachControlClient(Path.Combine(root, "missing", "none.db")); Check(false, "missing client rejected"); }
        catch (FileNotFoundException) { Check(!Directory.Exists(Path.Combine(root, "missing")), "connecting missing database does not create a directory or second authority"); }
        using var store = new AutomationStore(db); var consumer = new ControlIntentConsumer(store); var journal = new HostJournal(store);
        var n = 0; string Id() => "native-c5-" + ++n; long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); long Rev() => store.ReadControlSnapshot().Revision;
        var executable = Environment.ProcessPath!; var prefix = Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? new[] { typeof(Program).Assembly.Location, "--automation-protocol", Path.Combine(root, "gate") } : new[] { "--automation-protocol", Path.Combine(root, "gate") };
        var runtime = new HarnessRuntime("c5-fixture", "codex", executable, prefix, "fixture-v1");
        var runs = new System.Collections.Generic.Dictionary<string, ControlQueueReceipt>();
        var heads = new System.Collections.Generic.Dictionary<string, string>();
        foreach (var id in new[] { "a", "b" })
        {
            var tree = Path.Combine(root, "tree-" + id); Directory.CreateDirectory(tree);
            await Git(tree, "init", "-q"); await Git(tree, "config", "user.name", "Fixture"); await Git(tree, "config", "user.email", "fixture@example.invalid");
            File.WriteAllText(Path.Combine(tree, "fixture.txt"), "test\n"); await Git(tree, "add", "fixture.txt"); await Git(tree, "commit", "-q", "-m", "fixture");
            heads[id] = (await Git(tree, "rev-parse", "HEAD")).Trim();
            if (id == "b")
            {
                await Git(tree, "fetch", "-q", Path.Combine(root, "tree-a"), heads["a"]);
                await Git(tree, "checkout", "--detach", "-q", "FETCH_HEAD"); heads[id] = heads["a"];
            }
            store.ApplyControlCommand(new(Id(), Rev(), "fixture", ControlAction.DefinePackage, new(id, "same", "Inspect one fixture", ["fixture.txt", "b-delivery.txt"], ["independent evidence"], "codex", "devin/swe-2", id == "b" ? "code" : "evidence", [])
                { KnowledgeRefs = id == "a" ? ["held-policy"] : null }, ExpectedPackageVersion: 0), Now());
            store.CreateThread("thread-" + id, "project", "codex", "devin/swe-2", tree, null, Now());
            store.CreateTask("task-" + id, "thread-" + id, "same", heads[id], Now());
            store.CreateTask("extra-" + id, "thread-" + id, "same", heads[id], Now());
        }
        store.ApplyControlRelation(new(Id(), Rev(), "fixture", ControlRelationAction.DefineGroup,
            new("competition", ControlRelation.Competitive, ["a", "b"], heads["a"], ["Return independently accepted fixture evidence"])), Now());
        foreach (var id in new[] { "a", "b" })
            store.ApplyControlCommand(new(Id(), Rev(), "fixture", ControlAction.ApprovePackage, PackageId: id, ExpectedPackageVersion: 1), Now());
        store.ApplyControlCommand(new(Id(), Rev(), "fixture", ControlAction.SetMode, Mode: DevelopmentMode.SemiAutomatic), Now());
        foreach (var id in new[] { "a", "b" }) runs[id] = await consumer.QueueAsync(new(Id(), Rev(), "execute:" + id + ":1", 1, "thread-" + id, "task-" + id, runtime), Now());
        await using (var host = new BackgroundHost(new(db, Path.Combine(root, "runs"), "native-host", Path.Combine(root, "endpoint"))))
        {
            Check(await host.StartAsync(), "fixture uses real sole host and explicit model protocol children");
            await Until(() => runs.Values.All(r => journal.Get(r.RunId)!.State == HostRequestState.Settled)); await host.StopAsync();
        }
        var runStore = new HarnessRunStore(store); var receiver = new DeliveryReceiver(store, runStore, GitCliEvidence.Shared);
        foreach (var id in new[] { "a", "b" }) heads[id] = runStore.Get(runs[id].RunId).SettleHead!;
        foreach (var id in new[] { "a", "b" }) foreach (var part in id == "a" ? new[] { "a-one", "a-two" } : new[] { "b-one" })
        {
            store.CreatePart(part, "task-" + id, "same", Now());
            var received = await receiver.ReceiveAsync(new(runs[id].RunId, "thread-" + id, "task-" + id, part, heads[id], "{\"text\":\"OPAQUE_CARD\"}", Id(), Id(), Now()) { CardStatus = "module_ready" });
            Check(received.AcceptedForReview, "actual stopped run provides bound fixture card " + part);
        }
        store.AddDependency("task-b", "task-a", "a-one", requiresIntegration: false);
        var versionCopy = Path.Combine(root, "legacy.db");
        using (var source = new SqliteConnection("Data Source=" + db)) using (var copy = new SqliteConnection("Data Source=" + versionCopy))
        { source.Open(); copy.Open(); source.BackupDatabase(copy); using var q = copy.CreateCommand(); q.CommandText = "PRAGMA user_version=1"; q.ExecuteNonQuery(); }
        try { using var legacy = AutomationStore.AttachControlClient(versionCopy); Check(false, "legacy client rejected"); }
        catch (InvalidOperationException)
        { using var c = new SqliteConnection("Data Source=" + versionCopy); c.Open(); using var q = c.CreateCommand(); q.CommandText = "PRAGMA user_version"; Check(Convert.ToInt32(q.ExecuteScalar()) == 1, "connecting old schema neither migrates nor edits it"); }
        Check(store.ReadControlClientSnapshot().Graph is null, "legacy client snapshot stays additive and omits graph by default");
        store.ApplyControlCommand(new(Id(), Rev(), "fixture", ControlAction.DefinePackage,
            new("draft", "draft", "Unbound plan goal", ["fixture.txt"], ["fixture acceptance"], "codex", "devin/swe-2", "evidence", []), ExpectedPackageVersion: 0), Now());
        using var model = new AutomationControlViewModel(); var view = new AutomationControlPageView(); view.Attach(model);
        var window = new Window { Width = 980, Height = 800, Content = view }; window.Show();
        try
        {
            await model.ConnectAsync(db); await Paint(window);
            Check(model.Connected && model.Snapshot!.Graph!.Threads.Count == 2 && model.Snapshot.Graph.Threads.All(t => t.Tasks.Count == 2), "one native snapshot contains actual multi-task threads");
            Check(model.Snapshot!.State.Control.Revision == model.Snapshot.Inbox.Revision && model.Snapshot.State.EventCursor == model.Snapshot.Inbox.EventCursor, "graph/state/inbox share authoritative revision and event watermark");
            model.SelectedDecision = model.Inbox.Single(r => r.Item.Kind == "plan" && r.Item.ObjectId == "draft"); await model.DecideCommand.ExecuteAsync("approve");
            Check(store.ReadControlSnapshot().Packages.Single(p => p.Spec.Id == "draft").Approved && journal.List().Count == 2 && model.Snapshot!.Graph!.Threads.Count == 2,
                "native plan approval records an intent without inventing a thread or launching work");
            await Paint(window);
            // v4 页面默认在总览页签；依赖图是本 tab 内的第二视角，测试先切到执行线程 → 依赖图。
            model.SelectTab("threads"); model.SetThreadView("graph"); await Paint(window);
            var canvas = view.FindControl<Canvas>("GraphCanvas")!;
            var nodes = canvas.Children.OfType<Border>().ToArray();
            Check(nodes.Length == 2 && nodes[1].Bounds.Top > nodes[0].Bounds.Bottom && nodes.All(node => node.Bounds.Left == 12), "actual Canvas children arrange at the intended nonoverlapping coordinates");
            Click(view, "task:extra-b"); Check(model.DetailText.Contains("extra-b") && !model.DetailText.Contains("extra-a"), "duplicate title task click resolves real task ID");
            Click(view, "part:a-one"); Check(model.SelectedDecision?.Item.ObjectId == "a-one" && model.DetailText.Contains("OPAQUE_CARD"), "part click opens stopped card and exact review context");
            model.ReasonText = "unfinished user draft"; var originalNode = nodes[0]; await model.RefreshAsync();
            Check(ReferenceEquals(originalNode, canvas.Children.OfType<Border>().First()) && model.ReasonText == "unfinished user draft",
                "unchanged refresh preserves actual graph controls and the user's decision draft");
            Click(view, "part:b-one"); Check(model.SelectedDecision?.Item.ObjectId == "b-one", "duplicate title on another thread resolves its own part");
            Check(view.TaskPorts.ContainsKey("task-a") && view.PartPorts.ContainsKey("a-one") && canvas.Children.OfType<Avalonia.Controls.Shapes.Path>().Single().Tag is ControlDependencyEdge { SourcePartId: "a-one", TargetTaskId: "task-b" }, "actual dependency curve binds the requested part and target task");
            var button = FindButton(view, "part:a-one"); var center = button.TranslatePoint(new Avalonia.Point(button.Bounds.Width / 2, button.Bounds.Height / 2), canvas)!.Value;
            Check(Math.Abs(center.Y - view.PartPorts["a-one"].Y) < 1, $"real arranged part button aligns with its curve port: actual={center.Y}, port={view.PartPorts["a-one"].Y}");
            Check(canvas.Children.OfType<Border>().Count() == 2, "dependency graph keeps every registered thread node, idle siblings included");
            var executor = new HarnessExecutor(store, Path.Combine(root, "held-runs"), "native-held-host", GitCliEvidence.Shared);
            var held = executor.ExecuteAsync(runtime, new("held-a", "thread-a", "extra-a", "devin/swe-2", "hold"));
            try
            {
                await Until(() => executor.Runs.Get("held-a").Status == HarnessRunStatus.Running);
                await model.RefreshAsync(); await Paint(window);
                Check(model.Snapshot!.Graph!.Threads.Single(t => t.ThreadId == "thread-a").Running &&
                    model.ThreadRows.Single(row => row.ThreadId == "thread-a").Running &&
                    !model.ThreadRows.Single(row => row.ThreadId == "thread-b").Running,
                    "real held writer is reported as running while the idle sibling stays not running");
                model.SelectPart("a-one");
                Check(model.SelectedDecision is { Item.Actionable: false } && !model.DetailText.Contains("OPAQUE_CARD") && !model.DecideCommand.CanExecute("accept"),
                    "active writer redacts card body and disables native review");
                await CheckHeldContractMutationsAsync(store, root, Check);
            }
            finally { File.WriteAllText(Path.Combine(root, "gate.release"), "release"); await held; }
            await Until(() => !model.IsBusy); await model.RefreshAsync(); await Paint(window); await Until(() => !model.IsBusy);
            Click(view, "part:a-one"); Check(!model.DecideCommand.CanExecute("accept"), "native accept requires explicit verification evidence");
            model.EvidenceText = "Independently inspected fixture source, HEAD, and stopped writer";
            store.ApplyControlCommand(new(Id(), Rev(), "other-human", ControlAction.SetMode, Mode: DevelopmentMode.Manual), Now());
            await model.DecideCommand.ExecuteAsync("accept");
            Check(store.GetPart("a-one").Status == DeliveryPartStatus.Delivered && model.StatusText.Contains("操作未完成"), "stale native review refuses without silently rebasing the decision");
            await Until(() => !model.IsBusy); await model.SetModeAsync("semi"); Click(view, "part:a-one"); model.EvidenceText = "Rechecked current revision and stopped writer";
            await model.DecideCommand.ExecuteAsync("accept");
            Check(store.GetPart("a-one").Status == DeliveryPartStatus.Accepted && store.GetPart("a-two").Status == DeliveryPartStatus.Delivered && !store.IsIntegrated("a-one", 1), "native partial accept does not accept its sibling or claim integration");
            var decisions = store.ListDecisions("a-one").Count; await model.RetryReceiptAsync(); Check(store.ListDecisions("a-one").Count == decisions, "native original receipt replay does not duplicate a recorded review");
            await Until(() => !model.IsBusy); Click(view, "part:a-two"); model.EvidenceText = "Inspected fixture"; model.ReasonText = "Correct only this part"; await model.DecideCommand.ExecuteAsync("rework");
            Check(store.GetPart("a-two").Status == DeliveryPartStatus.ReworkRequested && store.GetPart("a-one").Status == DeliveryPartStatus.Accepted, "native rework preserves accepted sibling");
            await Until(() => !model.IsBusy); model.SelectedDecision = model.Inbox.Single(r => r.Item.Kind == "repair"); model.ReasonText = "Authorize original session repair";
            Check(model.DecideCommand.CanExecute("request_repair"), "repair button uses Core's actual request_repair option"); await model.DecideCommand.ExecuteAsync("request_repair");
            Check(store.ReadControlSnapshot().Repairs.Single().Status == "awaiting_consumer" && journal.List().Count == 2, "native repair approval waits for trusted consumer and does not start a model");
            await Until(() => !model.IsBusy); Click(view, "part:b-one"); model.EvidenceText = "Inspected source"; model.ReasonText = "Missing check"; await model.DecideCommand.ExecuteAsync("blocked");
            await Until(() => !model.IsBusy); model.SelectedDecision = model.Inbox.Single(r => r.Item.Kind == "reconsider"); model.ReasonText = "Check now available"; await model.DecideCommand.ExecuteAsync("reopen");
            Check(store.GetPart("b-one").Status == DeliveryPartStatus.Delivered && store.ListDecisions("b-one").Single().Decision == AutomationDecision.Blocked,
                "native reconsider reopens original evidence without deleting the blocked decision");
            await Until(() => !model.IsBusy); Click(view, "part:b-one"); model.EvidenceText = "Source check complete"; await model.DecideCommand.ExecuteAsync("accept");
            model.SelectedDecision = model.Inbox.Single(r => r.Item.Kind == "choice"); await Paint(window);
            Check(!model.DecideCommand.CanExecute("select") && model.WinnerChoices.Single().Outcome.PackageId == "b" && model.DetailText.Contains(heads["a"]) && model.DetailText.Contains("Return independently"),
                "competition displays baseline and criteria, excludes incomplete sibling, and requires an explicit winner");
            var selector = view.FindControl<ComboBox>("WinnerInput")!; selector.SelectedItem = model.WinnerChoices.Single();
            Check(model.SelectedWinner?.Outcome.PackageId == "b", "compiled winner selector binds the candidate's ID and evidence rather than its duplicate title");
            await PressDecision(window, view, "选择此候选", () => store.ApplyControlCommand(new(Id(), Rev(), "other-human", ControlAction.SetMode, Mode: DevelopmentMode.Manual), Now()));
            Check(store.ReadControlGroups().Single().Winner is null && model.StatusText.Contains("操作未完成"), "external state change refuses the captured winner without substituting fresh evidence");
            model.SelectedDecision = model.Inbox.Single(r => r.Item.Kind == "choice"); selector.SelectedItem = model.WinnerChoices.Single();
            await PressDecision(window, view, "选择此候选");
            Check(store.ReadControlGroups().Single().Winner == "b" && !store.IsIntegrated("b-one", 1), "actual focused decision button selects winner without integration");
            var selectionRevision = Rev(); await model.RetryReceiptAsync(); Check(Rev() == selectionRevision, "winner receipt replay is idempotent");
            await Until(() => !model.IsBusy); model.SelectedDecision = model.Inbox.Single(r => r.Item.Kind == "integration" && r.Item.ObjectId == "b"); await model.DecideCommand.ExecuteAsync("authorize");
            Check(store.ReadControlSnapshot().IntegrationAuthorizations.Single().Status == "authorized" && !store.IsIntegrated("b-one", 1), "native integration grant records permission without fabricating a Git merge");
            await Until(() => !model.IsBusy); model.SelectedDecision = model.Inbox.Single(r => r.Item.Kind == "authorization"); model.ReasonText = "Withdraw fixture permission"; await model.DecideCommand.ExecuteAsync("revoke");
            Check(store.ReadControlSnapshot().IntegrationAuthorizations.Single().Status == "revoked", "native revoke is bound to the exact authorization ID");
            await RunAdvancedAsync(store, model, view, window, Check);
            await RunCreationAsync(store, model, view, window, Check);
            await RunStopAsync(store, model, view, window, root, Check);
            model.ReturnToGraph(); Check(!model.HasDetails && model.SelectedDecision is null, "return action clears details without disconnecting or closing the tab");
            // v4 窄宽（<1000DIP）改为两列以下单列堆叠；图仍在自己的滚动视口内保持可读节点宽度。
            model.SelectTab("threads"); model.SetThreadView("graph");
            window.Width = 340; await Paint(window);
            Check(view.Bounds.Width == 340 && canvas.Children.OfType<Border>().All(node => node.Bounds.Width == 260) &&
                view.FindControl<Border>("DelegationDrawer") is { IsVisible: false } &&
                view.FindControl<Grid>("WorkbenchHeader") is { } narrowHeader && narrowHeader.Bounds.Width <= 340,
                "窄于 1000DIP 时单列布局不横向溢出，依赖图节点仍在自己的滚动视口内保持 260 宽");
            model.SelectTab("overview"); await Paint(window);
            using (var image = window.CaptureRenderedFrame()) image?.Save(Path.Combine(root, "native-narrow.png"), PngBitmapEncoderOptions.Default);
            window.Width = 1100; await Paint(window);
            using (var image = window.CaptureRenderedFrame()) image?.Save(Path.Combine(root, "v4-overview-1100.png"), PngBitmapEncoderOptions.Default);
            var path = model.DatabasePath; await model.ConnectAsync(Path.Combine(root, "missing.db")); Check(model.Connected && model.DatabasePath == path && !File.Exists(Path.Combine(root, "missing.db")), "failed reconnect preserves the previous authority without creating a blank database");
            var switched = Path.Combine(root, "switched.db");
            using (var source = new SqliteConnection("Data Source=" + db)) using (var copy = new SqliteConnection("Data Source=" + switched))
            { source.Open(); copy.Open(); source.BackupDatabase(copy); }
            using (var readLock = new SqliteConnection("Data Source=" + db))
            {
                readLock.Open(); using var query = readLock.CreateCommand(); query.CommandText = "BEGIN EXCLUSIVE"; query.ExecuteNonQuery();
                var observing = model.RefreshAsync(); await Task.Delay(30);
                Check(!model.IsBusy && !observing.IsCompleted, "exclusive SQLite observation leaves input actions available while the read is pending");
                var changing = model.ConnectAsync(db); await Task.Delay(30);
                Check(model.IsBusy && !changing.IsCompleted, "explicit connection change waits for the previous observation to settle");
                model.SetProjectContext("switch-during-observation"); await model.ConnectAsync(switched);
                query.CommandText = "ROLLBACK"; query.ExecuteNonQuery(); await Task.WhenAll(observing, changing);
                Check(model.Connected && model.DatabasePath == switched && model.ProjectContext == "switch-during-observation",
                    "settling an old observation connects only the latest queued project authority");
            }
            using (var locked = new SqliteConnection("Data Source=" + db))
            {
                locked.Open(); using var q = locked.CreateCommand(); q.CommandText = "BEGIN EXCLUSIVE"; q.ExecuteNonQuery();
                var connecting = model.ConnectAsync(db); await Task.Delay(30);
                Check(model.IsBusy, "an actual exclusive SQLite lock holds the previous connection read in flight");
                model.SetProjectContext("switch-during-read"); await model.ConnectAsync(switched);
                q.CommandText = "ROLLBACK"; q.ExecuteNonQuery(); await connecting;
                Check(model.Connected && model.DatabasePath == switched && model.ProjectContext == "switch-during-read", "project switch during an in-flight read connects only the latest requested authority");
            }
            model.SetProjectContext("other-project"); Check(!model.Connected && model.Snapshot is null && model.Inbox.Count == 0, "project switch clears old authority and decisions");
            await model.ConnectAsync(db); window.Content = null; window.Content = view; await Paint(window); Check(model.Connected, "temporary reparenting keeps the same client without disposing it");
            view.Detach(); Check(!model.RefreshCommand.CanExecute(null), "module release disables further reads and decisions");
        }
        finally { window.Close(); view.Detach(); }
        var shell = new MainWindow(AppOptions.Mock); shell.Show();
        try
        {
            shell.OpenDockPage("automation", Fufucode.App.Infrastructure.DockZone.Right);
            Check(shell.PageAvailableForTesting("automation") && shell.PageForTesting("automation") is AutomationControlPageView, "shared workbench catalog exposes the real native page");
            var page = shell.PageForTesting("automation"); shell.FloatDockPage("automation");
            var floated = shell.DockLayoutForTesting.Floating.Single(f => f.Pages.Contains("automation"));
            Check(shell.FloatingDockWindowForTesting(floated.Id) is not null && ReferenceEquals(page, shell.PageForTesting("automation")), "floating the native tab retains its one client instance");
            shell.DockFloatingRegion(floated.Id); Check(ReferenceEquals(page, shell.PageForTesting("automation")), "returning the floating tab docks the same live page");
        }
        finally { shell.Close(); }
        await RunPersistenceAsync(root, Check);
        Console.WriteLine("C5 evidence: " + root);
    }
    private static Button FindButton(Control view, string id) => view.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Tag, id));
    private static void Click(Control view, string id) => FindButton(view, id).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static async Task Paint(Window window) { window.UpdateLayout(); await Task.Delay(30); await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render); }
    private static async Task PressDecision(Window window, AutomationControlPageView view, string label, Action? beforeInput = null)
    {
        await Paint(window);
        // v4 决定与对账入口在“活动记录”页签；测试必须先真实切到该页签再按键盘。
        ((AutomationControlViewModel)view.DataContext!).SelectTab("activity");
        await Paint(window);
        var button = view.FindControl<ItemsControl>("DecisionActions")!.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, label));
        button.BringIntoView(); await Paint(window);
        await Until(() => !((AutomationControlViewModel)view.DataContext!).IsBusy);
        window.Activate();
        if (!button.IsEnabled || !button.Focus()) throw new InvalidOperationException("Decision button unavailable: " + label);
        var model = (AutomationControlViewModel)view.DataContext!; var previous = model.DecideCommand.ExecutionTask;
        beforeInput?.Invoke();
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        try { await Until(() => model.DecideCommand.ExecutionTask != previous); }
        catch (TimeoutException error) { throw new InvalidOperationException($"Actual decision key did not start command: {label}; busy={model.IsBusy}; enabled={button.IsEnabled}; focused={button.IsFocused}; selected={model.SelectedDecision?.Item.Id}; status={model.StatusText}", error); }
        await model.DecideCommand.ExecutionTask!; await Paint(window);
    }
    private static async Task Until(Func<bool> ready) { var end = DateTime.UtcNow.AddSeconds(30); while (!ready()) { if (DateTime.UtcNow > end) throw new TimeoutException(); await Task.Delay(20); } }
    private static async Task<string> Git(string directory, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var p = Process.Start(start)!; var output = p.StandardOutput.ReadToEndAsync(); var error = p.StandardError.ReadToEndAsync(); await p.WaitForExitAsync();
        var text = await output; if (p.ExitCode != 0) throw new Exception(await error); return text;
    }
}
