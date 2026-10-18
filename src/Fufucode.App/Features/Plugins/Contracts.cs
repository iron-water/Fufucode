using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json.Serialization;
using Fufucode.Core;

namespace Fufucode.App.Features.Plugins;

/// <summary>本产品本地插件包的后端标识。首版只有 Pi 有真实加载路径。</summary>
public enum PluginBackend
{
    Unknown,
    Pi,
    Omp,
    Codex,
    Dsh,
}

/// <summary>来源类型：本产品只从本地目录导入固定版本副本。</summary>
public enum PluginSourceKind
{
    Unknown,
    LocalDirectory,
}

/// <summary>
/// 宿主兼容判定结果。判定依据是描述文件声明的精确宿主版本列表与实际注入的宿主身份，
/// 不是文件存在、不是 enabled、也不是"看起来像 Pi 扩展"。
/// </summary>
public enum PluginCompatibility
{
    /// <summary>声明的宿主版本列表包含实际注入的宿主版本。</summary>
    Compatible,
    /// <summary>缺少宿主身份，或声明/实际版本无法核对。</summary>
    Unknown,
    /// <summary>声明与注入身份不匹配（后端不同或版本不在精确列表内）。</summary>
    Incompatible,
    /// <summary>后端属于首版明确不支持的范围（OMP / Codex / DSH）。</summary>
    Unsupported,
}

/// <summary>插件在本产品本地目录中的可见状态。</summary>
public enum LocalPluginStatus
{
    /// <summary>已导入固定版本副本，尚未选定/应用。</summary>
    Installed,
    /// <summary>已导入，且有可用固定版本等待宿主安全应用。</summary>
    ReadyToImport,
    /// <summary>已登记待应用/待回退/待移除，等宿主下一次安全重启。</summary>
    Pending,
    /// <summary>宿主快照确认该副本已被当前进程实际加载。</summary>
    Loaded,
    /// <summary>上一次应用或宿主校验失败，保留诊断。</summary>
    LoadError,
    /// <summary>兼容判定为 Incompatible 或 Unsupported，拒绝应用。</summary>
    Blocked,
    /// <summary>目录状态文件损坏或版本未知，不能静默重建。</summary>
    Corrupt,
}

/// <summary>宿主身份：由调用方注入的真实运行身份，模块不猜、不读全局配置。</summary>
public sealed record HostIdentity(
    PluginBackend Backend,
    string? Version)
{
    /// <summary>没有身份或版本为空：任何应用都必须标 unknown 并拒绝。</summary>
    public bool IsKnown => Backend != PluginBackend.Unknown && !string.IsNullOrWhiteSpace(Version);

    public static HostIdentity Unknown() => new(PluginBackend.Unknown, null);

    public static HostIdentity Pi(string version) => new(PluginBackend.Pi, version);
}

/// <summary>
/// 本产品本地插件包描述文件（fufucode-plugin.json）的模型。
/// 它是 Fufucode 自己的本地包描述，不伪装成 Pi/OMP 原生格式。
/// </summary>
public sealed record PluginDescriptor(
    string Id,
    string DisplayName,
    string Version,
    PluginBackend Backend,
    string EntryRelativePath,
    IReadOnlyList<string> SupportedHostVersions)
{
    public string? Description { get; init; }
    public string? SourceLabel { get; init; }
}

/// <summary>导入请求：本地插件目录 + 显式选择的相对入口。</summary>
public sealed record LocalPluginImportRequest(
    string SourceDirectory,
    string? EntryRelativePath = null);

/// <summary>导入预览：校验与兼容性结果，不产生任何副本。</summary>
public sealed record LocalPluginPreview(
    PluginDescriptor Descriptor,
    string EntryRelativePath,
    string EntryAbsolutePath,
    PluginCompatibility Compatibility,
    string CompatibilityText,
    LocalPluginImportRejection? Rejection)
{
    /// <summary>
    /// 机制校验通过即可导入本地副本：任务书明确"宿主缺失照常浏览/安装本地副本，应用不可用"，
    /// 因此兼容性不是导入门禁，而是应用门禁。
    /// </summary>
    public bool CanImport => Rejection is null;

    /// <summary>只有声明与注入宿主身份精确匹配才允许应用。</summary>
    public bool CanApplyVersion => Rejection is null && Compatibility == PluginCompatibility.Compatible;
}

