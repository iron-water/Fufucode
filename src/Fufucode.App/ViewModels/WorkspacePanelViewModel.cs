using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fufucode.App.Infrastructure;
using Fufucode.Core;

namespace Fufucode.App.ViewModels;

public enum WorkspacePanelKind { Files, Changes, Preview }
public enum WorkspaceChangeScope { Workspace, SessionWrites }

/// <summary>One context keeps stable tab identities and selections when the panel is hidden or narrowed.</summary>
public sealed partial class WorkspacePanelTabViewModel : ViewModelBase
{
    public WorkspacePanelTabViewModel(string panelId, WorkspacePanelKind kind, bool isOpen = true)
    {
        PanelId = panelId;
        Kind = kind;
        _isOpen = isOpen;
    }

    public string PanelId { get; }
    public WorkspacePanelKind Kind { get; }
    public string Title => Kind switch
    {
        WorkspacePanelKind.Files => "Files",
        WorkspacePanelKind.Changes => "Changes",
        _ => "Preview",
    };
    [ObservableProperty] private bool _isOpen = true;
    [ObservableProperty] private bool _isSelected;
}

/// <summary>Read-only presentation of A8. No line counts are invented; nullable flags stay unknown.</summary>
public sealed class WorkspacePanelChangedFileViewModel : ViewModelBase
{
    public WorkspacePanelChangedFileViewModel(ChangedFile source, string root)
    {
        Source = source;
        FullPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, source.Path));
    }

    public ChangedFile Source { get; }
    public string Path => Source.Path;
    public string FullPath { get; }
    public string? OldPath => Source.OldPath;
    public bool HasOldPath => OldPath is not null;
    public bool IsNew => Source.IsNew == true;
    public bool IsDeleted => Source.IsDeleted == true;
    public bool IsBinary => Source.IsBinary == true;
    public bool HasUnknownFlags => Source.IsNew is null || Source.IsDeleted is null || Source.IsBinary is null;
    public string UnknownFlags => string.Join(" · ", new[]
    {
        Source.IsNew is null ? "new ?" : null,
        Source.IsDeleted is null ? "deleted ?" : null,
        Source.IsBinary is null ? "binary ?" : null,
    }.Where(x => x is not null));
    public string KindText => Source.Kind switch
    {
        "modified" => "已修改", "added" => "已添加", "untracked" => "未跟踪", "deleted" => "已删除",
        "renamed" => "已重命名", "copied" => "已复制", "type-changed" => "类型变化", "unmerged" => "待解决冲突",
        "written" => "已确认写入", "missing" => "文件缺失", _ => Source.Kind,
    };
    public string StateText => $"{KindText} · index {DisplayStatus(Source.IndexStatus)} · worktree {DisplayStatus(Source.WorktreeStatus)}";
    private static string DisplayStatus(string? status) => status is null ? "未知" : status == " " ? "—" : status;
}

public sealed record WorkspacePanelFileItemViewModel(string Name, string RelativePath, string FullPath,
    bool IsDirectory, bool IsLink)
{
    public string Glyph => IsDirectory ? "▸" : "·";
    public string Detail => IsLink ? "链接 · 不展开" : IsDirectory ? "文件夹" : "文件";
}

/// <summary>Files and Changes view host. Queries are injected by the composition root; it never owns Core sessions.</summary>
public sealed partial class WorkspacePanelViewModel : ViewModelBase, IDisposable
{
    private const int DirectoryLimit = 300;
    private readonly IClipboardService _clipboard;
    private Func<CancellationToken, Task<ChangeRangeReport>>? _workspaceQuery;
    private Func<CancellationToken, Task<ChangeRangeReport>>? _sessionQuery;
    private CancellationTokenSource? _changeCancellation;
    private CancellationTokenSource? _fileCancellation;
    private string _contextIdentity = string.Empty;
    private int _changeGeneration;
    private int _fileGeneration;
    private bool _disposed;

    public WorkspacePanelViewModel(IClipboardService clipboard)
    {
        _clipboard = clipboard;
        RecreateTabs("unattached");
    }

