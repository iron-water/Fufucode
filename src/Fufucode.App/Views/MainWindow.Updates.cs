using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Fufucode.App.Integration;
using Fufucode.App.Views;
using Fufucode.Core.Updates;

namespace Fufucode.App;

public partial class MainWindow
{
    private bool _startupUpdateCheckStarted;

    /// <summary>
    /// 启动后在后台检查 GitHub 新版本，有更新就弹提示窗口。
    ///
    /// 只在发布渠道构建（build-info 的 channel=github/test 且写了仓库坐标）里启用：
    /// 本地开发构建不该联网打扰，也没有可比的发布版本。
    /// 延迟几秒再查，避免和首帧渲染、会话恢复抢带宽与 UI 线程。
    /// </summary>
    internal async Task CheckForUpdatesOnStartupAsync()
    {
        if (_startupUpdateCheckStarted) return;
        _startupUpdateCheckStarted = true;
        if (Program.SelfTestMode || _options.Mode != AppSessionMode.Real) return;

        var build = BuildInfo.ResolveCurrent();
        if (!UpdateService.IsReleaseBuild(build)) return;
        if (!EffectiveSettingsStore.Current.AutoCheckUpdates) return;

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(4));
            if (_closed) return;

            using var service = new UpdateService();
            var result = await service.CheckAsync(build.Version, build.GitHubRepository!,
                includePreReleases: build.IsTestChannel);
            if (result.Status != UpdateCheckStatus.UpdateAvailable || result.Manifest is null) return;
            if (_closed) return;

            await ShowUpdatePromptAsync(build, result.Manifest);
        }
        catch (Exception error)
        {
            // 检查更新失败不影响使用：网络不可用、限流、离线都属于正常情况。
            System.Diagnostics.Trace.TraceWarning("Startup update check failed: {0}", error.GetBaseException().Message);
        }
    }

    /// <summary>提示窗口确认后：下载已在窗口内完成，这里只负责启动安装器并退出。</summary>
    private async Task ShowUpdatePromptAsync(BuildInfo build, AppUpdateManifest manifest)
    {
        var layout = _options.CreateAppDataLayout();
        using var updates = new AppUpdateService(layout, EffectiveSettingsStore);
        var dialog = new UpdatePromptDialog(manifest, build.Version, updates);
        var request = await SkinDialog.ShowAsync<UpdateInstallRequest?>(dialog, this);
        if (request is null) return;

        Process.Start(new ProcessStartInfo(request.InstallerPath, request.Arguments ?? string.Empty)
        {
            UseShellExecute = true,
        });
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
        else
            Environment.Exit(0);
    }
}
