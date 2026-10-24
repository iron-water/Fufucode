using Fufucode.Core.Plugins;

namespace Fufucode.Plugins.FufuBridge;

/// <summary>
/// “Fufu 连接”插件包入口，被 <see cref="PluginPackageManager"/> 动态实例化。
///
/// 除保留原有命名管道桥接外，同时把自己注册为协同核心的连接器；
/// 发布状态事实，但不接收业务事件（管道 v1 没有对应帧）。
/// </summary>
public sealed class FufuBridgePackage : IPluginPackage
{
    public const string PluginId = "service.fufu-bridge";

    public string Id => PluginId;
    public string Version => "1.1.0";

    private FufuBridgeHost? _host;
    private FufuConnector? _connector;
    private System.Threading.Timer? _presenceTimer;

    public IPluginToolHandler? CreateToolHandler(PluginPackageContext context) => null;

    public Task InitializeAsync(PluginPackageContext context, CancellationToken cancellationToken = default)
    {
        if (_host is not null) return Task.CompletedTask;
        if (PluginPackageManager.ActiveFufuBridge is not null)
            throw new InvalidOperationException("Fufu 连接服务已有宿主，不能重复初始化。");
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
        _connector = new FufuConnector(_host);
        PluginPackageManager.CompanionCore.RegisterConnector(_connector);
        _connector.ObserveConnection();
        _presenceTimer = new System.Threading.Timer(
            _ => _connector?.ObserveConnection(),
            null,
            dueTime: TimeSpan.FromSeconds(2),
            period: TimeSpan.FromSeconds(2));
        PluginPackageManager.ActiveFufuBridge = _host;
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        var owned = _host;
        var connector = _connector;
        var timer = _presenceTimer;
        _host = null;
        _connector = null;
        _presenceTimer = null;
        timer?.Dispose();
        if (connector is not null)
        {
            PluginPackageManager.CompanionCore.UnregisterConnector(connector.ConnectorId);
            connector.Dispose();
        }
        if (ReferenceEquals(PluginPackageManager.ActiveFufuBridge, owned))
            PluginPackageManager.ActiveFufuBridge = null;
        owned?.Dispose();
        return Task.CompletedTask;
    }
}
