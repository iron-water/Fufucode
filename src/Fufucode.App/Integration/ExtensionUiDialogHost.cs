using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using Fufucode.App.Abstractions;
using Fufucode.Core;
using Fufucode.Pi;

namespace Fufucode.App.Integration;

/// <summary>
/// 审批/对话框宿主：把 <see cref="IExtensionUiSession"/> 的待处理请求映射到非模态弹窗。
/// 非模态保证审批期间主窗口时间线继续滚动；弹窗处理不等待
/// <c>SendAsync</c> 先完成（扩展命令的 prompt 响应在 handler 完成后才发）。
/// 请求失效（超时/停止/断开）经 <see cref="IExtensionUiSession.ExtensionUiRequestClosed"/> 撤下，
/// 不假设服务端清理本地 UI。
/// </summary>
public sealed class ExtensionUiDialogHost
{
    private readonly Window? _owner;
    private readonly Dictionary<string, ExtensionUiDialogWindow> _open = new(StringComparer.Ordinal);
    private IExtensionUiSession? _session;

    public ExtensionUiDialogHost(Window? owner) => _owner = owner;

    /// <summary>
    /// 测试钩子：返回非 null 时不弹窗、直接以此应答。无头自测用它驱动 A01 允许/拒绝路径。
    /// </summary>
    public Func<ExtensionUiRequest, PiExtensionUiResponse?>? AutoResponder { get; set; }

    /// <summary>经 AutoResponder 应答的次数（测试观察用）。</summary>
    public int AutoAnsweredCount { get; private set; }

    public void Attach(IChatSession chat)
    {
        Detach();
        if (chat is IExtensionUiSession session)
        {
            _session = session;
            _session.ExtensionUiRequestReceived += OnRequest;
            _session.ExtensionUiRequestClosed += OnClosed;
        }
    }

    public void Detach()
    {
        if (_session is not null)
        {
            _session.ExtensionUiRequestReceived -= OnRequest;
            _session.ExtensionUiRequestClosed -= OnClosed;
            _session = null;
        }

        // 会话切换/关闭：Core 会取消待处理请求；这里只撤窗口，不补应答。
        foreach (var window in _open.Values)
        {
            window.Dismiss();
        }

        _open.Clear();
    }

    private void OnRequest(ExtensionUiRequest request)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnRequest(request));
            return;
        }

        if (AutoResponder?.Invoke(request) is { } automatic)
        {
            AutoAnsweredCount++;
            _ = RespondAsync(request, automatic);
            return;
        }

        var window = new ExtensionUiDialogWindow(request, RespondAsync);
        _open[request.Id] = window;
        window.Closed += (_, _) => _open.Remove(request.Id);
        if (_owner is not null)
        {
            window.Show(_owner);
        }
        else
        {
            window.Show();
        }
    }

    private void OnClosed(ExtensionUiRequest request)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnClosed(request));
            return;
        }

        if (_open.Remove(request.Id, out var window))
        {
            window.Dismiss();
        }
    }

    private async Task RespondAsync(ExtensionUiRequest request, PiExtensionUiResponse response)
    {
        var session = _session;
        if (session is null)
        {
            return;
        }

        try
        {
            await session.RespondToExtensionUiAsync(request, response);
        }
        catch (Exception error)
        {
            // 请求已被应答/过期/断连（Core 抛 stale）：如实记录，不重试（一次请求只应答一次）。
            System.Diagnostics.Debug.WriteLine($"Extension UI respond failed: {error.Message}");
        }
    }
}
