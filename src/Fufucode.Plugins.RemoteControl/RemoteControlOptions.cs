using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Fufucode.Core.RemoteControl;

/// <summary>手机远控服务的运行状态。</summary>
public enum RemoteControlState
{
    Stopped,
    Running,
}

/// <summary>服务生命周期与状态查询的可序列化快照（IP、端口、连接数、配对信息）。</summary>
public sealed record RemoteControlStatus(
    RemoteControlState State,
    string? Address,
    int Port,
    int ConnectionCount,
    string? PairingUrl,
    string? Pin,
    TimeSpan TokenLifetime,
    DateTimeOffset? StartedAtUtc)
{
    public bool IsRunning => State == RemoteControlState.Running;

    public static RemoteControlStatus Stopped() =>
        new(RemoteControlState.Stopped, null, 0, 0, null, null, TimeSpan.Zero, null);
}

/// <summary>远控服务可配置项。默认监听全部网卡以支持局域网访问。</summary>
public sealed record RemoteControlOptions
{
    /// <summary>监听地址；测试可指定回环，产品默认监听任意网卡。</summary>
    public IPAddress BindAddress { get; init; } = IPAddress.Any;

    /// <summary>配对令牌有效期（短期令牌）。</summary>
    public TimeSpan TokenLifetime { get; init; } = TimeSpan.FromHours(12);

    /// <summary>同一客户端连续错误 PIN 次数上限，超过则锁定。</summary>
    public int MaxPinAttempts { get; init; } = 5;

    /// <summary>锁定/错误尝试计数窗口长度。</summary>
    public TimeSpan PinLockout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>对外公布的地址覆盖项；为 null 时自动探测局域网 IPv4。</summary>
    public string? AdvertisedHost { get; init; }
}

/// <summary>局域网地址探测：为配对 URL 提供可被手机访问的主机地址。</summary>
public static class RemoteControlNetwork
{
    /// <summary>返回第一个可用的局域网 IPv4 地址；找不到时返回 127.0.0.1。</summary>
    public static string GetLanIPv4Address()
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var bytes = unicast.Address.GetAddressBytes();
                    if (bytes[0] == 169 && bytes[1] == 254) continue; // APIPA 自动私有地址不可路由
                    return unicast.Address.ToString();
                }
            }
        }
        catch (NetworkInformationException)
        {
            // 探测失败时退回回环地址，不阻断服务启动。
        }

        return IPAddress.Loopback.ToString();
    }

    /// <summary>根据实际监听端点选择对外地址。</summary>
    public static string ResolveAdvertisedHost(RemoteControlOptions options, IPEndPoint endpoint)
    {
        if (!string.IsNullOrWhiteSpace(options.AdvertisedHost)) return options.AdvertisedHost!.Trim();
        if (!endpoint.Address.Equals(IPAddress.Any) && !endpoint.Address.Equals(IPAddress.IPv6Any))
            return endpoint.Address.ToString();
        return GetLanIPv4Address();
    }
}
