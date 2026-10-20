using System;
using Fufucode.Core.Plugins;

namespace Fufucode.Plugins.InteractiveUI;

/// <summary>
/// 交互增强插件入口：声明 <c>ui</c> 围栏语言，把 JSONL 描述的组件渲染成原生控件。
/// 插件只依赖宿主契约，不引用聊天页类型。
/// </summary>
public sealed class InteractiveUiPluginPackage : IPluginPackage
{
    public string Id => "chat.interactive-ui";

    public string Version => "1.0.0";

    public IPluginToolHandler? CreateToolHandler(PluginPackageContext context) => null;

    public PluginContentContribution? CreateContentContribution(PluginPackageContext context) =>
        new("ui", content => new WidgetsView(content));
}
