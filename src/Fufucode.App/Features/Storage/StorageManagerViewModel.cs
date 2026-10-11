using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Fufucode.App.Features.Storage;

/// <summary>一个存储区域的展示行：实际路径、清理策略与计量结果。</summary>
public sealed partial class StorageAreaRow : ObservableObject
{
    public StorageAreaRow(StorageArea area) => Area = area;

    public StorageArea Area { get; }
    public string Id => Area.Id;
    public string Title => Area.Title;
    public string Path => Area.Path;
    public string Description => Area.Description;
    public bool IsProtected => Area.IsProtected;

    public string PolicyText => Area.CleanupPolicy switch
    {
        StorageAreaCleanupPolicy.Protected => "不参与清理",
        StorageAreaCleanupPolicy.ExpiredLogs => "仅清理过期日志",
        _ => "缓存默认清理；登录数据需确认",
    };

    [ObservableProperty] private string _usageText = "未计量";
    [ObservableProperty] private bool _measured;

    public bool HasUsage => Measured;
    partial void OnMeasuredChanged(bool value) => OnPropertyChanged(nameof(HasUsage));
}

/// <summary>一个可选清理范围：默认状态、说明与会影响结果的提示。</summary>
public sealed partial class StorageScopeOption : ObservableObject
{
    public StorageScopeOption(StorageCleanupScope scope, string title, string description, bool defaultEnabled, string? warning)
    {
        Scope = scope;
        Title = title;
        _description = description;
        IsDefaultEnabled = defaultEnabled;
        Warning = warning;
        _isEnabled = defaultEnabled;
    }

    public StorageCleanupScope Scope { get; }
    public string Title { get; }
    [ObservableProperty] private string _description;
    public bool IsDefaultEnabled { get; }
    public string? Warning { get; }
    public bool HasWarning => !string.IsNullOrWhiteSpace(Warning);

    [ObservableProperty] private bool _isEnabled;
}

public sealed record StorageTargetRow(StorageCleanupScope Scope, string Path, string ScopeText, string SizeText, string DetailText);

public sealed record StorageFailureRow(string Path, string Reason);

/// <summary>被跳过的目标或条目：必须可见并带原因，不能静默当作清理成功。</summary>
public sealed record StorageSkipRow(string Path, string KindText, string Reason);

/// <summary>
/// 存储与数据管理 VM（任务书 M2）：区域计量（可取消）、保留策略、清理预览与实际结果。
/// 只做展示与编排，不触碰共享外壳；目录打开由宿主注入（<c>openFolder</c>）。
/// 默认范围固定为「可重建缓存 + 过期日志」，浏览器登录/站点数据必须显式勾选。
/// </summary>
public sealed partial class StorageManagerViewModel : ObservableObject, IDisposable
{
    private readonly StorageCatalog _catalog;
    private readonly StorageUsageMeter _meter;
    private readonly Action<string>? _openFolder;
    private StorageRetentionPolicy _policy;
    private StorageCleaner _cleaner;
    private CancellationTokenSource? _measureCts;
    private CancellationTokenSource? _cleanCts;
    private StorageCleanupPreview? _preview;
    private bool _disposed;

