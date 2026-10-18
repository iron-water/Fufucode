using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fufucode.App.ViewModels;
using Fufucode.Core;
using Fufucode.Core.Features.LocalReview;

namespace Fufucode.App.Features.LocalReview;

/// <summary>
/// 本地 Git 工作树与审查页面的视图模型（自包含模块，未接共享外壳）。
/// 边界：
/// - 项目路径、数据根与执行入口由宿主注入；模块只写自己的数据目录，绝不写 desktop.db；
/// - Git/文件 IO 一律离开 UI 线程；
/// - 取消只作用于本模块自己的运行；宿主 Detach 会停止运行，普通停靠移动不会；
/// - 切换项目立即失效上一次调用的结果（不串上下文）；
/// - 审查只读：不改文件、不自动接受/集成；打开、跳转、导出、复制只是宿主请求。
/// </summary>
public sealed partial class LocalReviewViewModel : ViewModelBase, IDisposable
{
    private readonly ILocalReviewExecutor? _executor;
    private readonly LocalReviewHistory? _history;
    private readonly CancellationTokenSource _lifetime = new();
    private LocalReviewScopeResolution? _scope;
    private LocalReviewRunResult? _result;
    private LocalReviewExport.ReviewDocument? _historyDocument;
    private IReadOnlyList<LocalReviewFinding> _allFindings = [];
    private CancellationTokenSource? _runCts;
    private int _runGeneration;
    private int _opSeq;
    private bool _disposed;
    private bool _running;

    public LocalReviewViewModel(string? dataRoot, ILocalReviewExecutor? executor = null,
        ILocalReviewHostRequests? host = null)
    {
        _executor = executor;
        Host = host;
        _history = dataRoot is null ? null : new LocalReviewHistory(Path.Combine(dataRoot, "history"));
        // 初始选中项必须是集合内的同一实例，否则 ComboBox 无法命中。
        SelectedScope = ScopeChoices[0];
    }

    public ILocalReviewHostRequests? Host { get; set; }

    public void RefreshExecutorContext() => OnPropertyChanged(nameof(ExecutorNote));

    public ObservableCollection<LocalReviewScopeChoice> ScopeChoices { get; } =
    [
        new LocalReviewScopeChoice(LocalReviewScopeKind.Workspace, "工作区差异（相对 HEAD，含未跟踪）"),
        new LocalReviewScopeChoice(LocalReviewScopeKind.Staged, "暂存差异（相对 HEAD）"),
        new LocalReviewScopeChoice(LocalReviewScopeKind.Commit, "指定提交（相对其第一父提交）"),
        new LocalReviewScopeChoice(LocalReviewScopeKind.Range, "基准..目标提交范围"),
    ];

    public ObservableCollection<LocalReviewWorktreeItem> Worktrees { get; } = [];
    public ObservableCollection<LocalReviewFindingItem> Findings { get; } = [];
    public ObservableCollection<LocalReviewRejectedItem> Rejected { get; } = [];
    public ObservableCollection<string> History { get; } = [];

    [ObservableProperty] private string _projectPath = string.Empty;
    [ObservableProperty] private LocalReviewScopeChoice? _selectedScope;
    [ObservableProperty] private string _revision = string.Empty;
    [ObservableProperty] private string _baseRevision = string.Empty;
    [ObservableProperty] private string _createDestination = string.Empty;
    [ObservableProperty] private LocalReviewWorktreeItem? _selectedWorktree;
    [ObservableProperty] private LocalReviewFindingItem? _selectedFinding;
    [ObservableProperty] private string _statusText = "尚未选择项目。";
    [ObservableProperty] private string _scopeSummary = string.Empty;
    [ObservableProperty] private string _staleWarning = string.Empty;
    [ObservableProperty] private string _priorityFilter = "全部";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _canCancel;
    [ObservableProperty] private bool _hasResult;
    [ObservableProperty] private string _exportedPath = string.Empty;

