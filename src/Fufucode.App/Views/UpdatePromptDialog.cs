using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Fufucode.Core.Updates;

namespace Fufucode.App.Views;

/// <summary>下载完成、等待宿主启动安装器的请求；<see cref="Arguments"/> 为安装器静默参数。</summary>
internal sealed record UpdateInstallRequest(string InstallerPath, string? Arguments);

/// <summary>
/// 「发现新版本」提示窗口：显示版本、体积与发布说明，用户确认后原地下载并校验，
/// 下载完成即关闭并把安装请求交给宿主（由宿主启动安装器并退出）。
///
/// 只在发布渠道构建里出现（调用方用 <see cref="UpdateService.IsReleaseBuild"/> 把关）；
/// 关闭窗口或点「稍后」返回 null，下载中途关闭会取消任务，不在磁盘留下半包。
/// </summary>
internal sealed class UpdatePromptDialog : Window
{
    private readonly AppUpdateManifest _manifest;
    private readonly string _currentVersion;
    private readonly AppUpdateService _updates;
    private readonly ProgressBar _progress;
    private readonly TextBlock _status;
    private readonly Button _download;
    private readonly Button _later;
    private CancellationTokenSource? _cts;
    private bool _busy;

    public UpdatePromptDialog(AppUpdateManifest manifest, string currentVersion, AppUpdateService updates)
    {
        _manifest = manifest;
        _currentVersion = currentVersion;
        _updates = updates;

        Title = "软件更新";
        Width = 620;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Bind(BackgroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("AppDialogBackground"));

        var sizeMb = _manifest.PackageSizeBytes / (1024d * 1024d);
        var summary = sizeMb > 0
            ? $"当前 v{currentVersion} · 新版本 {sizeMb:F1} MB · 发布于 {_manifest.ReleaseDate:yyyy-MM-dd}"
            : $"当前 v{currentVersion} · 发布于 {_manifest.ReleaseDate:yyyy-MM-dd}";

        var notes = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(_manifest.ReleaseNotes) ? "（该版本没有发布说明）" : _manifest.ReleaseNotes.Trim(),
            TextWrapping = TextWrapping.Wrap,
        };
        var notesBox = new ScrollViewer { MaxHeight = 180, Content = notes };

        _progress = new ProgressBar { Minimum = 0, Maximum = 100, Value = 0, IsVisible = false };
        _status = new TextBlock
        {
            Classes = { "caption" },
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
        };
        var policy = new TextBlock
        {
            Classes = { "caption" },
            Text = _manifest.PolicyNotice,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75,
        };

        var body = new StackPanel
        {
            Spacing = 10,
            Children = { notesBox, _progress, _status, policy },
        };

        _later = new Button { Content = "稍后", MinWidth = 88, IsCancel = true };
        _download = new Button { Content = "下载并安装", Classes = { "accent" }, MinWidth = 116 };
        _later.Click += (_, _) => { CancelDownload(); Close(null); };
        _download.Click += async (_, _) => await DownloadAndCloseAsync();

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto") };
        root.Children.Add(AppDialog.Header("Icon.ArrowDownload", $"发现新版本 v{_manifest.Version}", summary));
        var content = AppDialog.Body(AppDialog.Card(body));
        Grid.SetRow(content, 1);
        root.Children.Add(content);
        var footer = AppDialog.Footer(_later, _download);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        Content = root;
        Closed += (_, _) => CancelDownload();
    }

    private async Task DownloadAndCloseAsync()
    {
        if (_busy) return;
        _busy = true;
        _download.IsEnabled = false;
        _later.IsEnabled = false;
        _progress.IsVisible = true;
        _status.IsVisible = true;
        _status.Text = "准备下载…";
        _cts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<UpdateProgressInfo>(info =>
            {
                _progress.Value = info.Percentage;
                _status.Text = info.StatusText;
            });
            var installer = await _updates.DownloadUpdateAsync(_manifest, progress, _cts.Token);
            _status.Text = "校验通过，准备安装…";
            // 更新发生在启动阶段：此时用户还没有在途会话，直接放行。
            var plan = await _updates.PrepareUpdateAsync(_currentVersion, _manifest, installer, hasActiveTasks: false);
            if (plan.IsBlocked || plan.InstallerPath is null)
            {
                Fail(plan.BlockReason ?? "安装器不可用。");
                return;
            }
            Close(new UpdateInstallRequest(plan.InstallerPath, plan.Arguments));
        }
        catch (OperationCanceledException)
        {
            // 用户关闭窗口：静默收尾，宿主不再处理。
        }
        catch (Exception error)
        {
            Fail(error.GetBaseException().Message);
        }
    }

    private void Fail(string message)
    {
        _busy = false;
        _progress.IsVisible = false;
        _status.IsVisible = true;
        _status.Text = "下载失败：" + message;
        _download.IsEnabled = true;
        _later.IsEnabled = true;
    }

    private void CancelDownload()
    {
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
    }
}
