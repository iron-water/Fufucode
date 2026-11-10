using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Text.Json;
using Fufucode.App.Abstractions;
using Fufucode.Core.Backend;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Fufucode.App.Abstractions.Graph;
using Fufucode.App.Features.Graph;
using Fufucode.App.Features.SettingsExperience;
using Fufucode.App.Infrastructure;
using Fufucode.App.Integration;
using Fufucode.App.ViewModels;
using Fufucode.Core;
using Fufucode.Core.Plugins;
using Fufucode.Plugins.AutoDev.Graph;

namespace Fufucode.App;

public partial class MainWindow
{
    private sealed record GraphWorkspace(IGraphProjectService Service, GraphWorkspaceViewModel Model,
        Control Main, IReadOnlyDictionary<string, Control> Panels);
    private readonly ConcurrentDictionary<string, GraphWorkspace> _graphProjects = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Border> _graphPanelSlots = new(StringComparer.Ordinal);
    private readonly Border _graphMainHost = new();
    private GraphWorkspace? _graphWorkspace;
    private PersistentSessionManager? _projectOnlyManager;
    private AppPage _lastWorkMode = AppPage.Chat;
    private string? _modeLayoutProject;
    private readonly Dictionary<string, string> _modeLayouts = new(StringComparer.Ordinal);
    private bool GraphEnabled => EffectiveSettingsStore.Current.GraphEnabled;
    internal IGraphProjectService? GraphServiceForTesting => _graphWorkspace?.Service;
    internal GraphWorkspaceViewModel? GraphModelForTesting => _graphWorkspace?.Model;
    internal Task OpenGraphChatForTestingAsync(string? module, string? ticket, string context) => OpenGraphChatAsync(module, ticket, context);
    internal void OpenGraphPanelForTesting(string id) => OpenDockPage(id, DockZone.Right);
    internal Task OpenGraphSideChatForTestingAsync() => StartGraphSideChatAsync();
    internal Task PromoteGraphSideChatForTestingAsync() => PromoteSideChatAsync();
    internal string? GraphChatIdForTesting => _host?.Current.Session.Id;
    internal string? GraphSideChatIdForTesting => _sideSession?.Session.Id;
    internal MainViewModel? GraphSideChatModelForTesting => _sideChatPage.DataContext as MainViewModel;
    private AppDataLayout GraphLayout => Program.SelfTestMode && Program.SelfTestDataRoot is { } root
        ? new AppDataLayout(root) : _options.CreateAppDataLayout();

    private readonly Dictionary<string, (Fufucode.App.Abstractions.IChatSession Session, EventHandler<SessionEvent> Handler)> _graphChatSubscriptions = new(StringComparer.Ordinal);
    private sealed record GraphConversationLink(string ProjectId, string? ModuleId, string? TicketId);
    private IReadOnlyList<IPureToolDispatcher> GraphSessionTools(string path, string sessionId)
    {
        if (_closed || !GraphEnabled) return [];
        var metadata = new DesktopMetadataStore(GraphLayout);
        var json = metadata.GetPreference("graph-session-link:" + sessionId);
        if (string.IsNullOrWhiteSpace(json)) return [];
        GraphConversationLink? link;
        try { link = JsonSerializer.Deserialize<GraphConversationLink>(json); }
        catch (JsonException) { return []; }
        if (link is null || !_graphProjects.TryGetValue(link.ProjectId, out var workspace) || !workspace.Service.ReadOptions().Enabled) return [];
        if (!RealSessionHost.PathEquals(workspace.Service.ReadSnapshot().ProjectPath, path)) return [];
        return [new GraphApplicationToolDispatcher(new GraphToolHandler(workspace.Service, path))];
    }

    private void DetachGraphConversation(string sessionId)
    {
        if (_graphChatSubscriptions.Remove(sessionId, out var subscription))
            subscription.Session.Event -= subscription.Handler;
    }

