using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.Threading;
using Fufucode.App.Features.Performance;
using Fufucode.App.Integration;

namespace Fufucode.App;

internal static class Program
{
    /// <summary>无头自测模式：跳过启动期主题应用（自测自行控制 RequestedThemeVariant）。</summary>
    internal static bool SelfTestMode { get; private set; }

    /// <summary>自测隔离的数据根目录（AppSettingsStore/诊断用）；产品路径为 null。</summary>
    internal static string? SelfTestDataRoot { get; set; }
    internal static bool NativeModuleSelfTest { get; private set; }

    /// <summary>独立测试组件进入无头自测模式：隔离数据根并跳过启动期主题/产品路径副作用。</summary>
    internal static void EnterSelfTestMode(string dataRoot)
    {
        SelfTestMode = true;
        SelfTestDataRoot = dataRoot;
    }

    /// <summary>当前进程的启动追踪；为 null 表示数据根不可写或未启动。</summary>
    internal static StartupTrace? Startup { get; private set; }

    /// <summary>本进程持有的单实例守卫；自测/探针或命名对象不可用时为 null。</summary>
    internal static SingleInstanceGuard? SingleInstance { get; private set; }

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // 尽早解析数据根：自定义 --data-root 时启动记录也落到同一处，而不是全局默认目录。
        var startupOptions = AppOptions.Parse(args);
        // 自测/无头预览有自己的隔离数据根，且会在启动后重建；不把测试启动污染进用户性能目录。
        var isolatedRun = args.Contains("--selftest") || args.Contains("--headless-ui") || args.Contains("--native-module-selftest");

        // 单实例：同一登录会话只允许一个 Fufucode。第二个实例先唤醒已有窗口（可能在托盘），
        // 再自己退出；不在用户数据目录留下启动记录。自测/探针可并行，不参与单实例。
        var probeRun = args.Contains("--speech-probe") || args.Contains("--windows-toast-probe");
        if (!isolatedRun && !probeRun)
        {
            var singleInstance = SingleInstanceGuard.TryAcquire(null, out var guard);
            if (singleInstance == SingleInstanceAcquireResult.AlreadyRunning)
            {
                SingleInstanceGuard.SignalExisting(null);
                return;
            }
            if (singleInstance == SingleInstanceAcquireResult.Acquired) SingleInstance = guard;
        }

        if (!isolatedRun)
        {
            Startup = StartupTrace.Start(startupOptions.CreateAppDataLayout());
            if (Startup is { } startupTrace)
                Fufucode.Core.Diagnostics.StartupProbe.Handler = startupTrace.Mark;
            Startup?.Mark("process.main", "Program.Main entered");
        }

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
            Startup?.Mark("avalonia.native-selftest", "native module selftest");
            BuildAvaloniaApp(AppOptions.Mock with { AppDataRootOverride = SelfTestDataRoot })
                .StartWithClassicDesktopLifetime(Array.Empty<string>());
            Environment.Exit(completion.Task.IsCompleted ? completion.Task.Result : 1);
        }
        Startup?.Mark("avalonia.headless", "headless preview");
        if (args.Length > 0 && args[0] == "--headless-ui")
        {
            SelfTestMode = true;
            Environment.Exit(SelfTest.HeadlessUiRunner.RunProcess(args.Skip(1).ToArray()));
        }

        // --selftest：无头自测入口，不依赖交互式桌面会话即可验证界面与 mock 会话
        Startup?.Mark("avalonia.selftest", "headless selftest");
        if (Array.IndexOf(args, "--selftest") >= 0)
        {
            SelfTestMode = true;
            Environment.Exit(RunSelfTestHeadless(args));
        }

        // 部署的收尾：上次"部署最新构建"留下的锁定文件必须在本进程加载程序集之前替换，
        // 否则会一直读到旧程序集、且 restart-pending.json 永远残留。
        try
        {
            Startup?.Mark("deploy.pending", "pending update apply");
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

        Startup?.Mark("deploy.cleanup", "journal prune");
        Startup?.Mark("avalonia.setup", "Avalonia app builder configured");
        // 默认后端为 FUMO 原生内核；--mock 回到脚本化演示会话
        BuildAvaloniaApp(startupOptions).StartWithClassicDesktopLifetime(args);
        Startup?.Mark("avalonia.exited", "desktop lifetime returned");
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
