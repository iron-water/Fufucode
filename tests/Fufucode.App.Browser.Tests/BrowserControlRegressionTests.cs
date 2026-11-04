using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using Avalonia.Threading;
using Fufucode.App.Features.BrowserExperience;
using Fufucode.App.Features.ExternalBrowser;
using Fufucode.App.Infrastructure;
using Fufucode.App.Views;
using Fufucode.Core.Backend;
using Fufucode.Core.Plugins;
using Fufucode.Plugins.BrowserUse;

namespace Fufucode.App.Browser.Tests;

/// <summary>由 Program 的 Headless 初始化后调用 Run(Check)；不创建数据目录，不监听端口。</summary>
internal static class BrowserControlRegressionTests
{
    public static void Run(Action<bool, string> check)
    {
        check(!BrowserSettings.Default.BrowserUseEnabled && !BrowserSettings.Default.ExternalBrowserEnabled &&
            !BrowserSettings.Default.EnableFullCdpAccess, "浏览器控制与完整 CDP 默认关闭");
        check(BrowserUseBridge.NormalizeScriptResult(JsonSerializer.Serialize("{\"found\":true}")) == "{\"found\":true}",
            "结构化解开 WebView2 返回的 JSON 外层字符串");
        check(BrowserUseBridge.IsErrorResult(JsonSerializer.Serialize("{\"error\":\"missing\"}")) &&
            !BrowserUseBridge.IsErrorResult("{\"text\":\"error\",\"html\":\"<div>error</div>\"}"),
            "错误只按 JSON 结果字段判定，不误伤网页内容");

        BrowserUseBridge? current = null;
        var package = new BrowserUsePluginPackage();
        var plugin = (BrowserUsePlugin)package.CreateToolHandler(new PluginPackageContext("agent.browser-use", "unused", null,
            type => type == typeof(BrowserUseBridge) ? current : null))!;
        check(!Wait(plugin.ExecuteAsync(new PureToolCall("late", BrowserUsePlugin.GetStateTool, "{}"))).Success,
            "无视图时工具明确拒绝");
        current = new FakeBridge("first");
        check(Wait(plugin.ExecuteAsync(new PureToolCall("late", BrowserUsePlugin.GetStateTool, "{}"))).Content.Contains("first"),
            "插件创建后晚绑定的真实桥立即生效");
        current = new FakeBridge("second");
        check(Wait(plugin.ExecuteAsync(new PureToolCall("late", BrowserUsePlugin.GetStateTool, "{}"))).Content.Contains("second"),
            "替换浏览器桥后不使用旧视图");
        current = null;
        check(!Wait(plugin.ExecuteAsync(new PureToolCall("late", BrowserUsePlugin.GetStateTool, "{}"))).Success,
            "桥移除后不会继续调用上一个视图");
        current = new FakeBridge("error");
        check(!Wait(plugin.ExecuteAsync(new PureToolCall("evaluate", BrowserUsePlugin.EvaluateTool, "{\"script\":\"1\"}"))).Success,
            "evaluate 的宿主拒绝不会被包装成成功");

        var gate = new BrowserSettingsGate(false, true);
        var disabled = new WebView2BrowserUseBridge(() => throw new Exception("关闭控制时不应访问视图"), () => gate);
        check(BrowserUseBridge.IsErrorResult(Wait(disabled.CallCdpAsync("Runtime.evaluate", "{}"))!) &&
            BrowserUseBridge.IsErrorResult(Wait(disabled.EvaluateAsync("1"))!), "完整 CDP 开关不能绕过控制总开关");

        var settings = BrowserSettings.Default with { BrowserUseEnabled = true, DefaultBrowsePermission = "approval" };
        var view = new BrowserView();
        view.ViewModel.ActiveTab.ApplyNavigationState(false, false, false, "https://control.example/");
        view.ApplyAgentControl(settings, null, null);
        var bridge = new WebView2BrowserUseBridge(() => view, () => new BrowserSettingsGate(settings.BrowserUseEnabled, settings.EnableFullCdpAccess));
        var approvals = 0;
        bridge.ApprovalHandler = (_, _) => { approvals++; return Task.FromResult(false); };
        Wait(bridge.GetDomAsync(null, 200, 500));
        Wait(bridge.GetStateAsync());
        check(approvals == 2, "DOM 与状态观察都进入站点审批");
        bridge.ApprovalHandler = (_, _) =>
        {
            approvals++;
            settings = settings with { DefaultBrowsePermission = "block" };
            view.ApplyAgentControl(settings, null, null);
            return Task.FromResult(true);
        };
        check(Wait(bridge.GetDomAsync(null, 200, 500)).Contains("禁止访问"), "等待批准中改成 block 后仍拒绝执行");
        var count = approvals;
        Wait(bridge.GetDomAsync(null, 200, 500));
        check(approvals == count, "既有批准不能先于最新站点 block 放行");
        settings = settings with { DefaultBrowsePermission = "approval" };
        view.ApplyAgentControl(settings, null, null);
        var late = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        bridge.ApprovalHandler = (_, _) => late.Task; // 故意不合作，忽略传入的 token。
        using (var cancellation = new CancellationTokenSource())
        {
            var pending = bridge.GetStateAsync(cancellation.Token);
            cancellation.Cancel();
            late.TrySetResult(true);
            check(IsCanceled(pending), "取消后迟到批准不能继续执行（handler 忽略 token 也安全）");
        }

        var externalSettings = settings with { ExternalBrowserEnabled = true };
        var server = ConnectedExternal(() => externalSettings);
        try
        {
            server.ApprovalHandler = (_, _) => { approvals++; return Task.FromResult(false); };
            count = approvals;
            Wait(server.ExecuteAsync(ExternalBrowserActions.State, "{}"));
            Wait(server.ExecuteAsync(ExternalBrowserActions.Tabs, "{}"));
            check(approvals == count + 2, "外部状态和页签观察同样要求站点审批");
            externalSettings = externalSettings with { EnableFullCdpAccess = false };
            count = approvals;
            var cdpDenied = ErrorMessage(Wait(server.ExecuteAsync(ExternalBrowserActions.Evaluate, "{\"script\":\"1\"}")));
            check(cdpDenied.Contains("完整 CDP") && approvals == count,
                "外部 evaluate 必须先开启完整 CDP：" + cdpDenied);
            server.ApprovalHandler = (_, _) =>
            {
                externalSettings = externalSettings with { DefaultBrowsePermission = "block" };
                return Task.FromResult(true);
            };
            check(ErrorMessage(Wait(server.ExecuteAsync(ExternalBrowserActions.GetDom, "{}"))).Contains("禁止访问"),
                "外部审批之后重查站点 block");
            externalSettings = externalSettings with { DefaultBrowsePermission = "approval" };
            var semaphore = (SemaphoreSlim)Field("_executeGate").GetValue(server)!;
            semaphore.Wait();
            var queued = server.ExecuteAsync(ExternalBrowserActions.State, "{}");
            externalSettings = externalSettings with { ExternalBrowserEnabled = false };
            semaphore.Release();
            check(ErrorMessage(Wait(queued)).Contains("已关闭"), "外部动作排队后重查最新控制开关");
            externalSettings = externalSettings with { ExternalBrowserEnabled = true };
            late = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            server.ApprovalHandler = (_, _) => late.Task;
            using var cancellation = new CancellationTokenSource();
            var pending = server.ExecuteAsync(ExternalBrowserActions.State, "{}", cancellation.Token);
            cancellation.Cancel();
            late.TrySetResult(true);
            check(IsCanceled(pending), "外部不合作审批 handler 也能及时取消");
        }
        finally { Wait(server.DisposeAsync().AsTask()); }
    }