    private void AttachGraphConversation(PersistentSession session, bool main = true)
    {
        if (main)
            foreach (var id in _graphChatSubscriptions.Keys.Where(id => id != _sideSession?.Session.Id).ToArray())
                DetachGraphConversation(id);
        DetachGraphConversation(session.Session.Id);
        var json = new DesktopMetadataStore(GraphLayout).GetPreference("graph-session-link:" + session.Session.Id);
        if (string.IsNullOrWhiteSpace(json)) return;
        GraphConversationLink? link;
        try { link = JsonSerializer.Deserialize<GraphConversationLink>(json); }
        catch (JsonException) { return; }
        if (link is null || !_graphProjects.TryGetValue(link.ProjectId, out var workspace)) return;
        EventHandler<SessionEvent> handler = (_, value) =>
        {
            if (value is not SessionEvent.MessageFinished message || message.FinalMessage.Role is not (MessageRole.User or MessageRole.Assistant)) return;
            var text = string.Join(Environment.NewLine, message.FinalMessage.Blocks
                .OfType<Fufucode.App.Abstractions.TextBlock>().Select(block => block.Text));
            if (string.IsNullOrWhiteSpace(text)) return;
            try
            {
                ((GraphProjectService)workspace.Service).RecordSessionMessage(link.TicketId, link.ModuleId,
                    session.Session.Id, message.MessageId,
                    message.FinalMessage.Role == MessageRole.User ? "user" : "assistant", text);
            }
            catch (Exception error) { System.Diagnostics.Trace.TraceWarning("Graph correspondence: " + error.Message); }
        };
        session.Chat.Event += handler;
        _graphChatSubscriptions[session.Session.Id] = (session.Chat, handler);
    }

    private void ApplyWorkModeLayout(AppPage page)
    {
        if (page is not (AppPage.Chat or AppPage.Graph)) return;
        var project = _graphEntryPending ? null : _graphWorkspace?.Service.ReadSnapshot().ProjectId ?? _host?.Current.Project.Id;
        if (project is null)
        {
            _lastWorkMode = page;
            RenderDockPages();
            RefreshWorkModeSurfaces();
            return;
        }
        var metadata = new DesktopMetadataStore(GraphLayout);
        if (_modeLayoutProject == project && _lastWorkMode == page) return;
        SaveWorkModeLayout();
        var key = "workspace-mode-layout:" + project + ":" + page;
        var saved = _modeLayouts.GetValueOrDefault(key) ?? metadata?.GetPreference(key);
        _dockLayout = WorkbenchLayoutState.Restore(saved ?? _dockLayout.Serialize(), _workbenchPages.KnownPageIds);
        if (saved is null)
        {
            if (page == AppPage.Graph && GraphEnabled)
            {
                _dockLayout.Open("overview", DockZone.Left);
                _dockLayout.Open("graph-properties", DockZone.Right);
                _dockLayout.Open("graph-history", DockZone.Bottom);
            }
            else
                foreach (var id in _graphPanelSlots.Keys) _dockLayout.Close(id);
        }
        _modeLayoutProject = project;
        _lastWorkMode = page;
        RenderDockPages();
        RefreshWorkModeSurfaces();
    }

    private void SaveWorkModeLayout()
    {
        if (_modeLayoutProject is null || _graphEntryPending) return;
        var key = "workspace-mode-layout:" + _modeLayoutProject + ":" + _lastWorkMode;
        _modeLayouts[key] = _dockLayout.Serialize();
        new DesktopMetadataStore(GraphLayout).SetPreference(key, _modeLayouts[key]);
    }

