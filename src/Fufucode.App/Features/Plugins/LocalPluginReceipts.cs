using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Fufucode.App.Features.Plugins;

public enum PluginReceiptObservationKind { Unknown, Loaded, NotLoaded, Stale }

/// <summary>原请求回执与此刻的只读观察分开；不重写原结果、不认证请求曾成功。</summary>
public sealed record PluginReceiptObservation(string PluginId, ApplyReceipt Receipt,
    PluginReceiptObservationKind Kind, long CatalogGeneration, string? HostContext, string Explanation);

public sealed partial class LocalPluginService
{
    private static IReadOnlyList<ApplyReceipt> ReceiptHistory(PluginRecord record) => record.RecentReceipts
        .Concat(record.LastApplyReceipt is { } last ? new[] { last } : Array.Empty<ApplyReceipt>())
        .DistinctBy(receipt => receipt.Token).OrderByDescending(receipt => receipt.AtUtc).ToArray();

    public async Task<PluginReceiptObservation> InspectReceiptAsync(string pluginId, string token,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var before = PluginCatalogStore.Load(Root);
        if (before.IsCorrupt || before.State is null) throw new LocalPluginCatalogCorruptException(before.Reason ?? "目录不可读。");
        var record = before.State.Plugins.SingleOrDefault(plugin => plugin.Id == pluginId)
            ?? throw new InvalidOperationException("原插件记录不存在，未核对其他插件。");
        var receipt = ReceiptHistory(record).SingleOrDefault(candidate => candidate.Token == token)
            ?? throw new InvalidOperationException("原回执不存在，未改用最新回执。");
        PluginReceiptObservation Observe(PluginReceiptObservationKind kind, string text, string? context = null)
            => new(pluginId, receipt, kind, before.State.Generation, context, text);
        var snapshot = await SafeSnapshotAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var after = PluginCatalogStore.Load(Root);
        if (after.IsCorrupt || after.State is null ||
            JsonSerializer.Serialize(before.State) != JsonSerializer.Serialize(after.State))
            return Observe(PluginReceiptObservationKind.Stale, "核对期间目录或原回执改变，请重新选择原回执后核对。");
        if (snapshot is null || snapshot.IsUnknown || !snapshot.Identity.IsKnown || snapshot.Identity != CurrentHostIdentity)
            return Observe(PluginReceiptObservationKind.Unknown, "宿主快照或身份无法确认；原结果保持未知。", snapshot?.ContextId);
        var version = record.Versions.SingleOrDefault(candidate => candidate.VersionId == receipt.VersionId);
        if (version is null) return Observe(PluginReceiptObservationKind.Unknown, "原版本副本不存在，不能核对其他版本。", snapshot.ContextId);
        var candidateVersion = ToVersion(record, version);
        if (PluginCompatibilityRules.Evaluate(candidateVersion.DeclaredBackend, candidateVersion.DeclaredHostVersions,
                snapshot.Identity) != PluginCompatibility.Compatible)
            return Observe(PluginReceiptObservationKind.Unknown, "当前宿主与原版本不兼容，不能确认此请求。", snapshot.ContextId);
        var integrity = VerifyManagedCopy(record, candidateVersion);
        if (integrity is not null) return Observe(PluginReceiptObservationKind.Unknown, integrity, snapshot.ContextId);
        var entry = Path.Combine(VersionRoot(record, candidateVersion), candidateVersion.EntryRelativePath);
        var loaded = snapshot.LoadedExtensionPaths.Any(path => RealPathEquals(path, entry));
        return Observe(loaded ? PluginReceiptObservationKind.Loaded : PluginReceiptObservationKind.NotLoaded,
            loaded ? "此刻宿主实际加载原版本；原请求结果保持原样。未重发、未改选定或解除在途保护。"
                : "此刻宿主未加载原版本；不能据此证明原请求从未执行或解除在途保护。", snapshot.ContextId);
    }
}
