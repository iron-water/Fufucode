using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fufucode.App.Abstractions;
using Fufucode.App.Integration;
using Fufucode.Core;

namespace Fufucode.App.ViewModels;

/// <summary>启动失败记录：时间 + 消息，扩展页顶部 critical InfoBar 展示。</summary>
public sealed record StartupFailureRecord(DateTimeOffset When, string Message);

/// <summary>真实扩展目录的展示状态；None 表示有结果、尚未查询或正在查询。</summary>
public enum ExtensionEmptyState { None, NotInstalled, NoMatches, Failed }

/// <summary>
/// 扩展启停上下文（H4）：pending = 期望启用集合（下次重启生效）、active = 当前进程实际
/// 加载快照。由组合根提供持久化数据与"应用并重启"/文件选择回调。
/// </summary>
public sealed record ExtensionManageContext(
    IReadOnlyList<string> ActivePaths,
    IReadOnlyList<string> EnabledPaths,
    IReadOnlyList<string> AddedPaths,
    IReadOnlyList<string> MissingPaths,
    IReadOnlyDictionary<string, string> LoadErrors,
    Func<IReadOnlyList<string>, CancellationToken, Task<ExtensionApplyFailure?>> ApplyAsync,
    Func<CancellationToken, Task<string?>>? PickExtensionAsync,
    Action<string>? AddExtension,
    Action<string>? RemoveAddedExtension);

/// <summary>
/// 扩展页 VM（H3/M5）：真实会话下展示 get_commands 目录、磁盘发现结果、
/// 审批桥状态、项目信任与安全重启；mock 模式不造数据，只显示诚实空状态。
/// </summary>
public sealed partial class ExtensionsPageViewModel : ViewModelBase
{
    private ExtensionCatalog? _catalog;
    private IChatSession? _session;
    private IExtensionUiSession? _extensionUi;
    private Func<CancellationToken, Task>? _restartAsync;
    private Func<ProjectTrustStatus, Task<ProjectTrustDecision?>>? _askTrust;
    private Action<ProjectTrustStatus, ProjectTrustDecision>? _saveTrust;
    private Func<ProjectTrustStatus?>? _trustStatusSource;
    private Action<CommandEntry>? _fillCommand;
    private CancellationToken _sessionCancellation = CancellationToken.None;
    private bool _isMock = true;
    private bool _isRestarting;
    private StartupFailureRecord? _lastStartupFailure;
    private ProjectTrustStatus? _trustStatus;
    private bool _trustRestartPending;
    private string _commandFilter = string.Empty;
    private string _extensionFilter = string.Empty;
    private bool _hasSnapshot;
    private IReadOnlyList<CommandEntry> _allCommands = [];
    // H4 启停状态：pending 期望启用（下次启动生效）、active 当前进程实际加载。
    private ExtensionManageContext? _manage;
    private HashSet<string> _activePaths = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _pendingPaths = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _missingPaths = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _addedPaths = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _loadErrors = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<LoadedExtension> _loadedGroups = [];
    private IReadOnlyList<DiscoveredExtension> _discovered = [];
    private bool _previewInteractive;

    public ExtensionsPageViewModel()
    {
        Extensions.CollectionChanged += (_, _) => ApplyExtensionFilter();
    }

