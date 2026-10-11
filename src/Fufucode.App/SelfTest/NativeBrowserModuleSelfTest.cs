using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Fufucode.App.Infrastructure;
using Fufucode.App.ViewModels;
using Fufucode.App.Views;

namespace Fufucode.App.SelfTest;

internal static class NativeBrowserModuleSelfTest
{
    public static async Task<int> RunAsync(MainWindow window)
    {
        var failures = 0;
        void Check(bool result, string name)
        { Console.WriteLine((result ? "PASS: " : "FAIL: ") + name); if (!result) failures++; }
        Console.WriteLine("Native module data root: " + Program.SelfTestDataRoot);
        var file = Path.Combine(Program.SelfTestDataRoot!, "browser-module.html");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "<!doctype html><title>NATIVE_MODULE_OK</title><h1>Native module acceptance</h1>");
        await Until(() => window.DataContext is MainViewModel);
        var vm = (MainViewModel)window.DataContext!;
        vm.Input.DraftText = "preserved native module draft";
        window.OpenDockPage("browser", DockZone.Right);
        var browser = (BrowserView)window.PageForTesting("browser");
        browser.ViewModel.AddressText = file;
        browser.ViewModel.NavigateCommand.Execute(null);
        await Until(() => browser.HostForTesting.IsReady && browser.ViewModel.DocumentTitle == "NATIVE_MODULE_OK");
        Check(browser.HostForTesting.IsReady, "Real WebView2 controller opens local HTML");
        var second = Path.Combine(Program.SelfTestDataRoot!, "browser-second.html");
        File.WriteAllText(second, "<!doctype html><title>NATIVE_HISTORY_OK</title><h1>Second document</h1>");
        browser.ViewModel.AddressText = second;
        browser.ViewModel.NavigateCommand.Execute(null);
        await Until(() => browser.ViewModel.DocumentTitle == "NATIVE_HISTORY_OK" && browser.ViewModel.CanGoBack);
        browser.ViewModel.BackCommand.Execute(null);
        await Until(() => browser.ViewModel.DocumentTitle == "NATIVE_MODULE_OK" && browser.ViewModel.CanGoForward);
        Check(browser.ViewModel.CanGoForward, "Native browser back returns to previous document");
        browser.ViewModel.ForwardCommand.Execute(null);
        await Until(() => browser.ViewModel.DocumentTitle == "NATIVE_HISTORY_OK");
        Check(browser.ViewModel.DocumentTitle == "NATIVE_HISTORY_OK", "Native browser forward restores next document");
        browser.ViewModel.BackCommand.Execute(null);
        await Until(() => browser.ViewModel.DocumentTitle == "NATIVE_MODULE_OK");
        browser.ViewModel.ReloadCommand.Execute(null);
        await Until(() => !browser.ViewModel.IsLoading && browser.ViewModel.DocumentTitle == "NATIVE_MODULE_OK");
        Check(browser.HostForTesting.IsReady, "Native browser refresh keeps its document and controller");
        foreach (var zone in Enum.GetValues<DockZone>())
        {
            window.MoveDockPage("browser", zone);
            await Task.Delay(150);
            Check(ReferenceEquals(browser, window.PageForTesting("browser")) && browser.HostForTesting.IsReady &&
                browser.ViewModel.DocumentTitle == "NATIVE_MODULE_OK", "Native controller survives page move " + zone);
        }
        window.BrowserModuleForTesting.IsEnabled = false;
        await Task.Delay(150);
        Check(browser.HostForTesting.IsDisposedForTesting && !browser.HostForTesting.IsReady && TopLevel.GetTopLevel(browser) is null,
            "Module disable releases native controller and detaches page");
        Check(vm.Input.DraftText == "preserved native module draft" &&
            window.ActionsForTesting.GetDisabledReason("browser")?.Contains("已停用") == true &&
            window.GetVisualDescendants().OfType<DockRegionView>().All(dock => dock.Catalog.All(page => page.Id != "browser")),
            "Core draft and all module entry points remain consistent");
        window.BrowserModuleForTesting.IsEnabled = true;
        var restored = (BrowserView)window.PageForTesting("browser");
        await Until(() => restored.HostForTesting.IsReady && restored.ViewModel.DocumentTitle == "NATIVE_MODULE_OK");
        Check(!ReferenceEquals(restored, browser) && window.DockLayoutForTesting.Find("browser") == DockZone.Bottom,
            "Reactivation creates fresh native host and restores document and zone");
        window.BrowserModuleForTesting.IsEnabled = false;
        window.BrowserModuleForTesting.IsEnabled = true;
        var pending = (BrowserView)window.PageForTesting("browser");
        window.BrowserModuleForTesting.IsEnabled = false;
        await Task.Delay(1500);
        Check(pending.HostForTesting.IsDisposedForTesting && !pending.HostForTesting.IsReady,
            "Immediate disable cannot resurrect a disposed native host");
        Console.WriteLine("Native module failures: " + failures);
        return failures == 0 ? 0 : 1;
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(25);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Native browser state did not settle.");
            await Task.Delay(100);
        }
    }
}