/// <summary>导入/更新的显式拒绝原因。机制校验与描述文件校验分开，便于定位。</summary>
public enum LocalPluginRejectionKind
{
    None,
    MissingDescriptor,
    MalformedDescriptor,
    MissingEntry,
    EntryOutsideRoot,
    SymlinkTraversal,
    SizeLimitExceeded,
    FileCountLimitExceeded,
    SourceMissing,
    SourceUnreadable,
    VersionConflict,
    BackendUnsupported,
    IncompatibleHost,
    UnknownHost,
    PluginMissing,
    VersionMissing,
    HostUnavailable,
    ActiveInUse,
    LockedOrCorrupt,
    RemoveNotAllowed,
    StaleOperation,
    OperationBusy,
    OperationCancelled,
    IntegrityMismatch,
}

public sealed record LocalPluginImportRejection(LocalPluginRejectionKind Kind, string Message);

/// <summary>宿主真实加载快照。由调用方注入，模块不自行探测进程。</summary>
public sealed record HostPluginSnapshot(
    bool Success,
    HostIdentity Identity,
    IReadOnlyList<string> LoadedExtensionPaths,
    string? Error = null)
{
    /// <summary>真实宿主的项目/会话/连接代次绑定；仅显式安全重启回调使用，不凭UI标签猜身份。</summary>
    public string? ContextId { get; init; }
    public Guid? Generation { get; init; }
    public PluginHostContext? StructuredContext { get; init; }
    /// <summary>快照本身不可用：活动状态未知，只能登记 pending。</summary>
    public bool IsUnknown => !Success;

    public static HostPluginSnapshot Unavailable(string error) =>
        new(false, HostIdentity.Unknown(), [], error);
}

/// <summary>安全应用/校验结果。回调由宿主（M）接入既有安全重启。</summary>
public sealed record HostApplyResult(bool Success, string? Message = null)
{
    /// <summary>Only the original RPC child observed exiting; does not authorize protection release.</summary>
    public RpcProcessExit? OriginalProcessExit { get; init; }
}

/// <summary>
/// 宿主接线契约。四项能力全部由调用方注入：宿主身份与真实加载快照、安全应用回调、
/// 目录/入口选择。没有回调就不假称成功。
/// </summary>
public interface ILocalPluginHost
{
    /// <summary>注入的真实宿主身份（后端与精确版本）。</summary>
    HostIdentity Identity { get; }

    /// <summary>当前进程实际加载的扩展路径快照；拿不到就返回 Success=false。</summary>
    Task<HostPluginSnapshot> SnapshotAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 安全应用：把候选版本交给宿主（经既有安全重启）并确认。
    /// 返回 Success=false 时模块保留旧选定版本与文件。
    /// </summary>
    Task<HostApplyResult> ApplyAsync(string candidateEntryPath, string pluginId, string version,
        CancellationToken cancellationToken = default);
}

/// <summary>供 VM 使用的目录/入口选择委托，避免硬编码主区路径。</summary>
public interface ILocalPluginPicker
{
    Task<string?> PickPluginDirectoryAsync(CancellationToken cancellationToken = default);

    Task<string?> PickEntryFileAsync(string rootDirectory, CancellationToken cancellationToken = default);
}

/// <summary>明确的安全重启能力；普通Apply仍保留活动版本只登记pending的原契约。</summary>
public interface ILocalPluginRestartHost : ILocalPluginHost
{
    bool CanRestart { get; }
    Task<HostApplyResult> ApplyWithRestartAsync(string candidateEntryPath, string pluginId, string version,
        HostPluginSnapshot expectedSnapshot, CancellationToken cancellationToken = default);
}

/// <summary>Opaque structured scope: never recover project/session identity by splitting display text.</summary>
public sealed record PluginHostContext(string ProjectPath, string SessionId, Guid Generation);

/// <summary>Holds actual host apply and idle-maintenance gates during metadata-only reconciliation.</summary>
public interface ILocalPluginReconciliationHost : ILocalPluginHost
{
    Task<IPluginReconciliationLease?> AcquireReconciliationAsync(HostPluginSnapshot expectedSnapshot,
        CancellationToken cancellationToken = default);
}

public interface IPluginReconciliationLease : IDisposable
{
    bool IsCurrent { get; }
}

