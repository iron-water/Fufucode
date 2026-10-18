using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fufucode.App.ViewModels;
using Fufucode.Core;

namespace Fufucode.App.Features.Plugins;

/// <summary>版本行：来源、固定版本、摘要、是否选定/待应用/宿主实际加载。</summary>
public sealed class LocalPluginVersionItem
{
    internal LocalPluginVersionItem(LocalPluginVersion version, PluginSourceKind sourceKind,
        bool isSelected, bool isPending, bool isActive)
    {
        Version = version;
        VersionId = version.VersionId;
        VersionText = version.DisplayVersion;
        SourceKind = sourceKind;
        SourceText = sourceKind switch
        {
            PluginSourceKind.LocalDirectory => "本地目录副本",
            _ => "未知来源",
        };
        IsSelected = isSelected;
        IsPending = isPending;
        IsActive = isActive;
        SourceRootText = version.SourceRoot ?? "(来源未记录)";
        ImportedText = version.ImportedAtUtc.LocalDateTime.ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        EntryText = version.EntryRelativePath;
        HashText = version.ContentHash.Length >= 12 ? version.ContentHash[..12] : version.ContentHash;
        SingleLine = $"{VersionText} · {SourceText} · 摘要 {HashText} · 入口 {EntryText}" +
            (isActive ? " · 宿主实际加载" : string.Empty) +
            (isPending ? " · 待应用" : string.Empty) +
            (isSelected && !isActive ? " · 已选定（未确认加载）" : string.Empty);
        StatusText = isActive ? "宿主实际加载" : isPending ? "待应用" : isSelected ? "已选定" : "本地副本";
    }

    public LocalPluginVersion Version { get; }
    public string VersionId { get; }
    public string VersionText { get; }
    public PluginSourceKind SourceKind { get; }
    public string SourceText { get; }
    public string SourceRootText { get; }
    public string ImportedText { get; }
    public string EntryText { get; }
    public string HashText { get; }
    public bool IsSelected { get; }
    public bool IsPending { get; }
    public bool IsActive { get; }
    public string SingleLine { get; }
    public string StatusText { get; }
}

/// <summary>插件行：列表显示来源/固定版本/宿主兼容、选定/待应用/实际加载/加载错误。</summary>
public sealed class LocalPluginRow
{
    internal LocalPluginRow(LocalPluginItem item, string root)
    {
        Item = item;
        Id = item.Id;
        DisplayName = item.DisplayName;
        BackendText = item.BackendText;
        CompatibilityText = item.CompatibilityText;
        StatusText = item.StatusText;
        LoadError = item.LoadError;
        HasLoadError = !string.IsNullOrWhiteSpace(item.LoadError);
        StatusIsError = item.Status == LocalPluginStatus.LoadError || item.Status == LocalPluginStatus.Blocked ||
                        item.Status == LocalPluginStatus.Corrupt;
        StatusIsAttention = item.Status == LocalPluginStatus.Pending;
        StatusIsSuccess = item.IsActive;
        CanApply = item.CanApply;
        CanRollback = item.CanRollback;
        CanRemove = item.CanRemove;
        Versions = item.Versions.Select(version => new LocalPluginVersionItem(version,
            version.SourceKind,
            string.Equals(version.VersionId, item.SelectedVersionId, StringComparison.Ordinal),
            string.Equals(version.VersionId, item.PendingApplyVersionId, StringComparison.Ordinal),
            item.IsActive && string.Equals(version.VersionId, item.SelectedVersionId, StringComparison.Ordinal)))
            .OrderByDescending(v => v.Version.ImportedAtUtc).ToArray();
        CountText = Versions.Count == 1 ? "1 个固定版本" : $"{Versions.Count} 个固定版本";
        SelectedVersionText = Versions.FirstOrDefault(v => v.IsSelected)?.VersionText ?? "(未选定)";
        PendingText = item.PendingApplyVersionId is null ? string.Empty : "待应用：" +
            (Versions.FirstOrDefault(v => v.IsPending)?.VersionText ?? item.PendingApplyVersionId);
        PendingRemoveText = item.PendingRemoveVersionId is null
            ? string.Empty
            : "待移除：" + (Versions.FirstOrDefault(v => v.VersionId == item.PendingRemoveVersionId)?.VersionText
                            ?? item.PendingRemoveVersionId) + (item.PendingRemoveReady ? "（可执行移除）" : "（等待宿主确认）");
        HasPending = item.PendingApplyVersionId is not null ||
                     item.PendingRemoveVersionId is not null || item.PendingRollback;
        InFlightText = item.InFlightVersionIds.Count == 0
            ? string.Empty
            : "在途宿主回调受保护（不可删除）：" + string.Join(", ", item.InFlightVersionIds);
        StorageRootText = Path.Combine(root, PluginIdRules.Sanitize(item.Id, "plugin"));
        SingleLine = $"{DisplayName} · {BackendText} · 选定 {SelectedVersionText} · {CountText}";
    }

