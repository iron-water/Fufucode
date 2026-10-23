using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using Fufucode.App.Abstractions;
using Fufucode.Core;

namespace Fufucode.App.Integration;

/// <summary>
/// 审批/对话框宿主：把 <see cref="IExtensionUiSession"/> 的待处理请求映射到非模态弹窗。
/// 非模态保证审批期间主窗口时间线继续滚动（计划 §11 A02）；弹窗处理不等待
/// <c>SendAsync</c> 先完成（扩展命令的 prompt 响应在 handler 完成后才发，decisions.md [轨道B]）。
/// 请求失效（超时/停止/断开）经 <see cref="IExtensionUiSession.ExtensionUiRequestClosed"/> 撤下，
/// 不假设服务端清理本地 UI（计划 §8.4）。
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

    /// <summary>弹窗命中“始终允许”规则时直接应答，不再打扰。</summary>
    public Func<ExtensionUiRequest, bool>? AlwaysAllow { get; set; }

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

        if (request.Method == "confirm" && AlwaysAllow?.Invoke(request) == true)
        {
            AutoAnsweredCount++;
            _ = RespondAsync(request, PiExtensionUiResponse.Confirm(true));
            return;
        }

        var window = new ExtensionUiDialogWindow(request, RespondAsync,
            alwaysAllow: request.Method == "confirm" && _projectPath is not null
                ? () => ApprovalPolicy.Remember(request, _projectPath)
                : null);
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

    /// <summary>
    /// 系统通知按钮的应答入口：按请求 Id 找回当前挂起的原始请求再应答。
    /// 找不到（已超时/已应答/已换会话）时返回 false，绝不用链接内容顶替请求身份。
    /// </summary>
    internal async Task<bool> RespondByIdAsync(string requestId, ApprovalDecision decision)
    {
        var session = _session;
        if (session is null || string.IsNullOrEmpty(requestId)) return false;
        var request = session.PendingExtensionUiRequests.FirstOrDefault(item =>
            string.Equals(item.Id, requestId, StringComparison.Ordinal));
        // 系统通知上的三个按钮只对应工具审批；其它对话框请求必须回到页内作答。
        if (request is not { Method: "confirm" }) return false;

        if (decision == ApprovalDecision.AlwaysAllow && !ApprovalPolicy.Remember(request, _projectPath))
            return false; // 规则没写进去就不能宣称“始终允许”；让用户回到页内处理。

        if (_open.Remove(request.Id, out var window)) window.Dismiss();
        await RespondAsync(request, PiExtensionUiResponse.Confirm(decision != ApprovalDecision.Deny));
        return true;
    }

    /// <summary>当前会话对应的项目文件夹；“始终允许”规则按它隔离。</summary>
    public string? ProjectPath { get => _projectPath; set => _projectPath = value; }
    private string? _projectPath;

    /// <summary>按 Id 取当前挂起的请求；找不到返回 null（通知正文展示用）。</summary>
    public ExtensionUiRequest? PendingRequestFor(string requestId) =>
        _session?.PendingExtensionUiRequests.FirstOrDefault(item =>
            string.Equals(item.Id, requestId, StringComparison.Ordinal));

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
