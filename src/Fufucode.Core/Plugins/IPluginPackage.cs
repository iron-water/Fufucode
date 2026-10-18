using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Fufucode.Core.Backend;

namespace Fufucode.Core.Plugins;

/// <summary>
/// 插件声明的外部共享依赖（按需从共享包池获取与部署）。
/// </summary>
public sealed record PluginDependencyDeclaration(
    string PackageId,
    string Version,
    string? Sha256 = null,
    string? TargetFileName = null);

/// <summary>
/// 插件贡献的工作区功能页。
/// </summary>
public sealed record PluginPageContribution(
    string PageId,
    string Title,
    string IconKey,
    string ModuleId,
    Func<object> CreateView,
    Action<object>? DetachView = null);

/// <summary>
/// 插件贡献的个性化皮肤定义。
/// </summary>
public sealed record PluginSkinContribution(
    string StorageKey,
    string DisplayName,
    string Summary,
    string Description,
    string? TokenDictionaryUri,
    string? ScopedStylesUri);

/// <summary>
/// 物理插件包上下文，向插件提供安装目录与服务解析入口。
/// </summary>
public sealed record PluginPackageContext(
    string PluginId,
    string PackageDirectory,
    string? ProjectPath,
    Func<Type, object?>? ServiceResolver = null)
{
    public T? GetService<T>() where T : class => ServiceResolver?.Invoke(typeof(T)) as T;
}

/// <summary>
/// 物理插件包统一入口契约（由独立插件程序集实现）。
/// 插件包在被 <see cref="PluginPackageManager"/> 动态加载后，通过本接口发现并激活。
/// </summary>
public interface IPluginPackage
{
    /// <summary>插件唯一标识，必须与 <see cref="PluginMarketplaceCatalog"/> 登记的 ID 一致。</summary>
    string Id { get; }

    /// <summary>插件版本。</summary>
    string Version { get; }

    /// <summary>插件声明的外部共享依赖列表（默认无依赖）。</summary>
    IReadOnlyList<PluginDependencyDeclaration> Dependencies => Array.Empty<PluginDependencyDeclaration>();

    /// <summary>
    /// 创建该插件提供的工具处理器（如果该插件提供 Agent 工具）。
    /// </summary>
    IPluginToolHandler? CreateToolHandler(PluginPackageContext context);

    /// <summary>
    /// 插件贡献的工作区页面（可选）。
    /// </summary>
    PluginPageContribution? CreatePageContribution(PluginPackageContext context) => null;

    /// <summary>
    /// 插件贡献的主题皮肤（可选）。
    /// </summary>
    PluginSkinContribution? CreateSkinContribution(PluginPackageContext context) => null;

    /// <summary>
    /// 插件初始化生命周期钩子（可选）。
    /// </summary>
    Task InitializeAsync(PluginPackageContext context, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <summary>
    /// 插件停用或卸载生命周期钩子（必须释放进程、连接与文件句柄）。
    /// </summary>
    Task ShutdownAsync(CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
