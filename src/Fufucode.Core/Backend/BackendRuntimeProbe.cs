using System;
using System.Collections.Generic;
using System.IO;

namespace Fufucode.Core.Backend;

/// <summary>
/// 显式运行时路径解析。调用方（组合根）给出各内核的实际入口路径；
/// 未给出的内核视为未安装，探测不猜测路径。
/// </summary>
public sealed class BackendRuntimePaths
{
    private readonly Dictionary<EngineId, string> _paths = new();

    public BackendRuntimePaths Set(EngineId id, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) _paths.Remove(id);
        else _paths[id] = Path.GetFullPath(path);
        return this;
    }

    public string? Get(EngineId id) => _paths.TryGetValue(id, out var path) ? path : null;
}

/// <summary>
/// 基于显式路径的运行时探测。以文件实际存在与否判定可用性，
/// 版本号由组合根提供（本类不执行任何外部进程，不伪造版本）。
/// </summary>
public sealed class FileBackendRuntimeProbe : IBackendRuntimeProbe
{
    private readonly BackendRuntimePaths _paths;
    private readonly IReadOnlyDictionary<EngineId, string> _versions;

    public FileBackendRuntimeProbe(BackendRuntimePaths paths,
        IReadOnlyDictionary<EngineId, string>? versions = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _versions = versions ?? new Dictionary<EngineId, string>();
    }

    public BackendRuntime Probe(EngineId id)
    {
        if (id == EngineId.NativeCSharp)
        {
            var hostPath = Environment.ProcessPath ?? "dotnet";
            var clrVersion = Environment.Version.ToString();
            return new BackendRuntime(true, hostPath, clrVersion, "纯 C# 原生内核（全托管无外部依赖）");
        }

        var path = _paths.Get(id);
        if (path is null)
            return new BackendRuntime(false, null, null, "未配置该内核的运行时路径。");
        if (!File.Exists(path))
            return new BackendRuntime(false, path, null, "运行时路径不存在。");
        return new BackendRuntime(true, path, _versions.TryGetValue(id, out var version) ? version : null);
    }
}
