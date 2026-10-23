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
public sealed partial class WorkspacePanelChangedFileViewModel : ViewModelBase
{
    public WorkspacePanelChangedFileViewModel(ChangedFile source, string root)
    {
        Source = source;
        FullPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, source.Path));
        var name = System.IO.Path.GetFileName(source.Path.Replace('/', System.IO.Path.DirectorySeparatorChar));
        Name = name.Length > 0 ? name : source.Path;
        Folder = source.Path.Length > Name.Length
            ? source.Path[..(source.Path.Length - Name.Length - 1)]
            : string.Empty;
    }

    public ChangedFile Source { get; }
    public string Path => Source.Path;
    public string FullPath { get; }
    /// <summary>文件名与所在目录分开存放：列表按目录分组，行内只显示文件名。</summary>
    public string Name { get; }
    public string Folder { get; }
    /// <summary>扩展名，供语法高亮选择语法；隐藏文件没有扩展名时为空。</summary>
    public string Extension => System.IO.Path.GetExtension(Name).TrimStart('.').ToLowerInvariant();
    public int? AddedLines => Source.AddedLines;
    public int? RemovedLines => Source.RemovedLines;
    public bool HasLineCounts => Source.AddedLines is not null || Source.RemovedLines is not null;
    public bool HasFolder => Folder.Length > 0;
    /// <summary>列表内的选中态；由面板在 SelectedChange 变化时同步。</summary>
    [ObservableProperty] private bool _isSelected;
    public string AddedText => Source.AddedLines is { } added ? "+" + added : string.Empty;
    public string RemovedText => Source.RemovedLines is { } removed ? "−" + removed : string.Empty;
    public bool HasAdded => Source.AddedLines is > 0;
    public bool HasRemoved => Source.RemovedLines is > 0;
    /// <summary>单字符状态标记，替代旧版整句 "已修改 · index M · worktree —"。</summary>
    public string StatusMark => Source.Kind switch
    {
        "added" or "untracked" or "written" => "A",
        "deleted" or "missing" => "D",
        "renamed" => "R",
        "copied" => "C",
        "unmerged" => "!",
        "type-changed" => "T",
        _ => "M",
    };
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

/// <summary>变更列表里的一组文件（按所在目录），目录可折叠。</summary>
public sealed partial class WorkspaceChangeGroupViewModel : ViewModelBase
{
    public WorkspaceChangeGroupViewModel(string folder)
    {
        Folder = folder;
        Header = folder.Length == 0 ? "根目录" : folder;
    }

    public string Folder { get; }
    public string Header { get; }
    public System.Collections.ObjectModel.ObservableCollection<WorkspacePanelChangedFileViewModel> Files { get; } = new();
    [ObservableProperty] private bool _isExpanded = true;
    public bool IsCollapsed => !IsExpanded;
    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(IsCollapsed));
    public string CountText => Files.Count.ToString();
}

/// <summary>
/// 变更列表的呈现行：目录头是单独的对象，文件行直接复用文件 ViewModel 本身。
/// 共用同一批实例，选中项仍是 <see cref="WorkspacePanelChangedFileViewModel"/>，
/// 既有"按文件选中"的契约与快捷键导航都不需要改。
/// </summary>
public sealed partial class WorkspaceChangeGroupRowViewModel : ViewModelBase
{
    public WorkspaceChangeGroupRowViewModel(WorkspaceChangeGroupViewModel group) => Group = group;
    public WorkspaceChangeGroupViewModel Group { get; }
    public string Header => Group.Header;
    public string CountText => Group.CountText;
    public bool IsExpanded => Group.IsExpanded;
    public bool IsCollapsed => Group.IsCollapsed;
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

    /// <summary>请求把当前图表保存为图片；由视图执行渲染与文件选择。</summary>
    public event EventHandler? DiagramDownloadRequested;


    [ObservableProperty] private bool _isDiagramPreview;
    [ObservableProperty] private string _diagramSource = "";
    [ObservableProperty] private string _diagramError = "";

    /// <summary>文本/代码文件的侧栏预览：直接显示源码，不交给浏览器。</summary>
    [ObservableProperty] private bool _isTextPreview;
    [ObservableProperty] private string _previewText = "";
    [ObservableProperty] private string _previewLanguage = "";

    /// <summary>喂给富文本渲染器的 Mermaid 围栏文本。</summary>
    public string DiagramMarkdown => IsDiagramPreview && DiagramSource.Length > 0
        ? "```mermaid" + (char)10 + DiagramSource + (char)10 + "```"
        : string.Empty;

    public bool HasDiagramError => DiagramError.Length > 0;

    partial void OnDiagramErrorChanged(string value) => OnPropertyChanged(nameof(HasDiagramError));
    /// <summary>浏览器预览的可见性；用正向属性避免运行期绑定不支持取反。</summary>
    public bool IsBrowserPreviewVisible => !IsDiagramPreview && !IsTextPreview;

