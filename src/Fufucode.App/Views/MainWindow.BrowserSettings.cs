using System;
using System.Collections.Generic;
using System.IO;
using Avalonia.Controls;
using Fufucode.App.Features.BrowserExperience;
using Fufucode.App.Infrastructure;
using Fufucode.App.ViewModels;
using Fufucode.Core.Backend;
using Fufucode.Core.Plugins;
using Fufucode.Core;

namespace Fufucode.App;

public partial class MainWindow
{
    private BrowserSettingsHost _browserSettings = null!;
    private PluginToolRegistry? _browserToolRegistry;
    private PluginPackageManager? _pluginPackageManager;
    private WebView2BrowserUseBridge? _wiredBridge;
    internal BrowserSettingsViewModel BrowserSettingsForTesting => _browserSettings.ViewModel;

    /// <summary>Browser Use 工具注册表（真实接线状态）；未创建时为 null。</summary>
    internal PluginToolRegistry? BrowserToolRegistryForTesting => _browserToolRegistry;

    /// <summary>
    /// BRW-1：组合根创建唯一的浏览器设置宿主，同时供【设置 → 浏览器】与内置浏览器读取；
    /// 设置变化即时推给已打开的浏览器页（显示完整网址、Agent 控制门禁）。
    /// </summary>
    private void InitializeBrowserSettings()
    {
        var root = Program.SelfTestMode
            ? Program.SelfTestDataRoot ?? Path.Combine(Path.GetTempPath(), "fufucode-selftest-appdata")
            : _options.CreateAppDataLayout().Root;
        _browserSettings = new BrowserSettingsHost(root);
        MigrateLegacyBrowserSettings();
        _browserSettings.ViewModel.Changed += ApplyBrowserSettings;
        _settingsPage.AttachBrowserSettings(_browserSettings.ViewModel);
        WireBrowserUseRegistry(_browserSettings);
        ApplyBrowserSettings(_browserSettings.ViewModel.CurrentSettings);
    }

    /// <summary>
    /// 把浏览器视图的真实桥接进 Core 的插件工具注册表（BRW-1）：
    /// 只有“插件已安装且已启用”时 <see cref="PluginToolRegistry"/> 才暴露工具；
    /// 每次注册/重绑都使用当前活动浏览器视图的桥，切换页签后仍指向真实页面。
    /// </summary>
    private void WireBrowserUseRegistry(BrowserSettingsHost host)
    {
        if (_browserSettings.Store is null) return;
        var layout = new AppDataLayout(_browserSettingsRoot());
        var gateway = new PluginCapabilityGateway(new PluginStateStore(layout));
        _browserToolRegistry = new PluginToolRegistry(gateway, new ProductionToolDispatcher(_host?.ProjectPath));
        _pluginPackageManager = new PluginPackageManager(layout, _browserToolRegistry, serviceResolver: type =>
        {
            if (type == typeof(BrowserUseBridge)) return _wiredBridge;
            return null;
        });
        _pluginPackageManager.InitializeInstalledPlugins(_host?.ProjectPath);
    }

    private string _browserSettingsRoot() =>
        Program.SelfTestMode
            ? Program.SelfTestDataRoot ?? Path.Combine(Path.GetTempPath(), "fufucode-selftest-appdata")
            : _options.CreateAppDataLayout().Root;

    /// <summary>把设置推给现有浏览器视图；尚未创建的视图在创建时自行读取当前值。</summary>
    private void ApplyBrowserSettings(BrowserSettings settings)
    {
        if (_closed) return;
        foreach (var view in BrowserViews())
        {
            view.ViewModel.ApplyShowFullUrl(settings.ShowFullUrl);
            view.ApplyAgentControl(settings, _browserSettings.Store, _browserSettings.ViewModel);
            AttachBridge(view);
        }
    }

    /// <summary>
    /// 首次创建 browser-settings.json 时，把旧的 WorkbenchAppearance 浏览器偏好迁移过来，
    /// 避免老用户设置丢失；迁移只在文件尚不存在时发生一次，之后以浏览器设置为唯一真源。
    /// </summary>
    private void MigrateLegacyBrowserSettings()
    {
        if (_browserSettings.Store is not { } store || store.Exists) return;
        if (_customization is not { } customization) return;
        try
        {
            var legacy = BrowserSettings.FromLegacy(customization.Current);
            // 保留主开关的旧值：WorkbenchAppearance 里没有 BrowserUseEnabled 时按默认 true。
            store.Save(legacy with { BrowserUseEnabled = customization.Current.BrowserUseEnabled });
            _browserSettings.ViewModel.Attach(_browserSettingsRoot());
        }
        catch (Exception error)
        {
            _vm?.Timeline.AddNotification("浏览器设置迁移失败（保留默认值）：" + error.GetBaseException().Message);
        }
    }

    /// <summary>把该视图的桥接到插件处理器；桥只是入口，门禁仍在每次调用时校验。</summary>
    private void AttachBridge(Views.BrowserView view)
    {
        if (view.BridgeForTesting is not { } bridge) return;
        view.BrowserAgentBridgeChanged -= OnBrowserBridgeChanged;
        view.BrowserAgentBridgeChanged += OnBrowserBridgeChanged;
        OnBrowserBridgeChanged(view, bridge);
    }

    private void OnBrowserBridgeChanged(object? sender, WebView2BrowserUseBridge bridge)
    {
        // 最后一次绑定的活动视图胜出：工具调用始终命中用户当前正在看的页面。
        _wiredBridge = bridge;
    }

    private System.Collections.Generic.IEnumerable<Views.BrowserView> BrowserViews()
    {
        if (_workbenchPages.ContainsKey("browser") && _workbenchPages["browser"].Content is Views.BrowserView page)
            yield return page;
        if (WorkspacePanel is { } panel)
        {
            Views.BrowserView? preview = null;
            try
            {
                // 无 NameScope 的控件上 FindControl 会抛异常（右栏可整体收起），按"不可用"处理。
                if (Avalonia.Controls.NameScope.GetNameScope(panel) is not null)
                    preview = panel.FindControl<Views.BrowserView>(Views.BrowserView.PreviewControlName);
            }
            catch (InvalidOperationException) { preview = null; }
            if (preview is not null)
                yield return preview;
        }
    }
}
