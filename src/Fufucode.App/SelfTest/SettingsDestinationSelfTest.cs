using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fufucode.App.ViewModels;
using Fufucode.App.Views;
using Fufucode.Core;

namespace Fufucode.App.SelfTest;

/// <summary>Settings destination checks use actual persistence, injected RPC state, and real headless controls.</summary>
public static class SettingsDestinationSelfTest
{
    public static async Task RunAsync(Action<string> pass, Action<string> fail)
    {
        void Check(bool value, string description)
        {
            if (value) pass("W5-R3 设置：" + description);
            else fail("W5-R3 设置：" + description);
        }

        var root = Path.Combine(Path.GetTempPath(), "fufucode-settings-destination-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var previousTheme = Application.Current?.RequestedThemeVariant;
        Window? window = null;
        try
        {
            var store = new AppSettingsStore(new AppDataLayout(Path.Combine(root, "settings")));
            var vm = new SettingsPageViewModel();
            vm.Attach(Context(store));
            Check(vm.OtherHubItems.Any(category => category.Id == "modules") && vm.Categories.Any(category => category.Id == "about") &&
                vm.ShowAppearance && !vm.HasUnsavedChanges,
                "一级侧栏与「其他」收纳桶共同覆盖内建模块与关于，初始没有未保存草稿");
            Check(vm.Categories.Any(category => category.Id == "appearance") &&
                vm.OtherHubItems.Any(category => category.Id == "functions"),
                "一级侧栏包含外观，会话与消息收纳在「其他」");
            Check(vm.OtherHubItems.Any(category => category.Id == "config"),
                "配置文件页收纳在「其他」");
            Check(!vm.Categories.Any(category => category.Id is "layout" or "details") &&
                vm.WorkbenchSectionId == "appearance" && vm.ShowWorkbenchSection,
                "不再保留 layout/details 旧分类，外观页使用分区控件");
            Check(vm.WorkbenchSectionId == "appearance" && vm.ShowWorkbenchSection,
                "外观页使用统一设置分区，不再维护第二套可见性谓词");
            vm.SelectCategory("config");
            Check(vm.WorkbenchSectionId == "config" && vm.ShowConfigSection && vm.ShowWorkbenchSection,
                "配置文件是独立左栏页面，不再依赖二级导航");
            vm.SelectedCategory = vm.Categories.Single(category => category.Id == "appearance");
            vm.SelectedCategory = vm.Categories.Single(category => category.Id == "appearance");
            vm.SearchText = "代理";
            Check(vm.FilteredCategories.Count == 1 && vm.ShowNetwork && vm.ShowProxyRow && !vm.ShowEndpointRow,
                "搜索同时过滤分类与不匹配控件行");
            vm.SearchText = "does-not-exist";
            Check(!vm.HasSearchResults && vm.SelectedCategory is null && !vm.ShowNetwork, "无匹配设置显示明确空状态");
            vm.SearchText = string.Empty;
            vm.ThemeIndex = 2;
            Check(store.Current.Theme == "dark" && vm.AppearanceState == SettingSaveState.Applied && !vm.HasUnsavedChanges &&
                vm.AppearanceStateText == "已保存 · 当前已应用", "主题实际落盘并立即应用，自动保存不计 dirty");

            vm.ProxyInput = "http://127.0.0.1:8080";
            Check(vm.HasProxyDraft && vm.HasUnsavedChanges && store.Current.ProxyUrl is null, "编辑代理只产生草稿，不伪保存");
            vm.SaveProxyCommand.Execute(null);
            Check(store.Current.ProxyUrl == "http://127.0.0.1:8080" && !vm.HasProxyDraft && vm.NetworkState == SettingSaveState.RestartRequired &&
                vm.NetworkStateText == "已保存 · 等待重启会话", "代理保存与当前应用分开，等待重启");
            vm.EndpointFormOpen = true;
            vm.EndpointProviderName = "draft-provider";
            vm.EndpointFormOpen = false;
            Check(vm.HasEndpointDraft && vm.HasUnsavedChanges, "收起端点表单仍保留真实 dirty 状态");
            var discarded = false;
            vm.DraftsDiscarded += (_, _) => discarded = true;
            vm.SetEndpointKeyDraftPresence(true);
            vm.DiscardUnsavedChanges();
            Check(discarded && !vm.HasUnsavedChanges && store.Current.ProxyUrl == "http://127.0.0.1:8080" && store.Current.Endpoint is null,
                "丢弃仅恢复草稿，已保存设置不受影响，清密码事件已发出");
            var returned = false;
            vm.ReturnRequested += (_, _) => returned = true;
            vm.ReturnToWorkspaceCommand.Execute(null);
            Check(returned, "返回动作交由宿主统一处理 dirty 与焦点");

            var preferences = new NotificationPreferencesViewModel();
            vm.AttachNotificationPreferences(preferences);
            preferences.SelectedCompletionPolicyIndex = 2;
            Check(ReferenceEquals(vm.NotificationPreferences, preferences) && vm.HasNotificationPreferences &&
                vm.NotificationPreferences.SelectedCompletionPolicyIndex == 2 && preferences.SaveStatusText.Contains("未写入磁盘", StringComparison.Ordinal),
                "通知使用同一偏好对象，并如实区分窗口内应用与落盘");

            vm.Attach(Context(store) with { IsTurnActive = () => true,
                SetModel = (_, _, _) => Task.FromException<RpcResponse>(new InvalidOperationException("Running settings must not send RPC")) });
            var future = new SettingsPageViewModel.ModelChoice("fixture/future", "fixture/future");
            vm.ModelChoices.Add(future); vm.SelectedModel = future;
            Check(store.Current.SelectedModel == "fixture/future" && vm.ModelsState == SettingSaveState.Saved &&
                vm.ModelsInfo?.Contains("当前会话未切换", StringComparison.Ordinal) == true, "运行期间只保存未来默认值，不假标当前应用");

            var protectedRoot = Path.Combine(root, "protected");
            Directory.CreateDirectory(protectedRoot);
            const string protectedJson = "{\"version\":2,\"theme\":\"light\"}";
            File.WriteAllText(Path.Combine(protectedRoot, "settings.json"), protectedJson);
            var protectedStore = new AppSettingsStore(new AppDataLayout(protectedRoot));
            var rollback = new SettingsPageViewModel();
            var runtimeModel = "fixture/original";
            var rpcCalls = 0;
            rollback.Attach(Context(protectedStore) with
            {
                CurrentModel = () => runtimeModel,
                IsTurnActive = () => false,
                QueryState = _ => Task.FromResult(State(runtimeModel, "medium")),
                SetModel = (provider, id, _) => { rpcCalls++; runtimeModel = provider + "/" + id; return Task.FromResult(Success("set_model")); },
            });
            var selected = new SettingsPageViewModel.ModelChoice("fixture/next", "fixture/next");
            rollback.ModelChoices.Add(selected); rollback.SelectedModel = selected;
            await Dispatcher.Yield(DispatcherPriority.Background);
            Check(rpcCalls == 2 && runtimeModel == "fixture/original" && rollback.ModelsState == SettingSaveState.Failed &&
                rollback.ModelsStateText?.Contains("失败回退", StringComparison.Ordinal) == true &&
                File.ReadAllText(Path.Combine(protectedRoot, "settings.json")) == protectedJson,
                "RPC 应用后落盘失败实际恢复旧运行值，保留受保护配置文件");
            rollback.ThemeIndex = 2;
            Check(rollback.AppearanceState == SettingSaveState.Failed && rollback.HasAppearanceError &&
                rollback.AppearanceStateText?.Contains("失败回退", StringComparison.Ordinal) == true, "外观失败保留原值并明确回退状态");

            vm.SearchText = string.Empty;
            vm.SelectedCategory = vm.Categories.Single(x => x.Id == "network");
            vm.EndpointFormOpen = true;
            var page = new SettingsPage { DataContext = vm };
            window = new Window { Content = page, Width = 420, Height = 640 };
            window.Show();
            await Task.Delay(100);
            await Dispatcher.Yield(DispatcherPriority.Background);
            page.UpdateResponsiveLayout();
            var row = page.GetVisualDescendants().OfType<Grid>().First(x => x.Classes.Contains("settingsControlRow") &&
                x.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "代理"));
            Check(page.FindControl<ListBox>("CompactSettingsCategories")!.IsVisible && Grid.GetRow(row.Children[2]) == 1 &&
                Grid.GetColumnSpan(row.Children[2]) == 2, "420 DIP 时导航在顶部、控制行在说明下方");
            var key = page.FindControl<TextBox>("EndpointKeyBox")!;
            key.Text = "transient-key-fixture";
            await Dispatcher.Yield(DispatcherPriority.Background);
            Check(vm.HasEndpointDraft, "密码输入只通过存在标志加入 dirty");
            vm.DiscardUnsavedChanges();
            Check(string.IsNullOrEmpty(key.Text) && !vm.HasEndpointDraft, "丢弃时真实密码输入框清空");
            window.Width = 1280;
            await Task.Delay(100);
            await Dispatcher.Yield(DispatcherPriority.Background);
            page.UpdateResponsiveLayout();
            Check(page.FindControl<ListBox>("SettingsCategoryList")!.IsVisible && !page.FindControl<ListBox>("CompactSettingsCategories")!.IsVisible &&
                Grid.GetColumn(row.Children[2]) == 2 && Grid.GetRow(row.Children[2]) == 0, "1280 DIP 时恢复分类侧栏与左说明右控件");
        }
        finally
        {
            window?.Close();
            if (Application.Current is { } app && previousTheme is not null) app.RequestedThemeVariant = previousTheme;
            Directory.Delete(root, true);
        }
    }

    private static SettingsPageViewModel.Context Context(AppSettingsStore store) => new(store, true, () => null, null,
        null, null, null, () => null, () => null, () => [], () => null, () => "(fixture)",
        _ => Task.CompletedTask, _ => Task.CompletedTask, () => "unavailable", () => "unavailable");

    private static RpcResponse Success(string command) => new("fixture", command, true, null, null);
    private static RpcResponse State(string model, string thinking)
    {
        var pieces = model.Split('/', 2);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { model = new { provider = pieces[0], id = pieces[1] }, thinkingLevel = thinking }));
        return new("fixture", "get_state", true, document.RootElement.Clone(), null);
    }
}
