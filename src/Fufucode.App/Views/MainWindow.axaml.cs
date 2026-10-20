using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Fufucode.App.Abstractions;
using Fufucode.App.Infrastructure;
using Fufucode.App.Integration;
using Fufucode.App.ViewModels;
using Fufucode.Core;
using Fufucode.Core.Backend;
using Avalonia.VisualTree;

namespace Fufucode.App;

public partial class MainWindow : Window
{
    // M3.1：启动时可由最近项目解析或“选择项目”态补全；运行中切换项目整体替换。
    private AppOptions _options;
    private readonly CancellationTokenSource _bootstrapCts = new();
    private RealSessionHost? _host;
    private ExtensionUiDialogHost? _dialogHost;
    private DispatcherTimer? _draftDebounce;
    private bool _closed;
    private bool _closeApproved;
    private bool _closingInProgress;
    private bool _switching;
    private ResourceDictionary? _micaOverrides;
    // 最近一次启动/重启失败（时间+消息）——跨视图模型重建保留，扩展页顶部展示。
    private string? _lastStartupFailure;
    private bool _restarting;
    // 设置页发起的重启：重启成功重新 Attach 后把已存 SelectedModel 应用到当前空闲会话。
    private bool _settingsApplyModelAfterRestart;
    // 启动遮罩“重试”按钮要执行的动作：初始启动失败=整体重启 bootstrap；
    // 会话重启失败=按同一 session id 再恢复（旧进程已释放，不再重复释放步骤）。
    private Func<Task>? _startupRetry;
    // 自测接缝：注入重启操作与信任询问，验证失败后的无活动会话/遮罩/重试行为。
    internal Func<CancellationToken, Task>? RestartOperationForTesting;
    internal Func<ProjectTrustStatus, Task<ProjectTrustDecision?>>? AskTrustForTesting;
    // 自测接缝：注入启动打开操作与“停用扩展”移除动作（验证启动失败遮罩流程）。
    internal Func<CancellationToken, Task<RealSessionHost>>? OpenHostForTesting;
    internal Action<string>? DisableExtensionForTesting;
    // 启动失败的扩展路径（UserExtensionLoadException / ProjectExtensionNotTrustedException）。
    private string? _startupFailedExtensionPath;
    // 页面视图常驻：导航往返不重建，保住时间线滚动位置与 InputBox 的 Tunnel/preedit 接线。
    private readonly Views.ChatPage _chatPage = new();
    private readonly Views.ExtensionsPage _extensionsPage = new();
    private readonly Views.SettingsPage _settingsPage = new();
    // 设置 → 已归档：项目与会话的归档管理控件（按数据根重建）。
    private Features.Archive.ArchivePage? _archivePage;
    private string? _archiveStorePath;
    private MainViewModel? _vm;
    private AppSettingsStore? _settingsStore;

    /// <summary>无参构造保持 mock 会话（无头自测与设计器使用）；真实会话经 <see cref="AppOptions"/> 注入。</summary>
    public MainWindow() : this(AppOptions.Mock)
    {
    }

    public MainWindow(AppOptions options)
    {
        _options = options;
        if (Program.SelfTestMode)
            ConfirmDiscardSettingsForTesting = _ => Task.FromResult(true);
        InitializeComponent();
        InitializeWorkspaceShell();
        InitializeCaption();
        InitializeGlobalWorkspace();
        InitializeCustomization();
        // BRW-1：在 _customization 就绪后创建浏览器设置，便于一次性迁移旧偏好。
        InitializeBrowserSettings();
        InitializeShortcuts();
        InitializeStorage();
        InitializeLocalPlugins();
        // SEEP-1：Seep 逆向工作台插件的组合根接线（未安装时不启动任何子进程）。
        InitializeReverseLabPlugin();

        // 把已安装插件的工具与人格接入 FUMO 原生会话。延迟读取，安装/卸载后立即生效。
        NativeBackendWiring.ToolProviders = () => new IPureToolDispatcher?[]
        {
            _browserToolRegistry,
            _reverseLabToolRegistry
        }.OfType<IPureToolDispatcher>().ToArray();
        NativeBackendWiring.SystemPromptProvider = _reverseLabPromptProvider;
        AddHandler(GotFocusEvent, (_, e) => { if (e.Source is TextBox text) _lastTextFocus = text; }, Avalonia.Interactivity.RoutingStrategies.Bubble);

        // Mica：仅当系统实际启用了 Mica 才把页面/侧栏背景换为透明层，其余（含无头）保持实色。
        PropertyChanged += (_, args) =>
        {
            if (args.Property == ActualTransparencyLevelProperty)
            {
                ApplyMicaOverrides();
            }
        };

        if (options.Mode == AppSessionMode.Mock)
        {
            Title = "Fufucode（mock 演示）";
            AttachSession(new Mock.MockChatSession(), sessions: null, callbacks: null);
        }
        else
        {
            Title = "Fufucode";
            ShowStartup("正在启动会话…", inProgress: true);
        }

        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += OnClosed;
    }

    private void AttachSession(IChatSession session, IReadOnlyList<SessionItemViewModel>? sessions,
        SidebarViewModel.Callbacks? callbacks, IPromptSteering? steering = null, AppPage initialPage = AppPage.Chat)
    {
        var vm = new MainViewModel(
            session,
            new AvaloniaClipboardService(),
            new DispatcherRefreshScheduler(TimeSpan.FromMilliseconds(33)),
            sessions,
            callbacks);
        // Set before binding PageHost; session rebuild must not briefly mount a different page.
        vm.CurrentPage = initialPage;
        // P2-17：steer 旁路——真实会话由 AttachRealSession 显式注入；mock 会话自身实现 IPromptSteering。
        vm.Input.Steering = steering ?? session as IPromptSteering;
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm.WorkspacePanel.Dispose();
            _vm.Sidebar.Dispose();
        }

