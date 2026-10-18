using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Fufucode.App.Features.Plugins;
using Fufucode.Core;

namespace Fufucode.App.Integration;

/// <summary>当前真实Pi会话的本地固定版本适配；使用既有信任/安全重启链，不另起第二会话。</summary>
public sealed class PiLocalPluginHost : ILocalPluginRestartHost, ILocalPluginReconciliationHost
{
    private readonly AppDataLayout _layout;
    private readonly Func<RealSessionHost?> _current;
    private readonly Func<string?> _cli;
    private readonly Func<bool> _unavailable;
    private readonly Func<RealSessionHost, IReadOnlyList<string>, CancellationToken, Task<ExtensionApplyFailure?>> _restart;
    private readonly LocalPluginService _catalog;
    private readonly SemaphoreSlim _applyGate = new(1, 1);

    public PiLocalPluginHost(AppDataLayout layout, Func<RealSessionHost?> current, Func<string?> cli,
        Func<bool> unavailable, Func<RealSessionHost, IReadOnlyList<string>, CancellationToken, Task<ExtensionApplyFailure?>> restart)
    {
        _layout = layout; _current = current; _cli = cli; _unavailable = unavailable; _restart = restart;
        _catalog = new LocalPluginService(layout);
    }

    public HostIdentity Identity
    {
        get
        {
            var host = _current();
            if (host is null || !host.HasLiveSession || host.Current.Session.Backend != BackendId.Pi) return HostIdentity.Unknown();
            var version = DiagnosticsSummary.ResolvePiVersion(_cli());
            return version is "未知" or "不可用" ? HostIdentity.Unknown() : HostIdentity.Pi(version);
        }
    }

    public bool CanRestart
    {
        get
        {
            var host = _current();
            if (host is null || !host.HasLiveSession || _unavailable() || !Identity.IsKnown) return false;
            var state = host.Current.Coordinator.Snapshot;
            return !host.Current.Coordinator.IsInIdleMaintenance && state.Connection == ConnectionState.Connected && state.Activity == ActivityState.Idle &&
                state.QueuedTexts.Count == 0 && state.PendingUiRequests.Count == 0;
        }
    }

    private static string Context(RealSessionHost host) => host.ProjectPath + "\n" + host.Current.Session.Id + "\n" + host.Current.Coordinator.Snapshot.Generation;
    private static PluginHostContext? Scope(RealSessionHost host) => host.Current.Coordinator.Snapshot.Generation is { } generation
        ? new(host.ProjectPath, host.Current.Session.Id, generation) : null;

    public async Task<IPluginReconciliationLease?> AcquireReconciliationAsync(HostPluginSnapshot expectedSnapshot,
        CancellationToken cancellationToken = default)
    {
        if (!await _applyGate.WaitAsync(0, cancellationToken)) return null;
        IDisposable? maintenance = null;
        var transferred = false;
        try
        {
            var host = _current();
            if (!CanRestart || host is null || !expectedSnapshot.Success || expectedSnapshot.StructuredContext is null ||
                Scope(host) != expectedSnapshot.StructuredContext || Identity != expectedSnapshot.Identity) return null;
            var coordinator = host.Current.Coordinator;
            maintenance = coordinator.BeginIdleMaintenance();
            bool Current()
            {
                if (_unavailable() || !ReferenceEquals(_current(), host) || !host.HasLiveSession ||
                    !ReferenceEquals(host.Current.Coordinator, coordinator) || Scope(host) != expectedSnapshot.StructuredContext ||
                    Identity != expectedSnapshot.Identity || !coordinator.IsInIdleMaintenance) return false;
                var state = coordinator.Snapshot;
                return state.Connection == ConnectionState.Connected && state.Activity == ActivityState.Idle &&
                    state.QueuedTexts.Count == 0 && state.PendingUiRequests.Count == 0;
            }
            var lease = new ReconciliationLease(Current, maintenance, _applyGate);
            maintenance = null;
            transferred = true;
            try { if (lease.IsCurrent) return lease; }
            catch { lease.Dispose(); throw; }
            lease.Dispose();
            return null;
        }
        catch (InvalidOperationException) { return null; }
        finally
        {
            maintenance?.Dispose();
            if (!transferred) _applyGate.Release();
        }
    }

    private sealed class ReconciliationLease(Func<bool> current, IDisposable maintenance, SemaphoreSlim gate) : IPluginReconciliationLease
    {
        private IDisposable? _maintenance = maintenance;
        public bool IsCurrent => _maintenance is not null && current();
        public void Dispose()
        {
            var owned = Interlocked.Exchange(ref _maintenance, null);
            if (owned is null) return;
            try { owned.Dispose(); } finally { gate.Release(); }
        }
    }