    private void RegisterGraphActions()
    {
        Register("workspace.mode", "切换 Chat / Graph", "工作模式", "F6", "切换主内容视图",
            () => NavigateWorkspaceAsync(WorkMode == AppPage.Graph ? AppPage.Chat : AppPage.Graph),
            () => !GraphEnabled ? "Graph 模式已停用" : _vm is null ? "请先打开项目" : null,
            hideWhenUnavailable: true);
        Register("graph", "Graph 项目地图", "工作模式", "", "打开项目地图",
            () => NavigateWorkspaceAsync(AppPage.Graph), () => GraphEnabled ? null : "Graph 模式已停用", true);
        foreach (var (actionId, pageId, title) in new[]
        {
            ("graph.properties", "graph-properties", "模块属性"),
            ("graph.tickets", "graph-tickets", "邮件"),
            ("graph.history", "graph-history", "结构历史"),
            ("graph.minimap", "graph-minimap", "极简 Graph")
        })
            Register(actionId, title, "Graph", "", title, () =>
            {
                OpenDockPage(pageId, pageId == "graph-history" ? DockZone.Bottom : DockZone.Right);
                return Task.CompletedTask;
            }, () => GraphEnabled && _graphWorkspace is not null ? null : "Graph 模式未启用", true);
        Register("graph.resume-lead", "核查后恢复项目负责人", "Graph", "", "在核查未知影响后建立新的负责人复核回合", async () =>
        {
            var service = _graphWorkspace?.Service as GraphProjectService ?? throw new InvalidOperationException("项目未打开。");
            if (await ApproveGraphDevelopmentAsync("恢复项目负责人", "请先核查上次负责人运行的未知副作用。此操作建立新的复核回合，不重放原未知运行。"))
                service.ResumeLeadCycle(service.ReadSnapshot().ProjectPath);
        }, () => GraphEnabled && _graphWorkspace is not null ? null : "Graph 模式未启用", true);
        Register("graph.sidechat", "项目侧聊", "Graph", "", "打开侧聊",
            StartGraphSideChatAsync,
            () => GraphEnabled ? null : "Graph 模式已停用", true);
    }

    private void EnsureGraphProject(string path)
    {
        var full = Path.GetFullPath(path);
        var metadata = new DesktopMetadataStore(GraphLayout);
        var project = metadata.GetOrCreateProject(full);
        if (!_graphProjects.TryGetValue(project.Id, out var workspace))
        {
            GraphProjectService? service = null;
            GraphDevelopmentHost? developmentHost = null;
            var executor = new GraphNativeExecutor(GraphLayout,
                request => service is null ? null : new GraphToolHandler(service, request.ProjectPath, request.Identity),
                async session => await Dispatcher.UIThread.InvokeAsync<IDisposable?>(() =>
                {
                    var dialogs = new ExtensionUiDialogHost(this);
                    dialogs.Attach(session.Chat);
                    return new GraphApprovalLease(dialogs);
                }), sessionInUse: id => _sideSession?.Session.Id == id,
                authorize: request => service?.IsRunAuthorized(request.ProjectPath, request.TicketId, request.RunId) == true,
                resolveRepair: request => developmentHost?.GetIntegrationRepairBindingAsync(request.TicketId) ?? Task.FromResult<GraphIntegrationRepairBinding?>(null));
            service = new GraphProjectService(GraphLayout.Root, executor);
            service.SetProject(project.Id, full);
            if (service.ReadOptions().Enabled != GraphEnabled)
                service.SaveOptionsAsync(service.ReadOptions() with { Enabled = GraphEnabled }).GetAwaiter().GetResult();
            var model = new GraphWorkspaceViewModel(service);
            developmentHost = InitializeGraphDevelopment(service, model);
            var panels = new Dictionary<string, Control>(StringComparer.Ordinal)
            {
                ["graph-properties"] = new GraphPropertiesView(model),
                ["graph-tickets"] = new GraphTicketsView(model),
                ["graph-history"] = new GraphHistoryView(model),
                ["graph-minimap"] = new GraphMiniMapView(model)
            };
            workspace = new(service, model, new GraphPageView(model), panels);
            _graphProjects.TryAdd(project.Id, workspace);
            model.RevealPanelRequested += page => Dispatcher.UIThread.Post(() =>
            {
                if (!ReferenceEquals(_graphWorkspace?.Model, model)) return;
                if (page == "graph") { _ = NavigateWorkspaceAsync(AppPage.Graph); return; }
                OpenDockPage(page, page == "graph-history" ? DockZone.Bottom : DockZone.Right);
            });
            model.OpenChatRequested += async (module, ticket, context) =>
            {
                try { await OpenGraphChatAsync(module, ticket, context); }
                catch (Exception error) { _vm?.Timeline.AddNotification("打开关联会话失败：" + error.Message); }
            };
            service.Changed += () => Dispatcher.UIThread.Post(() =>
            {
                if (ReferenceEquals(_graphWorkspace?.Service, service)) RefreshIsland();
            });
        }
        metadata.SetPreference("workspace-graph:" + project.Id, "created");
        _graphWorkspace = workspace;
        _graphMainHost.Child = workspace.Main;
        RegisterGraphPanels();
        foreach (var pair in workspace.Panels)
        {
            _graphPanelSlots[pair.Key].Child = pair.Value;
            _workbenchPages.SetEnabled(pair.Key, GraphEnabled, _ => { });
        }
        if (_vm is { } vm) AttachGraphSettings(vm);
    }

