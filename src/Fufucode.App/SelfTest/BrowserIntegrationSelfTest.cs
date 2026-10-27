using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Fufucode.App.Features.BrowserExperience;
using Fufucode.App.Features.Shortcuts;
using Fufucode.App.Infrastructure;
using Fufucode.App.Views;

namespace Fufucode.App.SelfTest;

internal static class BrowserIntegrationSelfTest
{
    internal static async Task RunAsync(Action<string> pass, Action<string> fail)
    {
        void Check(bool good,string label){if(good)pass("BrowserIntegration: "+label);else fail("BrowserIntegration: "+label);}
        var previous=Program.SelfTestDataRoot;Program.SelfTestDataRoot=Path.Combine(Path.GetTempPath(),"fufucode-browser-integration-"+Guid.NewGuid().ToString("N"));
        var window=new MainWindow{Width=1440,Height=900};window.Show();
        try
        {
            window.OpenDockPage("browser",DockZone.Right);await Task.Delay(80);window.UpdateLayout();
            var view=(BrowserView)window.PageForTesting("browser");
            Check(BrowserSurfaceActions.Catalog.All(a=>window.ShortcutsForTesting.Rows.Any(r=>r.Id==a.Id)),"all 13 actions are in the actual shared settings catalog");
            Check(BrowserSurfaceActions.Catalog.All(a=>ShortcutDefaults.ScopeOf(a.Id)==ShortcutScope.Browser),"all browser actions share the browser scope");
            Check(ShortcutDefaults.DefaultGestures(BrowserSurfaceActions.CopyAddress).Count==0,"copy-address has no artificial None gesture");
            Check(window.ActionsForTesting.GetDisabledReason(BrowserSurfaceActions.Back) is not null,"real missing history is unavailable");
            Check(view.NativeShortcutHandler!(0x54,KeyModifiers.Control),"native accelerator resolves Ctrl+T through the shared table");
            Dispatcher.UIThread.RunJobs(); await Task.Delay(80); Dispatcher.UIThread.RunJobs();
            Check(view.ViewModel.Tabs.Count==2,"resolved native new-tab creates an actual independent tab");
            window.ShortcutsForTesting.SetBinding(BrowserSurfaceActions.NewTab,"Ctrl+Alt+T");
            Check(view.NativeShortcutHandler(0x54,KeyModifiers.Control),"old native default is suppressed after rebinding");
            Dispatcher.UIThread.RunJobs(); await Task.Delay(80); Dispatcher.UIThread.RunJobs();
            Check(view.ViewModel.Tabs.Count==2,"old Ctrl+T no longer creates a tab");
            view.NativeShortcutHandler(0x54,KeyModifiers.Control|KeyModifiers.Alt);
            Dispatcher.UIThread.RunJobs(); await Task.Delay(80); Dispatcher.UIThread.RunJobs();
            Check(view.ViewModel.Tabs.Count==3,"configured native gesture creates a tab");
            window.ShortcutsForTesting.SetBinding(BrowserSurfaceActions.NewTab,null);
            view.NativeShortcutHandler(0x54,KeyModifiers.Control);view.NativeShortcutHandler(0x54,KeyModifiers.Control|KeyModifiers.Alt);
            Dispatcher.UIThread.RunJobs(); await Task.Delay(80); Dispatcher.UIThread.RunJobs();
            Check(view.ViewModel.Tabs.Count==3,"disabled native action has no default or previous-binding fallback");
            Check(!view.NativeShortcutHandler(0x43,KeyModifiers.Control)&&!view.NativeShortcutHandler(0x58,KeyModifiers.None),"page copy and normal typing are not captured as app actions");
            window.ShortcutsForTesting.SetBinding(BrowserSurfaceActions.NewTab,"Ctrl+Alt+T");
            var floatingId=window.FloatDockPage("browser")!;await Task.Delay(80);
            Check(window.FloatingDockWindowForTesting(floatingId) is not null,"browser uses a real common floating container");
            view.NativeShortcutHandler(0x54,KeyModifiers.Control|KeyModifiers.Alt);await Task.Delay(80);
            Dispatcher.UIThread.RunJobs();
            Check(view.ViewModel.Tabs.Count==4,"floating browser resolves the same binding against its own view");
            window.DockFloatingRegion(floatingId,DockZone.Right);await Task.Delay(80);
            Check(ReferenceEquals(view,window.PageForTesting("browser")),"redocking preserves the browser view and all tab identities");
            window.BrowserModuleForTesting.IsEnabled=false;await Task.Delay(80);
            Check(window.ActionsForTesting.GetDisabledReason(BrowserSurfaceActions.NewTab) is not null,"disabled module reports unavailable actions");
            window.BrowserModuleForTesting.IsEnabled=true;await Task.Delay(80);
            var restored=(BrowserView)window.PageForTesting("browser");
            Check(restored.NativeShortcutHandler is not null&&restored.ViewModel.Tabs.Count==4,"module restoration reconnects bindings and restores four tabs");
            Check(BrowserVirtualKeys.Of(Key.OemPlus)==187&&BrowserVirtualKeys.Of(Key.D0)==48&&BrowserVirtualKeys.Of(Key.F12)==123,"native zoom/reset/devtools keys map correctly");
        }
        finally{window.Close();await Task.Delay(120);Program.SelfTestDataRoot=previous;}
    }
}
