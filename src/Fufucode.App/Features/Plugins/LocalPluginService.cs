using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Fufucode.Core;

namespace Fufucode.App.Features.Plugins;

/// <summary>目录状态损坏或被未来版本写入：不静默重建、不覆盖。</summary>
public sealed class LocalPluginCatalogCorruptException : InvalidOperationException
{
    public LocalPluginCatalogCorruptException(string reason) : base(reason) => Reason = reason;

    public string Reason { get; }
}

/// <summary>列表视图：插件行 + 宿主可用性 + 目录状态损坏原因（若有）。</summary>
public sealed record LocalPluginCatalogView(
    IReadOnlyList<LocalPluginItem> Items,
    bool HostSnapshotAvailable,
    string? HostSnapshotError,
    HostIdentity Identity,
    string? CorruptReason)
{
    public bool IsCorrupt => CorruptReason is not null;
}

/// <summary>
/// 本产品本地插件固定版本托管服务（E3-LOCAL 首版，后端仅 Pi）。
///
/// 边界（严格）：
/// - 只写在调用方提供的 AppDataLayout 根下的 plugins/local，不碰源目录、不碰 Pi/OMP 的加载目录、
///   不写 desktop.db、不改任何引擎配置；
///   显式 PreparePiLoadProofAsync 另写应用自有 plugins/loaders，不改原固定版本，不启动引擎；
/// - 实际加载与否只来自 <see cref="ILocalPluginHost.SnapshotAsync"/> 注入的真实宿主快照，
///   文件存在或 enabled 都不等于 active；
/// - 应用/回退经 <see cref="ILocalPluginHost.ApplyAsync"/> 交宿主（M 接既有安全重启）；
///   没有回调就返回不可用，不假称成功；
/// - 目录状态损坏或版本未知时保留原文件并显式报错，不静默重建。
/// </summary>
public sealed partial class LocalPluginService
{
    public const string PluginsRootName = "plugins";
    public const string LocalRootName = "local";
    public const string StagingDirectoryName = ".staging";
    /// <summary>回执历史上限（按原身份保留最近若干次，供宿主只读核对）。</summary>
    public const int MaxReceiptHistory = 20;

    private readonly AppDataLayout _layout;
    private readonly ILocalPluginHost? _host;
    private readonly LocalPluginLimits _limits;
    internal Action? CallbackRecordedForTesting { get; set; }

    public LocalPluginService(AppDataLayout layout, ILocalPluginHost? host = null,
        LocalPluginLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        _layout = layout;
        _host = host;
        _limits = limits ?? LocalPluginLimits.Default;
        Directory.CreateDirectory(Root);
    }

    /// <summary>本产品本地插件根：&lt;layout.Root&gt;/plugins/local。</summary>
    public string Root => Path.Combine(_layout.Root, PluginsRootName, LocalRootName);

    public bool HasHost => _host is not null;
    public HostIdentity CurrentHostIdentity => _host?.Identity ?? HostIdentity.Unknown();
    public bool SupportsSafeRestart => _host is ILocalPluginRestartHost;
    public bool CanRequestSafeRestart => _host is ILocalPluginRestartHost { CanRestart: true };

    internal async Task<bool> IsKnownProofAsync(PiPluginLoadProof proof, CancellationToken cancellationToken)
    {
        var record = await FindPluginAsync(proof.PluginId, cancellationToken);
        if (record is null) return false;
        var candidate = record.Versions.FirstOrDefault(version => version.Version == proof.Version && version.ContentHash == proof.ContentHash);
        if (candidate is null) return false;
        var version = ToVersion(record, candidate);
        return RealPathEquals(VersionRoot(record, version), proof.PackageRoot) &&
            RealPathEquals(Path.Combine(VersionRoot(record, version), version.EntryRelativePath), proof.EntryPath) &&
            VerifyManagedCopy(record, version) is null;
    }

    private static bool RealPathEquals(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>只生成自有包装入口；不选择/应用/启动插件。真实宿主必须另行安全重启并核对 RPC。</summary>
    public async Task<PiPluginLoadProof> PreparePiLoadProofAsync(string pluginId, string versionId,
        CancellationToken cancellationToken = default)
    {
        var record = await FindPluginAsync(pluginId, cancellationToken)
            ?? throw new InvalidOperationException("插件不存在。");
        var versionRecord = record.Versions.FirstOrDefault(version => version.VersionId == versionId)
            ?? throw new InvalidOperationException("固定版本不存在。");
        var version = ToVersion(record, versionRecord);
        if (DeclaredFor(record, versionRecord).Backend != PluginBackend.Pi)
            throw new InvalidOperationException("初始化证明仅支持 Pi 固定版本。");
        var error = VerifyManagedCopy(record, version);
        if (error is not null) throw new InvalidOperationException(error);
        cancellationToken.ThrowIfCancellationRequested();
        var root = VersionRoot(record, version);
        return PiPluginLoadProof.Create(_layout, root, Path.Combine(root, version.EntryRelativePath),
            pluginId, version.Version, version.ContentHash);
    }

    /// <summary>最近一次读取到的目录状态损坏原因（不重建，只报告）。</summary>
    public string? LastCorruptReason { get; private set; }

    /// <summary>
    /// 当前目录状态代次；损坏时为 null。调用方用它做 compare-and-set：
    /// 读到的代次与本次写入时不同即拒绝，防止旧状态覆盖新状态。
    /// </summary>
    public long? CurrentGeneration()
    {
        var load = PluginCatalogStore.Load(Root);
        return load.IsCorrupt || load.State is null ? null : load.State.Generation;
    }

    // ── 查询 ──

    public async Task<LocalPluginCatalogView> ListAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await SafeSnapshotAsync(cancellationToken);
        var load = PluginCatalogStore.Load(Root);
        LastCorruptReason = load.IsCorrupt ? load.Reason : null;
        if (load.IsCorrupt || load.State is null)
        {
            return new LocalPluginCatalogView([], snapshot?.Success ?? false, snapshot?.Error,
                snapshot?.Identity ?? HostIdentity.Unknown(), load.Reason ?? "目录状态不可用。");
        }

        var identity = _host?.Identity ?? HostIdentity.Unknown();
        var active = snapshot?.Success == true ? snapshot!.LoadedExtensionPaths : [];
        var items = load.State.Plugins.Select(record => BuildItem(record, identity, active, snapshot)).ToArray();
        return new LocalPluginCatalogView(items, snapshot?.Success ?? false, snapshot?.Error, identity, null);
    }

