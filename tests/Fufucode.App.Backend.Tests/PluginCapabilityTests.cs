using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Fufucode.Core;
using Fufucode.Core.Backend;
using Fufucode.Core.Plugins;
using Fufucode.Plugins.BrowserUse;
using Fufucode.Plugins.ComputerUse;

namespace Fufucode.App.Backend.Tests;

/// <summary>
/// 插件能力的动态装卸与权限门禁定向测试：
/// 未安装不注入工具、安装启用后解锁、卸载后注销（不留幽灵工具）、
/// 未启用与未安装的判定不同、门禁只影响对应能力。
/// 使用真实临时数据根与真实落盘状态；Win32 截图/输入用可注入替身，
/// 不把无桌面会话误判成插件能力缺失。
/// </summary>
internal static class PluginCapabilityTests
{
    public static async Task RunAsync(Checks checks)
    {
        var root = Path.Combine(Path.GetTempPath(), "fufucode-plugin-capability-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var layout = new AppDataLayout(root);
            var store = new PluginStateStore(layout);
            var gateway = new PluginCapabilityGateway(store);
            var registry = new PluginToolRegistry(gateway);
            var computer = new ComputerUsePlugin(new FakeScreenCapture(), new FakeInput(), Path.Combine(root, "shots"));
            var browser = new BrowserUsePlugin(new FakeBridge());
            registry.Register(computer);
            registry.Register(browser);

            // 预备物理 manifest 载荷，使真实载荷守卫放行
            Directory.CreateDirectory(Path.Combine(layout.PluginsDirectory, "agent.browser-use"));
            File.WriteAllText(Path.Combine(layout.PluginsDirectory, "agent.browser-use", "manifest.json"), "{\"id\":\"agent.browser-use\"}");
            Directory.CreateDirectory(Path.Combine(layout.PluginsDirectory, "agent.computer-use"));
            File.WriteAllText(Path.Combine(layout.PluginsDirectory, "agent.computer-use", "manifest.json"), "{\"id\":\"agent.computer-use\"}");

            await CapabilityMapping(checks);
            await BrowserUseLifecycle(checks, store, gateway, registry, browser);
            await ComputerUseLifecycle(checks, store, gateway, registry, computer, root);
            await UninstallRevokes(checks, store, registry, browser, computer, layout);
            await RegistryIsolation(checks, store, gateway, layout);
            await CheckPhysicalPackageLifecycle(checks, layout, store, gateway, registry);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task CheckPhysicalPackageLifecycle(
        Checks checks, AppDataLayout layout, PluginStateStore store,
        PluginCapabilityGateway gateway, PluginToolRegistry registry)
    {
        PluginPackageManager.RegisterPackageFactory("agent.browser-use", () => new BrowserUsePluginPackage());
        PluginPackageManager.RegisterPackageFactory("agent.computer-use", () => new ComputerUsePluginPackage());

        var testRegistry = new PluginToolRegistry(gateway);
        var packageManager = new PluginPackageManager(layout, testRegistry, store, type =>
        {
            if (type == typeof(BrowserUseBridge)) return new FakeBridge();
            if (type == typeof(IComputerScreenCapture)) return new FakeScreenCapture();
            if (type == typeof(IComputerInput)) return new FakeInput();
            return null;
        });

        // 1. 物理未安装确凿证据：目录不存在，无物理文件，无工具
        checks.Check(!packageManager.HasPhysicalPayload("agent.browser-use"),
            "物理包未安装确凿证据：BrowserUse 插件目录不存在且无 manifest");
        checks.Check(!Directory.Exists(layout.PluginDirectory("agent.browser-use")),
            "物理包未安装确凿证据：磁盘上无 agent.browser-use 物理目录");

        // 2. 真实物理安装：落盘 payload，激活工具
        packageManager.InstallPackage("agent.browser-use");
        checks.Check(packageManager.HasPhysicalPayload("agent.browser-use"),
            "物理安装成功：产生物理载荷目录与 manifest.json");
        checks.Check(testRegistry.ActiveToolNames.Contains(BrowserUsePlugin.CdpTool),
            "物理安装成功：CDP 工具动态注册并激活");

        // 3. 停用：保留物理目录，撤销工具调度表
        packageManager.SetPackageEnabled("agent.browser-use", false);
        checks.Check(packageManager.HasPhysicalPayload("agent.browser-use"),
            "物理停用：物理目录与载荷完好保留");
        checks.Check(!testRegistry.ActiveToolNames.Contains(BrowserUsePlugin.CdpTool),
            "物理停用：工具调度表中已注销 CDP 工具");

        // 4. 重新启用：工具恢复
        packageManager.SetPackageEnabled("agent.browser-use", true);
        checks.Check(testRegistry.ActiveToolNames.Contains(BrowserUsePlugin.CdpTool),
            "重新启用：工具调度表中动态恢复 CDP 工具");

        // 5. 真实物理卸载：物理删除载荷目录，彻底注销工具
        packageManager.UninstallPackage("agent.browser-use");
        checks.Check(!packageManager.HasPhysicalPayload("agent.browser-use"),
            "物理卸载成功：物理载荷已被删除");
        checks.Check(!Directory.Exists(layout.PluginDirectory("agent.browser-use")),
            "物理卸载确凿证据：磁盘物理目录已彻底删除，不留残余");
        checks.Check(!testRegistry.ActiveToolNames.Contains(BrowserUsePlugin.CdpTool),
            "物理卸载成功：工具调度表彻底注销，无幽灵工具");

        // 6. ComputerUse 物理生命周期验证
        checks.Check(!packageManager.HasPhysicalPayload("agent.computer-use"),
            "物理包未安装确凿证据：ComputerUse 插件目录不存在");
        packageManager.InstallPackage("agent.computer-use");
        checks.Check(packageManager.HasPhysicalPayload("agent.computer-use"),
            "ComputerUse 物理安装成功：产生载荷与 manifest");
        checks.Check(testRegistry.ActiveToolNames.Contains(ComputerUsePlugin.ScreenshotTool),
            "ComputerUse 物理安装成功：截图工具动态注册");
        packageManager.UninstallPackage("agent.computer-use");
        checks.Check(!Directory.Exists(layout.PluginDirectory("agent.computer-use")),
            "ComputerUse 物理卸载成功：物理目录已彻底删除");
        checks.Check(!testRegistry.ActiveToolNames.Contains(ComputerUsePlugin.ScreenshotTool),
            "ComputerUse 物理卸载成功：工具彻底注销");

        // 7. AutoDev 物理生命周期与真实工具调度验证
        PluginPackageManager.RegisterPackageFactory("automation.auto-dev", () => new Fufucode.Plugins.AutoDev.AutoDevPluginPackage());
        packageManager.InstallPackage("automation.auto-dev");
        checks.Check(packageManager.HasPhysicalPayload("automation.auto-dev"),
            "AutoDev 物理安装成功：产生载荷与 manifest");
        checks.Check(testRegistry.ActiveToolNames.Contains("autodev_status"),
            "AutoDev 物理安装成功：autodev_status 工具动态注册");
        checks.Check(testRegistry.ActiveToolNames.Contains("autodev_advance"),
            "AutoDev 物理安装成功：autodev_advance 工具动态注册");
        checks.Check(testRegistry.ActiveToolNames.Contains("autodev_plan"),
            "AutoDev 物理安装成功：autodev_plan 工具动态注册");

        var statusCall = new PureToolCall("call_autodev_1", "autodev_status", "{}");
        var statusResult = await testRegistry.DispatchAsync(statusCall);
        checks.Check(statusResult.Success, "AutoDev 工具执行成功");
        checks.Check(statusResult.Content.Contains("AutoDevelopment"), "AutoDev 状态输出包含能力标识");

        packageManager.UninstallPackage("automation.auto-dev");
        checks.Check(!Directory.Exists(layout.PluginDirectory("automation.auto-dev")),
            "AutoDev 物理卸载成功：物理目录已彻底删除");
        checks.Check(!testRegistry.ActiveToolNames.Contains("autodev_status"),
            "AutoDev 物理卸载成功：工具彻底注销");

        // 8. AeroSkin 插件包物理生命周期与皮肤贡献验证
        PluginPackageManager.RegisterPackageFactory("skin.aero", () => new Fufucode.Plugins.AeroSkin.AeroSkinPluginPackage());
        packageManager.InstallPackage("skin.aero");
        checks.Check(packageManager.HasPhysicalPayload("skin.aero"),
            "AeroSkin 物理安装成功：产生载荷与 manifest");
        var aeroPkg = new Fufucode.Plugins.AeroSkin.AeroSkinPluginPackage();
        var skinContrib = aeroPkg.CreateSkinContribution(new PluginPackageContext("skin.aero", layout.PluginDirectory("skin.aero"), null));
        checks.Check(skinContrib is not null, "AeroSkin 插件提供真实皮肤贡献");
        checks.Equal("aero", skinContrib!.StorageKey, "Aero 皮肤键为 aero");
        checks.Check(skinContrib.TokenDictionaryUri?.Contains("AeroTokens") == true, "Aero 包含真实 TokenDictionaryUri");

        packageManager.UninstallPackage("skin.aero");
        checks.Check(!Directory.Exists(layout.PluginDirectory("skin.aero")),
            "AeroSkin 物理卸载成功：物理目录已彻底删除");

        await packageManager.DisposeAsync();
    }

    private static Task CapabilityMapping(Checks checks)
    {
        checks.Equal("agent.browser-use", PluginCapabilityRegistry.ProviderId(PluginCapability.BrowserControl),
            "浏览器控制能力解析到 browser-use 插件");
        checks.Equal("agent.computer-use", PluginCapabilityRegistry.ProviderId(PluginCapability.ComputerControl),
            "电脑操控能力解析到 computer-use 插件");
        checks.Equal("automation.auto-dev", PluginCapabilityRegistry.ProviderId(PluginCapability.AutoDevelopment),
            "自动开发能力解析到 auto-dev 插件");
        checks.Check(PluginCapabilityRegistry.Provider(PluginCapability.BrowserControl) is not null &&
                     PluginCapabilityRegistry.Provider(PluginCapability.ComputerControl) is not null,
            "能力提供者都能解析到真实插件定义");
        checks.Check(PluginCapabilityRegistry.CapabilitiesOf("agent.browser-use")
                .SequenceEqual(new[] { PluginCapability.BrowserControl }) &&
            PluginCapabilityRegistry.CapabilitiesOf("core.csharp").Count == 0,
            "插件到能力是双向一致的映射");
        return Task.CompletedTask;
    }

    private static async Task BrowserUseLifecycle(Checks checks, PluginStateStore store,
        PluginCapabilityGateway gateway, PluginToolRegistry registry, BrowserUsePlugin browser)
    {
        // 未安装：不注入工具，且原因可读。
        var before = gateway.Status(PluginCapability.BrowserControl);
        checks.Check(!before.IsAvailable && !before.IsInstalled, "Browser Use 未安装时能力不可用");
        checks.Check(before.Reason.Contains("未安装", StringComparison.Ordinal), "未安装原因包含“未安装”");
        checks.Check(!registry.ActiveToolNames.Contains(BrowserUsePlugin.CdpTool),
            "未安装时 CDP 工具不进入工具调度表");
        checks.Check(registry.ActiveToolNames.Count == 0, "两个能力插件都未安装时调度表为空");

        var cdpCall = new PureToolCall("c1", BrowserUsePlugin.CdpTool, "{\"method\":\"Runtime.evaluate\"}");
        var blocked = await registry.DispatchAsync(cdpCall);
        checks.Check(!blocked.Success, "未安装时直接调用 CDP 被拒绝");
        checks.Check(blocked.Content.Contains("未安装", StringComparison.Ordinal),
            "未安装时拒绝原因指向未安装，而不是泛化失败");

        // 安装 + 启用：解锁 CDP 接口与 Browser Use 动作契约。
        store.Install("agent.browser-use");
        var installed = gateway.Status(PluginCapability.BrowserControl);
        checks.Check(installed.IsAvailable && installed.IsInstalled && installed.IsEnabled,
            "安装后 Browser Use 能力解锁");

        var active = registry.ActiveToolNames;
        checks.Check(active.Contains(BrowserUsePlugin.CdpTool) &&
                     active.Contains(BrowserUsePlugin.EvaluateTool) &&
                     active.Contains(BrowserUsePlugin.NavigateTool),
            "安装后 CDP / evaluate / navigate 动作契约全部注册");
        checks.Check(registry.ActiveRegistrations.All(r => r.ParametersJsonSchema.Length > 0),
            "注册的动作契约都带有参数 Schema");

        var executed = await registry.DispatchAsync(cdpCall);
        checks.Check(executed.Success && executed.Content.Contains("Runtime.evaluate", StringComparison.Ordinal),
            "安装后 CDP 调用转发到真实宿主桥并返回结果");

        // 停用（已安装但未启用）与未安装必须区分。
        store.SetEnabled("agent.browser-use", false);
        var disabled = gateway.Status(PluginCapability.BrowserControl);
        checks.Check(disabled.IsInstalled && !disabled.IsEnabled && !disabled.IsAvailable,
            "停用后为已安装未启用且能力不可用");
        checks.Check(disabled.Reason.Contains("停用", StringComparison.Ordinal), "停用原因不为“未安装”");
        checks.Check(!registry.ActiveToolNames.Contains(BrowserUsePlugin.CdpTool), "停用后 CDP 工具从调度表撤销");
        var rejected = await registry.DispatchAsync(cdpCall);
        checks.Check(!rejected.Success, "停用后 CDP 调用被拒绝");
        store.SetEnabled("agent.browser-use", true);
        checks.Check(registry.ActiveToolNames.Contains(BrowserUsePlugin.CdpTool), "重新启用后 CDP 工具恢复");
    }

    private static async Task ComputerUseLifecycle(Checks checks, PluginStateStore store,
        PluginCapabilityGateway gateway, PluginToolRegistry registry, ComputerUsePlugin computer, string root)
    {
        checks.Check(!gateway.IsAvailable(PluginCapability.ComputerControl), "Computer Use 未安装时能力不可用");
        checks.Check(!registry.ActiveToolNames.Contains(ComputerUsePlugin.ScreenshotTool),
            "未安装时屏幕截图工具不注入");

        store.Install("agent.computer-use");
        checks.Check(gateway.IsAvailable(PluginCapability.ComputerControl), "安装后电脑操控能力解锁");
        var active = registry.ActiveToolNames;
        checks.Check(active.Contains(ComputerUsePlugin.ScreenshotTool) &&
                     active.Contains(ComputerUsePlugin.MouseTool) &&
                     active.Contains(ComputerUsePlugin.KeyboardTool),
            "安装后截图/鼠标/键盘工具全部注册");

        // 截图经可注入替身执行：验证参数解析、真实落盘与尺寸回填。
        var shot = await registry.DispatchAsync(new PureToolCall("s1", ComputerUsePlugin.ScreenshotTool,
            "{\"deliveryMode\":\"foreground\"}"));
        checks.Check(shot.Success && shot.Content.Contains("屏幕截图成功", StringComparison.Ordinal), "截图工具经替身成功执行");
        var files = Directory.Exists(Path.Combine(root, "shots"))
            ? Directory.GetFiles(Path.Combine(root, "shots"), "*.png") : Array.Empty<string>();
        checks.Check(files.Length == 1, "截图按真实路径落盘到输出目录");

        var region = await registry.DispatchAsync(new PureToolCall("s2", ComputerUsePlugin.ScreenshotTool,
            "{\"left\":10,\"top\":20,\"width\":200,\"height\":100,\"deliveryMode\":\"foreground\"}"));
        checks.Check(region.Success, "带区域的截图同样成功");

        // 参数校验：错误参数不得被当作成功。
        var bad = await registry.DispatchAsync(new PureToolCall("s3", ComputerUsePlugin.MouseTool, "{\"action\":\"move\"}"));
        checks.Check(!bad.Success && bad.Content.Contains("参数错误", StringComparison.Ordinal),
            "缺少坐标的鼠标动作按参数错误拒绝");

        // 输入动作默认后台；没有窗口目标时按设计拒绝。替身验证走显式 foreground。
        var click = await registry.DispatchAsync(new PureToolCall("s4", ComputerUsePlugin.MouseTool,
            "{\"action\":\"click\",\"x\":12,\"y\":34,\"deliveryMode\":\"foreground\"}"));
        checks.Check(click.Success, "合法鼠标点击执行成功");

        var backgroundClick = await registry.DispatchAsync(new PureToolCall("s4b", ComputerUsePlugin.MouseTool,
            "{\"action\":\"click\",\"x\":12,\"y\":34}"));
        checks.Check(!backgroundClick.Success && backgroundClick.Content.Contains("后台鼠标动作需要", StringComparison.Ordinal),
            "后台鼠标没有窗口目标时拒绝，不触碰真实鼠标");

        var hotkey = await registry.DispatchAsync(new PureToolCall("s5", ComputerUsePlugin.KeyboardTool,
            "{\"action\":\"hotkey\",\"keys\":[\"Ctrl\",\"S\"],\"deliveryMode\":\"foreground\"}"));
        checks.Check(hotkey.Success, "组合键执行成功");

        var capabilities = await registry.DispatchAsync(new PureToolCall("s6", ComputerUsePlugin.CapabilitiesTool, "{}"));
        checks.Check(capabilities.Success && capabilities.Content.Contains("后台", StringComparison.Ordinal),
            "能力查询报告后台与前台通道");

        // 注入式后台后端：验证窗口身份和 deliveryMode 会沿调用链传递，
        // 不需要启动真实桌面或移动用户鼠标。
        var backgroundBackend = new FakeComputerBackend();
        var routed = new ComputerUsePlugin(backends: new[] { backgroundBackend });
        var routedResult = await routed.ExecuteAsync(new PureToolCall("s7", ComputerUsePlugin.MouseTool,
            "{\"action\":\"click\",\"x\":4,\"y\":5,\"processId\":42,\"windowId\":99,\"deliveryMode\":\"background\"}"));
        checks.Check(routedResult.Success && backgroundBackend.LastMouse?.Context.DeliveryMode == ComputerUseDeliveryMode.Background &&
                     backgroundBackend.LastMouse.Context.Target?.ProcessId == 42 &&
                     backgroundBackend.LastMouse.Context.Target.WindowId == 99,
            "后台后端收到精确窗口目标且保持后台投递模式");

        // 权限开关：关闭后如实拒绝，而不是伪装成功。
        var locked = new ComputerUsePlugin(new FakeScreenCapture(), new FakeInput())
        {
            ScreenCaptureEnabled = false, InputSimulationEnabled = false,
        };
        var lockedShot = await locked.ExecuteAsync(new PureToolCall("l1", ComputerUsePlugin.ScreenshotTool, "{}"));
        checks.Check(!lockedShot.Success && lockedShot.Content.Contains("已关闭", StringComparison.Ordinal),
            "截图开关关闭时如实报告不可执行");
        var lockedKey = await locked.ExecuteAsync(new PureToolCall("l2", ComputerUsePlugin.KeyboardTool,
            "{\"action\":\"key\",\"key\":\"A\"}"));
        checks.Check(!lockedKey.Success, "输入开关关闭时拒绝键盘模拟");

        var gate = ComputerUseOperationGate.Shared;
        await using var first = await gate.EnterAsync("test-input");
        var waiting = gate.EnterAsync("test-input").AsTask();
        checks.Check(!waiting.IsCompleted, "相同输入资源的并发调用会等待前一个动作释放");
        await first.DisposeAsync();
        await using var second = await waiting;
        checks.Check(second is not null, "输入资源释放后下一调用获得独占权");
        _ = computer;
    }

    private static async Task UninstallRevokes(Checks checks, PluginStateStore store, PluginToolRegistry registry,
        BrowserUsePlugin browser, ComputerUsePlugin computer, AppDataLayout layout)
    {
        try { Directory.Delete(Path.Combine(layout.PluginsDirectory, "agent.browser-use"), true); } catch { }
        try { Directory.Delete(Path.Combine(layout.PluginsDirectory, "agent.computer-use"), true); } catch { }
        store.Uninstall("agent.browser-use");
        store.Uninstall("agent.computer-use");
        checks.Check(registry.ActiveToolNames.Count == 0, "卸载后两个能力的工具全部撤销");
        var ghost = await registry.DispatchAsync(new PureToolCall("g1", BrowserUsePlugin.CdpTool, "{\"method\":\"Browser.close\"}"));
        checks.Check(!ghost.Success, "卸载后不存在可调用的幽灵 CDP 工具");
        var ghostShot = await registry.DispatchAsync(new PureToolCall("g2", ComputerUsePlugin.ScreenshotTool, "{}"));
        checks.Check(!ghostShot.Success, "卸载后不存在可调用的幽灵截图工具");

        // 注销是幂等的；重复注销不抛异常也不影响其它处理器。
        checks.Check(registry.Unregister(browser), "浏览器处理器已被注销");
        checks.Check(!registry.Unregister(browser), "重复注销是幂等空操作");

        // 通过桥验证未接线时如实报告，而不是伪造 CDP 结果。
        var unwired = new BrowserUsePlugin(null);
        var unwiredResult = await unwired.ExecuteAsync(new PureToolCall("u1", BrowserUsePlugin.CdpTool, "{\"method\":\"X\"}"));
        checks.Check(!unwiredResult.Success && unwiredResult.Content.Contains("尚未接线", StringComparison.Ordinal),
            "未接线宿主桥时 CDP 调用如实报告未接线");
        _ = computer;
    }

    private static Task RegistryIsolation(Checks checks, PluginStateStore store, PluginCapabilityGateway gateway, AppDataLayout layout)
    {
        // 两个能力的门禁互不影响：只装电脑操控时浏览器控制仍不可用。
        Directory.CreateDirectory(Path.Combine(layout.PluginsDirectory, "agent.computer-use"));
        File.WriteAllText(Path.Combine(layout.PluginsDirectory, "agent.computer-use", "manifest.json"), "{\"id\":\"agent.computer-use\"}");
        store.Install("agent.computer-use");
        checks.Check(gateway.IsAvailable(PluginCapability.ComputerControl), "仅安装电脑操控时该能力可用");
        checks.Check(!gateway.IsAvailable(PluginCapability.BrowserControl), "仅安装电脑操控不影响浏览器控制门禁");

        // 未知插件能力：未登记的能力不给假提供者。
        checks.Check(PluginCapabilityRegistry.ProviderId((PluginCapability)999) is null, "未登记能力没有提供者");
        try { Directory.Delete(Path.Combine(layout.PluginsDirectory, "agent.computer-use"), true); } catch { }
        store.Uninstall("agent.computer-use");

        // 注册表快照与状态存储保持一致。
        var statuses = gateway.AllStatuses();
        checks.Check(statuses.Count == Enum.GetValues<PluginCapability>().Length,
            "能力快照覆盖全部已登记能力");
        checks.Check(statuses.All(s => !s.IsAvailable), "卸载后能力快照全部不可用");
        return Task.CompletedTask;
    }

    /// <summary>可控的截图替身：返回固定 PNG 负载，不访问真实桌面。</summary>
    private sealed class FakeScreenCapture : IComputerScreenCapture
    {
        public List<ScreenRect> Calls { get; } = new();

        public Task<ScreenCaptureResult> CaptureAsync(ScreenRect bounds, CancellationToken cancellationToken = default)
        {
            Calls.Add(bounds);
            // 最小合法 PNG 头（仅用于让上层校验签名，不代表真实图像内容）。
            byte[] png = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0];
            var width = bounds.Width ?? 640;
            var height = bounds.Height ?? 480;
            return Task.FromResult(new ScreenCaptureResult(true, png, width, height, null));
        }
    }

    /// <summary>可控的输入替身：记录调用，不触碰真实鼠标键盘。</summary>
    private sealed class FakeInput : IComputerInput
    {
        public List<string> Calls { get; } = new();

        public bool Move(int x, int y) { Calls.Add($"move:{x},{y}"); return true; }
        public bool Click(int x, int y, MouseButton button) { Calls.Add($"click:{button}:{x},{y}"); return true; }
        public bool DoubleClick(int x, int y) { Calls.Add($"double:{x},{y}"); return true; }
        public bool Scroll(int delta) { Calls.Add($"scroll:{delta}"); return true; }
        public bool TypeText(string text) { Calls.Add("type:" + text.Length); return true; }
        public bool PressKey(string key) { Calls.Add("key:" + key); return true; }
        public bool Hotkey(IReadOnlyList<string> keys) { Calls.Add("hotkey:" + string.Join("+", keys)); return true; }
    }

    /// <summary>可控的后台电脑后端替身：记录精确窗口目标，不驱动真实桌面。</summary>
    private sealed class FakeComputerBackend : IComputerUseBackend
    {
        public ComputerUseBackendKind Kind => ComputerUseBackendKind.CuaDriver;
        public bool IsAvailable => true;
        public string? UnavailableReason => null;
        public bool SupportsBackground => true;
        public bool SupportsForeground => true;
        public ComputerUseMouseRequest? LastMouse { get; private set; }

        public Task<ComputerUseBackendResult> CaptureAsync(ComputerUseCaptureRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ComputerUseBackendResult(true, "fake capture", [0x89, (byte)'P', (byte)'N', (byte)'G'], 1, 1));

        public Task<ComputerUseBackendResult> MouseAsync(ComputerUseMouseRequest request,
            CancellationToken cancellationToken = default)
        {
            LastMouse = request;
            return Task.FromResult(new ComputerUseBackendResult(true, "fake mouse"));
        }

        public Task<ComputerUseBackendResult> KeyboardAsync(ComputerUseKeyboardRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ComputerUseBackendResult(true, "fake keyboard"));
    }

    /// <summary>可控的浏览器桥替身：回显方法名，不驱动真实浏览器。</summary>
    private sealed class FakeBridge : BrowserUseBridge
    {
        public List<string> Calls { get; } = new();

        public override Task<string?> CallCdpAsync(string method, string parametersJson,
            CancellationToken cancellationToken = default)
        {
            Calls.Add("cdp:" + method);
            return Task.FromResult<string?>($"{{\"method\":\"{method}\"}}");
        }

        public override Task<string?> EvaluateAsync(string javaScript, CancellationToken cancellationToken = default)
        {
            Calls.Add("eval");
            return Task.FromResult<string?>("null");
        }

        public override Task<bool> NavigateAsync(string url, CancellationToken cancellationToken = default)
        {
            Calls.Add("nav:" + url);
            return Task.FromResult(true);
        }
    }
}
