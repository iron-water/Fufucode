using System.Threading;
using System.Threading.Tasks;
using Fufucode.App.Features.RemoteControl;
using Fufucode.Core.Plugins;

namespace Fufucode.Plugins.RemoteControl;

/// <summary>
/// “Jiii 连接”插件包入口，被 <see cref="PluginPackageManager"/> 动态实例化。
///
/// 除保留原有远控宿主外，同时把自己注册为协同核心的连接器：
/// 没有核心时（极少数自测/无宿主场景）只跳过注册，不阻断远控本身。
/// </summary>
public sealed class RemoteControlPluginPackage : IPluginPackage
{
    public string Id => "service.remote-control";
    public string Version => "1.1.0";
    private RemoteControlHost? _host;
    private JiiiConnector? _connector;

    public IPluginToolHandler? CreateToolHandler(PluginPackageContext context) => null;

    public Task InitializeAsync(PluginPackageContext context, CancellationToken cancellationToken = default)
    {
        if (_host is not null) return Task.CompletedTask;
        if (PluginPackageManager.ActiveRemoteControlService is not null)
            throw new InvalidOperationException("Jiii 连接服务已有宿主，不能重复初始化。");
        _host = new RemoteControlHost();
        _connector = new JiiiConnector(_host);
        _host.AdapterChanged += _connector.Observe;
        PluginPackageManager.CompanionCore.RegisterConnector(_connector);
        PluginPackageManager.ActiveRemoteControlService = _host;
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        var owned = _host;
        var connector = _connector;
        _host = null;
        _connector = null;
        if (owned is not null && connector is not null)
            owned.AdapterChanged -= connector.Observe;
        if (connector is not null)
        {
            PluginPackageManager.CompanionCore.UnregisterConnector(connector.ConnectorId);
            connector.Dispose();
        }
        if (ReferenceEquals(PluginPackageManager.ActiveRemoteControlService, owned))
            PluginPackageManager.ActiveRemoteControlService = null;
        owned?.Dispose();
        return Task.CompletedTask;
    }
}
