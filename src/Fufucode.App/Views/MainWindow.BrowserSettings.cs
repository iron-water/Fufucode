using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Fufucode.App.Features.BrowserExperience;
using Fufucode.App.Infrastructure;
using Fufucode.App.Infrastructure.Browser;
using Fufucode.App.ViewModels;
using Fufucode.Core.Backend;
using Fufucode.Core.Plugins;
using Fufucode.Core;

namespace Fufucode.App;

public partial class MainWindow
{
    private BrowserSettingsHost _browserSettings = null!;
    private BrowserBookmarkStore? _browserBookmarks;
    private BrowserHistoryStore? _browserHistory;
    private BrowserDownloadHistory? _browserDownloads;
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
        EnsureBrowserStores();
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
        _pluginPackageManager.PagesChanged += AttachAutomationPluginPage;
        _pluginPackageManager.InitializeInstalledPlugins(_host?.ProjectPath);
        AttachAutomationPluginPage();
    }

    /// <summary>
    /// 浏览器页可能在设置宿主之前由工作区注册表惰性创建；存储必须可重复安全初始化，
    /// 否则先创建的页会拿到 null 下载历史，表现为“下载完成但不落历史”。
    /// </summary>
    private void EnsureBrowserStores()
    {
        var root = _browserSettingsRoot();
        _browserBookmarks ??= BrowserBookmarkStore.ForRoot(root);
        _browserHistory ??= BrowserHistoryStore.ForRoot(root);
        if (_browserDownloads is not null) return;
        try { _browserDownloads = new BrowserDownloadHistory(root); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _vm?.Timeline.AddNotification("下载历史不可用：" + error.Message);
        }
    }

    private string _browserSettingsRoot() =>
        Program.SelfTestMode
            ? Program.SelfTestDataRoot ?? Path.Combine(Path.GetTempPath(), "fufucode-selftest-appdata")
            : _options.CreateAppDataLayout().Root;

    /// <summary>把设置推给现有浏览器视图；尚未创建的视图在创建时自行读取当前值。</summary>
    private void ApplyBrowserSettings(BrowserSettings settings)
    {
        if (_closed) return;
        _lastBrowserSettings = settings.Normalize();
        foreach (var view in BrowserViews())
        {
            view.ViewModel.ApplyShowFullUrl(settings.ShowFullUrl);
            view.ApplyAgentControl(settings, _browserSettings.Store, _browserSettings.ViewModel);
            view.SetAgentApprovalHandler(ApproveBrowserAgentActionAsync);
            AttachBridge(view);
        }
    }

    private BrowserSettings _lastBrowserSettings = BrowserSettings.Default;

    /// <summary>新创建的浏览器视图（如 XAML 预览页）在挂载时读取最近一次下发的设置。</summary>
    private void ApplyLatestBrowserSettings(Views.BrowserView view)
    {
        view.ViewModel.ApplyShowFullUrl(_lastBrowserSettings.ShowFullUrl);
        view.ApplyAgentControl(_lastBrowserSettings, _browserSettings?.Store, _browserSettings?.ViewModel);
        view.SetAgentApprovalHandler(ApproveBrowserAgentActionAsync);
    }

    /// <summary>
    /// 智能体浏览器动作的人工审批：复用现有“扩展 UI 确认”卡片（非模态，关闭即拒绝）。
    /// 审批必须在 UI 线程弹窗，判定结果原样返回给桥，不在 auto/full 模式放行。
    /// </summary>
    private Task<bool> ApproveBrowserAgentActionAsync(BrowserApprovalRequest request,
        System.Threading.CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            try
            {
                var payload = System.Text.Json.JsonSerializer.SerializeToElement(new
                {
                    title = "智能体浏览器动作确认",
                    message = $"{request.Action} → {request.Host}\n{request.Address}\n\n允许本次操作？",
                });
                var uiRequest = new Fufucode.Core.ExtensionUiRequest(Guid.NewGuid(), Guid.NewGuid().ToString("N"), "confirm",
                    payload, DateTimeOffset.UtcNow, WebView2BrowserUseBridge.ApprovalTimeout);
                var window = new ExtensionUiDialogWindow(uiRequest,
                    (_, response) =>
                    {
                        completion.TrySetResult(response.Confirmed == true);
                        return Task.CompletedTask;
                    });
                window.Closed += (_, _) => completion.TrySetResult(false);
                if (this.IsVisible) window.Show(this); else window.Show();
            }
            catch (Exception error)
            {
                _vm?.Timeline.AddNotification("浏览器动作确认卡片打开失败：" + error.Message);
                completion.TrySetResult(false);
            }
        });
        cancellationToken.Register(() => completion.TrySetResult(false));
        return completion.Task;
    }

    /// <summary>浏览器引用/标注进入当前主聊天草稿（与资料库引用同一套不覆盖语义）。</summary>
    private void OnChatReferenceRequested(Views.BrowserView view, Views.BrowserChatReference reference)
        => _ = AcceptBrowserReferenceAsync(reference);

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
