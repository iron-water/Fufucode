using System.Threading;
using System.Threading.Tasks;
using Fufucode.App.Features.RemoteControl;
using Fufucode.Core.Plugins;

namespace Fufucode.Plugins.RemoteControl;

/// <summary>
/// 手机远程控制插件包入口，被 <see cref="PluginPackageManager"/> 动态实例化。
/// </summary>
public sealed class RemoteControlPluginPackage : IPluginPackage
{
    public string Id => "service.remote-control";
    public string Version => "1.0.0";
    private RemoteControlHost? _host;

    public IPluginToolHandler? CreateToolHandler(PluginPackageContext context) => null;

    public Task InitializeAsync(PluginPackageContext context, CancellationToken cancellationToken = default)
    {
        if (_host is not null) return Task.CompletedTask;
        if (PluginPackageManager.ActiveRemoteControlService is not null)
            throw new InvalidOperationException("远程控制服务已有宿主，不能重复初始化。");
        _host = new RemoteControlHost();
        PluginPackageManager.ActiveRemoteControlService = _host;
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        var owned = _host;
        _host = null;
        if (ReferenceEquals(PluginPackageManager.ActiveRemoteControlService, owned))
            PluginPackageManager.ActiveRemoteControlService = null;
        owned?.Dispose();
        return Task.CompletedTask;
    }
}