    partial void OnIsDiagramPreviewChanged(bool value)
    {
        OnPropertyChanged(nameof(DiagramMarkdown));
        OnPropertyChanged(nameof(IsBrowserPreviewVisible));
    }

    partial void OnIsTextPreviewChanged(bool value) => OnPropertyChanged(nameof(IsBrowserPreviewVisible));
    partial void OnDiagramSourceChanged(string value) => OnPropertyChanged(nameof(DiagramMarkdown));
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
    public void CloseFilePreview()
    {
        IsFilePreviewOpen = false;
        IsDiagramPreview = false;
        DiagramSource = string.Empty;
        IsTextPreview = false;
        PreviewText = string.Empty;
    }

    /// <summary>.mermaid/.mmd 文件不走浏览器预览，直接渲染成图表并给出下载/复制。</summary>
    private void UpdateDiagramPreview(string full)
    {
        var extension = System.IO.Path.GetExtension(full);
        var isDiagram = extension.Equals(".mermaid", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".mmd", StringComparison.OrdinalIgnoreCase);
        if (!isDiagram)
        {
            IsDiagramPreview = false;
            DiagramSource = string.Empty;
            UpdateTextPreview(full);
            return;
        }

        IsTextPreview = false;
        PreviewText = string.Empty;

        try
        {
            var info = new FileInfo(full);
            // 图表源文件通常几 KB；超大文件不解析，避免界面卡死。
            if (!info.Exists || info.Length > 2 * 1024 * 1024)
            {
                IsDiagramPreview = false;
                DiagramSource = string.Empty;
                return;
            }

            DiagramSource = File.ReadAllText(full);
            IsDiagramPreview = true;
        }
        catch (Exception ex)
        {
            IsDiagramPreview = false;
            DiagramSource = string.Empty;
            DiagramError = ex.Message;
        }
    }

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "cs", "fs", "vb", "py", "js", "jsx", "ts", "tsx", "java", "kt", "kts", "go", "rs",
        "c", "h", "cc", "cpp", "cxx", "hpp", "m", "mm", "swift", "php", "rb", "lua", "dart",
        "json", "jsonc", "yaml", "yml", "toml", "xml", "xsd", "html", "htm", "css", "scss",
        "less", "sh", "bash", "zsh", "ps1", "bat", "cmd", "sql", "md", "markdown", "txt",
        "log", "ini", "conf", "cfg", "env", "gitignore", "editorconfig", "gradle", "properties",
    };

    /// <summary>
    /// 文本类文件用代码视图预览。二进制或过大的文件仍然退回浏览器，
    /// 避免把乱码或几百 MB 的内容塞进控件。
    /// </summary>
    private void UpdateTextPreview(string full)
    {
        var extension = System.IO.Path.GetExtension(full).TrimStart('.').ToLowerInvariant();
        if (extension.Length == 0 || !TextExtensions.Contains(extension))
        {
            IsTextPreview = false;
            PreviewText = string.Empty;
            return;
        }

        try
        {
            var info = new FileInfo(full);
            if (!info.Exists || info.Length > 512 * 1024)
            {
                IsTextPreview = false;
                PreviewText = string.Empty;
                return;
            }

            PreviewText = File.ReadAllText(full);
            PreviewLanguage = extension;
            IsTextPreview = true;
        }
        catch (Exception)
        {
            IsTextPreview = false;
            PreviewText = string.Empty;
        }
    }

    [RelayCommand]
    private async Task CopyDiagramSourceAsync() => await _clipboard.SetTextAsync(DiagramSource);

    /// <summary>下载交给视图：只有视图能拿到已渲染的控件并保存为图片。</summary>
    [RelayCommand]
    private void DownloadDiagram() => DiagramDownloadRequested?.Invoke(this, EventArgs.Empty);

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
            UpdateDiagramPreview(full);
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

    /// <summary>
    /// 变更列表的呈现行：目录头与文件行混排成一条平铺列表。
    /// 文件行的选中语义不变（SelectedChange 仍是 WorkspacePanelChangedFileViewModel），分组只在显示层。
    /// </summary>
    public ObservableCollection<object> ChangeRows { get; } = new();

    private readonly List<WorkspaceChangeGroupViewModel> _groups = new();
    public IReadOnlyList<WorkspaceChangeGroupViewModel> ChangeGroups => _groups;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChangeFilter))]
    private string _changeFilter = string.Empty;

    public bool HasChangeFilter => ChangeFilter.Trim().Length > 0;
    public bool HasFilteredOut => HasChangeFilter && Changes.Count > 0 && ChangeRows.Count == 0;
    public bool IsChangeListEmpty => !IsChangesLoading && !HasChangesError && !HasChanges;

    [ObservableProperty] private object? _selectedChangeRow;

    /// <summary>栏宽够时左右分栏，窄栏退回"列表 ↔ 文件"的单栏切换。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChangeListVisible))]
    [NotifyPropertyChangedFor(nameof(IsChangeListNarrow))]
    private bool _isChangePaneWide;

    /// <summary>宽栏列表固定宽度；窄栏里选中文件后列表让位给 diff。</summary>
    public bool IsChangeListVisible => IsChangePaneWide || !HasSelectedChange;
    /// <summary>窄栏才需要"返回列表"入口，宽栏列表一直可见。</summary>
    public bool IsChangeListNarrow => !IsChangePaneWide;
    [ObservableProperty] private bool _isDark = true;
    public string AddedTotalText => "+" + Changes.Sum(file => file.AddedLines ?? 0);
    public string RemovedTotalText => "−" + Changes.Sum(file => file.RemovedLines ?? 0);
    public bool HasTotals => Changes.Any(file => file.HasLineCounts);

    /// <summary>
    /// 列表选中项：文件行转给 SelectedChange，目录头就地折叠且不留选中态。
    /// </summary>
    partial void OnSelectedChangeRowChanged(object? value)
    {
        if (value is WorkspaceChangeGroupRowViewModel header)
        {
            header.Group.IsExpanded = !header.Group.IsExpanded;
            _suppressRowSync = true;
            SelectedChangeRow = null;
            _suppressRowSync = false;
            return;
        }
        if (_suppressRowSync) return;
        SelectedChange = value as WorkspacePanelChangedFileViewModel;
    }

    private bool _suppressRowSync;

    partial void OnChangeFilterChanged(string value)
    {
        RebuildChangeGroups();
        OnPropertyChanged(nameof(HasChangeFilter));
    }

    /// <summary>筛选匹配路径任意片段；分组重算后重建平铺行。</summary>
    private void RebuildChangeGroups()
    {
        _groups.Clear();
        var filter = ChangeFilter.Trim();
        var visible = string.IsNullOrEmpty(filter)
            ? Changes.AsEnumerable()
            : Changes.Where(file => file.Path.Contains(filter, StringComparison.OrdinalIgnoreCase));
        foreach (var group in visible.GroupBy(file => file.Folder, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var item = new WorkspaceChangeGroupViewModel(group.Key);
            foreach (var file in group.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)) item.Files.Add(file);
            item.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(WorkspaceChangeGroupViewModel.IsExpanded)) RebuildChangeRows();
            };
            _groups.Add(item);
        }
        RebuildChangeRows();
        OnPropertyChanged(nameof(ChangeGroups));
        OnPropertyChanged(nameof(HasFilteredOut));
        OnPropertyChanged(nameof(IsChangeListEmpty));
    }

    private void ClearChangeRows()
    {
        _groups.Clear();
        ChangeRows.Clear();
        OnPropertyChanged(nameof(ChangeGroups));
    }

    /// <summary>目录折叠/展开后只重建平铺行，不重新分组。</summary>
    private void RebuildChangeRows()
    {
        ChangeRows.Clear();
        foreach (var group in _groups)
        {
            ChangeRows.Add(new WorkspaceChangeGroupRowViewModel(group));
            if (!group.IsExpanded) continue;
            foreach (var file in group.Files) ChangeRows.Add(file);
        }
    }

    /// <summary>
    /// 由视图注入：语法着色按深浅主题取调色板。
    /// 颜色是烘进 diff 行里的，主题变了要重新取一次，否则阅读区还停在旧配色。
    /// </summary>
    public void SetDarkTheme(bool dark)
    {
        if (IsDark == dark) return;
        IsDark = dark;
        if (SelectedChange is not null && DiffModel is not null) _ = RefreshSelectedDiffAsync();
    }

    private bool IsDarkTheme() => IsDark;

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
            ClearChangeRows();
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
        ClearChangeRows();
        ChangesRoot = string.Empty;
        ExcludedPaths = 0;
        NotifyChangeRows();
        if (_workspaceQuery is not null) _ = RefreshChangesAsync();
    }

    partial void OnSelectedChangeChanged(WorkspacePanelChangedFileViewModel? value)
    {
        CopyFeedback = string.Empty;
        foreach (var file in Changes) file.IsSelected = ReferenceEquals(file, value);
        if (!_suppressRowSync)
        {
            _suppressRowSync = true;
            SelectedChangeRow = value;
            _suppressRowSync = false;
        }
        OnPropertyChanged(nameof(HasSelectedChange));
        OnPropertyChanged(nameof(IsChangeListVisible));
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
                ClearChangeRows();
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
        RebuildChangeGroups();
        // 重新分组后行是新对象，选中态要再同步一次。
        if (SelectedChange is { } current)
            foreach (var file in Changes) file.IsSelected = ReferenceEquals(file, current);
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
        OnPropertyChanged(nameof(IsChangeListEmpty));
        OnPropertyChanged(nameof(HasFilteredOut));
        OnPropertyChanged(nameof(AddedTotalText));
        OnPropertyChanged(nameof(RemovedTotalText));
        OnPropertyChanged(nameof(HasTotals));
        OnPropertyChanged(nameof(HasSelectedChange));
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