    private void RegisterGraphPanels()
    {
        if (_graphPanelSlots.Count != 0) return;
        foreach (var (id, title, glyph) in new[]
        {
            ("graph-properties", "属性", "Info"), ("graph-tickets", "邮件", "Document"),
            ("graph-history", "历史", "History"),
            ("graph-minimap", "Graph", "Branch")
        })
        {
            var slot = new Border();
            _graphPanelSlots[id] = slot;
            _workbenchPages.RegisterOptional(id, title, glyph, "autodev", () => slot, _ => { });
        }
    }

    private void AttachGraphSettings(MainViewModel vm)
    {
        if (_graphWorkspace is not { } workspace) return;
        var settings = new GraphSettingsView(workspace.Service, _host?.Manager.Settings ?? _projectOnlyManager?.Settings ?? EffectiveSettingsStore);
        settings.ModelsRequested += () => vm.Settings.SelectCategory("models");
        settings.Applied += async options =>
        {
            var store = _host?.Manager.Settings ?? _projectOnlyManager?.Settings ?? EffectiveSettingsStore;
            store.Update(current => current with { GraphEnabled = options.Enabled });
            await Task.WhenAll(_graphProjects.Values.Select(project =>
                project.Service.SaveOptionsAsync(ReferenceEquals(project, workspace) ? options :
                    project.Service.ReadOptions() with { Enabled = options.Enabled })));
            if (!options.Enabled)
            {
                SaveWorkModeLayout();
                _settingsReturnPage = AppPage.Chat;
                if (vm.IsGraphPage) await NavigateWorkspaceAsync(AppPage.Chat);
                foreach (var id in _graphPanelSlots.Keys)
                {
                    CloseDockPage(id);
                    _workbenchPages.SetEnabled(id, false, _ => { });
                }
            }
            else
                foreach (var id in _graphPanelSlots.Keys) _workbenchPages.SetEnabled(id, true, _ => { });
            _customization?.NotifyPagesChanged();
            _actions.RefreshCapabilities();
            if (_customization is not null) BuildActionSurfaces(_customization.Current);
            RenderDockPages();
        };
        vm.Settings.GraphSettingsContent = settings;
    }

    private async Task StartGraphSideChatAsync()
    {
        if (!GraphEnabled) throw new InvalidOperationException("Graph 模式已停用。");
        var workspace = _graphWorkspace ?? throw new InvalidOperationException("尚未打开项目。");
        var returnToGraph = _vm?.IsGraphPage == true;
        await EnsureChatHostAsync();
        if (_sideSession is null)
        {
            await StartSideChatAsync();
            if (_sideSession is { } session && ReferenceEquals(workspace, _graphWorkspace))
            {
                var module = workspace.Model.SelectedModuleId;
                var ticket = workspace.Model.SelectedTicketId;
                if (ticket is not null) workspace.Service.LinkSession(ticket, session.Session.Id);
                _host!.Manager.Metadata.SetPreference("graph-session-link:" + session.Session.Id,
                    JsonSerializer.Serialize(new GraphConversationLink(workspace.Model.Snapshot.ProjectId, module, ticket)));
                AttachGraphConversation(session, main: false);
                if (_sideChatPage.DataContext is MainViewModel side)
                    await AttachGraphContextAsync(side.Input, session.Session.Id, workspace.Model.Snapshot.Revision,
                        workspace.Service.BuildContext(module, ticket, workspace.Model.Snapshot.IsHistorical ? workspace.Model.Snapshot.Revision : null));
            }
        }
        if (returnToGraph) await NavigateWorkspaceAsync(AppPage.Graph);
        OpenDockPage("sidechat", DockZone.Right);
    }