    /// <summary>真实会话接线：目录查询器、会话事件守卫、信任与重启回调由组合根注入。
    /// catalog 传 null 时（测试用）不发起 get_commands 查询。</summary>
    public void AttachRealSession(
        ExtensionCatalog? catalog,
        IChatSession chat,
        string projectPath,
        string agentDirectory,
        Func<CancellationToken, Task> restartAsync,
        Func<ProjectTrustStatus, Task<ProjectTrustDecision?>> askTrust,
        Action<ProjectTrustStatus, ProjectTrustDecision> saveTrust,
        Func<ProjectTrustStatus?> trustStatusSource,
        Action<CommandEntry> fillCommand,
        CancellationToken sessionCancellation = default,
        ExtensionManageContext? manage = null)
    {
        if (_session is not null)
        {
            _session.Event -= OnSessionEvent;
        }

        if (_extensionUi is not null)
        {
            _extensionUi.ExtensionUiRequestReceived -= OnExtensionUiGuardChanged;
            _extensionUi.ExtensionUiRequestClosed -= OnExtensionUiGuardChanged;
        }

        _catalog = catalog;
        _hasSnapshot = false;
        QueryError = null;
        _session = chat;
        _extensionUi = chat as IExtensionUiSession;
        _restartAsync = restartAsync;
        _askTrust = askTrust;
        _saveTrust = saveTrust;
        _trustStatusSource = trustStatusSource;
        _fillCommand = fillCommand;
        _sessionCancellation = sessionCancellation;
        _manage = manage;
        if (manage is not null)
        {
            _activePaths = new HashSet<string>(manage.ActivePaths, StringComparer.OrdinalIgnoreCase);
            // pending = 持久启用列表 ∩ 磁盘存在（缺失路径不进入启用集合，行显示"路径缺失"）。
            _pendingPaths = new HashSet<string>(
                manage.EnabledPaths.Where(p => !manage.MissingPaths.Contains(p, StringComparer.OrdinalIgnoreCase)),
                StringComparer.OrdinalIgnoreCase);
            _missingPaths = new HashSet<string>(manage.MissingPaths, StringComparer.OrdinalIgnoreCase);
            _addedPaths = new HashSet<string>(manage.AddedPaths, StringComparer.OrdinalIgnoreCase);
            _loadErrors = new Dictionary<string, string>(manage.LoadErrors,
                StringComparer.OrdinalIgnoreCase);
        }

        ProjectPath = projectPath;
        DiscoveryLocationsText = $"项目：{System.IO.Path.Combine(projectPath, ".pi", "extensions")} · " +
                                 $"全局：{System.IO.Path.Combine(agentDirectory, "extensions")}";
        OnPropertyChanged(nameof(DiscoveryLocationsText));
        IsMock = false;
        _session.Event += OnSessionEvent;
        if (_extensionUi is not null)
        {
            _extensionUi.ExtensionUiRequestReceived += OnExtensionUiGuardChanged;
            _extensionUi.ExtensionUiRequestClosed += OnExtensionUiGuardChanged;
        }

        RefreshTrustStatus();
        UpdateRestartGuard();
        NotifyEmptyStates();
        _ = RefreshAsync();
    }

    /// <summary>mock 模式：不制造任何扩展数据。</summary>
    public void AttachMock()
    {
        IsMock = true;
        _manage = null;
        _hasSnapshot = false;
        _loadedGroups = [];
        _discovered = [];
        _activePaths.Clear();
        _pendingPaths.Clear();
        _missingPaths.Clear();
        _addedPaths.Clear();
        _loadErrors.Clear();
        _previewInteractive = false;
        Extensions.Clear();
        Commands.Clear();
        FlyoutCommands.Clear();
        _allCommands = [];
        QueryError = null;
        UpdateRestartGuard();
        UpdatePendingChanges();
    }

    // ── 页面状态 ──

    public bool IsMock
    {
        get => _isMock;
        private set
        {
            if (SetProperty(ref _isMock, value))
            {
                OnPropertyChanged(nameof(CanUseCommands));
                OnPropertyChanged(nameof(CommandButtonToolTip));
                NotifyEmptyStates();
            }
        }
    }

    [ObservableProperty]
    private string _projectPath = string.Empty;

    /// <summary>mock 空状态文案：如实说明未连接真实 Pi。</summary>
    public string MockNotice => "mock 模式未连接真实 Pi，扩展信息不可用。";

    /// <summary>统一扩展列表：内置审批桥 + 已加载/已发现/手动添加/已启用路径。</summary>
    public ObservableCollection<ExtensionItemViewModel> Extensions { get; } = new();

    /// <summary>筛选后的展示集合；包含源集合中的同一行对象，不改写启停数据。</summary>
    public ObservableCollection<ExtensionItemViewModel> FilteredExtensions { get; } = new();

    public ObservableCollection<CommandItemViewModel> Commands { get; } = new();

    /// <summary>两个发现位置的绝对路径（供"未发现"说明行使用）。</summary>
    public string DiscoveryLocationsText { get; private set; } = string.Empty;

    public bool HasExtensions => Extensions.Count > 0;
    public bool HasCommands => Commands.Count > 0;

