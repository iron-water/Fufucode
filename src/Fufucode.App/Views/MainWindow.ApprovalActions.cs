using System;
using System.Threading.Tasks;
using Avalonia.Threading;
using Fufucode.App.Integration;

namespace Fufucode.App;

public partial class MainWindow
{
    /// <summary>审批弹窗宿主：记住项目文件夹，“始终允许”规则按项目隔离。</summary>
    private ExtensionUiDialogHost CreateDialogHost(string projectPath)
    {
        var host = new ExtensionUiDialogHost(this) { ProjectPath = projectPath };
        host.AlwaysAllow = request => ApprovalPolicy.IsAlwaysAllowed(request, host.ProjectPath);
        return host;
    }

    /// <summary>
    /// 第二个实例转发来的激活负载：系统通知按钮点按后，由已运行实例完成应答。
    /// 来自激活事件的是后台线程调用，必须切回 UI 线程。
    /// </summary>
    internal void OnExternalActivation()
    {
        if (Dispatcher.UIThread.CheckAccess()) DrainActivationInbox();
        else Dispatcher.UIThread.Post(DrainActivationInbox);
    }

    private void DrainActivationInbox()
    {
        if (_closed) return;
        foreach (var payload in ActivationInbox.Drain())
        {
            if (ApprovalLink.TryParse(payload, out var action)) _ = HandleApprovalActionAsync(action);
        }
    }

    /// <summary>本进程即由通知按钮启动：直接处理启动链接。</summary>
    private async Task HandleApprovalPayloadAsync(string payload)
    {
        if (!ApprovalLink.TryParse(payload, out var action)) return;
        await HandleApprovalActionAsync(action);
    }

    /// <summary>
    /// 处理一次通知按钮的决定：先落到目标会话，再按请求 Id 应答当前挂起的原始请求。
    /// 请求已过期（超时/已应答/会话已换）时什么也不做——绝不凭链接内容伪造一次审批。
    /// </summary>
    internal async Task<bool> HandleApprovalActionAsync(ApprovalAction action)
    {
        if (_closed) return false;
        if (!Dispatcher.UIThread.CheckAccess())
        {
            return await Dispatcher.UIThread.InvokeAsync(() => HandleApprovalActionAsync(action));
        }
        ShowFromTray();

        // 目标项目与当前不同：先按项目的信任流程切换；切换失败就保持现状，不越权处理。
        if (!string.IsNullOrWhiteSpace(action.ProjectPath) &&
            (_host is null || !RealSessionHost.PathEquals(_host.ProjectPath, action.ProjectPath)))
        {
            try { await SwitchProjectAsync(action.ProjectPath); }
            catch (Exception) { return false; }
        }
        if (_host is { } host && !string.Equals(host.Current.Session.Id, action.SessionId, StringComparison.Ordinal))
        {
            var switched = await SwitchSessionFromRemoteAsync(action.SessionId, default);
            if (!switched.Accepted) return false;
        }

        if (_dialogHost is { } main && await main.RespondByIdAsync(action.RequestId, action.Decision)) return true;
        if (_sideDialogHost is { } side && await side.RespondByIdAsync(action.RequestId, action.Decision)) return true;
        return false;
    }
}
