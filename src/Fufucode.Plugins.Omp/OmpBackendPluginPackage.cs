using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Fufucode.Core;
using Fufucode.Core.Plugins;
using Fufucode.Pi;

namespace Fufucode.Plugins.Omp;

/// <summary>
/// OMP 后端插件入口。OMP 的 RPC 线路与 Pi 相同，插件携带 Fufucode.Plugins.Pi.dll；
/// 与 Pi/Claude/Codex/DSH 同一套注册约定：初始化时登记 RPC 工厂，停用时注销。
/// </summary>
public sealed class OmpBackendPluginPackage : IPluginPackage
{
    public const string PluginId = "backend.omp";

    public string Id => PluginId;
    public string Version => "1.0.0";

    public IReadOnlyList<PluginDependencyDeclaration> Dependencies =>
        Array.Empty<PluginDependencyDeclaration>();

    public IPluginToolHandler? CreateToolHandler(PluginPackageContext context) => null;

    public Task InitializeAsync(PluginPackageContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        BackendPluginLoader.RegisterRpcClientFactory(BackendId.Omp, spec => new PiRpcClientFactory(spec));
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        BackendPluginLoader.UnregisterRpcClientFactory(BackendId.Omp);
        return Task.CompletedTask;
    }
}