    public LocalPluginItem Item { get; }
    public string Id { get; }
    public string DisplayName { get; }
    public string BackendText { get; }
    public string CompatibilityText { get; }
    public string StatusText { get; }
    public string? LoadError { get; }
    public bool HasLoadError { get; }
    public bool StatusIsError { get; }
    public bool StatusIsAttention { get; }
    public bool StatusIsSuccess { get; }
    public bool CanApply { get; }
    public bool CanRollback { get; }
    public bool CanRemove { get; }
    public IReadOnlyList<LocalPluginVersionItem> Versions { get; }
    public string CountText { get; }
    public string SelectedVersionText { get; }
    public string PendingText { get; }
    public string PendingRemoveText { get; }
    public string InFlightText { get; }
    public bool HasPending { get; }
    public string StorageRootText { get; }
    public string SingleLine { get; }
}

/// <summary>
/// 本地插件管理 VM（E3-LOCAL）。可嵌入现有扩展页，不复制窗口/侧边栏，不接主区路径。
/// 选择器/文件夹打开/宿主快照/安全应用全部由调用方注入；没有宿主就不假称加载成功。
/// </summary>
public sealed partial class LocalPluginViewModel : ViewModelBase, IDisposable
{
    private readonly LocalPluginService? _service;
    private readonly ILocalPluginPicker? _picker;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed;
    private int _opSeq;
    private int _previewSeq;
    private bool _hasSnapshot;

    public LocalPluginViewModel(LocalPluginService? service = null, ILocalPluginPicker? picker = null)
    {
        _service = service;
        _picker = picker;
        StatusText = _service is null
            ? "插件目录未接线：宿主需注入 AppDataLayout 根。"
            : "尚未刷新。本地插件是本地可执行代码，信任与权限仍由宿主负责。";
    }

    public ObservableCollection<LocalPluginRow> Plugins { get; } = [];
    public ObservableCollection<LocalPluginVersionItem> Versions { get; } = [];
    public ObservableCollection<PluginReceiptRow> Receipts { get; } = [];
    [ObservableProperty] private PluginReceiptRow? _selectedReceipt;
    [ObservableProperty] private string _receiptObservationText = string.Empty;
    public bool HasReceipts => Receipts.Count != 0;
    public bool CanInspectReceipt => !IsBusy && SelectedPlugin is not null && SelectedReceipt is not null && _service is not null;
    public ObservableCollection<PluginInFlightRow> InFlightRequests { get; } = [];
    [ObservableProperty] private PluginInFlightRow? _selectedInFlight;
    [ObservableProperty] private string _inFlightObservationText = string.Empty;
    public bool HasInFlightRequests => InFlightRequests.Count != 0;
    public bool CanInspectInFlight => !_disposed && !IsBusy && SelectedPlugin is not null && SelectedInFlight is not null && _service is not null;
    private long? _inFlightInspectionGeneration;
    [ObservableProperty] private bool _confirmRegistrationEnd;
    [ObservableProperty] private string _reconciliationHistoryText = string.Empty;
    public bool HasReconciliationHistory => ReconciliationHistoryText.Length != 0;
    public bool CanReconcileInFlight => CanInspectInFlight && ConfirmRegistrationEnd && _inFlightInspectionGeneration is not null;
    partial void OnConfirmRegistrationEndChanged(bool value) => ReconcileInFlightCommand.NotifyCanExecuteChanged();