    // 仅模拟已握手的连接元数据；TcpListener 从未 Start，不产生监听/网络/用户数据副作用。
    private static ExternalBrowserBridgeServer ConnectedExternal(Func<BrowserSettings> settings)
    {
        var server = new ExternalBrowserBridgeServer(settings, "test-token", "1234");
        Field("_listener").SetValue(server, new TcpListener(IPAddress.Loopback, 0));
        Field("_lastSeenUtc").SetValue(server, DateTimeOffset.UtcNow);
        Field("_lastHandshakeUtc").SetValue(server, DateTimeOffset.UtcNow);
        Field("_lastUrl").SetValue(server, "https://control.example/");
        Field("_lastHost").SetValue(server, "control.example");
        return server;
    }

    private static string ErrorMessage(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("error").GetString() ?? "";
    }

    private static FieldInfo Field(string name) => typeof(ExternalBrowserBridgeServer)
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new MissingFieldException(name);

    private static bool IsCanceled(Task task)
    {
        try { Wait(task); return false; }
        catch (OperationCanceledException) { return true; }
    }

    private static void Wait(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!task.IsCompleted && DateTime.UtcNow < deadline) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(1); }
        if (!task.IsCompleted) throw new TimeoutException("浏览器控制回归未按期返回。");
        task.GetAwaiter().GetResult();
    }

    private static T Wait<T>(Task<T> task) { Wait((Task)task); return task.GetAwaiter().GetResult(); }

    private sealed class FakeBridge(string marker) : BrowserUseBridge
    {
        public override Task<string?> CallCdpAsync(string method, string parametersJson, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>("{\"error\":\"denied\"}");
        public override Task<string?> EvaluateAsync(string javaScript, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>("{\"error\":\"denied\"}");
        public override Task<bool> NavigateAsync(string url, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public override Task<string> GetStateAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(JsonSerializer.Serialize(new { marker }));
    }
}