    private async Task AttachGraphContextAsync(InputViewModel input, string sessionId, long revision, string context)
    {
        var directory = Path.Combine(GraphLayout.Root, "graph-context", sessionId);
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "项目地图-v" + revision + ".json");
        await File.WriteAllTextAsync(file, context);
        input.AddAttachment(new AttachedFile(file, context), insertReference: false);
    }

    private void RestoreGraphDraft(InputViewModel input, string sessionId)
    {
        if (new DesktopMetadataStore(GraphLayout).GetPreference("graph-session-link:" + sessionId) is null) return;
        var draft = AttachmentPresentation.Parse(input.DraftText);
        if (draft.Files.Count == 0) return;
        input.DraftText = draft.Body;
        foreach (var file in draft.Files) input.AddAttachment(file, insertReference: false);
    }

    private async Task EnsureChatHostAsync()
    {
        if (_options.Mode == AppSessionMode.Real && _host is null)
            await BootstrapRealAsync();
        if (_options.Mode == AppSessionMode.Real && _host is null)
            throw new InvalidOperationException("会话尚未打开，请检查启动错误。");
    }

    private async Task OpenGraphChatAsync(string? moduleId, string? ticketId, string context)
    {
        if (_closed || _switching || _restarting) return;
        if (!GraphEnabled) throw new InvalidOperationException("Graph 模式已停用。");
        if (_vm?.Input.IsTurnActive == true) throw new InvalidOperationException("当前聊天仍在运行，请先停止或等待结束。");
        if (!await ConfirmLeaveSettingsAsync()) return;
        var workspace = _graphWorkspace ?? throw new InvalidOperationException("尚未打开项目。");
        var snapshot = workspace.Service.ReadSnapshot();
        var metadata = new DesktopMetadataStore(GraphLayout);
        var ticket = snapshot.Tickets.FirstOrDefault(item => item.Id == ticketId);
        var projectDiscussion = ticketId is null && moduleId is null;
        var key = projectDiscussion ? "graph-project-lead-session:" + snapshot.ProjectId : "graph-module-session:" + snapshot.ProjectId + ":" + moduleId;
        var linked = ticketId is not null ? ticket?.SessionId : metadata.GetPreference(key);
        if (!string.IsNullOrEmpty(linked) && metadata.GetSession(linked).ProjectId != snapshot.ProjectId)
            throw new InvalidOperationException("关联会话与项目身份不一致。");
        var name = projectDiscussion ? "项目负责人" : ticket?.Title ?? snapshot.Modules.FirstOrDefault(item => item.Id == moduleId)?.Name ?? "项目讨论";
        _switching = true;
        try
        {
            if (_host is null)
            {
                // Explicitly create or resume the associated conversation, never rename a recent ordinary Chat.
                var previousOptions = _options;
                _options = _options with { SessionId = linked, NewSession = linked is null };
                try { await EnsureChatHostAsync(); }
                finally { _options = _options with { SessionId = previousOptions.SessionId, NewSession = previousOptions.NewSession }; }
                if (linked is null) await _host!.RenameCurrentAsync("Graph / " + name);
            }
            else
            {
                var host = _host;
                if (_vm is { } previous) host.SaveDraft(previous.Input.ComposePrompt());
                _draftDebounce?.Stop();
                var previousId = host.Current.Session.Id;
                await ProjectTrustFlow.RunAsync(async () =>
                {
                    if (linked is not null) await host.SwitchToAsync(linked, _bootstrapCts.Token);
                    else await host.NewSessionAsync("Graph / " + name, _bootstrapCts.Token);
                    return true;
                }, AskTrustAsync, (status, decision) => RealSessionHost.SaveTrustDecision(_options, status, decision));
                Integration.SessionWindowRegistry.TryUnregisterOwner(previousId, this);
                Integration.SessionWindowRegistry.RegisterOwner(host.Current.Session.Id, host, this);
            }
            if (_closed || !ReferenceEquals(workspace, _graphWorkspace)) return;
            var current = _host ?? throw new InvalidOperationException("关联会话未打开。");
            var id = current.Current.Session.Id;
            if (ticketId is not null) workspace.Service.LinkSession(ticketId, id);
            else metadata.SetPreference(key, id);
            metadata.SetPreference("graph-session-link:" + id,
                JsonSerializer.Serialize(new GraphConversationLink(snapshot.ProjectId, moduleId, ticketId)));
            AttachRealSession();
            SessionPresence.MarkEstablished(metadata, id);
            // A restored draft is the user's work; only a new conversation receives the initial context.
            if (linked is null && _vm is { } target)
                await AttachGraphContextAsync(target.Input, id, snapshot.Revision, context);
            await NavigateWorkspaceAsync(AppPage.Chat);
            OpenDockPage("graph-minimap", DockZone.Right);
            _chatPage.Input.Focus();
            RefreshSessionList();
        }
        finally { _switching = false; }
    }

    private async Task OpenProjectGraphAsync(string path)
    {
        if (!GraphEnabled) throw new InvalidOperationException("Graph 模式已停用。");
        var full = Path.GetFullPath(path);
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException(full);
        if (_vm?.Input.IsTurnActive == true) throw new InvalidOperationException("请先停止当前聊天。");
        if (!await ConfirmLeaveSettingsAsync()) return;
        SaveWorkModeLayout();
        var manager = new PersistentSessionManager(string.Empty, string.Empty, GraphLayout);
        manager.Metadata.GetOrCreateProject(full);
        EnsureGraphProject(full);
        _graphEntryPending = false;
        if (_host is { } old)
        {
            if (_vm is { } previous) old.SaveDraft(previous.Input.ComposePrompt());
            _draftDebounce?.Stop();
            await CloseSideChatAsync();
            Integration.SessionWindowRegistry.TryUnregisterOwner(old.Current.Session.Id, this);
            _dialogHost?.Detach();
            foreach (var subscription in _graphChatSubscriptions.Values) subscription.Session.Event -= subscription.Handler;
            _graphChatSubscriptions.Clear();
            _backgroundHosts.Add(old);
            _host = null;
        }
        _options = _options with { ProjectPath = full, SessionId = null };
        _projectOnlyManager = manager;
        var callbacks = new SidebarViewModel.Callbacks(async id =>
        {
            _options = _options with { SessionId = id };
            await BootstrapRealAsync();
        }, NewWorkspaceAsync, _ => Task.CompletedTask, full, Path.GetFileName(full), string.Empty,
            SwitchProjectRequested: SwitchProjectAsync, OpenProjectRequested: OpenProjectAsync,
            RecentProjects: ProjectPreferences.LoadRecent(manager.Metadata)
                .Where(candidate => !ProjectArchive.IsArchivedPath(manager.Metadata, candidate) &&
                    !ScratchWorkspace.Matches(GraphLayout, candidate)).ToArray());
        AttachSession(new UnopenedChatSession(), [], callbacks, initialPage: AppPage.Graph);
        if (!GraphEnabled) { await BootstrapRealAsync(); return; }
        ProjectPreferences.RecordOpened(_projectOnlyManager.Metadata, full);
        HideStartupOverlay();
        StartupPickProject.IsVisible = false;
        _lastWorkMode = AppPage.Graph;
        Program.Startup?.FinishAfterRender();
    }

    private sealed class GraphApprovalLease(ExtensionUiDialogHost host) : IDisposable
    {
        public void Dispose() => Dispatcher.UIThread.Post(host.Detach);
    }
}
