using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fufucode.Core;
using Fufucode.Core.Updates;

namespace Fufucode.App.ViewModels;

/// <summary>
/// 设置页的"构建与更新"：
/// - 本地开发构建：按钮执行**部署**（把最新本机构建同步到运行位置），比较构建身份，不联网；
/// - 发布渠道构建：按钮执行**更新**（检查 GitHub Release 并安装），比较版本号。
/// 两条路径由构建标记 channel 决定，互不回退。
/// </summary>
public sealed partial class SettingsPageViewModel
{
    [ObservableProperty] private string _updateStatusText = "尚未检查";
    [ObservableProperty] private bool _hasUpdateAvailable;
    [ObservableProperty] private bool _isCheckingUpdate;
    [ObservableProperty] private bool _isDownloadingUpdate;
    [ObservableProperty] private double _updateProgressPercent;
    [ObservableProperty] private string _updateProgressText = "";
    [ObservableProperty] private bool _canApplyUpdate;
    [ObservableProperty] private string? _downloadedInstallerPath;
    [ObservableProperty] private string? _updateInterruptedNotice;
    [ObservableProperty] private AppUpdateManifest? _updateManifest;
    [ObservableProperty] private string _updateChannelText = "尚未确定构建渠道";
    [ObservableProperty] private string _updateActionText = "检查";
    [ObservableProperty] private string _currentBuildText = "未知";

    public bool HasInterruptedNotice => !string.IsNullOrEmpty(UpdateInterruptedNotice);
    public bool HasUpdateManifest => UpdateManifest is not null;

    private CancellationTokenSource? _downloadCts;
    private BuildInfo? _build;

