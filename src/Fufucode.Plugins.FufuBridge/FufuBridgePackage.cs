using Fufucode.Core.Plugins;

namespace Fufucode.Plugins.FufuBridge;

/// <summary>FUFU 桌宠桥接插件包入口，被 <see cref="PluginPackageManager"/> 动态实例化。</summary>
public sealed class FufuBridgePackage : IPluginPackage
{
    public const string PluginId = "service.fufu-bridge";

    public string Id => PluginId;
    public string Version => "1.0.0";

    private FufuBridgeHost? _host;

    public IPluginToolHandler? CreateToolHandler(PluginPackageContext context) => null;

    public Task InitializeAsync(PluginPackageContext context, CancellationToken cancellationToken = default)
    {
        if (_host is not null) return Task.CompletedTask;
        if (PluginPackageManager.ActiveFufuBridge is not null)
            throw new InvalidOperationException("FUFU 桥接服务已有宿主，不能重复初始化。");
        try
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(context.DataRoot ?? context.PackageDirectory, "fufu-bridge.log"),
                $"[{DateTime.UtcNow:o}] initialize: dir={context.PackageDirectory}\n");
        }
        catch { }
        var logPath = System.IO.Path.Combine(context.DataRoot ?? context.PackageDirectory, "fufu-bridge.log");
        try
        {
            _host = new FufuBridgeHost(logPath);
        }
        catch (Exception ex)
        {
            try { System.IO.File.AppendAllText(logPath, $"[{DateTime.UtcNow:o}] host ctor failed: {ex}\n"); } catch { }
            throw;
        }
        PluginPackageManager.ActiveFufuBridge = _host;
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        var owned = _host;
        _host = null;
        if (ReferenceEquals(PluginPackageManager.ActiveFufuBridge, owned))
            PluginPackageManager.ActiveFufuBridge = null;
        owned?.Dispose();
        return Task.CompletedTask;
    }
}