    // 实例属性：XAML 编译绑定不解析静态成员。
    public IReadOnlyList<string> PriorityFilters { get; } = ["全部", "Critical", "High", "Medium", "Low"];

    public bool PathValid => !string.IsNullOrWhiteSpace(ProjectPath);
    public bool HasExecutor => _executor is not null;
    public bool CanReview => !IsBusy && HasExecutor && SelectedScope is not null && PathValid;
    public bool CanCreateWorktree => !IsBusy && PathValid && !string.IsNullOrWhiteSpace(CreateDestination);
    public bool CanExport => HasResult && Findings.Count + Rejected.Count > 0;
    public string ExecutorNote => HasExecutor
        ? "执行入口：" + _executor!.Model
        : "尚未接线执行入口（宿主需注入 ILocalReviewExecutor；Git/工作树步骤仍可用）。";

    partial void OnProjectPathChanged(string value)
    {
        // 项目切换：停掉本模块进行中的运行并清空上一项目结果，避免跨项目串上下文。
        // 序号自增让所有在途调用的收尾自动失效，忙态在此处直接归位，不会卡住。
        CancelRunningRun();
        _opSeq++;
        IsBusy = false;
        CanCancel = false;
        ClearResult();
        _scope = null;
        OnPropertyChanged(nameof(PathValid));
        OnPropertyChanged(nameof(CanReview));
        OnPropertyChanged(nameof(CanCreateWorktree));
    }

    partial void OnSelectedScopeChanged(LocalReviewScopeChoice? value)
    {
        OnPropertyChanged(nameof(CanReview));
        // 换范围后上一次解析结果不再适用。
        _scope = null;
        ScopeSummary = string.Empty;
        if (value?.Kind is LocalReviewScopeKind.Commit or LocalReviewScopeKind.Range)
            StatusText = "提交/范围需填入基准后使用“解析范围”；合并提交请改用显式范围。";
    }