    private LocalPluginItem BuildItem(PluginRecord record, HostIdentity identity,
        IReadOnlyList<string> activePaths, HostPluginSnapshot? snapshot)
    {
        var versions = record.Versions.Select(version => ToVersion(record, version)).ToArray();
        var selected = record.FindVersionFor(record.SelectedVersionId, versions);
        var pendingVersion = record.FindVersionFor(record.PendingApplyVersionId, versions);
        var pendingRemove = record.FindVersionFor(record.PendingRemoveVersionId, versions);

        // 兼容门禁按"所选固定版本冻结的声明"（无选定则按最新版本）；插件级声明只作旧数据回退与展示。
        var gate = selected ?? versions.FirstOrDefault();
        var backend = gate?.DeclaredBackend ?? PluginDescriptorFile.ParseBackend(record.Backend);
        var declared = gate?.DeclaredHostVersions ?? record.SupportedHostVersions;
        var compatibility = PluginCompatibilityRules.Evaluate(backend, declared, identity);

        // 实际加载：必须由真实宿主快照确认。宿主报告的是入口文件（或副本内任意路径），
        // 因此判定要覆盖"副本目录根"与"目录内的任意文件"两种情况。
        bool IsActive(LocalPluginVersion? version) =>
            version is not null && snapshot?.Success == true && IsVersionLoaded(record, version, activePaths);

        var selectedActive = IsActive(selected);
        var anyActive = versions.Any(v => IsActive(v));
        bool snapshotKnown = snapshot?.Success == true;
        // 待移除已具备执行条件：快照可用且该目标未被宿主加载。
        var pendingRemoveReady = pendingRemove is not null && snapshotKnown && !IsActive(pendingRemove);

        var status = BuildStatus(record, compatibility, selectedActive, anyActive, snapshotKnown);
        var canApply = record.PendingRemoveVersionId is null && compatibility == PluginCompatibility.Compatible &&
                       snapshotKnown && pendingVersion is null &&
                       record.Versions.Count > 0;
        var canRollback = canApply && record.PreviousVersionId is not null &&
                          record.FindVersionFor(record.PreviousVersionId, versions) is not null &&
                          !string.Equals(record.PreviousVersionId, record.SelectedVersionId, StringComparison.Ordinal);
        // 移除是"版本级"判断：只限自己托管且该具体版本未被宿主加载；
        // 正在应用（等待宿主回调）的候选版本不得被移除。行级只表达"至少有一个可移除版本"，
        // 具体版本由调用方选行后交给服务校验。
        var canRemove = versions.Any(version =>
            !string.Equals(version.VersionId, record.PendingApplyVersionId, StringComparison.Ordinal) &&
            !(snapshotKnown && IsActive(version)));

        return new LocalPluginItem(
            record.Id, record.DisplayName, backend, versions,
            record.SelectedVersionId, record.PreviousVersionId, record.PendingApplyVersionId,
            record.PendingRemoveVersionId, pendingRemoveReady, record.PendingRollback,
            record.InFlight.Select(c => c.VersionId).ToArray(),
            compatibility, PluginCompatibilityRules.Describe(compatibility, backend, identity, declared),
            status, BuildStatusText(status, record, compatibility, declared, selectedActive, pendingVersion, pendingRemove),
            record.LoadError, selectedActive, canApply, canRollback, canRemove)
        { ApplyReceipts = ReceiptHistory(record), InFlightRequests = record.InFlight.ToArray(), Reconciliations = record.Reconciliations };
    }

    private static LocalPluginStatus BuildStatus(PluginRecord record, PluginCompatibility compatibility,
        bool selectedActive, bool anyActive, bool snapshotKnown)
    {
        // 三种非兼容状态都不能应用：明确标 Blocked，不显示"可加载"。
        if (compatibility != PluginCompatibility.Compatible) return LocalPluginStatus.Blocked;
        if (record.PendingRemoveVersionId is not null) return LocalPluginStatus.Pending;
        if (record.PendingApplyVersionId is not null || record.PendingRollback) return LocalPluginStatus.Pending;
        if (record.LoadError is not null) return LocalPluginStatus.LoadError;
        if (selectedActive) return LocalPluginStatus.Loaded;
        if (!snapshotKnown && record.SelectedVersionId is not null) return LocalPluginStatus.ReadyToImport;
        if (!snapshotKnown) return LocalPluginStatus.ReadyToImport;
        return anyActive ? LocalPluginStatus.Loaded
            : record.SelectedVersionId is null ? LocalPluginStatus.Installed
            : LocalPluginStatus.ReadyToImport;
    }

    private static string BuildStatusText(LocalPluginStatus status, PluginRecord record,
        PluginCompatibility compatibility, IReadOnlyList<string> declared, bool selectedActive,
        LocalPluginVersion? pendingVersion, LocalPluginVersion? pendingRemove)
    {
        if (compatibility == PluginCompatibility.Unsupported)
            return $"后端 {record.Backend} 首版不支持：拒绝应用";
        if (compatibility == PluginCompatibility.Incompatible)
            return $"所选固定版本声明的宿主版本 [{string.Join(", ", declared)}] 与当前宿主不匹配：拒绝应用";
        if (compatibility == PluginCompatibility.Unknown)
            return $"宿主身份未知：无法确认兼容，拒绝应用（本地副本保留：{record.Versions.Count} 个固定版本）";
        return status switch
        {
            LocalPluginStatus.Pending when pendingRemove is not null =>
                $"已登记待移除 {pendingRemove.Version}：未删除任何文件；宿主确认未使用后再次执行移除即可",
            LocalPluginStatus.Pending when record.PendingRollback =>
                "已登记待回退：等待宿主安全重启后确认",
            LocalPluginStatus.Pending =>
                "已登记待应用：" + (pendingVersion is null ? "等待宿主" : pendingVersion.Version) + "，等待宿主安全重启后确认",
            LocalPluginStatus.LoadError => "上次应用失败：" + record.LoadError,
            LocalPluginStatus.Loaded when selectedActive => "宿主实际已加载（来自真实宿主快照）",
            LocalPluginStatus.Loaded => "宿主正在加载其他版本",
            LocalPluginStatus.ReadyToImport => record.SelectedVersionId is null
                ? "本地副本已导入，未选定版本"
                : "已选定版本，宿主快照不可用：不能确认加载",
            _ => "本地副本已导入",
        };
    }

    private static LocalPluginVersion ToVersion(PluginRecord owner, PluginVersionRecord record)
    {
        var (backend, supported) = DeclaredFor(owner, record);
        return new LocalPluginVersion(
            record.VersionId, record.Version, record.DirectoryName, record.EntryRelativePath,
            record.ContentHash, record.ImportedAtUtc)
        {
            SourceKind = record.SourceKind == "localDirectory" ? PluginSourceKind.LocalDirectory : PluginSourceKind.Unknown,
            SourceRoot = record.SourceRoot,
            DeclaredBackend = backend,
            DeclaredHostVersions = supported,
        };
    }

    /// <summary>
    /// 版本冻结的声明：优先取版本记录；早期数据没有版本级声明时回退到插件级。
    /// 后续导入其他版本绝不会改写已存在版本的声明。
    /// </summary>
    private static (PluginBackend Backend, IReadOnlyList<string> Supported) DeclaredFor(
        PluginRecord owner, PluginVersionRecord version)
    {
        var backendText = string.IsNullOrWhiteSpace(version.Backend) ? owner.Backend : version.Backend;
        var supported = version.SupportedHostVersions.Count > 0
            ? version.SupportedHostVersions
            : owner.SupportedHostVersions;
        return (PluginDescriptorFile.ParseBackend(backendText), supported);
    }

    // ── 预览与导入 ──

    /// <summary>只读预览：机制校验 + 宿主兼容判定，不复制任何文件。</summary>
    public LocalPluginPreview Preview(LocalPluginImportRequest request, HostIdentity? identity = null)
    {
        var host = identity ?? _host?.Identity ?? HostIdentity.Unknown();
        var (descriptor, rejection) = ReadPackage(request.SourceDirectory, request.EntryRelativePath,
            out var entryRelative, out var entryAbsolute);
        if (rejection is not null || descriptor is null)
        {
            return new LocalPluginPreview(
                new PluginDescriptor(string.Empty, string.Empty, string.Empty, PluginBackend.Unknown, request.EntryRelativePath ?? string.Empty, []),
                request.EntryRelativePath ?? string.Empty, string.Empty,
                PluginCompatibility.Unknown, "描述文件未通过校验", rejection);
        }

        var compatibility = PluginCompatibilityRules.Evaluate(descriptor.Backend, descriptor.SupportedHostVersions, host);
        var text = PluginCompatibilityRules.Describe(compatibility, descriptor.Backend, host, descriptor.SupportedHostVersions);
        // 导入门禁只做机制校验（描述文件、入口、越界、链接、大小/数量）。
        // 兼容性是应用门禁：unknown/incompatible/unsupported 一律拒绝应用，但本地副本照常浏览与安装，
        // 与任务书"宿主缺失照常浏览/安装本地副本，应用不可用"一致；不在此假称兼容。
        return new LocalPluginPreview(descriptor, entryRelative!, entryAbsolute!, compatibility, text, null);
    }