    public ExtensionEmptyState EmptyState => IsMock ? ExtensionEmptyState.None
        : HasQueryError ? ExtensionEmptyState.Failed
        : IsRefreshing || !_hasSnapshot ? ExtensionEmptyState.None
        : !HasExtensions ? ExtensionEmptyState.NotInstalled
        : FilteredExtensions.Count == 0 ? ExtensionEmptyState.NoMatches
        : ExtensionEmptyState.None;

    public bool HasExtensionEmptyState => EmptyState != ExtensionEmptyState.None;
    public bool ExtensionFilterHasNoMatches => EmptyState == ExtensionEmptyState.NoMatches;
    public string ExtensionEmptyTitle => EmptyState switch
    {
        ExtensionEmptyState.NotInstalled => "尚未安装扩展",
        ExtensionEmptyState.NoMatches => "没有匹配的扩展",
        ExtensionEmptyState.Failed => "扩展目录查询失败",
        _ => string.Empty,
    };

    public string ExtensionEmptyDescription => EmptyState switch
    {
        ExtensionEmptyState.NotInstalled => $"未发现扩展。发现位置：{DiscoveryLocationsText}。可用“添加扩展…”加入本地扩展，启用后等待安全重启。",
        ExtensionEmptyState.NoMatches => $"没有与“{ExtensionFilter.Trim()}”匹配的扩展。调整或清空筛选后可查看已有扩展。",
        ExtensionEmptyState.Failed => $"{QueryError}。扩展加载与命令信息尚未确认，请刷新重试。",
        _ => string.Empty,
    };