    private AppDataLayout ResolveLayout() => new(
        _context?.DataRoot ?? Program.SelfTestDataRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    private AppSettingsStore ResolveSettingsStore(AppDataLayout layout) =>
        _context?.Store ?? new AppSettingsStore(layout);

    /// <summary>当前构建信息（首次访问时解析一次）。</summary>
    public BuildInfo CurrentBuild => _build ??= BuildInfo.ResolveCurrent();

    /// <summary>当前是发布构建（走 GitHub 更新）。</summary>
    public bool IsReleaseBuild => UpdateService.IsReleaseBuild(CurrentBuild);

    /// <summary>刷新构建渠道标签与按钮文案；由 Attach 与部署/更新完成后调用。</summary>
    public void RefreshBuildChannel()
    {
        _build = BuildInfo.ResolveCurrent();
        CurrentBuildText = $"{_build.Version} · {_build.BuildId}";
        if (IsReleaseBuild)
        {
            UpdateChannelText = $"发布渠道（{_build.GitHubRepository}）";
            UpdateActionText = CanApplyUpdate ? "安装并重启" : "检查更新";
            UpdateStatusText = "尚未检查更新。";
        }
        else
        {
            UpdateChannelText = "本地开发构建 · 部署本机最新构建";
            UpdateActionText = "部署最新构建";
            UpdateStatusText = "尚未部署。点击后同步本机最新构建。";
        }
    }

    /// <summary>统一按钮：按构建渠道分派。两种渠道不共享发现逻辑。</summary>
    [RelayCommand]
    public async Task CheckAndUpgradeAsync()
    {
        if (IsCheckingUpdate || IsDownloadingUpdate) return;
        RefreshBuildChannel();
        if (IsReleaseBuild)
        {
            if (CanApplyUpdate) await ApplyUpdateAsync();
            else await CheckReleaseAsync();
        }
        else
        {
            await DeployLocalBuildAsync();
        }
    }

    // ── 部署（本地构建） ──

    /// <summary>
    /// 部署本机最新构建到运行位置：比较构建身份而非版本号；
    /// 增量复制，锁定文件在重启后替换。整个过程不联网。
    /// </summary>
    private async Task DeployLocalBuildAsync()
    {
        IsCheckingUpdate = true;
        UpdateProgressText = "正在查找最新构建…";
        try
        {
            var layout = ResolveLayout();
            var deployment = new BuildDeploymentService(layout);
            var build = deployment.FindLatestBuild(out var sourceDirectory);
            if (build is null || sourceDirectory is null)
            {
                UpdateStatusText = "本地未找到可部署的构建（需先构建出 out/app 并包含 build-info.json）。";
                return;
            }

            if (CurrentBuild.SameBuild(build))
            {
                UpdateStatusText = $"运行中的已是构建 {build.BuildId}，无需部署。";
                return;
            }

            UpdateStatusText = $"正在部署构建 {build.BuildId}…";
            UpdateProgressText = "正在同步程序文件…";
            var result = await Task.Run(() => deployment.Deploy(sourceDirectory, AppContext.BaseDirectory));
            if (!result.Success)
            {
                UpdateStatusText = "部署失败：" + result.Message;
                return;
            }

            UpdateProgressPercent = 100;
            UpdateProgressText = result.Message;
            _build = null;
            RefreshBuildChannel();
            if (result.RestartRequired)
            {
                UpdateStatusText = "部署完成，正在重启以替换正在使用的文件…";
                RestartApplication();
            }
            else
            {
                UpdateStatusText = "部署完成，无需重启。";
            }
        }
        catch (Exception ex)
        {
            UpdateStatusText = "部署失败：" + ex.GetBaseException().Message;
        }
        finally
        {
            IsCheckingUpdate = false;
        }
    }

    // ── 更新（GitHub 发布渠道） ──

    private async Task CheckReleaseAsync()
    {
        IsCheckingUpdate = true;
        UpdateStatusText = "正在检查 GitHub Release…";
        HasUpdateAvailable = false;
        CanApplyUpdate = false;
        try
        {
            var layout = ResolveLayout();
            using var service = new UpdateService();
            if (CurrentBuild.GitHubRepository is not { Length: > 0 } repository)
            {
                UpdateStatusText = "当前构建未写入 GitHub 仓库坐标，无法检查发布更新。";
                return;
            }

            var result = await service.CheckAsync(CurrentBuild.Version, repository);
            UpdateStatusText = result.Message;
            if (result.Status == UpdateCheckStatus.UpdateAvailable && result.Manifest is not null)
            {
                UpdateManifest = result.Manifest;
                HasUpdateAvailable = true;
                UpdateStatusText = result.Message + " 正在准备安装包…";
                await DownloadUpdateCoreAsync();
            }
            else
            {
                UpdateManifest = null;
                HasUpdateAvailable = false;
            }
        }
        catch (Exception ex)
        {
            UpdateStatusText = "检查更新失败：" + ex.GetBaseException().Message;
        }
        finally
        {
            IsCheckingUpdate = false;
            OnPropertyChanged(nameof(UpdateActionText));
        }
    }

    [RelayCommand]
    public Task CheckForUpdatesAsync() =>
        IsReleaseBuild ? CheckReleaseAsync() : DeployLocalBuildAsync();

    [RelayCommand]
    public Task DownloadUpdateAsync() => DownloadUpdateCoreAsync();

    private async Task DownloadUpdateCoreAsync()
    {
        if (UpdateManifest is null || IsDownloadingUpdate) return;
        IsDownloadingUpdate = true;
        UpdateProgressPercent = 0;
        UpdateProgressText = "准备下载...";
        CanApplyUpdate = false;
        try
        {
            var layout = ResolveLayout();
            var service = new AppUpdateService(layout, ResolveSettingsStore(layout));
            var progress = new Progress<UpdateProgressInfo>(info =>
            {
                UpdateProgressPercent = info.Percentage;
                UpdateProgressText = info.StatusText;
            });
            _downloadCts = new CancellationTokenSource();
            DownloadedInstallerPath = await service.DownloadUpdateAsync(UpdateManifest, progress, _downloadCts.Token);
            CanApplyUpdate = true;
            UpdateActionText = "安装并重启";
            UpdateStatusText = $"已下载 v{UpdateManifest.Version}（SHA-256 校验通过），点击按钮安装并重启。";
        }
        catch (Exception ex)
        {
            UpdateStatusText = "下载更新失败：" + ex.GetBaseException().Message;
            CanApplyUpdate = false;
        }
        finally
        {
            IsDownloadingUpdate = false;
            OnPropertyChanged(nameof(UpdateActionText));
        }
    }

    [RelayCommand]
    public void CancelDownload()
    {
        if (!IsDownloadingUpdate) return;
        _downloadCts?.Cancel();
        UpdateStatusText = "下载已由用户取消。";
        IsDownloadingUpdate = false;
    }

    [RelayCommand]
    public async Task ApplyUpdateAsync()
    {
        if (DownloadedInstallerPath is null || UpdateManifest is null) return;
        var layout = ResolveLayout();
        var service = new AppUpdateService(layout, ResolveSettingsStore(layout));
        var hasActiveTasks = _context?.IsTurnActive?.Invoke() ?? false;
        var plan = await service.PrepareUpdateAsync(CurrentBuild.Version, UpdateManifest,
            DownloadedInstallerPath, hasActiveTasks);
        if (plan.IsBlocked)
        {
            UpdateStatusText = "更新被阻止：" + plan.BlockReason;
            return;
        }

        UpdateStatusText = "正在启动独立安装器并安全退出主程序...";
        try
        {
            Process.Start(new ProcessStartInfo(plan.InstallerPath!, plan.Arguments ?? "") { UseShellExecute = true });
            if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls
                .ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.Shutdown();
            }
            else
            {
                Environment.Exit(0);
            }
        }
        catch (Exception ex)
        {
            UpdateStatusText = "启动更新安装器失败：" + ex.GetBaseException().Message;
        }
    }

    private static void RestartApplication()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe is null) return;
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
            if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls
                .ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.Shutdown();
            }
            else
            {
                Environment.Exit(0);
            }
        }
        catch (Exception)
        {
            // 重启失败时保留当前实例，用户可手动重启完成替换。
        }
    }

    [RelayCommand]
    public void DismissInterruptedNotice()
    {
        UpdateInterruptedNotice = null;
        OnPropertyChanged(nameof(HasInterruptedNotice));
    }

    public async Task CheckPendingOrInterruptedUpdateAsync()
    {
        try
        {
            var layout = ResolveLayout();
            var service = new AppUpdateService(layout, ResolveSettingsStore(layout));
            var interrupted = await service.CheckAndClearInterruptedStateAsync();
            if (interrupted is not null)
            {
                UpdateInterruptedNotice = $"上次升级至 v{interrupted.AttemptedVersion} 未能完成（原因：{interrupted.ErrorMessage}），系统已自动恢复为稳定版本 v{interrupted.RestoredToVersion}。";
                OnPropertyChanged(nameof(HasInterruptedNotice));
            }
            else
            {
                var completed = await service.CheckAndCompletePendingUpdateAsync(CurrentBuild.Version);
                if (completed)
                {
                    UpdateStatusText = $"应用已成功升级至 v{CurrentBuild.Version}。";
                }
            }
        }
        catch { }
    }
}
