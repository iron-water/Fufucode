using Fufucode.Core;
using Fufucode.Core.Backend;
using Fufucode.Core.Plugins;

namespace Fufucode.Plugins.Claude;

/// <summary>
/// Claude 后端插件的物理包入口。安装/启用后注册会话工厂；
/// 停用/卸载时注销，Core 随即报告后端不可用，不保留半连接状态。
/// </summary>
public sealed class ClaudeBackendPluginPackage : IPluginPackage
{
    public const string PluginId = "backend.claude";

    public string Id => PluginId;
    public string Version => "1.0.0";

    public IReadOnlyList<PluginDependencyDeclaration> Dependencies =>
        Array.Empty<PluginDependencyDeclaration>();

    public IPluginToolHandler? CreateToolHandler(PluginPackageContext context) => null;

    public Task InitializeAsync(PluginPackageContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        // 只登记工厂，不在磁盘/内存里做隐式探测；卸载时对称注销。
        BackendPluginLoader.RegisterSessionFactory(new ClaudeSessionFactory());
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        BackendPluginLoader.UnregisterSessionFactory(BackendId.Claude);
        return Task.CompletedTask;
    }
}