    public bool HasCommandEmptyState => !IsMock && _hasSnapshot && !IsRefreshing &&
                                        !HasQueryError && !HasCommands;
    public string CommandEmptyText => _allCommands.Count == 0
        ? "没有已注册的命令或技能。"
        : $"没有与“{CommandFilter.Trim()}”匹配的命令或技能。调整或清空筛选后可查看已注册条目。";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasQueryError))]
    private string? _queryError;

    partial void OnQueryErrorChanged(string? value) => NotifyEmptyStates();

    public bool HasQueryError => QueryError is not null;

    [ObservableProperty]
    private string? _queriedAtText;

    [ObservableProperty]
    private bool _isRefreshing;

    partial void OnIsRefreshingChanged(bool value) => NotifyEmptyStates();

    private void NotifyEmptyStates()
    {
        OnPropertyChanged(nameof(EmptyState));
        OnPropertyChanged(nameof(HasExtensionEmptyState));
        OnPropertyChanged(nameof(ExtensionFilterHasNoMatches));
        OnPropertyChanged(nameof(ExtensionEmptyTitle));
        OnPropertyChanged(nameof(ExtensionEmptyDescription));
        OnPropertyChanged(nameof(HasCommandEmptyState));
        OnPropertyChanged(nameof(CommandEmptyText));
    }

    // ── 启动失败 ──

    public StartupFailureRecord? LastStartupFailure
    {
        get => _lastStartupFailure;
        private set
        {
            if (SetProperty(ref _lastStartupFailure, value))
            {
                OnPropertyChanged(nameof(HasStartupFailure));
            }
        }
    }

    public bool HasStartupFailure => LastStartupFailure is not null;

    /// <summary>组合根在启动/重启失败（非信任、非取消）时记录；成功后可清除。</summary>
    public void SetStartupFailure(string? message)
    {
        LastStartupFailure = message is null
            ? null
            : new StartupFailureRecord(DateTimeOffset.Now, message);
    }

    // ── 信任 ──

    public ProjectTrustStatus? TrustStatus
    {
        get => _trustStatus;
        private set
        {
            if (SetProperty(ref _trustStatus, value))
            {
                OnPropertyChanged(nameof(TrustDecisionText));
                OnPropertyChanged(nameof(ProtectedResources));
                OnPropertyChanged(nameof(HasProtectedResources));
            }
        }
    }

    public string TrustDecisionText => TrustStatus?.Decision switch
    {
        ProjectTrustDecision.Trust => "已信任",
        ProjectTrustDecision.DoNotTrust => "不信任",
        _ => "未作出决定",
    };

    public IReadOnlyList<string> ProtectedResources => TrustStatus?.ProtectedResources ?? [];

    public bool HasProtectedResources => ProtectedResources.Count > 0;

    /// <summary>信任决定已更改但尚未重启时显示提示与"立即安全重启"按钮。</summary>
    public bool TrustRestartPending
    {
        get => _trustRestartPending;
        private set => SetProperty(ref _trustRestartPending, value);
    }

    /// <summary>测试/截图注入信任状态。</summary>
    public void PreviewTrustStatus(ProjectTrustStatus status) => TrustStatus = status;

    public void RefreshTrustStatus()
    {
        try
        {
            TrustStatus = _trustStatusSource?.Invoke();
        }
        catch (Exception)
        {
            TrustStatus = null;
        }
    }

    // ── 扩展与命令过滤 ──

    public string ExtensionFilter
    {
        get => _extensionFilter;
        set
        {
            if (SetProperty(ref _extensionFilter, value)) ApplyExtensionFilter();
        }
    }

    [RelayCommand]
    private void ClearExtensionFilter() => ExtensionFilter = string.Empty;

    private void ApplyExtensionFilter()
    {
        var filter = ExtensionFilter.Trim();
        FilteredExtensions.Clear();
        foreach (var item in Extensions)
        {
            if (filter.Length == 0 ||
                item.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                item.Path.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                item.ScopeText.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                item.StatusText.Contains(filter, StringComparison.OrdinalIgnoreCase))
                FilteredExtensions.Add(item);
        }

        OnPropertyChanged(nameof(HasExtensions));
        NotifyEmptyStates();
    }

    public string CommandFilter
    {
        get => _commandFilter;
        set
        {
            if (SetProperty(ref _commandFilter, value))
            {
                ApplyCommandFilter();
            }
        }
    }

    // ── 刷新 ──

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (_catalog is null)
        {
            return;
        }

        IsRefreshing = true;
        try
        {
            var snapshot = await _catalog.QueryAsync(_sessionCancellation);
            ApplySnapshot(snapshot);
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    /// <summary>测试/截图夹具注入：呈现一份快照而不经过真实会话。</summary>
    public void PreviewSnapshot(ExtensionCatalogSnapshot snapshot)
    {
        IsMock = false;
        ApplySnapshot(snapshot);
    }

    /// <summary>把快照落到可绑定集合（截图/自测可直接调用注入夹具快照）。</summary>
    public void ApplySnapshot(ExtensionCatalogSnapshot snapshot)
    {
        _hasSnapshot = true;
        _allCommands = snapshot.Commands;
        _loadedGroups = snapshot.LoadedExtensions;
        _discovered = snapshot.DiscoveredExtensions;
        _approvalAvailability = snapshot.ApprovalAvailability;
        _approvalDiagnostic = snapshot.ApprovalDiagnostic;

        QueryError = snapshot.QueryError;
        QueriedAtText = snapshot.QueriedAt == default
            ? null
            : $"上次刷新 {snapshot.QueriedAt.LocalDateTime:HH:mm:ss}";
        ApplyCommandFilter();
        RebuildExtensionRows();
    }

    // ── 扩展启停（H4）──

    private ApprovalAvailability? _approvalAvailability;
    private string? _approvalDiagnostic;

    /// <summary>预览/截图注入启停状态（不经过 host）。</summary>
    public void PreviewExtensionState(
        IReadOnlyList<string> activePaths, IReadOnlyList<string> pendingPaths,
        IReadOnlyList<string> addedPaths, IReadOnlyList<string> missingPaths,
        IReadOnlyDictionary<string, string>? loadErrors = null)
    {
        _activePaths = new HashSet<string>(activePaths, StringComparer.OrdinalIgnoreCase);
        _pendingPaths = new HashSet<string>(pendingPaths, StringComparer.OrdinalIgnoreCase);
        _addedPaths = new HashSet<string>(addedPaths, StringComparer.OrdinalIgnoreCase);
        _missingPaths = new HashSet<string>(missingPaths, StringComparer.OrdinalIgnoreCase);
        _loadErrors = new Dictionary<string, string>(loadErrors ?? new Dictionary<string, string>(),
            StringComparer.OrdinalIgnoreCase);
        IsMock = false;
        _previewInteractive = true;
        _hasSnapshot = true;
        RebuildExtensionRows();
    }

    /// <summary>待生效变更数（pending 与当前进程 active 的对称差）。</summary>
    public int PendingChangeCount { get; private set; }

    public bool HasPendingChanges => PendingChangeCount > 0;

    public string PendingChangesText => $"有 {PendingChangeCount} 项扩展变更，重启会话后生效";

    /// <summary>“添加扩展”失败提示（如未找到入口）。</summary>
    [ObservableProperty]
    private string? _addExtensionHint;

    public bool HasAddExtensionHint => AddExtensionHint is not null;

    private void RebuildExtensionRows()
    {
        Extensions.Clear();
        var bridge = _loadedGroups.FirstOrDefault(g => g.IsApprovalBridge);
        if (bridge is not null)
        {
            Extensions.Add(ExtensionItemViewModel.Bridge(bridge,
                _approvalAvailability, _approvalDiagnostic));
        }

        var bridgePath = bridge?.Path;
        var allPaths = _loadedGroups.Select(g => g.Path)
            .Concat(_discovered.Select(d => d.Path))
            .Concat(_addedPaths)
            .Concat(_pendingPaths)
            .Concat(_activePaths)
            .Concat(_missingPaths)
            .Concat(_loadErrors.Keys)
            .Where(p => p.Length > 0 &&
                        (bridgePath is null || !PathEq(p, bridgePath)))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var path in allPaths)
        {
            Extensions.Add(BuildRow(path));
        }

        OnPropertyChanged(nameof(HasExtensions));
        UpdatePendingChanges();
    }

    private ExtensionItemViewModel BuildRow(string path)
    {
        var pendingOn = _pendingPaths.Contains(path);
        var isActive = _activePaths.Contains(path);
        var missing = _missingPaths.Contains(path);
        _loadErrors.TryGetValue(path, out var loadError);

        var loadedGroup = _loadedGroups.FirstOrDefault(g => PathEq(g.Path, path));
        var discovered = _discovered.FirstOrDefault(d => PathEq(d.Path, path));
        var isAdded = _addedPaths.Contains(path);
        var scope = discovered?.Scope ??
                    (isAdded ? "manual" :
                     IsProjectLocal(path) ? "project" : "global");
        var isProjectScope = scope == "project";
        var needsTrust = isProjectScope && TrustStatus?.Decision != ProjectTrustDecision.Trust &&
                         pendingOn;

        // 状态优先级：路径缺失 > 待重启启停 > 已加载（active 压过残留错误记录）
        // > 加载失败（启用失败后回滚：未启用且带错误）> 未启用。
        var (statusText, statusKind, statusTip) =
            missing ? ("路径缺失", "attention", "该路径在磁盘上已不存在，本次启动已跳过")
            : pendingOn && !isActive ? ("待重启：启用", "attention", "安全重启会话后生效")
            : !pendingOn && isActive ? ("待重启：停用", "attention", "安全重启会话后生效")
            : pendingOn ? ("已加载", "success",
                "Pi 加载扩展失败会启动即退出，因此启动成功即代表已加载")
            : loadError is not null ? ("加载失败", "critical", loadError)
            : ("未启用", "neutral", "启用后在下次安全重启时加载");

        return new ExtensionItemViewModel(
            path, scope, isAdded,
            toggleOn: pendingOn && !missing,
            toggleEnabled: (_manage is not null || _previewInteractive) &&
                           !missing && !IsRestarting,
            statusText, statusKind, statusTip,
            loadedGroup?.Commands.Count ?? 0,
            loadError, needsTrust,
            onToggle: ToggleExtension,
            onRemove: _manage?.RemoveAddedExtension is null || !isAdded
                ? null
                : RemoveAddedExtension);
    }

    private bool IsProjectLocal(string path)
    {
        try
        {
            var root = System.IO.Path.GetFullPath(ProjectPath);
            var full = System.IO.Path.GetFullPath(path);
            return full.Equals(root, StringComparison.OrdinalIgnoreCase) ||
                   full.StartsWith(root + System.IO.Path.DirectorySeparatorChar,
                       StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool PathEq(string a, string b) =>
        string.Equals(a.TrimEnd(System.IO.Path.DirectorySeparatorChar,
                            System.IO.Path.AltDirectorySeparatorChar),
            b.TrimEnd(System.IO.Path.DirectorySeparatorChar,
                      System.IO.Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private void ToggleExtension(ExtensionItemViewModel item, bool enabled)
    {
        if (enabled)
        {
            _pendingPaths.Add(item.Path);
        }
        else
        {
            _pendingPaths.Remove(item.Path);
        }

        RebuildExtensionRows();
    }

    private void RemoveAddedExtension(string path)
    {
        _manage?.RemoveAddedExtension?.Invoke(path);
        _addedPaths.Remove(path);
        // 若已启用则同时计入待停用（等价于 pending 移除）。
        _pendingPaths.Remove(path);
        RebuildExtensionRows();
    }

    private void UpdatePendingChanges()
    {
        var all = _pendingPaths.Union(_activePaths, StringComparer.OrdinalIgnoreCase);
        PendingChangeCount = all.Count(p => _pendingPaths.Contains(p) != _activePaths.Contains(p));
        OnPropertyChanged(nameof(PendingChangeCount));
        OnPropertyChanged(nameof(HasPendingChanges));
        OnPropertyChanged(nameof(PendingChangesText));
        UpdateRestartGuard();
    }

    [RelayCommand]
    private void UndoChanges()
    {
        _pendingPaths = new HashSet<string>(_activePaths, StringComparer.OrdinalIgnoreCase);
        RebuildExtensionRows();
    }

    [RelayCommand]
    private async Task AddExtensionAsync()
    {
        if (_manage?.PickExtensionAsync is null)
        {
            return;
        }

        var path = await _manage.PickExtensionAsync(_sessionCancellation);
        if (path is null)
        {
            return;
        }

        if (!ExtensionCatalog.IsValidExtensionPath(path))
        {
            AddExtensionHint = "未找到扩展入口：目录需要 index.ts/index.js 或 package.json 的 pi.extensions 声明。";
            OnPropertyChanged(nameof(HasAddExtensionHint));
            return;
        }

        AddExtensionHint = null;
        OnPropertyChanged(nameof(HasAddExtensionHint));
        var full = System.IO.Path.GetFullPath(path);
        _manage.AddExtension?.Invoke(full);
        _addedPaths.Add(full);
        RebuildExtensionRows();
    }

    private void ApplyCommandFilter()
    {
        Refill(Commands, _commandFilter);
        OnPropertyChanged(nameof(HasCommands));
        NotifyEmptyStates();
        Refill(FlyoutCommands, _flyoutFilter);
    }

    private void Refill(ObservableCollection<CommandItemViewModel> target, string filterText)
    {
        target.Clear();
        var filter = filterText.Trim();
        foreach (var command in _allCommands)
        {
            if (filter.Length > 0 &&
                !command.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
                !command.Invocation.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
                !(command.Description?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false))
            {
                continue;
            }

            target.Add(new CommandItemViewModel(command, _fillCommand));
        }
    }

    // ── 输入区命令 Flyout（与页面共用目录数据，独立过滤词） ──

    public ObservableCollection<CommandItemViewModel> FlyoutCommands { get; } = new();

    private string _flyoutFilter = string.Empty;

    public string FlyoutFilter
    {
        get => _flyoutFilter;
        set
        {
            if (SetProperty(ref _flyoutFilter, value))
            {
                Refill(FlyoutCommands, _flyoutFilter);
            }
        }
    }

    /// <summary>命令入口可用性：mock 模式禁用（Flyout 按钮 Tooltip 说明）。</summary>
    public bool CanUseCommands => !IsMock;

    public string CommandButtonToolTip => IsMock ? "mock 模式未连接真实 Pi，命令列表不可用" : "插入命令或技能";

    // ── 安全重启 ──

    public bool CanRestart { get; private set; }

    public string RestartDisabledReason { get; private set; } = string.Empty;

    public bool HasRestartDisabledReason => !CanRestart && RestartDisabledReason.Length > 0;

    public string RestartButtonText => IsRestarting ? "正在重启…" : "安全重启会话";

    public bool IsRestarting
    {
        get => _isRestarting;
        private set
        {
            if (SetProperty(ref _isRestarting, value))
            {
                OnPropertyChanged(nameof(RestartButtonText));
                UpdateRestartGuard();
            }
        }
    }

    private void UpdateRestartGuard()
    {
        var pending = _extensionUi?.PendingExtensionUiRequests.Count ?? 0;
        var activity = _session?.Activity;
        var can = !IsMock && !IsRestarting && _restartAsync is not null &&
                  activity == SessionActivity.Idle && pending == 0;
        var reason =
            IsRestarting ? "重启进行中" :
            IsMock || _restartAsync is null ? "仅真实会话可重启" :
            pending > 0 ? "有待处理的扩展请求" :
            activity != SessionActivity.Idle ? "当前回合进行中" :
            string.Empty;
        CanRestart = can;
        RestartDisabledReason = reason;
        OnPropertyChanged(nameof(CanRestart));
        OnPropertyChanged(nameof(RestartDisabledReason));
        OnPropertyChanged(nameof(HasRestartDisabledReason));
        RestartCommand.NotifyCanExecuteChanged();
    }

    private void OnSessionEvent(object? sender, SessionEvent e)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnSessionEvent(sender, e));
            return;
        }

        UpdateRestartGuard();
    }

    private void OnExtensionUiGuardChanged(ExtensionUiRequest _)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => UpdateRestartGuard());
            return;
        }

        UpdateRestartGuard();
    }

    [RelayCommand(CanExecute = nameof(CanRestart))]
    private async Task RestartAsync()
    {
        if (_restartAsync is null || IsRestarting)
        {
            return;
        }

        IsRestarting = true;
        try
        {
            if (HasPendingChanges && _manage is not null)
            {
                // 期望启用集合 = pending ∪ 缺失路径（缺失路径保留在持久启用列表，
                // host 落盘完整列表、实际启动时再过滤）。
                var pending = _pendingPaths
                    .Union(_missingPaths, StringComparer.OrdinalIgnoreCase).ToArray();
                var failure = await _manage.ApplyAsync(pending, _sessionCancellation);
                if (failure is not null)
                {
                    // 应用失败已回滚到旧启用列表：pending 回到 active，行内显示最近错误。
                    _loadErrors[failure.ExtensionPath] = failure.Message;
                    _pendingPaths = new HashSet<string>(_activePaths,
                        StringComparer.OrdinalIgnoreCase);
                    RebuildExtensionRows();
                }
                else
                {
                    // 成功：active = 实际加载（missing 未进入进程）。
                    _activePaths = new HashSet<string>(
                        pending.Where(path => !_missingPaths.Contains(path)),
                        StringComparer.OrdinalIgnoreCase);
                    foreach (var path in pending)
                    {
                        _loadErrors.Remove(path);
                    }

                    RebuildExtensionRows();
                }
            }
            else
            {
                await _restartAsync(_sessionCancellation);
            }

            TrustRestartPending = false;
        }
        catch (ProjectTrustDeclinedException)
        {
            // 信任决定被放弃：pending 保持，行状态不变。
            RebuildExtensionRows();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SetStartupFailure(ex.GetBaseException().Message);
        }
        finally
        {
            IsRestarting = false;
        }
    }

    // ── 信任更改 ──

    [RelayCommand]
    private async Task ChangeTrustAsync()
    {
        if (_askTrust is null || _saveTrust is null || TrustStatus is null)
        {
            return;
        }

        var decision = await _askTrust(TrustStatus);
        if (decision is null)
        {
            return;
        }

        _saveTrust(TrustStatus, decision.Value);
        RefreshTrustStatus();
        TrustRestartPending = true;
    }
}

