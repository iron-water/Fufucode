using System;
using System.Collections.Generic;
using System.Linq;

namespace Fufucode.Core.Backend;

/// <summary>
/// 后端引擎基类：固定标识与显示名，运行时探测交给注入的探测入口。
/// </summary>
public abstract class BackendEngineBase : IBackendEngine
{
    private readonly IBackendRuntimeProbe _probe;

    protected BackendEngineBase(EngineId id, string displayName, BackendContractKind contractKind,
        IBackendRuntimeProbe probe)
    {
        Id = id;
        DisplayName = displayName;
        ContractKind = contractKind;
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
    }

    public EngineId Id { get; }

    public string DisplayName { get; }

    public BackendContractKind ContractKind { get; }

    public BackendRuntime Runtime => _probe.Probe(Id);
}

/// <summary>Pi：默认 RPC 内核（本应用管理的 Node/RPC 子进程）。</summary>
public sealed class PiBackendEngine : BackendEngineBase
{
    public PiBackendEngine(IBackendRuntimeProbe probe)
        : base(EngineId.Pi, "Pi", BackendContractKind.RpcKernel, probe)
    {
    }
}

/// <summary>Omp：Oh My Pi 独立引擎，外部 CLI 进程契约。</summary>
public sealed class OmpBackendEngine : BackendEngineBase
{
    public OmpBackendEngine(IBackendRuntimeProbe probe)
        : base(EngineId.Omp, "OMP", BackendContractKind.ExternalCliEngine, probe)
    {
    }
}

/// <summary>Codex：Codex 协议适配内核。</summary>
public sealed class CodexBackendEngine : BackendEngineBase
{
    public CodexBackendEngine(IBackendRuntimeProbe probe)
        : base(EngineId.Codex, "Codex", BackendContractKind.ProtocolAdapter, probe)
    {
    }
}

/// <summary>Dsh：DeepSeek Harness 协议适配内核。</summary>
public sealed class DshBackendEngine : BackendEngineBase
{
    public DshBackendEngine(IBackendRuntimeProbe probe)
        : base(EngineId.Dsh, "DSH", BackendContractKind.ProtocolAdapter, probe)
    {
    }
}

/// <summary>
/// Claude：官方 Claude Code CLI 的 stream-json 协议适配内核。
/// 只调用已安装的官方可执行文件，不读、不改、不反编译其源码或二进制。
/// </summary>
public sealed class ClaudeBackendEngine : BackendEngineBase
{
    public ClaudeBackendEngine(IBackendRuntimeProbe probe)
        : base(EngineId.Claude, "Claude", BackendContractKind.ProtocolAdapter, probe)
    {
    }
}

/// <summary>FUMO：全托管纯 C# 原生内核（零外部进程依赖，由 NativeAgentLoop 驱动）。</summary>
public sealed class NativeCSharpBackendEngine : BackendEngineBase
{
    public NativeCSharpBackendEngine(IBackendRuntimeProbe probe)
        : base(EngineId.NativeCSharp, "FUMO", BackendContractKind.NativeManagedRuntime, probe)
    {
    }
}

/// <summary>按标识创建内置引擎的工厂。</summary>
public static class BackendEngineFactory
{
    public static IBackendEngine Create(EngineId id, IBackendRuntimeProbe probe) => id switch
    {
        EngineId.Pi => new PiBackendEngine(probe),
        EngineId.Omp => new OmpBackendEngine(probe),
        EngineId.Codex => new CodexBackendEngine(probe),
        EngineId.Dsh => new DshBackendEngine(probe),
        EngineId.Claude => new ClaudeBackendEngine(probe),
        EngineId.NativeCSharp => new NativeCSharpBackendEngine(probe),
        _ => throw new ArgumentOutOfRangeException(nameof(id), id, "Unknown backend engine id."),
    };

    /// <summary>创建全部内置引擎（按 <see cref="EngineId"/> 枚举顺序）。</summary>
    public static IReadOnlyList<IBackendEngine> CreateAll(IBackendRuntimeProbe probe) =>
        Enum.GetValues<EngineId>().Select(id => Create(id, probe)).ToArray();
}
