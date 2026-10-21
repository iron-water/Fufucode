using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Fufucode.App.Infrastructure;
using Fufucode.App.Integration;
using Fufucode.App.ViewModels;
using Fufucode.Core;

namespace Fufucode.App;

public partial class MainWindow
{
    private PersistentSession? _sideSession;
    private readonly Views.ChatPage _sideChatPage = new();
    private ExtensionUiDialogHost? _sideDialogHost;
    private IDisposable? _sideExtensionUiWiring;
    private string? _sideProject;
    private Button _sideStart = null!;
    private TextBlock _sideStatus = null!;
    private StackPanel _sideEmpty = null!;
    private bool _sideStarting;
    private Mock.MockChatSession? _sideMock;
    private readonly Border _sideChatHost = new();
    private Border? _sideChatDockSlot;
    private Control CreateSideChatHost()
    {
        _sideChatPage.DataContext = null;
        _sideStart = new Button { Content = "新建侧边聊天", HorizontalAlignment = HorizontalAlignment.Left };
        _sideStatus = new TextBlock { Text = "尚无侧边会话", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        _sideEmpty = new StackPanel { Margin = new Avalonia.Thickness(12), Spacing = 12, Children = { _sideStatus, _sideStart } };
        _sideChatPage.IsVisible = false;
        _sideStart.Click += async (_, _) =>
        {
            try { await StartSideChatAsync(); }
            catch (Exception error) { _sideStatus.Text = "启动失败：" + error.Message; }
        };
        _sideChatHost.Child = new Grid { Children = { _sideChatPage, _sideEmpty } };
        // 通用功能页只承载同一业务控件；移栏、隐藏和浮动不另建会话。
        _sideChatDockSlot = new Border { Child = _sideChatHost };
        return _sideChatDockSlot;
    }
    private async Task StartSideChatAsync()
    {
        if (_sideStarting) return;
        if (_options.Mode == AppSessionMode.Real && _host is null) throw new InvalidOperationException("主会话尚未就绪");
        if ((_sideChatPage.DataContext as MainViewModel)?.Input.IsTurnActive == true) throw new InvalidOperationException("请先停止或等待侧边聊天结束");
        _sideStarting = true; _sideStart.IsEnabled = false;
        try
        {
            await CloseSideChatAsync();
            _sideStatus.Text = "正在创建侧边会话…";
            if (_host is null)
            {
                _sideMock = new Mock.MockChatSession();
                var vm = new MainViewModel(_sideMock, new AvaloniaClipboardService(), new DispatcherRefreshScheduler(TimeSpan.FromMilliseconds(33)));
                vm.Input.Steering = _sideMock;
                vm.Extensions.AttachMock();
                _sideChatPage.DataContext = vm;
                await _sideMock.ConnectAsync();
                _sideChatPage.ApplyAppearance(_customization.Current);
            }
            else
            {
                var host = _host;
                var session = await host.Manager.CreateAsync(host.ProjectPath, "侧边聊天");
                if (_closed || !ReferenceEquals(host, _host)) { await session.DisposeAsync(); return; }
                _sideSession = session;
                AttachSideSession(session);
            }
            _sideEmpty.IsVisible = false; _sideChatPage.IsVisible = true;
            _sideChatPage.AttachWorkbenchActions(_actions);
            var menu = new MenuFlyout();
            var create = new MenuItem { Header = "新建侧边聊天" };
            create.Click += async (_, _) =>
            {
                try { await StartSideChatAsync(); }
                catch (Exception error) { _sideChatPage.ReportActionError(error.Message); }
            };
            menu.Items.Add(create);
            _sideChatPage.SetHeaderMenu(menu);
            OpenDockPage("sidechat", DockZone.Right);
        }
        finally { _sideStarting = false; _sideStart.IsEnabled = true; }
    }
    private void AttachSideSession(PersistentSession session)
    {
        _sideDialogHost?.Detach();
        _sideProject = session.Project.Path;
        MainViewModel vm = null!;
        var callbacks = new SidebarViewModel.Callbacks(_ => Task.CompletedTask, StartSideChatAsync, async name =>
        {
            await session.RenameAsync(name);
            vm.Sidebar.UpdateSessions([new SessionItemViewModel(name, "侧边会话", session.Session.Id, true)], session.Session.Id, name);
        }, session.Project.Path, session.Session.Name ?? "侧边聊天", session.Session.Id);
        vm = new MainViewModel(session.Chat, new AvaloniaClipboardService(), new DispatcherRefreshScheduler(TimeSpan.FromMilliseconds(33)),
            [new SessionItemViewModel(session.Session.Name ?? "侧边聊天", "侧边会话", session.Session.Id, true)], callbacks);
        vm.Input.Steering = new RealPromptSteering(session.Coordinator);
        vm.Settings.Customization = _customization;
        if (_vm?.Settings.AttachedContext is { } context)
            vm.Settings.Attach(context with
            {
                CurrentModel = () => session.Chat.CurrentModel,
                QueryModels = ct => session.Coordinator.QueryAvailableModelsAsync(ct),
                IsTurnActive = () => vm.Input.IsTurnActive,
                SetModel = (provider, model, ct) => session.Coordinator.SetModelAsync(provider, model, ct),
                SetThinkingLevel = (level, ct) => session.Coordinator.SetThinkingLevelAsync(level, ct),
                QueryThinkingLevels = ct => session.Coordinator.QueryThinkingLevelsAsync(ct),
                QueryState = ct => session.Coordinator.QueryStateAsync(ct),
                RestartSession = null,
                ModelApplied = model => vm.StatusBar.SetModelOverride(model)
            });
        _sideChatPage.DataContext = vm;
        _sideEmpty.IsVisible = false; _sideChatPage.IsVisible = true;
        vm.Input.IdentityContext = _vm?.Input.IdentityContext ?? "";
        vm.Input.DraftText = _host?.Manager.Metadata.GetDraft(session.Session.Id) ?? "";
        vm.Timeline.LoadHistory(HistoryMapper.MapTranscript(session.History.CurrentBranchMessages, session.Chat.CurrentModel));
        _sideChatPage.ApplyAppearance(_customization.Current);
        if (_host is { } host)
            vm.Extensions.AttachRealSession(new ExtensionCatalog(session.Coordinator, session.Project.Path, host.AgentDirectory), session.Chat,
                session.Project.Path, host.AgentDirectory, _ => StartSideChatAsync(), AskTrustAsync,
                (status, decision) => RealSessionHost.SaveTrustDecision(_options, status, decision), () => host.TrustStatus,
                command => { vm.Input.ApplyExternalText(command.Invocation); _sideChatPage.Input.Focus(); });
        _sideDialogHost = new ExtensionUiDialogHost(this);
        _sideDialogHost.Attach(session.Chat);
        _sideExtensionUiWiring = SessionUiWiring.AttachExtensionUi(session.Chat, vm.Timeline, vm.Input, vm.StatusBar, _ => { });
    }

    private async Task CloseSideChatAsync()
    {
        _sideDialogHost?.Detach(); _sideDialogHost = null;
        _sideExtensionUiWiring?.Dispose(); _sideExtensionUiWiring = null;
        if (_sideChatPage.DataContext is MainViewModel vm)
        {
            if (_sideSession is { } current && _host is { } host) host.Manager.Metadata.SetDraft(current.Session.Id, vm.Input.DraftText);
            // 侧边聊天换会话/关闭：解除旧时间线的会话订阅与工具组订阅，避免旧会话继续驱动。
            vm.Timeline.Dispose();
            vm.WorkspacePanel.Dispose(); vm.Sidebar.Dispose();
        }
        _sideChatPage.DataContext = null;
        _sideChatPage.IsVisible = false;
        _sideEmpty.IsVisible = true; _sideStatus.Text = "尚无侧边会话";
        if (_sideMock is { } mock) { _sideMock = null; await mock.StopAsync(); }
        if (_sideSession is { } session) { _sideSession = null; await session.DisposeAsync(); }
        _sideProject = null;
    }
}