    /// <summary>
    /// 导入固定版本副本：先校验，再复制到本轮新暂存，再原子发布。
    /// 同名版本内容不一致明确拒绝；成功返回版本记录。
    /// <param name="expectedGeneration">
    /// 调用方读到的目录状态代次（compare-and-set）。传入后若当前代次不同，
    /// 在任何副作用之前拒绝，不用旧状态覆盖新状态。
    /// </param>
    /// </summary>
    public async Task<LocalPluginVersion> ImportAsync(LocalPluginImportRequest request,
        CancellationToken cancellationToken = default, long? expectedGeneration = null)
    {
        var preview = Preview(request);
        if (preview.Rejection is not null)
        {
            throw new InvalidOperationException(preview.Rejection.Message);
        }

        var descriptor = preview.Descriptor;
        var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.SourceDirectory));
        var storageKey = PluginIdRules.Sanitize(descriptor.Id, "plugin");
        // 版本目录名只由版本号决定：同名版本不同内容走冲突拒绝，不另建副本。
        var versionId = PluginIdRules.Sanitize(descriptor.Version, "v");
        var stagingRoot = Path.Combine(Root, StagingDirectoryName);
        var staging = Path.Combine(stagingRoot, Guid.NewGuid().ToString("N"));
        var final = Path.Combine(Root, storageKey, versionId);

        try
        {
            Directory.CreateDirectory(stagingRoot);
            PluginPackageScanner.CopyToStaging(source, staging);
            var hash = PluginPackageScanner.ComputeContentHash(staging);

            // 原子发布前先做冲突判定：同版本名 + 不同摘要 = 明确拒绝，不能覆盖固定版本。
            return await MutateAsync(state =>
            {
                var existing = state.Plugins.FirstOrDefault(p => string.Equals(p.Id, descriptor.Id, StringComparison.Ordinal));
                if (existing is not null)
                {
                    // 后端一致性按"任一已导入版本冻结的后端"判定，避免插件级字段被后来版本改写。
                    var existingBackend = existing.Versions.Count > 0
                        ? DeclaredFor(existing, existing.Versions[0]).Backend
                        : PluginDescriptorFile.ParseBackend(existing.Backend);
                    if (existingBackend != descriptor.Backend)
                    {
                        throw new InvalidOperationException(
                            $"插件 {descriptor.Id} 已以后端 {existingBackend} 导入，不能改为 {descriptor.Backend}。");
                    }

                    var sameVersion = existing.Versions.FirstOrDefault(v =>
                        string.Equals(v.Version, descriptor.Version, StringComparison.Ordinal));
                    if (sameVersion is not null)
                    {
                        if (!string.Equals(sameVersion.ContentHash, hash, StringComparison.Ordinal))
                        {
                            throw new InvalidOperationException(
                                $"版本 {descriptor.Version} 已存在且内容摘要不同：拒绝覆盖固定版本。");
                        }

                        // 同一版本同一摘要：不重复复制，保留原有来源与摘要绑定。
                        Directory.Delete(staging, recursive: true);
                        return (state, ToVersion(existing, sameVersion));
                    }
                }

                Directory.CreateDirectory(Path.GetDirectoryName(final)!);
                Directory.Move(staging, final);

                var record = new PluginVersionRecord
                {
                    VersionId = versionId,
                    Version = descriptor.Version,
                    DirectoryName = versionId,
                    EntryRelativePath = preview.EntryRelativePath,
                    ContentHash = hash,
                    ImportedAtUtc = DateTimeOffset.UtcNow,
                    SourceKind = "localDirectory",
                    SourceRoot = source,
                    // 声明按版本冻结：后续导入其他版本不得改写本版本的门禁。
                    Backend = descriptor.Backend.ToString().ToLowerInvariant(),
                    SupportedHostVersions = descriptor.SupportedHostVersions,
                };

                var plugins = state.Plugins.ToList();
                if (existing is null)
                {
                    plugins.Add(new PluginRecord
                    {
                        Id = descriptor.Id,
                        DisplayName = descriptor.DisplayName,
                        Backend = descriptor.Backend.ToString().ToLowerInvariant(),
                        StorageKey = storageKey,
                        Versions = [record],
                        // 插件级只保留首版声明作旧数据回退；门禁一律读版本记录。
                        SupportedHostVersions = descriptor.SupportedHostVersions,
                        UpdatedAtUtc = DateTimeOffset.UtcNow,
                    });
                }
                else
                {
                    var updated = existing with
                    {
                        DisplayName = descriptor.DisplayName,
                        Versions = existing.Versions.Append(record).ToArray(),
                        UpdatedAtUtc = DateTimeOffset.UtcNow,
                    };
                    plugins[plugins.IndexOf(existing)] = updated;
                }

                var owner = existing ?? plugins[^1];
                return (PluginCatalogStore.ToState(plugins, state.Generation), ToVersion(owner, record));
            }, cancellationToken, expectedGeneration);
        }
        catch
        {
            // 失败保留原源与既有目录：只清理本轮自己的暂存。
            TryDelete(staging);
            throw;
        }
    }

    private (PluginDescriptor? Descriptor, LocalPluginImportRejection? Rejection) ReadPackage(
        string sourceDirectory, string? entryOverride, out string? entryRelative, out string? entryAbsolute)
    {
        entryRelative = null;
        entryAbsolute = null;
        if (string.IsNullOrWhiteSpace(sourceDirectory))
        {
            return (null, new LocalPluginImportRejection(LocalPluginRejectionKind.SourceMissing, "未选择插件目录。"));
        }

        string root;
        try { root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceDirectory)); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return (null, new LocalPluginImportRejection(LocalPluginRejectionKind.SourceMissing,
                $"插件目录路径不合法：{error.GetBaseException().Message}"));
        }

        if (!Directory.Exists(root))
        {
            return (null, new LocalPluginImportRejection(LocalPluginRejectionKind.SourceMissing,
                $"插件目录不存在：{root}"));
        }

        var (descriptor, rejection) = PluginDescriptorFile.Read(root);
        if (rejection is not null || descriptor is null) return (null, rejection);

        // 调用方显式选择的相对入口优先于描述文件声明；两者都必须通过同一套越界/存在校验。
        var entryToUse = string.IsNullOrWhiteSpace(entryOverride) ? descriptor.EntryRelativePath : entryOverride.Trim();
        var (entry, entryRejection) = PluginPackageScanner.ResolveEntry(root, entryToUse);
        if (entryRejection is not null) return (null, entryRejection);

        var inspectRejection = PluginPackageScanner.Inspect(root, _limits);
        if (inspectRejection is not null) return (null, inspectRejection);

        entryRelative = entryToUse;
        entryAbsolute = entry;
        return (descriptor with { EntryRelativePath = entryToUse }, null);
    }

    // ── 选定 / 应用 / 回退 / 移除 ──

    /// <summary>选定固定版本并尝试应用；活动使用或宿主状态未知时只登记 pending。</summary>
    public Task<LocalPluginActionResult> ApplyVersionAsync(string pluginId, string versionId,
        CancellationToken cancellationToken = default, long? expectedGeneration = null)
        => ApplyVersionCoreAsync(pluginId, versionId, cancellationToken, expectedGeneration, safeRestart: false);

    public Task<LocalPluginActionResult> ApplyVersionWithRestartAsync(string pluginId, string versionId,
        CancellationToken cancellationToken = default, long? expectedGeneration = null)
        => ApplyVersionCoreAsync(pluginId, versionId, cancellationToken, expectedGeneration, safeRestart: true);

    private async Task<LocalPluginActionResult> ApplyVersionCoreAsync(string pluginId, string versionId,
        CancellationToken cancellationToken, long? expectedGeneration, bool safeRestart)
    {
        if (safeRestart && _host is not ILocalPluginRestartHost { CanRestart: true })
            return LocalPluginActionResult.Rejected(LocalPluginRejectionKind.HostUnavailable,
                "宿主不具备空闲安全重启条件，未修改选定或待应用版本。");
        if (_host is null)
        {
            return LocalPluginActionResult.Rejected(LocalPluginRejectionKind.HostUnavailable,
                "宿主未接线：应用不可用，未改动任何副本或选定版本。");
        }

        var snapshot = await SafeSnapshotAsync(cancellationToken);
        var identity = _host.Identity;

        // 操作身份：接受请求时换成新令牌。跨 await 的完成提交必须核对令牌，
        // 旧操作的结果不得覆盖更新的 pending/选定/回退目标。
        var token = Guid.NewGuid().ToString("N");
        // Held through the entire original call, including its local completion/exception unwind.
        // A returned Task journal alone does not certify that a live completion writer has stopped.
        using var requestLease = SessionLease.Acquire(Root, RequestLeaseKey(token));
        PluginRecord? record;
        try
        {
            record = await MutateAsync(state =>
            {
                var existing = FindPlugin(state, pluginId);
                var version = existing.Versions.FirstOrDefault(v => v.VersionId == versionId);
                if (version is null) throw new InvalidOperationException($"插件 {pluginId} 没有版本 {versionId}。");
                // 兼容门禁按该固定版本冻结的声明判定，不读插件级（可能被后来导入改写）。
                var (declaredBackend, declaredHosts) = DeclaredFor(existing, version);
                var compatibility = PluginCompatibilityRules.Evaluate(declaredBackend, declaredHosts, identity);
                if (compatibility != PluginCompatibility.Compatible)
                {
                    throw new InvalidOperationException(PluginCompatibilityRules.Describe(compatibility,
                        declaredBackend, identity, declaredHosts));
                }

                // 历史版本指针只在应用成功后推进（见下方成功分支）：失败时不能把"上一版本"
                // 覆盖成刚失败的版本，否则回退目标会被污染。
                var next = existing with
                {
                    PendingApplyVersionId = versionId,
                    PendingRollback = false,
                    OperationToken = token,
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                };
                var plugins = Replace(state, existing, next);
                return (PluginCatalogStore.ToState(plugins, state.Generation), next);
            }, cancellationToken, expectedGeneration);
        }
        catch (InvalidOperationException error)
        {
            return LocalPluginActionResult.Rejected(LocalPluginRejectionKind.IncompatibleHost, error.Message);
        }

        var candidate = ToVersion(record, record.Versions.First(v => v.VersionId == versionId));

        if (snapshot is null || snapshot.IsUnknown)
        {
            // 延后：保留 pending 目标但释放在途令牌，这样后续按真实快照确认才能推进；
            // 同时令牌已换，任何更早的在途回调结果都会因令牌不匹配而被判为过期。
            return await DeferAsync(pluginId, token, versionId,
                "宿主快照不可用：已登记待应用，未改动加载目录、未启动会话。");
        }

        // 活动使用：当前进程仍在加载本插件任一版本时只登记 pending，不原地改/删加载目录。
        var current = await FindPluginAsync(pluginId, cancellationToken);
        if (current is null) return LocalPluginActionResult.Failed("插件记录已不存在。");
        var active = snapshot.LoadedExtensionPaths;
        if (!safeRestart && current.Versions.Any(v => active.Any(p => CoversPath(VersionRoot(current, ToVersion(current, v)), p))))
        {
            return await DeferAsync(pluginId, token, versionId,
                "宿主当前正在使用本插件：只登记待应用，延后到宿主安全重启，不原地改动加载目录。");
        }

        // 应用到宿主之前重新核对托管副本：摘要/入口/声明/路径与导入时一致，且无链接遍历。
        // 篡改或损坏一律在调用宿主之前拒绝，绝不改写记录里的旧摘要制造通过。
        var integrityError = VerifyManagedCopy(current, candidate);
        if (integrityError is not null)
        {
            var rejected = await CompleteAsync(pluginId, token, versionId, "integrity_rejected", integrityError, existing => existing with
            {
                PendingApplyVersionId = null,
                PendingRollback = false,
                LoadError = integrityError,
                LoadErrorAtUtc = DateTimeOffset.UtcNow,
                Versions = existing.Versions.Select(v => v.VersionId == versionId
                    ? v with { IntegrityError = integrityError, IntegrityCheckedAtUtc = DateTimeOffset.UtcNow }
                    : v).ToArray(),
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            });
            _ = rejected;
            return LocalPluginActionResult.Rejected(LocalPluginRejectionKind.IntegrityMismatch,
                integrityError + "（未调用宿主，未改动加载目录。）");
        }

        // 调用宿主前登记"在途候选"（持久化）：回调未返回期间任何实例都不得删除该候选文件。
        var originalRequest = new InFlightCandidate
        { VersionId = versionId, Token = token, StartedAtUtc = DateTimeOffset.UtcNow };
        var registered = await MutateAsync(state =>
        {
            var existing = OptionalPlugin(state, pluginId);
            if (existing is null) return (state, false);
            if (!string.Equals(existing.OperationToken, token, StringComparison.Ordinal))
            {
                return (state, false);
            }

            var next = existing with
            {
                InFlight = existing.InFlight
                    .Where(c => !string.Equals(c.Token, token, StringComparison.Ordinal))
                    .Append(originalRequest)
                    .ToArray(),
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };
            return (PluginCatalogStore.ToState(Replace(state, existing, next), state.Generation), true);
        }, CancellationToken.None);
        if (!registered)
        {
            return LocalPluginActionResult.Superseded(
                "该应用已被更新的操作取代：未进入宿主调用。");
        }

        if (cancellationToken.IsCancellationRequested)
        {
            // 宿主副作用尚未开始：取消可以干净回收，不需要回执。
            await CompleteAsync(pluginId, token, versionId, "cancelled", "调用前已取消，未调用宿主。", existing => existing with
            {
                PendingApplyVersionId = null,
                PendingRollback = false,
                LoadError = "应用在调用宿主前被取消：未产生宿主副作用。",
                LoadErrorAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            });
            return LocalPluginActionResult.Failed("应用在调用宿主前被取消：未产生宿主副作用，可安全重试。",
                LocalPluginRejectionKind.OperationCancelled);
        }

        var entryPath = Path.GetFullPath(Path.Combine(VersionRoot(current, candidate), candidate.EntryRelativePath));
        void RecordReturn(string outcome, string? message, RpcProcessExit? exited = null)
        {
            PluginCallbackJournal.Save(Root,
            new PluginCallbackCompletion(PluginCallbackJournal.SchemaVersion, pluginId, token, versionId,
                originalRequest.StartedAtUtc, snapshot.Identity, snapshot.ContextId, outcome, message, DateTimeOffset.UtcNow)
            {
                OriginalGeneration = snapshot.Generation,
                OriginalProcessExit = exited is not null && snapshot.Generation == exited.Generation ? exited : null,
                OriginalContext = snapshot.StructuredContext,
                RequestLeaseVersion = 1,
            });
            CallbackRecordedForTesting?.Invoke();
        }
        HostApplyResult result;
        try
        {
            result = safeRestart
                ? await ((ILocalPluginRestartHost)_host).ApplyWithRestartAsync(entryPath, current.Id, candidate.Version, snapshot, cancellationToken)
                : await _host.ApplyAsync(entryPath, current.Id, candidate.Version, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            RecordReturn("cancelled", "原宿主调用已抛出取消；实际副作用仍未知。");
            // 自己的取消/未知结果：保留回执与诊断，回收在途登记，不假称失败也不自动重发。
            var cancelledCommit = await CompleteAsync(pluginId, token, versionId, "cancelled_unknown",
                "宿主调用被取消：结果未知，未自动重发。", existing => existing with
            {
                PendingApplyVersionId = null,
                PendingRollback = false,
                LoadError = "应用被取消：宿主可能已执行，结果未知，未自动重发（回执可供只读核对）。",
                LoadErrorAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            });
            _ = cancelledCommit;
            return LocalPluginActionResult.Failed(
                "应用被取消：宿主可能已执行，结果未知，未自动重发（回执可供只读核对）。",
                LocalPluginRejectionKind.OperationCancelled);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            RecordReturn("threw", error.GetBaseException().Message);
            var thrownCommit = await CompleteAsync(pluginId, token, versionId, "threw",
                error.GetBaseException().Message, existing => existing with
            {
                PendingApplyVersionId = null,
                PendingRollback = false,
                LoadError = "宿主应用抛出异常：" + error.GetBaseException().Message,
                LoadErrorAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            });
            if (!thrownCommit)
            {
                return LocalPluginActionResult.Superseded(
                    "该应用已被更新的操作取代：异常结果未覆盖新的待应用/选定/诊断状态。");
            }

            return LocalPluginActionResult.Failed(
                "宿主应用抛出异常，已保留旧选定版本与文件：" + error.GetBaseException().Message,
                LocalPluginRejectionKind.HostUnavailable);
        }

        RecordReturn(result.Success ? "returned_success" : "returned_failure", result.Message, result.OriginalProcessExit);

        // 调用方在宿主返回前取消了令牌，但宿主已返回结果：本地不据此宣称成功，
        // 保留回执与未知状态，回收在途登记，不自动重发、也不误报为"被新操作取代"。
        if (cancellationToken.IsCancellationRequested)
        {
            await CompleteAsync(pluginId, token, versionId, "cancelled_unknown",
                result.Message ?? "宿主已返回结果，但调用方已取消。", existing => existing with
            {
                PendingApplyVersionId = null,
                PendingRollback = false,
                LoadError = "应用在宿主返回后被检测到取消：宿主可能已成功，结果未知，未自动重发（回执可供只读核对）。",
                LoadErrorAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            });
            return LocalPluginActionResult.Failed(
                "应用在宿主返回后被检测到取消：宿主可能已成功，结果未知，未自动重发（回执可供只读核对）。",
                LocalPluginRejectionKind.OperationCancelled);
        }

        if (result.Success)
        {
            var committed = await CompleteAsync(pluginId, token, versionId, "confirmed", result.Message, existing => existing with
            {
                // 只有确认成功才推进：旧选定版本成为可回退目标。
                PreviousVersionId = existing.SelectedVersionId is { Length: > 0 } previous &&
                                    !string.Equals(previous, versionId, StringComparison.Ordinal)
                    ? previous
                    : existing.PreviousVersionId,
                SelectedVersionId = versionId,
                PendingApplyVersionId = null,
                PendingRollback = false,
                LoadError = null,
                LoadErrorAtUtc = null,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            });
            if (!committed)
            {
                return LocalPluginActionResult.Superseded(
                    "该应用已被更新的操作取代：旧结果未覆盖新的待应用/选定/回退状态。");
            }

            return LocalPluginActionResult.Ok(LocalPluginStatus.Pending,
                "已交宿主应用；实际加载以宿主安全重启后的真实快照为准。");
        }

        // 失败保旧：选定版本回到失败前的稳定版本，副本与文件都不动，只记诊断。
        var failedCommit = await CompleteAsync(pluginId, token, versionId, "failed",
            result.Message ?? "宿主应用失败（无诊断信息）", existing => existing with
        {
            PendingApplyVersionId = null,
            PendingRollback = false,
            LoadError = result.Message ?? "宿主应用失败（无诊断信息）",
            LoadErrorAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        });
        if (!failedCommit)
        {
            return LocalPluginActionResult.Superseded(
                "该应用已被更新的操作取代：失败结果未覆盖新的待应用/选定/回退状态。");
        }

        return LocalPluginActionResult.Failed(
            "宿主应用未确认成功，已保留旧选定版本与文件：" + (result.Message ?? "宿主未提供诊断信息。"),
            LocalPluginRejectionKind.HostUnavailable);
    }

    /// <summary>
    /// 延后提交：保留 pending 目标、释放在途令牌（后续可按真实快照确认推进），无宿主调用。
    /// 若令牌已被更新的操作替换，则如实返回"被取代"，不覆盖新状态。
    /// </summary>
    private async Task<LocalPluginActionResult> DeferAsync(string pluginId, string token, string versionId,
        string message)
    {
        var stillCurrent = await CompleteAsync(pluginId, token, versionId, "deferred", message,
            decisionUpdate: null);
        return stillCurrent
            ? LocalPluginActionResult.Pending(message)
            : LocalPluginActionResult.Superseded("该应用已被更新的操作取代：旧结果未覆盖新的待应用/选定状态。");
    }

    /// <summary>
    /// 完成提交。三件事互相独立：
    /// 1) 回收本次操作自己的在途候选登记（无论结果如何，宿主回调已返回）；
    /// 2) 写本次操作回执（供宿主只读核对，不自动重发）；
    /// 3) 仅当令牌仍是当前令牌时改 pending/选定/回退目标并清令牌，否则原样保留新状态。
    /// 状态提交一律用 None：调用方取消的是宿主操作，不是本地记账；否则会留下永久在途令牌。
    /// 回执身份来自**接受请求时的原版本 + 本操作自己的令牌**（优先取持久化的在途登记），
    /// 绝不从可变的当前 pending / 上一条回执推断归属。
    /// </summary>
    private async Task<bool> CompleteAsync(string pluginId, string token, string requestedVersionId,
        string outcome, string? message, Func<PluginRecord, PluginRecord>? decisionUpdate)
    {
        return await MutateAsync(state =>
        {
            var existing = OptionalPlugin(state, pluginId);
            // 插件记录已不存在（例如候选已按显式移除收敛）：迟到结果没有可登记的归属，
            // 直接返回"未提交"，既不抛异常也不复活记录。
            if (existing is null) return (state, false);
            var mine = existing.InFlight.FirstOrDefault(c =>
                string.Equals(c.Token, token, StringComparison.Ordinal));
            var tokenCurrent = string.Equals(existing.OperationToken, token, StringComparison.Ordinal);

            // 原身份：持久化在途登记里的候选优先（它就是接受请求时写下的版本），否则用调用方传入的原版本。
            var originalVersionId = mine is { VersionId.Length: > 0 } ? mine.VersionId : requestedVersionId;
            var changed = mine is null
                ? existing
                : existing with
                {
                    InFlight = existing.InFlight.Where(c => !ReferenceEquals(c, mine)).ToArray(),
                };
            var receipt = new ApplyReceipt
            {
                VersionId = originalVersionId,
                Token = token,
                Outcome = outcome,
                Message = message,
                AtUtc = DateTimeOffset.UtcNow,
            };
            changed = changed with
            {
                // 只有仍是最新操作才更新"最新回执"；迟到结果只进按原身份保留的历史，
                // 既不顶掉已确认的新回执，也不会把 v1 的结果写成 v2 的。
                LastApplyReceipt = tokenCurrent ? receipt : existing.LastApplyReceipt,
                RecentReceipts = existing.RecentReceipts
                    .Where(r => !string.Equals(r.Token, token, StringComparison.Ordinal))
                    .Append(receipt)
                    .TakeLast(MaxReceiptHistory)
                    .ToArray(),
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };
            var next = tokenCurrent
                ? (decisionUpdate is null ? changed : decisionUpdate(changed)) with { OperationToken = null }
                : changed;
            return (PluginCatalogStore.ToState(Replace(state, existing, next), state.Generation), tokenCurrent);
        }, CancellationToken.None);
    }

    /// <summary>
    /// 宿主显式对账回收在途登记：仅在宿主确认"该候选已不再加载且不会有后续副作用"时调用，
    /// 用于持有者进程异常结束等场景，避免永久保护。本模块绝不自行解除保护。
    /// </summary>
    public async Task<LocalPluginActionResult> AbandonInFlightAsync(string pluginId, string versionId, string reason,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return LocalPluginActionResult.Rejected(LocalPluginRejectionKind.RemoveNotAllowed,
                "对账回收必须给出宿主证据说明。");
        }

        var removed = await MutateAsync(state =>
        {
            var existing = OptionalPlugin(state, pluginId);
            if (existing is null) return (state, false);
            var abandoned = existing.InFlight
                .Where(c => string.Equals(c.VersionId, versionId, StringComparison.Ordinal)).ToArray();
            var remaining = existing.InFlight
                .Where(c => !string.Equals(c.VersionId, versionId, StringComparison.Ordinal)).ToArray();
            var ownedTokenReclaimed = abandoned.Any(c =>
                string.Equals(c.Token, existing.OperationToken, StringComparison.Ordinal));
            var pendingPointsAtCandidate = string.Equals(existing.PendingApplyVersionId, versionId,
                StringComparison.Ordinal);
            if (abandoned.Length == 0 && !pendingPointsAtCandidate) return (state, false);

            var next = existing with
            {
                InFlight = remaining,
                // 宿主已确认该候选不会再有副作用：同时回收指向它的在途令牌与 pending，
                // 否则会留下永远无法推进也无法删除的目标。
                OperationToken = ownedTokenReclaimed ? null : existing.OperationToken,
                PendingApplyVersionId = pendingPointsAtCandidate &&
                                        (ownedTokenReclaimed || existing.OperationToken is null)
                    ? null
                    : existing.PendingApplyVersionId,
                LoadError = "宿主对账回收在途候选 " + versionId + "：" + reason,
                LoadErrorAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };
            return (PluginCatalogStore.ToState(Replace(state, existing, next), state.Generation), true);
        }, cancellationToken);

        return removed
            ? LocalPluginActionResult.Ok(LocalPluginStatus.Installed,
                "已按宿主证据回收在途候选 " + versionId + "（未删除任何文件）。")
            : LocalPluginActionResult.Rejected(LocalPluginRejectionKind.VersionMissing,
                "没有该版本的在途登记，未改动任何内容。");
    }

    /// <summary>
    /// 应用/回退前的托管副本完整性复核：路径、入口、链接与内容摘要都必须与导入时一致。
    /// 只读检查，不修改任何记录（绝不用"改写摘要"来制造通过）。
    /// </summary>
    private string? VerifyManagedCopy(PluginRecord record, LocalPluginVersion version)
    {
        var root = VersionRoot(record, version);
        if (!IsUnder(Root, root))
        {
            return "托管副本路径超出本产品目录：拒绝应用。";
        }

        if (!Directory.Exists(root))
        {
            return "托管副本目录不存在：拒绝应用。";
        }

        if (PluginPackageScanner.IsReparse(root))
        {
            return "托管副本目录是符号链接或重解析点：拒绝应用。";
        }

        var inspect = PluginPackageScanner.Inspect(root, _limits);
        if (inspect is not null) return "托管副本校验失败：" + inspect.Message;

        var (entry, entryError) = PluginPackageScanner.ResolveEntry(root, version.EntryRelativePath);
        if (entryError is not null) return "托管副本入口校验失败：" + entryError.Message;
        _ = entry;

        var hash = PluginPackageScanner.ComputeContentHash(root);
        if (!string.Equals(hash, version.ContentHash, StringComparison.Ordinal))
        {
            return $"托管副本内容与导入时摘要不一致（记录 {Short(version.ContentHash)}，实际 {Short(hash)}）：" +
                   "可能被改写或损坏，拒绝应用。";
        }

        return null;
    }

    private static string Short(string hash) => hash.Length >= 12 ? hash[..12] + "…" : hash;

    /// <summary>显式回退到上一个选定版本；走同一宿主门，不直接改文件或杀进程。</summary>
    public Task<LocalPluginActionResult> RollbackAsync(string pluginId,
        CancellationToken cancellationToken = default)
        => RollbackCoreAsync(pluginId, cancellationToken, safeRestart: false);

    public Task<LocalPluginActionResult> RollbackWithRestartAsync(string pluginId,
        CancellationToken cancellationToken = default)
        => RollbackCoreAsync(pluginId, cancellationToken, safeRestart: true);

    private async Task<LocalPluginActionResult> RollbackCoreAsync(string pluginId,
        CancellationToken cancellationToken, bool safeRestart)
    {
        var record = await FindPluginAsync(pluginId, cancellationToken);
        if (record is null)
        {
            return LocalPluginActionResult.Rejected(LocalPluginRejectionKind.PluginMissing, $"没有插件 {pluginId}。");
        }

        if (record.PreviousVersionId is null)
        {
            return LocalPluginActionResult.Rejected(LocalPluginRejectionKind.VersionMissing,
                "没有可回退的历史版本：本次导入后尚未成功应用过其他版本。");
        }

        await MutateAsync(state =>
        {
            var existing = FindPlugin(state, pluginId);
            var next = existing with { PendingRollback = true, UpdatedAtUtc = DateTimeOffset.UtcNow };
            return (PluginCatalogStore.ToState(Replace(state, existing, next), state.Generation), 0);
        }, cancellationToken);

        var result = await ApplyVersionCoreAsync(pluginId, record.PreviousVersionId, cancellationToken, null, safeRestart);
        // 被更新操作取代时不回收标志：那会覆盖新操作的状态。
        if (!result.Success && result.Rejection != LocalPluginRejectionKind.StaleOperation)
        {
            await MutateAsync(state =>
            {
                var existing = FindPlugin(state, pluginId);
                var next = existing with { PendingRollback = false, UpdatedAtUtc = DateTimeOffset.UtcNow };
                return (PluginCatalogStore.ToState(Replace(state, existing, next), state.Generation), 0);
            }, cancellationToken);
        }

        return result;
    }

    /// <summary>
    /// 显式移除本产品托管、且未被宿主实际加载的"具体版本"。
    /// 快照未知或该版本正在使用时只登记该版本的 pending，不删除任何文件；
    /// 删除失败/状态提交失败如实报告并保留记录与诊断。绝不删外部源、内置审批桥或用户其他插件。
    /// </summary>
    public async Task<LocalPluginActionResult> RemoveVersionAsync(string pluginId, string versionId,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await SafeSnapshotAsync(cancellationToken);
        PluginRecord? record;
        try
        {
            record = await FindPluginAsync(pluginId, cancellationToken);
        }
        catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            // 目录状态当前不可读/被占用：在删除任何文件之前就明确失败，不改动任何内容。
            return LocalPluginActionResult.Failed(
                "移除失败：目录状态当前不可用，未改动任何内容（可重试）：" + error.GetBaseException().Message,
                LocalPluginRejectionKind.LockedOrCorrupt);
        }

        if (record is null)
        {
            return LocalPluginActionResult.Rejected(LocalPluginRejectionKind.PluginMissing, $"没有插件 {pluginId}。");
        }

        var version = record.Versions.FirstOrDefault(v => v.VersionId == versionId);
        if (version is null)
        {
            return LocalPluginActionResult.Rejected(LocalPluginRejectionKind.VersionMissing, $"没有版本 {versionId}。");
        }

        var candidate = ToVersion(record, version);
        var versionRoot = VersionRoot(record, candidate);
        if (!IsUnder(Root, versionRoot))
        {
            return LocalPluginActionResult.Rejected(LocalPluginRejectionKind.RemoveNotAllowed,
                "该副本不在本产品托管目录内：拒绝移除。");
        }

        // 正在应用（等待宿主回调）的候选版本不得被移除：不删候选文件、不破坏在途操作。
        // 在途登记是持久化的（跨实例/重开可见），因此这里能保护"已有更新操作接管 pending、
        // 但旧宿主回调仍在运行"的候选——那是宿主副作用仍可能发生的窗口。
        if (record.InFlight.Any(c => string.Equals(c.VersionId, versionId, StringComparison.Ordinal)))
        {
            return LocalPluginActionResult.Rejected(LocalPluginRejectionKind.OperationBusy,
                "该候选仍有在途宿主回调（结果未知）：拒绝删除候选文件；" +
                "确需回收时须由宿主在确认无后续副作用后调用 AbandonInFlightAsync 显式对账。");
        }

        if (string.Equals(record.PendingApplyVersionId, versionId, StringComparison.Ordinal))
        {
            return LocalPluginActionResult.Rejected(LocalPluginRejectionKind.OperationBusy,
                "该版本正在应用或等待宿主确认：拒绝移除候选文件，未删除任何内容。");
        }

        if (snapshot is null || snapshot.IsUnknown)
        {
            await SetPendingRemoveAsync(pluginId, versionId, cancellationToken);
            return LocalPluginActionResult.Pending(
                $"宿主快照不可用：已登记待移除 {version.Version}，未删除任何文件。");
        }

        if (IsVersionLoaded(record, candidate, snapshot.LoadedExtensionPaths))
        {
            await SetPendingRemoveAsync(pluginId, versionId, cancellationToken);
            return LocalPluginActionResult.Pending(
                $"宿主当前正在使用 {version.Version}：只登记待移除，不删除加载目录。");
        }

        // 保护性复核 + 删除 + 状态提交放在同一个独占写入区内：
        // 这样"读取后、删除前"别的实例登记在途候选的竞态被彻底关掉；
        // 删除失败（文件被占用等）时不写任何状态，绝不把残留版本写成"已移除"。
        try
        {
            await MutateAsync(state =>
            {
                var existing = FindPlugin(state, pluginId);
                var currentVersion = existing.Versions.FirstOrDefault(v => v.VersionId == versionId)
                    ?? throw new PluginOperationBlockedException(LocalPluginRejectionKind.VersionMissing,
                        $"没有版本 {versionId}。");
                var currentRoot = VersionRoot(existing, ToVersion(existing, currentVersion));
                if (!IsUnder(Root, currentRoot))
                {
                    throw new PluginOperationBlockedException(LocalPluginRejectionKind.RemoveNotAllowed,
                        "该副本不在本产品托管目录内：拒绝移除。");
                }

                if (existing.InFlight.Any(c => string.Equals(c.VersionId, versionId, StringComparison.Ordinal)))
                {
                    throw new PluginOperationBlockedException(LocalPluginRejectionKind.OperationBusy,
                        "该候选仍有在途宿主回调（结果未知）：拒绝删除候选文件；" +
                        "确需回收时须由宿主在确认无后续副作用后调用 AbandonInFlightAsync 显式对账。");
                }

                if (string.Equals(existing.PendingApplyVersionId, versionId, StringComparison.Ordinal))
                {
                    throw new PluginOperationBlockedException(LocalPluginRejectionKind.OperationBusy,
                        "该版本正在应用或等待宿主确认：拒绝移除候选文件，未删除任何内容。");
                }

                if (IsVersionLoaded(existing, ToVersion(existing, currentVersion), snapshot!.LoadedExtensionPaths))
                {
                    throw new PluginOperationBlockedException(LocalPluginRejectionKind.ActiveInUse,
                        "宿主刚报告正在使用该版本：拒绝删除加载目录。");
                }

                if (Directory.Exists(currentRoot)) Directory.Delete(currentRoot, recursive: true);

                var remaining = existing.Versions.Where(v => v.VersionId != versionId).ToArray();
                var removals = existing.Removals.Append(new PluginRemovalRecord
                {
                    VersionId = versionId,
                    Version = currentVersion.Version,
                    RemovedAtUtc = DateTimeOffset.UtcNow,
                    Reason = "user-removed",
                }).ToArray();
                var next = existing with
                {
                    Versions = remaining,
                    Removals = removals,
                    PendingRemoveVersionId = string.Equals(existing.PendingRemoveVersionId, versionId, StringComparison.Ordinal)
                        ? null : existing.PendingRemoveVersionId,
                    SelectedVersionId = string.Equals(existing.SelectedVersionId, versionId, StringComparison.Ordinal)
                        ? null : existing.SelectedVersionId,
                    PreviousVersionId = string.Equals(existing.PreviousVersionId, versionId, StringComparison.Ordinal)
                        ? null : existing.PreviousVersionId,
                    PendingApplyVersionId = string.Equals(existing.PendingApplyVersionId, versionId, StringComparison.Ordinal)
                        ? null : existing.PendingApplyVersionId,
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                };
                var plugins = remaining.Length == 0
                    ? state.Plugins.Where(p => !string.Equals(p.Id, pluginId, StringComparison.Ordinal)).ToList()
                    : Replace(state, existing, next);
                return (PluginCatalogStore.ToState(plugins, state.Generation), 0);
            }, cancellationToken);
        }
        catch (PluginOperationBlockedException blocked)
        {
            return LocalPluginActionResult.Rejected(blocked.Kind, blocked.Message);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // 如实报告失败，并把诊断留在记录里作为恢复线索；诊断写入本身失败也一并汇报，不吞。
            string? diagnosticWriteError = null;
            try
            {
                await MutateAsync(inner =>
                {
                    var existing = FindPlugin(inner, pluginId);
                    var next = existing with
                    {
                        LoadError = "移除失败：" + error.GetBaseException().Message,
                        LoadErrorAtUtc = DateTimeOffset.UtcNow,
                        UpdatedAtUtc = DateTimeOffset.UtcNow,
                    };
                    return (PluginCatalogStore.ToState(Replace(inner, existing, next), inner.Generation), 0);
                }, CancellationToken.None);
            }
            catch (Exception writeError)
            {
                diagnosticWriteError = writeError.GetBaseException().Message;
            }

            return LocalPluginActionResult.Failed(
                $"移除失败：副本文件被占用或不可写（{error.GetBaseException().Message}）；" +
                "目录状态、记录与所有权均保留，可重试。" +
                (diagnosticWriteError is null ? string.Empty : "诊断写入也失败：" + diagnosticWriteError),
                LocalPluginRejectionKind.LockedOrCorrupt);
        }
        catch (Exception error) when (error is InvalidOperationException)
        {
            return LocalPluginActionResult.Failed(
                "副本文件已删除，但目录状态提交失败：" + error.GetBaseException().Message +
                "；记录仍保留，重试移除可收敛。",
                LocalPluginRejectionKind.LockedOrCorrupt);
        }

        return LocalPluginActionResult.Ok(LocalPluginStatus.Installed,
            $"已移除本产品托管副本 {version.Version}（未活动使用）。");
    }

    /// <summary>独占写入区内发现的"竞争/保护"情形：映射为明确的 Rejected 结果，不改动状态。</summary>
    private sealed class PluginOperationBlockedException : Exception
    {
        public PluginOperationBlockedException(LocalPluginRejectionKind kind, string message) : base(message) =>
            Kind = kind;

        public LocalPluginRejectionKind Kind { get; }
    }

    /// <summary>
    /// 宿主（M）完成安全重启后调用：只按真实快照登记已确认的事实。
    /// 契约是只读核对——绝不删除文件、绝不自动执行更新/重启/模型请求；
    /// 实际删除只能由显式的 <see cref="RemoveVersionAsync"/> 且只作用于原目标版本。
    /// </summary>
    public async Task<int> ResolvePendingAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await SafeSnapshotAsync(cancellationToken);
        if (snapshot is null || snapshot.IsUnknown) return 0;

        var resolved = 0;
        await MutateAsync(state =>
        {
            var plugins = state.Plugins.ToList();
            for (var i = 0; i < plugins.Count; i++)
            {
                var record = plugins[i];

                // 只登记"宿主已实际加载待应用版本"这一条事实；在途操作（令牌未清）不覆盖。
                if (record.PendingApplyVersionId is { Length: > 0 } pendingId && record.OperationToken is null)
                {
                    var version = record.Versions.FirstOrDefault(v => v.VersionId == pendingId);
                    if (version is not null &&
                        IsVersionLoaded(record, ToVersion(record, version), snapshot.LoadedExtensionPaths))
                    {
                        plugins[i] = record with
                        {
                            PreviousVersionId = record.SelectedVersionId ?? record.PreviousVersionId,
                            SelectedVersionId = pendingId,
                            PendingApplyVersionId = null,
                            PendingRollback = false,
                            LoadError = null,
                            LoadErrorAtUtc = null,
                            UpdatedAtUtc = DateTimeOffset.UtcNow,
                        };
                        resolved++;
                    }
                }

            }

            return (PluginCatalogStore.ToState(plugins, state.Generation), resolved);
        }, cancellationToken);

        return resolved;
    }

    /// <summary>pending 必须绑定具体版本：只登记"这个版本待移除"。</summary>
    private async Task SetPendingRemoveAsync(string pluginId, string versionId, CancellationToken cancellationToken) =>
        await MutateAsync(state =>
        {
            var existing = FindPlugin(state, pluginId);
            var next = existing with
            {
                PendingRemoveVersionId = versionId,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };
            return (PluginCatalogStore.ToState(Replace(state, existing, next), state.Generation), 0);
        }, cancellationToken);

    private async Task<PluginRecord?> FindPluginAsync(string pluginId, CancellationToken cancellationToken) =>
        await MutateAsync(state =>
        {
            var found = state.Plugins.FirstOrDefault(p => string.Equals(p.Id, pluginId, StringComparison.Ordinal));
            return (state, found);
        }, cancellationToken);

    // ── 持久化与并发 ──

    /// <summary>
    /// 独占写入：OS 文件锁 + 目录状态 generation。读取后 generation 不匹配即拒绝，
    /// 损坏/未来版本文件原样保留并显式报错。
    /// </summary>
    private async Task<T> MutateAsync<T>(Func<PluginCatalogState, (PluginCatalogState Next, T Value)> update,
        CancellationToken cancellationToken, long? expectedGeneration = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SessionLease lease;
        try
        {
            lease = SessionLease.Acquire(Root, "plugins-local-catalog");
        }
        catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            // SessionLease 在锁文件被别的进程独占时抛 InvalidOperationException，
            // 某些文件系统上底层 IOException 会直接冒泡；两者都表示"另一个写入者持有目录"，
            // 统一成一句可解释的中文，不改动任何状态。
            throw new InvalidOperationException(
                "插件目录正被另一个进程或实例写入：本次未改动任何状态。", error);
        }

        using (lease)
        {
            var load = PluginCatalogStore.Load(Root);
            LastCorruptReason = load.IsCorrupt ? load.Reason : null;
            if (load.IsCorrupt || load.State is null)
            {
                throw new LocalPluginCatalogCorruptException(load.Reason ?? "目录状态不可用。");
            }

            var current = load.State;
            // compare-and-set：在任何副作用（复制/发布/目录删除）之前核对代次。
            if (expectedGeneration is { } expected && current.Generation != expected)
            {
                throw new InvalidOperationException(
                    $"目录状态代次为 {current.Generation}，与调用方读到的 {expected} 不同：拒绝用旧状态覆盖新状态。");
            }

            var (next, value) = update(current);
            if (ReferenceEquals(next, current)) return value;

            PluginCatalogStore.Save(Root, next with
            {
                SchemaVersion = PluginCatalogState.CurrentSchemaVersion,
                Generation = current.Generation + 1,
            });
            LastCorruptReason = null;
            return value;
        }
    }

    private async Task<HostPluginSnapshot?> SafeSnapshotAsync(CancellationToken cancellationToken)
    {
        if (_host is null) return null;
        try
        {
            return await _host.SnapshotAsync(cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return HostPluginSnapshot.Unavailable(error.GetBaseException().Message);
        }
    }

    private string VersionRoot(PluginRecord record, LocalPluginVersion version) =>
        Path.GetFullPath(Path.Combine(Root, record.StorageKey, version.DirectoryName));

    private static List<PluginRecord> Replace(PluginCatalogState state, PluginRecord existing, PluginRecord next)
    {
        var plugins = state.Plugins.ToList();
        var index = plugins.IndexOf(existing);
        if (index < 0) plugins.Add(next); else plugins[index] = next;
        return plugins;
    }

    private static PluginRecord FindPlugin(PluginCatalogState state, string pluginId) =>
        state.Plugins.FirstOrDefault(p => string.Equals(p.Id, pluginId, StringComparison.Ordinal))
        ?? throw new InvalidOperationException($"没有插件 {pluginId}。");

    /// <summary>不抛异常查找：供迟到回调等"记录可能已不存在"的收尾路径使用。</summary>
    private static PluginRecord? OptionalPlugin(PluginCatalogState state, string pluginId) =>
        state.Plugins.FirstOrDefault(p => string.Equals(p.Id, pluginId, StringComparison.Ordinal));

    /// <summary>宿主加载路径落在该副本目录内即视为该版本被使用。</summary>
    private static bool CoversPath(string versionRoot, string loadedPath)
    {
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(versionRoot));
            var full = Path.GetFullPath(loadedPath);
            return full.Equals(root, StringComparison.OrdinalIgnoreCase) ||
                   full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// 宿主报告的加载路径可以是副本目录根，也可以是副本内任意文件（Pi 报告入口文件）。
    /// 两种都要算命中，只比目录根会漏掉入口文件形式。
    /// </summary>
    private bool IsVersionLoaded(PluginRecord record, LocalPluginVersion version,
        IReadOnlyList<string> loadedPaths)
    {
        var versionRoot = VersionRoot(record, version);
        var entryPath = Path.GetFullPath(Path.Combine(versionRoot, version.EntryRelativePath));
        return loadedPaths.Any(path =>
        {
            if (CoversPath(versionRoot, path)) return true;
            try
            {
                return string.Equals(Path.GetFullPath(path), entryPath, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return false;
            }
        });
    }

    private static bool IsUnder(string root, string candidate)
    {
        try
        {
            var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
            return full.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // 保留现场：删除失败不能静默，也不能改动目录状态。
        }
    }
}

/// <summary>兼容判定：声明的精确宿主版本列表与实际注入的宿主身份匹配。</summary>
public static class PluginCompatibilityRules
{
    public static PluginCompatibility Evaluate(PluginBackend backend, IReadOnlyList<string> supportedHostVersions,
        HostIdentity identity)
    {
        // 首版明确不支持的后端：不看版本，直接 unsupported。
        if (backend is PluginBackend.Omp or PluginBackend.Codex or PluginBackend.Dsh)
            return PluginCompatibility.Unsupported;
        if (backend == PluginBackend.Unknown) return PluginCompatibility.Unsupported;

        // 缺身份：unknown，拒绝应用。
        if (!identity.IsKnown) return PluginCompatibility.Unknown;

        // 后端不同即不兼容（统一界面不代表统一运行格式）。
        if (identity.Backend != backend) return PluginCompatibility.Incompatible;

        if (!supportedHostVersions.Any(version => string.Equals(version.Trim(), identity.Version!.Trim(),
                StringComparison.Ordinal)))
        {
            return PluginCompatibility.Incompatible;
        }

        return PluginCompatibility.Compatible;
    }

    public static string Describe(PluginCompatibility compatibility, PluginBackend backend, HostIdentity identity,
        IReadOnlyList<string> supported)
    {
        var declared = supported.Count == 0 ? "(未声明)" : string.Join(", ", supported);
        var actual = identity.IsKnown ? $"{identity.Backend} {identity.Version}" : "(宿主身份未知)";
        return compatibility switch
        {
            PluginCompatibility.Compatible => $"兼容：声明 [{declared}] 与宿主 {actual} 精确匹配",
            PluginCompatibility.Unknown => $"未知：宿主身份未知，声明 [{declared}]，无法确认兼容",
            PluginCompatibility.Incompatible => $"不兼容：声明 [{declared}] 与宿主 {actual} 不匹配",
            _ => $"不支持：后端 {backend} 首版不支持（仅 Pi 有真实加载路径）",
        };
    }
}