/// <summary>统一扩展行（H4）：内置桥 / 已加载 / 待重启 / 未启用 / 路径缺失 / 加载失败。</summary>
public sealed class ExtensionItemViewModel : ViewModelBase
{
    private bool _isPendingEnabled;

    public ExtensionItemViewModel(
        string path, string scope, bool isAdded, bool toggleOn, bool toggleEnabled,
        string statusText, string statusKind, string statusTip, int commandCount,
        string? loadError, bool needsTrust,
        Action<ExtensionItemViewModel, bool>? onToggle,
        Action<string>? onRemove)
    {
        Path = path;
        Name = System.IO.Path.GetFileName(
            path.TrimEnd(System.IO.Path.DirectorySeparatorChar,
                System.IO.Path.AltDirectorySeparatorChar));
        ScopeText = scope switch
        {
            "project" => "项目",
            "global" => "全局",
            "builtin" => "内置",
            _ => "手动添加",
        };
        IsAdded = isAdded;
        _isPendingEnabled = toggleOn;
        ToggleIsEnabled = toggleEnabled;
        StatusText = statusText;
        StatusKind = statusKind;
        StatusTip = statusTip;
        CommandCountText = $"{commandCount} 个命令";
        LoadError = loadError;
        HasLoadError = loadError is not null;
        ShowTrustWarning = needsTrust;
        _onToggle = onToggle;
        if (onRemove is not null)
        {
            RemoveCommand = new RelayCommand(() => onRemove(path));
        }
    }

