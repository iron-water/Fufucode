using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Fufucode.Core;
using Fufucode.Core.Backend;
using Fufucode.Core.Plugins;

namespace Fufucode.App.Backend.Tests;

/// <summary>
/// 共享依赖池与物理插件包安装/更新/回滚深度测试：
/// 1. 宿主已有程序集不重复打包；
/// 2. 外部依赖集中缓存至 .shared-packages/，同版本只部署一次，去重复用；
/// 3. 哈希校验 fail-closed（坏包直接拒绝且清理）；
/// 4. 引用计数与垃圾回收（卸载时只释放本插件引用，仅引用归零才删除物理文件）；
/// 5. 插件升级失败原子回滚（恢复旧版本可用状态，不留破损环境）。
/// </summary>
internal static class PluginDependencyTests
{
    public static async Task RunAsync(Checks checks)
    {
        var root = Path.Combine(Path.GetTempPath(), "fufucode-dep-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var layout = new AppDataLayout(root);
            layout.EnsureCreated();
            var store = new PluginStateStore(layout);
            var gateway = new PluginCapabilityGateway(store);
            var registry = new PluginToolRegistry(gateway);
            var depStore = new PluginDependencyStore(layout.PluginsDirectory);

            await TestSharedDependencyDeduplicationAndGC(checks, layout, depStore);
            await TestHashMismatchFailsClosed(checks, layout, depStore);
            await TestPluginUpdateAndRollback(checks, layout, store, gateway, registry);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }
    }

    private static Task TestSharedDependencyDeduplicationAndGC(
        Checks checks, AppDataLayout layout, PluginDependencyStore depStore)
    {
        var distDir = Path.Combine(layout.Root, "mock-dist");
        Directory.CreateDirectory(distDir);

        var sharedContent = "SHARED-DLL-CONTENT-V1";
        var sharedBytes = Encoding.UTF8.GetBytes(sharedContent);
        var sharedSha = Convert.ToHexString(SHA256.HashData(sharedBytes));
        var sharedFile = Path.Combine(distDir, "shared.lib.dll");
        File.WriteAllBytes(sharedFile, sharedBytes);

        var uniqueContent = "UNIQUE-DLL-CONTENT";
        var uniqueBytes = Encoding.UTF8.GetBytes(uniqueContent);
        var uniqueSha = Convert.ToHexString(SHA256.HashData(uniqueBytes));
        var uniqueFile = Path.Combine(distDir, "unique.lib.dll");
        File.WriteAllBytes(uniqueFile, uniqueBytes);

        var depShared = new PluginDependencyDeclaration("shared.lib", "1.0.0", sharedSha, "shared.lib.dll");
        var depUnique = new PluginDependencyDeclaration("unique.lib", "1.0.0", uniqueSha, "unique.lib.dll");

        // 1. 插件 A 获取 shared.lib 与 unique.lib
        var targetShared1 = depStore.ResolveAndAcquire(depShared, "plugin.a", distDir);
        var targetUnique = depStore.ResolveAndAcquire(depUnique, "plugin.a", distDir);
        checks.Check(File.Exists(targetShared1), "依赖池按需获取成功：shared.lib.dll 实体已部署进共享池");
        checks.Check(File.Exists(targetUnique), "依赖池按需获取成功：unique.lib.dll 实体已部署进共享池");
        checks.Check(targetShared1.Contains(".shared-packages"), "共享依赖存放在 .shared-packages 集中池中");

        // 2. 插件 B 同样声明 shared.lib：命中缓存，复用已有文件，引用计数增加
        var targetShared2 = depStore.ResolveAndAcquire(depShared, "plugin.b", distDir);
        checks.Equal(targetShared1, targetShared2, "去重复用：插件 B 获取同一共享依赖返回完全相同的物理路径");
        checks.Check(depStore.IsReferencedByOthers("shared.lib", "1.0.0", "plugin.a"),
            "引用计数：shared.lib 当前同时被插件 A 和插件 B 引用");

        // 3. 卸载插件 A：释放其引用
        depStore.ReleaseDependencies("plugin.a", new[] { depShared, depUnique });
        checks.Check(File.Exists(targetShared1), "共享安全：插件 A 释放后，插件 B 仍引用的 shared.lib 物理文件完好保留");
        checks.Check(!File.Exists(targetUnique), "引用归零垃圾回收：仅被插件 A 引用的 unique.lib 物理目录已彻底清理");

        // 4. 卸载插件 B：释放其对 shared.lib 的引用 -> 引用归零，物理回收
        depStore.ReleaseDependencies("plugin.b", new[] { depShared });
        checks.Check(!File.Exists(targetShared1), "全量引用归零：最后一个引用者释放后，shared.lib 物理文件已被安全回收");
        return Task.CompletedTask;
    }

