using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Fufucode.Core;
using Fufucode.Core.Updates;

namespace Fufucode.App.Backend.Tests;

/// <summary>
/// 主程序安装更新服务全链路测试：
/// 1. 基于本地离线清单或配置发布源检查更新（不依赖 git/gh/node）；
/// 2. 版本比较逻辑（已是最新、发现新版本、版本过低不兼容）；
/// 3. 下载校验：严格校验 SHA-256 哈希，损坏或篡改包自动删除并 fail-closed；
/// 4. 签名政策：明确未签名政策说明，不伪造数字签名；
/// 5. 安全避让：后台写入任务在途时明确阻止直接覆盖强杀；
/// 6. 中断与回滚恢复：下次启动准确解释中断原因并恢复稳定版；
/// 7. 挂起安装完成：版本匹配时完成收尾并清理暂存。
/// </summary>
internal static class AppUpdateServiceTests
{
    public static async Task RunAsync(Checks checks)
    {
        var root = Path.Combine(Path.GetTempPath(), "fufucode-update-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var layout = new AppDataLayout(root);
            layout.EnsureCreated();
            var settingsStore = new AppSettingsStore(layout);
            using var updateService = new AppUpdateService(layout, settingsStore);

            await TestCheckUpdatesLogic(checks, layout, settingsStore, updateService);
            await TestDownloadAndHashVerification(checks, layout, updateService);
            await TestPrepareUpdateAndTaskSafety(checks, layout, updateService);
            await TestPendingAndInterruptedLifecycle(checks, layout, updateService);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }
    }

    private static async Task TestCheckUpdatesLogic(
        Checks checks, AppDataLayout layout, AppSettingsStore settingsStore, AppUpdateService updateService)
    {
        // 1. 无配置且无本地清单时，如实报告失败，不伪造更新
        var noFeedResult = await updateService.CheckForUpdatesAsync("1.4.0");
        checks.Equal(UpdateCheckStatus.Failed, noFeedResult.Status, "无发布源且无清单时返回 Failed");
        checks.Check(noFeedResult.Message.Contains("未配置更新发布源"), "失败信息指出未配置发布源");

        // 2. 创建本地测试更新清单
        var feedFile = Path.Combine(layout.UpdatesDirectory, "feed.json");
        var manifest = new AppUpdateManifest(
            Version: "1.5.0",
            ReleaseDate: DateTimeOffset.UtcNow,
            ReleaseNotes: "新增全量离线更新与插件共享依赖池支持。",
            PackageUrl: "file://" + Path.Combine(layout.Root, "mock-installer.exe").Replace('\\', '/'),
            Sha256: "DUMMY_SHA256",
            PackageSizeBytes: 1024 * 1024,
            MinCompatibleVersion: "1.2.0",
            SignaturePolicy: "Unsigned");

        await File.WriteAllTextAsync(feedFile, JsonSerializer.Serialize(manifest));

        // 3. 当前版本为 1.4.0（介于 1.2.0 与 1.5.0 之间）-> 发现可用更新
        var checkAvailable = await updateService.CheckForUpdatesAsync("1.4.0");
        checks.Equal(UpdateCheckStatus.UpdateAvailable, checkAvailable.Status, "发现可用新版本 1.5.0");
        checks.Check(checkAvailable.Manifest is not null, "更新清单解析成功");
        checks.Equal("1.5.0", checkAvailable.Manifest!.Version, "新版本号为 1.5.0");
        checks.Check(checkAvailable.Manifest.IsUnsigned, "明确标明属于未签名更新包");
        checks.Check(checkAvailable.Manifest.PolicyNotice.Contains("不伪造数字签名"), "政策说明如实披露不伪造签名");

        // 4. 当前版本已是 1.5.0 或更新 -> UpToDate
        var checkUpToDate = await updateService.CheckForUpdatesAsync("1.5.0");
        checks.Equal(UpdateCheckStatus.UpToDate, checkUpToDate.Status, "同版本返回已是最新");
        var checkNewer = await updateService.CheckForUpdatesAsync("1.6.0");
        checks.Equal(UpdateCheckStatus.UpToDate, checkNewer.Status, "当前版本更高同样判定为已是最新");

        // 5. 当前版本过低（如 1.0.0，低于 MinCompatibleVersion 1.2.0）-> Incompatible
        var checkIncompatible = await updateService.CheckForUpdatesAsync("1.0.0");
        checks.Equal(UpdateCheckStatus.Incompatible, checkIncompatible.Status, "低于最低兼容版本返回 Incompatible");
        checks.Check(checkIncompatible.Message.Contains("无法直接热更新"), "提示用户版本过低需手动安装全量包");
    }

