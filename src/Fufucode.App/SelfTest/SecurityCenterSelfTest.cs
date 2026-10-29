using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fufucode.App.ViewModels;
using Fufucode.App.Views;
using Fufucode.Core;
using Fufucode.Core.Security;

namespace Fufucode.App.SelfTest;

public static class SecurityCenterSelfTest
{
    public static async Task RunAsync(Action<string> pass, Action<string> fail)
    {
        void Check(bool condition, string name)
        { if (condition) pass("安全中心：" + name); else fail("安全中心：" + name); }
        var root = Path.GetFullPath("tmp/security-center-ui-tests/" + Guid.NewGuid().ToString("N"));
        Window? window = null;
        try
        {
            var store = new AppSettingsStore(new AppDataLayout(root));
            var vm = new SettingsPageViewModel();
            vm.Attach(new(store, true, () => null, null, null, root, null, () => null, () => null,
                () => [], () => null, () => root, _ => Task.CompletedTask, _ => Task.CompletedTask,
                () => "unavailable", () => "unavailable"));
            vm.SelectCategory("security");
            Check(vm.ShowSecurity && vm.Categories.Any(c => c.Id == "security" && !c.CanMoveTier), "固定一级分类可直达");
            Check(vm.Security.Settings.Enabled && File.Exists(Path.Combine(root, "security.json")), "默认开启并实际创建策略文件");
            Fufucode.App.Integration.ApprovalPolicy.Configure(root);
            var securityRequest = new ExtensionUiRequest(Guid.NewGuid(), "security-required", "confirm",
                System.Text.Json.JsonSerializer.SerializeToElement(new { toolName = "run_command", securityRequired = true }),
                DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
            Check(!Fufucode.App.Integration.ApprovalPolicy.Remember(securityRequest, root) &&
                !Fufucode.App.Integration.ApprovalPolicy.IsAlwaysAllowed(securityRequest, root), "安全中心询问不接受旧工具级始终允许");
            var page = new SettingsPage { DataContext = vm };
            window = new Window { Content = page, Width = 1280, Height = 900 };
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.Background);
            window.UpdateLayout();
            T Find<T>(string id) where T : Control => page.GetVisualDescendants().OfType<T>()
                .Single(control => AutomationProperties.GetAutomationId(control) == id);
            Find<ToggleSwitch>("security-behavior").IsChecked = false;
            Check(!new SecurityPolicyStore(root).Current.BehaviorReviewEnabled, "真实开关写入执行端策略真源");
            Find<ToggleSwitch>("security-behavior").IsChecked = true;
            vm.OpenPage("security.commands");
            await Dispatcher.Yield(DispatcherPriority.Background);
            window.UpdateLayout();
            Find<TextBox>("security-command-pattern").Text = "git push";
            Find<Button>("security-add-command").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(new SecurityPolicyStore(root).Current.CommandRules.FirstOrDefault() is { Pattern: "git push", Action: SecurityAction.Ask }, "命令规则添加并落盘");
            vm.GoBack();
            vm.OpenPage("security.files");
            await Dispatcher.Yield(DispatcherPriority.Background);
            window.UpdateLayout();
            Find<TextBox>("security-file-pattern").Text = "**/private/**";
            Find<Button>("security-add-file").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(new SecurityPolicyStore(root).Current.FileRules.First().Pattern == "**/private/**", "新增文件规则优先级最高");
            vm.OpenPage("security.sensitive");
            await Dispatcher.Yield(DispatcherPriority.Background);
            window.UpdateLayout();
            var before = new SecurityPolicyStore(root).Current.SecretRules.Count;
            Find<TextBox>("security-secret-name").Text = "无效规则";
            Find<TextBox>("security-secret-pattern").Text = "[";
            Find<Button>("security-add-secret").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(vm.Security.HasError && new SecurityPolicyStore(root).Current.SecretRules.Count == before, "无效正则保留原配置并显示错误");
            foreach (var id in new[] { "security.network", "security.review", "security.audit" })
            {
                vm.OpenPage(id);
                await Dispatcher.Yield(DispatcherPriority.Background);
                window.UpdateLayout();
                Check(page.GetVisualDescendants().OfType<SecurityCenterView>().Single().IsVisible, id + " 子页真实渲染");
            }
            vm.GoBack();
            window.Width = 520;
            await Dispatcher.Yield(DispatcherPriority.Background);
            window.UpdateLayout();
            Check(page.FindControl<ListBox>("CompactSettingsCategories")!.IsVisible, "窄窗口可访问安全中心");
            vm.SearchText = "审计";
            Check(vm.FilteredCategories.Any(c => c.Id == "security"), "搜索审计命中安全中心");
        }
        finally { window?.Close(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
