using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Fufucode.App.Features.Plugins;

/// <summary>原在途登记的只读观察，不合成回执，也不授权解除保护。</summary>
public sealed record PluginInFlightObservation(string PluginId, InFlightCandidate Request,
    PluginReceiptObservationKind Kind, long CatalogGeneration, string? HostContext, string Explanation)
{
    public PluginCallbackCompletion? CallbackCompletion { get; init; }
    public string CallbackDiagnostic { get; init; } = string.Empty;
}

public sealed partial class LocalPluginService
{
    public async Task<PluginInFlightObservation> InspectInFlightAsync(string pluginId, string token,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("未提供原请求标识，未核对其他请求。");
        var before = PluginCatalogStore.Load(Root);
        if (before.IsCorrupt || before.State is null) throw new LocalPluginCatalogCorruptException(before.Reason ?? "目录不可读。");
        var record = before.State.Plugins.SingleOrDefault(plugin => plugin.Id == pluginId)
            ?? throw new InvalidOperationException("原插件记录不存在，未核对其他插件。");
        var request = record.InFlight.SingleOrDefault(candidate => candidate.Token == token)
            ?? throw new InvalidOperationException("原请求的在途登记已不存在，请刷新查看原回执；未改用其他请求。");
        PluginInFlightObservation Observe(PluginReceiptObservationKind kind, string text, string? context = null)
        {
            var journal = PluginCallbackJournal.Read(Root, pluginId, request);
            return new(pluginId, request, kind, before.State.Generation, context, text)
            { CallbackCompletion = journal.Completion, CallbackDiagnostic = journal.Diagnostic };
        }
        var snapshot = await SafeSnapshotAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var after = PluginCatalogStore.Load(Root);
        if (after.IsCorrupt || after.State is null || JsonSerializer.Serialize(before.State) != JsonSerializer.Serialize(after.State))
            return Observe(PluginReceiptObservationKind.Stale, "核对期间原登记或目录改变，请刷新后重新选择原请求。未解除保护。");
        if (snapshot is null || snapshot.IsUnknown || !snapshot.Identity.IsKnown || snapshot.Identity != CurrentHostIdentity)
            return Observe(PluginReceiptObservationKind.Unknown, "宿主快照或身份无法确认；原请求尚无结束回执，保护保持。", snapshot?.ContextId);
        var version = record.Versions.SingleOrDefault(candidate => candidate.VersionId == request.VersionId);
        if (version is null) return Observe(PluginReceiptObservationKind.Unknown, "原版本副本不存在，未核对其他版本。保护保持。", snapshot.ContextId);
        var candidateVersion = ToVersion(record, version);
        if (PluginCompatibilityRules.Evaluate(candidateVersion.DeclaredBackend, candidateVersion.DeclaredHostVersions,
                snapshot.Identity) != PluginCompatibility.Compatible)
            return Observe(PluginReceiptObservationKind.Unknown, "当前宿主与原版本不兼容，不能确认原请求。保护保持。", snapshot.ContextId);
        var integrity = VerifyManagedCopy(record, candidateVersion);
        if (integrity is not null) return Observe(PluginReceiptObservationKind.Unknown, integrity + " 保护保持。", snapshot.ContextId);
        var entry = Path.Combine(VersionRoot(record, candidateVersion), candidateVersion.EntryRelativePath);
        var loaded = snapshot.LoadedExtensionPaths.Any(path => RealPathEquals(path, entry));
        return Observe(loaded ? PluginReceiptObservationKind.Loaded : PluginReceiptObservationKind.NotLoaded,
            (loaded ? "此刻宿主实际加载原版本；不能认证原请求已成功。"
                : "此刻宿主未加载原版本；不能证明原回调不会再产生副作用。") +
            " 未结束请求仍受保护；未重发、未改选定、未解除保护。", snapshot.ContextId);
    }
}
