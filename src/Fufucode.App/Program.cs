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
using Fufucode.App.Infrastructure;

namespace Fufucode.App;

internal static class Program
{
    /// <summary>无头自测模式：跳过启动期主题应用（自测自行控制 RequestedThemeVariant）。</summary>
    internal static bool SelfTestMode { get; private set; }

    /// <summary>自测隔离的数据根目录（AppSettingsStore/诊断用）；产品路径为 null。</summary>
    internal static string? SelfTestDataRoot { get; set; }
    internal static bool NativeModuleSelfTest { get; private set; }
    internal static bool NativeMotionSelfTest { get; private set; }

    /// <summary>独立测试组件进入无头自测模式：隔离数据根并跳过启动期主题/产品路径副作用。</summary>
    internal static void EnterSelfTestMode(string dataRoot)
    {
        SelfTestMode = true;
        SelfTestDataRoot = dataRoot;
        if (!string.IsNullOrWhiteSpace(dataRoot))
        {
            Fufucode.Core.Backend.FileChangeJournal.Configure(dataRoot);
            Integration.ActivationInbox.Configure(dataRoot);
        }
    }

    /// <summary>当前进程的启动追踪；为 null 表示数据根不可写或未启动。</summary>
    internal static StartupTrace? Startup { get; private set; }

    /// <summary>本进程持有的单实例守卫；自测/探针或命名对象不可用时为 null。</summary>
    internal static SingleInstanceGuard? SingleInstance { get; private set; }

    /// <summary>本进程启动时携带的审批协议负载；交给窗口在会话就绪后应答。</summary>
    internal static string? StartupActivationPayload { get; set; }

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // 尽早解析数据根：自定义 --data-root 时启动记录也落到同一处，而不是全局默认目录。
        // 「发现新版本」提示窗口的无头渲染自测（截图 + 文案断言），不联网。
        var updateShotIndex = Array.IndexOf(args, "--update-prompt-selftest");
        if (updateShotIndex >= 0 && updateShotIndex + 1 < args.Length)
            Environment.Exit(SelfTest.UpdatePromptShot.Run(args[updateShotIndex + 1]));

        // 诊断入口：只跑一次发布更新检查并打印结果，不启动 UI。
        // 用于验证发布包能否连上 GitHub Release（也可在 CI 里当冒烟）。
        if (args.Contains("--check-updates"))
        {
            var build = Fufucode.Core.Updates.BuildInfo.ResolveCurrent();
            if (!Fufucode.Core.Updates.UpdateService.IsReleaseBuild(build))
            {
                Console.WriteLine($"not-release-build version={build.Version} channel={build.Channel}");
                Environment.Exit(2);
            }
            using var updater = new Fufucode.Core.Updates.UpdateService();
            var result = updater.CheckAsync(build.Version, build.GitHubRepository!,
                includePreReleases: build.IsTestChannel).GetAwaiter().GetResult();
            Console.WriteLine($"status={result.Status} current={build.Version} repo={build.GitHubRepository}");
            Console.WriteLine("message=" + result.Message);
            if (result.Manifest is { } manifest)
            {
                Console.WriteLine($"manifest={manifest.Version} size={manifest.PackageSizeBytes} " +
                                  $"url={manifest.PackageUrl} sha256={manifest.Sha256}");
            }
            Environment.Exit(result.Status == Fufucode.Core.Updates.UpdateCheckStatus.Failed ? 1 : 0);
        }

        var startupOptions = AppOptions.Parse(args);
        // 显式 --data-root 不进环境变量；撤销备份与激活投递箱必须跟随本进程的实际数据根。
        var startupLayout = startupOptions.CreateAppDataLayout();
        Fufucode.Core.Backend.FileChangeJournal.Configure(startupLayout.Root);
        Integration.ActivationInbox.Configure(startupLayout.Root);
        StartupActivationPayload = startupOptions.ActivationPayload;
        // 自测/无头预览有自己的隔离数据根，且会在启动后重建；不把测试启动污染进用户性能目录。
        var isolatedRun = args.Contains("--selftest") || args.Contains("--headless-ui") ||
                          args.Contains("--native-module-selftest") || args.Contains("--browser-sidebar-selftest") || args.Contains("--browser-memory-probe") || args.Contains("--native-security-selftest") ||
                          args.Contains("--native-motion-selftest") || args.Contains("--native-graph-selftest") || args.Contains("--parallel-review");