    /// <summary>内置审批桥行：开关常开且禁用，附审批状态 chip。</summary>
    public static ExtensionItemViewModel Bridge(LoadedExtension extension,
        ApprovalAvailability? availability, string? diagnostic)
    {
        // 运行时错误（ExtensionError 事件）在桥行同样展示。
        var runtimeError = extension.RecentErrors.Count > 0
            ? string.Join("\n", extension.RecentErrors.Select(error =>
                $"{error.When.LocalDateTime:HH:mm:ss} · {error.EventName}：{error.Error}"))
            : null;
        return new ExtensionItemViewModel(
            extension.Path, "builtin", isAdded: false,
            toggleOn: true, toggleEnabled: false,
            statusText: "已加载", statusKind: "success",
            statusTip: "审批桥始终显式加载，不可停用",
            commandCount: extension.Commands.Count,
            loadError: runtimeError, needsTrust: false,
            onToggle: null, onRemove: null)
        {
            IsApprovalBridge = true,
            ApprovalChipText = availability switch
            {
                Core.ApprovalAvailability.Verified => "审批桥：已验证",
                Core.ApprovalAvailability.Unverified => "审批桥：未验证",
                Core.ApprovalAvailability.Unavailable => "审批桥：不可用",
                _ => string.Empty,
            },
            ApprovalChipIsSuccess = availability == Core.ApprovalAvailability.Verified,
            ApprovalDiagnostic = diagnostic,
        };
    }