        _vm = vm;
        vm.NavigateRequested = NavigateWorkspaceAsync;
        vm.Timeline.ScrollToEndRequested += OnScrollToEndRequested;
        vm.PropertyChanged += OnVmPropertyChanged;
        if (session is Mock.MockChatSession)
        {
            vm.Extensions.AttachMock();
        }

        vm.Extensions.SetStartupFailure(_lastStartupFailure);
        AttachSettingsContext(vm);
        WireSettingsReturn(vm);
        DataContext = vm;
        AttachWorkspaceContext(vm);
        AttachGlobalContext(vm);
        InitializeSessionManagement();
        // 常驻页面：ChatPage 继承窗口 DataContext（MainViewModel），扩展/设置页绑定自己的 VM。
        _extensionsPage.DataContext = vm.Extensions;
        _settingsPage.DataContext = vm.Settings;
        UpdatePageHost();
    }

    /// <summary>设置页上下文（W2）：真实模式用 host.Manager.Settings（单一存储，Pi 启动读它）；
    /// mock 用 App.SettingsStore（它此后仅供启动读主题，不再写真实会话设置）。</summary>
    private void AttachSettingsContext(MainViewModel vm)
    {
        _settingsStore ??= App.SettingsStore;
        var host = _host;
        var layout = host?.Manager.Layout;
        var (nodeHint, cliHint) = RuntimePathHints();
        var context = new SettingsPageViewModel.Context(
            Store: host?.Manager.Settings ?? _settingsStore,
            IsMock: host is null,
            CurrentModel: () => host?.Chat.CurrentModel,
            QueryModels: host is null
                ? null
                : cancellationToken => host.Current.Coordinator.QueryAvailableModelsAsync(cancellationToken),
            IsTurnActive: () => vm.Input.IsTurnActive,
            SetModel: host is null || !host.HasLiveSession
                ? null
                : (provider, modelId, ct) =>
                    host.Current.Coordinator.SetModelAsync(provider, modelId, ct),
            SetThinkingLevel: host is null || !host.HasLiveSession
                ? null
                : (level, ct) => host.Current.Coordinator.SetThinkingLevelAsync(level, ct),
            QueryState: host is null || !host.HasLiveSession
                ? null
                : ct => host.Current.Coordinator.QueryStateAsync(ct),
            RestartSession: () =>
            {
                // 设置页发起的重启：成功后把已存 SelectedModel 应用到新会话（ApplySavedModelIfIdle）。
                _settingsApplyModelAfterRestart = true;
                return RestartSessionAsync(CancellationToken.None);
            },
            SaveCredential: (envName, key) =>
                new Fufucode.Core.WindowsPasswordVaultCredentialResolver().Save(envName, key),
            // 与 Core 启动 Pi 时的解析完全一致：凭据管理器 → 进程环境变量（不含用户级回退，
            // 否则仅用户级存在的变量会通过校验却在重启时抛 credential unavailable）。
            ResolveCredential: envName =>
                new Fufucode.Core.WindowsPasswordVaultCredentialResolver().ResolveAsync(envName),
            ModelApplied: model => vm.StatusBar.SetModelOverride(model),
            AgentDirectory: layout?.AgentDirectory,
            DataRoot: layout?.Root,
            LogsDirectory: layout?.LogsDirectory,
            ApprovalText: () => host is null ? null : host.Current.Coordinator.ApprovalAvailability.ToString(),
            ApprovalDiagnostic: () => host?.Current.Coordinator.ApprovalDiagnostic,
            ActiveExtensionPaths: () => host?.ActiveExtensionPaths.ToArray() ?? [],
            LastStartupFailure: () => _lastStartupFailure,
            ProjectPath: () => host?.ProjectPath ?? "(mock)",
            OpenFolder: OpenFolderAsync,
            CopyText: text => Clipboard is { } clipboard
                ? Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, text)
                : Task.CompletedTask,
            PiVersion: () => DiagnosticsSummary.ResolvePiVersion(cliHint),
            NodeVersion: () => DiagnosticsSummary.ResolveNodeVersion(nodeHint));
        vm.Settings.Attach(context);
        if (_settingsApplyModelAfterRestart)
        {
            // 设置页「立即重启」成功后的同进程应用（重启失败/未走到 attach 时下一周期再判）。
            _settingsApplyModelAfterRestart = false;
            vm.Settings.ApplySavedModelIfIdle();
        }
    }

    /// <summary>关于页版本探测用的运行时路径提示：完整解析失败时退回显式覆盖参数（不抛）。</summary>
    private (string? Node, string? Cli) RuntimePathHints()
    {
        try
        {
            var (node, cli) = RealSessionBootstrap.ResolveRuntime(_options);
            return (node, cli);
        }
        catch (Exception)
        {
            return (_options.PiNodePath, _options.PiCliPath);
        }
    }

    private Task OpenFolderAsync(string path)
    {
        // 系统外壳打开目录（Windows=Explorer；其它平台按默认处理程序）。
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
        {
            UseShellExecute = true,
        });
        return Task.CompletedTask;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsRightPanelOpen))
        {
            if (!_syncingDockState && _vm is { } viewModel)
            {
                // 页签被关空的区域重开时回填默认页，否则面板永远打不开（Pages 空+Hidden=false 的死态）。
                if (viewModel.IsRightPanelOpen && EnsureDockHasPages(DockZone.Right))
                { _dockLayout.Hidden[DockZone.Right] = false; RenderDockPages(); SaveWorkspaceDimensions(); }
                else _dockLayout.Hidden[DockZone.Right] = !viewModel.IsRightPanelOpen;
            }
            if (_vm?.IsRightPanelOpen == true)
            {
                _rightReturnFocus = FocusManager?.GetFocusedElement() as Control;
                _ = _vm.WorkspacePanel.RefreshAsync();
            }
            ApplyWorkspaceLayout();
            if (_vm?.IsRightPanelOpen == false) RestoreFocus(_rightReturnFocus);
        }

        if (e.PropertyName == nameof(MainViewModel.CurrentPage))
        {
            UpdatePageHost();
            ApplyWorkspaceLayout();
        }
    }

    private void UpdatePageHost()
    {
        if (_vm is null)
        {
            return;
        }

        PageHost.Content = _vm.CurrentPage switch
        {
            AppPage.Extensions => _extensionsPage,
            AppPage.Settings => _settingsPage,
            _ => (object)_chatPage,
        };
        _vm.Sidebar.SetConversationVisible(_vm.IsChatPage);
        // 新进入 PageHost 的页面（设置页导航等）也要挂上玻璃 chrome。
        if (_windowSkins.TryGetValue(this, out var skinLease))
            Dispatcher.UIThread.Post(skinLease.RescanGlassChrome, DispatcherPriority.Loaded);
    }

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        ApplyMicaOverrides();
        if (_options.Mode == AppSessionMode.Real)
        {
            // M3.1：无显式 --project 时回退到最近一个仍存在的项目；全部缺失则进入“选择项目”态。
            if (_options.ProjectPath is null)
            {
                var metadata = new DesktopMetadataStore(_options.CreateAppDataLayout());
                var recent = ProjectPreferences.LoadRecent(metadata)
                    .Where(path => !ProjectArchive.IsArchivedPath(metadata, path))
                    .ToArray();
                var resolved = ProjectPreferences.ResolveStartupProject(
                    _options.ProjectPath, recent, Directory.Exists);
                if (resolved is not null)
                {
                    _options = _options with { ProjectPath = resolved };
                }
                else
                {
                    ShowProjectPicker(recent.Where(Directory.Exists).Take(5).ToArray());
                    return;
                }
            }

            await BootstrapRealAsync();
            return;
        }

        if (DataContext is MainViewModel vm)
        {
            await vm.InitializeCommand.ExecuteAsync(null);
        }

        ScrollToEnd();
    }

    /// <summary>
    /// 真实组合根：启动 Pi 子进程、恢复（或新建）持久会话、回放历史，然后才接收新工作
    /// （计划 §7.2）。失败时保留遮罩并给出可操作诊断，不静默退回 mock。
    /// </summary>
    private async Task BootstrapRealAsync()
    {
        try
        {
            StartupRetryButton.IsVisible = false;
            ShowStartup("正在启动会话…", inProgress: true);
            var host = await ProjectTrustFlow.RunAsync(
                () => OpenHostForTesting is { } testOpen
                    ? testOpen(_bootstrapCts.Token)
                    : RealSessionHost.OpenAsync(_options, _bootstrapCts.Token),
                AskTrustAsync,
                (status, decision) => RealSessionHost.SaveTrustDecision(_options, status, decision));
            if (_closed)
            {
                await host.DisposeAsync();
                return;
            }

            _host = host;
            _dialogHost = new ExtensionUiDialogHost(this);
            AttachRealSession();
            ProjectPreferences.RecordOpened(host.Manager.Metadata, host.ProjectPath);
            StartupOverlay.IsVisible = false;
            StartupPickProject.IsVisible = false;
            ScrollToEnd();
        }
        catch (OperationCanceledException)
        {
            // 窗口在启动期间被关闭，属正常路径。
        }
        catch (ProjectTrustDeclinedException)
        {
            // 用户放弃信任决定：不启动会话，保留遮罩并给出重试入口。
            ShowStartup("未作出信任决定，会话未启动。", inProgress: false);
            StartupRetryButton.IsVisible = true;
            StartupOpenOtherButton.IsVisible = true;
        }
        catch (Exception ex)
        {
            // 启动失败（扩展加载失败会导致 Pi 启动即退出）——记入扩展页 LastStartupFailure。
            _lastStartupFailure = ex.GetBaseException().Message;
            _startupFailedExtensionPath = ex switch
            {
                UserExtensionLoadException load => load.ExtensionPath,
                ProjectExtensionNotTrustedException trust => trust.ExtensionPath,
                _ => null,
            };
            ShowStartup(_startupFailedExtensionPath is { } extPath
                    ? $"扩展 {System.IO.Path.GetFileName(extPath)} 加载失败：{_lastStartupFailure}"
                    : "启动失败：" + _lastStartupFailure,
                inProgress: false);
            StartupRetryButton.IsVisible = true;
            StartupDisableExtensionButton.IsVisible = _startupFailedExtensionPath is not null;
            StartupOpenOtherButton.IsVisible = true;
            (DataContext as MainViewModel)?.Extensions.SetStartupFailure(_lastStartupFailure);
        }
    }

    // ── M3.1 项目选择与运行中切换 ──

    /// <summary>启动遮罩“选择项目”态：主按钮弹文件夹选择器，下方列最近项目（最多 5 个）。</summary>
    private void ShowProjectPicker(IReadOnlyList<string> recent)
    {
        ShowStartup("选择一个项目文件夹开始", inProgress: false);
        StartupArt.IsVisible = false;
        StartupPickProject.IsVisible = true;
        StartupRecentProjects.ItemsSource = recent;
        // 从失败/未信任态转入：隐藏原操作按钮。
        StartupRetryButton.IsVisible = false;
        StartupDisableExtensionButton.IsVisible = false;
        StartupOpenOtherButton.IsVisible = false;
    }

    /// <summary>失败/未信任态“打开其他项目…”：进入与首次启动相同的选择项目态（最近列表含当前失败项目）。</summary>
    private void StartupOpenOther_Click(object? sender, RoutedEventArgs e)
    {
        var metadata = new DesktopMetadataStore(_options.CreateAppDataLayout());
        var recent = ProjectPreferences.LoadRecent(metadata).Where(Directory.Exists).Take(5).ToArray();
        ShowProjectPicker(recent);
    }

    private async void StartupOpenProject_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "打开项目文件夹",
            AllowMultiple = false,
        });
        var path = folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        if (path is not null)
        {
            BeginProject(path);
        }
        // 取消选择器：停留在“选择项目”态。
    }

    private void StartupRecentProject_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is string path)
        {
            BeginProject(path);
        }
    }

    /// <summary>选定项目后走标准真实启动流程（放弃任何 --session 指定，恢复该项目最近会话）。</summary>
    private void BeginProject(string path)
    {
        _options = _options with { ProjectPath = path, SessionId = null };
        StartupPickProject.IsVisible = false;
        _ = BootstrapRealAsync();
    }

    /// <summary>运行中切换项目（先开后放：新项目打开成功才释放旧 host，失败/放弃旧会话原样保留）。</summary>
    private async Task SwitchProjectAsync(string path)
    {
        if (_switching || _restarting || _closed || _host is null)
        {
            return;
        }

        if (RealSessionHost.PathEquals(_host.ProjectPath, path))
        {
            return;
        }

        if (DataContext is MainViewModel vm && vm.Input.IsTurnActive)
        {
            vm.Timeline.AddNotification("当前回合进行中，请先停止或等待完成再切换项目");
            return;
        }

        _switching = true;
        try
        {
            if (!await ConfirmLeaveSettingsAsync()) return;
            if (DataContext is MainViewModel pre)
            {
                _host.SaveDraft(pre.Input.ComposePrompt());
            }

            // 旧 vm 的防抖 Tick 可能在 Dispose/打开的 await 间隙触发并写入新 host——先停掉。
            _draftDebounce?.Stop();
            ShowStartup("正在打开项目…", inProgress: true);
            var newOptions = _options with { ProjectPath = path, SessionId = null };
            var next = await ProjectTrustFlow.RunAsync(
                () => RealSessionHost.OpenAsync(newOptions, _bootstrapCts.Token),
                AskTrustAsync,
                (status, decision) => RealSessionHost.SaveTrustDecision(newOptions, status, decision));
            if (_closed)
            {
                await next.DisposeAsync();
                return;
            }

            _dialogHost?.Detach();
            var previous = _host;
            _options = newOptions;
            await CloseSideChatAsync();
            _host = next;
            _dialogHost = new ExtensionUiDialogHost(this);
            await previous.DisposeAsync();
            AttachRealSession();
            ProjectPreferences.RecordOpened(_host.Manager.Metadata, _host.ProjectPath);
            StartupOverlay.IsVisible = false;
            StartupPickProject.IsVisible = false;
            ScrollToEnd();
        }
        catch (ProjectTrustDeclinedException)
        {
            StartupOverlay.IsVisible = false;
            (DataContext as MainViewModel)?.Timeline.AddNotification(
                "未切换项目：未作出信任决定，已保留当前会话。");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StartupOverlay.IsVisible = false;
            (DataContext as MainViewModel)?.Timeline.AddNotification(
                $"未切换项目：{ex.GetBaseException().Message}");
        }
        finally
        {
            _switching = false;
        }
    }

    /// <summary>“打开其他文件夹…”：系统文件夹选择器 → 与最近项目同一切换路径。</summary>
    private async Task OpenProjectAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "打开项目文件夹",
            AllowMultiple = false,
        });
        var path = folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        if (path is not null)
        {
            await SwitchProjectAsync(path);
        }
    }

    /// <summary>自测入口：与侧栏项目 Flyout“最近项目”同一路径。</summary>
    internal Task SwitchProjectForTestingAsync(string path) => SwitchProjectAsync(path);

    /// <summary>自测观察口：启动解析/项目切换后的生效选项。</summary>
    internal AppOptions OptionsForTesting => _options;

    private async void StartupRetryButton_Click(object? sender, RoutedEventArgs e)
    {
        StartupRetryButton.IsVisible = false;
        StartupDisableExtensionButton.IsVisible = false;
        await (_startupRetry ?? BootstrapRealAsync)();
    }

    /// <summary>启动期扩展加载失败：把该扩展从持久启用列表移除后重开（不再触它）。</summary>
    private async void StartupDisableExtensionButton_Click(object? sender, RoutedEventArgs e)
    {
        StartupDisableExtensionButton.IsVisible = false;
        var path = _startupFailedExtensionPath;
        _startupFailedExtensionPath = null;
        if (path is not null)
        {
            if (DisableExtensionForTesting is { } testDisable)
            {
                testDisable(path);
            }
            else
            {
                RealSessionHost.DisableExtension(_options, path);
            }
        }

        await BootstrapRealAsync();
    }

    /// <summary>项目信任询问：模态对话框返回决定；点 X 放弃（null）。</summary>
    private Task<ProjectTrustDecision?> AskTrustAsync(ProjectTrustStatus status) =>
        AskTrustForTesting?.Invoke(status) ?? AskTrustDialogAsync(status);

    private async Task<ProjectTrustDecision?> AskTrustDialogAsync(ProjectTrustStatus status)
    {
        var dialog = new ProjectTrustDialog(status);
        return await Fufucode.App.Views.SkinDialog.ShowAsync<ProjectTrustDecision?>(dialog, this);
    }

    /// <summary>按宿主当前会话重建视图模型：历史回放、草稿恢复、审批弹窗与通知接线。</summary>
    private void AttachRealSession(bool preservePage = false)
    {
        // 会话成功打开/重启/切换后，此前的启动失败记录已过时。
        _lastStartupFailure = null;
        var host = _host!;
        // Read the current page at attachment time, so navigation during maintenance wins.
        var page = preservePage ? _vm?.CurrentPage ?? AppPage.Chat : AppPage.Chat;
        var current = host.Current.Session;
        var callbacks = new SidebarViewModel.Callbacks(
            SwitchRequested: id => SwitchSessionAsync(id),
            NewSessionRequested: () => SwitchSessionAsync(sessionId: null),
            RenameRequested: name => RenameSessionAsync(name),
            ProjectText: host.ProjectPath,
            CurrentName: current.Name ?? string.Empty,
            CurrentSessionId: current.Id,
            SwitchProjectRequested: path => SwitchProjectAsync(path),
            OpenProjectRequested: () => OpenProjectAsync(),
            RecentProjects: ProjectPreferences.LoadRecent(host.Manager.Metadata)
                .Where(path => !ProjectArchive.IsArchivedPath(host.Manager.Metadata, path))
                .ToArray());

        AttachSession(host.Chat, BuildSessionItems(host), callbacks,
            new RealPromptSteering(host), page);
        RenderOverview();
        var vm = (MainViewModel)DataContext!;
        vm.Timeline.LoadHistory(host.CurrentTranscript());
        vm.RemoteHistory = host.CurrentTranscript;
        if (host.Diagnostic is { } diagnostic)
        {
            vm.Timeline.AddNotification(diagnostic);
        }

        if (host.Chat.LastOutcome == SessionOutcome.Interrupted)
        {
            vm.Timeline.AddNotification("上次运行被中断（进程退出或已停止）；不会自动重发，可继续提出新要求。");
        }

        // 草稿按会话隔离恢复（M4；desktop.db drafts 表）。
        vm.Input.DraftText = host.LoadDraft() ?? string.Empty;
        WireDraftPersistence(vm);

        _dialogHost!.Attach(host.Chat);
        Fufucode.Core.Plugins.PluginPackageManager.ActiveRemoteControlService?.Attach(host.Chat, host.ProjectPath);
        Fufucode.Core.Plugins.PluginPackageManager.ActiveRemoteControlService?.LoadHistory(host.CurrentTranscript());
        SessionUiWiring.AttachExtensionUi(host.Chat, vm.Timeline, vm.Input, vm.StatusBar,
            title => Title = $"Fufucode — {title}");

        // H3/H4 扩展目录：get_commands + 磁盘发现 + 扩展错误订阅；
        // 启停上下文（H4）：pending/active 持久状态与"应用并重启"由 host 提供。
        var catalog = new ExtensionCatalog(host.Current.Coordinator, host.ProjectPath, host.AgentDirectory);
        var manage = new ExtensionManageContext(
            ActivePaths: host.ActiveExtensionPaths,
            EnabledPaths: host.EnabledExtensionPaths,
            AddedPaths: host.AddedExtensionPaths,
            MissingPaths: host.SkippedMissingExtensionPaths,
            LoadErrors: host.ExtensionLoadErrors,
            ApplyAsync: (paths, ct) => ApplyExtensionsAsync(paths, ct),
            PickExtensionAsync: PickExtensionFolderAsync,
            AddExtension: host.AddExtensionPath,
            RemoveAddedExtension: host.RemoveAddedExtensionPath);
        vm.Extensions.AttachRealSession(
            catalog, host.Chat, host.ProjectPath, host.AgentDirectory,
            RestartSessionAsync, AskTrustAsync,
            (status, decision) => RealSessionHost.SaveTrustDecision(_options, status, decision),
            () => _host?.TrustStatus,
            FillCommand,
            manage: manage);
        _localPluginViewModel.NotifyHostChanged();
        _ = _localPluginViewModel.RefreshHostAsync();
    }

    /// <summary>“添加扩展…”文件夹选择（Pi 目录入口：index.ts/index.js 或 package.json pi.extensions）。</summary>
    private async Task<string?> PickExtensionFolderAsync(CancellationToken _)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择扩展目录",
            AllowMultiple = false,
        });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    /// <summary>
    /// 应用扩展变更并安全重启（H4）：走与 RestartSessionAsync 相同的遮罩/信任流程。
    /// 返回 null=成功；扩展加载失败已回滚时返回 <see cref="ExtensionApplyFailure"/>；
    /// 信任拒绝且旧会话仍存活时隐藏遮罩并重抛给 VM（VM 保留 pending）。
    /// </summary>
    private async Task<ExtensionApplyFailure?> ApplyExtensionsAsync(
        IReadOnlyList<string> pending, CancellationToken cancellationToken, RealSessionHost? expectedHost = null)
    {
        if (_restarting || _closed || _host is null)
        {
            return new ExtensionApplyFailure(string.Empty, "重启进行中", string.Empty);
        }

        var target = _host;
        if (expectedHost is not null && !ReferenceEquals(expectedHost, target))
            return new ExtensionApplyFailure(string.Empty, "原项目/会话已改变，未应用扩展。", string.Empty);
        var options = _options;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _bootstrapCts.Token);

        _restarting = true;
        if (DataContext is MainViewModel preVm && _host.HasLiveSession)
        {
            target.SaveDraft(preVm.Input.ComposePrompt());
        }

        try
        {
            ShowStartup("正在应用扩展变更并重启会话…", inProgress: true);
            var failure = await ProjectTrustFlow.RunAsync(
                async () =>
                {
                    if (!ReferenceEquals(target, _host) || _closed) throw new InvalidOperationException("原会话已改变，未应用扩展。");
                    return await target.ApplyExtensionsAndRestartAsync(pending, linked.Token);
                },
                AskTrustAsync,
                (status, decision) => RealSessionHost.SaveTrustDecision(options, status, decision));

            if (_closed)
            {
                return new ExtensionApplyFailure(string.Empty, "窗口已关闭，应用结果未确认。", string.Empty);
            }

            if (failure is not null)
            {
                // 已回滚到旧启用列表：会话恢复可用，隐藏遮罩，把失败明细还给 VM 行展示。
                // 旧Chat已在安全重启中释放；必须重新接恢复后的真实连接，不能留指向已退出进程的VM。
                if (ReferenceEquals(target, _host) && target.HasLiveSession) AttachRealSession(preservePage: true);
                StartupOverlay.IsVisible = false;
                return failure;
            }

            AttachRealSession(preservePage: true);
            StartupOverlay.IsVisible = false;
            ScrollToEnd();
            _startupRetry = null;
            return null;
        }
        catch (ProjectTrustDeclinedException)
        {
            if (target.HasLiveSession)
            {
                // 信任预检在释放前抛出：旧会话仍然存活，仅提示并保留 pending。
                StartupOverlay.IsVisible = false;
                if (DataContext is MainViewModel declined)
                {
                    declined.Timeline.AddNotification("未作出信任决定，扩展变更未应用。");
                }
            }
            else
            {
                ShowStartup("未作出信任决定，会话未恢复。", inProgress: false);
                StartupRetryButton.IsVisible = true;
                _startupRetry = RetryRestartAsync;
            }

            throw;
        }
        catch (OperationCanceledException)
        {
            if (!_closed && ReferenceEquals(target, _host))
            {
                if (target.HasLiveSession)
                {
                    AttachRealSession(preservePage: true);
                    StartupOverlay.IsVisible = false;
                }
                else
                {
                    ShowStartup("扩展应用已取消。可重试恢复原会话与原启用列表。", inProgress: false);
                    StartupRetryButton.IsVisible = true;
                    StartupOpenOtherButton.IsVisible = true;
                    _startupRetry = RetryRestartAsync;
                }
            }
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _lastStartupFailure = ex.GetBaseException().Message;
            if (target.HasLiveSession)
            {
                // 未走到释放阶段的失败：会话仍可用，不盖遮罩。
                StartupOverlay.IsVisible = false;
            }
            else
            {
                ShowStartup("应用扩展变更失败：" + _lastStartupFailure, inProgress: false);
                StartupRetryButton.IsVisible = true;
                _startupRetry = RetryRestartAsync;
            }

            if (DataContext is MainViewModel vm)
            {
                vm.Extensions.SetStartupFailure(_lastStartupFailure);
            }

            throw;
        }
        finally
        {
            _restarting = false;
            _localPluginViewModel.NotifyHostChanged();
        }
    }

    /// <summary>
    /// 安全重启当前会话（H3）：同一 session id 关旧开新，历史恢复、草稿保留、
    /// 扩展目录与 UI/审批/扩展请求接线整体重建。守卫（回合中/待决请求）在 VM 层完成；
    /// 失败记录 LastStartupFailure，旧进程已释放时启动遮罩会显示错误。
    /// </summary>
    private async Task RestartSessionAsync(CancellationToken cancellationToken)
    {
        var testOp = RestartOperationForTesting;
        if (_restarting || _closed || (_host is null && testOp is null))
        {
            return;
        }

        _restarting = true;
        // 重启前把输入框草稿显式落盘：AttachRealSession 恢复草稿读的是持久层，
        // 600ms 防抖可能尚未触发（H3 复审 #2）。
        if (DataContext is MainViewModel preVm && _host is { HasLiveSession: true } liveHost)
        {
            liveHost.SaveDraft(preVm.Input.ComposePrompt());
        }

        try
        {
            ShowStartup("正在安全重启会话…", inProgress: true);
            await ProjectTrustFlow.RunAsync(
                async () =>
                {
                    if (testOp is not null)
                    {
                        await testOp(cancellationToken);
                    }
                    else
                    {
                        await _host!.RestartCurrentAsync(_bootstrapCts.Token);
                    }

                    return true;
                },
                AskTrustAsync,
                (status, decision) => RealSessionHost.SaveTrustDecision(_options, status, decision));

            if (_closed)
            {
                return;
            }

            if (_host is not null)
            {
                AttachRealSession();
            }

            StartupOverlay.IsVisible = false;
            ScrollToEnd();
            _startupRetry = null;
        }
        catch (ProjectTrustDeclinedException)
        {
            // 旧进程已释放、会话实际未恢复：遮罩保持可见并给重试，不得谎称“已保留当前会话”。
            ShowStartup("未作出信任决定，会话未恢复。", inProgress: false);
            StartupRetryButton.IsVisible = true;
            _startupRetry = RetryRestartAsync;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 旧进程已释放：保留遮罩 + 重试按钮（RetryRestartAsync 不再重复释放）。
            _lastStartupFailure = ex.GetBaseException().Message;
            ShowStartup("重启失败：" + _lastStartupFailure, inProgress: false);
            StartupRetryButton.IsVisible = true;
            _startupRetry = RetryRestartAsync;
            if (DataContext is MainViewModel vm)
            {
                vm.Extensions.SetStartupFailure(_lastStartupFailure);
                vm.Timeline.AddNotification($"重启会话失败：{_lastStartupFailure}");
            }
        }
        finally
        {
            _restarting = false;
        }
    }

    private Task RetryRestartAsync() => RestartSessionAsync(CancellationToken.None);

    /// <summary>自测入口：与扩展页“安全重启会话”命令同一路径。</summary>
    internal Task RestartSessionForTestingAsync() => RestartSessionAsync(CancellationToken.None);

    /// <summary>扩展页「填入」/命令 Flyout：调用名填入输入框并切回对话页聚焦。</summary>
    private void FillCommand(CommandEntry entry)
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        vm.Input.InsertInvocation(entry.Invocation);
        vm.CurrentPage = AppPage.Chat;
    }

    private static IReadOnlyList<SessionItemViewModel> BuildSessionItems(RealSessionHost host)
    {
        var currentId = host.Current.Session.Id;
        var currentModel = host.Chat.CurrentModel;
        var archivedProjects = ProjectArchive.LoadSet(host.Manager.Metadata);
        return host.Sessions.Where(record => record.Id == currentId ||
                !SessionArchive.IsArchived(host.Manager.Metadata, record, archivedProjects))
            .OrderByDescending(record => SessionPresentation.Load(host.Manager.Metadata, record.Id).Pinned).Select(record =>
        {
            var isCurrent = record.Id == currentId;
            var title = record.Name ?? record.Id[..Math.Min(8, record.Id.Length)];
            var subtitle = BackendCapabilities.For(record.Backend).Name + " · " +
                (isCurrent ? "当前活跃会话" : record.LastOpened.LocalDateTime.ToString("MM-dd HH:mm"));
            var item = new SessionItemViewModel(
                title: title,
                subtitle: subtitle,
                id: record.Id,
                isCurrent: isCurrent,
                isInterrupted: record.WasActive && !isCurrent,
                modelName: isCurrent ? currentModel : null,
                tokenSummary: null,
                lastOpened: record.LastOpened);
            item.MarkUnread(SessionPresentation.Load(host.Manager.Metadata, record.Id).Unread);
            return item;
        }).ToArray();
    }

    /// <summary>切换（或新建）会话：先落当前草稿，换会话后整体重建视图模型。</summary>
    private async Task SwitchSessionAsync(string? sessionId)
    {
        if (_switching || _restarting || _host is null || _closed)
        {
            return;
        }

        if (_vm?.Input.IsTurnActive == true)
        { _vm.Timeline.AddNotification("当前回合进行中，请先停止或等待完成再切换会话"); return; }

        _switching = true;
        try
        {
            // 新建聊天不再弹后端选择窗：直接使用设置里的默认 Agent 内核。
            var newBackend = BackendId.NativeCSharp;
            string? backendNote = null;
            if (sessionId is null)
            {
                (newBackend, backendNote) = ResolveNewSessionBackend();
            }
            if (!await ConfirmLeaveSettingsAsync())
            {
                if (_vm is { } retained)
                    retained.Sidebar.UpdateSessions(BuildSessionItems(_host), _host.Current.Session.Id, _host.Current.Session.Name ?? retained.Sidebar.CurrentName);
                return;
            }
            if (DataContext is MainViewModel previous)
            {
                _host.SaveDraft(previous.Input.ComposePrompt());
            }

            // 同上：先停防抖，避免旧 vm 的 Tick 在切换间隙把旧草稿写进新会话。
            _draftDebounce?.Stop();
            ShowStartup(sessionId is null ? "正在新建会话…" : "正在切换会话…", inProgress: true);
            await ProjectTrustFlow.RunAsync<bool>(
                async () =>
                {
                    if (sessionId is null)
                    {
                        await _host!.NewSessionAsync(cancellationToken: _bootstrapCts.Token, backend: newBackend);
                    }
                    else
                    {
                        await _host!.SwitchToAsync(sessionId, _bootstrapCts.Token);
                    }

                    return true;
                },
                AskTrustAsync,
                (status, decision) => RealSessionHost.SaveTrustDecision(_options, status, decision));

            var presentation = SessionPresentation.Load(_host.Manager.Metadata, _host.Current.Session.Id);
            if (presentation.Unread) SessionPresentation.Save(_host.Manager.Metadata, _host.Current.Session.Id, presentation with { Unread = false });
            AttachRealSession();
            if (backendNote is not null) _vm?.Timeline.AddNotification(backendNote);
            StartupOverlay.IsVisible = false;
            ScrollToEnd();
        }
        catch (ProjectTrustDeclinedException)
        {
            // 信任决定被放弃：旧会话保持可用（RealSessionHost 先开后放），仅提示。
            StartupOverlay.IsVisible = false;
            if (DataContext is MainViewModel declined)
            {
                declined.Timeline.AddNotification("未作出信任决定，已保留当前会话。");
            }
        }
        catch (Exception ex)
        {
            // 切换失败：旧会话保持可用（RealSessionHost 先开后放），如实报告。
            StartupOverlay.IsVisible = false;
            if (DataContext is MainViewModel vm)
            {
                vm.Timeline.AddNotification($"切换会话失败：{ex.GetBaseException().Message}");
            }
        }
        finally
        {
            _switching = false;
        }
    }

    private async Task RenameSessionAsync(string name)
    {
        if (_host is null)
        {
            return;
        }

        try
        {
            await _host.RenameCurrentAsync(name);
            if (DataContext is MainViewModel vm)
            {
                vm.Sidebar.UpdateSessions(BuildSessionItems(_host), _host.Current.Session.Id, name);
            }
        }
        catch (Exception ex)
        {
            if (DataContext is MainViewModel vm)
            {
                vm.Timeline.AddNotification($"重命名失败：{ex.GetBaseException().Message}");
            }
        }
    }

    /// <summary>草稿防抖落盘：编辑停顿 600ms 后写入 desktop.db（按当前会话隔离）。</summary>
    private void WireDraftPersistence(MainViewModel vm)
    {
        _draftDebounce?.Stop();
        _draftDebounce = null;

        if (_host is null)
        {
            return;
        }

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _host?.SaveDraft(vm.Input.ComposePrompt());
        };
        vm.Input.PropertyChanged += OnInputPropertyChanged;
        _draftDebounce = timer;

        void OnInputPropertyChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName is nameof(InputViewModel.DraftText) or nameof(InputViewModel.ReferenceContext))
            {
                timer.Stop();
                timer.Start();
            }
        }
    }

    private void ShowStartup(string message, bool inProgress)
    {
        ReturnStartupPlugins();
        StartupText.Text = message;
        StartupProgress.IsVisible = inProgress;
        StartupLocalPluginsButton.IsVisible = !inProgress;
        // 失败态（非进行中）显示 interrupted 空态插画；进行中保持原标识。
        StartupArt.IsVisible = !inProgress;
        // 进行中态不显示“打开其他项目…”；失败态由各自 catch 分支决定。
        if (inProgress)
        {
            StartupOpenOtherButton.IsVisible = false;
        }

        StartupOverlay.IsVisible = true;
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeApproved)
        {
            return;
        }
        e.Cancel = true;
        if (_closingInProgress) return;
        _closingInProgress = true;
        if (!await ConfirmLeaveSettingsAsync())
        {
            _closingInProgress = false;
            return;
        }
        _closed = true;
        Fufucode.Core.Plugins.PluginPackageManager.ActiveRemoteControlService?.Stop();
        _speech.Dispose();
        // SEEP-1：终止本插件启动的 MCP / 中继子进程，避免退出后残留。
        await ShutdownReverseLabAsync();
        await CloseSideChatAsync();
        _bootstrapCts.Cancel();

        _dialogHost?.Detach();
        _draftDebounce?.Stop();
        SaveWorkspaceDimensions();
        DisposeFloatingDocks();
        try { SaveModulePreferences(); }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        { LayoutStateText.Text = "模块状态保存失败：" + error.Message; }
        foreach (var terminal in _projectTerminals.Values) terminal.Dispose();
        foreach (var page in _workbenchPages.Values)
            foreach (var browser in page.Content.GetVisualDescendants().OfType<Views.BrowserViewHost>()) browser.Dispose();
        _workbenchPages.Dispose();
        _actions.Changed -= OnShortcutCapabilitiesChanged;
        _shortcuts.Changed -= UpdateShortcutLabels;
        _storageManager.Dispose();
        _extensionsPage.ReleaseLocalPlugins();
        _libraryService?.Dispose();
        _palette.Dispose();
        _usage.Dispose();
        _notifications.Dispose();
        if (_workspaceSession is not null) _workspaceSession.Event -= OnWorkspaceSessionEvent;
        _vm?.WorkspacePanel.Dispose();
        _vm?.Sidebar.Dispose();
        if (DataContext is MainViewModel vm && _host is not null)
        {
            _host.SaveDraft(vm.Input.ComposePrompt());
        }

        if (_host is not null)
        {
            try
            {
                await _host.DisposeAsync();
            }
            catch
            {
                // 关闭路径尽力而为；异常不阻止退出。
            }
        }

        _closeApproved = true;
        Close();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _bootstrapCts.Dispose();
    }

    private void OnScrollToEndRequested() => ScrollToEnd();

    private void ScrollToEnd() => FindChatPage()?.ScrollToEnd();

    /// <summary>对话页由页面模板实例化，滚动/输入细节封装在 ChatPage 内。</summary>
    private Views.ChatPage? FindChatPage() =>
        this.GetVisualDescendants().OfType<Views.ChatPage>().FirstOrDefault();

    /// <summary>
    /// 参考 UniGetUI Styles.WindowsMica.axaml 的做法：只有当 ActualTransparencyLevel == Mica
    /// 时才覆盖页面/侧栏背景为透明层，让 Mica 透出；回退（含无头渲染）保持实色令牌。
    /// </summary>
    private void ApplyMicaOverrides()
    {
        if (ActualTransparencyLevel != WindowTransparencyLevel.Mica && ActualTransparencyLevel != WindowTransparencyLevel.AcrylicBlur)
        {
            if (_micaOverrides is not null)
            {
                Resources.MergedDictionaries.Remove(_micaOverrides);
                _micaOverrides = null;
            }
            return;
        }

        if (_micaOverrides is not null)
        {
            return;
        }

        var dict = (ResourceDictionary)Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(
            new Uri("avares://Fufucode.App/Styles/MicaOverrides.axaml"));
        Resources.MergedDictionaries.Add(dict);
        _micaOverrides = dict;
    }

}