    public event EventHandler? CloseRequested;
    public ObservableCollection<WorkspacePanelTabViewModel> Tabs { get; } = new();
    public ObservableCollection<WorkspacePanelChangedFileViewModel> Changes { get; } = new();
    public ObservableCollection<WorkspacePanelFileItemViewModel> Files { get; } = new();
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VisibleFiles))]
    [NotifyPropertyChangedFor(nameof(IsFilesEmpty))]
    private string _fileFilter = "";
    public System.Collections.Generic.IEnumerable<WorkspacePanelFileItemViewModel> VisibleFiles =>
        Files.Where(file => file.Name.Contains(FileFilter, StringComparison.OrdinalIgnoreCase));
    public string ProjectPath { get; private set; } = string.Empty;
    public string? SessionId { get; private set; }
    public bool IsMock { get; private set; }
    public string SourceLabel => IsMock ? "示例数据" : "项目数据";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFilesSelected))]
    [NotifyPropertyChangedFor(nameof(IsChangesSelected))]
    [NotifyPropertyChangedFor(nameof(HasSelectedTab))]
    private WorkspacePanelTabViewModel? _selectedTab;
    public bool IsFilesSelected => SelectedTab?.Kind == WorkspacePanelKind.Files;
    public bool IsChangesSelected => SelectedTab?.Kind == WorkspacePanelKind.Changes;
    public bool IsPreviewSelected => IsFilePreviewOpen;
    public bool HasSelectedTab => SelectedTab is not null;

    [ObservableProperty] private bool _isFilePreviewOpen;
    [ObservableProperty] private string _previewFileName = string.Empty;
    [ObservableProperty] private string _previewFilePath = string.Empty;
    [ObservableProperty] private string _previewFileSize = string.Empty;
    [ObservableProperty] private string _previewFileDetail = string.Empty;

    [RelayCommand]
    public void CloseFilePreview() => IsFilePreviewOpen = false;

    public void OpenFilePreview(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            var full = Path.GetFullPath(path);
            var info = new FileInfo(full);
            PreviewFileName = info.Name;
            PreviewFilePath = full;
            PreviewFileSize = info.Exists ? FormatFileSize(info.Length) : "文件不存在";
            PreviewFileDetail = info.Exists ? $"修改时间 {info.LastWriteTime:yyyy-MM-dd HH:mm:ss}" : "无法获取文件信息";
            IsFilePreviewOpen = true;
        }
        catch (Exception ex)
        {
            PreviewFileName = Path.GetFileName(path);
            PreviewFilePath = path;
            PreviewFileSize = "未知大小";
            PreviewFileDetail = ex.Message;
            IsFilePreviewOpen = true;
        }
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F1} MB";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWorkspaceScope))]
    [NotifyPropertyChangedFor(nameof(IsSessionScope))]
    [NotifyPropertyChangedFor(nameof(ScopeDescription))]
    private WorkspaceChangeScope _selectedScope;
    public bool IsWorkspaceScope => SelectedScope == WorkspaceChangeScope.Workspace;
    public bool IsSessionScope => SelectedScope == WorkspaceChangeScope.SessionWrites;
    public string ScopeDescription => IsWorkspaceScope
        ? "整个 Git 工作树的当前状态，包含用户与外部程序改动。"
        : "当前分支已确认成功的 write/edit 路径；不包含终端命令的全部副作用。";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChangesEmpty))]
    private bool _isChangesLoading;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChangesError))]
    [NotifyPropertyChangedFor(nameof(IsChangesEmpty))]
    private string? _changesError;
    [ObservableProperty] private string _changesRoot = string.Empty;
    [ObservableProperty] private string _changesUpdatedText = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasExcludedPaths))]
    private int _excludedPaths;
    public bool HasExcludedPaths => ExcludedPaths > 0;
    public string ExcludedPathsText => $"已排除 {ExcludedPaths} 个超出项目范围的工具路径";
    public bool HasChangesError => ChangesError is not null;
    public bool HasChanges => Changes.Count > 0;
    public bool IsChangesEmpty => !IsChangesLoading && !HasChangesError && !HasChanges;
    public string ChangesCountText => $"{Changes.Count} 个文件";

    [ObservableProperty] private WorkspacePanelChangedFileViewModel? _selectedChange;
    public bool HasSelectedChange => SelectedChange is not null;

    [ObservableProperty] private string _currentDirectory = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFilesEmpty))]
    private bool _isFilesLoading;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFilesError))]
    [NotifyPropertyChangedFor(nameof(IsFilesEmpty))]
    private string? _filesError;
    [ObservableProperty] private bool _isDirectoryTruncated;
    [ObservableProperty] private WorkspacePanelFileItemViewModel? _selectedFile;
    [ObservableProperty] private string _copyFeedback = string.Empty;
    public bool HasFilesError => FilesError is not null;
    public bool IsFilesEmpty => !IsFilesLoading && !HasFilesError && !VisibleFiles.Any();
    public bool CanGoUp => ProjectPath.Length > 0 && !PathEquals(CurrentDirectory, ProjectPath);
    public string CurrentDirectoryText => CurrentDirectory.Length == 0 ? "尚未选择项目" : CurrentDirectory;
    public string FileSelectionText => SelectedFile?.FullPath ?? "选择文件查看完整路径";
    public bool HasSelectedFile => SelectedFile is not null;
    public bool CanPreviewSelectedFile => SelectedFile is { IsDirectory: false };
    public string FileCountText => $"{Files.Count} 项";

    /// <summary>All calls occur on the UI thread. The delegates capture the current Core context in the caller.</summary>
    public void AttachContext(string projectPath, string? sessionId,
        Func<CancellationToken, Task<ChangeRangeReport>> workspaceQuery,
        Func<CancellationToken, Task<ChangeRangeReport>> sessionQuery)
    {
        Attach(projectPath, sessionId, workspaceQuery, sessionQuery, false);
    }

    /// <summary>Uses the same report projection as a Core query. File browsing uses only the supplied existing project.</summary>
    public void AttachMock(string projectPath)
    {
        var root = Path.GetFullPath(projectPath);
        Attach(root, "mock-session", _ => Task.FromResult(CreateMockReport(root, WorkspaceChangeScope.Workspace)),
            _ => Task.FromResult(CreateMockReport(root, WorkspaceChangeScope.SessionWrites)), true);
    }

    private void Attach(string projectPath, string? sessionId,
        Func<CancellationToken, Task<ChangeRangeReport>> workspaceQuery,
        Func<CancellationToken, Task<ChangeRangeReport>> sessionQuery, bool mock)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var full = Path.GetFullPath(projectPath);
        var identity = full + "|" + (sessionId ?? "no-session");
        _workspaceQuery = workspaceQuery;
        _sessionQuery = sessionQuery;
        ProjectPath = full;
        SessionId = sessionId;
        IsMock = mock;
        OnPropertyChanged(nameof(ProjectPath));
        OnPropertyChanged(nameof(SessionId));
        OnPropertyChanged(nameof(IsMock));
        OnPropertyChanged(nameof(SourceLabel));
        if (_contextIdentity != identity)
        {
            _contextIdentity = identity;
            _changeCancellation?.Cancel();
            _fileCancellation?.Cancel();
            _changeGeneration++;
            _fileGeneration++;
            Changes.Clear();
            Files.Clear();
            SelectedChange = null;
            SelectedFile = null;
            ChangesError = null;
            FilesError = null;
            IsChangesLoading = false;
            IsFilesLoading = false;
            IsDirectoryTruncated = false;
            ChangesRoot = string.Empty;
            ChangesUpdatedText = string.Empty;
            ExcludedPaths = 0;
            CopyFeedback = string.Empty;
            CurrentDirectory = full;
            RecreateTabs(identity);
            NotifyChangeRows();
            NotifyFileRows();
        }
    }

    private void RecreateTabs(string identity)
    {
        Tabs.Clear();
        Tabs.Add(new(identity + "|files", WorkspacePanelKind.Files));
        Tabs.Add(new(identity + "|changes", WorkspacePanelKind.Changes));
        SelectedTab = Tabs[0];
    }

    partial void OnSelectedTabChanged(WorkspacePanelTabViewModel? value)
    {
        foreach (var tab in Tabs) tab.IsSelected = ReferenceEquals(tab, value);
    }

    partial void OnSelectedScopeChanged(WorkspaceChangeScope value)
    {
        SelectedChange = null;
        Changes.Clear();
        ChangesRoot = string.Empty;
        ExcludedPaths = 0;
        NotifyChangeRows();
        if (_workspaceQuery is not null) _ = RefreshChangesAsync();
    }

    partial void OnSelectedChangeChanged(WorkspacePanelChangedFileViewModel? value)
    {
        CopyFeedback = string.Empty;
        OnPropertyChanged(nameof(HasSelectedChange));
        _ = RefreshSelectedDiffAsync();
    }
    partial void OnSelectedFileChanged(WorkspacePanelFileItemViewModel? value)
    {
        CopyFeedback = string.Empty;
        OnPropertyChanged(nameof(HasSelectedFile));
        OnPropertyChanged(nameof(CanPreviewSelectedFile));
        OnPropertyChanged(nameof(FileSelectionText));
    }
    partial void OnCurrentDirectoryChanged(string value)
    {
        FileFilter = "";
        OnPropertyChanged(nameof(CanGoUp));
        OnPropertyChanged(nameof(CurrentDirectoryText));
    }
    partial void OnExcludedPathsChanged(int value) => OnPropertyChanged(nameof(ExcludedPathsText));

    [RelayCommand] private void SelectTab(WorkspacePanelTabViewModel? tab)
    {
        if (tab?.IsOpen == true && Tabs.Contains(tab)) SelectedTab = tab;
    }
    [RelayCommand] private void CloseTab(WorkspacePanelTabViewModel? tab)
    {
        if (tab is null || !Tabs.Contains(tab)) return;
        tab.IsOpen = false;
        if (ReferenceEquals(SelectedTab, tab)) SelectedTab = Tabs.FirstOrDefault(x => x.IsOpen);
    }
    [RelayCommand] private void OpenFiles() => Open(WorkspacePanelKind.Files);
    [RelayCommand] private void OpenChanges() => Open(WorkspacePanelKind.Changes);
    [RelayCommand] private void OpenPreview()
    {
        if (SelectedFile is { IsDirectory: false } file)
            OpenFilePreview(file.FullPath);
        Open(WorkspacePanelKind.Files);
    }
    private void Open(WorkspacePanelKind kind)
    {
        var tab = Tabs.First(x => x.Kind == kind);
        tab.IsOpen = true;
        SelectedTab = tab;
    }
    [RelayCommand] private void HidePanel() => CloseRequested?.Invoke(this, EventArgs.Empty);
    [RelayCommand] private void WorkspaceScope() => SelectedScope = WorkspaceChangeScope.Workspace;
    [RelayCommand] private void SessionScope() => SelectedScope = WorkspaceChangeScope.SessionWrites;

    [RelayCommand]
    public async Task RefreshAsync()
    {
        await Task.WhenAll(RefreshFilesAsync(), RefreshChangesAsync());
    }

    [RelayCommand]
    public async Task RefreshChangesAsync()
    {
        if (_disposed) return;
        _changeCancellation?.Cancel();
        _changeCancellation?.Dispose();
        _changeCancellation = new();
        var cancellation = _changeCancellation.Token;
        var generation = ++_changeGeneration;
        IsChangesLoading = true;
        ChangesError = null;
        try
        {
            var query = IsWorkspaceScope ? _workspaceQuery : _sessionQuery;
            var report = query is null
                ? new ChangeRangeReport(IsWorkspaceScope ? "workspace" : "session-writes", ProjectPath, false, [], "context-unavailable")
                : await query(cancellation);
            if (generation != _changeGeneration || _disposed) return;
            ApplyReport(report);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (generation == _changeGeneration && !_disposed)
            {
                Changes.Clear();
                SelectedChange = null;
                ChangesError = $"变更查询失败：{error.Message}";
                NotifyChangeRows();
            }
        }
        finally { if (generation == _changeGeneration && !_disposed) IsChangesLoading = false; }
    }

    /// <summary>Projection is shared by mock and real reports. Available=false never becomes an empty success.</summary>
    public void ApplyReport(ChangeRangeReport report)
    {
        var selectedPath = SelectedChange?.Path;
        Changes.Clear();
        ChangesRoot = report.Root;
        ExcludedPaths = report.ExcludedToolPaths;
        ChangesUpdatedText = $"已刷新 {DateTimeOffset.Now:HH:mm:ss}";
        if (!report.Available)
        {
            ChangesError = ErrorText(report.Error);
            SelectedChange = null;
        }
        else
        {
            ChangesError = null;
            foreach (var file in report.Files) Changes.Add(new(file, report.Root));
            SelectedChange = Changes.FirstOrDefault(x => x.Path == selectedPath);
        }
        NotifyChangeRows();
    }

    private static string ErrorText(string? code) => code switch
    {
        "not-a-git-worktree" => "此目录不是 Git 工作树，无法读取工作区变更。",
        "git-unavailable" => "Git 不可用，无法读取工作区变更。",
        "context-unavailable" => "尚未连接项目与会话。",
        "no-session" => "尚未选择会话，无法读取确认写入路径。",
        _ => $"无法读取变更范围：{code ?? "原因未知"}",
    };

    [RelayCommand]
    public async Task RefreshFilesAsync()
    {
        if (_disposed) return;
        _fileCancellation?.Cancel();
        _fileCancellation?.Dispose();
        _fileCancellation = new();
        var cancellation = _fileCancellation.Token;
        var generation = ++_fileGeneration;
        var directory = CurrentDirectory;
        var root = ProjectPath;
        IsFilesLoading = true;
        FilesError = null;
        try
        {
            var result = await Task.Run(() => ReadDirectory(directory, root, cancellation), cancellation);
            if (generation != _fileGeneration || _disposed) return;
            var selection = SelectedFile?.FullPath;
            Files.Clear();
            foreach (var item in result.Items) Files.Add(item);
            IsDirectoryTruncated = result.Truncated;
            SelectedFile = Files.FirstOrDefault(x => PathEquals(x.FullPath, selection));
            NotifyFileRows();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            if (generation == _fileGeneration && !_disposed)
            {
                Files.Clear();
                SelectedFile = null;
                FilesError = $"目录不可读取：{error.Message}";
                NotifyFileRows();
            }
        }
        finally { if (generation == _fileGeneration && !_disposed) IsFilesLoading = false; }
    }

    private static (IReadOnlyList<WorkspacePanelFileItemViewModel> Items, bool Truncated) ReadDirectory(
        string directory, string root, CancellationToken cancellation)
    {
        if (directory.Length == 0 || root.Length == 0) throw new IOException("尚未选择项目。");
        if (!IsUnderRoot(directory, root)) throw new IOException("目录超出项目范围。");
        var rows = new List<WorkspacePanelFileItemViewModel>();
        foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
        {
            cancellation.ThrowIfCancellationRequested();
            var isDirectory = (entry.Attributes & FileAttributes.Directory) != 0;
            var isLink = (entry.Attributes & FileAttributes.ReparsePoint) != 0;
            rows.Add(new(entry.Name, Path.GetRelativePath(root, entry.FullName).Replace('\\', '/'), entry.FullName, isDirectory, isLink));
            if (rows.Count > DirectoryLimit) break;
        }
        return (rows.Take(DirectoryLimit).OrderByDescending(x => x.IsDirectory).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray(),
            rows.Count > DirectoryLimit);
    }

    [RelayCommand]
    private async Task BrowseFileAsync(WorkspacePanelFileItemViewModel? file)
    {
        if (file is null || !Files.Contains(file)) return;
        SelectedFile = file;
        if (!file.IsDirectory || file.IsLink) return;
        if (!IsUnderRoot(file.FullPath, ProjectPath)) return;
        CurrentDirectory = file.FullPath;
        SelectedFile = null;
        await RefreshFilesAsync();
    }

    [RelayCommand]
    private async Task GoUpAsync()
    {
        if (!CanGoUp || Path.GetDirectoryName(CurrentDirectory) is not { } parent || !IsUnderRoot(parent, ProjectPath)) return;
        CurrentDirectory = parent;
        SelectedFile = null;
        await RefreshFilesAsync();
    }

    [RelayCommand]
    private async Task CopyFilePathAsync()
    {
        if (SelectedFile is null) return;
        await CopyPathAsync(SelectedFile.FullPath);
    }

    [RelayCommand]
    private async Task CopyChangePathAsync()
    {
        if (SelectedChange is null) return;
        await CopyPathAsync(SelectedChange.FullPath);
    }

    private async Task CopyPathAsync(string path)
    {
        try { await _clipboard.SetTextAsync(path); CopyFeedback = "已复制完整路径"; }
        catch (Exception error) { CopyFeedback = $"复制失败：{error.Message}"; }
    }

    private void NotifyChangeRows()
    {
        OnPropertyChanged(nameof(HasChanges));
        OnPropertyChanged(nameof(IsChangesEmpty));
        OnPropertyChanged(nameof(ChangesCountText));
    }
    private void NotifyFileRows()
    {
        OnPropertyChanged(nameof(VisibleFiles));
        OnPropertyChanged(nameof(IsFilesEmpty));
        OnPropertyChanged(nameof(FileCountText));
    }
    private static bool PathEquals(string? a, string? b) => string.Equals(a, b,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static bool IsUnderRoot(string path, string root) => PathEquals(path, root) || path.StartsWith(
        Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    public static ChangeRangeReport CreateMockReport(string root, WorkspaceChangeScope scope) => new(
        scope == WorkspaceChangeScope.Workspace ? "workspace" : "session-writes", Path.GetFullPath(root), true,
        [new("src/Workbench.cs", null, "modified", " ", "M", true, false, []),
         new("src/PanelLayout.cs", null, "added", "A", " ", true, false, scope == WorkspaceChangeScope.SessionWrites ? ["demo-write"] : []),
         new("assets/obsolete.bin", null, "deleted", "D", " ", false, true, []),
         new("notes/draft.md", null, "written", null, null, true, null, ["demo-edit"])], null);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _changeGeneration++;
        _fileGeneration++;
        _changeCancellation?.Cancel();
        _fileCancellation?.Cancel();
        _changeCancellation?.Dispose();
        _fileCancellation?.Dispose();
    }
}