    private readonly Action<ExtensionItemViewModel, bool>? _onToggle;

    public string Path { get; }
    public string Name { get; }
    public string ScopeText { get; }
    public bool IsAdded { get; }

    /// <summary>ToggleSwitch 绑定值：期望启用（pending），改动只进待重启集合。</summary>
    public bool IsPendingEnabled
    {
        get => _isPendingEnabled;
        set
        {
            if (SetProperty(ref _isPendingEnabled, value))
            {
                _onToggle?.Invoke(this, value);
            }
        }
    }

    public bool ToggleIsEnabled { get; }
    public string StatusText { get; }

    /// <summary>badge 样式后缀：success / attention / critical / neutral。</summary>
    public string StatusKind { get; }
    public string StatusTip { get; }
    public bool StatusIsSuccess => StatusKind == "success";
    public bool StatusIsAttention => StatusKind == "attention";
    public bool StatusIsCritical => StatusKind == "critical";
    public string CommandCountText { get; }
    public string? LoadError { get; private set; }
    public bool HasLoadError { get; private set; }
    public bool ShowTrustWarning { get; }
    public bool IsApprovalBridge { get; private set; }
    public string ApprovalChipText { get; private set; } = string.Empty;
    public bool ApprovalChipIsSuccess { get; private set; }
    public string? ApprovalDiagnostic { get; private set; }
    public IRelayCommand? RemoveCommand { get; }
}

/// <summary>命令/技能行。</summary>
public sealed class CommandItemViewModel : ViewModelBase
{
    public CommandItemViewModel(CommandEntry entry, Action<CommandEntry>? fill)
    {
        Entry = entry;
        Invocation = entry.Invocation;
        Description = entry.Description ?? string.Empty;
        SourcePath = entry.SourcePath ?? string.Empty;
        SourceBadgeText = entry.Source switch
        {
            "extension" => "扩展",
            "prompt" => "提示词",
            "skill" => "技能",
            _ => entry.Source,
        };
        IsSkill = entry.Source == "skill";
        IsExtension = entry.Source == "extension";
        if (fill is not null)
        {
            FillCommand = new RelayCommand(() => fill(entry));
        }
    }

    public CommandEntry Entry { get; }
    public string Invocation { get; }
    public string Description { get; }
    public string SourcePath { get; }
    public string SourceBadgeText { get; }
    public bool IsSkill { get; }
    public bool IsExtension { get; }
    public IRelayCommand? FillCommand { get; }
}
