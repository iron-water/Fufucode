using System;
using System.Threading;
using System.Threading.Tasks;

namespace Fufucode.Core.Updates;

/// <summary>
/// 主程序安装更新服务契约：
/// 1. 基于显式配置的发布源检查更新（在正式运行环境中不依赖 git / gh / node 等开发工具）；
/// 2. 支持用户主动触发下载、进度回报、取消与哈希校验（明确未签名政策，不伪造签名）；
/// 3. 安全更新前检查（禁止在在途写入任务运行时强杀覆盖）；
/// 4. 挂起记录与失败中断状态解释。
/// </summary>
public interface IAppUpdateService
{
    /// <summary>检查更新。</summary>
    Task<UpdateCheckResult> CheckForUpdatesAsync(string currentVersion, CancellationToken ct = default);

    /// <summary>下载更新包并严格校验 SHA-256 与签名。</summary>
    Task<string> DownloadUpdateAsync(AppUpdateManifest manifest, IProgress<UpdateProgressInfo>? progress = null, CancellationToken ct = default);

    /// <summary>准备应用更新（检查后台在途任务、创建挂起记录）。</summary>
    Task<UpdateApplyPlan> PrepareUpdateAsync(string currentVersion, AppUpdateManifest manifest, string installerPath, bool hasActiveTasks);

    /// <summary>检查上一轮更新是否发生中断或回滚，若有则读取并清除。</summary>
    Task<UpdateInterruptedState?> CheckAndClearInterruptedStateAsync();

    /// <summary>检查挂起更新是否已成功完成并清理临时文件。</summary>
    Task<bool> CheckAndCompletePendingUpdateAsync(string currentVersion);

    /// <summary>记录更新失败/中断回滚状态（供下次启动向用户解释）。</summary>
    Task RecordInterruptedStateAsync(string attemptedVersion, string restoredToVersion, string errorMessage);
}