    private static Task TestHashMismatchFailsClosed(
        Checks checks, AppDataLayout layout, PluginDependencyStore depStore)
    {
        var distDir = Path.Combine(layout.Root, "mock-tampered-dist");
        Directory.CreateDirectory(distDir);
        var tamperedFile = Path.Combine(distDir, "tampered.dll");
        File.WriteAllText(tamperedFile, "TAMPERED_OR_CORRUPT_BYTES");

        var badDep = new PluginDependencyDeclaration(
            "tampered", "1.0.0", "0000000000000000000000000000000000000000000000000000000000000000", "tampered.dll");

        bool threw = false;
        try
        {
            depStore.ResolveAndAcquire(badDep, "plugin.test", distDir);
        }
        catch (InvalidDataException)
        {
            threw = true;
        }

        checks.Check(threw, "哈希校验严防死守：SHA-256 不匹配的损坏依赖直接抛出 InvalidDataException");
        var targetPath = Path.Combine(depStore.SharedPoolDirectory, "tampered", "1.0.0", "tampered.dll");
        checks.Check(!File.Exists(targetPath), "损坏包不入池：校验失败的物理文件绝不存入共享池");
        return Task.CompletedTask;
    }

    private static Task TestPluginUpdateAndRollback(
        Checks checks, AppDataLayout layout, PluginStateStore store,
        PluginCapabilityGateway gateway, PluginToolRegistry registry)
    {
        var testPluginId = "agent.browser-use";
        var packageManager = new PluginPackageManager(layout, registry, store);

        // 1. 准备旧版本 v1.0.0 载荷并安装
        var v1Dir = Path.Combine(layout.Root, "v1-dist");
        Directory.CreateDirectory(v1Dir);
        File.WriteAllText(Path.Combine(v1Dir, "manifest.json"),
            "{\"id\":\"agent.browser-use\",\"name\":\"BrowserUse\",\"version\":\"1.0.0\",\"entryAssembly\":\"dummy.dll\"}");
        File.WriteAllText(Path.Combine(v1Dir, "dummy.dll"), "V1_BINARY_CONTENT");

        packageManager.InstallPackage(testPluginId, v1Dir);
        checks.Check(packageManager.HasPhysicalPayload(testPluginId), "初始安装成功：插件物理就绪");
        var v1InstalledFile = Path.Combine(layout.PluginDirectory(testPluginId), "dummy.dll");
        checks.Equal("V1_BINARY_CONTENT", File.ReadAllText(v1InstalledFile), "安装内容为 v1.0.0 载荷");

        // 2. 模拟更新失败场景：新版载荷源损坏或缺失依赖
        var v2BadDir = Path.Combine(layout.Root, "v2-corrupt-dist");
        Directory.CreateDirectory(v2BadDir);
        // 缺少 manifest.json 或缺少 entry assembly

        bool updateFailed = false;
        try
        {
            packageManager.UpdatePackage(testPluginId, Path.Combine(layout.Root, "non-existent-source"));
        }
        catch (Exception ex)
        {
            updateFailed = true;
            checks.Check(ex.Message.Contains("已自动回滚并恢复上一版本可用状态"),
                "更新失败说明中明确说明已自动回滚");
        }

        checks.Check(updateFailed, "升级失败如实抛出异常，绝不假装成功");
        checks.Check(packageManager.HasPhysicalPayload(testPluginId), "回滚保护：旧版本物理载荷完好保留");
        checks.Equal("V1_BINARY_CONTENT", File.ReadAllText(v1InstalledFile), "回滚验证：插件文件完全恢复为旧版 v1.0.0 内容");
        checks.Check(store.GetState(testPluginId).IsInstalled, "状态保护：插件仍然保持已安装可用状态，未变成损坏态");

        return Task.CompletedTask;
    }
}
