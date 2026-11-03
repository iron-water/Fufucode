using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fufucode.App.Infrastructure;
using Fufucode.App.Integration;
using Fufucode.App.Views;
using Fufucode.Core;
using Fufucode.Core.Updates;

namespace Fufucode.App.SelfTest;

/// <summary>
/// 「发现新版本」提示窗口的无头渲染验证：用合成 manifest 建窗、截图并断言文案与控件状态。
/// 入口：<c>Fufucode.App.exe --update-prompt-selftest &lt;输出目录&gt;</c>。
/// 只写临时数据根，不联网、不触发真实下载。
/// </summary>
internal static class UpdatePromptShot
{
    internal static int Run(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var data = Path.Combine(Path.GetTempPath(), "fufucode-update-prompt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(data);
        Program.EnterSelfTestMode(data);

        var completed = new TaskCompletionSource<int>();
        Dispatcher.UIThread.Post(async () =>
        {
            var code = 1;
            try
            {
                await Task.Delay(200);
                var layout = new AppDataLayout(data);
                using var updates = new AppUpdateService(layout, new AppSettingsStore(layout));
                var manifest = new AppUpdateManifest(
                    Version: "1.5.0",
                    ReleaseDate: DateTimeOffset.Now,
                    ReleaseNotes: "· 新增启动时自动检查更新\n· 更新提示窗口支持进度与校验\n· 修复分页读取超长单行不截断",
                    PackageUrl: "https://example.invalid/Fufucode-Native-Setup.exe",
                    Sha256: new string('a', 64),
                    PackageSizeBytes: 50_540_000);
                var dialog = new UpdatePromptDialog(manifest, "1.4.0", updates);
                dialog.Show();
                await Task.Delay(300);
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                using (var frame = dialog.CaptureRenderedFrame())
                    frame?.Save(Path.Combine(outputDirectory, "update-prompt.png"), PngBitmapEncoderOptions.Default);

                var texts = dialog.GetVisualDescendants().OfType<TextBlock>()
                    .Select(t => t.Text ?? string.Empty).Where(t => t.Length > 0).ToArray();
                var checks = new[]
                {
                    (texts.Any(t => t.Contains("发现新版本 v1.5.0")), "标题显示新版本号"),
                    (texts.Any(t => t.Contains("当前 v1.4.0")), "副标题显示当前版本"),
                    (texts.Any(t => t.Contains("启动时自动检查更新")), "发布说明可见"),
                    (texts.Any(t => t.Contains("未签名")), "签名政策提示可见"),
                    (dialog.GetVisualDescendants().OfType<Button>().Any(b => (b.Content as string) == "下载并安装"),
                        "下载按钮存在"),
                    (dialog.GetVisualDescendants().OfType<Button>().Any(b => (b.Content as string) == "稍后"),
                        "稍后按钮存在"),
                    (!dialog.GetVisualDescendants().OfType<ProgressBar>().Any(p => p.IsVisible),
                        "下载前进度条隐藏"),
                };
                code = checks.All(c => c.Item1) ? 0 : 1;
                foreach (var (ok, label) in checks)
                    Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {label}");
                dialog.Close();
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("update prompt selftest failed: " + error);
            }
            finally
            {
                if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
                    lifetime.Shutdown();
                completed.TrySetResult(code);
            }
        }, DispatcherPriority.Background);

        AppBuilder.Configure(() => new App(AppOptions.Mock with { AppDataRootOverride = data }))
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .UseSkia().WithInterFont().WithAppTypography().StartWithClassicDesktopLifetime([]);
        return completed.Task.GetAwaiter().GetResult();
    }
}
