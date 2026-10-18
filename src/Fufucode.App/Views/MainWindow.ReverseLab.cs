using System;
using Fufucode.App.Infrastructure;
using Fufucode.Core;
using Fufucode.Core.Backend;
using Fufucode.Core.Plugins;

namespace Fufucode.App;

/// <summary>
/// Seep 逆向工程工作台的组合根接线（SEEP-1）：
/// 把插件市场的实时状态接到 <see cref="SeepReverseLabPluginHost"/>，
/// 由宿主统一启动/停止 MCP 子进程、注册/注销 23 工具、联动 17890 合规转译中继，
/// 并把启用插件的人格片段暴露给原生会话（<see cref="IPluginSystemPromptProvider"/>）。
///
/// 门禁事实：非必需插件默认不安装（DefaultInstalled: false）。
/// 未安装时本接线不启动任何子进程、不注册任何工具、不渲染可用开关。
/// </summary>
public partial class MainWindow
{
    private PluginStateStore? _reverseLabStore;
    private PluginToolRegistry? _reverseLabToolRegistry;
    private SeepReverseLabPluginHost? _reverseLabHost;
    private IPluginSystemPromptProvider? _reverseLabPromptProvider;

    /// <summary>Seep 工具注册表（真实接线状态）；未创建时为 null。</summary>
    internal PluginToolRegistry? ReverseLabToolRegistryForTesting => _reverseLabToolRegistry;

    /// <summary>Seep 生命周期宿主（真实接线状态）；未创建时为 null。</summary>
    internal SeepReverseLabPluginHost? ReverseLabHostForTesting => _reverseLabHost;

    /// <summary>供会话使用的人格提供方；未接线时为 null（不注入插件人格）。</summary>
    internal IPluginSystemPromptProvider? SystemPromptProviderForTesting => _reverseLabPromptProvider;

    private string _reverseLabDataRoot() =>
        Program.SelfTestMode
            ? Program.SelfTestDataRoot ?? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fufucode-selftest-appdata")
            : _options.CreateAppDataLayout().Root;

    /// <summary>
    /// SEEP-1 组合根：创建独立的插件状态存储与工具注册表，注册生命周期宿主，
    /// 并按当前插件状态做一次对齐（未安装时不启动子进程）。
    /// 与 Browser Use 一样是独立注册表：两个插件互不影响彼此的门禁与工具清单。
    /// </summary>
    private void InitializeReverseLabPlugin()
    {
        var root = _reverseLabDataRoot();
        _reverseLabStore = new PluginStateStore(new AppDataLayout(root));
        var gateway = new PluginCapabilityGateway(_reverseLabStore);
        _reverseLabToolRegistry = new PluginToolRegistry(gateway, new ProductionToolDispatcher(_host?.ProjectPath));
        _reverseLabHost = new SeepReverseLabPluginHost(_reverseLabStore, _reverseLabToolRegistry);
        _reverseLabPromptProvider = new PluginStateSystemPromptProvider(_reverseLabStore);

        // 按当前真实状态对齐一次：已安装且已启用才启动子进程并注册工具。
        _ = SynchronizeReverseLabAsync();
    }

    private async System.Threading.Tasks.Task SynchronizeReverseLabAsync()
    {
        if (_reverseLabHost is null) return;
        try
        {
            await _reverseLabHost.SynchronizeAsync().ConfigureAwait(true);
        }
        catch (Exception error)
        {
            _vm?.Timeline.AddNotification("Seep 工作台插件对齐失败（保留其它功能）：" + error.GetBaseException().Message);
        }
    }

    /// <summary>收尾：终止本插件启动的 MCP 与中继子进程，避免残留进程。</summary>
    private async System.Threading.Tasks.Task ShutdownReverseLabAsync()
    {
        if (_reverseLabHost is null) return;
        try
        {
            await _reverseLabHost.DisposeAsync();
        }
        catch (Exception)
        {
            // 收尾失败不阻塞关窗；子进程 Kill 已尽力执行。
        }
    }
}