        // 单实例：同一登录会话只允许一个 Fufucode。第二个实例先唤醒已有窗口（可能在托盘），
        // 再自己退出；不在用户数据目录留下启动记录。自测/探针可并行，不参与单实例。
        var probeRun = args.Contains("--speech-probe") || args.Contains("--windows-toast-probe");
        if (!isolatedRun && !probeRun)
        {
            // --data-root 不写环境变量：必须显式传入，否则隔离实例会算出默认根的名字，
            // 把用户安装版的单实例槽位占住（表现就是点任务栏没反应）。
            var singleInstance = SingleInstanceGuard.TryAcquire(null, out var guard,
                startupOptions.AppDataRootOverride);
            if (singleInstance == SingleInstanceAcquireResult.AlreadyRunning)
            {
                // 系统通知按钮以协议激活启动第二个进程：先留下负载，已运行实例取走后再应答审批。
                if (startupOptions.ActivationPayload is { } payload) Integration.ActivationInbox.Enqueue(payload);
                SingleInstanceGuard.SignalExisting(null, startupOptions.AppDataRootOverride);
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
            // --approval 追加三个审批按钮：真实渲染验证（点按会走协议链路回到本应用）。
            var approval = args.Contains("--approval");
            var probeSession = Guid.NewGuid().ToString();
            var actions = approval
                ? new[]
                {
                    new Infrastructure.WindowsToastAction("允许",
                        Integration.ApprovalLink.Create("probe-request", probeSession, Integration.ApprovalDecision.Allow)),
                    new Infrastructure.WindowsToastAction("始终允许",
                        Integration.ApprovalLink.Create("probe-request", probeSession, Integration.ApprovalDecision.AlwaysAllow)),
                    new Infrastructure.WindowsToastAction("拒绝",
                        Integration.ApprovalLink.Create("probe-request", probeSession, Integration.ApprovalDecision.Deny)),
                }
                : null;
            var result = Infrastructure.WindowsToastService.SendAsync(Guid.NewGuid().ToString("N"),
                approval ? "有一项工具待批准" : "Fufucode 通知验收",
                approval ? "允许执行 run_command？" : "这是一次真实 Windows 系统通知投递测试。",
                actions: actions).GetAwaiter().GetResult();
            Console.WriteLine(result.Status);
            Environment.Exit(result.Delivered ? 0 : 1);
        }
        if (args.Contains("--native-graph-selftest"))
        {
            var graphOptions = AppOptions.Parse(args) with { StartGraph = true, ParallelReview = true };
            SelfTestMode = true;
            SelfTestDataRoot = graphOptions.CreateAppDataLayout().Root;
            var evidence = System.IO.Path.Combine(SelfTestDataRoot, "evidence");
            var completion = new TaskCompletionSource<int>();
            Dispatcher.UIThread.Post(async () =>
            {
                var lifetime = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
                lifetime.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                var code = 1;
                try { code = args.Contains("--graph-interaction-only")
                    ? await SelfTest.NativeGraphInteractionSelfTest.RunAsync((MainWindow)lifetime.MainWindow!, evidence)
                    : args.Contains("--workspace-modes-only")
                    ? await SelfTest.NativeWorkModesSelfTest.RunAsync((MainWindow)lifetime.MainWindow!, evidence)
                    : await SelfTest.NativeGraphSelfTest.RunAsync((MainWindow)lifetime.MainWindow!, evidence, args.Contains("--autodev-settings-only")); }
                catch (Exception error) { Console.WriteLine("Native Graph failed: " + error); }
                finally { completion.TrySetResult(code); lifetime.Shutdown(code); }
            }, DispatcherPriority.Background);
            BuildAvaloniaApp(graphOptions).StartWithClassicDesktopLifetime(Array.Empty<string>());
            Environment.Exit(completion.Task.IsCompleted ? completion.Task.Result : 1);
        }
        if (args.Contains("--native-motion-selftest"))
        {
            SelfTestMode = NativeMotionSelfTest = true;
            SelfTestDataRoot = System.IO.Path.GetFullPath("tmp/dock-motion-native-data");
            var completion = new TaskCompletionSource<int>();
            Dispatcher.UIThread.Post(async () =>
            {
                var lifetime = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
                lifetime.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                var code = 1;
                try
                {
                    var window = (MainWindow)lifetime.MainWindow!;
                    code = args.Contains("--chat-icons-only")
                        ? await SelfTest.NativeChatIconsSelfTest.RunAsync(window)
                        : args.Contains("--markdown-table-only")
                        ? await SelfTest.NativeMarkdownTableSelfTest.RunAsync(window)
                        : args.Contains("--model-switch-only")
                            ? await SelfTest.NativeModelSwitchSelfTest.RunAsync(window)
                            : args.Contains("--typography-only")
                                ? await SelfTest.NativeTypographySelfTest.RunAsync(window)
                                : args.Contains("--chat-feedback-only")
                                    ? await SelfTest.NativeChatFeedbackSelfTest.RunAsync(window)
                                    : await SelfTest.NativeDockMotionSelfTest.RunAsync(window);
                }
                catch (Exception error) { Console.WriteLine("Native motion test failed: " + error); }
                finally { completion.TrySetResult(code); lifetime.Shutdown(code); }
            }, DispatcherPriority.Background);
            BuildAvaloniaApp(AppOptions.Mock with { AppDataRootOverride = SelfTestDataRoot })
                .StartWithClassicDesktopLifetime(Array.Empty<string>());
            Environment.Exit(completion.Task.IsCompleted ? completion.Task.Result : 1);
        }
        if (args.Contains("--native-security-selftest"))
        {
            SelfTestMode = true;
            SelfTestDataRoot = System.IO.Path.GetFullPath("tmp/security-center-native-data");
            var completion = new TaskCompletionSource<int>();
            Dispatcher.UIThread.Post(async () =>
            {
                var lifetime = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
                lifetime.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                var code = 1;
                try { code = await SelfTest.NativeSecurityCenterSelfTest.RunAsync((MainWindow)lifetime.MainWindow!); }
                catch (Exception error) { Console.WriteLine("Native security test failed: " + error); }
                finally { completion.TrySetResult(code); lifetime.Shutdown(code); }
            }, DispatcherPriority.Background);
            BuildAvaloniaApp(AppOptions.Mock with { AppDataRootOverride = SelfTestDataRoot })
                .StartWithClassicDesktopLifetime(Array.Empty<string>());
            Environment.Exit(completion.Task.IsCompleted ? completion.Task.Result : 1);
        }
        if (args.Contains("--browser-memory-probe"))
        {
            SelfTestMode = NativeModuleSelfTest = true;
            SelfTestDataRoot = System.IO.Path.GetFullPath("tmp/memory-probe-" + Guid.NewGuid().ToString("N"));
            var completion = new TaskCompletionSource<int>();
            Dispatcher.UIThread.Post(async () =>
            {
                var lifetime = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
                lifetime.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                var window = (MainWindow)lifetime.MainWindow!;
                var code = 1;
                try { code = await SelfTest.BrowserMemoryProbe.RunAsync(window); }
                catch (Exception error) { Console.WriteLine("Memory probe failed: " + error); }
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
        if (args.Contains("--native-module-selftest") || args.Contains("--browser-sidebar-selftest"))
        {
            SelfTestMode = NativeModuleSelfTest = true;
            SelfTestDataRoot = System.IO.Path.GetFullPath("tmp/native-modules-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(SelfTestDataRoot);
            using var nativeLog = new System.IO.StreamWriter(System.IO.Path.Combine(SelfTestDataRoot, "native-selftest.log")) { AutoFlush = true };
            Console.SetOut(nativeLog);
            Console.SetError(nativeLog);
            var completion = new TaskCompletionSource<int>();
            Dispatcher.UIThread.Post(async () =>
            {
                var lifetime = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
                lifetime.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                var window = (MainWindow)lifetime.MainWindow!;
                var code = 1;
                try
                {
                    code = args.Contains("--browser-sidebar-selftest")
                        ? await SelfTest.NativeBrowserSidebarSelfTest.RunAsync(window)
                        : await SelfTest.NativeBrowserModuleSelfTest.RunAsync(window);
                }
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
            try
            {
                BuildAvaloniaApp(AppOptions.Mock with { AppDataRootOverride = SelfTestDataRoot })
                    .StartWithClassicDesktopLifetime(Array.Empty<string>());
            }
            catch (Exception error) { Console.Error.WriteLine("Native startup failed: " + error); }
            Environment.Exit(completion.Task.IsCompleted ? completion.Task.Result : 1);
        }
        if (args.Length > 0 && args[0] == "--headless-ui")
        {
            // 里程碑必须只在真正进入该分支时记录；否则普通启动的追踪里会混入不存在的阶段。
            Startup?.Mark("avalonia.headless", "headless preview");
            SelfTestMode = true;
            Environment.Exit(SelfTest.HeadlessUiRunner.RunProcess(args.Skip(1).ToArray()));
        }

        // --selftest：无头自测入口，不依赖交互式桌面会话即可验证界面与 mock 会话
        if (Array.IndexOf(args, "--selftest") >= 0)
        {
            Startup?.Mark("avalonia.selftest", "headless selftest");
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
        try
        {
            // 启动前读取内存优化开关：软件渲染必须在 Avalonia 初始化之前决定。
            MemoryProfile.Apply(new Fufucode.Core.AppSettingsStore(startupOptions.CreateAppDataLayout()).Current.Performance);
        }
        catch (Exception error)
        {
            Console.WriteLine("读取内存优化设置失败：" + error.Message);
        }
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
                .WithAppTypography()
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
    {
        var builder = AppBuilder.Configure(() => new App(options))
            .UsePlatformDetect()
            .WithInterFont()
            .WithAppTypography()
            .LogToTrace();
        // 低内存模式：不初始化 ANGLE/D3D，不加载显卡驱动的大块模块。
        if (MemoryProfile.SoftwareRendering)
        {
            builder = builder.With(new Win32PlatformOptions
            {
                RenderingMode = new[] { Win32RenderingMode.Software },
            });
        }
        return builder;
    }
}
