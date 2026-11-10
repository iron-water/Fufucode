using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fufucode.App.Abstractions;
using Fufucode.App.Integration;
using Fufucode.App.ViewModels;
using Fufucode.App.Views;
using Fufucode.Core;
using Fufucode.Core.Backend;

namespace Fufucode.App.SelfTest;

/// <summary>真实窗口/宿主/原生 SSE 接线；只用本机确定性 HTTP 夹具，不调用外部模型。</summary>
internal static class SessionRepairSelfTest
{
    internal static async Task RunAsync(Action<string> pass, Action<string> fail)
    {
        void Check(bool condition, string name)
        {
            if (condition) pass("Session repair: " + name);
            else { fail("Session repair: " + name); throw new InvalidOperationException(name); }
        }
        await using var server = new LocalModelServer();
        var root = Path.GetFullPath(Path.Combine("tmp", "session-repair-" + Guid.NewGuid().ToString("N")));
        var project = Path.Combine(root, "project");
        Directory.CreateDirectory(project);
        var layout = new AppDataLayout(Path.Combine(root, "data"));
        layout.EnsureCreated();
        var settings = new AppSettingsStore(layout);
        settings.Update(s => s with
        {
            AutoCheckUpdates = false,
            SelectedModel = "test/model",
            Endpoint = new CompatibleEndpoint("test", server.BaseUrl, "test/model", "FUFUCODE_OFFLINE_TEST_KEY", ["stale/saved-model"])
        });
        var manager = new PersistentSessionManager("", "", layout);
        manager.SetProjectTrustDecision(project, ProjectTrustDecision.DoNotTrust);
        var options = new AppOptions(AppSessionMode.Real, project, null, null, null, layout.Root, BackendId.NativeCSharp)
            { NewSession = true };
        var one = new MainWindow(options);
        MainWindow? two = null;
        one.Show();
        try
        {
            await Until(() => one.CurrentSessionId is not null, "first window opens");
            var originalId = one.CurrentSessionId!;
            var originalHost = Host(one);
            var firstVm = (MainViewModel)one.DataContext!;
            firstVm.Input.DraftText = "initial question";
            await firstVm.Input.SendCommand.ExecuteAsync(null);
            await server.FirstChunk.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Until(() => AssistantText((MainViewModel)one.DataContext!).Contains("first-part"), "stream appears");
            Check(originalHost.Chat.Activity == SessionActivity.Running, "real native SSE turn is running");

            // 使用用户实际点击“新建窗口”的动作入口，不手工拼一个旁路窗口。
            var lifetime = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
            var before = lifetime.Windows.ToArray();
            var actions = (Infrastructure.AppActionRegistry)typeof(MainWindow)
                .GetField("_actions", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(one)!;
            var leftMenu = one.GetVisualDescendants().OfType<Button>()
                .FirstOrDefault(b => Avalonia.Automation.AutomationProperties.GetName(b) == "左辅助功能")?.Flyout as MenuFlyout;
            Check(leftMenu?.Items.OfType<MenuItem>().Any(item => Equals(item.Header, "新建窗口") &&
                ReferenceEquals(item.Command, actions.GetCommand("window.new"))) == true,
                "New Window is directly reachable in the left auxiliary dropdown");
            Check((await actions.ExecuteAsync("window.new")).Executed, "left auxiliary menu New Window action opens without a picker");
            two = lifetime.Windows.OfType<MainWindow>().First(w => !before.Contains(w));
            await Until(() => two.CurrentSessionId == originalId, "second window opens current session");
            Check(ReferenceEquals(originalHost.Current, Host(two).Current), "two independent hosts share exactly one persistent session");
            Check(!ReferenceEquals(originalHost, Host(two)), "view selection is owned by each window, not by shared mutable host");
            await Until(() => AssistantText((MainViewModel)two.DataContext!).Contains("first-part"), "second window receives in-flight prefix");
            Check(AssistantText((MainViewModel)two.DataContext!).Split("first-part").Length == 2,
                "snapshot plus queued deltas never duplicates the streaming prefix");

            var secondVm = (MainViewModel)two.DataContext!;
            firstVm.Timeline.SetAutoScrollAtBottom(false);
            secondVm.Timeline.SetAutoScrollAtBottom(true);
            Check(!firstVm.Timeline.IsAtBottom && secondVm.Timeline.IsAtBottom, "viewports scroll independently while sharing session facts");
            one.RenderOverview();
            var runningOrbit = one.GetVisualDescendants().OfType<SessionOrbit>().FirstOrDefault();
            Check(runningOrbit?.IsAnimationRunning == true, "running session starts its border orbit clock");
            secondVm.Sidebar.BeginRenameCommand.Execute(null);
            secondVm.Sidebar.RenameDraft = "renamed while streaming";
            Check(secondVm.Sidebar.SaveNameCommand.CanExecute(null), "previously unnamed session can be named during a turn");
            await secondVm.Sidebar.SaveNameCommand.ExecuteAsync(null);
            await Until(() => ((MainViewModel)one.DataContext!).Sidebar.CurrentName == "renamed while streaming", "rename broadcasts");
            Check(originalHost.Current.Session.Name == "renamed while streaming", "rename updates the shared session and persistent metadata");

            // 两个窗口都可以独立新建/切换，而原回合不被取消。
            await Invoke(two, "SwitchSessionAsync", null, null);
            var otherId = two.CurrentSessionId!;
            Check(otherId != originalId && one.CurrentSessionId == originalId && originalHost.Chat.Activity == SessionActivity.Running,
                "new session in window two does not change or stop window one");
            await two.SwitchSessionForOverviewTestingAsync(originalId);
            Check(two.CurrentSessionId == originalId && ReferenceEquals(Host(two).Current, originalHost.Current),
                "switch back to active session reuses the same writer and lease");
            await Until(() => AssistantText((MainViewModel)two.DataContext!).Contains("first-part"), "switch back restores current stream");
            await Invoke(one, "SwitchSessionAsync", otherId, null);
            Check(one.CurrentSessionId == otherId && two.CurrentSessionId == originalId,
                "first window can independently switch while original turn runs");
            Check(originalHost.Manager.Metadata.GetSession(originalId).Name == "renamed while streaming", "rename survives metadata reread");

            secondVm = (MainViewModel)two.DataContext!;
            secondVm.Input.DraftText = "steer during final text";
            await secondVm.Input.SteerCommand.ExecuteAsync(null);
            await Until(() => secondVm.Input.QueuedPreview.Any(q => q.IsSteer), "pending steer feedback");
            Check(((NativeCSharpChatSession)Host(two).Chat).PendingQueue.Steering?.Single() == true,
                "backend queue explicitly reports steering delivery mode");
            server.AllowFinal.TrySetResult();
            await Until(() => Host(two).Chat.Activity == SessionActivity.Idle && server.ChatCalls >= 2, "steer continuation completes");
            await Until(() => secondVm.Timeline.Entries.OfType<UserMessageViewModel>().Any(m => m.Text.Contains("steer during final text")),
                "steering appears in real-time timeline");
            Check(secondVm.Input.QueuedTexts.Count == 0, "consumed steering leaves no phantom pending entry");
            var file = NativeSessionFactory.SessionFilePath(layout, Host(two).Current.Project.Id, originalId);
            Check(File.ReadAllText(file).Contains("steer during final text"), "consumed steering is durably stored");
            var response = await Host(two).Current.Coordinator.QueryEntriesAsync();
            Check(response.Success && response.Data!.Value.GetRawText().Contains("steer during final text"), "history replay includes steering");

            // 已保存清单不能遮住目录；同一连接两次刷新看到提供商增删。
            var models = await Host(two).Current.Coordinator.QueryAvailableModelsAsync();
            Check(models.Success && models.Data!.Value.GetRawText().Contains("krill/new-gpt") &&
                  !models.Data.Value.GetRawText().Contains("stale/saved-model"), "live catalog overrides saved snapshot and exposes provider-qualified ids");
            server.CatalogVersion = 2;
            models = await Host(two).Current.Coordinator.QueryAvailableModelsAsync();
            Check(models.Success && models.Data!.Value.GetRawText().Contains("krill/replacement") &&
                  !models.Data.Value.GetRawText().Contains("krill/new-gpt"), "next refresh reflects upstream additions and removals");

            // 折叠只隐藏超出五条的内容。
            var mock = new Mock.MockChatSession();
            using var input = new InputViewModel(mock);
            await mock.ConnectAsync();
            mock.SimulateRunningQueue(Enumerable.Range(1, 7).Select(i => "queued-" + i).ToArray());
            await Task.Delay(50);
            Check(input.HasQueueOverflow && input.VisibleQueuedPreview.Count() == 5, "collapsed queue retains five visible rows");
            input.ToggleQueueCommand.Execute(null);
            Check(input.VisibleQueuedPreview.Count() == 7, "overflow expands without dropping any entries");
            await mock.StopAsync();

            // 关闭一个视图不释放另一个窗口持有的真实会话。
            var shared = Host(two).Current;
            one.Close();
            await Until(() => !one.IsVisible, "first window closes");
            Check(shared.Chat.Connection == SessionConnection.Connected && two.CurrentSessionId == originalId,
                "closing one window leaves the other editable and connected");
            secondVm.Input.DraftText = "message after other window closed";
            await secondVm.Input.SendCommand.ExecuteAsync(null);
            await Until(() => shared.Chat.Activity == SessionActivity.Idle && server.ChatCalls >= 3, "remaining window sends");
            Check(shared.Chat.LastOutcome == SessionOutcome.Completed, "remaining window can finish another turn");
            two.RenderOverview();
            var orbit = two.GetVisualDescendants().OfType<SessionOrbit>().FirstOrDefault();
            Check(orbit is not null, "completed session has an emphasis orbit in the actual sidebar tree");
        }
        finally
        {
            server.AllowFinal.TrySetResult();
            one.Close();
            two?.Close();
            await Task.Delay(100);
        }
    }

    private static RealSessionHost Host(MainWindow window) => (RealSessionHost)typeof(MainWindow)
        .GetField("_host", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
    private static string AssistantText(MainViewModel vm) => string.Join("\n", vm.Timeline.Entries.OfType<AssistantMessageViewModel>()
        .SelectMany(m => m.Blocks).OfType<TextBlockViewModel>().Select(b => b.Text));
    private static async Task Invoke(MainWindow window, string name, params object?[] arguments)
    {
        try { await (Task)typeof(MainWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, arguments)!; }
        catch (TargetInvocationException e) when (e.InnerException is not null) { throw e.InnerException; }
    }
    private static async Task Until(Func<bool> condition, string name)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!condition())
        {
            try { await Task.Delay(20, timeout.Token); }
            catch (OperationCanceledException) { throw new TimeoutException(name); }
        }
        await Task.Delay(30);
    }

