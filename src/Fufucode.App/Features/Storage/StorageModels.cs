using System;
using System.Collections.Generic;
using System.Linq;

namespace Fufucode.App.Features.Storage;

/// <summary>存储区域类别。只用于分组展示与说明，不决定清理与否。</summary>
public enum StorageAreaKind
{
    AppRoot,
    Runtime,
    AgentConfig,
    Sessions,
    Logs,
    Exports,
    BrowserData,
    Library,
}

/// <summary>区域参与清理的方式。<see cref="Protected"/> 的区域永不进入清理目标。</summary>
public enum StorageAreaCleanupPolicy
{
    /// <summary>保护：不参与任何清理（会话、配置、认证、资料、运行时、用户导出）。</summary>
    Protected,
    /// <summary>仅清理超过保留天数的诊断日志。</summary>
    ExpiredLogs,
    /// <summary>可重建缓存（默认清理）与需明确确认的浏览器登录/站点数据（含离线存储）。</summary>
    CacheAndSiteData,
}

/// <summary>一次清理可选中的逻辑范围。默认只勾选前两项。</summary>
public enum StorageCleanupScope
{
    /// <summary>可重建缓存（浏览器 HTTP/着色器等运行时缓存）；默认勾选。</summary>
    RebuildableCache,
    /// <summary>超过保留天数的诊断日志；默认勾选。</summary>
    ExpiredLogs,
    /// <summary>
    /// 浏览器登录/站点数据：cookie、登录态、历史、站点存储与 Service Worker 离线存储（含 CacheStorage）。
    /// 默认关闭，需明确确认，会影响网站登录与离线可用性。
    /// </summary>
    BrowserSiteData,
}

/// <summary>被计量的存储区域。<see cref="Path"/> 是实际路径，界面不拼接、不猜测。</summary>
public sealed record StorageArea(
    string Id,
    string Title,
    string Path,
    StorageAreaKind Kind,
    StorageAreaCleanupPolicy CleanupPolicy,
    string Description)
{
    public bool IsProtected => CleanupPolicy == StorageAreaCleanupPolicy.Protected;
}

/// <summary>
/// 单个区域的计量结果。<see cref="SkippedEntries"/> 是未能统计的条目数
/// （不可读、以及未跟随的重解析点），因此「已计量大小」是下界而非估计值。
/// </summary>
public sealed record StorageAreaUsage(
    string Path,
    bool Exists,
    long Bytes,
    long FileCount,
    long DirectoryCount,
    long SkippedEntries)
{
    public static StorageAreaUsage Missing(string path) => new(path, false, 0, 0, 0, 0);
}

public sealed record StorageAreaMeasurement(StorageArea Area, StorageAreaUsage Usage);

/// <summary>整个数据根的计量结果。<see cref="Completed"/> 为 false 表示被取消（结果不完整）。</summary>
public sealed record StorageMeasurement(
    IReadOnlyList<StorageAreaMeasurement> Areas,
    DateTime MeasuredUtc,
    bool Completed)
{
    public long TotalBytes => Areas.Sum(area => area.Usage.Bytes);
    public long TotalFiles => Areas.Sum(area => area.Usage.FileCount);
    public long TotalSkipped => Areas.Sum(area => area.Usage.SkippedEntries);
}

/// <summary>计量进度（已完成区域数 / 总量、当前累计字节）。</summary>
public sealed record StorageMeasurementProgress(int AreasDone, int AreasTotal, long Bytes, string CurrentAreaTitle);

/// <summary>
/// 清理目标在预览时的快照。执行时用它核对「没有变化」：文件目标比对长度与最后写入时间，
/// 目录目标比对相对文件/目录清单（删除范围与用户确认的预览一致，预览后新增内容不纳入）。
/// </summary>
public sealed record StorageTargetSnapshot(
    long Bytes,
    long FileCount,
    DateTime? LastWriteUtc,
    long? Length,
    IReadOnlyList<string> Files,
    IReadOnlyList<string> Directories)
{
    public static StorageTargetSnapshot ForFile(long length, DateTime lastWriteUtc) =>
        new(length, 1, lastWriteUtc, length, [], []);

    public static StorageTargetSnapshot ForDirectory(long bytes, long fileCount,
        IReadOnlyList<string> files, IReadOnlyList<string> directories) =>
        new(bytes, fileCount, null, null, files, directories);
}

/// <summary>
/// 清理目标。<see cref="IsDirectory"/> 为真时只清空目录内容（且只清预览时存在的条目）、目录本身保留；
/// <see cref="Snapshot"/> 是执行时的核对依据。
/// </summary>
public sealed record StorageCleanupTarget(
    string AreaId,
    string Path,
    StorageCleanupScope Scope,
    bool IsDirectory,
    StorageTargetSnapshot Snapshot);

/// <summary>跳过原因：均为「不删除、如实报告」，不同于删除失败的占用/权限错误。</summary>
public enum StorageCleanupSkipKind
{
    /// <summary>重解析点（junction / 符号链接 / 文件链接）：不跟随、不删除链接目标。</summary>
    ReparsePoint,
    /// <summary>目标不在允许清理的区域内。</summary>
    OutOfArea,
    /// <summary>执行时目标已不存在。</summary>
    Missing,
    /// <summary>预览后目标内容/身份已变化（重新写入、新增内容、类型改变）。</summary>
    ChangedSincePreview,
    /// <summary>执行时日志已不再超过保留期限。</summary>
    NotExpired,
    /// <summary>目标或路径链上存在无法读取属性的条目：按不可安全删除处理。</summary>
    Unreadable,
}

/// <summary>被跳过的目标或条目：必须可见并带原因，不能静默当作清理成功。</summary>
public sealed record StorageCleanupSkip(string Path, StorageCleanupSkipKind Kind, string Reason);

/// <summary>
/// 清理预览：实际目标 + 预计大小 + 跳过项。<see cref="Warnings"/> 是会影响结果或需要用户判断的提示。
/// </summary>
public sealed record StorageCleanupPreview(
    IReadOnlyCollection<StorageCleanupScope> Scopes,
    IReadOnlyList<StorageCleanupTarget> Targets,
    IReadOnlyList<string> AffectedAreaIds,
    long TotalBytes,
    long TotalFiles,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<StorageCleanupSkip> Skipped,
    DateTime GeneratedUtc);

public sealed record StorageCleanupFailure(string Path, string Reason);

public sealed record StorageCleanupProgress(int TargetsDone, int TargetsTotal, string CurrentPath);

/// <summary>
/// 清理实际结果。<see cref="BytesBefore"/>/<see cref="BytesAfter"/> 是受影响区域在清理前后的实际计量，
/// 作为「占用变化」的证据；<see cref="Skipped"/> 是未删除的原因，<see cref="Failures"/> 是删除失败的原因；
/// <see cref="UnprocessedTargets"/> 是被取消时尚未开始的目标数。
/// </summary>
public sealed record StorageCleanupResult(
    IReadOnlyCollection<StorageCleanupScope> Scopes,
    long BytesBefore,
    long BytesAfter,
    int DeletedFiles,
    int DeletedDirectories,
    long DeletedBytes,
    IReadOnlyList<StorageCleanupSkip> Skipped,
    IReadOnlyList<StorageCleanupFailure> Failures,
    bool Canceled,
    bool Completed,
    int UnprocessedTargets,
    DateTime StartedUtc,
    DateTime FinishedUtc)
{
    public long BytesFreed => Math.Max(0, BytesBefore - BytesAfter);
}