/// <summary>本产品托管的一个固定版本副本。</summary>
public sealed record LocalPluginVersion(
    string VersionId,
    string Version,
    string DirectoryName,
    string EntryRelativePath,
    string ContentHash,
    DateTimeOffset ImportedAtUtc)
{
    public PluginSourceKind SourceKind { get; init; } = PluginSourceKind.LocalDirectory;
    public string? SourceRoot { get; init; }
    /// <summary>该固定版本导入时冻结的后端与宿主版本声明（兼容判定只读这里）。</summary>
    public PluginBackend DeclaredBackend { get; init; } = PluginBackend.Unknown;
    public IReadOnlyList<string> DeclaredHostVersions { get; init; } = [];

    [JsonIgnore]
    public string DisplayVersion => Version.Length == 0 ? "(无版本)" : Version;
}

/// <summary>移除记录：只记事实，不复活已移除的版本。</summary>
public sealed record LocalPluginRemovalLog(
    string VersionId,
    string Version,
    DateTimeOffset RemovedAtUtc,
    string Reason);

/// <summary>本产品目录中的一个插件（可含多个固定版本）。</summary>
public sealed record LocalPlugin(
    string Id,
    string DisplayName,
    PluginBackend Backend,
    string StorageKey,
    IReadOnlyList<LocalPluginVersion> Versions)
{
    public string? SelectedVersionId { get; init; }
    public string? PreviousVersionId { get; init; }
    public string? PendingApplyVersionId { get; init; }
    public bool PendingRemove { get; init; }
    public bool PendingRollback { get; init; }
    public IReadOnlyList<string> SupportedHostVersions { get; init; } = [];
    public string? LoadError { get; init; }
    public DateTimeOffset? LoadErrorAtUtc { get; init; }
    public IReadOnlyList<LocalPluginRemovalLog> Removals { get; init; } = [];
    public DateTimeOffset UpdatedAtUtc { get; init; }

    public LocalPluginVersion? FindVersion(string? versionId) =>
        versionId is null ? null : Versions.FirstOrDefault(v => v.VersionId == versionId);
}

/// <summary>列表展示行：来源、固定版本、宿主兼容、选定/待应用/实际加载/加载错误分别成列。</summary>
public sealed record LocalPluginItem(
    string Id,
    string DisplayName,
    PluginBackend Backend,
    IReadOnlyList<LocalPluginVersion> Versions,
    string? SelectedVersionId,
    string? PreviousVersionId,
    string? PendingApplyVersionId,
    string? PendingRemoveVersionId,
    bool PendingRemoveReady,
    bool PendingRollback,
    /// <summary>仍有在途宿主回调的候选版本（受保护、不可删除）。</summary>
    IReadOnlyList<string> InFlightVersionIds,
    PluginCompatibility Compatibility,
    string CompatibilityText,
    LocalPluginStatus Status,
    string StatusText,
    string? LoadError,
    bool IsActive,
    bool CanApply,
    bool CanRollback,
    bool CanRemove)
{
    public IReadOnlyList<ApplyReceipt> ApplyReceipts { get; init; } = [];
    public IReadOnlyList<InFlightCandidate> InFlightRequests { get; init; } = [];
    public IReadOnlyList<PluginRegistrationReconciliation> Reconciliations { get; init; } = [];
    public string BackendText => Backend switch
    {
        PluginBackend.Pi => "Pi",
        PluginBackend.Omp => "OMP",
        PluginBackend.Codex => "Codex",
        PluginBackend.Dsh => "DSH",
        _ => "未知后端",
    };
}

/// <summary>动作结果：成功、待宿主应用、被拒绝、失败。</summary>
public sealed record LocalPluginActionResult(
    bool Success,
    LocalPluginStatus Status,
    string Message,
    LocalPluginRejectionKind Rejection = LocalPluginRejectionKind.None)
{
    public bool IsPending => Status == LocalPluginStatus.Pending;

    public static LocalPluginActionResult Ok(LocalPluginStatus status, string message) => new(true, status, message);

    public static LocalPluginActionResult Pending(string message) => new(true, LocalPluginStatus.Pending, message);

    public static LocalPluginActionResult Rejected(LocalPluginRejectionKind kind, string message) =>
        new(false, LocalPluginStatus.Blocked, message, kind);

    public static LocalPluginActionResult Failed(string message, LocalPluginRejectionKind kind = LocalPluginRejectionKind.None) =>
        new(false, LocalPluginStatus.LoadError, message, kind);

    /// <summary>旧操作的结果被更新的操作取代：不覆盖任何新状态。</summary>
    public static LocalPluginActionResult Superseded(string message) =>
        new(false, LocalPluginStatus.Pending, message, LocalPluginRejectionKind.StaleOperation);
}
