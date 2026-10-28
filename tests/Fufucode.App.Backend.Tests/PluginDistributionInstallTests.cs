using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Fufucode.Core;
using Fufucode.Core.Backend;
using Fufucode.Core.Plugins;

namespace Fufucode.App.Backend.Tests;

/// <summary>
/// CLI 安装落点与激活落点不一致的回归：
/// `fufucode plugins install` 把载荷放在 &lt;dataRoot&gt;/distribution/&lt;id&gt;，
/// 而激活只认 &lt;dataRoot&gt;/plugins/&lt;id&gt; 下的 manifest。缺了这一段，
/// 市场里显示"已启用"、进程里却永远拿不到插件能力（实测装完 subagents 后无法派发子代理）。
/// </summary>
internal static class PluginDistributionInstallTests
{
    private const string PluginId = "workspace.subagents";

    public static void Run(Checks checks)
    {
        var root = Path.Combine(Path.GetTempPath(), "fufucode-dist-install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var layout = new AppDataLayout(root);
            var store = new PluginStateStore(layout);
            var gateway = new PluginCapabilityGateway(store);
            var registry = new PluginToolRegistry(gateway);
            var manager = new PluginPackageManager(layout, registry, store);

            // 模拟 CLI 安装：只在 distribution/<id> 放载荷
            var distribution = Path.Combine(layout.Root, "distribution", PluginId);
            Directory.CreateDirectory(distribution);
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Fufucode.Plugins.Subagents.dll"),
                Path.Combine(distribution, "Fufucode.Plugins.Subagents.dll"), true);
            File.WriteAllText(Path.Combine(distribution, "manifest.json"), JsonSerializer.Serialize(new
            {
                id = PluginId,
                name = "Subagent 子代理",
                version = "1.0.0",
                entryAssembly = "Fufucode.Plugins.Subagents.dll",
                packageType = "Fufucode.Plugins.Subagents.SubagentPluginPackage",
                capabilities = new[] { "Subagents" },
            }));

            checks.Check(!File.Exists(Path.Combine(layout.PluginDirectory(PluginId), "manifest.json")),
                "前提：plugins 目录里还没有该插件载荷（只有 distribution 有）");

            store.Install(PluginId);
            store.SetEnabled(PluginId, true);

            checks.Check(manager.HasPhysicalPayload(PluginId),
                "distribution-only 的安装被部署成可激活形态（不再停在'已启用但永不加载'）");
            checks.Check(NativeSubagentRegistry.Factory is not null,
                "自愈部署后插件真正初始化（子代理工厂已注册）");

            // 幂等：再次同步不重复部署、不报错
            manager.SetPackageEnabled(PluginId, false);
            manager.SetPackageEnabled(PluginId, true);
            checks.Check(manager.HasPhysicalPayload(PluginId),
                "停用再启用后载荷仍在且可再次激活");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
