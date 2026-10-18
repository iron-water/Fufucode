using System;
using System.Collections.Generic;
using System.Linq;

namespace Fufucode.Core.Backend;

/// <summary>
/// 线程安全的内核注册表。重复注册同一标识会抛出，避免静默覆盖；
/// 未知标识解析抛出而不回退到默认内核。
/// </summary>
public sealed class BackendRegistry : IBackendRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<EngineId, IBackendEngine> _engines = new();

    public BackendRegistry(IEnumerable<IBackendEngine>? engines = null)
    {
        if (engines is null) return;
        foreach (var engine in engines) Register(engine);
    }

    /// <summary>以运行时探测创建四个内置引擎的注册表。</summary>
    public static BackendRegistry CreateDefault(IBackendRuntimeProbe probe) =>
        new(BackendEngineFactory.CreateAll(probe));

    public IReadOnlyCollection<IBackendEngine> Engines
    {
        get { lock (_gate) return _engines.Values.ToArray(); }
    }

    public void Register(IBackendEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        lock (_gate)
        {
            if (!_engines.TryAdd(engine.Id, engine))
                throw new InvalidOperationException($"Backend engine '{EngineIdMap.ToStorageId(engine.Id)}' is already registered.");
        }
    }

    public bool TryResolve(EngineId id, out IBackendEngine engine)
    {
        lock (_gate) return _engines.TryGetValue(id, out engine!);
    }

    public IBackendEngine Resolve(EngineId id) =>
        TryResolve(id, out var engine)
            ? engine
            : throw new InvalidOperationException($"Backend engine '{EngineIdMap.ToStorageId(id)}' is not registered.");
}
