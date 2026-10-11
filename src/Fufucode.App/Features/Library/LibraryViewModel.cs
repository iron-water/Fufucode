using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fufucode.App.ViewModels;

namespace Fufucode.App.Features.Library;

/// <summary>
/// 宿主需实现的文件/目录选择器。资料库模块不直接依赖窗口或存储提供者，
/// 由接线方（Codex）用 TopLevel.StorageProvider 实现后注入。
/// </summary>
public interface ILibraryFilePicker
{
    Task<IReadOnlyList<string>> PickFilesAsync();

    Task<string?> PickFolderAsync(string title);
}

/// <summary>
/// 资料库页面视图模型：列表/检索/标签/预览/导入导出/引用。
/// 只调用 LibraryService；不直接读写数据库，也不接触聊天会话（引用载荷由宿主接线消费）。
/// </summary>
public sealed partial class LibraryViewModel : ViewModelBase, IDisposable
{
    private const int PreviewCharLimit = 20000;

    private readonly LibraryService _service;
    private CancellationTokenSource? _indexCts;
    private bool _disposed;

    public LibraryViewModel(LibraryService service, ILibraryFilePicker? picker = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        Picker = picker;
    }

    public LibraryService Service => _service;

    public ILibraryFilePicker? Picker { get; set; }

    public ObservableCollection<LibraryEntry> Entries { get; } = new();
    public ObservableCollection<LibraryTagCount> Tags { get; } = new();
    public ObservableCollection<LibraryVersion> Versions { get; } = new();

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private string? _selectedTag;
    [ObservableProperty] private LibraryEntry? _selectedEntry;
    [ObservableProperty] private string _previewText = string.Empty;
    [ObservableProperty] private string _previewNote = string.Empty;
    [ObservableProperty] private string _citationText = string.Empty;
    [ObservableProperty] private string _newTag = string.Empty;
    [ObservableProperty] private string _statusText = "资料库就绪";
    [ObservableProperty] private string _indexStatusText = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isIndexing;
    [ObservableProperty] private bool _importAsCopy = true;
    [ObservableProperty] private bool _deleteArmed;

    public bool HasSelection => SelectedEntry is not null;
    public bool HasPreview => PreviewText.Length > 0;
    public bool HasCitation => CitationText.Length > 0;
    public bool HasVersions => Versions.Count > 0;
    public string ImportModeHint => ImportAsCopy
        ? "副本：内容复制进资料库，源文件删除后仍可读，占用额外空间。"
        : "引用：只记录来源路径与版本，不复制内容；源文件移动或删除后内容不可读。";
    public string EmptyHint => Entries.Count == 0 ? "还没有资料条目。用“导入文件”或“收藏片段”开始。" : string.Empty;
    public string DeleteButtonText => DeleteArmed ? "确认删除" : "删除条目";


    /// <summary>宿主接线：把引用文本写进输入框或剪贴板。模块不直接操作聊天会话。</summary>
    public event Action<string>? CitationCopyRequested;

    partial void OnSelectedEntryChanged(LibraryEntry? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        DeleteArmed = false;
        LoadPreview(value);
    }

    partial void OnImportAsCopyChanged(bool value) => OnPropertyChanged(nameof(ImportModeHint));
    partial void OnPreviewTextChanged(string value) => OnPropertyChanged(nameof(HasPreview));
    partial void OnCitationTextChanged(string value) => OnPropertyChanged(nameof(HasCitation));
    partial void OnSelectedTagChanged(string? value) => _ = ReloadAsync();
    partial void OnDeleteArmedChanged(bool value) => OnPropertyChanged(nameof(DeleteButtonText));

    public async Task InitializeAsync()
    {
        await ReloadAsync().ConfigureAwait(true);
        StartBackgroundIndexing();
    }

    private void StartBackgroundIndexing()
    {
        _indexCts?.Cancel();
        _indexCts?.Dispose();
        _indexCts = new CancellationTokenSource();
        var token = _indexCts.Token;
        var progress = new Progress<LibraryIndexProgress>(update =>
        {
            IndexStatusText = update.Text;
            IsIndexing = update.Completed < update.Total;
        });
        _ = Task.Run(async () =>
        {
            try
            {
                IsIndexing = true;
                var result = await _service.IndexPendingAsync(progress, token).ConfigureAwait(false);
                if (!token.IsCancellationRequested)
                    IndexStatusText = result.Total == 0 ? "索引已是最新" : result.Text;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception error)
            {
                IndexStatusText = "索引中断：" + error.Message;
            }
            finally
            {
                IsIndexing = false;
            }
        }, token);
    }