    public StorageManagerViewModel(
        StorageCatalog catalog,
        int logRetentionDays = StorageRetentionPolicy.DefaultLogRetentionDays,
        Action<string>? openFolder = null,
        StorageUsageMeter? meter = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _catalog = catalog;
        _meter = meter ?? new StorageUsageMeter();
        _openFolder = openFolder;
        _policy = new StorageRetentionPolicy(logRetentionDays);
        _cleaner = new StorageCleaner(_catalog, _policy, _meter);
        _logRetentionDays = _policy.LogRetentionDays;
        _retentionText = _policy.Describe();

        foreach (var area in _catalog.Areas) Areas.Add(new StorageAreaRow(area));
        Scopes = new ObservableCollection<StorageScopeOption>
        {
            new(StorageCleanupScope.RebuildableCache, "可重建缓存", "浏览器运行时缓存（WebView2 缓存目录）：删除后由浏览器按需重建。", true, null),
            new(StorageCleanupScope.ExpiredLogs, "过期日志", _policy.Describe() + "。", true, null),
            new(StorageCleanupScope.BrowserSiteData, "浏览器登录/站点数据",
                "cookie、登录态、历史、站点存储与 Service Worker 离线存储（CacheStorage）；清理后相关网站需要重新登录、离线内容需重新下载。", false,
                "警告：清理后网站登录状态与离线可用性会失效；模型认证在 Pi 配置目录，不受影响。"),
        };
        foreach (var option in Scopes) option.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(StorageScopeOption.IsEnabled)) InvalidatePreview("清理范围已改变，请重新预览");
        };
    }

    /// <summary>页面销毁时取消在途的计量/清理；已释放后不再发起新操作。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _measureCts?.Cancel();
        _cleanCts?.Cancel();
    }

    public bool IsDisposed => _disposed;

    /// <summary>需要重建动态列表时触发（区域计量更新、预览、结果）。</summary>
    public event Action? ViewChanged;

    public ObservableCollection<StorageAreaRow> Areas { get; } = [];
    public ObservableCollection<StorageScopeOption> Scopes { get; }
    public ObservableCollection<StorageTargetRow> Targets { get; } = [];
    public ObservableCollection<StorageFailureRow> Failures { get; } = [];
    public ObservableCollection<StorageSkipRow> Skipped { get; } = [];

    [ObservableProperty] private bool _isMeasuring;
    [ObservableProperty] private bool _isPreviewing;
    [ObservableProperty] private bool _isCleaning;
    [ObservableProperty] private string _status = "尚未计量";
    [ObservableProperty] private string _measureStatus = string.Empty;
    [ObservableProperty] private string _measuredText = string.Empty;
    [ObservableProperty] private string _totalText = string.Empty;
    [ObservableProperty] private bool _hasMeasurement;
    [ObservableProperty] private bool _hasPreview;
    [ObservableProperty] private string _previewSummary = string.Empty;
    [ObservableProperty] private string _previewWarnings = string.Empty;
    [ObservableProperty] private bool _hasPreviewWarnings;
    [ObservableProperty] private bool _hasResult;
    [ObservableProperty] private string _resultText = string.Empty;
    [ObservableProperty] private string _retentionText = string.Empty;

    [ObservableProperty] private int _logRetentionDays;

    public bool IsBusy => IsMeasuring || IsPreviewing || IsCleaning;
    public bool CanMeasure => !IsBusy;
    public bool CanCancelMeasure => IsMeasuring;
    public bool CanPreview => !IsBusy;
    public bool CanClean => !IsBusy && HasPreview;
    public bool CanCancelClean => IsCleaning;
    public bool CanOpenFolders => _openFolder is not null;
    public IReadOnlyList<StorageCleanupScope> DefaultScopes => StorageCleaner.DefaultScopes;
    public bool HasFailures => Failures.Count > 0;
    public bool HasSkipped => Skipped.Count > 0;

    /// <summary>当前选中的清理范围（枚举顺序，去重）。</summary>
    public IReadOnlyCollection<StorageCleanupScope> SelectedScopes() =>
        Scopes.Where(option => option.IsEnabled).Select(option => option.Scope).ToArray();

    // ── 计量 ──────────────────────────────────────────────────────────────

    public async Task MeasureAsync()
    {
        if (IsBusy || _disposed) return;
        using var cts = new CancellationTokenSource();
        _measureCts = cts;
        IsMeasuring = true;
        MeasureStatus = "正在计量…";
        try
        {
            var progress = new Progress<StorageMeasurementProgress>(update =>
                MeasureStatus = $"正在计量 {update.CurrentAreaTitle}（{update.AreasDone}/{update.AreasTotal}），已累计 {StorageFormat.Bytes(update.Bytes)}");
            var measurement = await _meter.MeasureAsync(_catalog, progress, cts.Token).ConfigureAwait(true);
            ApplyMeasurement(measurement);
            Status = "计量完成";
        }
        catch (OperationCanceledException)
        {
            // 部分结果不发布：宁可显示「未计量」也不显示不完整数字。
            MeasureStatus = "计量已取消（结果不完整，占用未更新）";
            Status = "计量已取消";
        }
        catch (Exception error)
        {
            MeasureStatus = "计量失败：" + error.Message;
            Status = "计量失败";
        }
        finally
        {
            IsMeasuring = false;
            if (ReferenceEquals(_measureCts, cts)) _measureCts = null;
        }
    }

    public void CancelMeasure() => _measureCts?.Cancel();

    private void ApplyMeasurement(StorageMeasurement measurement)
    {
        foreach (var row in Areas)
        {
            var entry = measurement.Areas.FirstOrDefault(item => item.Area.Id == row.Id);
            if (entry is null) continue;
            row.Measured = true;
            row.UsageText = entry.Usage.Exists
                ? $"{StorageFormat.Bytes(entry.Usage.Bytes)} · {StorageFormat.Count(entry.Usage.FileCount)} 个文件"
                : "目录不存在";
        }

        HasMeasurement = true;
        MeasuredText = $"计量于 {measurement.MeasuredUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}；" +
            $"{StorageFormat.Count(measurement.TotalFiles)} 个文件，合计 {StorageFormat.Bytes(measurement.TotalBytes)}" +
            (measurement.TotalSkipped > 0
                ? $"；{StorageFormat.Count(measurement.TotalSkipped)} 个条目未能统计（不可读或未跟随链接）"
                : string.Empty);
        TotalText = "数据根合计 " + StorageFormat.Bytes(measurement.TotalBytes);
        MeasureStatus = MeasuredText;
        InvalidatePreview(null);
        ViewChanged?.Invoke();
    }

    // ── 预览 ──────────────────────────────────────────────────────────────

    public async Task PreviewCleanupAsync()
    {
        if (IsBusy || _disposed) return;
        var scopes = SelectedScopes();
        IsPreviewing = true;
        Status = "正在生成清理预览…";
        try
        {
            var preview = await _cleaner.BuildPreviewAsync(scopes).ConfigureAwait(true);
            ApplyPreview(preview);
            Status = preview.Targets.Count == 0
                ? "所选范围内没有可清理的目标"
                : $"预览完成：{preview.Targets.Count} 个目标，预计释放 {StorageFormat.Bytes(preview.TotalBytes)}";
        }
        catch (Exception error)
        {
            Status = "预览失败：" + error.Message;
        }
        finally
        {
            IsPreviewing = false;
        }
    }

    private void ApplyPreview(StorageCleanupPreview preview)
    {
        _preview = preview.Targets.Count > 0 ? preview : null;
        Targets.Clear();
        foreach (var target in preview.Targets.OrderByDescending(target => target.Snapshot.Bytes))
        {
            Targets.Add(new StorageTargetRow(target.Scope, target.Path, ScopeTitle(target.Scope),
                StorageFormat.Bytes(target.Snapshot.Bytes) + " · " + StorageFormat.Count(target.Snapshot.FileCount) + " 个文件",
                target.IsDirectory ? "清空目录内容（仅预览时存在的条目，保留目录）" : "删除文件"));
        }
        Skipped.Clear();
        foreach (var skip in preview.Skipped) Skipped.Add(new StorageSkipRow(skip.Path, SkipTitle(skip.Kind), skip.Reason));
        OnPropertyChanged(nameof(HasSkipped));
        PreviewSummary = preview.Targets.Count == 0
            ? "没有目标"
            : $"{preview.Targets.Count} 个目标 · 预计释放 {StorageFormat.Bytes(preview.TotalBytes)} · " +
              $"范围：{string.Join("、", preview.Scopes.Select(ScopeTitle))}";
        PreviewWarnings = string.Join("\n", preview.Warnings);
        HasPreviewWarnings = preview.Warnings.Count > 0;
        HasPreview = _preview is not null;
        if (_preview is null) Status = "所选范围内没有可清理的目标";
        ViewChanged?.Invoke();
    }

    // ── 执行 ──────────────────────────────────────────────────────────────

    public async Task CleanAsync()
    {
        if (IsBusy || _preview is null || _disposed) return;
        var preview = _preview;
        using var cts = new CancellationTokenSource();
        _cleanCts = cts;
        IsCleaning = true;
        Status = "正在清理…";
        try
        {
            var progress = new Progress<StorageCleanupProgress>(update =>
                Status = $"正在清理（{update.TargetsDone}/{update.TargetsTotal}）：{update.CurrentPath}");
            var result = await _cleaner.CleanAsync(preview, progress, cts.Token).ConfigureAwait(true);
            ApplyResult(result);
        }
        catch (OperationCanceledException)
        {
            Status = "清理已取消（尚未删除任何目标）";
        }
        catch (Exception error)
        {
            Status = "清理失败：" + error.Message;
        }
        finally
        {
            IsCleaning = false;
            if (ReferenceEquals(_cleanCts, cts)) _cleanCts = null;
        }
    }

    public void CancelClean() => _cleanCts?.Cancel();

    private void ApplyResult(StorageCleanupResult result)
    {
        Failures.Clear();
        foreach (var failure in result.Failures)
            Failures.Add(new StorageFailureRow(failure.Path, failure.Reason));
        OnPropertyChanged(nameof(HasFailures));

        Skipped.Clear();
        foreach (var skip in result.Skipped) Skipped.Add(new StorageSkipRow(skip.Path, SkipTitle(skip.Kind), skip.Reason));
        OnPropertyChanged(nameof(HasSkipped));

        var parts = new List<string>
        {
            $"删除 {StorageFormat.Count(result.DeletedFiles)} 个文件 / {StorageFormat.Count(result.DeletedDirectories)} 个目录，释放 {StorageFormat.Bytes(result.DeletedBytes)}",
            $"受影响区域占用：{StorageFormat.Bytes(result.BytesBefore)} → {StorageFormat.Bytes(result.BytesAfter)}",
        };
        if (result.UnprocessedTargets > 0) parts.Add($"已取消：{StorageFormat.Count(result.UnprocessedTargets)} 个目标未处理");
        if (result.Skipped.Count > 0) parts.Add($"{StorageFormat.Count(result.Skipped.Count)} 个条目因链接/已变化/不再过期而未删除（见跳过清单）");
        if (result.Failures.Count > 0) parts.Add($"{StorageFormat.Count(result.Failures.Count)} 项失败（被占用或无权限，已保留原文件）");
        ResultText = $"{result.StartedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} 完成；" + string.Join("；", parts) + "。";
        HasResult = true;

        _preview = null;
        Targets.Clear();
        HasPreview = false;
        Status = result.Completed ? "清理完成" : "清理已取消（部分目标已处理）";
        if (HasMeasurement) MeasureStatus = "清理后占用已变化，请重新计量";
        ViewChanged?.Invoke();
    }

    public void OpenFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || _openFolder is null) return;
        _openFolder(path);
    }

    /// <summary>把日志保留天数夹紧到合法区间并重建清理器；预览随之失效。</summary>
    partial void OnLogRetentionDaysChanged(int value)
    {
        var clamped = StorageRetentionPolicy.Clamp(value);
        if (clamped != value)
        {
            LogRetentionDays = clamped;
            return;
        }
        _policy = new StorageRetentionPolicy(clamped);
        _cleaner = new StorageCleaner(_catalog, _policy, _meter);
        RetentionText = _policy.Describe();
        if (Scopes.FirstOrDefault(option => option.Scope == StorageCleanupScope.ExpiredLogs) is { } logs)
            // 说明文本随保留天数更新，界面与执行口径保持一份。
            logs.Description = _policy.Describe() + "。";
        InvalidatePreview("日志保留天数已改变，请重新预览");
    }

    private void InvalidatePreview(string? status)
    {
        _preview = null;
        var hadPreview = HasPreview || Targets.Count > 0;
        Targets.Clear();
        HasPreview = false;
        if (hadPreview)
        {
            ViewChanged?.Invoke();
            if (status is not null) Status = status;
        }
    }

    private static string ScopeTitle(StorageCleanupScope scope) => scope switch
    {
        StorageCleanupScope.RebuildableCache => "可重建缓存",
        StorageCleanupScope.ExpiredLogs => "过期日志",
        _ => "浏览器登录/站点数据",
    };

    private static string SkipTitle(StorageCleanupSkipKind kind) => kind switch
    {
        StorageCleanupSkipKind.ReparsePoint => "链接未跟随",
        StorageCleanupSkipKind.Unreadable => "无法读取属性",
        StorageCleanupSkipKind.OutOfArea => "越界目标",
        StorageCleanupSkipKind.Missing => "已不存在",
        StorageCleanupSkipKind.NotExpired => "不再过期",
        _ => "预览后已变化",
    };

    partial void OnIsMeasuringChanged(bool value) => RaiseBusyState();
    partial void OnIsPreviewingChanged(bool value) => RaiseBusyState();
    partial void OnIsCleaningChanged(bool value) => RaiseBusyState();
    partial void OnHasPreviewChanged(bool value) => RaiseBusyState();
    partial void OnHasMeasurementChanged(bool value) => RaiseBusyState();

    private void RaiseBusyState()
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(CanMeasure));
        OnPropertyChanged(nameof(CanCancelMeasure));
        OnPropertyChanged(nameof(CanPreview));
        OnPropertyChanged(nameof(CanClean));
        OnPropertyChanged(nameof(CanCancelClean));
    }
}
