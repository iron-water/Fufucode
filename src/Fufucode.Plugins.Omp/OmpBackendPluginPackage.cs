using Fufucode.Core;
using Fufucode.Core.Backend;
using Fufucode.Core.Plugins;

namespace Fufucode.Plugins.Omp;

/// <summary>
/// OMP 后端插件入口。OMP 的 RPC 线路与 Pi 相同，插件携带 Fufucode.Plugins.Pi.dll；
/// 初始化时登记目录，Core 的 BackendPluginLoader 即可为 BackendId.Omp 创建客户端工厂。
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
        BackendPluginLoader.RegisterPluginDirectory(context.PackageDirectory);
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        BackendPluginLoader.ResetCache();
        return Task.CompletedTask;
    }
}
