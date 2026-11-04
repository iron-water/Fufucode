using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Fufucode.App.Features.BrowserExperience;
using Fufucode.App.Features.ExternalBrowser;
using Fufucode.App.Infrastructure;
using Fufucode.App.Infrastructure.Browser;
using Fufucode.App.ViewModels;
using Fufucode.App.Views;
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
    private PluginStateStore? _browserPluginState;
    private WebView2BrowserUseBridge? _wiredBridge;
    private ExternalBrowserBridgeServer? _externalBrowserBridge;
    private readonly SemaphoreSlim _externalBrowserLifecycle = new(1, 1);
    private readonly CancellationTokenSource _browserControlLifetime = new();
    private bool _browserControlDisposed;
    private BrowserSettings _lastBrowserSettings = BrowserSettings.Default;
    internal ExternalBrowserBridgeServer? ExternalBrowserBridgeForTesting => _externalBrowserBridge;
    internal BrowserSettingsViewModel BrowserSettingsForTesting => _browserSettings.ViewModel;
    internal PluginToolRegistry? BrowserToolRegistryForTesting => _browserToolRegistry;

    private bool IsBrowserUsePluginEnabled => !_browserControlDisposed &&
        _browserPluginState?.GetState("agent.browser-use") is { IsInstalled: true, IsEnabled: true } &&
        _pluginPackageManager?.GetActivePluginIds().Contains("agent.browser-use") == true;

    private void InitializeBrowserSettings()
    {
        var root = _browserSettingsRoot();
        _browserSettings = new BrowserSettingsHost(root);
        EnsureBrowserStores();
        MigrateLegacyBrowserSettings();
        _browserSettings.ViewModel.Changed += ApplyBrowserSettings;
        _settingsPage.AttachBrowserSettings(_browserSettings.ViewModel);
        WireBrowserUseRegistry(_browserSettings);
        PluginStateNotifier.Changed += OnBrowserPluginStateChanged;
        ApplyBrowserSettings(_browserSettings.ViewModel.CurrentSettings);
    }

    private void WireBrowserUseRegistry(BrowserSettingsHost host)
    {
        if (_browserSettings.Store is null) return;
        var layout = new AppDataLayout(_browserSettingsRoot());
        _browserPluginState = new PluginStateStore(layout);
        _browserToolRegistry = new PluginToolRegistry(new PluginCapabilityGateway(_browserPluginState),
            new ProductionToolDispatcher(_host?.ProjectPath));
        // 桥本身不保留 BrowserView；每次工具调用重新寻找仍挂载的可见页面。
        _wiredBridge = new WebView2BrowserUseBridge(ResolveBrowserControlView, () =>
        {
            var settings = _browserSettings.Store?.Current ?? BrowserSettings.Default;
            return new BrowserSettingsGate(IsBrowserUsePluginEnabled && settings.BrowserUseEnabled, settings.EnableFullCdpAccess);
        }) { ApprovalHandler = ApproveBrowserAgentActionAsync };
        _pluginPackageManager = new PluginPackageManager(layout, _browserToolRegistry, _browserPluginState,
            serviceResolver: type =>
            {
                if (!IsBrowserUsePluginEnabled) return null;
                if (type == typeof(BrowserUseBridge)) return _wiredBridge;
                if (type == typeof(ExternalBrowserBridge) && _browserSettings.Store?.Current.ExternalBrowserEnabled == true)
                    return _externalBrowserBridge;
                return null;
            });
        _pluginPackageManager.InitializeInstalledPlugins(_host?.ProjectPath);
    }

    private BrowserView? ResolveBrowserControlView()
    {
        if (_browserControlDisposed) return null;
        var views = BrowserViews().ToList();
        return views.FirstOrDefault(view => view.IsEffectivelyVisible) ?? views.FirstOrDefault();
    }

    private void OnBrowserPluginStateChanged()
    {
        if (_browserControlDisposed) return;
        // PackageManager 也会刷新同一 state store；这里主动读取确保订阅顺序不影响门禁。
        _browserPluginState?.Reload();
        void Apply() { if (!_browserControlDisposed) ApplyBrowserSettings(_browserSettings.ViewModel.CurrentSettings); }
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess()) Apply();
        else Avalonia.Threading.Dispatcher.UIThread.Post(Apply);
    }

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

    private string _browserSettingsRoot() => Program.SelfTestMode
        ? Program.SelfTestDataRoot ?? Path.Combine(Path.GetTempPath(), "fufucode-selftest-appdata")
        : _options.CreateAppDataLayout().Root;

    private void ApplyBrowserSettings(BrowserSettings settings)
    {
        if (_closed || _browserControlDisposed) return;
        _lastBrowserSettings = settings.Normalize();
        foreach (var view in BrowserViews()) ApplyLatestBrowserSettings(view);
        _ = ApplyExternalBrowserSettingsAsync();
    }

    private void ApplyLatestBrowserSettings(BrowserView view)
    {
        var enabled = IsBrowserUsePluginEnabled;
        var settings = enabled ? _lastBrowserSettings : _lastBrowserSettings with { BrowserUseEnabled = false };
        view.ViewModel.ApplyShowFullUrl(settings.ShowFullUrl);
        view.ApplyAgentControl(settings, _browserSettings?.Store, _browserSettings?.ViewModel);
        view.SetAgentApprovalHandler(enabled ? ApproveBrowserAgentActionAsync : null);
    }

    /// <summary>串行收敛最新设置；停用/卸载/关闭后，即使此前启动仍在 await 也会停止监听。</summary>
    private async Task ApplyExternalBrowserSettingsAsync()
    {
        await _externalBrowserLifecycle.WaitAsync();
        try
        {
            var settings = _browserSettings.Store?.Current ?? BrowserSettings.Default;
            if (!IsBrowserUsePluginEnabled || !settings.ExternalBrowserEnabled)
            {
                if (_externalBrowserBridge is not null) await _externalBrowserBridge.StopAsync();
                UpdateExternalBrowserStatus();
                return;
            }
            EnsureExternalBrowserIdentity();
            settings = _browserSettings.Store?.Current ?? settings;
            if (_externalBrowserBridge is null || _externalBrowserBridge.Token != settings.ExternalBrowserToken)
            {
                if (_externalBrowserBridge is not null)
                {
                    _externalBrowserBridge.StatusChanged -= UpdateExternalBrowserStatus;
                    await _externalBrowserBridge.DisposeAsync();
                }
                _externalBrowserBridge = new ExternalBrowserBridgeServer(
                    () => IsBrowserUsePluginEnabled ? _browserSettings.Store?.Current ?? BrowserSettings.Default
                        : BrowserSettings.Default, settings.ExternalBrowserToken, settings.ExternalBrowserPin)
                {
                    PageReferenceHandler = AcceptExternalBrowserReferenceAsync,
                    ApprovalHandler = ApproveBrowserAgentActionAsync,
                };
                _externalBrowserBridge.StatusChanged += UpdateExternalBrowserStatus;
            }
            var bridge = _externalBrowserBridge;
            // 实际端口可能因占用而不同；记住请求配置避免每次普通设置变化都重启。
            if (!bridge.Running || _externalBrowserRequestedPort != settings.ExternalBrowserPort || bridge.Pin != settings.ExternalBrowserPin)
            {
                _externalBrowserRequestedPort = settings.ExternalBrowserPort;
                await bridge.StartAsync(settings.ExternalBrowserPort, settings.ExternalBrowserPin);
            }
            if (_browserControlDisposed || !IsBrowserUsePluginEnabled || _browserSettings.Store?.Current.ExternalBrowserEnabled != true)
                await bridge.StopAsync();
            else if (bridge.Running)
                _externalBrowserExtensionPath = ExternalBrowserExtensionInstaller.Ensure(
                    _browserSettingsRoot(), bridge.Port, settings.ExternalBrowserPin);
            UpdateExternalBrowserStatus();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Net.Sockets.SocketException)
        {
            _vm?.Timeline.AddNotification("外部浏览器桥启动失败：" + error.Message);
            _browserSettings.ViewModel.ReportExternalBrowser("启动失败：" + error.Message, 0, false, string.Empty);
        }
        finally { _externalBrowserLifecycle.Release(); }
    }

    private int _externalBrowserRequestedPort;
    private string _externalBrowserExtensionPath = string.Empty;

    private void EnsureExternalBrowserIdentity()
    {
        if (_browserSettings.Store is not { } store) return;
        var current = store.Current;
        var pin = current.ExternalBrowserPin;
        if (pin.Length != 4) pin = Random.Shared.Next(0, 10000).ToString("D4");
        var token = current.ExternalBrowserToken;
        if (string.IsNullOrWhiteSpace(token)) token = Guid.NewGuid().ToString("N");
        if (pin != current.ExternalBrowserPin || token != current.ExternalBrowserToken)
            store.Save(current with { ExternalBrowserPin = pin, ExternalBrowserToken = token });
    }

    private void UpdateExternalBrowserStatus()
    {
        void Apply()
        {
            if (_browserControlDisposed) return;
            var bridge = _externalBrowserBridge;
            var enabled = IsBrowserUsePluginEnabled && _browserSettings.Store?.Current.ExternalBrowserEnabled == true;
            var running = enabled && bridge?.Running == true;
            var status = running ? $"运行中 · 端口 {bridge!.Port} · " +
                (bridge.Status.HandshakeComplete ? "扩展已就绪" : bridge.IsConnected ? "扩展已连接，等待握手" : "等待扩展连接")
                : "未启动";
            _browserSettings.ViewModel.ReportExternalBrowser(status, running ? bridge!.Port : 0,
                running && bridge!.IsConnected, running ? _externalBrowserExtensionPath : string.Empty, running ? bridge!.Status : null);
        }
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess()) Apply();
        else Avalonia.Threading.Dispatcher.UIThread.Post(Apply);
    }

    /// <summary>由 MainWindow 关闭入口 await；解除事件、停止监听并释放插件宿主。</summary>
    internal async Task DisposeBrowserControlAsync()
    {
        if (_browserControlDisposed) return;
        _browserControlDisposed = true;
        _browserControlLifetime.Cancel();
        PluginStateNotifier.Changed -= OnBrowserPluginStateChanged;
        if (_browserSettings is not null) _browserSettings.ViewModel.Changed -= ApplyBrowserSettings;
        _wiredBridge = null;
        await _externalBrowserLifecycle.WaitAsync();
        try
        {
            if (_externalBrowserBridge is not null)
            {
                _externalBrowserBridge.StatusChanged -= UpdateExternalBrowserStatus;
                await _externalBrowserBridge.DisposeAsync();
                _externalBrowserBridge = null;
            }
            if (_pluginPackageManager is not null)
            {
                await _pluginPackageManager.DisposeAsync();
                _pluginPackageManager = null;
            }
        }
        finally { _externalBrowserLifecycle.Release(); }
    }

    private async Task<string> AcceptExternalBrowserReferenceAsync(ExternalBrowserPageReference reference)
    {
        var text = new System.Text.StringBuilder();
        text.AppendLine("【来自设备浏览器扩展的引用】");
        if (!string.IsNullOrWhiteSpace(reference.Title)) text.AppendLine("标题：" + reference.Title);
        if (!string.IsNullOrWhiteSpace(reference.Url)) text.AppendLine("地址：" + reference.Url);
        if (!string.IsNullOrWhiteSpace(reference.Selector)) text.AppendLine("选择器：" + reference.Selector);
        if (!string.IsNullOrWhiteSpace(reference.Content)) text.AppendLine().Append(reference.Content);
        var dispatcher = Avalonia.Threading.Dispatcher.UIThread;
        if (dispatcher.CheckAccess())
            return await AcceptBrowserReferenceAsync(new BrowserChatReference(text.ToString().TrimEnd(), null));
        return await dispatcher.InvokeAsync(() => AcceptBrowserReferenceAsync(new BrowserChatReference(text.ToString().TrimEnd(), null)));
    }

    private async Task<bool> ApproveBrowserAgentActionAsync(BrowserApprovalRequest request, CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _browserControlLifetime.Token);
        lifetime.CancelAfter(WebView2BrowserUseBridge.ApprovalTimeout);
        cancellationToken = lifetime.Token;
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ExtensionUiDialogWindow? window = null;
        using var registration = cancellationToken.Register(() =>
        {
            completion.TrySetResult(false);
            Avalonia.Threading.Dispatcher.UIThread.Post(() => window?.Close());
        });
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_closed || _browserControlDisposed || cancellationToken.IsCancellationRequested) { completion.TrySetResult(false); return; }
            try
            {
                var payload = System.Text.Json.JsonSerializer.SerializeToElement(new
                {
                    title = "智能体浏览器动作确认",
                    message = $"{request.Action} → {request.Host}\n{request.Address}\n\n允许本次操作？",
                });
                var uiRequest = new Fufucode.Core.ExtensionUiRequest(Guid.NewGuid(), Guid.NewGuid().ToString("N"), "confirm",
                    payload, DateTimeOffset.UtcNow, WebView2BrowserUseBridge.ApprovalTimeout);
                window = new ExtensionUiDialogWindow(uiRequest, (_, response) =>
                {
                    completion.TrySetResult(!cancellationToken.IsCancellationRequested && response.Confirmed == true);
                    return Task.CompletedTask;
                });
                window.Closed += (_, _) => completion.TrySetResult(false);
                if (IsVisible) window.Show(this); else window.Show();
            }
            catch (Exception error)
            {
                _vm?.Timeline.AddNotification("浏览器动作确认卡片打开失败：" + error.Message);
                completion.TrySetResult(false);
            }
        });
        return await completion.Task.ConfigureAwait(false);
    }

    private void OnChatReferenceRequested(BrowserView view, BrowserChatReference reference) => _ = AcceptBrowserReferenceAsync(reference);

    private void MigrateLegacyBrowserSettings()
    {
        if (_browserSettings.Store is not { } store || store.Exists || _customization is not { } customization) return;
        try
        {
            var legacy = BrowserSettings.FromLegacy(customization.Current);
            store.Save(legacy with { BrowserUseEnabled = false }); // 旧模型默认 true 不代表用户显式授权控制。
            _browserSettings.ViewModel.Attach(_browserSettingsRoot());
        }
        catch (Exception error)
        {
            _vm?.Timeline.AddNotification("浏览器设置迁移失败（保留默认值）：" + error.GetBaseException().Message);
        }
    }

    private IEnumerable<BrowserView> BrowserViews()
    {
        if (_workbenchPages.ContainsKey("browser") && _workbenchPages["browser"].Content is BrowserView page) yield return page;
        if (WorkspacePanel is not { } panel) yield break;
        BrowserView? preview = null;
        try
        {
            if (Avalonia.Controls.NameScope.GetNameScope(panel) is not null)
                preview = panel.FindControl<BrowserView>(BrowserView.PreviewControlName);
        }
        catch (InvalidOperationException) { }
        if (preview is not null) yield return preview;
    }
}