    partial void OnCreateDestinationChanged(string value) => OnPropertyChanged(nameof(CanCreateWorktree));

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanReview));
        OnPropertyChanged(nameof(CanCreateWorktree));
    }

    partial void OnPriorityFilterChanged(string value) => ApplyFilter();
    partial void OnHasResultChanged(bool value) => OnPropertyChanged(nameof(CanExport));
    partial void OnSelectedFindingChanged(LocalReviewFindingItem? value) { }
    partial void OnSelectedWorktreeChanged(LocalReviewWorktreeItem? value) { }

    /// <summary>宿主设置当前项目：只登记路径，不自动运行任何 Git 动作。</summary>
    public void SetProject(string? projectPath)
    {
        ProjectPath = projectPath ?? string.Empty;
        StatusText = PathValid
            ? "项目已选择：" + ProjectPath + "（只读检查，尚未解析范围）"
            : "尚未选择项目。";
    }

    /// <summary>宿主注入执行入口后刷新可见说明。</summary>
    public bool HasDataRoot => _history is not null;

    [RelayCommand]
    public async Task RefreshWorktreesAsync()
    {
        if (_disposed || !PathValid) { if (!_disposed) StatusText = "请先选择项目。"; return; }
        var project = ProjectPath;
        var op = ++_opSeq;
        IsBusy = true;
        try
        {
            var listing = await Task.Run(() => GitWorktreeService.ListAsync(project, LifetimeToken), LifetimeToken);
            if (IsStaleCall(project)) return;
            Worktrees.Clear();
            if (listing.Reason != GitWorktreeInspectReason.Ok)
            {
                StatusText = "工作树列表不可用：" + (listing.Error ?? listing.Reason.ToString());
                return;
            }
            foreach (var worktree in listing.Worktrees) Worktrees.Add(new LocalReviewWorktreeItem(worktree));
            SelectedWorktree ??= Worktrees.FirstOrDefault();
            StatusText = $"共 {Worktrees.Count} 个工作树（只读列表，未做任何移除）。";
        }
        catch (OperationCanceledException) { if (!_disposed) StatusText = "已取消工作树列表读取。"; }
        catch (Exception error) { if (!_disposed) StatusText = "工作树列表失败：" + error.Message; }
        finally { if (op == _opSeq && !_disposed) IsBusy = false; }
    }

    [RelayCommand]
    public async Task CreateWorktreeAsync()
    {
        if (_disposed || !CanCreateWorktree) return;
        var project = ProjectPath;
        var destination = CreateDestination;
        var op = ++_opSeq;
        IsBusy = true;
        try
        {
            var created = await Task.Run(() => GitWorktreeService.CreateAsync(project, destination, LifetimeToken),
                LifetimeToken);
            if (IsStaleCall(project)) return;
            var notice = $"已创建：{created.Path}（分支 {created.Branch}，基于 {Shorten(created.SourceCommit)}）。" +
                "新目录未被自动信任，当前会话未切换。";
            CreateDestination = string.Empty;
            await RefreshWorktreesAsync();
            // 刷新列表会改写状态行；创建结果的关键提示（不自动信任/未切换会话）必须保留。
            if (!IsStaleCall(project))
                StatusText = notice + $"（工作树列表共 {Worktrees.Count} 个）";
        }
        catch (OperationCanceledException) { if (!_disposed) StatusText = "已取消创建。"; }
        catch (Exception error) { if (!_disposed) StatusText = "创建失败：" + error.Message; }
        finally { if (op == _opSeq && !_disposed) IsBusy = false; }
    }

    [RelayCommand]
    public void OpenSelectedWorktree()
    {
        if (_disposed) return;
        if (SelectedWorktree is null) { StatusText = "未选择工作树。"; return; }
        if (SelectedWorktree.Prunable)
        {
            StatusText = "该工作树目录已缺失：本模块不做强制移除，请你在磁盘确认后自行处理。";
            return;
        }
        if (Host is null) { StatusText = "打开请求未接线（宿主需实现 ILocalReviewHostRequests.OpenWorktree）。"; return; }
        Host.OpenWorktree(SelectedWorktree.Path);
        StatusText = "已发出打开请求：" + SelectedWorktree.Path;
    }

    [RelayCommand]
    public async Task ResolveScopeAsync()
    {
        if (_disposed) return;
        if (!PathValid || SelectedScope is null) { StatusText = "请先选择项目与范围。"; return; }
        var project = ProjectPath;
        var kind = SelectedScope.Kind;
        var revision = Revision.Trim();
        var baseRevision = BaseRevision.Trim();
        var op = ++_opSeq;
        IsBusy = true;
        try
        {
            var scope = await Task.Run(() => LocalReviewScopeResolver.ResolveAsync(project, kind,
                string.IsNullOrWhiteSpace(revision) ? null : revision,
                string.IsNullOrWhiteSpace(baseRevision) ? null : baseRevision, LifetimeToken), LifetimeToken);
            if (IsStaleCall(project)) return;
            _scope = scope;
            ScopeSummary = Describe(scope);
            StatusText = scope.Status switch
            {
                LocalReviewScopeStatus.Resolved =>
                    $"范围有效：{scope.Files.Count} 个文件。{FreezeNote(scope)}",
                LocalReviewScopeStatus.EmptyScope => "范围没有变更内容：" + (scope.Error ?? "无文件"),
                LocalReviewScopeStatus.MergeCommitUnsupported =>
                    "合并提交不默认第一父：请改用显式 base..target 范围。",
                LocalReviewScopeStatus.InvalidRevision => "基准/提交无效：" + scope.Error,
                _ => "范围解析失败：" + (scope.Error ?? scope.Status.ToString()),
            };
        }
        catch (OperationCanceledException) { if (!_disposed) StatusText = "已取消范围解析。"; }
        catch (Exception error) { if (!_disposed) StatusText = "范围解析异常：" + error.Message; }
        finally { if (op == _opSeq && !_disposed) IsBusy = false; }
    }

    [RelayCommand]
    public async Task ReviewAsync()
    {
        // 释放后不得再启动运行，也不得重建取消令牌源。
        if (_disposed || !CanReview || _executor is null)
        {
            if (!_disposed && !HasExecutor) StatusText = "执行入口未接线，无法开始真实审查。";
            return;
        }
        var project = ProjectPath;
        if (_scope is null || _scope.Status != LocalReviewScopeStatus.Resolved ||
            !string.Equals(_scope.ProjectPath, Path.GetFullPath(project), StringComparison.OrdinalIgnoreCase))
        {
            await ResolveScopeAsync();
            if (_disposed) return;
        }
        if (_scope is null || _scope.Status != LocalReviewScopeStatus.Resolved) return;
        if (_scope.SnapshotCapability != LocalReviewSnapshotCapability.FullSnapshot)
        {
            // 精确降级禁止：不拿工作区快照冒充暂存/提交/范围审查。
            StatusText = "此范围暂不能精确冻结：" +
                LocalReviewScopeResolver.MissingSharedApiFor(_scope.Kind) +
                " 已按任务书提交协调卡请求共享快照 API 扩展；本次未降级为工作区审查。";
            return;
        }

        CancelRunningRun();
        var runCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _runCts = runCts;
        var runToken = runCts.Token;
        var generation = ++_runGeneration;
        var op = ++_opSeq;
        CanCancel = true;
        _running = true;
        IsBusy = true;
        ClearResult();
        var scope = _scope;
        try
        {
            var result = await _executor.ReviewAsync(scope, runToken);
            if (IsStaleCall(project) || _disposed || generation != _runGeneration) return;
            _result = result;
            ShowResult(result);
            if (_history is not null)
            {
                try
                {
                    var fileName = _history.Save(result);
                    History.Insert(0, fileName);
                }
                catch (Exception error) { StatusText += "｜历史写入失败：" + error.Message; }
            }
        }
        catch (OperationCanceledException)
        {
            if (!_disposed && generation == _runGeneration)
                StatusText = "已取消审查：本次运行结果未知，重发前需对账（不会自动重发）。";
        }
        catch (Exception error)
        {
            if (!_disposed && generation == _runGeneration) StatusText = "审查异常终止：" + error.Message;
        }
        finally
        {
            runCts.Dispose();
            if (ReferenceEquals(_runCts, runCts)) _runCts = null;
            // 只有仍是最新的操作才回收状态；被替换/切换项目后的旧运行不得清掉新操作的忙态。
            if (op == _opSeq && generation == _runGeneration && !_disposed)
            {
                _running = false;
                IsBusy = false;
                CanCancel = false;
            }
        }
    }

    [RelayCommand]
    public void Cancel()
    {
        if (_disposed) return;
        if (!_running) { StatusText = "当前没有进行中的审查。"; return; }
        _runCts?.Cancel();
        StatusText = "取消已发出：等待运行进入未知状态标记（不会自动重发）。";
    }

    [RelayCommand]
    public async Task JumpAsync()
    {
        if (_disposed) return;
        if (SelectedFinding is null) { StatusText = "未选择发现项。"; return; }
        if (_historyDocument is not null) { StatusText = "历史仅保存发现证据，未保存可复核字节；不能定位当前文件。"; return; }
        if (_result?.Snapshot is null) { StatusText = "没有可用的固定快照。"; return; }
        var result = _result;
        var finding = SelectedFinding;
        bool matches;
        try { matches = !result.IsStale && await result.Snapshot.MatchesWorkingTreeAsync(_lifetime.Token); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
        if (!matches)
        {
            StatusText = "当前文件与固定快照不同，已阻止使用旧行号跳转；请查看发现证据或重新审查当前范围。";
            return;
        }
        if (_disposed || !ReferenceEquals(_result, result) || !ReferenceEquals(SelectedFinding, finding)) return;
        var root = _result.Snapshot.RepositoryRoot;
        if (string.IsNullOrEmpty(root)) root = _result.Scope.ProjectPath;
        var absolute = Path.GetFullPath(Path.Combine(root, SelectedFinding.Finding.Path));
        if (Host is null)
        {
            StatusText = "跳转请求未接线（宿主需实现编辑器定位）：" + absolute + ":" + SelectedFinding.Finding.Line;
            return;
        }
        Host.JumpToLocation(absolute, SelectedFinding.Finding.Line);
        StatusText = _result.IsStale
            ? "已请求跳转，但快照已过期：行号来自固定快照，可能不匹配当前内容。"
            : "已请求跳转：" + SelectedFinding.Location;
    }

    [RelayCommand]
    public async Task CopyFindingsAsync()
    {
        if (_disposed) return;
        if (_result is null && _historyDocument is null) { StatusText = "没有可复制的结果。"; return; }
        if (Host is null) { StatusText = "复制未接线（宿主需提供剪贴板）。"; return; }
        var text = _historyDocument is { } history ? LocalReviewExport.ToMarkdown(history) : LocalReviewExport.ToMarkdown(_result!);
        await Host.CopyToClipboardAsync(text);
        StatusText = "已复制 Markdown 结果（含范围与快照状态）。";
    }

    [RelayCommand]
    public async Task ExportAsync(string? format)
    {
        if (_disposed) return;
        if (_result is null && _historyDocument is null) { StatusText = "没有可导出的结果。"; return; }
        if (Host is null) { StatusText = "导出未接线（宿主需提供保存路径选择）。"; return; }
        var json = string.Equals(format, "json", StringComparison.OrdinalIgnoreCase);
        var document = _historyDocument ?? LocalReviewExport.ToDocument(_result!);
        var content = json ? LocalReviewExport.ToJson(document) : LocalReviewExport.ToMarkdown(document);
        var suggested = _result is null ? "review-history" + (json ? ".json" : ".md") :
            LocalReviewExport.SuggestedFileName(_result, json ? ".json" : ".md");
        var path = await Host.PickExportPathAsync(suggested);
        if (string.IsNullOrWhiteSpace(path)) { StatusText = "已取消导出。"; return; }
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(path, content, new UTF8Encoding(false));
            ExportedPath = path;
            StatusText = "已导出：" + path;
        }
        catch (Exception error) { StatusText = "导出失败：" + error.Message; }
    }

    [RelayCommand]
    public void LoadHistory(string? fileName)
    {
        if (_disposed) return;
        if (_history is null || string.IsNullOrWhiteSpace(fileName))
        {
            StatusText = _history is null ? "历史未接线（宿主需注入模块数据根）。" : "未选择历史条目。";
            return;
        }
        var document = _history.Load(fileName!);
        if (document is null) { StatusText = "历史条目不可读：" + fileName; return; }
        _result = null;
        _historyDocument = document;
        SelectedFinding = null;
        ScopeSummary = $"{document.Scope}｜快照 {Shorten(document.HeadCommit)}｜{document.Status}｜历史文件 {fileName}";
        StaleWarning = "历史记录的状态仅对应保存时刻；当前文件定位未验证。";
        Findings.Clear();
        Rejected.Clear();
        foreach (var finding in document.Findings)
            Findings.Add(new LocalReviewFindingItem(new LocalReviewFinding(
                Enum.TryParse<LocalReviewPriority>(finding.Priority, true, out var p)
                    ? p : LocalReviewPriority.Low,
                finding.Path, finding.Line, finding.Explanation, finding.Evidence), document.Stale));
        foreach (var rejected in document.Rejected)
            Rejected.Add(new LocalReviewRejectedItem(new LocalReviewParseIssue(rejected, null, null, null)));
        _allFindings = [.. Findings.Select(item => item.Finding)];
        HasResult = true;
        StatusText = $"读回历史：{document.Findings.Count} 项发现（只读，不重新审查）。";
    }

    [RelayCommand]
    public void ReloadHistory()
    {
        if (_disposed) return;
        History.Clear();
        if (_history is null) { StatusText = "历史未接线（宿主需注入模块数据根）。"; return; }
        foreach (var file in ListHistoryFiles()) History.Add(file);
        StatusText = $"历史共 {History.Count} 条。";
    }

    private IEnumerable<string> ListHistoryFiles()
    {
        if (_history is null) return [];
        try
        {
            return Directory.GetFiles(_history.Root, "*.json")
                .Select(Path.GetFileName).OfType<string>()
                .OrderByDescending(name => name, StringComparer.Ordinal);
        }
        catch (Exception) { return []; }
    }

    private void ShowResult(LocalReviewRunResult result)
    {
        HasResult = true;
        ScopeSummary = result.ScopeDescription;
        StaleWarning = result.IsStale
            ? "快照已过期：以下行号来自固定快照，不保证对应当前文件内容。"
            : string.Empty;
        _allFindings = result.Findings;
        ApplyFilter();
        Rejected.Clear();
        foreach (var issue in result.Rejected) Rejected.Add(new LocalReviewRejectedItem(issue));
        StatusText = result.StatusText + "｜" + result.Findings.Count + " 项有效，" +
            result.Rejected.Count + " 项被拒绝" +
            (result.ResponseTruncated ? "（响应被截断，结果不完整）" : string.Empty);
    }

    private void ApplyFilter()
    {
        Findings.Clear();
        var stale = _historyDocument is not null || (_result?.IsStale ?? false);
        IEnumerable<LocalReviewFinding> source = _allFindings;
        if (!string.Equals(PriorityFilter, "全部", StringComparison.Ordinal) &&
            Enum.TryParse<LocalReviewPriority>(PriorityFilter, true, out var priority))
            source = source.Where(finding => finding.Priority == priority);
        foreach (var finding in source) Findings.Add(new LocalReviewFindingItem(finding, stale));
        OnPropertyChanged(nameof(CanExport));
    }

    private void ClearResult()
    {
        _result = null;
        _historyDocument = null;
        _allFindings = [];
        Findings.Clear();
        Rejected.Clear();
        SelectedFinding = null;
        HasResult = false;
        StaleWarning = string.Empty;
        ScopeSummary = string.Empty;
        ExportedPath = string.Empty;
    }

    private string Describe(LocalReviewScopeResolution scope) => scope.Status switch
    {
        LocalReviewScopeStatus.Resolved => scope.Description + "｜" + scope.Files.Count + " 文件" +
            "｜基准 " + Shorten(scope.BaseCommit) + " → 目标 " + Shorten(scope.TargetCommit),
        _ => scope.Description + "｜" + (scope.Error ?? scope.Status.ToString()),
    };

    private static string FreezeNote(LocalReviewScopeResolution scope) =>
        scope.SnapshotCapability == LocalReviewSnapshotCapability.FullSnapshot
            ? "可用既有快照精确冻结。"
            : "等待共享快照 API（见协调卡）。";

    // _lifetime 只取消不释放：它的令牌可能被后台线程读取，释放会造成竞态。
    // 无注册的 CTS 不持有非托管资源，随对象一起回收。
    private CancellationToken LifetimeToken => _lifetime.Token;

    private bool IsStaleCall(string projectAtStart) =>
        _disposed || !string.Equals(projectAtStart, ProjectPath, StringComparison.OrdinalIgnoreCase);

    private void CancelRunningRun()
    {
        _running = false;
        var cts = _runCts;
        // 只取消，不在此释放：仍在 await 的运行可能还在使用该令牌注册回调，
        // 提前 Dispose 会让它抛 ObjectDisposedException。释放由该运行的 finally 负责。
        if (cts is not null) cts.Cancel();
        CanCancel = false;
    }

    private static string Shorten(string? sha) => string.IsNullOrEmpty(sha) ? "-" :
        sha.Length >= 8 ? sha[..8] : sha;

    /// <summary>宿主显式释放：取消本模块全部运行与只读 Git 调用。普通停靠移动不会走到这里。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        CancelRunningRun();
        // 视图模型已销毁：不再有操作可以回收忙态，这里直接归位，避免残留运行中外观。
        _opSeq++;
        IsBusy = false;
        CanCancel = false;
    }

    public bool IsDisposed => _disposed;
}
