using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Fufucode.Core;
using Fufucode.Core.Backend;
using Fufucode.Core.Plugins;

namespace Fufucode.App.Backend.Tests;

/// <summary>
/// 可选的真实联调探针（不进默认套件，需要本机真实部署 Seep 工作台与 Python）：
///   dotnet run --project tests/Fufucode.App.Backend.Tests -- --seep-live [labRoot]
///
/// 真实启动 <c>Tool/mcp/seep_mcp_server.py</c> 子进程，验证 C# 桥的
/// initialize / tools/list / tools/call 全程与真实服务端互通，并核对 23 工具。
/// 探针失败即非零退出，不使用替身，不掩盖真实差异。
/// </summary>
internal static class SeepRealMcpProbe
{
    public static async Task<int> RunAsync(string? labRoot)
    {
        var probe = SeepReverseLabAssets.Probe(labRoot);
        Console.WriteLine($"[live] labRoot={probe.LabRoot}");
        Console.WriteLine($"[live] rootExists={probe.LabRootExists} mcp={probe.McpServerExists} relay={probe.RelayScriptExists}");
        if (!probe.McpAvailable)
        {
            Console.WriteLine("[live] SKIP 未部署或缺少 " + probe.McpServerPath);
            return 2;
        }

        var root = Path.Combine(Path.GetTempPath(), "seep-live-" + Guid.NewGuid().ToString("N"));
        var dataRoot = Path.Combine(root, "appdata");
        Directory.CreateDirectory(dataRoot);
        var failures = 0;
        void Check(bool condition, string name)
        {
            Console.WriteLine((condition ? "PASS " : "FAIL ") + name);
            if (!condition) failures++;
        }

        try
        {
            var store = new PluginStateStore(new AppDataLayout(dataRoot));
            var gateway = new PluginCapabilityGateway(store);
            var registry = new PluginToolRegistry(gateway, new ProductionToolDispatcher(root));
            var bridge = new SeepMcpClientBridge(labRoot: labRoot);
            var relay = new ReverseLabRelayHost(SeepReverseLabAssets.RelayPort, labRoot);
            await using var host = new SeepReverseLabPluginHost(store, registry, bridge, relay, relayEnabled: false);

            store.Install(SeepReverseLabAssets.PluginId);
            await host.SynchronizeAsync();

            Check(bridge.IsRunning, "真实启动 MCP 子进程");
            var childPid = bridge.ProcessId;
            Console.WriteLine("[live] mcpChildPid=" + (childPid?.ToString() ?? "(none)"));
            Check(childPid is > 0, "拿到真实 MCP 子进程 PID");
            Console.WriteLine($"[live] serverInfo={bridge.ServerInfo} toolCount={bridge.ToolCount}");
            Check(bridge.ToolCount == SeepReverseLabAssets.ExpectedToolCount,
                $"真实 tools/list 返回 {SeepReverseLabAssets.ExpectedToolCount} 个工具");

            var active = registry.ActiveToolNames;
            Check(active.Count == bridge.ToolCount, "全部真实工具进入调度表");
            Check(registry.ActiveRegistrations.All(r => r.ParametersJsonSchema.Length > 0),
                "真实工具契约都带参数 Schema");

            // 真实调用最轻量的工具：seep_status 只读本地工具就绪度。
            var status = await registry.DispatchAsync(new PureToolCall("live1", "seep_status", "{}"));
            Console.WriteLine("[live] seep_status => " + Trim(status.Content, 600));
            Check(status.Success, "真实调用 seep_status 成功");
            Check(status.Content.Contains("seep", StringComparison.OrdinalIgnoreCase) ||
                  status.Content.Contains("radare", StringComparison.OrdinalIgnoreCase),
                "seep_status 返回真实工具链信息");

            // 真实调用错误路径：核实桥如实转述服务端结果，不自行编造成功或失败。
            // 实测事实（2026-10-16）：seep MCP 的工具错误以文本形式返回
            // （"[ERROR]: Target file does not exist: ..."），并未设置 MCP 的 isError 标志。
            // 桥的契约是忠实转述 result/isError；把文本启发式判成失败属越权推测，本探针不据此断言。
            var missing = await registry.DispatchAsync(new PureToolCall("live2", "seep_r2_info",
                "{\"binary_path\":\"Z:/definitely-missing-fixture.bin\"}"));
            Console.WriteLine("[live] seep_r2_info(missing) isErrorFlag=" + (!missing.Success)
                + " => " + Trim(missing.Content, 200));
            Check(missing.Content.Contains("does not exist", StringComparison.OrdinalIgnoreCase) ||
                  missing.Content.Contains("ERROR", StringComparison.Ordinal),
                "真实服务端对缺失目标的报错原文被完整转述（未被吞掉或改写）");

            // 真实卸载：终止子进程、撤销工具。
            store.Uninstall(SeepReverseLabAssets.PluginId);
            await host.WaitForPendingAsync();
            Check(registry.ActiveToolNames.Count == 0, "真实卸载后工具全部撤销");
            Check(!bridge.IsRunning, "真实卸载后子进程终止");
            Check(childPid is { } pid && !ProcessExists(pid), "卸载后原 MCP 子进程 PID 已退出（无残留）");

            Console.WriteLine(failures == 0 ? $"[live] ALL PASS" : $"[live] FAILURES={failures}");
            return failures == 0 ? 0 : 1;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("[live] 异常：" + error);
            return 1;
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    private static string Trim(string text, int max) =>
        text.Length <= max ? text.Replace('\n', ' ') : text[..max].Replace('\n', ' ') + "…";

    /// <summary>指定 PID 是否仍然存活；只读探测，不终止任何进程。</summary>
    private static bool ProcessExists(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception)
        {
            return false; // 进程不存在（或已退出）即视为无残留。
        }
    }
}
