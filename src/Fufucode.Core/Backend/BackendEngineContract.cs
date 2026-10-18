using System;

namespace Fufucode.Core.Backend;

/// <summary>
/// 统一执行内核标识。默认内核为 FUMO（纯 C# 实现）；
/// Pi/Omp/Codex/Dsh/Claude 为可选后端插件，
/// 其中 Pi 是 RPC 内核，Omp 是独立 CLI 进程契约，其余为协议适配内核。
/// </summary>
public enum EngineId
{
    /// <summary>默认 RPC 内核。</summary>
    Pi,

    /// <summary>Oh My Pi 独立引擎（独立 CLI 进程契约）。</summary>
    Omp,

    /// <summary>Codex 协议适配内核。</summary>
    Codex,

    /// <summary>DeepSeek Harness 协议适配内核。</summary>
    Dsh,

    /// <summary>Claude Code 官方 CLI 的 stream-json 协议适配内核（官方接口，不读源码）。</summary>
    Claude,

    /// <summary>FUMO 原生内核（纯 C# 实现，零外部 Node/Python 依赖）。</summary>
    NativeCSharp,
}

/// <summary>
/// 执行内核的接入形态。只描述内核如何被承载，不代表能力协商结果。
/// </summary>
public enum BackendContractKind
{
    /// <summary>本应用管理的 RPC 子进程内核（默认路线）。</summary>
    RpcKernel,

    /// <summary>外部独立引擎 / CLI 进程契约，由外部安装并提供可执行文件。</summary>
    ExternalCliEngine,

    /// <summary>协议适配内核：本应用负责把外部协议映射到统一契约。</summary>
    ProtocolAdapter,

    /// <summary>纯托管原生运行时：应用内嵌原生执行，零外部进程依赖。</summary>
    NativeManagedRuntime,
}

/// <summary>
/// 统一后端引擎契约。五种执行内核共用同一抽象，调用方按标识解析后只依赖本接口。
/// </summary>
public interface IBackendEngine
{
    /// <summary>稳定内核标识。</summary>
    EngineId Id { get; }

    /// <summary>面向用户的显示名。</summary>
    string DisplayName { get; }

    /// <summary>接入形态。</summary>
    BackendContractKind ContractKind { get; }

    /// <summary>标准运行时探测结果（可用性、路径、版本）。</summary>
    BackendRuntime Runtime { get; }
}

/// <summary>
/// 标准运行时探测结果。<see cref="Version"/> 未知时为 null（不伪造版本号）。
/// </summary>
public sealed record BackendRuntime(
    bool IsAvailable,
    string? RuntimePath,
    string? Version,
    string? Diagnostic = null);

/// <summary>标准运行时探测入口，使注册表可注入真实探测或测试替身。</summary>
public interface IBackendRuntimeProbe
{
    BackendRuntime Probe(EngineId id);
}

/// <summary>内核注册表：按标识注册与解析，禁止重复注册与未知标识。</summary>
public interface IBackendRegistry
{
    IReadOnlyCollection<IBackendEngine> Engines { get; }

    bool TryResolve(EngineId id, out IBackendEngine engine);

    IBackendEngine Resolve(EngineId id);

    void Register(IBackendEngine engine);
}

/// <summary>引擎标识与持久化字符串（StorageId）之间的稳定映射。</summary>
public static class EngineIdMap
{
    /// <summary>持久化使用的稳定小写标识。</summary>
    public static string ToStorageId(EngineId id) => id switch
    {
        EngineId.Pi => "pi",
        EngineId.Omp => "omp",
        EngineId.Codex => "codex",
        EngineId.Dsh => "dsh",
        EngineId.Claude => "claude",
        EngineId.NativeCSharp => "fumo",
        _ => throw new ArgumentOutOfRangeException(nameof(id), id, "Unknown backend engine id."),
    };

    /// <summary>解析持久化标识；未知标识抛出而不静默回退。</summary>
    public static EngineId Parse(string id) => id switch
    {
        "pi" => EngineId.Pi,
        "omp" => EngineId.Omp,
        "codex" => EngineId.Codex,
        "dsh" => EngineId.Dsh,
        "claude" => EngineId.Claude,
        "fumo" or "csharp" or "native-csharp" => EngineId.NativeCSharp,
        _ => throw new ArgumentException($"Unknown backend engine id '{id}'.", nameof(id)),
    };

    /// <summary>尝试解析持久化标识。</summary>
    public static bool TryParse(string? id, out EngineId engine)
    {
        switch (id)
        {
            case "pi": engine = EngineId.Pi; return true;
            case "omp": engine = EngineId.Omp; return true;
            case "codex": engine = EngineId.Codex; return true;
            case "dsh": engine = EngineId.Dsh; return true;
            case "claude": engine = EngineId.Claude; return true;
            case "fumo" or "csharp" or "native-csharp": engine = EngineId.NativeCSharp; return true;
            default: engine = default; return false;
        }
    }
}
