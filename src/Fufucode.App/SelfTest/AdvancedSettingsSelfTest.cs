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
using Fufucode.App.Views;
using Fufucode.Core;
using Fufucode.Core.Shell;

namespace Fufucode.App.SelfTest;

/// <summary>设置 → 高级：一级分类可直达，真实控件写入执行端读取的 settings.json。</summary>
public static class AdvancedSettingsSelfTest
{
    public static async Task RunAsync(Action<string> pass, Action<string> fail)
    {
        void Check(bool condition, string name)
        { if (condition) pass("高级设置：" + name); else fail("高级设置：" + name); }
        var root = Path.GetFullPath("tmp/advanced-settings-ui-tests/" + Guid.NewGuid().ToString("N"));
        var evidence = Path.GetFullPath("tmp/advanced-settings-evidence");
        Directory.CreateDirectory(evidence);
        Window? window = null;
        try
        {
            var store = new AppSettingsStore(new AppDataLayout(root));
            var vm = new SettingsPageViewModel();
            vm.Attach(new(store, true, () => null, null, null, root, null, () => null, () => null,
                () => [], () => null, () => root, _ => Task.CompletedTask, _ => Task.CompletedTask,
                () => "unavailable", () => "unavailable"));
            Check(vm.FilteredCategories.Any(c => c.Id == "advanced" && c.Title == "高级" && !c.CanMoveTier),
                "「高级」是固定的一级菜单");
            vm.SelectCategory("advanced");
            Check(vm.ShowAdvanced && !vm.ShowSecurity, "选中高级只显示高级页");

            var page = new SettingsPage { DataContext = vm };
            window = new Window { Content = page, Width = 1280, Height = 900 };
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.Background);
            window.UpdateLayout();
            T Find<T>(string id) where T : Control => page.GetVisualDescendants().OfType<T>()
                .Single(control => AutomationProperties.GetAutomationId(control) == id);

            var view = page.GetVisualDescendants().OfType<AdvancedSettingsView>().Single();
            Check(view.IsEffectivelyVisible, "高级页真实渲染");
            var shell = Find<ComboBox>("advanced-fumo-shell");
            var selected = shell.SelectedItem as AdvancedSettingsViewModel.ShellOption;
            Check(selected?.Id == CommandShells.DefaultId, "未设置时下拉框显示默认解释器（PowerShell 7）");
            var effective = Find<TextBlock>("advanced-fumo-shell-effective").Text ?? "";
            var resolved = CommandShells.Resolve(null, out _);
            Check(resolved is not null && effective.Contains(resolved.Executable), "行内显示实际生效的可执行文件");
            Check(store.Current.Fumo is null, "只浏览不写盘" + (store.Current.Fumo is { } written ? "（写入了 " + written + "）" : ""));
            Save(window, "advanced-default.png");

            var cmd = ((System.Collections.Generic.IEnumerable<AdvancedSettingsViewModel.ShellOption>)shell.ItemsSource!)
                .Single(option => option.Id == CommandShells.Cmd);
            shell.SelectedItem = cmd;
            Check(new AppSettingsStore(new AppDataLayout(root)).Current.Fumo?.Shell == CommandShells.Cmd, "选择 cmd 写入 settings.json");
            Check(FumoRuntimeSettingsProbe(store) == CommandShells.Cmd, "执行端下一次解析即使用新解释器");

            Find<ToggleSwitch>("advanced-fumo-shell-profile").IsChecked = true;
            Check(store.Current.Fumo?.LoadShellProfile == true, "加载配置文件开关落盘");
            Find<NumericUpDown>("advanced-fumo-command-timeout").Value = 900;
            Check(store.Current.Fumo?.CommandTimeoutSeconds == 900, "默认超时落盘");
            Save(window, "advanced-changed.png");

            Find<Button>("advanced-fumo-reset").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Dispatcher.Yield(DispatcherPriority.Background);
            Check(store.Current.Fumo is null && (shell.SelectedItem as AdvancedSettingsViewModel.ShellOption)?.Id == CommandShells.DefaultId,
                "恢复默认清除设置并回到 PowerShell 7");

            vm.SearchText = "pwsh";
            Check(vm.FilteredCategories.Any(c => c.Id == "advanced"), "搜索 pwsh 命中高级");
            vm.SearchText = "";
            window.Width = 520;
            await Dispatcher.Yield(DispatcherPriority.Background);
            window.UpdateLayout();
            Check(page.FindControl<ListBox>("CompactSettingsCategories")!.IsVisible && view.IsEffectivelyVisible, "窄窗口可访问高级页");
            Save(window, "advanced-narrow.png");
        }
        finally { window?.Close(); if (Directory.Exists(root)) Directory.Delete(root, true); }

        void Save(Visual target, string name)
        {
            using var bitmap = new RenderTargetBitmap(new PixelSize(Math.Max(1, (int)target.Bounds.Width), Math.Max(1, (int)target.Bounds.Height)));
            bitmap.Render(target);
            bitmap.Save(Path.Combine(evidence, name), PngBitmapEncoderOptions.Default);
        }
    }

    private static string? FumoRuntimeSettingsProbe(AppSettingsStore store)
    {
        FumoRuntimeSettings.Use(() => store.Current.Fumo);
        return FumoRuntimeSettings.ResolveDefaultShell(out _)?.Kind.Id;
    }
}
