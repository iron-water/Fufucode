using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;

namespace Fufucode.App.Abstractions.Automation.Harnesses;

// 执行基库（宿主契约）：运行环境描述、一次运行的请求与结果、协议适配器身份、
// harness id → 适配器的登记点。具体协议实现随自动开发插件发布；宿主与其内置模块
// （本地审查）只依赖本文件，停止/卸载插件后这里不再有任何已登记的 harness。

// Deployment (installed CLI, bundled runtime, remote host) is distinct from protocol.
public sealed record HarnessRuntime(string Id, string Harness, string Executable,
    string[] PrefixArguments, string Version, Dictionary<string, string>? Environment = null)
{
    // Deployment owner opt-in, never a repair-client override. Null preserves old payload hashes.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? CodexSandbox { get; init; }
}

public sealed record HarnessRequest(string RunId, string ThreadId, string TaskId,
    string Model, string Prompt, string? Provider = null);

public sealed record HarnessCapabilities(bool Resume, bool Streaming, bool InteractiveApproval,
    bool GracefulCancel, bool AttachRunning);

public enum HarnessRunStatus { Dispatching, Running, Completed, Failed, Interrupted, Unknown }

public sealed record HarnessRun(string RunId, string ThreadId, HarnessRunStatus Status,
    string? SessionId, string? SessionLocator, string? Output, string? Error)
{
    /// <summary>
    /// The task this run was dispatched for, persisted from the dispatch request at claim time.
    /// Null only for rows written before this column existed; nothing here is inferred from output
    /// text or from a request hash.
    /// </summary>
    public string? TaskId { get; init; }
    /// <summary>Owner evidence recorded by the launching supervisor, never inferred from a bare pid.</summary>
    public string? Worktree { get; init; }
    /// <summary>Supervisor (host) process identity: pid plus start time, recorded before the child is awaited.</summary>
    public int? SupervisorPid { get; init; }
    public long? SupervisorStartUtc { get; init; }
    /// <summary>The harness child process that actually writes into the worktree.</summary>
    public int? WriterPid { get; init; }
    public long? WriterStartUtc { get; init; }
    public string? HostId { get; init; }
    public string? WorktreeHead { get; init; }
    /// <summary>
    /// Repository HEAD read by the supervising host immediately after this run's own writer process
    /// exited. A delivery is bound to this value; null means the provenance was never observed, which
    /// is never replaced by a later reading of the same worktree.
    /// </summary>
    public string? SettleHead { get; init; }
}

public sealed record HarnessSignal(string Kind, string? SessionId = null,
    string? Text = null, bool? Success = null);

/// <summary>Protocol adapters never approve deliveries or own project planning.</summary>
public interface IHarnessAdapter
{
    string Id { get; }
    HarnessCapabilities Capabilities { get; }
    ProcessStartInfo Prepare(HarnessRuntime runtime, HarnessRequest request,
        string worktree, string runDirectory, string? savedSession);
    HarnessSignal Parse(JsonElement record);
}

public static class HarnessAdapters
{
    // 适配器实现属于自动开发插件；宿主只登记“哪个 id 由哪个装载器提供”，
    // 插件加载时注册、卸载时注销。未登记时与未知 harness 同样拒绝。
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Func<IHarnessAdapter>>
        Registered = new(StringComparer.OrdinalIgnoreCase);

    public static void Register(string id, Func<IHarnessAdapter> factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(factory);
        Registered[id] = factory;
    }

    public static bool Unregister(string id) => Registered.TryRemove(id, out _);

    public static IReadOnlyCollection<string> RegisteredIds => System.Linq.Enumerable.ToArray(Registered.Keys);

    public static IHarnessAdapter Get(string id) =>
        Registered.TryGetValue(id, out var factory)
            ? factory()
            : throw new NotSupportedException($"Unknown harness: {id}");
}
