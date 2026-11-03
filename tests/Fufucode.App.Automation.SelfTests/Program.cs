using Fufucode.App.Features.Automation;
using Fufucode.Plugins.AutoDev.Engine.Harnesses;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.Threading;
using Fufucode.App;
using Fufucode.App.Integration;
using Fufucode.App.Infrastructure;
using Fufucode.App.SelfTest;
using Fufucode.Core;
using Fufucode.Core.Plugins;
using Fufucode.Plugins.AutoDev;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Themes.Fluent;

namespace Fufucode.App.Automation.SelfTests;

/// <summary>
/// 自动开发自测的独立入口。验证内建入口归属，并保留旧控制协议与页面的兼容测试。
/// </summary>
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // 被自测内部当作最小协议 fixture 子进程再次启动。
        if (args.Length >= 2 && args[0] == "--automation-protocol")
        {
            AutomationControlSelfTest.ProtocolAsync(args).GetAwaiter().GetResult();
            return 0;
        }

        BatchHarnessAdapters.Register();

        _ = typeof(Fufucode.App.MainWindow).Assembly;
        var root = Path.Combine(Path.GetTempPath(),
            "fufucode-automation-selftest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Fufucode.App.Program.EnterSelfTestMode(root);

        var failures = new List<string>();
        var completion = new TaskCompletionSource<int>();
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                // 空插件目录下仍报告内建功能，旧插件页面不再占据宿主页签。
                if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime0)
                    lifetime0.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                var shell = new MainWindow(AppOptions.Mock);
                try
                {
                    shell.Show();
                    var status = shell.CustomizationForTesting.StatusOf("automation");
                    var builtIn = status.Detail.Contains("内建") && status.PageId != "marketplace" &&
                        PluginMarketplaceCatalog.Find("automation.auto-dev") is null &&
                        !shell.PageAvailableForTesting("automation");
                    Console.WriteLine(builtIn
                        ? "PASS 自动开发使用内建入口，不再挂载旧插件页面"
                        : "FAIL 自动开发仍依赖旧插件页面或市场安装状态");
                    if (!builtIn) failures.Add("自动开发内建入口归属错误");
                }
                finally { shell.Close(); }
                BatchHarnessAdapters.Register();
                await AutomationControlSelfTest.RunAsync(
                    name => Console.WriteLine("PASS " + name),
                    name => { failures.Add(name); Console.WriteLine("FAIL " + name); });
                completion.TrySetResult(failures.Count == 0 ? 0 : 1);
            }
            catch (Exception error)
            {
                Console.WriteLine("自动化自测异常：" + error);
                completion.TrySetResult(1);
            }
            finally
            {
                if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
                    lifetime.Shutdown();
            }
        }, DispatcherPriority.Background);

        try
        {
            // 测试程序集自身不参与 App 的 XAML 编译，必须显式以 App 程序集作为资源基，
            // 否则 App.Initialize 找不到预编译的 App.axaml。
            // App 的 ApplicationDefinition 资源只能由入口程序集加载；测试用同等的
            // 主题组合（Fluent + 产品样式），页面与窗口的 DynamicResource 键保持一致。
            AppBuilder.Configure<TestApp>()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .UseSkia().WithInterFont().WithAppTypography()
                .StartWithClassicDesktopLifetime([]);
        }
        catch (Exception error)
        {
            Console.WriteLine("无头生命周期异常：" + error);
            if (!completion.Task.IsCompleted) completion.TrySetResult(1);
        }

        var code = completion.Task.IsCompleted ? completion.Task.Result : 1;
        Console.WriteLine($"Automation page failures: {failures.Count}");
        return code;
    }

    private sealed class TestApp : Application
    {
        public override void Initialize()
        {
            AppTypography.Initialize();
            Styles.Add(new FluentTheme());
            Styles.Add(new StyleInclude(new Uri("avares://Fufucode.App/"))
            {
                Source = new Uri("avares://Fufucode.App/Styles/FufucodeTheme.axaml"),
            });
            Styles.Add(new StyleInclude(new Uri("avares://Fufucode.App/"))
            {
                Source = new Uri("avares://Fufucode.App/Styles/WorkbenchStyles.axaml"),
            });
        }
    }
}
