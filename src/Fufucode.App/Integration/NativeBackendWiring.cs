using System;
using System.Collections.Generic;
using Fufucode.Core.Backend;
using Fufucode.Core.Plugins;

namespace Fufucode.App.Integration;

/// <summary>
/// 组合根到原生内核的插件接线：MainWindow 在创建插件注册表后登记，
/// <see cref="RealSessionHost"/> 打开 FUMO 会话时读取并注入。
/// 读取的是延迟委托，安装/卸载插件后下一次请求立即生效。
/// </summary>
public static class NativeBackendWiring
{
    /// <summary>返回当前所有实际存在的插件工具注册表。</summary>
    public static Func<IReadOnlyList<IPureToolDispatcher>>? ToolProviders { get; set; }

    /// <summary>启用插件提供的人格片段（目前为 Seep 作战协议）；未启用时为 null。</summary>
    public static IPluginSystemPromptProvider? SystemPromptProvider { get; set; }
}
