using System;
using System.Threading;
using System.Threading.Tasks;
using Fufucode.App.Abstractions;
using Fufucode.Core.RemoteControl;

namespace Fufucode.Core.Plugins;

/// <summary>
/// FUFU 桌宠桥接服务统一接口（service.fufu-bridge 插件）。
/// 形态与 <see cref="IRemoteControlService"/> 相同：宿主负责喂会话与目录回调，
/// 实现（Bridge 插件）作为命名管道客户端把这些状态推给 FUFU 桌面进程。
/// </summary>
public interface IFufuBridge : IDisposable
{
    /// <summary>当前与 FUFU 命名管道的连接状态。</summary>
    bool IsConnected { get; }

    /// <summary>接入当前会话；会话切换/重建时宿主重复调用。</summary>
    void Attach(IChatSession session, string sessionTitle);

    /// <summary>登记会话目录与切换/提交回调（宿主在窗口层实现）。</summary>
    void ConfigureSessionDirectory(
        Func<RemoteSessionCatalog> catalog,
        Func<string, CancellationToken, Task<RemoteCommandResult>> switchSession,
        Func<string, string, CancellationToken, Task<RemoteCommandResult>> submitTask);

    /// <summary>把当前会话历史推给桥（重连后补发快照）。</summary>
    void LoadHistory(System.Collections.Generic.IReadOnlyList<ChatMessage> messages) { }
}