    private sealed class LocalModelServer : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentBag<Task> _handlers = new();
        private readonly Task _loop;
        private int _chatCalls;
        internal int ChatCalls => Volatile.Read(ref _chatCalls);
        internal int CatalogVersion { get; set; } = 1;
        internal TaskCompletionSource FirstChunk { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource AllowFinal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal string BaseUrl { get; }
        internal LocalModelServer()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            BaseUrl = $"http://127.0.0.1:{port}/v1";
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _loop = Task.Run(async () =>
            {
                try
                {
                    while (!_stop.IsCancellationRequested)
                    {
                        var context = await _listener.GetContextAsync().WaitAsync(_stop.Token);
                        _handlers.Add(Respond(context));
                    }
                }
                catch (Exception e) when (e is OperationCanceledException or HttpListenerException or ObjectDisposedException) { }
            });
        }
        private async Task Respond(HttpListenerContext context)
        {
            try
            {
                if (context.Request.Url!.AbsolutePath.EndsWith("/models"))
                {
                    var id = CatalogVersion == 1 ? "krill/new-gpt" : "krill/replacement";
                    var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { data = new[] { new { id = "test/model" }, new { id } } }));
                    context.Response.ContentType = "application/json";
                    await context.Response.OutputStream.WriteAsync(bytes);
                    return;
                }
                Interlocked.Increment(ref _chatCalls);
                using var reader = new StreamReader(context.Request.InputStream);
                await reader.ReadToEndAsync();
                context.Response.ContentType = "text/event-stream";
                context.Response.SendChunked = true;
                async Task Chunk(string text)
                {
                    var payload = JsonSerializer.Serialize(new { choices = new[] { new { index = 0, delta = new { content = text }, finish_reason = (string?)null } } });
                    await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("data: " + payload + "\n\n"));
                    await context.Response.OutputStream.FlushAsync();
                }
                await Chunk("first-part");
                FirstChunk.TrySetResult();
                await AllowFinal.Task.WaitAsync(_stop.Token);
                await Chunk(" second-part");
                await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("data: [DONE]\n\n"));
            }
            catch (Exception e) when (e is OperationCanceledException or HttpListenerException or IOException or ObjectDisposedException) { }
            finally { context.Response.Close(); }
        }
        public async ValueTask DisposeAsync()
        {
            AllowFinal.TrySetResult();
            _stop.Cancel();
            _listener.Close();
            await _loop;
            await Task.WhenAll(_handlers).WaitAsync(TimeSpan.FromSeconds(5));
            _stop.Dispose();
        }
    }
}
