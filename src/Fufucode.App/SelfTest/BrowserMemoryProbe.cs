using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Fufucode.App.Infrastructure;
using Fufucode.App.ViewModels;
using Fufucode.App.Views;
using Fufucode.App.Features.BrowserExperience;

namespace Fufucode.App.SelfTest;

/// <summary>
/// 真实 WebView2 内存曲线探针：1/3/10 页签、热池收紧、后台 3/6/11 分钟、恢复。
/// 输出每个阶段子进程 PID/PrivateMemorySize64 与主进程占用。
/// </summary>
internal static class BrowserMemoryProbe
{
    public static async Task<int> RunAsync(MainWindow window)
    {
        var root = Program.SelfTestDataRoot!;
        Directory.CreateDirectory(root);
        var page = Path.Combine(root, "memory-probe.html");
        var body = new StringBuilder("<!doctype html><title>MEMORY_PROBE</title><body>");
        for (var i = 0; i < 400; i++)
            body.Append("<p>memory probe line ").Append(i).Append(" — ").Append(new string('x', 60)).Append("</p>");
        body.Append("</body>");
        File.WriteAllText(page, body.ToString());

        await Until(() => window.DataContext is MainViewModel);
        window.OpenDockPage("browser", DockZone.Right);
        var browser = (BrowserView)window.PageForTesting("browser");
        browser.MemoryGovernorForTesting.HotLimit = 10;

        browser.ViewModel.AddressText = page;
        browser.ViewModel.NavigateCommand.Execute(null);
        await Until(() => browser.HostForTesting.IsReady && browser.ViewModel.DocumentTitle == "MEMORY_PROBE");
        Measure(browser, "tabs-1");

        for (var index = 2; index <= 10; index++)
        {
            browser.ViewModel.NewTabCommand.Execute(null);
            browser.ViewModel.AddressText = page;
            browser.ViewModel.NavigateCommand.Execute(null);
            await Until(() => browser.HostForTesting.IsReady && browser.ViewModel.DocumentTitle == "MEMORY_PROBE");
            if (index is 3 or 10) Measure(browser, "tabs-" + index);
        }

        browser.MemoryGovernorForTesting.HotLimit = 3;
        await browser.RunLifecycleForTestingAsync();
        await Task.Delay(1000);
        Measure(browser, "hot-limit-3");

        await browser.RunLifecycleForTestingAsync(DateTimeOffset.UtcNow.AddMinutes(-3));
        await Task.Delay(800);
        Measure(browser, "background-3m");

        await browser.RunLifecycleForTestingAsync(DateTimeOffset.UtcNow.AddMinutes(-6));
        await Task.Delay(800);
        Measure(browser, "background-6m");

        await browser.RunLifecycleForTestingAsync(DateTimeOffset.UtcNow.AddMinutes(-11));
        await Task.Delay(800);
        Measure(browser, "background-11m");

        await browser.RunLifecycleForTestingAsync(null);
        var active = browser.ViewModel.ActiveTab;
        await browser.ActivateTabForTestingAsync(active);
        await Task.Delay(1200);
        Measure(browser, "restored");

        Console.WriteLine("MEMORY_PROBE_DONE");
        return 0;
    }

    private static void Measure(BrowserView browser, string label)
    {
        var snapshot = browser.SampleProcessesForTesting();
        var main = Process.GetCurrentProcess().PrivateMemorySize64 / 1024 / 1024;
        var discarded = browser.ViewModel.Tabs.Count(tab => tab.IsDiscarded);
        var suspended = browser.ViewModel.Tabs.Count(tab => tab.Lifecycle == BrowserTabLifecycle.Suspended);
        Console.WriteLine(
            $"MEASURE {label} tabs={browser.ViewModel.Tabs.Count} discarded={discarded} suspended={suspended} " +
            $"processes={snapshot.ProcessCount} total={snapshot.TotalPrivateBytes / 1024 / 1024}MB " +
            $"browser={snapshot.BrowserBytes / 1024 / 1024}MB renderer={snapshot.RendererBytes / 1024 / 1024}MB " +
            $"gpu={snapshot.GpuBytes / 1024 / 1024}MB main={main}MB exact={snapshot.IsExact} " +
            $"pids={string.Join(",", snapshot.ProcessIds)}");
        Console.Out.Flush();
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Memory probe state did not settle.");
            await Task.Delay(100);
        }
    }
}
