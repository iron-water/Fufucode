using System;
using System.Threading.Tasks;
using Fufucode.App.Abstractions;

namespace Fufucode.Core.Plugins;

/// <summary>
/// 手机局域网远程控制服务统一接口（service.remote-control 插件）。
/// 核心外壳与 ViewModel 仅依赖此抽象接口，不硬编码 HTTP/WebSocket 监听器。
/// </summary>
public interface IRemoteControlService : IDisposable
{
    bool IsRunning { get; }
    string? PairingQrSvg { get; }
    Task<(int Port, string Pin, string PairingUrl)> StartAsync(int port = 0);
    void Stop();
    void Attach(IChatSession session, string sessionTitle);
    /// <summary>登记桌面会话目录与切换动作；实现可暂不支持则保持默认空操作。</summary>
    void ConfigureSessionDirectory(
        Func<Fufucode.Core.RemoteControl.RemoteSessionCatalog> catalog,
        Func<string, System.Threading.CancellationToken, Task<Fufucode.Core.RemoteControl.RemoteCommandResult>> switchSession)
    {
    }
    void LoadHistory(System.Collections.Generic.IReadOnlyList<ChatMessage> messages) { }
}