    public async Task<HostPluginSnapshot> SnapshotAsync(CancellationToken cancellationToken = default)
    {
        var host = _current();
        if (host is null || !host.HasLiveSession || _unavailable() || !Identity.IsKnown)
            return HostPluginSnapshot.Unavailable("真实Pi连接/精确版本不可用。");
        var coordinator = host.Current.Coordinator;
        var state = coordinator.Snapshot;
        if (state.Connection != ConnectionState.Connected || state.Generation is not { } generation)
            return HostPluginSnapshot.Unavailable("Pi连接代次未确认。");
        var context = Context(host);
        var enabled = host.ActiveExtensionPaths.ToArray();
        var response = await coordinator.QueryCommandsAsync(cancellationToken);
        if (!ReferenceEquals(host, _current()) || !host.HasLiveSession || !ReferenceEquals(coordinator, host.Current.Coordinator) ||
            Context(host) != context || coordinator.Snapshot.Connection != ConnectionState.Connected || !response.Success)
            return HostPluginSnapshot.Unavailable("读取期间项目/会话/连接改变，未使用旧加载快照。");
        var loaded = new List<string>();
        foreach (var path in enabled)
        {
            if (PluginPackageScanner.IsInsideRoot(Path.Combine(_layout.Root, "plugins", "loaders"), path))
            {
                try
                {
                    var proof = PiPluginLoadProof.Open(_layout, path);
                    if (!await _catalog.IsKnownProofAsync(proof, cancellationToken) ||
                        !proof.TryConfirmInitialization(response, generation, generation, out _))
                        return HostPluginSnapshot.Unavailable("托管插件的原入口初始化证明未确认或目录内容已改变。");
                    loaded.Add(proof.EntryPath);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
                { return HostPluginSnapshot.Unavailable("加载身份不可核对，保留副本：" + error.GetBaseException().Message); }
            }
            else if (PluginPackageScanner.IsInsideRoot(_catalog.Root, path))
                return HostPluginSnapshot.Unavailable("托管目录中的旧直接入口缺初始化证明，保留副本与未知状态。");
        }
        if (!ReferenceEquals(host, _current()) || !host.HasLiveSession || Context(host) != context)
            return HostPluginSnapshot.Unavailable("核对期间连接改变。");
        return new HostPluginSnapshot(true, Identity, loaded)
        { ContextId = context, Generation = generation, StructuredContext = Scope(host) };
    }

    // 普通接口也只能走明确的安全重启，不会就地改加载文件。
    public async Task<HostApplyResult> ApplyAsync(string entry, string pluginId, string version, CancellationToken cancellationToken = default)
        => await ApplyWithRestartAsync(entry, pluginId, version, await SnapshotAsync(cancellationToken), cancellationToken);

    public async Task<HostApplyResult> ApplyWithRestartAsync(string entry, string pluginId, string version,
        HostPluginSnapshot expectedSnapshot, CancellationToken cancellationToken = default)
    {
        if (!await _applyGate.WaitAsync(0, cancellationToken)) return new(false, "已有插件应用在进行中，未启动第二次重启。");
        try
        {
            var host = _current();
            if (!CanRestart || host is null || !expectedSnapshot.Success || Context(host) != expectedSnapshot.ContextId)
                return new(false, "原项目/会话/连接已变更或未空闲，未重启。");
            var sessionId = host.Current.Session.Id;
            var originalCoordinator = host.Current.Coordinator;
            HostApplyResult Result(bool success, string message)
            {
                var exited = originalCoordinator.LastProcessExit;
                return new(success, message)
                { OriginalProcessExit = exited is not null && expectedSnapshot.Generation == exited.Generation ? exited : null };
            }
            var previous = host.EnabledExtensionPaths.ToArray();
            var item = (await _catalog.ListAsync(cancellationToken)).Items.SingleOrDefault(plugin => plugin.Id == pluginId)
                ?? throw new InvalidOperationException("插件记录不存在。");
            var candidate = item.Versions.Single(candidate => candidate.Version == version);
            var proof = await _catalog.PreparePiLoadProofAsync(pluginId, candidate.VersionId, cancellationToken);
            if (!RealSessionHost.PathEquals(entry, proof.EntryPath) || !CanRestart ||
                !ReferenceEquals(host, _current()) || Context(host) != expectedSnapshot.ContextId)
                return Result(false, "候选入口或原连接不再匹配，未重启。");
            var next = new List<string>();
            foreach (var path in previous)
            {
                if (PluginPackageScanner.IsInsideRoot(Path.Combine(_layout.Root, "plugins", "loaders"), path) &&
                    PiPluginLoadProof.Open(_layout, path).PluginId == pluginId) continue;
                next.Add(path);
            }
            next.Add(proof.LoaderPath);
            var failure = await _restart(host, next, cancellationToken);
            if (failure is not null)
            {
                var restored = await SnapshotAsync(cancellationToken);
                return Result(false, restored.Success
                    ? "候选初始化失败，原启用列表及真实加载快照已恢复：" + failure.Message
                    : "候选初始化失败，原加载快照未确认，保留所有副本：" + failure.Message);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(host, _current()) || !host.HasLiveSession || host.Current.Session.Id != sessionId)
                return Result(false, "重启完成前项目/会话改变，结果未知；未修改其他会话。");
            var snapshot = await SnapshotAsync(cancellationToken);
            if (snapshot.Success && snapshot.LoadedExtensionPaths.Any(path => RealSessionHost.PathEquals(path, proof.EntryPath)))
                return Result(true, "原会话安全恢复，固定入口已完成真实初始化。");
            // 启动成功而证明未确认也不能发布选定；明确恢复原启用列表，不把文件存在当成功。
            var restore = await _restart(host, previous, cancellationToken);
            return Result(false, restore is null ? "初始化证明未确认，已恢复原启用列表。" : "初始化证明与恢复均未确认，保留所有固定副本。");
        }
        finally { _applyGate.Release(); }
    }
}
