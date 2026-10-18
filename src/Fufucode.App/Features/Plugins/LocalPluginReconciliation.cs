using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Fufucode.Core;

namespace Fufucode.App.Features.Plugins;

public sealed partial class LocalPluginService
{
    // SessionLease accepts restricted names; preserve opaque original tokens through a hash.
    internal static string RequestLeaseKey(string token) => "plugin-request-" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>Ends one returned registration, not the plugin's external effects or application result.</summary>
    public async Task<LocalPluginActionResult> ReconcileReturnedRequestAsync(string pluginId, string token,
        long expectedGeneration, CancellationToken cancellationToken = default)
    {
        LocalPluginActionResult Reject(string message) => LocalPluginActionResult.Rejected(
            LocalPluginRejectionKind.RemoveNotAllowed, message + " 原登记、文件与回执保持。");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(token) || _host is not ILocalPluginReconciliationHost host)
                return Reject("宿主未提供请求对账能力。");
            var before = PluginCatalogStore.Load(Root);
            if (before.IsCorrupt || before.State is null || before.State.Generation != expectedGeneration)
                return Reject("目录不可读或核对已过期，请重新核对原请求。");
            var record = FindPlugin(before.State, pluginId);
            var request = record.InFlight.SingleOrDefault(item => item.Token == token);
            if (request is null) return Reject("原请求已不在登记中；未改用其他请求。");
            var completion = PluginCallbackJournal.Read(Root, pluginId, request).Completion;
            if (completion is not { RequestLeaseVersion: 1, OriginalContext: { } original,
                    OriginalProcessExit: { } exited } || completion.Outcome is not ("returned_success" or "returned_failure") ||
                !completion.HostIdentity.IsKnown || completion.HostIdentity.Backend != PluginBackend.Pi ||
                original.Generation == Guid.Empty || original.Generation != completion.OriginalGeneration ||
                exited.Generation != original.Generation || string.IsNullOrWhiteSpace(original.ProjectPath) ||
                string.IsNullOrWhiteSpace(original.SessionId))
                return Reject("缺少可核对的原返回、结构化会话或原连接退出事实；未知/取消/异常请求不能结束登记。");

            // The OS lease outlives the original host callback AND its catalog completion writer.
            using var holder = SessionLease.Acquire(Root, RequestLeaseKey(token));
            var snapshot = await SafeSnapshotAsync(cancellationToken);
            if (snapshot is not { Success: true, StructuredContext: { } current } ||
                snapshot.Identity != completion.HostIdentity || snapshot.Identity != CurrentHostIdentity ||
                current.ProjectPath != original.ProjectPath || current.SessionId != original.SessionId ||
                current.Generation == Guid.Empty || current.Generation != snapshot.Generation || current.Generation == original.Generation)
                return Reject("当前真实宿主不在原项目/会话的新连接中。");
            var version = record.Versions.SingleOrDefault(item => item.VersionId == request.VersionId);
            if (version is null) return Reject("原版本不存在。");
            var candidate = ToVersion(record, version);
            if (PluginCompatibilityRules.Evaluate(candidate.DeclaredBackend, candidate.DeclaredHostVersions, snapshot.Identity)
                    != PluginCompatibility.Compatible || VerifyManagedCopy(record, candidate) is not null)
                return Reject("原副本不完整或与当前宿主不兼容。");
            if (IsVersionLoaded(record, candidate, snapshot.LoadedExtensionPaths))
                return Reject("当前宿主仍加载原候选；请先明确切换到其他固定版本。");
            using var runtime = await host.AcquireReconciliationAsync(snapshot, cancellationToken);
            if (runtime is null || !runtime.IsCurrent) return Reject("宿主有任务/维护或连接已改变，未取得空闲对账门。");
            return await MutateAsync(state =>
            {
                // No await or engine action in this catalog critical section.
                var existing = FindPlugin(state, pluginId);
                var mine = existing.InFlight.SingleOrDefault(item => item.Token == token);
                if (mine != request || !runtime.IsCurrent || CurrentHostIdentity != completion.HostIdentity ||
                    PluginCallbackJournal.Read(Root, pluginId, request).Completion != completion ||
                    VerifyManagedCopy(existing, candidate) is not null)
                    return (state, Reject("原请求、返回记录或宿主在提交前改变。"));
                var ownsPending = existing.OperationToken == token;
                var next = existing with
                {
                    InFlight = existing.InFlight.Where(item => item.Token != token).ToArray(),
                    OperationToken = ownsPending ? null : existing.OperationToken,
                    PendingApplyVersionId = ownsPending && existing.PendingApplyVersionId == request.VersionId
                        ? null : existing.PendingApplyVersionId,
                    PendingRollback = ownsPending ? false : existing.PendingRollback,
                    Reconciliations = existing.Reconciliations.Append(new PluginRegistrationReconciliation(
                        request, completion, current, DateTimeOffset.UtcNow)).TakeLast(MaxReceiptHistory).ToArray(),
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                };
                return (PluginCatalogStore.ToState(Replace(state, existing, next), state.Generation),
                    LocalPluginActionResult.Ok(LocalPluginStatus.Installed,
                        "已结束原已返回请求的登记；文件、原回执与选定版本保留。未删除、未重发、未认证外部任务终止或应用成功。"));
            }, cancellationToken, expectedGeneration);
        }
        catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException)
        { return Reject("对账未提交：" + error.GetBaseException().Message); }
    }
}