    private static async Task TestDownloadAndHashVerification(
        Checks checks, AppDataLayout layout, AppUpdateService updateService)
    {
        var installerFile = Path.Combine(layout.Root, "mock-installer.exe");
        var testContent = "FUFUCODE_GENUINE_INSTALLER_PAYLOAD_BYTES";
        var bytes = Encoding.UTF8.GetBytes(testContent);
        await File.WriteAllBytesAsync(installerFile, bytes);
        var genuineSha256 = Convert.ToHexString(SHA256.HashData(bytes));

        // 1. 合法哈希校验通过
        var validManifest = new AppUpdateManifest(
            Version: "1.5.0",
            ReleaseDate: DateTimeOffset.UtcNow,
            ReleaseNotes: "更新测试",
            PackageUrl: installerFile,
            Sha256: genuineSha256,
            PackageSizeBytes: bytes.Length,
            SignaturePolicy: "Unsigned");

        var downloadedPath = await updateService.DownloadUpdateAsync(validManifest);
        checks.Check(File.Exists(downloadedPath), "更新包下载/获取成功落盘");
        checks.Equal("FUFUCODE_GENUINE_INSTALLER_PAYLOAD_BYTES", await File.ReadAllTextAsync(downloadedPath), "包内容完整保真");

        // 2. 篡改/损坏哈希：严格拒绝并删除暂存包
        var tamperedManifest = validManifest with
        {
            Version = "1.5.1",
            Sha256 = "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF"
        };

        bool hashFailed = false;
        try
        {
            await updateService.DownloadUpdateAsync(tamperedManifest);
        }
        catch (InvalidDataException ex)
        {
            hashFailed = true;
            checks.Check(ex.Message.Contains("哈希校验失败"), "异常明确指出 SHA-256 哈希校验失败");
        }

        checks.Check(hashFailed, "篡改更新包严格抛出 InvalidDataException");
        var badStagingPath = Path.Combine(layout.UpdateStagingDirectory("1.5.1"), "mock-installer.exe");
        checks.Check(!File.Exists(badStagingPath), "安全删除：校验失败的更新文件已被物理删除，不留隐患");
    }

    private static async Task TestPrepareUpdateAndTaskSafety(
        Checks checks, AppDataLayout layout, AppUpdateService updateService)
    {
        var installerPath = Path.Combine(layout.UpdateStagingDirectory("1.5.0"), "mock-installer.exe");
        var manifest = new AppUpdateManifest(
            Version: "1.5.0",
            ReleaseDate: DateTimeOffset.UtcNow,
            ReleaseNotes: "安全更新",
            PackageUrl: installerPath,
            Sha256: "MATCH",
            PackageSizeBytes: 1024);

        // 1. 存在后台在途写入任务时：必须拦截，禁止强杀
        var blockedPlan = await updateService.PrepareUpdateAsync("1.4.0", manifest, installerPath, hasActiveTasks: true);
        checks.Check(blockedPlan.IsBlocked, "安全避让：有在途后台任务时必须阻止立即应用更新");
        checks.Check(blockedPlan.BlockReason?.Contains("后台写入任务仍在运行") == true, "阻止原因明确说明有在途任务");
        var pendingFile = Path.Combine(layout.UpdatesDirectory, "pending-update.json");
        checks.Check(!File.Exists(pendingFile), "阻止时不创建挂起状态文件");

        // 2. 无在途任务（安全态）：生成准备计划并落盘 pending-update.json
        var readyPlan = await updateService.PrepareUpdateAsync("1.4.0", manifest, installerPath, hasActiveTasks: false);
        checks.Check(!readyPlan.IsBlocked, "无在途任务时放行更新准备");
        checks.Check(File.Exists(pendingFile), "挂起状态记录 pending-update.json 已真实写入");
        checks.Equal(installerPath, readyPlan.InstallerPath, "准备就绪的安装器路径正确");
    }

    private static async Task TestPendingAndInterruptedLifecycle(
        Checks checks, AppDataLayout layout, AppUpdateService updateService)
    {
        var pendingFile = Path.Combine(layout.UpdatesDirectory, "pending-update.json");

        // 1. 模拟下次启动：版本仍为 1.4.0（说明安装未生效）-> 返回 false
        var completedFalse = await updateService.CheckAndCompletePendingUpdateAsync("1.4.0");
        checks.Check(!completedFalse, "版本未变时挂起状态不判定为完成");
        checks.Check(File.Exists(pendingFile), "挂起状态依然保留");

        // 2. 模拟下次启动：版本已变为 1.5.0（安装成功）-> 返回 true 并清理状态
        var completedTrue = await updateService.CheckAndCompletePendingUpdateAsync("1.5.0");
        checks.Check(completedTrue, "版本升级为 1.5.0 时判定更新圆满完成");
        checks.Check(!File.Exists(pendingFile), "更新完成后 pending-update.json 自动清理");

        // 3. 模拟更新中断或回滚：写入 interrupted.json，下次启动读取并向用户解释
        await updateService.RecordInterruptedStateAsync("1.6.0", "1.5.0", "安装器被用户取消或被外部防病毒阻止");
        var interrupted = await updateService.CheckAndClearInterruptedStateAsync();
        checks.Check(interrupted is not null, "成功捕获上一轮更新中断回滚状态");
        checks.Equal("1.6.0", interrupted!.AttemptedVersion, "记录了尝试更新的版本");
        checks.Equal("1.5.0", interrupted.RestoredToVersion, "记录了已回滚恢复的稳定版本");
        checks.Check(interrupted.ErrorMessage.Contains("安装器被用户取消"), "保留真实中断错误信息");

        // 4. 清理后再次检查：无中断状态
        var secondCheck = await updateService.CheckAndClearInterruptedStateAsync();
        checks.Check(secondCheck is null, "通知读取后状态已自动清除，不重复弹窗");
    }
}
