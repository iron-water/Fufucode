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
using Fufucode.App.SelfTest;
using Fufucode.Core;
using Fufucode.Core.Plugins;
using Fufucode.Plugins.AutoDev;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Themes.Fluent;

namespace Fufucode.App.Automation.SelfTests;

/// <summary>
/// 自动开发自测的独立入口。App 的发布程序不再静态引用自动开发插件；
/// 本组件同时引用 App 与插件，验证页面挂载、停靠和真实执行链路。
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

        var root = Path.Combine(Path.GetTempPath(),
            "fufucode-automation-selftest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Fufucode.App.Program.EnterSelfTestMode(root);

        // 自测通过工厂安装插件，模拟“已安装并启用”；宿主随后从同一数据根加载。
        var layout = new AppDataLayout(root);
        PluginPackageManager.RegisterPackageFactory(AutoDevPluginPackage.PluginId, () => new AutoDevPluginPackage());
        var registry = new PluginToolRegistry(new PluginCapabilityGateway(new PluginStateStore(layout)));
        var manager = new PluginPackageManager(layout, registry);
        if (!manager.HasPhysicalPayload(AutoDevPluginPackage.PluginId))
            manager.InstallPackage(AutoDevPluginPackage.PluginId);
        manager.SetPackageEnabled(AutoDevPluginPackage.PluginId, true);

        var failures = new List<string>();
        var completion = new TaskCompletionSource<int>();
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                // 先验证插件页面确实挂进宿主：未安装时容器为空，安装后控制器必须存在。
                if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime0)
                    lifetime0.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                var shell = new MainWindow(AppOptions.Mock);
                try
                {
                    shell.Show();
                    var host = shell.PageForTesting("automation") as Features.Plugins.AutomationPageHost;
                    Console.WriteLine(host?.Controller is not null
                        ? "PASS 插件页面已挂载到宿主页签"
                        : "FAIL 插件页面未挂载到宿主页签");
                    if (host?.Controller is null) failures.Add("插件页面未挂载到宿主页签");
                }
                finally { shell.Close(); }
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
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true })
                .WithInterFont()
                .StartWithClassicDesktopLifetime([]);
        }
        catch (Exception error)
        {
            Console.WriteLine("无头生命周期异常：" + error);
            if (!completion.Task.IsCompleted) completion.TrySetResult(1);
        }

        var code = completion.Task.IsCompleted ? completion.Task.Result : 1;
        Console.WriteLine($"Automation page failures: {failures.Count}");
        manager.DisposeAsync().AsTask().GetAwaiter().GetResult();
        return code;
    }

    private sealed class TestApp : Application
    {
        public override void Initialize()
        {
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
