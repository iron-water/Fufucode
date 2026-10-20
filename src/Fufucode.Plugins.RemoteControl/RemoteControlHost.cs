using System;
using System.Threading;
using System.Threading.Tasks;
using Fufucode.App.Abstractions;
using Fufucode.Core.Plugins;
using Fufucode.Core.RemoteControl;

namespace Fufucode.App.Features.RemoteControl;

/// <summary>
/// 手机远控宿主：把当前真实会话接到内置局域网服务，并对外提供
/// 配对信息（IP/端口/4 位 PIN/二维码 SVG）与生命周期。
///
/// 接线约定（由主控 M 统一收编，本模块不修改 MainWindow* 等共享外壳）：
/// <code>
/// await using var host = new RemoteControlHost(options);
/// host.Attach(activeChatSession, "会话标题");
/// var status = await host.StartAsync();          // port = 0 自动分配
/// // 面板显示：status.Pin / status.PairingUrl / host.PairingQrSvg
/// host.Stop();                                    // 或 await host.DisposeAsync()
/// </code>
/// 同一会话只允许一个远控宿主；切换会话时保持监听与配对令牌不变，只替换适配器。
/// </summary>
public sealed class RemoteControlHost : IAsyncDisposable, IDisposable, IRemoteControlService
{
    public static RemoteControlHost Shared { get; } = new();
    private readonly RemoteControlOptions _options;
    private readonly RemoteControlWebAssets _assets = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private RemoteSessionAdapter? _adapter;
    private RemoteControlServer? _server;
    private Func<RemoteSessionCatalog>? _sessionCatalog;
    private Func<string, CancellationToken, Task<RemoteCommandResult>>? _switchSession;
    private bool _disposed;

    public RemoteControlHost(RemoteControlOptions? options = null) =>
        _options = options ?? new RemoteControlOptions();

    /// <summary>服务是否运行中。</summary>
    public bool IsRunning => _server?.IsRunning ?? false;

    /// <summary>当前状态（IP、端口、连接数、PIN、配对 URL）；未启动时为 Stopped。</summary>
    public RemoteControlStatus Status => _server?.Status ?? RemoteControlStatus.Stopped();

    /// <summary>当前已绑定的会话适配器；未绑定时为 null。</summary>
    public RemoteSessionAdapter? Adapter => _adapter;

    /// <summary>服务端真实监听端口；未启动时为 0。</summary>
    public int Port => Status.Port;

    /// <summary>4 位配对码；未启动时为 null。</summary>
    public string? Pin => Status.Pin;

    /// <summary>手机访问地址（配对 URL）；未启动时为 null。</summary>
    public string? PairingUrl => Status.PairingUrl;

    /// <summary>当前配对 URL 的二维码 SVG；无法编码或未启动时为 null（UI 降级为纯文本地址）。</summary>
    public string? PairingQrSvg
    {
        get
        {
            var url = PairingUrl;
            return url is null ? null : RemoteControlQr.ToSvg(url);
        }
    }

    /// <summary>登记桌面会话目录与切换动作；远控服务只调用该动作，不直接操作共享会话。</summary>
    public void ConfigureSessionDirectory(
        Func<RemoteSessionCatalog> catalog,
        Func<string, CancellationToken, Task<RemoteCommandResult>> switchSession)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(switchSession);
        _gate.Wait();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _sessionCatalog = catalog;
            _switchSession = switchSession;
            _server?.ConfigureSessionDirectory(catalog, switchSession);
            _server?.Broadcast(new RemoteSessionsUpdate(catalog()));
        }
        finally { _gate.Release(); }
    }

    /// <summary>把当前会话接到远控服务。重复调用只替换适配器，保留监听与现有配对令牌。</summary>
    public RemoteSessionAdapter Attach(IChatSession session, string title)
    {
        ArgumentNullException.ThrowIfNull(session);
        _gate.Wait();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_adapter is null)
            {
                _adapter = new RemoteSessionAdapter(session, title);
            }
            else
            {
                _adapter.Rebind(session, title);
            }
            if (_server is not null && _sessionCatalog is not null)
                _server.Broadcast(new RemoteSessionsUpdate(_sessionCatalog()));
            return _adapter;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>启动远控服务；<paramref name="port"/> 为 0 时自动分配端口。</summary>
    public async Task<RemoteControlStatus> StartAsync(int port = 0, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var adapter = _adapter ?? throw new InvalidOperationException(
                "启动前必须先 Attach 一个会话；远控宿主不做第二会话写入者。");
            if (_server is { IsRunning: true }) return _server.Status;
            if (_server is not null) await _server.DisposeAsync().ConfigureAwait(false);
            var server = new RemoteControlServer(adapter, _options, _assets,
                sessionCatalog: _sessionCatalog, switchSession: _switchSession);
            _server = server;
            try { return await server.StartAsync(port, cancellationToken).ConfigureAwait(false); }
            catch
            {
                _server = null;
                await server.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>停止服务并撤销全部令牌。</summary>
    public void Stop()
    {
        _gate.Wait();
        try
        {
            var server = _server;
            _server = null;
            server?.Stop();
            if (server is not null) _ = server.DisposeAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    async Task<(int Port, string Pin, string PairingUrl)> IRemoteControlService.StartAsync(int port)
    {
        var status = await StartAsync(port);
        return (status.Port, status.Pin ?? "", status.PairingUrl ?? "");
    }

    void IRemoteControlService.Attach(IChatSession session, string sessionTitle) =>
        Attach(session, sessionTitle);
    void IRemoteControlService.ConfigureSessionDirectory(
        Func<RemoteSessionCatalog> catalog,
        Func<string, CancellationToken, Task<RemoteCommandResult>> switchSession) =>
        ConfigureSessionDirectory(catalog, switchSession);
    public void LoadHistory(System.Collections.Generic.IReadOnlyList<ChatMessage> messages) =>
        _adapter?.LoadHistory(messages);

    string? IRemoteControlService.PairingQrSvg => PairingQrSvg;

    /// <summary>登记一个待手机审批的工具调用（宿主在执行前调用）。</summary>
    public RemoteApprovalRequest? RequestApproval(string toolCallId, string toolName, string argsJson,
        string? summary = null) => _adapter?.RequestApproval(toolCallId, toolName, argsJson, summary);

    /// <summary>等待某个审批被手机端决定。</summary>
    public Task<RemoteApprovalResolution>? WaitForApprovalAsync(string approvalId, CancellationToken cancellationToken = default) =>
        _adapter?.Approvals.WaitAsync(approvalId, cancellationToken);

    /// <summary>向全部已配对手机推送一个增量事件。</summary>
    public void Broadcast(RemoteUpdate update) => _server?.Broadcast(update);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _adapter?.Dispose();
        _adapter = null;
        // 已排队的 Start/Attach/Stop 仍需取得 gate 并观察 disposed，不提前释放信号量。
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
