using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Fufucode.Core;
using Fufucode.Core.Backend;
using Fufucode.Core.Plugins;

namespace Fufucode.App.Backend.Tests;

/// <summary>
/// SEEP-1 Seep 逆向工程工作台插件定向测试：
/// 安装/启用/停用/卸载的工具门禁、23 工具动态映射、人格注入、合规中继宿主、
/// 以及“不留幽灵工具”的负向断言。使用真实临时数据根与真实 plugins.json；
/// MCP 子进程用脚本化替身（回放真实录制的 tools/list 清单），不需要真实 Python 运行时。
/// </summary>
internal static class SeepReverseLabPluginTests
{
    public static async Task RunAsync(Checks checks)
    {
        var root = Path.Combine(Path.GetTempPath(), "fufucode-seep-plugin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            CatalogContract(checks);
            PersonaAsset(checks);
            await ToolMappingAndGatingAsync(checks, root);
            await LifecycleAndGhostToolsAsync(checks, root);
            await CrossInstanceStateAsync(checks, root);
            await PersonaInjectionAsync(checks, root);
            await RelayHostStatesAsync(checks, root);
            await UnavailableLabRootAsync(checks, root);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    // ─────────────────────────── 目录与能力映射 ───────────────────────────

    private static void CatalogContract(Checks checks)
    {
        var definition = PluginMarketplaceCatalog.Find(SeepReverseLabAssets.PluginId);
        checks.Check(definition is not null, "插件目录包含 tool.seep-reverse-lab");
        checks.Check(definition is not null && definition.Category == PluginCategory.ToolExtension,
            "Seep 工作台归类为工具扩展");
        checks.Check(definition is not null && !definition.DefaultInstalled && !definition.DefaultEnabled,
            "Seep 工作台非必需：默认未安装且未启用");
        checks.Check(definition is not null && !definition.IsCoreBuiltin,
            "Seep 工作台不是核心内置组件，可卸载");
        checks.Check(definition is not null && definition.RequiredPermissions.Count > 0,
            "Seep 工作台明确声明所需权限");
        checks.Check(definition is not null && !string.IsNullOrWhiteSpace(definition.SystemPromptFragment),
            "Seep 工作台在目录中携带作战协议人格片段");

        checks.Equal(SeepReverseLabAssets.PluginId,
            PluginCapabilityRegistry.ProviderId(PluginCapability.ReverseLab),
            "逆向工程能力解析到 seep 插件");
        checks.Check(PluginCapabilityRegistry.Provider(PluginCapability.ReverseLab) is not null,
            "逆向工程能力能解析到真实插件定义");
        checks.Check(PluginCapabilityRegistry.CapabilitiesOf(SeepReverseLabAssets.PluginId)
                .SequenceEqual(new[] { PluginCapability.ReverseLab }),
            "插件到能力是双向一致的映射");

        // 既有能力不被本插件影响。
        checks.Equal("agent.browser-use",
            PluginCapabilityRegistry.ProviderId(PluginCapability.BrowserControl),
            "新增能力不影响 Browser Use 能力映射");
        checks.Equal("agent.computer-use",
            PluginCapabilityRegistry.ProviderId(PluginCapability.ComputerControl),
            "新增能力不影响 Computer Use 能力映射");
    }

    private static void PersonaAsset(Checks checks)
    {
        checks.Check(SeepReverseLabAssets.IsPersonaAvailable, "作战协议人格作为内嵌资源可用");
        var persona = SeepReverseLabAssets.SystemPromptFragment;
        checks.Check(persona.Length > 4000, "作战协议人格为完整原文而非摘要");
        checks.Check(persona.Contains("实验环境开关协议", StringComparison.Ordinal) &&
                     persona.Contains("反退缩规则", StringComparison.Ordinal),
            "人格包含 Lab Mode 与反退缩核心条款");
    }

    // ─────────────────────────── 工具映射与门禁 ───────────────────────────

    /// <summary>参数 Schema 必须是可解析的 JSON 对象（对象键序无关），且包含 type 判定。</summary>
    private static bool IsJsonObjectSchema(PluginToolRegistration registration)
    {
        try
        {
            using var document = JsonDocument.Parse(registration.ParametersJsonSchema);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("type", out var type) &&
                   type.ValueKind == JsonValueKind.String;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static async Task ToolMappingAndGatingAsync(Checks checks, string root)
    {
        var dataRoot = Path.Combine(root, "mapping");
        Directory.CreateDirectory(dataRoot);
        var labRoot = SeepFixtures.CreateFakeLab(root, createRelay: false);
        var store = new PluginStateStore(new AppDataLayout(dataRoot));
        var gateway = new PluginCapabilityGateway(store);
        var registry = new PluginToolRegistry(gateway, new ProductionToolDispatcher(root));
        var fake = SeepFakeProcess.FromRecordedManifest();
        var bridge = new SeepMcpClientBridge(SeepFixtures.Spec(labRoot), fake);
        var relay = new ReverseLabRelayHost(SeepFixtures.FreePort(), labRoot, new RecordingProcessFactory());
        await using var host = new SeepReverseLabPluginHost(store, registry, bridge, relay, relayEnabled: false);

        // ── 未安装：不启动子进程、注册表为空、直接调用被拒且原因指向未安装 ──
        var before = gateway.Status(PluginCapability.ReverseLab);
        checks.Check(!before.IsAvailable && !before.IsInstalled, "Seep 工作台未安装时能力不可用");
        checks.Check(before.Reason.Contains("未安装", StringComparison.Ordinal),
            "未安装原因如实包含“未安装”，不泛化");
        checks.Check(registry.ActiveToolNames.Count == 0, "未安装时插件工具调度表为空");
        checks.Check(!fake.Started, "未安装时没有启动 MCP 子进程");

        var blocked = await registry.DispatchAsync(new PureToolCall("n1", "seep_status", "{}"));
        checks.Check(!blocked.Success, "未安装时直接调用 seep_status 被拒绝");
        checks.Check(blocked.Content.Contains("未安装", StringComparison.Ordinal),
            "未安装时拒绝原因指向未安装");

        // ── 安装 + 启用：启动子进程，23 工具全部注册且带参数 Schema ──
        store.Install(SeepReverseLabAssets.PluginId);
        await host.SynchronizeAsync();
        checks.Check(fake.Started, "启用后真实启动 MCP 子进程（替身）");
        checks.Check(fake.SawInitialize && fake.SawInitializedNotification,
            "桥按协议发送 initialize 并补发 notifications/initialized");
        checks.Check(fake.SawToolsList, "桥通过 tools/list 动态获取工具，而非硬编码清单");

        const int expectedCount = SeepReverseLabAssets.ExpectedToolCount;
        checks.Equal(expectedCount, bridge.ToolCount, "工具数量等于真实 tools/list 的 23 个");
        var active = registry.ActiveToolNames;
        checks.Equal(expectedCount, active.Count, "启用后 23 个工具全部进入调度表");
        checks.Check(SeepFixtures.RecordedToolNames().All(n => active.Contains(n)),
            "全部录制工具名都在调度表中（无遗漏）");
        var registrations = registry.ActiveRegistrations;
        checks.Check(registrations.All(r => r.ParametersJsonSchema.Length > 0),
            "注册的每个工具都带参数 Schema");
        checks.Check(registrations.All(IsJsonObjectSchema),
            "工具参数 Schema 是合法 JSON 对象文本，不含 null");
        checks.Check(registrations.All(r => r.Capability == PluginCapability.ReverseLab),
            "注册工具统一挂在逆向工程能力下");
        checks.Check(registrations.Any(r => r.Description.Length > 0),
            "工具契约保留服务端提供的说明文本");

        // ── 真实调用：参数与结果都按真实协议路径翻译 ──
        var ok = await registry.DispatchAsync(new PureToolCall("c1", "seep_status", "{}"));
        checks.Check(ok.Success && ok.Content.Contains("RTK-FAKE-STATUS", StringComparison.Ordinal),
            "工具调用经 stdio JSON-RPC 往返并返回文本结果");
        checks.Check(fake.LastToolCallName == "seep_status", "桥把工具名原样透传为 tools/call");
        checks.Check(fake.LastToolArguments == "{}", "桥原样透传参数对象");

        // 参数不是合法 JSON 时按参数错误拒绝，不落到子进程。
        var badArgs = await registry.DispatchAsync(new PureToolCall("c2", "seep_r2_info", "{not json"));
        checks.Check(!badArgs.Success && badArgs.Content.Contains("合法 JSON", StringComparison.Ordinal),
            "非法 JSON 参数按参数错误拒绝");

        // 服务端 isError 结果不得被当作成功。
        var toolError = await registry.DispatchAsync(new PureToolCall("c3", "seep_r2_info",
            "{\"binary_path\":\"fail\"}"));
        checks.Check(!toolError.Success && toolError.Content.Contains("FIXTURE-ERROR", StringComparison.Ordinal),
            "服务端 isError=true 如实映射成失败结果");

        // 未注册的工具名如实拒绝，不被转成成功。
        var unknown = await registry.DispatchAsync(new PureToolCall("u1", "seep_nope", "{}"));
        checks.Check(!unknown.Success && unknown.Content.Contains("未知", StringComparison.Ordinal),
            "清单外的 seep 工具名如实拒绝");
    }

    // ─────────────────────────── 启停与幽灵工具 ───────────────────────────

    private static async Task LifecycleAndGhostToolsAsync(Checks checks, string root)
    {
        var dataRoot = Path.Combine(root, "lifecycle");
        Directory.CreateDirectory(dataRoot);
        var labRoot = SeepFixtures.CreateFakeLab(root, createRelay: false);
        var store = new PluginStateStore(new AppDataLayout(dataRoot));
        var gateway = new PluginCapabilityGateway(store);
        var registry = new PluginToolRegistry(gateway, new ProductionToolDispatcher(root));
        var fake = SeepFakeProcess.FromRecordedManifest();
        var bridge = new SeepMcpClientBridge(SeepFixtures.Spec(labRoot), fake);
        var relay = new ReverseLabRelayHost(SeepFixtures.FreePort(), labRoot, new RecordingProcessFactory());
        await using var host = new SeepReverseLabPluginHost(store, registry, bridge, relay, relayEnabled: false);

        store.Install(SeepReverseLabAssets.PluginId);
        await host.SynchronizeAsync();
        checks.Check(bridge.IsRunning && registry.ActiveToolNames.Count == 23,
            "启用后子进程运行且 23 工具可调度");

        // ── 停用：已安装但未启用 ≠ 未安装；工具撤销、子进程终止 ──
        store.SetEnabled(SeepReverseLabAssets.PluginId, false);
        await host.WaitForPendingAsync();
        var disabled = gateway.Status(PluginCapability.ReverseLab);
        checks.Check(disabled.IsInstalled && !disabled.IsEnabled && !disabled.IsAvailable,
            "停用后为已安装未启用且能力不可用");
        checks.Check(disabled.Reason.Contains("停用", StringComparison.Ordinal) &&
                     !disabled.Reason.Contains("未安装", StringComparison.Ordinal),
            "停用原因说明已停用，而不是未安装");
        checks.Check(registry.ActiveToolNames.Count == 0, "停用后 23 工具全部从调度表撤销");
        checks.Check(!bridge.IsRunning, "停用后 MCP 子进程已终止");
        checks.Check(fake.KillCount > 0, "停用后子进程收到真实终止调用");

        var ghost = await registry.DispatchAsync(new PureToolCall("g1", "seep_status", "{}"));
        checks.Check(!ghost.Success, "停用后不存在可调用的幽灵 seep 工具");
        checks.Check(ghost.Content.Contains("停用", StringComparison.Ordinal),
            "停用后调用被拒且原因指向已停用");

        // ── 重新启用：工具恢复，子进程重新启动 ──
        store.SetEnabled(SeepReverseLabAssets.PluginId, true);
        await host.WaitForPendingAsync();
        checks.Check(registry.ActiveToolNames.Count == 23, "重新启用后 23 工具恢复注册");
        checks.Check(bridge.IsRunning, "重新启用后子进程重新启动");

        // ── 卸载：回到未安装，工具注销、子进程终止；重复卸载幂等 ──
        store.Uninstall(SeepReverseLabAssets.PluginId);
        await host.WaitForPendingAsync();
        var uninstalled = gateway.Status(PluginCapability.ReverseLab);
        checks.Check(!uninstalled.IsInstalled && !uninstalled.IsAvailable, "卸载后回到未安装态");
        checks.Check(registry.ActiveToolNames.Count == 0, "卸载后工具全部注销");
        checks.Check(!bridge.IsRunning, "卸载后子进程已终止");

        var postUninstall = await registry.DispatchAsync(new PureToolCall("g2", "seep_status", "{}"));
        checks.Check(!postUninstall.Success, "卸载后不存在可调用的幽灵工具");
        checks.Check(postUninstall.Content.Contains("未安装", StringComparison.Ordinal),
            "卸载后调用被拒且原因指向未安装（保留处理器只为解释原因）");

        // 宿主收尾路径：显式注销处理器后，工具名退回“未注册”，注销本身幂等。
        checks.Check(registry.Unregister(bridge), "卸载后宿主可显式注销 seep 处理器");
        checks.Check(!registry.Unregister(bridge), "重复注销是幂等空操作");
        var afterUnregister = await registry.DispatchAsync(new PureToolCall("g3", "seep_status", "{}"));
        checks.Check(!afterUnregister.Success, "注销处理器后调用仍被拒绝");

        // 卸载后再次显式对齐不得复活工具。
        var startsAfterUninstall = fake.StartCount;
        await host.SynchronizeAsync();
        checks.Check(registry.ActiveToolNames.Count == 0, "卸载后同步不会复活工具");
        checks.Equal(startsAfterUninstall, fake.StartCount, "卸载后同步不会重新启动子进程");
    }

    // ─────────────────── 跨实例状态（设置页写入 → 组合根刷新） ───────────────────

    private static async Task CrossInstanceStateAsync(Checks checks, string root)
    {
        var dataRoot = Path.Combine(root, "cross-instance");
        Directory.CreateDirectory(dataRoot);
        var labRoot = SeepFixtures.CreateFakeLab(root, createRelay: false);

        // 组合根持有的实例（与设置页实例是同一 plugins.json 的两个 PluginStateStore）。
        var hostStore = new PluginStateStore(new AppDataLayout(dataRoot));
        var gateway = new PluginCapabilityGateway(hostStore);
        var registry = new PluginToolRegistry(gateway, new ProductionToolDispatcher(root));
        var fake = SeepFakeProcess.FromRecordedManifest();
        var bridge = new SeepMcpClientBridge(SeepFixtures.Spec(labRoot), fake);
        var relay = new ReverseLabRelayHost(SeepFixtures.FreePort(), labRoot, new RecordingProcessFactory());
        await using var host = new SeepReverseLabPluginHost(hostStore, registry, bridge, relay, relayEnabled: false);

        checks.Check(registry.ActiveToolNames.Count == 0, "跨实例场景起点为未安装");

        // 设置页实例写入安装命令 —— 组合根实例此前并不知道这次写入。
        var settingsStore = new PluginStateStore(new AppDataLayout(dataRoot));
        settingsStore.Install(SeepReverseLabAssets.PluginId);
        await host.WaitForPendingAsync();

        checks.Check(hostStore.IsActive(SeepReverseLabAssets.PluginId),
            "组合根实例通过广播 + reload 读到设置页写入的真实状态");
        checks.Check(registry.ActiveToolNames.Count == 23,
            "设置页安装后组合根的工具门禁立即放行 23 工具");

        // 设置页卸载同样要反映到组合根。
        settingsStore.Uninstall(SeepReverseLabAssets.PluginId);
        await host.WaitForPendingAsync();
        checks.Check(registry.ActiveToolNames.Count == 0, "设置页卸载后组合根立即撤销工具");
        checks.Check(!bridge.IsRunning, "设置页卸载后组合根终止了子进程");
    }

    // ─────────────────────────── 人格注入 ───────────────────────────

    private static async Task PersonaInjectionAsync(Checks checks, string root)
    {
        var dataRoot = Path.Combine(root, "persona");
        Directory.CreateDirectory(dataRoot);
        var store = new PluginStateStore(new AppDataLayout(dataRoot));
        var provider = new PluginStateSystemPromptProvider(store);

        checks.Check(provider.Current is null, "未安装时人格提供方为 null（不注入空 System）");
        checks.Check(PluginSystemPromptComposer.Compose(store) is null, "未安装时不组装任何人格");

        store.Install(SeepReverseLabAssets.PluginId);
        var fragment = provider.Current;
        checks.Check(fragment is not null, "安装启用后提供方给出人格片段");
        checks.Check(fragment == SeepReverseLabAssets.SystemPromptFragment,
            "人格片段与内嵌作战协议逐字一致");

        store.SetEnabled(SeepReverseLabAssets.PluginId, false);
        checks.Check(provider.Current is null, "停用后人格立即消失（不为停用插件注入）");

        // 追加语义：原有 System 文本保留，片段追加在后。
        var appended = PluginSystemPromptComposer.AppendTo("原有系统指令。", "人格片段。");
        checks.Equal("原有系统指令。\n\n人格片段。", appended, "人格片段追加在原有 System 文本之后");
        checks.Equal("原有系统指令。", PluginSystemPromptComposer.AppendTo("原有系统指令。", null),
            "没有人格时不改变原有 System 文本");

        // 会话层：真实模型请求里出现 System 消息（用脚本化模型客户端捕获请求）。
        var sessionRoot = Path.Combine(root, "persona-session");
        Directory.CreateDirectory(sessionRoot);
        var store2 = new PluginStateStore(new AppDataLayout(sessionRoot));
        var provider2 = new PluginStateSystemPromptProvider(store2);
        var client = new CapturingModelClient();
        await using (var session = new NativeCSharpChatSession(
            new PureCSharpAgentEngine(new ProductionToolDispatcher(root), client),
            systemPromptProvider: provider2))
        {
            await session.SendAsync("分析这个样本");
            await WaitForRequestsAsync(client, 1);
        }
        var withoutPlugin = client.Requests.FirstOrDefault();
        checks.Check(withoutPlugin is not null, "会话真实发出了模型请求");
        checks.Check(withoutPlugin is not null &&
                     withoutPlugin.Messages.All(m => m.Role != PureAgentRole.System),
            "未启用插件时请求里没有 System 消息");

        store2.Install(SeepReverseLabAssets.PluginId);
        var beforeCount = client.Requests.Count;
        await using (var session = new NativeCSharpChatSession(
            new PureCSharpAgentEngine(new ProductionToolDispatcher(root), client),
            systemPromptProvider: provider2))
        {
            await session.SendAsync("分析这个样本");
            await WaitForRequestsAsync(client, beforeCount + 1);
        }
        var withPlugin = client.Requests.Last();
        checks.Check(withPlugin.Messages.Count > 0 && withPlugin.Messages[0].Role == PureAgentRole.System,
            "启用插件后请求首条为 System 人格消息");
        checks.Check(withPlugin.Messages[0].Content.Contains("实验环境开关协议", StringComparison.Ordinal),
            "注入的 System 消息内容为作战协议人格");
    }

    private static async Task WaitForRequestsAsync(CapturingModelClient client, int expected)
    {
        for (var i = 0; i < 100; i++)
        {
            if (client.Requests.Count >= expected) return;
            await Task.Delay(20);
        }
        throw new CheckFailed($"模型客户端未在限时内收到第 {expected} 次请求");
    }

    // ─────────────────────────── 合规转译中继宿主 ───────────────────────────

    private static async Task RelayHostStatesAsync(Checks checks, string root)
    {
        // 1) 工作台脚本缺失 → NotDeployed，且不抛异常打断插件启用。
        var missing = new ReverseLabRelayHost(SeepFixtures.FreePort(),
            Path.Combine(root, "not-a-lab"), new RecordingProcessFactory());
        var notDeployed = await missing.StartAsync();
        checks.Check(notDeployed.State == RelayHostState.NotDeployed,
            "工作台未部署时中继返回 NotDeployed");
        checks.Check(notDeployed.Reason.Contains("未找到", StringComparison.Ordinal),
            "未部署原因给出具体缺项，不泛化");
        await missing.DisposeAsync();

        // 2) 端口已被外部进程占用（本用例真实监听一个回环端口）→ 只报告，不接管、不终止。
        var occupiedPort = SeepFixtures.StartLoopbackListener();
        try
        {
            var labRoot = SeepFixtures.CreateFakeLab(root, createRelay: true);
            var externalFactory = new RecordingProcessFactory();
            var external = new ReverseLabRelayHost(occupiedPort, labRoot, externalFactory);
            var occupied = await external.StartAsync();
            checks.Check(occupied.State == RelayHostState.PortOccupiedExternally,
                "端口被外部占用时中继返回 PortOccupiedExternally");
            checks.Check(occupied.IsForwardingAvailable, "外部已监听时如实认为转发可用");
            checks.Check(!external.OwnsProcess, "外部占用场景不接管该进程");
            checks.Check(externalFactory.StartCount == 0, "外部占用时不重复启动中继子进程");

            await external.StopAsync();
            await Task.Delay(100);
            checks.Check(SeepFixtures.IsPortOpen(occupiedPort),
                "停止本插件中继不会终止外部进程（端口仍在监听）");
            await external.DisposeAsync();
        }
        finally
        {
            SeepFixtures.StopLoopbackListener(occupiedPort);
        }

        // 3) 工作台就位 + 可启动 → 真实等待监听并判定 Running。
        var port = SeepFixtures.FreePort();
        var processFactory = new LoopbackRelayProcessFactory();
        var deployedRoot = SeepFixtures.CreateFakeLab(root, createRelay: true);
        var host = new ReverseLabRelayHost(port, deployedRoot, processFactory);
        try
        {
            var started = await host.StartAsync();
            checks.Check(started.State == RelayHostState.Running, "工作台就位且能启动时中继进入 Running");
            checks.Check(host.OwnsProcess && processFactory.StartCount > 0,
                "本插件看到自己启动的中继进程");
            checks.Check(host.IsListening(), "Running 状态下端口真的可连接（不靠进程存在冒充）");

            await host.StopAsync();
            checks.Check(!host.OwnsProcess, "停止后本插件不再持有中继进程");
            checks.Check(processFactory.KillCount > 0, "停止真实终止了本插件启动的中继进程");
        }
        finally
        {
            await host.DisposeAsync();
            processFactory.Stop();
        }

        // 4) 子进程起来但从不监听 → Failed，且回收子进程。
        var stalledFactory = new RecordingProcessFactory();
        var stalled = new ReverseLabRelayHost(SeepFixtures.FreePort(), deployedRoot, stalledFactory);
        var failed = await stalled.StartAsync();
        checks.Check(failed.State == RelayHostState.Failed,
            "子进程未进入监听时中继如实报告 Failed，而不是假装 Running");
        checks.Check(!stalled.OwnsProcess && stalledFactory.KillCount > 0,
            "判定失败后回收了子进程");
        await stalled.DisposeAsync();
    }

    // ─────────────────────────── 工作台未部署 ───────────────────────────

    private static async Task UnavailableLabRootAsync(Checks checks, string root)
    {
        var dataRoot = Path.Combine(root, "unavailable");
        Directory.CreateDirectory(dataRoot);
        var missingRoot = Path.Combine(root, "absent-lab");
        var probe = SeepReverseLabAssets.Probe(missingRoot);
        checks.Check(!probe.McpAvailable, "工作台未部署时 MCP 探测为不可用");
        checks.Check(probe.Describe().Contains(missingRoot, StringComparison.Ordinal),
            "未部署说明包含真实查找路径");

        var store = new PluginStateStore(new AppDataLayout(dataRoot));
        var gateway = new PluginCapabilityGateway(store);
        var registry = new PluginToolRegistry(gateway);
        var bridge = new SeepMcpClientBridge(
            SeepReverseLabAssets.CreateMcpProcessSpec(missingRoot), new RecordingProcessFactory());
        await using var host = new SeepReverseLabPluginHost(store, registry, bridge,
            new ReverseLabRelayHost(SeepFixtures.FreePort(), missingRoot, new RecordingProcessFactory()),
            relayEnabled: false);

        store.Install(SeepReverseLabAssets.PluginId);
        await host.SynchronizeAsync();

        checks.Check(gateway.IsAvailable(PluginCapability.ReverseLab),
            "能力门禁只反映插件市场状态，与工作台是否部署解耦");
        checks.Check(registry.ActiveToolNames.Count == 0,
            "工作台未部署时不注册任何工具（不留半套注册）");
        checks.Check(bridge.LastError is not null &&
                     bridge.LastError.Contains(missingRoot, StringComparison.Ordinal),
            "未部署时桥给出包含真实路径的可读原因");
        checks.Check(!bridge.IsRunning, "未部署时不会留下运行中的子进程");

        var call = await registry.DispatchAsync(new PureToolCall("x1", "seep_status", "{}"));
        checks.Check(!call.Success, "未部署时工具调用失败");
        checks.Check(call.Content.Contains(missingRoot, StringComparison.Ordinal),
            "未部署时拒绝原因给出真实缺失路径，不伪造工具结果");
    }
}

/// <summary>捕获发往模型的请求，用于断言 System 人格注入；响应恒为终态文本。</summary>
internal sealed class CapturingModelClient : IPureModelClient
{
    public List<PureModelRequest> Requests { get; } = new();

    public Task<PureModelResponse> GenerateAsync(PureModelRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (Requests) Requests.Add(request);
        return Task.FromResult(PureModelResponse.FromText("已捕获。"));
    }
}