    [RelayCommand]
    private async Task ReloadAsync()
    {
        if (_disposed) return;
        IsBusy = true;
        try
        {
            var selectedId = SelectedEntry?.Id;
            var hits = _service.Search(SearchText, SelectedTag is null ? null : new[] { SelectedTag });
            Entries.Clear();
            foreach (var hit in hits) Entries.Add(hit.Entry);
            OnPropertyChanged(nameof(EmptyHint));

            Tags.Clear();
            foreach (var tag in _service.ListTags()) Tags.Add(tag);

            if (selectedId is not null)
                SelectedEntry = Entries.FirstOrDefault(entry => entry.Id == selectedId);
            StatusText = Entries.Count == 0 ? "没有匹配的资料条目" : $"共 {Entries.Count} 条匹配";
        }
        catch (Exception error)
        {
            StatusText = "读取失败：" + error.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void LoadPreview(LibraryEntry? entry)
    {
        Versions.Clear();
        CitationText = string.Empty;
        if (entry is null)
        {
            PreviewText = string.Empty;
            PreviewNote = "选择左侧条目查看预览。";
            return;
        }

        foreach (var version in _service.ListVersions(entry.Id)) Versions.Add(version);
        OnPropertyChanged(nameof(HasVersions));

        var text = _service.GetPreviewText(entry.Id) ?? string.Empty;
        if (text.Length > PreviewCharLimit)
        {
            PreviewText = text[..PreviewCharLimit];
            PreviewNote = $"预览截断显示前 {PreviewCharLimit} 字符；全文仍参与检索。";
        }
        else
        {
            PreviewText = text;
            PreviewNote = text.Length == 0
                ? "没有可预览的文本：" + (entry.TextError ?? entry.TextStateText)
                : entry.TextError ?? string.Empty;
        }
    }

    [RelayCommand]
    private async Task ImportFilesAsync()
    {
        if (Picker is null)
        {
            StatusText = "尚未接线文件选择器（宿主需注入 ILibraryFilePicker）。";
            return;
        }
        var files = await Picker.PickFilesAsync().ConfigureAwait(true);
        if (files.Count == 0) return;
        IsBusy = true;
        try
        {
            var requests = files.Select(path => new LibraryImportRequest(
                LibraryEntryKind.File,
                ImportAsCopy ? LibraryStorageMode.Copy : LibraryStorageMode.Reference,
                path)).ToList();
            var outcomes = await _service.ImportManyAsync(requests).ConfigureAwait(true);
            var imported = outcomes.Count(outcome => outcome.IsImported);
            var duplicates = outcomes.Count(outcome => outcome.Status == LibraryImportStatus.Duplicate);
            var failed = outcomes.Count(outcome => outcome.Status == LibraryImportStatus.Failed);
            StatusText = $"导入完成：新增 {imported}，重复 {duplicates}，失败 {failed}";
            var firstFailure = outcomes.FirstOrDefault(outcome => outcome.Status == LibraryImportStatus.Failed);
            if (firstFailure is not null) StatusText += "；" + firstFailure.Title + "：" + firstFailure.Message;
            await ReloadAsync().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>收藏会话片段或任意文本（聊天“收藏到资料库”由宿主调用同一入口）。</summary>
    public async Task<LibraryImportOutcome> ImportSnippetAsync(string title, string text)
    {
        var outcome = await _service.ImportAsync(new LibraryImportRequest(
            LibraryEntryKind.SessionSnippet, LibraryStorageMode.Copy, Text: text, Title: title)).ConfigureAwait(true);
        StatusText = outcome.Message;
        await ReloadAsync().ConfigureAwait(true);
        return outcome;
    }

    [RelayCommand]
    private async Task RefreshSourcesAsync()
    {
        IsBusy = true;
        try
        {
            var states = await _service.RefreshAllAsync(acceptChange: false).ConfigureAwait(true);
            var changed = states.Count(state => state.Changed);
            var missing = states.Count(state => !state.Exists);
            StatusText = $"来源检查完成：变化 {changed}，不可达 {missing}（未覆盖已记录版本）";
            await ReloadAsync().ConfigureAwait(true);
        }
        catch (Exception error)
        {
            StatusText = "来源检查失败：" + error.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>接受当前选中条目的来源变化：记录新版本并重建索引。</summary>
    [RelayCommand]
    private async Task AcceptChangeAsync()
    {
        if (SelectedEntry is null) return;
        IsBusy = true;
        try
        {
            var state = await _service.RefreshStatusAsync(SelectedEntry.Id, acceptChange: true).ConfigureAwait(true);
            StatusText = state.Reason ?? "已同步来源变化";
            await ReloadAsync().ConfigureAwait(true);
            SelectedEntry = Entries.FirstOrDefault(entry => entry.Id == SelectedEntry?.Id) ?? SelectedEntry;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RebuildIndexAsync()
    {
        IsBusy = true;
        var progress = new Progress<LibraryIndexProgress>(update => IndexStatusText = update.Text);
        try
        {
            var result = await Task.Run(() => _service.RebuildIndexAsync(progress)).ConfigureAwait(true);
            StatusText = "索引重建完成：" + result.Total + " 条";
            await ReloadAsync().ConfigureAwait(true);
        }
        catch (Exception error)
        {
            StatusText = "索引重建失败：" + error.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        if (Picker is null)
        {
            StatusText = "尚未接线目录选择器（宿主需注入 ILibraryFilePicker）。";
            return;
        }
        var targets = Entries.Where(entry => entry.Id == SelectedEntry?.Id).Select(entry => entry.Id).ToList();
        if (targets.Count == 0)
        {
            StatusText = "请先选择要导出的条目。";
            return;
        }
        var folder = await Picker.PickFolderAsync("选择导出目录").ConfigureAwait(true);
        if (folder is null) return;
        IsBusy = true;
        try
        {
            var result = await _service.ExportAsync(targets, folder).ConfigureAwait(true);
            StatusText = $"已导出 {result.EntryCount} 条到 {result.ManifestPath}";
            if (result.Warnings.Count > 0) StatusText += "；" + string.Join("；", result.Warnings);
        }
        catch (Exception error)
        {
            StatusText = "导出失败：" + error.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ImportBundleAsync()
    {
        if (Picker is null)
        {
            StatusText = "尚未接线目录选择器（宿主需注入 ILibraryFilePicker）。";
            return;
        }
        var folder = await Picker.PickFolderAsync("选择资料包目录（含 manifest.json）").ConfigureAwait(true);
        if (folder is null) return;
        IsBusy = true;
        try
        {
            var result = await _service.ImportBundleAsync(folder).ConfigureAwait(true);
            StatusText = $"资料包导入：新增 {result.Imported}，重复 {result.Duplicates}，失败 {result.Failed}";
            await ReloadAsync().ConfigureAwait(true);
        }
        catch (Exception error)
        {
            StatusText = "资料包导入失败：" + error.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>两段式删除：首次点击只进入待确认状态，第二次才真正删除。</summary>
    [RelayCommand]
    private async Task DeleteSelectedAsync()
    {
        if (SelectedEntry is null) return;
        if (!DeleteArmed)
        {
            DeleteArmed = true;
            StatusText = "再次点击“删除”确认：只删除资料库条目与副本，不会删除你的源文件。";
            return;
        }
        DeleteArmed = false;
        IsBusy = true;
        try
        {
            var title = SelectedEntry.Title;
            var source = SelectedEntry.SourcePath;
            var removed = _service.DeleteEntry(SelectedEntry.Id);
            SelectedEntry = null;
            StatusText = removed
                ? $"已删除条目《{title}》；源文件{(source is null ? "不适用" : "保持原样：" + source)}"
                : "条目已不存在";
            await ReloadAsync().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task AddTagAsync()
    {
        if (SelectedEntry is null || string.IsNullOrWhiteSpace(NewTag)) return;
        _service.AddTag(SelectedEntry.Id, NewTag.Trim());
        StatusText = "已添加标签：" + NewTag.Trim();
        NewTag = string.Empty;
        await ReloadAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RemoveTagAsync(string? tag)
    {
        if (SelectedEntry is null || string.IsNullOrWhiteSpace(tag)) return;
        _service.RemoveTag(SelectedEntry.Id, tag);
        StatusText = "已移除标签：" + tag;
        await ReloadAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void ClearTagFilter() => SelectedTag = null;
    /// <summary>视图模型是否已释放。释放后的实例不可重新挂载，宿主重建页面时应新建实例。</summary>
    public bool IsDisposed => _disposed;

    [RelayCommand]
    private void CopyCitation()
    {
        if (SelectedEntry is null) return;
        var citation = _service.BuildCitation(SelectedEntry.Id, SearchText);
        if (citation is null) return;
        CitationText = citation.Markdown;
        CitationCopyRequested?.Invoke(CitationText);
        StatusText = "已生成引用（含来源与版本）；聊天接线后可直接插入输入框。";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _indexCts?.Cancel();
        _indexCts?.Dispose();
        _indexCts = null;
    }
}
