using System;
using System.IO;
using System.Linq;
using Fufucode.Core.Backend;

namespace Fufucode.App.Backend.Tests;

/// <summary>五后端引擎注册与解析、运行时探测、标识映射。</summary>
internal static class BackendContractTests
{
    public static void Run(Checks checks)
    {
        RegistrationAndResolution(checks);
        RuntimeProbe(checks);
        EngineIdMapping(checks);
    }

    private static void RegistrationAndResolution(Checks checks)
    {
        var probe = new FakeRuntimeProbe()
            .Set(EngineId.Pi, true, "pi-node", "1.1.0")
            .Set(EngineId.Omp, true, "omp-cli", "18.8.5")
            .Set(EngineId.Codex, false)
            .Set(EngineId.Dsh, false)
            .Set(EngineId.Claude, true, "claude-cli", "2.1.296")
            .Set(EngineId.NativeCSharp, true, "dotnet", "10.0.401");
        var registry = BackendRegistry.CreateDefault(probe);

        checks.Equal(6, registry.Engines.Count, "默认注册表包含六个后端引擎");
        checks.Equal("Pi", registry.Resolve(EngineId.Pi).DisplayName, "Pi 引擎显示名为 Pi");
        checks.Equal("OMP", registry.Resolve(EngineId.Omp).DisplayName, "OMP 引擎显示名为 OMP");
        checks.Equal("Codex", registry.Resolve(EngineId.Codex).DisplayName, "Codex 引擎显示名为 Codex");
        checks.Equal("DSH", registry.Resolve(EngineId.Dsh).DisplayName, "DSH 引擎显示名为 DSH");
        checks.Equal("Claude", registry.Resolve(EngineId.Claude).DisplayName, "Claude 引擎显示名为 Claude");
        checks.Equal("FUMO", registry.Resolve(EngineId.NativeCSharp).DisplayName, "FUMO 引擎显示名为 FUMO");

        checks.Equal(BackendContractKind.RpcKernel, registry.Resolve(EngineId.Pi).ContractKind,
            "Pi 为 RPC 内核契约");
        checks.Equal(BackendContractKind.ExternalCliEngine, registry.Resolve(EngineId.Omp).ContractKind,
            "OMP 为独立 CLI 进程契约");
        checks.Equal(BackendContractKind.ProtocolAdapter, registry.Resolve(EngineId.Codex).ContractKind,
            "Codex 为协议适配内核");
        checks.Equal(BackendContractKind.ProtocolAdapter, registry.Resolve(EngineId.Dsh).ContractKind,
            "DSH 为协议适配内核");
        checks.Equal(BackendContractKind.ProtocolAdapter, registry.Resolve(EngineId.Claude).ContractKind,
            "Claude 为协议适配内核");
        checks.Equal(BackendContractKind.NativeManagedRuntime, registry.Resolve(EngineId.NativeCSharp).ContractKind,
            "NativeCSharp 为纯托管原生运行时");

        checks.Check(registry.TryResolve(EngineId.NativeCSharp, out var resolved) && resolved.Id == EngineId.NativeCSharp,
            "TryResolve 命中已注册原生内核");

        var empty = new BackendRegistry();
        checks.Check(!empty.TryResolve(EngineId.Codex, out _), "空注册表 TryResolve 返回 false");
        checks.Throws<InvalidOperationException>(() => empty.Resolve(EngineId.Codex),
            "解析未注册内核抛出而不回退");

        checks.Throws<InvalidOperationException>(
            () => registry.Register(new PiBackendEngine(probe)), "重复注册同一内核抛出");

        checks.Throws<ArgumentException>(() => EngineIdMap.Parse("bogus"),
            "解析未知持久化标识抛出");
    }

    private static void RuntimeProbe(Checks checks)
    {
        var root = Path.Combine(Path.GetTempPath(), "fufucode-backend-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var present = Path.Combine(root, "engine.bin");
            File.WriteAllText(present, "stub");
            var missing = Path.Combine(root, "missing.bin");

            var versions = new System.Collections.Generic.Dictionary<EngineId, string>
            {
                [EngineId.Omp] = "18.8.5",
            };
            var probe = new FileBackendRuntimeProbe(new BackendRuntimePaths()
                .Set(EngineId.Omp, present)
                .Set(EngineId.Dsh, missing), versions);

            var available = probe.Probe(EngineId.Omp);
            checks.Check(available.IsAvailable, "已配置且存在的运行时探测为可用");
            checks.Equal(Path.GetFullPath(present), available.RuntimePath, "运行时路径为规范化绝对路径");
            checks.Equal("18.8.5", available.Version, "运行时版本来自组合根提供值");

            var absent = probe.Probe(EngineId.Dsh);
            checks.Check(!absent.IsAvailable, "路径不存在时探测为不可用");
            checks.Check(absent.Diagnostic is not null, "不可用探测给出诊断信息");
            checks.Check(absent.Version is null, "不可用探测不伪造版本号");

            var unconfigured = probe.Probe(EngineId.Codex);
            checks.Check(!unconfigured.IsAvailable && unconfigured.RuntimePath is null,
                "未配置路径的内核探测为不可用且不猜测路径");

            var nativeProbe = probe.Probe(EngineId.NativeCSharp);
            checks.Check(nativeProbe.IsAvailable && nativeProbe.RuntimePath is not null && nativeProbe.Version is not null,
                "纯 C# 原生内核始终可用且暴露当前进程与 CLR 版本");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void EngineIdMapping(Checks checks)
    {
        var all = Enum.GetValues<EngineId>();
        checks.Equal(6, all.Length, "EngineId 枚举恰有六个内核");

        foreach (var id in all)
        {
            var storageId = EngineIdMap.ToStorageId(id);
            checks.Equal(id, EngineIdMap.Parse(storageId), $"标识 {storageId} 往返解析一致");
            checks.Check(EngineIdMap.TryParse(storageId, out var parsed) && parsed == id,
                $"TryParse 识别 {storageId}");
        }

        checks.Equal("pi", EngineIdMap.ToStorageId(EngineId.Pi), "Pi 持久化标识为 pi");
        checks.Equal("omp", EngineIdMap.ToStorageId(EngineId.Omp), "OMP 持久化标识为 omp");
        checks.Equal("codex", EngineIdMap.ToStorageId(EngineId.Codex), "Codex 持久化标识为 codex");
        checks.Equal("dsh", EngineIdMap.ToStorageId(EngineId.Dsh), "DSH 持久化标识为 dsh");
        checks.Equal("claude", EngineIdMap.ToStorageId(EngineId.Claude), "Claude 持久化标识为 claude");
        checks.Equal("fumo", EngineIdMap.ToStorageId(EngineId.NativeCSharp), "FUMO 持久化标识为 fumo");
        checks.Check(!EngineIdMap.TryParse("PI", out _), "标识解析区分大小写，不静默接受");
        checks.Check(EngineIdMap.TryParse(null, out _) == false, "TryParse(null) 返回 false");
    }
}
