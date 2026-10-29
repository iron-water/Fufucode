using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fufucode.App.ViewModels;
using Fufucode.Core.Security;

namespace Fufucode.App.SelfTest;

/// <summary>在隔离的真实原生窗口里操作安全中心并留存渲染证据。</summary>
internal static class NativeSecurityCenterSelfTest
{
    public static async Task<int> RunAsync(MainWindow window)
    {
        var evidence = Path.GetFullPath("tmp/security-center-evidence");
        Directory.CreateDirectory(evidence);
        await Task.Delay(1500);
        var vm = (MainViewModel)window.DataContext!;
        await window.NavigateForTestingAsync(AppPage.Settings);
        await Task.Delay(500);
        vm.Settings.ThemeIndex = 2;
        vm.Settings.SelectCategory("security");
        window.WindowState = WindowState.Normal;
        window.Width = 1440;
        window.Height = 1000;
        await Settle();
        T Find<T>(string id) where T : Control => window.GetVisualDescendants().OfType<T>()
            .FirstOrDefault(c => AutomationProperties.GetAutomationId(c) == id && c.IsEffectivelyVisible)
            ?? throw new InvalidOperationException("Missing native control: " + id + " | page=" + vm.CurrentPage +
                " category=" + vm.Settings.SelectedCategory?.Id + " status=" + vm.Settings.Security.Status +
                " visible security views=" + string.Join(",", window.GetVisualDescendants().OfType<Fufucode.App.Views.SecurityCenterView>().Select(v => $"{v.Page}:{v.IsEffectivelyVisible}:{v.DataContext?.GetType().Name}")));
        var toggle = Find<ToggleSwitch>("security-behavior");
        toggle.IsChecked = false;
        if (new SecurityPolicyStore(vm.Settings.Security.DataRoot!).Current.BehaviorReviewEnabled)
            throw new InvalidOperationException("Native toggle did not save.");
        toggle.IsChecked = true;
        await Screenshot("overview-dark.png");
        vm.Settings.OpenPage("security.commands");
        await Settle();
        Find<TextBox>("security-command-pattern").Text = "git push";
        Find<Button>("security-add-command").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Settle();
        if (new SecurityPolicyStore(vm.Settings.Security.DataRoot!).Current.CommandRules.FirstOrDefault()?.Pattern != "git push")
            throw new InvalidOperationException("Native command rule did not save.");
        await Screenshot("commands-dark.png");
        vm.Settings.OpenPage("security.files");
        await Settle();
        await Screenshot("files-dark.png");
        vm.Settings.GoBack();
        window.Width = 900;
        await Settle();
        await Screenshot("overview-narrow.png");
        window.Width = 1440;
        vm.Settings.ThemeIndex = 1;
        vm.Settings.GoBack();
        var scroller = window.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault(x => x.Name == "SettingsScroller");
        if (scroller is not null) scroller.Offset = default;
        await Task.Delay(1500);
        await Settle();
        await Screenshot("overview-light.png");
        Console.WriteLine("PASS native security navigation, toggle persistence, add command rule, dark/light/narrow renders.");
        Console.WriteLine("Evidence: " + evidence);
        return 0;

        async Task Settle()
        {
            await Task.Delay(350);
            await Dispatcher.Yield(DispatcherPriority.Background);
            window.UpdateLayout();
        }
        async Task Screenshot(string file)
        {
            await Settle();
            using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height));
            bitmap.Render(window);
            bitmap.Save(Path.Combine(evidence, file), PngBitmapEncoderOptions.Default);
            Console.WriteLine("SCREENSHOT " + file);
        }
    }
}
