using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Fufucode.App.Features.BrowserExperience;
using Fufucode.App.Infrastructure;
using Fufucode.App.Infrastructure.Browser;
using Fufucode.App.ViewModels;
using Fufucode.App.Views;
using Fufucode.Plugins.BrowserUse;

namespace Fufucode.App.Browser.Tests;

/// <summary>
/// BRW-1 定向测试：浏览器设置原子落盘、损坏/新版文件降级、地址栏安全态、
/// 白名单解析、以及 Browser Use 桥的门禁与真实拒绝语义。全部使用临时真实目录与真实文件。
/// </summary>
internal static class BrowserSettingsTests
{
    public static void Run(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "fufucode-brw1-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            AtomicPersistence(check, root);
            CorruptAndFutureVersion(check, root);
            SecurityState(check);
            SiteListParsing(check);
            BridgeGating(check, root);
            RegistryEndToEnd(check, root);
            ClearScopeSafety(check, root);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private static void AtomicPersistence(Action<bool, string> check, string root)
    {
        var dir = Path.Combine(root, "atomic");
        var store = new BrowserSettingsStore(dir);
        check(File.Exists(store.Path) is false, "初始不存在 settings 文件时不预写空文件");

        var saved = store.Save(store.Current with
        {
            ShowFullUrl = false,
            ScreenshotMode = "always",
            AllowedSites = new[] { new BrowserSitePermission("Example.com", "allow") },
        });
        check(saved.ShowFullUrl is false && saved.ScreenshotMode == "always", "保存返回真实生效值");
        check(File.Exists(store.Path), "保存后文件真实落盘");

        var json = File.ReadAllText(store.Path);
        using var doc = JsonDocument.Parse(json);
        check(doc.RootElement.GetProperty("showFullUrl").GetBoolean() is false, "落盘内容与内存一致（camelCase）");

        var reloaded = new BrowserSettingsStore(dir);
        check(reloaded.Current.ShowFullUrl is false && reloaded.Current.AllowedSites.Count == 1 &&
              reloaded.Current.AllowedSites[0].Host == "example.com",
            "重开后可读回同一份设置（站点主机名已规范化）");

        reloaded.Save(reloaded.Current with { ScreenshotMode = "bogus", WebUrlOpenTarget = "ftp" });
        check(reloaded.Current.ScreenshotMode == "ask" && reloaded.Current.WebUrlOpenTarget == "builtin",
            "非法枚举值收敛到默认值，不写入非法状态");
    }

    private static void CorruptAndFutureVersion(Action<bool, string> check, string root)
    {
        var corruptDir = Path.Combine(root, "corrupt");
        Directory.CreateDirectory(corruptDir);
        var corruptPath = Path.Combine(corruptDir, "browser-settings.json");
        File.WriteAllText(corruptPath, "{ this is not json");
        var corrupt = new BrowserSettingsStore(corruptDir);
        check(corrupt.Current == BrowserSettings.Default && corrupt.LastLoadError is not null,
            "损坏文件回退默认值并如实报告错误");
        check(File.ReadAllText(corruptPath) == "{ this is not json", "损坏文件不被静默覆盖");

        var futureDir = Path.Combine(root, "future");
        Directory.CreateDirectory(futureDir);
        var futurePath = Path.Combine(futureDir, "browser-settings.json");
        File.WriteAllText(futurePath, "{\"version\":99,\"showFullUrl\":false}");
        var future = new BrowserSettingsStore(futureDir);
        check(future.LastLoadError is not null && File.ReadAllText(futurePath).Contains("99"),
            "新版写入的文件保留原文件并报告，不覆盖");
    }

    private static void SecurityState(Action<bool, string> check)
    {
        var tab = new BrowserTab("t");
        check(tab.Security == BrowserSecurityState.None && !tab.ShowSecurity, "新页签没有安全态，不显示锁图标");

        tab.ApplyNavigationState(false, false, false, "https://secure.example/");
        check(tab.Security == BrowserSecurityState.Secure && tab.IsSecure && tab.ShowSecurity, "HTTPS 显示安全锁");

        tab.ApplyNavigationState(false, false, false, "http://plain.example/");
        check(tab.Security == BrowserSecurityState.Insecure && tab.IsInsecure, "HTTP 标记为不安全");

        tab.ApplyNavigationState(false, false, false, "about:blank");
        check(tab.Security == BrowserSecurityState.None, "about:blank 不显示安全态");

        tab.ApplyShowFullUrl(true);
        tab.ApplyNavigationState(false, false, false, "https://host.example/deep/path?q=1");
        check(tab.DisplayAddress == "https://host.example/deep/path?q=1", "显示完整网址时保留协议与路径");
        tab.ApplyShowFullUrl(false);
        check(tab.DisplayAddress == "host.example", "关闭完整网址后只显示主机名");
    }

    private static void SiteListParsing(Action<bool, string> check)
    {
        var parsed = BrowserSettingsViewModel.ParseSites(
            "example.com = allow\nbad host\nhttps://nope.example/\nplain.example\nexample.com = block\n");
        check(parsed.Count == 2, "空白/非法主机名被过滤，重复主机名只保留首个");
        check(parsed[0].Host == "example.com" && parsed[0].Permission == "allow", "首个 example.com = allow 生效");
        check(parsed[1].Host == "plain.example" && parsed[1].Permission == "approval", "未写权限时默认 approval");

        var formatted = BrowserSettingsViewModel.FormatSites(parsed);
        var roundTrip = BrowserSettingsViewModel.ParseSites(formatted);
        check(roundTrip.Count == parsed.Count && roundTrip[0].Host == parsed[0].Host &&
              roundTrip[0].Permission == parsed[0].Permission,
            "格式化与解析往返一致");
    }

    private static void BridgeGating(Action<bool, string> check, string root)
    {
        var view = new BrowserView();
        try
        {
            check(view.BridgeForTesting is null, "未应用设置时没有 Browser Use 桥");

            var dir = Path.Combine(root, "bridge");
            var store = new BrowserSettingsStore(dir);
            var vm = new BrowserSettingsViewModel();
            vm.Attach(dir);

            view.ApplyAgentControl(store.Current with { BrowserUseEnabled = false }, store, vm);
            check(view.BridgeForTesting is not null, "应用设置后创建真实桥");
            check(view.AgentGateForTesting.AgentControlAllowed is false, "主开关关闭时门禁为拒绝");
            var denied = view.BridgeForTesting!.EvaluateAsync("1+1").GetAwaiter().GetResult();
            check(denied is not null && denied.Contains("\"error\"", StringComparison.Ordinal),
                "主开关关闭时 browser_evaluate 被如实拒绝，不返回伪造结果");
            check(!view.BridgeForTesting.NavigateAsync("https://example.com/").GetAwaiter().GetResult(),
                "主开关关闭时 browser_navigate 返回 false");

            view.ApplyAgentControl(store.Current with { BrowserUseEnabled = true, EnableFullCdpAccess = false }, store, vm);
            check(view.AgentGateForTesting.AgentControlAllowed && !view.AgentGateForTesting.FullCdpAllowed,
                "开启 Agent 控制但关闭完整 CDP 时门禁区分生效");
            var cdp = view.BridgeForTesting!.CallCdpAsync("Runtime.evaluate", "{\"expression\":\"1\"}").GetAwaiter().GetResult();
            check(cdp is not null && cdp.Contains("完整 CDP 访问权限未开启", StringComparison.Ordinal),
                "完整 CDP 关闭时 browser_cdp 给出可读拒绝原因");

            check(view.BridgeForTesting.NavigateAsync("javascript:alert(1)").GetAwaiter().GetResult() is false,
                "被白名单拒绝的地址不会被 Agent 导航");
        }
        finally { view.Dispose(); }
    }

    private static void ClearScopeSafety(Action<bool, string> check, string root)
    {
        var dataRootClear = Path.Combine(root, "clear");
        var webviewClear = Path.Combine(dataRootClear, "webview");
        var cacheClear = Path.Combine(webviewClear, "EBWebView", "Default", "Cache");
        var cookiesClear = Path.Combine(webviewClear, "EBWebView", "Default", "Network");
        Directory.CreateDirectory(cacheClear);
        Directory.CreateDirectory(cookiesClear);
        File.WriteAllText(Path.Combine(cacheClear, "data.bin"), "x");
        File.WriteAllText(Path.Combine(cookiesClear, "Cookies"), "y");
        var crashpadClear = Path.Combine(webviewClear, "EBWebView", "Crashpad");
        Directory.CreateDirectory(crashpadClear);

        var cacheReport = BrowserUserDataLayout.ClearBrowsingData(dataRootClear, BrowserDataScope.Cache);
        check(cacheReport.Success && cacheReport.Removed.Count >= 1 && !Directory.Exists(cacheClear),
            "按范围清除缓存真实删除缓存目录");
        check(File.Exists(Path.Combine(cookiesClear, "Cookies")), "清除缓存不误删 Cookie");
        check(Directory.Exists(crashpadClear), "清除缓存保留 Crashpad（崩溃报告）");

        var cookieReport = BrowserUserDataLayout.ClearBrowsingData(dataRootClear, BrowserDataScope.Cookies);
        check(cookieReport.Success && !File.Exists(Path.Combine(cookiesClear, "Cookies")),
            "按范围清除 Cookie 真实删除 Cookie 文件");
    }

    private static void RegistryEndToEnd(Action<bool, string> check, string root)
    {
        // ── 端到端：插件注册表 + 真实桥 + 门禁 ──
        var pluginRoot = Path.Combine(root, "registry");
        Directory.CreateDirectory(pluginRoot);
        var layout = new Fufucode.Core.AppDataLayout(pluginRoot);
        var store = new Fufucode.Core.Plugins.PluginStateStore(layout);
        var gateway = new Fufucode.Core.Plugins.PluginCapabilityGateway(store);
        var registry = new Fufucode.Core.Plugins.PluginToolRegistry(gateway,
            new Fufucode.Core.Backend.ProductionToolDispatcher(pluginRoot));
        var plugin = new BrowserUsePlugin();
        registry.Register(plugin);

        check(registry.ActiveToolNames.Count == 0, "Browser Use 未安装时真实调度表不暴露任何浏览器工具");

        store.Install("agent.browser-use");
        // GetState 只把“有真实载荷”的条目录为已安装：这里用真实 manifest 文件提供载荷，不绕过安装语义。
        var payload = Path.Combine(layout.PluginsDirectory, "agent.browser-use");
        Directory.CreateDirectory(payload);
        File.WriteAllText(Path.Combine(payload, "manifest.json"), "{\"id\":\"agent.browser-use\"}");
        var active = registry.ActiveToolNames;
        string[] expected =
        [
            BrowserUsePlugin.NavigateTool,
            BrowserUsePlugin.ClickTool,
            BrowserUsePlugin.TypeTool,
            BrowserUsePlugin.ScreenshotTool,
            BrowserUsePlugin.EvaluateTool,
            BrowserUsePlugin.GetStateTool,
        ];
        var missing = expected.Where(name => !active.Contains(name)).ToArray();
        check(missing.Length == 0,
            "安装并启用后暴露 browser.navigate/click/screenshot/evaluate/get_state（缺失：" + string.Join("、", missing) + "）");
        check(registry.ActiveRegistrations.All(r => r.ParametersJsonSchema.Length > 0),
            "五个浏览器动作契约都带参数 Schema");

        // 未接线桥时调用返回"未接线"，不伪造成功
        var unwired = registry.DispatchAsync(new Fufucode.Core.Backend.PureToolCall("u1",
            BrowserUsePlugin.NavigateTool, "{\"url\":\"https://example.com/\"}"))
            .GetAwaiter().GetResult();
        check(!unwired.Success && unwired.Content.Contains("尚未接线", StringComparison.Ordinal),
            "注册表已解锁但桥未接线时如实返回“尚未接线”");

        // 装上真实桥（受用户设置门禁）后，CDP 在未开启完整权限时被拒
        var view = new BrowserView();
        try
        {
            var dir = Path.Combine(pluginRoot, "view");
            var settings = new BrowserSettingsStore(dir);
            var settingsVm = new BrowserSettingsViewModel();
            settingsVm.Attach(dir);
            view.ApplyAgentControl(settings.Current with { BrowserUseEnabled = true, EnableFullCdpAccess = false }, settings, settingsVm);
            plugin.Bridge = view.BridgeForTesting;
            var cdp = registry.DispatchAsync(new Fufucode.Core.Backend.PureToolCall("c1",
                BrowserUsePlugin.CdpTool, "{\"method\":\"Runtime.evaluate\",\"params\":{\"expression\":\"1\"}}"))
                .GetAwaiter().GetResult();
            check(!cdp.Success && cdp.Content.Contains("完整 CDP 访问权限未开启", StringComparison.Ordinal),
                "端到端：完整 CDP 关闭时 browser_cdp 经注册表被拒绝");
        }
        finally { view.Dispose(); }

        store.Uninstall("agent.browser-use");
        check(registry.ActiveToolNames.Count == 0, "卸载后调度表立即撤销全部浏览器工具（无幽灵工具）");
    }
}
