using System;

namespace Fufucode.App.Features.Plugins;

/// <summary>
/// 宿主把插件页面挂进停靠区契约时使用的创建入口。
/// 插件可以返回实现了 <see cref="IPluginWorkspacePage"/> 的对象，或返回
/// <see cref="PluginPageView"/> 适配器；后者只依赖 <see cref="PluginPageHost"/>，
/// 插件工程不必引用 Fufucode.App 即可贡献页面。
/// </summary>
public interface IPluginPageProvider
{
    /// <summary>创建页面；每次插件激活只调用一次。</summary>
    object CreatePluginPage();
}