    partial void OnSelectedInFlightChanged(PluginInFlightRow? value)
    {
        InFlightObservationText = string.Empty;
        _inFlightInspectionGeneration = null;
        ConfirmRegistrationEnd = false;
        InspectInFlightCommand.NotifyCanExecuteChanged();
        ReconcileInFlightCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedReceiptChanged(PluginReceiptRow? value)
    {
        ReceiptObservationText = string.Empty;
        InspectReceiptCommand.NotifyCanExecuteChanged();
    }

    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private string _sourceDirectory = string.Empty;
    [ObservableProperty] private string _entryRelativePath = string.Empty;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasPreview))] private string _previewText = string.Empty;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasPreviewError))] private string _previewError = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _canImport;
    [ObservableProperty] private LocalPluginRow? _selectedPlugin;
    [ObservableProperty] private LocalPluginVersionItem? _selectedVersion;
    [ObservableProperty] private string _hostText = "宿主未接线";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasCorrupt))] private string _corruptText = string.Empty;

    public bool HasPreviewError => PreviewError.Length > 0;
    public bool HasPreview => PreviewText.Length > 0;
    public bool HasCorrupt => CorruptText.Length > 0;
    public bool HasPlugins => Plugins.Count > 0;
    public bool HasHost => _service?.CurrentHostIdentity.IsKnown ?? false;
    public string ApplyButtonText => _service?.SupportsSafeRestart == true && HasHost ? "安全重启并应用" : "应用所选版本";

    public string EmptyText => _service is null
        ? "插件目录未接线（宿主需注入 AppDataLayout 根）。"
        : HasCorrupt ? CorruptText
        : HasPlugins ? string.Empty
        : "本产品本地插件目录为空。用“选择插件目录…”导入本地固定版本副本。";

    public bool CanApply => !IsBusy && SelectedVersion is not null &&
        (_service?.SupportsSafeRestart == true
            ? _service.CanRequestSafeRestart && SelectedPlugin?.Item.Compatibility == PluginCompatibility.Compatible && SelectedPlugin.Item.PendingRemoveVersionId is null
            : SelectedPlugin?.CanApply == true);
    public bool CanRollback => !IsBusy && SelectedPlugin?.CanRollback == true &&
        (_service?.SupportsSafeRestart != true || _service.CanRequestSafeRestart);
    public bool CanRemove => !IsBusy && SelectedVersion is not null && SelectedPlugin?.CanRemove == true;
    public bool CanRefresh => !IsBusy && _service is not null;
    public bool CanPickDirectory => !IsBusy && _picker is not null;
    public bool CanPickEntry => !IsBusy && _picker is not null && SourceDirectory.Trim().Length > 0;
    /// <summary>是否已完成过一次真实目录刷新（用于区分"空目录"与"尚未查询"）。</summary>
    public bool HasSnapshot => _hasSnapshot;

    public void NotifyHostChanged()
    {
        if (_disposed) return;
        OnPropertyChanged(nameof(HasHost));
        OnPropertyChanged(nameof(ApplyButtonText));
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(CanRollback));
    }

    public Task RefreshHostAsync()
    {
        NotifyHostChanged();
        return IsBusy || _disposed ? Task.CompletedTask : RefreshAsync();
    }

    partial void OnSelectedPluginChanged(LocalPluginRow? value)
    {
        ReconciliationHistoryText = value is null ? string.Empty : string.Join("\n", value.Item.Reconciliations.Reverse().Select(item =>
            $"{item.AtUtc.LocalDateTime:yyyy-MM-dd HH:mm:ss} · 已结束登记 · 请求 {item.Request.Token} · 版本 {item.Request.VersionId}\n" +
            $"原宿主返回：{CallbackOutcomeText(item.OriginalReturn.Outcome)}；仅登记对账，未认证应用成功或外部任务终止。"));
        OnPropertyChanged(nameof(HasReconciliationHistory));
        InFlightRequests.Clear();
        SelectedInFlight = null;
        if (value is not null)
            foreach (var request in value.Item.InFlightRequests) InFlightRequests.Add(new PluginInFlightRow(request));
        SelectedInFlight = InFlightRequests.FirstOrDefault();
        OnPropertyChanged(nameof(HasInFlightRequests));
        Receipts.Clear();
        SelectedReceipt = null;
        if (value is not null)
            foreach (var receipt in value.Item.ApplyReceipts) Receipts.Add(new PluginReceiptRow(receipt));
        SelectedReceipt = Receipts.FirstOrDefault();
        OnPropertyChanged(nameof(HasReceipts));
        Versions.Clear();
        SelectedVersion = null;
        if (value is null) return;
        foreach (var version in value.Versions) Versions.Add(version);
        SelectedVersion = Versions.FirstOrDefault(v => v.IsSelected) ?? Versions.FirstOrDefault();
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(CanRollback));
        OnPropertyChanged(nameof(CanRemove));
    }

    partial void OnSelectedVersionChanged(LocalPluginVersionItem? value)
    {
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(CanRemove));
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(CanRollback));
        OnPropertyChanged(nameof(CanRemove));
        OnPropertyChanged(nameof(CanRefresh));
        OnPropertyChanged(nameof(CanPickDirectory));
        OnPropertyChanged(nameof(CanPickEntry));
        PickDirectoryCommand.NotifyCanExecuteChanged();
        RefreshCommand.NotifyCanExecuteChanged();
        PickEntryCommand.NotifyCanExecuteChanged();
        InspectReceiptCommand.NotifyCanExecuteChanged();
        InspectInFlightCommand.NotifyCanExecuteChanged();
        ReconcileInFlightCommand.NotifyCanExecuteChanged();
    }

    partial void OnSourceDirectoryChanged(string value)
    {
        InvalidatePreview();
        OnPropertyChanged(nameof(CanPickEntry));
        PickEntryCommand.NotifyCanExecuteChanged();
    }

    partial void OnEntryRelativePathChanged(string value) => InvalidatePreview();

    private void InvalidatePreview()
    {
        ++_previewSeq;
        CanImport = false;
        PreviewText = string.Empty;
        PreviewError = string.Empty;
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (_disposed || _service is null) return;
        var op = ++_opSeq;
        IsBusy = true;
        try
        {
            var view = await _service.ListAsync(_lifetime.Token);
            if (IsStale(op)) return;
            _hasSnapshot = true;
            OnPropertyChanged(nameof(HasSnapshot));
            HostText = view.HostSnapshotAvailable
                ? $"宿主 {view.Identity.Backend} {view.Identity.Version}（快照可用）"
                : "宿主快照不可用：只能浏览/导入本地副本，应用不可用";
            CorruptText = view.IsCorrupt
                ? $"目录状态损坏，未读取也未重建：{view.CorruptReason}"
                : string.Empty;
            OnPropertyChanged(nameof(HasCorrupt));

            // 刷新必须先清空再重建：否则列表里会同时留下旧行与新行，
            // 索引 0 是刷新前的陈旧行，页面会显示过期状态。
            var selectedId = SelectedPlugin?.Id;
            Plugins.Clear();
            foreach (var item in view.Items) Plugins.Add(new LocalPluginRow(item, _service.Root));
            SelectedPlugin = Plugins.FirstOrDefault(p => p.Id == selectedId) ?? Plugins.FirstOrDefault();
            OnPropertyChanged(nameof(HasPlugins));
            OnPropertyChanged(nameof(EmptyText));
            StatusText = view.IsCorrupt
                ? CorruptText
                : Plugins.Count == 0
                    ? "本产品本地插件目录为空。"
                    : $"共 {Plugins.Count} 个本地插件副本" +
                      (view.HostSnapshotAvailable ? "；宿主快照可用。" : "；宿主快照不可用，未确认加载状态。");
        }
        catch (OperationCanceledException) { if (!_disposed) StatusText = "已取消刷新。"; }
        catch (Exception error)
        {
            if (!IsStale(op) && !_disposed) StatusText = "刷新失败：" + error.GetBaseException().Message;
        }
        finally { if (!IsStale(op) && !_disposed) IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanPickDirectory))]
    private async Task PickDirectoryAsync()
    {
        if (_disposed || _picker is null) { StatusText = "目录选择未接线（宿主需注入 ILocalPluginPicker）。"; return; }
        var op = ++_opSeq;
        IsBusy = true;
        try
        {
            var path = await _picker.PickPluginDirectoryAsync(_lifetime.Token);
            if (IsStale(op) || path is null) return;
            SourceDirectory = path;
            await PreviewAsync();
        }
        catch (OperationCanceledException) { if (!IsStale(op)) StatusText = "已取消目录选择。"; }
        catch (Exception error) { if (!IsStale(op)) StatusText = "目录选择失败：" + error.GetBaseException().Message; }
        finally { if (!IsStale(op)) IsBusy = false; }
    }

    /// <summary>显式选择相对入口；未选择时使用描述文件声明的 entryRelativePath。</summary>
    [RelayCommand(CanExecute = nameof(CanPickEntry))]
    private async Task PickEntryAsync()
    {
        if (_disposed || _picker is null || SourceDirectory.Trim().Length == 0)
        {
            StatusText = "请先选择插件目录，再选择入口（宿主需注入 ILocalPluginPicker）。";
            return;
        }

        var op = ++_opSeq;
        var source = SourceDirectory.Trim();
        IsBusy = true;
        try
        {
            var entry = await _picker.PickEntryFileAsync(source, _lifetime.Token);
            if (IsStale(op) || string.IsNullOrWhiteSpace(entry) || SourceDirectory.Trim() != source) return;
            var root = Path.GetFullPath(source);
            var full = Path.GetFullPath(Path.Combine(root, entry));
            // 只接受包内相对入口，不把外部绝对路径拼进导入请求。
            EntryRelativePath = PluginPackageScanner.IsInsideRoot(root, full)
                ? Path.GetRelativePath(root, full).Replace('\\', '/')
                : entry;
            await PreviewAsync();
        }
        catch (OperationCanceledException) { if (!IsStale(op)) StatusText = "已取消入口选择。"; }
        catch (Exception error) { if (!IsStale(op)) StatusText = "入口选择失败：" + error.GetBaseException().Message; }
        finally { if (!IsStale(op)) IsBusy = false; }
    }

    /// <summary>只读预览：机制校验 + 宿主兼容，不复制任何文件。</summary>
    [RelayCommand]
    private async Task PreviewAsync()
    {
        if (_disposed || _service is null) return;
        var sequence = ++_previewSeq;
        var source = SourceDirectory.Trim();
        var entry = EntryRelativePath.Trim();
        CanImport = false;
        PreviewError = string.Empty;
        PreviewText = string.Empty;
        if (source.Length == 0) return;

        LocalPluginPreview preview;
        try
        {
            preview = await Task.Run(() => _service.Preview(new LocalPluginImportRequest(source,
                entry.Length == 0 ? null : entry)), _lifetime.Token);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception error)
        {
            if (!_disposed && sequence == _previewSeq) PreviewError = "预览失败：" + error.GetBaseException().Message;
            return;
        }
        if (_disposed || sequence != _previewSeq) return;
        if (preview.Rejection is not null)
        {
            PreviewError = preview.Rejection.Message;
            CanImport = false;
            PreviewText = string.Empty;
            return;
        }

        var descriptor = preview.Descriptor;
        PreviewText = $"{descriptor.DisplayName} {descriptor.Version} · {descriptor.Backend} · " +
                      $"入口 {preview.EntryRelativePath} · {preview.CompatibilityText}";
        CanImport = preview.CanImport;
        OnPropertyChanged(nameof(HasPreview));
        OnPropertyChanged(nameof(HasPreviewError));
    }

    /// <summary>导入本地固定版本副本：先校验、再复制到本轮新暂存、再原子发布。</summary>
    [RelayCommand]
    private async Task ImportAsync()
    {
        if (_disposed || _service is null) return;
        if (SourceDirectory.Trim().Length == 0) { StatusText = "请先选择插件目录。"; return; }
        var op = ++_opSeq;
        IsBusy = true;
        try
        {
            var version = await _service.ImportAsync(
                new LocalPluginImportRequest(SourceDirectory.Trim(), EntryRelativePath.Trim()), _lifetime.Token);
            if (IsStale(op)) return;
            StatusText = $"已导入固定版本 {version.DisplayVersion}（摘要 {version.ContentHash[..12]}）；" +
                         "尚未应用，未改动加载目录，未启动会话。";
            await ReloadAsync(op);
        }
        catch (OperationCanceledException) { if (!_disposed) StatusText = "已取消导入。"; }
        catch (Exception error)
        {
            if (!IsStale(op) && !_disposed) StatusText = "导入失败：" + error.GetBaseException().Message;
        }
        finally { if (!IsStale(op) && !_disposed) IsBusy = false; }
    }

    [RelayCommand]
    private async Task ApplyAsync()
    {
        if (_disposed || _service is null || SelectedPlugin is null || SelectedVersion is null) return;
        var pluginId = SelectedPlugin.Id;
        var versionId = SelectedVersion.VersionId;
        var op = ++_opSeq;
        IsBusy = true;
        try
        {
            var result = _service.SupportsSafeRestart
                ? await _service.ApplyVersionWithRestartAsync(pluginId, versionId, _lifetime.Token)
                : await _service.ApplyVersionAsync(pluginId, versionId, _lifetime.Token);
            if (IsStale(op)) return;
            StatusText = result.Message;
            await ReloadAsync(op);
        }
        catch (OperationCanceledException) { if (!_disposed) StatusText = "已取消应用。"; }
        catch (Exception error)
        {
            if (!IsStale(op) && !_disposed) StatusText = "应用失败（保留旧版本与文件）：" + error.GetBaseException().Message;
        }
        finally { if (!IsStale(op) && !_disposed) IsBusy = false; }
    }

    [RelayCommand]
    private async Task RollbackAsync()
    {
        if (_disposed || _service is null || SelectedPlugin is null) return;
        var pluginId = SelectedPlugin.Id;
        var op = ++_opSeq;
        IsBusy = true;
        try
        {
            var result = _service.SupportsSafeRestart
                ? await _service.RollbackWithRestartAsync(pluginId, _lifetime.Token)
                : await _service.RollbackAsync(pluginId, _lifetime.Token);
            if (IsStale(op)) return;
            StatusText = result.Success ? "回退：" + result.Message : "回退未生效（保留旧选定版本）：" + result.Message;
            await ReloadAsync(op);
        }
        catch (OperationCanceledException) { if (!_disposed) StatusText = "已取消回退。"; }
        catch (Exception error)
        {
            if (!IsStale(op) && !_disposed) StatusText = "回退失败：" + error.GetBaseException().Message;
        }
        finally { if (!IsStale(op) && !_disposed) IsBusy = false; }
    }

    [RelayCommand]
    private async Task RemoveAsync()
    {
        if (_disposed || _service is null || SelectedPlugin is null || SelectedVersion is null) return;
        var pluginId = SelectedPlugin.Id;
        var versionId = SelectedVersion.VersionId;
        var op = ++_opSeq;
        IsBusy = true;
        try
        {
            var result = await _service.RemoveVersionAsync(pluginId, versionId, _lifetime.Token);
            if (IsStale(op)) return;
            StatusText = result.Message;
            await ReloadAsync(op);
        }
        catch (OperationCanceledException) { if (!_disposed) StatusText = "已取消移除。"; }
        catch (Exception error)
        {
            if (!IsStale(op) && !_disposed) StatusText = "移除失败（目录状态保留）：" + error.GetBaseException().Message;
        }
        finally { if (!IsStale(op) && !_disposed) IsBusy = false; }
    }

    private async Task ReloadAsync(int op)
    {
        if (_service is null || IsStale(op)) return;
        Plugins.Clear();
        Versions.Clear();
        var view = await _service.ListAsync(_lifetime.Token);
        if (IsStale(op)) return;
        CorruptText = view.IsCorrupt ? $"目录状态损坏，未读取也未重建：{view.CorruptReason}" : string.Empty;
        OnPropertyChanged(nameof(HasCorrupt));
        foreach (var item in view.Items) Plugins.Add(new LocalPluginRow(item, _service.Root));
        OnPropertyChanged(nameof(HasPlugins));
        OnPropertyChanged(nameof(EmptyText));
        SelectedPlugin = Plugins.FirstOrDefault();
    }

    /// <summary>宿主（M）完成安全重启后调用：只登记真实快照确认的事实。</summary>
    [RelayCommand]
    private async Task ResolvePendingAsync()
    {
        if (_disposed || _service is null) return;
        var op = ++_opSeq;
        IsBusy = true;
        try
        {
            var resolved = await _service.ResolvePendingAsync(_lifetime.Token);
            if (IsStale(op)) return;
            StatusText = resolved == 0
                ? "宿主快照未确认任何待应用/待移除项。"
                : $"已按真实宿主快照确认 {resolved} 项（未自动执行更新/重启/模型请求）。";
            await ReloadAsync(op);
        }
        catch (OperationCanceledException) { if (!_disposed) StatusText = "已取消确认。"; }
        catch (Exception error)
        {
            if (!IsStale(op) && !_disposed) StatusText = "确认失败：" + error.GetBaseException().Message;
        }
        finally { if (!IsStale(op) && !_disposed) IsBusy = false; }
    }

    private bool IsStale(int op) => _disposed || op != _opSeq;

    [RelayCommand(CanExecute = nameof(CanInspectReceipt))]
    private async Task InspectReceiptAsync()
    {
        if (!CanInspectReceipt) return;
        var id = SelectedPlugin!.Id;
        var token = SelectedReceipt!.Receipt.Token;
        var op = ++_opSeq;
        IsBusy = true;
        try
        {
            var result = await _service!.InspectReceiptAsync(id, token, _lifetime.Token);
            if (IsStale(op) || SelectedPlugin?.Id != id || SelectedReceipt?.Receipt.Token != token) return;
            ReceiptObservationText = $"核对时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}；目录代次：{result.CatalogGeneration}\n" +
                result.Explanation + (result.HostContext is { } context ? "\n宿主身份：" + context : string.Empty);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!IsStale(op) && SelectedPlugin?.Id == id && SelectedReceipt?.Receipt.Token == token)
                ReceiptObservationText = "只读核对失败：" + error.GetBaseException().Message;
        }
        finally { if (!IsStale(op)) IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanInspectInFlight))]
    private async Task InspectInFlightAsync()
    {
        if (!CanInspectInFlight) return;
        var id = SelectedPlugin!.Id;
        var token = SelectedInFlight!.Request.Token;
        var op = ++_opSeq;
        IsBusy = true;
        _inFlightInspectionGeneration = null;
        ConfirmRegistrationEnd = false;
        InFlightObservationText = "正在只读核对原未结束请求；保护保持。";
        try
        {
            var result = await _service!.InspectInFlightAsync(id, token, _lifetime.Token);
            if (IsStale(op) || SelectedPlugin?.Id != id || SelectedInFlight?.Request.Token != token) return;
            if (result.Kind != PluginReceiptObservationKind.Stale) _inFlightInspectionGeneration = result.CatalogGeneration;
            var stateText = result.Kind switch
            {
                PluginReceiptObservationKind.Loaded => "当前加载原版本",
                PluginReceiptObservationKind.NotLoaded => "当前未加载原版本",
                PluginReceiptObservationKind.Stale => "核对已过期",
                _ => "无法确认",
            };
            InFlightObservationText = $"核对时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}\n原请求：{token}\n" +
                $"当前观察：{stateText}\n目录代次：{result.CatalogGeneration}\n宿主上下文：{result.HostContext ?? "未确认"}\n{result.Explanation}\n" +
                (result.CallbackCompletion is { } completion
                    ? $"原宿主返回：{CallbackOutcomeText(completion.Outcome)}（{completion.ReturnedAtUtc.LocalDateTime:yyyy-MM-dd HH:mm:ss}）\n" +
                      $"原宿主上下文：{completion.HostContext ?? "未记录"}\n原返回说明：{completion.Message}\n" +
                      (completion.OriginalProcessExit is { } exited
                          ? $"原RPC进程已退出：PID {exited.ProcessId}，退出码 {exited.ExitCode}\n原连接代次：{exited.Generation}\n仅此进程退出；原请求保护保持。\n"
                          : "原RPC进程退出尚未关联；保护保持。\n")
                    : string.Empty) + result.CallbackDiagnostic;
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!IsStale(op) && SelectedPlugin?.Id == id && SelectedInFlight?.Request.Token == token)
                InFlightObservationText = "只读核对失败：" + error.GetBaseException().Message;
        }
        finally { if (!IsStale(op)) IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanReconcileInFlight))]
    private async Task ReconcileInFlightAsync()
    {
        if (!CanReconcileInFlight) return;
        var id = SelectedPlugin!.Id;
        var token = SelectedInFlight!.Request.Token;
        var generation = _inFlightInspectionGeneration!.Value;
        var op = ++_opSeq;
        IsBusy = true;
        ConfirmRegistrationEnd = false;
        try
        {
            var result = await _service!.ReconcileReturnedRequestAsync(id, token, generation, _lifetime.Token);
            if (IsStale(op) || SelectedPlugin?.Id != id || SelectedInFlight?.Request.Token != token) return;
            StatusText = result.Message;
            if (result.Success) await ReloadAsync(op);
            else InFlightObservationText = result.Message;
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!IsStale(op) && SelectedPlugin?.Id == id && SelectedInFlight?.Request.Token == token)
                InFlightObservationText = "对账未提交，原登记保留：" + error.GetBaseException().Message;
        }
        finally { if (!IsStale(op)) IsBusy = false; }
    }

    public bool IsDisposed => _disposed;

    private static string CallbackOutcomeText(string outcome) => outcome switch
    {
        "returned_success" => "调用返回成功（未认证目录提交）",
        "returned_failure" => "调用返回失败",
        "cancelled" => "调用抛出取消，副作用未知",
        _ => "调用抛出异常",
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _opSeq++;
        _lifetime.Cancel();
        IsBusy = false;
        InspectInFlightCommand.NotifyCanExecuteChanged();
        ReconcileInFlightCommand.NotifyCanExecuteChanged();
    }
}

public sealed record PluginReceiptRow(ApplyReceipt Receipt)
{
    public string Caption => $"{Receipt.AtUtc.LocalDateTime:MM-dd HH:mm:ss} · 版本 {Receipt.VersionId} · " + (Receipt.Outcome switch
    {
        "confirmed" => "原请求已确认", "cancelled_unknown" => "已取消，结果未知", "failed" => "原请求失败",
        "threw" => "原请求异常", "cancelled" => "调用前取消", _ => "原结果：" + Receipt.Outcome,
    });
    public string OriginalDetails => $"原请求标识：{Receipt.Token}\n原版本：{Receipt.VersionId}\n原结果：{Receipt.Outcome}\n原说明：{Receipt.Message}";
}

public sealed record PluginInFlightRow(InFlightCandidate Request)
{
    public string Caption => $"{Request.StartedAtUtc.LocalDateTime:MM-dd HH:mm:ss} · 版本 {Request.VersionId} · 未结束，保护保持 · 请求 {Request.Token}";
    public string OriginalDetails => $"原请求标识：{Request.Token}\n原版本：{Request.VersionId}\n登记时间：{Request.StartedAtUtc:O}\n尚无结束回执；当前加载观察不能证明回调已结束。";
}
