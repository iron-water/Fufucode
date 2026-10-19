using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.Threading;
using Fufucode.App.Integration;

namespace Fufucode.App;

internal static class Program
{
    /// <summary>无头自测模式：跳过启动期主题应用（自测自行控制 RequestedThemeVariant）。</summary>
    internal static bool SelfTestMode { get; private set; }

    /// <summary>自测隔离的数据根目录（AppSettingsStore/诊断用）；产品路径为 null。</summary>
    internal static string? SelfTestDataRoot { get; set; }
    internal static bool NativeModuleSelfTest { get; private set; }

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // 用户按需安装到 Fufucode 的工具链（node/bun/python/git-bash 等）对子进程可见；
        // 系统已有工具链时排在最前，不改变现有行为。
        try
        {
            Fufucode.Core.Runtimes.ToolchainEnvironment.ApplyToProcess(new Fufucode.Core.AppDataLayout());
        }
        catch (Exception error)
        {
            Console.WriteLine("工具链 PATH 注入失败：" + error.Message);
        }
        if (args.Length >= 2 && args[0] == "--automation-protocol")
        {
            SelfTest.AutomationControlSelfTest.ProtocolAsync(args).GetAwaiter().GetResult(); return;
        }
        if (args.Contains("--speech-probe")) Environment.Exit(SelfTest.SpeechAcceptance.RunAsync().GetAwaiter().GetResult());
        if (args.Contains("--windows-toast-probe"))
        {
            var result = Infrastructure.WindowsToastService.SendAsync(Guid.NewGuid().ToString("N"),
                "Fufucode 通知验收", "这是一次真实 Windows 系统通知投递测试。").GetAwaiter().GetResult();
            Console.WriteLine(result.Status);
            Environment.Exit(result.Delivered ? 0 : 1);
        }
        if (args.Contains("--native-module-selftest"))
        {
            SelfTestMode = NativeModuleSelfTest = true;
            SelfTestDataRoot = System.IO.Path.GetFullPath("tmp/native-modules-" + Guid.NewGuid().ToString("N"));
            var completion = new TaskCompletionSource<int>();
            Dispatcher.UIThread.Post(async () =>
            {
                var lifetime = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
                lifetime.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                var window = (MainWindow)lifetime.MainWindow!;
                var code = 1;
                try { code = await SelfTest.NativeBrowserModuleSelfTest.RunAsync(window); }
                catch (Exception error) { Console.WriteLine("Native module test failed: " + error); }
                finally
                {
                    window.Close();
                    await Task.Delay(1000);
                    completion.TrySetResult(code);
                    lifetime.Shutdown(code);
                }
            }, DispatcherPriority.Background);
            BuildAvaloniaApp(AppOptions.Mock with { AppDataRootOverride = SelfTestDataRoot })
                .StartWithClassicDesktopLifetime(Array.Empty<string>());
            Environment.Exit(completion.Task.IsCompleted ? completion.Task.Result : 1);
        }
        if (args.Length > 0 && args[0] == "--headless-ui")
        {
            SelfTestMode = true;
            Environment.Exit(SelfTest.HeadlessUiRunner.RunProcess(args.Skip(1).ToArray()));
        }

        // --selftest：无头自测入口，不依赖交互式桌面会话即可验证界面与 mock 会话
        if (Array.IndexOf(args, "--selftest") >= 0)
        {
            SelfTestMode = true;
            Environment.Exit(RunSelfTestHeadless(args));
        }

        // 部署的收尾：上次"部署最新构建"留下的锁定文件必须在本进程加载程序集之前替换，
        // 否则会一直读到旧程序集、且 restart-pending.json 永远残留。
        try
        {
            new Fufucode.Core.Updates.BuildDeploymentService(
                new Fufucode.Core.AppDataLayout()).ApplyPendingOnStartup();
        }
        catch (Exception error)
        {
            Console.WriteLine("待重启部署收尾失败：" + error.Message);
        }

        // 撤销备份只服务最近几轮；启动时顺手清掉过期文件，避免无限增长。
        try { Fufucode.Core.Backend.FileChangeJournal.PruneExpired(); }
        catch (Exception error)
        {
            Console.WriteLine("撤销备份清理失败：" + error.Message);
        }

        // 默认后端为 FUMO 原生内核；--mock 回到脚本化演示会话
        BuildAvaloniaApp(AppOptions.Parse(args)).StartWithClassicDesktopLifetime(args);
    }

    private static int RunSelfTestHeadless(string[] args)
    {
        var completion = new TaskCompletionSource<int>();

        // 消息循环由 StartWithClassicDesktopLifetime 阻塞式驱动，因此先把自测投递到 UI 调度器，
        // 待循环启动后执行；测试结束再关闭生命周期，使 Start 调用返回。
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                var exitCode = await SelfTest.SelfTestRunner.RunAsync(
                    args.Where(arg => arg != "--selftest").ToArray());
                completion.SetResult(exitCode);
            }
            catch (Exception ex)
            {
                Console.WriteLine("自测入口异常：" + ex);
                completion.SetResult(1);
            }
            finally
            {
                if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
                {
                    lifetime.Shutdown();
                }
            }
        }, DispatcherPriority.Background);

        try
        {
            AppBuilder.Configure<App>()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions())
                .UseSkia()
                .WithInterFont()
                .LogToTrace()
                .StartWithClassicDesktopLifetime(Array.Empty<string>());
        }
        catch (Exception ex)
        {
            Console.WriteLine("无头生命周期异常：" + ex);
            if (!completion.Task.IsCompleted)
            {
                completion.SetResult(1);
            }
        }

        return completion.Task.IsCompleted ? completion.Task.Result : 1;
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => BuildAvaloniaApp(AppOptions.Mock);

    public static AppBuilder BuildAvaloniaApp(AppOptions options)
        => AppBuilder.Configure(() => new App(options))
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
