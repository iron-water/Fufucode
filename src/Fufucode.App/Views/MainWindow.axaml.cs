using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
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
using Fufucode.Core.RemoteControl;
using Fufucode.Core.Backend;
using Avalonia.VisualTree;

namespace Fufucode.App;

public partial class MainWindow : Window, Features.Plugins.IBrowserPageFactory,
    Features.Plugins.ILibraryPageFactory, Features.Plugins.ILocalReviewPageFactory
{
    // M3.1：启动时可由最近项目解析或“选择项目”态补全；运行中切换项目整体替换。
    private AppOptions _options;
    private readonly CancellationTokenSource _bootstrapCts = new();
    private RealSessionHost? _host;
    private ExtensionUiDialogHost? _dialogHost;
    private DispatcherTimer? _draftDebounce;
    // 当前挂有草稿防抖 Input.PropertyChanged 订阅的 VM（重建前先解除，避免多份订阅与旧计时器）。
    private MainViewModel? _draftVm;
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
    // 已接扩展 UI 的会话对象与解除句柄：同一会话重复 AttachRealSession 时不再叠加订阅
    // （句柄用于替换视图模型时解除旧接线，引用比较作为第二道防线）。
    private IChatSession? _extensionUiWiredSession;
    private IDisposable? _extensionUiWiring;
    // AttachGlobalContext 的页面切换处理器：持引用以便换 VM/关闭时解除。
    private PropertyChangedEventHandler? _globalVmPropertyChanged;
    private AppSettingsStore? _settingsStore;
    // 自测隔离：无头测试通过 Program.SelfTestDataRoot 指定数据根，同一根复用一份存储。
    private static AppSettingsStore? _selfTestSettingsStore;
    private static string? _selfTestSettingsRoot;
    // 后台常驻（托盘）：默认关闭。开启后用户关窗只隐藏窗口，进程与会话继续运行。
    private TrayIconService? _trayIcon;
    private AppSettingsStore? _backgroundStore;
    private bool _hiddenToTray;
    private bool _exitRequested;
    private bool _trayUnavailable;
    /// <summary>自测接缝：无头平台没有真实托盘，用它验证关窗/恢复/退出语义。</summary>
    internal bool SimulateTrayAvailableForTesting { get; set; }
    /// <summary>自测接缝：窗口当前是否处于托盘隐藏态。</summary>
    internal bool HiddenToTrayForTesting => _hiddenToTray;

    /// <summary>无参构造保持 mock 会话（无头自测与设计器使用）；真实会话经 <see cref="AppOptions"/> 注入。</summary>
    public MainWindow() : this(AppOptions.Mock)
    {
    }

    public MainWindow(AppOptions options)
    {
        Program.Startup?.Mark("window.ctor.begin", options.Mode.ToString());
        _options = options;
        if (Program.SelfTestMode)
            ConfirmDiscardSettingsForTesting = _ => Task.FromResult(true);
        InitializeComponent();
        Core.Diagnostics.StartupProbe.Mark("window.xaml", "InitializeComponent done");
        InitializeWorkspaceShell();
        Core.Diagnostics.StartupProbe.Mark("window.workspace-shell", "dock/workbench pages registered");
        InitializeCaption();
        InitializeRowHover();
        InitializeWindowTips();
        InitializeGlobalWorkspace();
        Core.Diagnostics.StartupProbe.Mark("window.global", "caption and global workspace ready");
        InitializeCustomization();
        Core.Diagnostics.StartupProbe.Mark("window.customization", "customization/skin initialized");
        // BRW-1：在 _customization 就绪后创建浏览器设置，便于一次性迁移旧偏好。
        InitializeBrowserSettings();
        Core.Diagnostics.StartupProbe.Mark("window.browser-settings", "browser settings initialized");
        InitializeShortcuts();
        Program.Startup?.Mark("window.shell", "workspace shell, caption and global workspace ready");
        InitializePluginPages();
        Core.Diagnostics.StartupProbe.Mark("window.plugin-pages", "plugin pages synchronized");
        InitializeStorage();
        Core.Diagnostics.StartupProbe.Mark("window.storage", "storage page initialized");
        InitializeLocalPlugins();
        // SEEP-1：Seep 逆向工作台插件的组合根接线（未安装时不启动任何子进程）。
        Program.Startup?.Mark("window.plugins", "plugin pages and reverse lab wired");
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

        var windowTitle = options.Mode == AppSessionMode.Mock ? "Fufucode（mock 演示）" : "Fufucode";
        if (!string.IsNullOrWhiteSpace(options.ReviewLabel)) windowTitle += " · " + options.ReviewLabel;
        Title = windowTitle;
        if (options.Mode == AppSessionMode.Mock)
        {
            AttachSession(new Mock.MockChatSession(), sessions: null, callbacks: null);
        }
        else
        {
            ShowStartup("正在启动会话…", inProgress: true);
        }

        Program.Startup?.Mark("window.ctor.end", "MainWindow constructed");
        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += OnClosed;
        AttachBackgroundMode(EffectiveSettingsStore);
    }

    private void AttachSession(IChatSession session, IReadOnlyList<SessionItemViewModel>? sessions,
        SidebarViewModel.Callbacks? callbacks, IPromptSteering? steering = null, AppPage initialPage = AppPage.Chat)
    {
        // 同一会话对象重复挂接（mock 初始化、重启失败回切等）只需切回页面并刷新侧栏，不重建 VM：
        // 重建会再挂一份事件/输入订阅，导致重复回放与重复处理（内存与行为双膨胀）。
        if (_vm is { } existing && ReferenceEquals(existing.Session, session))
        {
            existing.CurrentPage = initialPage;
            if (sessions is not null && callbacks is not null)
                existing.Sidebar.UpdateSessions(sessions, callbacks.CurrentSessionId, callbacks.CurrentName);
            return;
        }

        Core.Diagnostics.StartupProbe.Mark("attach.vm.begin", "MainViewModel construction");
        var vm = new MainViewModel(
            session,
            new AvaloniaClipboardService(),
            new DispatcherRefreshScheduler(TimeSpan.FromMilliseconds(33)),
            sessions,
            callbacks);
        Core.Diagnostics.StartupProbe.Mark("attach.vm.ctor", "MainViewModel constructed");
        // Set before binding PageHost; session rebuild must not briefly mount a different page.
        vm.CurrentPage = initialPage;
        // P2-17：steer 旁路——真实会话由 AttachRealSession 显式注入；mock 会话自身实现 IPromptSteering。
        vm.Input.Steering = steering ?? session as IPromptSteering;
        if (_vm is not null)
        {
            _vm.Timeline.ScrollToEndRequested -= OnScrollToEndRequested;
            _vm.PropertyChanged -= OnVmPropertyChanged;
            if (_globalVmPropertyChanged is not null) _vm.PropertyChanged -= _globalVmPropertyChanged;
            // 旧 VM 的会话/输入订阅：先解除再释放，避免旧会话继续驱动已废弃的 VM。
            _extensionUiWiring?.Dispose();
            _extensionUiWiring = null;
            _extensionUiWiredSession = null;
            DetachCustomizationSession();
            DetachDraftPersistence();
            _vm.Timeline.Dispose();
            _vm.WorkspacePanel.Dispose();
            _vm.Sidebar.Dispose();
        }

        _vm = vm;
        // 发送交互：乐观气泡与等待提示只依赖 UI 事件，所有后端表现一致。
        vm.Input.SubmissionStarted += vm.Timeline.BeginSubmission;
        vm.Input.SubmissionFailed += vm.Timeline.FailSubmission;
        vm.NavigateRequested = NavigateWorkspaceAsync;
        vm.Timeline.ScrollToEndRequested += OnScrollToEndRequested;
        vm.PropertyChanged += OnVmPropertyChanged;
        if (session is Mock.MockChatSession)
        {
            vm.Extensions.AttachMock();
        }

        vm.Extensions.SetStartupFailure(_lastStartupFailure);
        Core.Diagnostics.StartupProbe.Mark("attach.settings", "settings context begin");
        AttachSettingsContext(vm);
        Core.Diagnostics.StartupProbe.Mark("attach.settings.done", "settings context ready");
        WireSettingsReturn(vm);
        Core.Diagnostics.StartupProbe.Mark("attach.settings-return", "settings return wiring");
        DataContext = vm;
        Core.Diagnostics.StartupProbe.Mark("attach.datacontext", "window DataContext set");
        Core.Diagnostics.StartupProbe.Mark("attach.workspace.begin", "workspace context");
        AttachWorkspaceContext(vm);
        Core.Diagnostics.StartupProbe.Mark("attach.workspace.done", "workspace context ready");
        AttachGlobalContext(vm);
        Core.Diagnostics.StartupProbe.Mark("attach.global.done", "global context ready");
        InitializeSessionManagement();
        Core.Diagnostics.StartupProbe.Mark("attach.session-mgmt", "session management ready");
        // 常驻页面：ChatPage 继承窗口 DataContext（MainViewModel），扩展/设置页绑定自己的 VM。
        _extensionsPage.DataContext = vm.Extensions;
        _settingsPage.DataContext = vm.Settings;
        UpdatePageHost();
        Core.Diagnostics.StartupProbe.Mark("attach.page-host", "page host updated");

        // 每个新 VM 只允许一条提交接线；旧 VM 的订阅随其输入对象一起丢弃。
        vm.Input.SubmissionStarting += _notifications.BeginSubmission;
        vm.Input.SubmissionRejected += _notifications.CancelSubmission;
    }

    /// <summary>设置页上下文（W2）：真实模式用 host.Manager.Settings（单一存储，Pi 启动读它）；
    /// mock 用 App.SettingsStore（它此后仅供启动读主题，不再写真实会话设置）。</summary>
    private void AttachSettingsContext(MainViewModel vm)
    {
        // 无头自测必须读写隔离数据根，不能碰用户真实的 settings.json。
        _settingsStore ??= Program.SelfTestMode && Program.SelfTestDataRoot is { } selfTestRoot
            ? SelfTestSettingsStore(selfTestRoot)
            : App.SettingsStore;
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
            QueryThinkingLevels: host is null || !host.HasLiveSession
                ? null
                : ct => host.Current.Coordinator.QueryThinkingLevelsAsync(ct),
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
        // 后台常驻跟随真实设置存储（host.Manager.Settings）；设置页保存后立即生效。
        AttachBackgroundMode(context.Store);
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
    }

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        ApplyMicaOverrides();
        AttachComputerCursorOverlay();
        if (_options.Mode == AppSessionMode.Real)
        {
            // M3.1：无显式 --project 时回退到最近一个仍存在的项目；全部缺失则进入“选择项目”态。
            if (_options.ProjectPath is null)
            {
                var metadata = new DesktopMetadataStore(_options.CreateAppDataLayout());
                var recent = ProjectPreferences.LoadRecent(metadata)
                    .Where(path => !ProjectArchive.IsArchivedPath(metadata, path) &&
                        !ScratchWorkspace.Matches(_options.CreateAppDataLayout(), path))
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

            Program.Startup?.Mark("session.bootstrap", "real session bootstrap begins");
            await BootstrapRealAsync();
            return;
        }

        Program.Startup?.Mark("session.ready", "mock session ready");
        Program.Startup?.FinishAfterRender();
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
            Core.Diagnostics.StartupProbe.Mark("session.open.begin", "RealSessionHost.OpenAsync");
            var host = await ProjectTrustFlow.RunAsync(
                () => OpenHostForTesting is { } testOpen
                    ? testOpen(_bootstrapCts.Token)
                    : RealSessionHost.OpenAsync(_options, _bootstrapCts.Token),
                AskTrustAsync,
                (status, decision) => RealSessionHost.SaveTrustDecision(_options, status, decision));
            Core.Diagnostics.StartupProbe.Mark("session.open.end", "RealSessionHost.OpenAsync returned");
            if (_closed)
            {
                await host.DisposeAsync();
                return;
            }

            _host = host;
            _dialogHost = CreateDialogHost(host.ProjectPath);
            AttachRealSession();
            ProjectPreferences.RecordOpened(host.Manager.Metadata, host.ProjectPath);
            HideStartupOverlay();
            StartupPickProject.IsVisible = false;
            ScrollToEnd();
            Core.Diagnostics.StartupProbe.Mark("session.ready", "startup overlay hidden, timeline scrolled");
            Program.Startup?.FinishAfterRender();
            // 由系统通知按钮启动的实例：会话就绪后应答那条审批（找不到对应请求就什么也不做）。
            if (Program.StartupActivationPayload is { } startPayload)
            {
                Program.StartupActivationPayload = null;
                _ = HandleApprovalPayloadAsync(startPayload);
            }
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
            _dialogHost = CreateDialogHost(next.ProjectPath);
            await previous.DisposeAsync();
            AttachRealSession();
            ProjectPreferences.RecordOpened(_host.Manager.Metadata, _host.ProjectPath);
            HideStartupOverlay();
            StartupPickProject.IsVisible = false;
            ScrollToEnd();
        }
        catch (ProjectTrustDeclinedException)
        {
            HideStartupOverlay();
            (DataContext as MainViewModel)?.Timeline.AddNotification(
                "未切换项目：未作出信任决定，已保留当前会话。");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            HideStartupOverlay();
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
            // 独立工作区是内部目录，不作为“最近项目”展示。
            RecentProjects: ProjectPreferences.LoadRecent(host.Manager.Metadata)
                .Where(path => !ProjectArchive.IsArchivedPath(host.Manager.Metadata, path) &&
                    !ScratchWorkspace.Matches(_options.CreateAppDataLayout(), path))
                .ToArray(),
            NewSessionBackends: BuildNewSessionBackendOptions(host),
            NewSessionWithBackendRequested: key =>
                SwitchSessionAsync(null, BackendCapabilities.Parse(key)));

        AttachSession(host.Chat, BuildSessionItems(host), callbacks,
            new RealPromptSteering(host), page);
        Core.Diagnostics.StartupProbe.Mark("session.viewmodel", "main view model attached");
        // 概览不是进入会话的必要内容：放到空闲优先级，等首帧与布局队列排空后再计算。
        Dispatcher.UIThread.Post(RenderOverview, DispatcherPriority.ApplicationIdle);
        Core.Diagnostics.StartupProbe.Mark("session.overview", "overview queued");
        var vm = (MainViewModel)DataContext!;
        vm.Timeline.LoadHistory(host.CurrentTranscript());
        Core.Diagnostics.StartupProbe.Mark("session.history", "timeline history replayed");
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

        _perfWiring?.AttachSession(host.Chat);
        _dialogHost!.Attach(host.Chat);
        var remoteService = Fufucode.Core.Plugins.PluginPackageManager.ActiveRemoteControlService;
        remoteService?.ConfigureSessionDirectory(BuildRemoteSessionCatalog, SwitchSessionFromRemoteAsync);
        remoteService?.Attach(host.Chat, host.ProjectPath);
        remoteService?.LoadHistory(host.CurrentTranscript());
        var fufuBridge = Fufucode.Core.Plugins.PluginPackageManager.ActiveFufuBridge;
        fufuBridge?.ConfigureSessionDirectory(BuildRemoteSessionCatalog, SwitchSessionFromRemoteAsync,
            SubmitTaskFromBridgeAsync);
        fufuBridge?.Attach(host.Chat, host.ProjectPath);
        fufuBridge?.LoadHistory(host.CurrentTranscript());
        if (!ReferenceEquals(_extensionUiWiredSession, host.Chat))
        {
            // 换会话/重建：先解除旧接线，避免旧时间线与输入框继续接收扩展事件。
            _extensionUiWiring?.Dispose();
            _extensionUiWiring = SessionUiWiring.AttachExtensionUi(host.Chat, vm.Timeline, vm.Input, vm.StatusBar,
                title => Title = $"Fufucode — {title}");
            _extensionUiWiredSession = host.Chat;
        }

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
        Core.Diagnostics.StartupProbe.Mark("session.attach.end", "dialogs, extensions and plugins wired");
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
                HideStartupOverlay();
                return failure;
            }

            AttachRealSession(preservePage: true);
            HideStartupOverlay();
            ScrollToEnd();
            _startupRetry = null;
            return null;
        }
        catch (ProjectTrustDeclinedException)
        {
            if (target.HasLiveSession)
            {
                // 信任预检在释放前抛出：旧会话仍然存活，仅提示并保留 pending。
                HideStartupOverlay();
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
                    HideStartupOverlay();
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
                HideStartupOverlay();
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

            HideStartupOverlay();
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
            // 没有名字的会话显示「新会话」，不再显示一串 id。
            var title = SessionTitles.Display(record);
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

    /// <summary>手机只读目录：跨项目的会话元信息；不含文件路径、令牌或时间线正文。</summary>
    private RemoteSessionCatalog BuildRemoteSessionCatalog()
    {
        if (_host is null)
            return new(string.Empty, []);

        try
        {
            var metadata = _host.Manager.Metadata;
            var currentId = _host.Current.Session.Id;
            var currentProjectPath = _host.ProjectPath;
            var turnActive = _vm?.Input.IsTurnActive == true;
            var archivedProjects = ProjectArchive.LoadSet(metadata);
            var projects = metadata.ListProjects();
            var trustedProjects = projects
                .Where(project => !RealSessionHost.PathEquals(project.Path, currentProjectPath))
                .Select(project => (
                    project.Id,
                    Trusted: System.IO.Directory.Exists(project.Path) &&
                        _host.Manager.GetProjectTrustStatus(project.Path).Decision == ProjectTrustDecision.Trust))
                .ToDictionary(item => item.Id, item => item.Trusted);
            var sessions = projects
                .SelectMany(project => metadata.ListSessions(project.Id).Select(record => (project, record)))
                .Where(pair => pair.record.Id == currentId ||
                    !SessionArchive.IsArchived(metadata, pair.record, archivedProjects))
                .Select(pair =>
                {
                    var presentation = SessionPresentation.Load(metadata, pair.record.Id);
                    var sameProject = RealSessionHost.PathEquals(pair.project.Path, currentProjectPath);
                    var canSwitch = !turnActive && pair.record.Id != currentId &&
                        (sameProject || trustedProjects.GetValueOrDefault(pair.project.Id));
                    var projectName = Path.GetFileName(pair.project.Path.TrimEnd(
                        Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                    return new RemoteSessionInfo(
                        pair.record.Id,
                        SessionTitles.Display(pair.record),
                        string.IsNullOrWhiteSpace(projectName) ? pair.project.Path : projectName,
                        BackendCapabilities.For(pair.record.Backend).Name,
                        pair.record.Id == currentId,
                        presentation.Pinned,
                        presentation.Unread,
                        canSwitch,
                        pair.record.LastOpened);
                })
                .OrderByDescending(item => item.IsPinned)
                .ThenByDescending(item => item.LastOpenedUtc)
                .Take(100)
                .ToArray();
            return new RemoteSessionCatalog(currentId, sessions);
        }
        catch (Exception)
        {
            return new(_host.Current.Session.Id, []);
        }
    }

    /// <summary>手机切换会话必须回到 Avalonia UI 线程执行；失败不会伪装成成功。</summary>
    private async Task<RemoteCommandResult> SwitchSessionFromRemoteAsync(
        string sessionId, CancellationToken cancellationToken)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            var operation = await Dispatcher.UIThread.InvokeAsync(
                () => SwitchSessionFromRemoteCoreAsync(sessionId, cancellationToken));
            return operation;
        }
        return await SwitchSessionFromRemoteCoreAsync(sessionId, cancellationToken);
    }

    private async Task<RemoteCommandResult> SwitchSessionFromRemoteCoreAsync(
        string sessionId, CancellationToken cancellationToken)
    {
        if (_closed || _host is null || _switching || _restarting)
            return new(false, "电脑端当前忙碌，暂时不能切换会话。");
        if (string.IsNullOrWhiteSpace(sessionId))
            return new(false, "会话 ID 为空。");
        if (_vm?.Input.IsTurnActive == true)
            return new(false, "电脑端当前回合进行中，请停止或等待完成后再切换。");
        if (_vm?.IsSettingsPage == true)
            return new(false, "请先在电脑端退出设置页。");

        try
        {
            var metadata = _host.Manager.Metadata;
            var target = metadata.GetSession(sessionId);
            var project = metadata.GetProject(target.ProjectId);
            if (!Directory.Exists(project.Path))
                return new(false, "目标项目文件夹不存在。");

            if (!RealSessionHost.PathEquals(project.Path, _host.ProjectPath))
            {
                var trust = _host.Manager.GetProjectTrustStatus(project.Path);
                if (trust.RequiresDecision || trust.Decision == ProjectTrustDecision.DoNotTrust)
                    return new(false, "目标项目需要先在电脑端确认信任。");
                await SwitchProjectAsync(project.Path);
            }

            if (_host is null || !RealSessionHost.PathEquals(_host.ProjectPath, project.Path))
                return new(false, "电脑端未能打开目标项目。");
            if (_host.Current.Session.Id != sessionId)
                await SwitchSessionAsync(sessionId);
            var accepted = _host is not null && _host.Current.Session.Id == sessionId;
            return accepted
                ? new(true, "已切换会话。")
                : new(false, "电脑端未能切换会话，请查看电脑端提示。");
        }
        catch (Exception error)
        {
            return new(false, "切换失败：" + error.GetBaseException().Message);
        }
    }

    /// <summary>FUFU 受控提交：目标会话若不是当前会话先切换，再把文本交给会话输入管线。</summary>
    private async Task<RemoteCommandResult> SubmitTaskFromBridgeAsync(
        string sessionId, string text, CancellationToken cancellationToken)
    {
        if (!Dispatcher.UIThread.CheckAccess())
            return await Dispatcher.UIThread.InvokeAsync(
                () => SubmitTaskFromBridgeAsync(sessionId, text, cancellationToken));
        if (_closed || _host is null)
            return new(false, "电脑端不可用。");
        if (string.IsNullOrWhiteSpace(text))
            return new(false, "提交内容为空。");
        try
        {
            if (_host.Current.Session.Id != sessionId)
            {
                var switched = await SwitchSessionFromRemoteCoreAsync(sessionId, cancellationToken);
                if (!switched.Accepted) return switched;
            }
            if (_host is null) return new(false, "会话未就绪。");
            await _host.Chat.SendAsync(text, cancellationToken);
            return new(true, "已提交。");
        }
        catch (Exception error)
        {
            return new(false, "提交失败：" + error.GetBaseException().Message);
        }
    }

    /// <summary>切换（或新建）会话：先落当前草稿，换会话后整体重建视图模型。</summary>
    private async Task SwitchSessionAsync(string? sessionId, BackendId? requestedBackend = null)
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
                (newBackend, backendNote) = ResolveNewSessionBackend(requestedBackend);
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
            HideStartupOverlay();
            ScrollToEnd();
        }
        catch (ProjectTrustDeclinedException)
        {
            // 信任决定被放弃：旧会话保持可用（RealSessionHost 先开后放），仅提示。
            HideStartupOverlay();
            if (DataContext is MainViewModel declined)
            {
                declined.Timeline.AddNotification("未作出信任决定，已保留当前会话。");
            }
        }
        catch (Exception ex)
        {
            // 切换失败：旧会话保持可用（RealSessionHost 先开后放），如实报告。
            HideStartupOverlay();
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
            // 用户手动改名后，自动取名不得再覆盖。
            var metadata = _host.Manager.Metadata;
            var id = _host.Current.Session.Id;
            SessionPresentation.Save(metadata, id,
                SessionPresentation.Load(metadata, id) with { NameUserSet = true, NameAutoAttempted = true });
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
        DetachDraftPersistence();

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
        // 局部函数在方法内即已确定，DetachDraftPersistence 解绑的是同一委托。
        void OnInputPropertyChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName is nameof(InputViewModel.DraftText) or nameof(InputViewModel.ReferenceContext))
            {
                timer.Stop();
                timer.Start();
            }
        }

        vm.Input.PropertyChanged += OnInputPropertyChanged;
        _draftDebounce = timer;
        _draftVm = vm;
    }

    /// <summary>解除草稿防抖：移除旧 VM 的输入订阅并停掉计时器（重建/关闭时调用）。</summary>
    private void DetachDraftPersistence()
    {
        if (_draftVm is not null) _draftVm.Input.PropertyChanged -= OnInputPropertyChanged;
        _draftVm = null;
        _draftDebounce?.Stop();
        _draftDebounce = null;
    }

    private void OnInputPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(InputViewModel.DraftText) or nameof(InputViewModel.ReferenceContext)
            && _draftDebounce is { } timer)
        {
            timer.Stop();
            timer.Start();
        }
    }

    private void ShowStartup(string message, bool inProgress)
    {
        StartupText.Text = message;
        StartupProgress.IsVisible = inProgress;
        // 失败态（非进行中）显示 interrupted 空态插画；进行中保持原标识。
        StartupArt.IsVisible = !inProgress;
        // 进行中态不显示“打开其他项目…”；失败态由各自 catch 分支决定。
        if (inProgress)
        {
            StartupOpenOtherButton.IsVisible = false;
        }

        StartupOverlay.Opacity = 1;
        StartupOverlay.IsVisible = true;
        if (inProgress) StartStartupAnimation(); else StopStartupAnimation();
    }

    private DispatcherTimer? _startupPulseTimer;
    private DispatcherTimer? _startupFadeTimer;

    /// <summary>
    /// 启动动画只做一次 900ms 的透明度呼吸，不跑逐帧布局：开销可忽略，也不会推迟会话打开。
    /// 系统开启“减少动画”或自测模式下直接静态显示。
    /// </summary>
    private void StartStartupAnimation()
    {
        if (Program.SelfTestMode || MotionPreference.ReducedMotion) return;
        if (StartupWordmark.Transitions is not { Count: > 0 })
            StartupWordmark.Transitions = new Transitions
            {
                new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(450), Easing = new CubicEaseOut() },
            };
        _startupPulseTimer ??= CreatePulseTimer();
        StartupWordmark.Opacity = 0.55;
        _startupPulseTimer.Start();
    }

    private DispatcherTimer CreatePulseTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        timer.Tick += (_, _) =>
            StartupWordmark.Opacity = StartupWordmark.Opacity > 0.7 ? 0.55 : 1.0;
        return timer;
    }

    private void StopStartupAnimation()
    {
        _startupPulseTimer?.Stop();
        StartupWordmark.Opacity = 1;
    }

    /// <summary>启动页淡出：180ms 透明度过渡期间不阻塞 UI；结束后从可视化树移除。</summary>
    private void HideStartupOverlay()
    {
        if (!StartupOverlay.IsVisible) return;
        StopStartupAnimation();
        if (Program.SelfTestMode || MotionPreference.ReducedMotion || _closed)
        {
            StartupOverlay.IsVisible = false;
            StartupOverlay.Opacity = 1;
            return;
        }
        if (StartupOverlay.Transitions is not { Count: > 0 })
            StartupOverlay.Transitions = new Transitions
            {
                new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(180), Easing = new CubicEaseOut() },
            };
        StartupOverlay.Opacity = 0;
        _startupFadeTimer ??= CreateFadeTimer();
        _startupFadeTimer.Stop();
        _startupFadeTimer.Start();
    }

    private DispatcherTimer CreateFadeTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            StartupOverlay.IsVisible = false;
            StartupOverlay.Opacity = 1;
        };
        return timer;
    }

    // ── 后台常驻（托盘） ──

    /// <summary>设置页写入的存储；真实模式为 host.Manager.Settings，mock 为进程级设置。</summary>
    private AppSettingsStore EffectiveSettingsStore =>
        _backgroundStore ?? (_settingsStore ??= App.SettingsStore);

    private bool CloseToTrayEnabled => EffectiveSettingsStore.Current.Background?.CloseToTray == true;

    /// <summary>托盘入口是否真实可用。创建失败时不隐藏窗口，避免用户失去恢复入口。</summary>
    private bool TrayAvailable => _trayIcon is not null || SimulateTrayAvailableForTesting;

    /// <summary>自测接缝：当前窗口生效的设置存储。</summary>
    internal AppSettingsStore BackgroundStoreForTesting => EffectiveSettingsStore;

    private static AppSettingsStore SelfTestSettingsStore(string root)
    {
        if (_selfTestSettingsStore is null || !string.Equals(_selfTestSettingsRoot, root, StringComparison.Ordinal))
        {
            _selfTestSettingsStore = new AppSettingsStore(new AppDataLayout(root));
            _selfTestSettingsRoot = root;
        }
        return _selfTestSettingsStore;
    }

    private void AttachBackgroundMode(AppSettingsStore store)
    {
        if (!ReferenceEquals(_backgroundStore, store))
        {
            if (_backgroundStore is not null) _backgroundStore.Changed -= OnBackgroundSettingsChanged;
            _backgroundStore = store;
            _backgroundStore.Changed += OnBackgroundSettingsChanged;
        }
        ApplyBackgroundMode();
    }

    private void OnBackgroundSettingsChanged(AppSettings settings)
    {
        // 设置存储也可能被后台代码更新；托盘是 UI 对象，统一切回 UI 线程。
        if (Dispatcher.UIThread.CheckAccess()) ApplyBackgroundMode();
        else Dispatcher.UIThread.Post(ApplyBackgroundMode);
    }

    /// <summary>应用运行时托盘图标常驻可见；关闭窗口时是退出还是最小化到托盘由 CloseToTrayEnabled 决定。</summary>
    private void ApplyBackgroundMode()
    {
        if (_closed) return;
        EnsureTrayIcon();
        if (_trayIcon is { } tray) tray.IsVisible = true;
        if (!CloseToTrayEnabled && _hiddenToTray) ShowFromTray();
    }

    private void EnsureTrayIcon()
    {
        if (_trayIcon is not null || _trayUnavailable) return;
        // 无头自测没有真实托盘实现；用 SimulateTrayAvailableForTesting 接缝验证行为。
        if (Program.SelfTestMode) return;
        try
        {
            _trayIcon = new TrayIconService(ShowFromTray, FillTrayMenu);
        }
        catch (Exception error)
        {
            _trayUnavailable = true;
            System.Diagnostics.Trace.TraceWarning("托盘图标创建失败：{0}", error.Message);
        }
    }

    /// <summary>
    /// 组装托盘右键菜单。NativeMenu 的 NeedsUpdate 会在每次打开前调用，
    /// 因此固定/最近会话、当前主题色和设置状态都不会变成启动时的旧快照。
    /// </summary>
    private void FillTrayMenu(NativeMenu menu)
    {
        AddTrayAction(menu, "显示主窗口", ShowFromTray);
        menu.Add(new NativeMenuItemSeparator());

        menu.Add(CreateTraySessionMenu("固定会话", pinned: true));
        menu.Add(CreateTraySessionMenu("最近会话", pinned: false));
        menu.Add(new NativeMenuItemSeparator());

        AddTrayAction(menu, "新建会话", () => _ = RunTrayActionAsync(async () =>
        {
            ShowFromTray();
            await NewChatAsync();
            await NavigateWorkspaceAsync(AppPage.Chat);
        }));
        AddTrayAction(menu, "关于 Fufucode", () => OpenFufucodeRepository());
        menu.Add(CreateTrayThemeMenu());
        AddTrayAction(menu, "设置", () => _ = RunTrayActionAsync(OpenSettingsFromTrayAsync));

        var fufu = new NativeMenuItem("召唤 FUFU（即将推出）") { IsEnabled = false };
        menu.Add(fufu);
        menu.Add(new NativeMenuItemSeparator());
        AddTrayAction(menu, "退出", ExitApplication);
    }

    internal void FillTrayMenuForTesting(NativeMenu menu) => FillTrayMenu(menu);

    private static void AddTrayAction(NativeMenu menu, string title, Action action)
    {
        var item = new NativeMenuItem(title);
        item.Click += (_, _) => action();
        menu.Add(item);
    }

    private NativeMenuItem CreateTraySessionMenu(string title, bool pinned)
    {
        var submenu = new NativeMenu();
        var sessions = TraySessions(pinned);
        if (sessions.Count == 0)
        {
            submenu.Add(new NativeMenuItem(pinned ? "暂无固定会话" : "暂无最近会话") { IsEnabled = false });
        }
        else
        {
            foreach (var session in sessions.Take(3))
            {
                var target = session;
                var item = new NativeMenuItem(TraySessionTitle(target));
                item.Click += (_, _) => _ = ActivateTraySessionAsync(target);
                submenu.Add(item);
            }
        }
        return new NativeMenuItem(title) { Menu = submenu };
    }

    private IReadOnlyList<SessionRecord> TraySessions(bool pinned)
    {
        if (_host is null) return [];
        var metadata = _host.Manager.Metadata;
        var archivedProjects = ProjectArchive.LoadSet(metadata);
        var sessions = metadata.ListProjects()
            .SelectMany(project => metadata.ListSessions(project.Id))
            .Where(record => !SessionArchive.IsArchived(metadata, record, archivedProjects))
            .Select(record => (Record: record, Presentation: SessionPresentation.Load(metadata, record.Id)))
            .Where(pair => pinned ? pair.Presentation.Pinned : !pair.Presentation.Pinned)
            .OrderByDescending(pair => pair.Record.LastOpened)
            .Select(pair => pair.Record)
            .Take(3)
            .ToArray();
        return sessions;
    }

    private string TraySessionTitle(SessionRecord session)
    {
        var title = SessionTitles.Display(session);
        if (_host is null) return title;
        try
        {
            var project = _host.Manager.Metadata.GetProject(session.ProjectId);
            var projectName = Path.GetFileName(project.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            return string.IsNullOrWhiteSpace(projectName) ? title : $"{title} · {projectName}";
        }
        catch (Exception) { return title; }
    }

    private NativeMenuItem CreateTrayThemeMenu()
    {
        var submenu = new NativeMenu();
        var accent = _customization?.Current.Accent ?? "system";
        AddTrayAccent(submenu, "默认色（跟随系统）", "system", accent);
        submenu.Add(new NativeMenuItemSeparator());
        AddTrayAccent(submenu, "Fufucode 蓝", "#0078D4", accent);
        AddTrayAccent(submenu, "青绿色", "#087E66", accent);
        AddTrayAccent(submenu, "紫色", "#7B51B5", accent);
        AddTrayAccent(submenu, "玫瑰红", "#B14670", accent);
        AddTrayAccent(submenu, "琥珀橙", "#B15B00", accent);
        AddTrayAccent(submenu, "中性灰", "#6B7075", accent);
        return new NativeMenuItem("主题色") { Menu = submenu };
    }

    private void AddTrayAccent(NativeMenu menu, string title, string value, string current)
    {
        var item = new NativeMenuItem(title)
        {
            IsChecked = string.Equals(current, value, StringComparison.OrdinalIgnoreCase),
            ToggleType = MenuItemToggleType.Radio,
        };
        item.Click += (_, _) => _customization?.Change(current => current with { Accent = value });
        menu.Add(item);
    }

    private async Task ActivateTraySessionAsync(SessionRecord session)
    {
        try
        {
            ShowFromTray();
            await ActivateOverviewSessionAsync(session);
            await NavigateWorkspaceAsync(AppPage.Chat);
        }
        catch (Exception error)
        {
            _vm?.Timeline.AddNotification("打开托盘会话失败：" + error.GetBaseException().Message);
        }
    }

    private async Task OpenSettingsFromTrayAsync()
    {
        ShowFromTray();
        await NavigateWorkspaceAsync(AppPage.Settings);
    }

    private async Task RunTrayActionAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception error) { _vm?.Timeline.AddNotification("托盘操作失败：" + error.GetBaseException().Message); }
    }

    private static void OpenFufucodeRepository()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://github.com/iron-water/Fufucode",
                UseShellExecute = true,
            });
        }
        catch (Exception) { }
    }

    /// <summary>只有用户主动关窗才隐藏；系统注销/关闭与显式退出仍走真实退出流程。</summary>
    private bool ShouldHideToTray(WindowCloseReason reason) =>
        !_closed && !_exitRequested &&
        reason is WindowCloseReason.WindowClosing or WindowCloseReason.Undefined &&
        CloseToTrayEnabled && TrayAvailable;

    private void HideToTray()
    {
        if (_hiddenToTray || _closed) return;
        _hiddenToTray = true;
        // 辅助栏窗口由主窗口拥有：Hide() 不会连带隐藏，逐个收起；恢复时由布局重新显示。
        foreach (var host in _floatingDocks.Values) host.Window.Hide();
        Hide();
    }

    /// <summary>托盘左键/菜单“显示主窗口”/第二个实例启动时恢复主窗口。</summary>
    internal void ShowFromTray()
    {
        if (_closed) return;
        _hiddenToTray = false;
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show();
        RenderFloatingWindows();
        Activate();
        // 进程外激活（第二个实例）时 Windows 可能拒绝前台切换：短暂置顶一次把窗口带到用户面前。
        var previous = Topmost;
        Topmost = true;
        Topmost = previous;
    }

    /// <summary>托盘“退出”：标记为显式退出，交给既有关闭流程释放会话与子进程。</summary>
    internal void ExitApplication()
    {
        if (_closed) return;
        _exitRequested = true;
        if (_hiddenToTray && _vm?.Settings.HasUnsavedChanges == true)
        {
            ShowFromTray();
        }
        if (_trayIcon is { } tray) tray.IsVisible = false;
        Close();
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeApproved)
        {
            return;
        }

        // 后台常驻：用户点关闭按钮 / Alt+F4 时只隐藏到托盘，进程、会话与子进程继续运行。
        if (ShouldHideToTray(e.CloseReason))
        {
            e.Cancel = true;
            HideToTray();
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
        // 语音服务归插件所有：窗口只解除状态广播订阅，进程由插件管理器在停用时释放。
        Fufucode.Core.Plugins.PluginStateNotifier.Changed -= OnPluginStateChanged;
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
        _pluginPageHost?.Dispose();
        DisposePerformance();
        foreach (var page in _workbenchPages.Values)
            foreach (var browser in page.Content.GetVisualDescendants().OfType<Views.BrowserViewHost>()) browser.Dispose();
        _workbenchPages.Dispose();
        _actions.Changed -= OnShortcutCapabilitiesChanged;
        _shortcuts.Changed -= UpdateShortcutLabels;
        _storageManager.Dispose();
        _localPluginViewModel?.Dispose();
        _libraryService?.Dispose();
        _palette.Dispose();
        _notifications.Dispose();
        if (_workspaceSession is not null) _workspaceSession.Event -= OnWorkspaceSessionEvent;
        _vm?.WorkspacePanel.Dispose();
        _vm?.Sidebar.Dispose();
        if (DataContext is MainViewModel vm && _host is not null)
        {
            _host.SaveDraft(vm.Input.ComposePrompt());
        }

        // 释放主 VM：解除草稿防抖、扩展接线、定制/全局处理器与时间线的会话订阅
        // （_vm 可能不是 DataContext 绑定的实例，须单独释放）。
        DetachDraftPersistence();
        DetachCustomizationSession();
        if (_vm is not null && _globalVmPropertyChanged is not null) _vm.PropertyChanged -= _globalVmPropertyChanged;
        _globalVmPropertyChanged = null;
        _extensionUiWiring?.Dispose();
        _extensionUiWiring = null;
        _extensionUiWiredSession = null;
        try { _vm?.Timeline.Dispose(); }
        catch
        {
            // 关闭路径尽力而为；异常不阻止退出。
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
        DisposeComputerCursorOverlay();
        _bootstrapCts.Dispose();
        if (_backgroundStore is not null)
        {
            _backgroundStore.Changed -= OnBackgroundSettingsChanged;
            _backgroundStore = null;
        }
        _trayIcon?.Dispose();
        _trayIcon = null;
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
