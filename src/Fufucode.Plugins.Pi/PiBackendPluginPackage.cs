using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Fufucode.Core.Plugins;

namespace Fufucode.Plugins.Pi;

/// <summary>
/// Pi 后端插件的物理包入口。默认包不随附本程序集；插件市场安装后由
/// <see cref="Fufucode.Core.BackendPluginLoader"/> 动态加载，并按需创建 RPC 客户端。
///
/// 本插件只提供后端能力，不贡献工具、页面或皮肤，因此对应创建方法返回 null。
/// </summary>
public sealed class PiBackendPluginPackage : IPluginPackage
{
    public const string PluginId = "backend.pi";

    public string Id => PluginId;
    public string Version => "1.0.0";

    public IReadOnlyList<PluginDependencyDeclaration> Dependencies => Array.Empty<PluginDependencyDeclaration>();

    public IPluginToolHandler? CreateToolHandler(PluginPackageContext context) => null;

    public Task InitializeAsync(PluginPackageContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        // 让后续后端查找能命中本程序集所在目录；不启动任何进程，真正启动发生在会话打开时。
        Fufucode.Core.BackendPluginLoader.RegisterPluginDirectory(context.PackageDirectory);
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        Fufucode.Core.BackendPluginLoader.ResetCache();
        return Task.CompletedTask;
    }
}
