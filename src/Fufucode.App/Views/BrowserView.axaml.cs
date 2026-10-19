using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Fufucode.App.Features.BrowserExperience;
using Fufucode.App.Infrastructure;
using Fufucode.App.Infrastructure.Browser;
using Fufucode.App.ViewModels;

namespace Fufucode.App.Views;

/// <summary>从内置浏览器发送到对话的引用：文本 + 可选的标注截图文件。</summary>
public sealed record BrowserChatReference(string Text, string? ImagePath);

/// <summary>
/// 右栏 Preview / 工作区浏览器页：页签栏 + 导航 + 地址 + 查找 + 缩放 + WebView2 宿主。
/// 每个页签持有独立的 BrowserViewHost（独立原生 controller、DOM、历史），
/// 因此切换/关闭页签不会串页或覆盖对方状态。
/// 自带 BrowserViewModel（不继承外层 DataContext），因此可以整体搬到右栏任意位置。
/// </summary>
public partial class BrowserView : UserControl, IDisposable
{
    private readonly BrowserViewModel _viewModel;
    private readonly Grid _hostLayer;
    private readonly Dictionary<string, BrowserViewHost> _hosts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _findStarted = new(StringComparer.Ordinal);
    /// <summary>每个页签查找回调的世代号：关闭/换 query/导航/关 tab/Dispose 都会递增，旧回调不得写回。</summary>
    private readonly Dictionary<string, int> _findGeneration = new(StringComparer.Ordinal);
    /// <summary>记录已向 SDK 发起过 Start 的 (query, 文档世代)；同 query 但换文档必须重新 Start。</summary>
    private readonly Dictionary<string, string> _findDocumentEpoch = new(StringComparer.Ordinal);
    private readonly BrowserDownloadHistory? _downloadHistory;
    private readonly HashSet<string> _recordedDownloads = new(StringComparer.Ordinal);
    private BrowserViewHost? _lastHost;
    private bool _disposed;

    /// <summary>BRW-1：Agent 控制与 CDP 门禁快照；设置变化时由组合根更新。</summary>
    private BrowserSettingsGate _agentGate = BrowserSettingsGate.Disabled;
    /// <summary>当前生效的浏览器设置快照（地址搜索、下载、权限、UA 都从这里取）。</summary>
    private BrowserSettings _settings = BrowserSettings.Default;
    private BrowserSiteRules _siteRules = new(BrowserSettings.Default);
    private WebView2BrowserUseBridge? _bridge;

    /// <summary>XAML（WorkbenchSections 预览页）实例化入口：不连收藏/历史，但仍连真实设置。</summary>
    public BrowserView() : this(null, null, null) { }

    public BrowserView(BrowserBookmarkStore? bookmarks = null, BrowserHistoryStore? history = null,
        BrowserDownloadHistory? downloads = null)
    {
        _downloadHistory = downloads;
        _viewModel = new BrowserViewModel(bookmarks, history);
        DataContext = _viewModel;
        InitializeComponent();
        var tabScroll = this.FindControl<ScrollViewer>("BrowserTabScroller")!;
        tabScroll.AddHandler(PointerWheelChangedEvent, (_, e) =>
        {
            var maximum = Math.Max(0, tabScroll.Extent.Width - tabScroll.Viewport.Width);
            if (maximum <= 0) return;
            var delta = e.Delta.X != 0 ? e.Delta.X : e.Delta.Y;
            tabScroll.Offset = new Avalonia.Vector(Math.Clamp(tabScroll.Offset.X - delta * 48, 0, maximum), 0);
            e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _hostLayer = this.FindControl<Grid>("HostLayer")
                     ?? throw new InvalidOperationException("BrowserView 缺少 WebView2 宿主层（x:Name=HostLayer）。");
        if (this.FindControl<TextBox>("AddressBox") is { } addressBox) addressBox.TextChanged += Address_TextChanged;
        SizeChanged += (_, _) => _viewModel.IsCompact = Bounds.Width < 380;
        _viewModel.IsCompact = Bounds.Width > 0 && Bounds.Width < 380;

        _viewModel.TabNavigationRequested += OnNavigationRequested;
        _viewModel.TabActionRequested += OnActionRequested;
        _viewModel.ZoomRequested += OnZoomRequested;
        _viewModel.TabCreated += OnTabCreated;
        _viewModel.TabClosed += OnTabClosed;
        _viewModel.PropertyChanged += OnTabSelectionChanged;
        _viewModel.AddressCopyRequested += OnAddressCopyRequested;
        _viewModel.ExternalOpenRequested += OnExternalOpenRequested;
        _viewModel.ShareRequested += OnShareRequested;

        // 首个页签的宿主先建好，保持与旧行为一致（构造完成即返回，不触碰运行时）。
        HostFor(_viewModel.ActiveTab);
        UpdateHostVisibility();
    }

    public BrowserViewModel ViewModel => _viewModel;

    /// <summary>当前 Agent 控制门禁（供组合根读取，判断是否需要注册 Browser Use 工具）。</summary>
    internal BrowserSettingsGate AgentGateForTesting => _agentGate;

    /// <summary>
    /// 应用用户设置：同步“显示完整网址”，并记录 Agent 控制/完整 CDP 门禁。
    /// 桥只创建一次；关闭开关后桥仍在，但每次调用都会再次校验门禁并如实拒绝。
    /// </summary>
    public void ApplyAgentControl(BrowserSettings settings, BrowserSettingsStore? store, BrowserSettingsViewModel? settingsViewModel)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings.Normalize();
        _siteRules = new BrowserSiteRules(_settings);
        _viewModel.ApplySiteRules(_siteRules);
        _viewModel.ApplyShowFullUrl(_settings.ShowFullUrl);
        _viewModel.ApplySearchUrlTemplate(_settings.SearchUrlTemplate);
        _agentGate = new BrowserSettingsGate(_settings.BrowserUseEnabled, _settings.EnableFullCdpAccess);
        foreach (var host in _hosts.Values)
        {
            host.SetUserAgent(_settings.UserAgent);
            host.SetOpenLinksExternally(_siteRules.OpensWebExternally);
        }
        _bridge ??= new WebView2BrowserUseBridge(() => this, () => _agentGate);
        BrowserAgentBridgeChanged?.Invoke(this, _bridge);
    }

    /// <summary>当前生效设置（真实来源：browser-settings.json）；只读快照。</summary>
    public BrowserSettings CurrentSettings => _settings;

    /// <summary>站点权限判定（白名单/默认权限）；供下载与导航使用。</summary>
    public BrowserSiteRules SiteRules => _siteRules;

    /// <summary>接线智能体动作的人工审批通道（由 MainWindow 接到确认卡片）。</summary>
    public void SetAgentApprovalHandler(Func<BrowserApprovalRequest, System.Threading.CancellationToken, Task<bool>>? handler)
    {
        if (_bridge is not null) _bridge.ApprovalHandler = handler;
    }

    /// <summary>桥与门禁变化通知；组合根据此把真实桥接进插件工具注册表。</summary>
    internal event EventHandler<WebView2BrowserUseBridge>? BrowserAgentBridgeChanged;

    /// <summary>当前桥（未应用设置时为 null）。</summary>
    internal WebView2BrowserUseBridge? BridgeForTesting => _bridge;

    /// <summary>把网页引用/标注发送到当前对话；由 MainWindow 接线到聊天输入。</summary>
    public event EventHandler<BrowserChatReference>? ChatReferenceRequested;

    /// <summary>右键菜单动作：复制地址、外部打开、发送页面/选区到对话、进入标注。</summary>
    private async void HandleContextMenuAction(BrowserTab tab, BrowserViewHost host, string action)
    {
        switch (action)
        {
            case "copy-address":
                if (tab.NavigationAddress is { Length: > 0 } address) await CopyAddressAsync(address);
                else _viewModel.ReportStatusTo(tab, "当前没有可复制的地址。");
                break;
            case "open-external":
                if (tab.NavigationAddress is { Length: > 0 } open) await OpenExternalAsync(open);
                else _viewModel.ReportStatusTo(tab, "当前没有可外部打开的地址。");
                break;
            case "send-page":
                await SendPageToChatAsync(tab, host, selectionOnly: false);
                break;
            case "send-selection":
                await SendPageToChatAsync(tab, host, selectionOnly: true);
                break;
            case "annotate":
                await StartAnnotationAsync(tab, host);
                break;
        }
    }

    /// <summary>读取真实标题/地址/选区，组装成可粘贴到对话的引用文本。</summary>
    private async Task SendPageToChatAsync(BrowserTab tab, BrowserViewHost host, bool selectionOnly)
    {
        var raw = await host.ExecuteScriptAsync(
            "JSON.stringify({title:document.title||'',url:location.href||'',selection:(window.getSelection()?.toString()||'')})");
        if (string.IsNullOrWhiteSpace(raw))
        {
            _viewModel.ReportStatusTo(tab, "无法读取当前页面的内容。");
            return;
        }
        string title = string.Empty, url = tab.NavigationAddress ?? string.Empty, selection = string.Empty;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(UnquoteJson(raw));
            var root = doc.RootElement;
            if (root.TryGetProperty("title", out var t)) title = t.GetString() ?? string.Empty;
            if (root.TryGetProperty("url", out var u)) url = u.GetString() ?? url;
            if (root.TryGetProperty("selection", out var s)) selection = s.GetString() ?? string.Empty;
        }
        catch (System.Text.Json.JsonException) { }

        if (selectionOnly && string.IsNullOrWhiteSpace(selection))
        {
            _viewModel.ReportStatusTo(tab, "没有选中文字；请先在页面里选中内容再右键。");
            return;
        }
        var text = new System.Text.StringBuilder();
        text.AppendLine("【网页引用】");
        if (title.Length > 0) text.AppendLine("标题：" + title);
        text.AppendLine("地址：" + url);
        if (selection.Length > 0) text.AppendLine("选中内容：\n" + selection.Trim());
        ChatReferenceRequested?.Invoke(this, new BrowserChatReference(text.ToString().TrimEnd(), null));
        _viewModel.ReportStatusTo(tab, "已发送到对话输入框。");
    }

    /// <summary>
    /// 下载决策：站点权限 block 直接拒绝；approval 或“下载前询问”弹保存对话框；
    /// allow 且配置了默认目录时直接落到该目录，否则保留 WebView2 的默认下载位置。
    /// 所有路径都来自真实文件系统检查，不伪造成功。
    /// </summary>
    /// <summary>
    /// 弹窗处理：按设置可改投系统浏览器；否则开新页签并把新页签的真实 CoreWebView2 交回。
    /// 不覆盖原页签，也不伪造 opener 关系。
    /// </summary>
    private async Task HandleNewWindowAsync(BrowserNewWindowRequest request)
    {
        if (_siteRules.OpensWebExternally && Uri.TryCreate(request.Uri, UriKind.Absolute, out _))
        {
            await OpenExternalAsync(request.Uri);
            request.Resolve(null);
            return;
        }
        var created = _viewModel.CreateTab();
        var createdHost = HostFor(created);
        UpdateHostVisibility();
        if (request.Uri is { Length: > 0 } uri &&
            !uri.Equals("about:blank", StringComparison.OrdinalIgnoreCase))
        {
            created.AddressText = uri;
            created.NavigateCommand.Execute(null);
        }
        var core = await createdHost.EnsureCoreAsync();
        request.Resolve(core);
        if (core is null) _viewModel.ReportStatusTo(created, "新窗口未能建立浏览器运行时，已保留为新页签。");
    }

    private async Task HandleDownloadRequestAsync(BrowserDownloadRequest request)
    {
        try
        {
            var permission = _siteRules.DownloadPermissionFor(request.SourceUri);
            if (string.Equals(permission, "block", StringComparison.OrdinalIgnoreCase))
            {
                request.Resolve(BrowserDownloadDecision.Deny("站点下载权限为“阻止”，未下载任何文件。"));
                return;
            }

            var fileName = SanitizeFileName(request.FileName);
            string? targetPath = null;
            var needsPrompt = string.Equals(permission, "approval", StringComparison.OrdinalIgnoreCase) ||
                              _settings.PromptDownloadLocation;
            if (needsPrompt)
            {
                targetPath = await PickDownloadPathAsync(fileName);
                if (targetPath is null)
                {
                    request.Resolve(BrowserDownloadDecision.Deny("用户取消了下载。"));
                    return;
                }
            }
            else if (!string.IsNullOrWhiteSpace(_settings.DefaultDownloadDirectory))
            {
                var folder = Path.GetFullPath(_settings.DefaultDownloadDirectory);
                Directory.CreateDirectory(folder);
                targetPath = UniquePath(Path.Combine(folder, fileName));
            }
            request.Resolve(BrowserDownloadDecision.Allow(targetPath));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            request.Resolve(BrowserDownloadDecision.Deny("下载路径不可用：" + error.Message));
        }
    }

    private async Task<string?> PickDownloadPathAsync(string fileName)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is not { } storage) return null;
        var extension = Path.GetExtension(fileName);
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "保存下载文件",
            SuggestedFileName = fileName,
            DefaultExtension = extension.Length > 1 ? extension[1..] : null,
            ShowOverwritePrompt = true,
        });
        return file?.TryGetLocalPath();
    }

    private static string SanitizeFileName(string? fileName)
    {
        var name = Path.GetFileName(fileName ?? string.Empty);
        foreach (var invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
        return string.IsNullOrWhiteSpace(name) ? "download.bin" : name;
    }

    /// <summary>同名文件已存在时追加 "(1)"、"(2)"，不覆盖已有文件。</summary>
    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var folder = Path.GetDirectoryName(path) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var index = 1; index < 10_000; index++)
        {
            var candidate = Path.Combine(folder, $"{name} ({index}){extension}");
            if (!File.Exists(candidate)) return candidate;
        }
        return path;
    }

    private static string PermissionLabel(string uri, string kind) => kind switch
    {
        "Camera" => "使用摄像头",
        "Microphone" => "使用麦克风",
        "Geolocation" => "获取你的位置",
        "Notifications" => "发送通知",
        "ClipboardRead" => "读取剪贴板",
        "Autoplay" => "自动播放媒体",
        "MultipleAutomaticDownloads" => "连续下载多个文件",
        _ => "使用 " + kind,
    };

    /// <summary>
    /// 站点权限：站点被 block 时直接拒绝；没有规则时询问用户（记住本进程内的决定），
    /// 规则为 allow 时放行且写入 profile。
    /// </summary>
    private async Task HandlePermissionRequestAsync(BrowserPermissionRequest request)
    {
        var remembered = _siteRules.PermissionDecision(request.Host, request.Kind);
        if (remembered is { } known)
        {
            request.Resolve(known ? BrowserPermissionDecision.Always() : BrowserPermissionDecision.Deny());
            return;
        }

        var site = _siteRules.PermissionFor(request.Uri);
        if (string.Equals(site, "block", StringComparison.OrdinalIgnoreCase))
        {
            request.Resolve(BrowserPermissionDecision.Deny($"{request.Host} 的站点权限为“禁止访问”，已拒绝{PermissionLabel(request.Uri, request.Kind)}。"));
            return;
        }
        if (string.Equals(site, "allow", StringComparison.OrdinalIgnoreCase))
        {
            request.Resolve(BrowserPermissionDecision.Always());
            return;
        }

        // 需要批准：非用户发起的请求（后台脚本自动触发）一律先拒绝，避免无提示的静默授权。
        if (!request.IsUserInitiated)
        {
            request.Resolve(BrowserPermissionDecision.Deny("已拒绝后台自动触发的权限请求；请在页面中主动操作后重试。"));
            return;
        }

        var decision = await AskPermissionAsync(request.Host, PermissionLabel(request.Uri, request.Kind));
        _siteRules.RememberPermission(request.Host, request.Kind, decision.Allow);
        request.Resolve(decision);
    }

    private async Task<BrowserPermissionDecision> AskPermissionAsync(string host, string label)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        var dialog = new Window
        {
            Title = "站点权限",
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var panel = new StackPanel { Margin = new Thickness(18), Spacing = 12 };
        panel.Children.Add(new TextBlock
        {
            Text = $"{host} 正在请求{label}。",
            TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeight.SemiBold,
        });
        panel.Children.Add(new TextBlock
        {
            Text = "只在你信任该站点时允许。本次选择在当前运行期间对该站点保持。",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75,
            FontSize = 12,
        });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        var deny = new Button { Content = "拒绝" };
        var allow = new Button { Content = "允许" };
        buttons.Children.Add(deny);
        buttons.Children.Add(allow);
        panel.Children.Add(buttons);
        dialog.Content = panel;

        var completion = new TaskCompletionSource<BrowserPermissionDecision>();
        deny.Click += (_, _) => { completion.TrySetResult(BrowserPermissionDecision.Deny()); dialog.Close(); };
        allow.Click += (_, _) => { completion.TrySetResult(BrowserPermissionDecision.Always()); dialog.Close(); };
        dialog.Closed += (_, _) => completion.TrySetResult(BrowserPermissionDecision.Deny());
        if (owner is not null) await dialog.ShowDialog(owner);
        else dialog.Show();
        return await completion.Task;
    }

    private void ApplyDownloadProgress(BrowserDownloadProgress progress)
    {
        _viewModel.ApplyDownloadProgress(progress);
        if (progress.State != BrowserDownloadState.Completed || _recordedDownloads.Contains(progress.Id)) return;
        if (_settings.DownloadHistoryEnabled && _downloadHistory is not null)
        {
            _downloadHistory.Add(new BrowserDownloadEntry(
                progress.FileName,
                progress.ResultFilePath,
                progress.TotalBytes,
                "completed",
                DateTimeOffset.Now));
            _recordedDownloads.Add(progress.Id);
        }
    }

    private static string UnquoteJson(string raw)
    {
        var text = raw.Trim();
        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"')
        {
            try { return JsonSerializer.Deserialize<string>(text) ?? text; }
            catch (JsonException) { }
        }
        return text;
    }

    /// <summary>
    /// 标注模式：页面上拖一个框 → 截取该区域 → 填写说明 → 发送到当前对话。
    /// 选区完全由注入脚本完成；截图走真实 CDP Page.captureScreenshot 的 clip，不裁 Avalonia 位图。
    /// </summary>
    private async Task StartAnnotationAsync(BrowserTab tab, BrowserViewHost host)
    {
        if (!host.IsReady)
        {
            _viewModel.ReportStatusTo(tab, "请先加载网页再标注。");
            return;
        }
        var started = await host.ExecuteScriptAsync(AnnotationScript);
        if (started is null)
        {
            _viewModel.ReportStatusTo(tab, "无法在当前页面启动标注。");
            return;
        }
        _viewModel.ReportStatusTo(tab, "标注模式：在页面上拖出区域，按 Esc 取消。");

        string? payload = null;
        for (var attempt = 0; attempt < 400; attempt++)
        {
            await Task.Delay(150);
            var probe = await host.ExecuteScriptAsync(
                "window.__fufuAnnotationResult ? JSON.stringify(window.__fufuAnnotationResult) : null");
            if (string.IsNullOrWhiteSpace(probe) || probe.Trim() is "null" or "\"null\"") continue;
            payload = UnquoteJson(probe);
            break;
        }
        if (payload is null)
        {
            _viewModel.ReportStatusTo(tab, "标注等待超时，未发送任何内容。");
            return;
        }

        int x = 0, y = 0, width = 0, height = 0;
        string selector = string.Empty;
        bool cancelled = false;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (root.TryGetProperty("cancelled", out var cancel)) cancelled = cancel.ValueKind == JsonValueKind.True;
            if (root.TryGetProperty("x", out var xv)) x = (int)xv.GetDouble();
            if (root.TryGetProperty("y", out var yv)) y = (int)yv.GetDouble();
            if (root.TryGetProperty("w", out var wv)) width = (int)wv.GetDouble();
            if (root.TryGetProperty("h", out var hv)) height = (int)hv.GetDouble();
            if (root.TryGetProperty("selector", out var sel)) selector = sel.GetString() ?? string.Empty;
        }
        catch (JsonException)
        {
            _viewModel.ReportStatusTo(tab, "标注结果无法解析，未发送任何内容。");
            return;
        }
        if (cancelled)
        {
            _viewModel.ReportStatusTo(tab, "已取消标注。");
            return;
        }
        if (width < 2 || height < 2)
        {
            _viewModel.ReportStatusTo(tab, "框选区域太小，请重新标注。");
            return;
        }

        string? imagePath = null;
        Bitmap? preview = null;
        if (_settings.AnnotationIncludeScreenshot && !string.Equals(_settings.ScreenshotMode, "never", StringComparison.OrdinalIgnoreCase))
        {
            (imagePath, preview) = await CaptureAnnotationAsync(host, x, y, width, height);
        }

        var comment = await PromptAnnotationCommentAsync(preview, tab.DocumentTitle, selector);
        preview?.Dispose();
        if (comment is null)
        {
            _viewModel.ReportStatusTo(tab, "已取消标注。");
            return;
        }

        var text = new StringBuilder();
        text.AppendLine("【网页标注】");
        if (tab.DocumentTitle is { Length: > 0 } title) text.AppendLine("标题：" + title);
        text.AppendLine("地址：" + (tab.NavigationAddress ?? string.Empty));
        if (selector.Length > 0) text.AppendLine("元素：" + selector);
        text.AppendLine($"区域：{x},{y} {width}×{height}");
        text.AppendLine("说明：" + (comment.Length > 0 ? comment : "（未填写说明，仅标注区域）"));
        ChatReferenceRequested?.Invoke(this, new BrowserChatReference(text.ToString().TrimEnd(), imagePath));
        _viewModel.ReportStatusTo(tab, "标注已发送到对话输入框。");
    }

    private async Task<(string? Path, Bitmap? Preview)> CaptureAnnotationAsync(
        BrowserViewHost host, int x, int y, int width, int height)
    {
        try
        {
            var parameters = JsonSerializer.Serialize(new
            {
                format = "png",
                clip = new { x, y, width = Math.Max(1, width), height = Math.Max(1, height), scale = 1.0 },
            });
            var result = await host.CallDevToolsProtocolMethodAsync("Page.captureScreenshot", parameters);
            if (string.IsNullOrWhiteSpace(result)) return (null, null);
            using var doc = JsonDocument.Parse(result);
            if (!doc.RootElement.TryGetProperty("data", out var data)) return (null, null);
            var bytes = Convert.FromBase64String(data.GetString() ?? string.Empty);
            if (bytes.Length == 0) return (null, null);
            var folder = Path.Combine(Path.GetTempPath(), "fufucode-browser-annotations");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "annotation-" + Guid.NewGuid().ToString("N") + ".png");
            await File.WriteAllBytesAsync(path, bytes);
            using var stream = new MemoryStream(bytes);
            return (path, new Bitmap(stream));
        }
        catch (Exception error) when (error is JsonException or FormatException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return (null, null);
        }
    }

    /// <summary>标注说明输入框；返回 null 表示取消，空字符串表示只标注不写说明。</summary>
    private async Task<string?> PromptAnnotationCommentAsync(Bitmap? preview, string title, string selector)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        var dialog = new Window
        {
            Title = "标注页面",
            Width = 520,
            Height = preview is null ? 300 : 470,
            MinWidth = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = true,
        };
        var panel = new StackPanel { Margin = new Thickness(18), Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(title) ? "标注当前页面" : title,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });
        if (selector.Length > 0)
            panel.Children.Add(new TextBlock { Text = "元素：" + selector, Opacity = 0.7, FontSize = 12 });
        if (preview is not null)
        {
            panel.Children.Add(new Border
            {
                Height = 200,
                Background = Brushes.Transparent,
                Child = new Image { Source = preview, Stretch = Stretch.Uniform, Height = 200 },
            });
        }
        var comment = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 90,
            PlaceholderText = "说明想改什么（可留空，仅发送区域截图）…",
        };
        panel.Children.Add(comment);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };
        var cancel = new Button { Content = "取消" };
        var send = new Button { Content = "发送到对话", Classes = { "accent" } };
        buttons.Children.Add(cancel);
        buttons.Children.Add(send);
        panel.Children.Add(buttons);
        dialog.Content = panel;

        var completion = new TaskCompletionSource<string?>();
        cancel.Click += (_, _) => { completion.TrySetResult(null); dialog.Close(); };
        send.Click += (_, _) => { completion.TrySetResult((comment.Text ?? string.Empty).Trim()); dialog.Close(); };
        dialog.Closed += (_, _) => completion.TrySetResult(null);
        if (owner is not null) await dialog.ShowDialog(owner);
        else dialog.Show();
        return await completion.Task;
    }

    /// <summary>页面内拖框脚本：只做选区与结果写入，不发送任何网络请求。</summary>
    private const string AnnotationScript = """
        (function () {
          try {
            if (window.__fufuAnnotationCleanup) window.__fufuAnnotationCleanup();
            window.__fufuAnnotationResult = null;
            const layer = document.createElement('div');
            Object.assign(layer.style, { position: 'fixed', inset: '0', zIndex: '2147483647',
              cursor: 'crosshair', background: 'rgba(0,0,0,0.04)' });
            const box = document.createElement('div');
            Object.assign(box.style, { position: 'absolute', border: '2px solid #4c8dff',
              background: 'rgba(76,141,255,0.16)', display: 'none', pointerEvents: 'none' });
            layer.appendChild(box);
            document.body.appendChild(layer);
            let sx = 0, sy = 0, dragging = false;
            function cleanup() {
              layer.remove();
              document.removeEventListener('mousedown', down, true);
              document.removeEventListener('mousemove', move, true);
              document.removeEventListener('mouseup', up, true);
              document.removeEventListener('keydown', key, true);
              window.__fufuAnnotationCleanup = null;
            }
            function move(e) {
              if (!dragging) return;
              const x = Math.min(sx, e.clientX), y = Math.min(sy, e.clientY);
              Object.assign(box.style, { left: x + 'px', top: y + 'px',
                width: Math.abs(e.clientX - sx) + 'px', height: Math.abs(e.clientY - sy) + 'px' });
            }
            function down(e) {
              dragging = true; sx = e.clientX; sy = e.clientY;
              box.style.display = 'block'; move(e);
              e.preventDefault(); e.stopPropagation();
            }
            function up(e) {
              if (!dragging) return;
              dragging = false;
              const x = Math.min(sx, e.clientX), y = Math.min(sy, e.clientY);
              const w = Math.abs(e.clientX - sx), h = Math.abs(e.clientY - sy);
              const el = document.elementFromPoint(x + w / 2, y + h / 2);
              let selector = '';
              if (el) {
                if (el.id) selector = '#' + el.id;
                else if (typeof el.className === 'string' && el.className.trim())
                  selector = '.' + el.className.trim().split(/\s+/).join('.');
                else selector = el.tagName.toLowerCase();
              }
              cleanup();
              window.__fufuAnnotationResult = { x: x, y: y, w: w, h: h, selector: selector };
              e.preventDefault(); e.stopPropagation();
            }
            function key(e) {
              if (e.key === 'Escape') {
                cleanup();
                window.__fufuAnnotationResult = { cancelled: true };
              }
            }
            window.__fufuAnnotationCleanup = cleanup;
            document.addEventListener('mousedown', down, true);
            document.addEventListener('mousemove', move, true);
            document.addEventListener('mouseup', up, true);
            document.addEventListener('keydown', key, true);
            return 'annotation-started';
          } catch (error) { return 'annotation-error:' + error.message; }
        })();
        """;

    /// <summary>
    /// Agent 请求导航：走与用户点击同一套规范化与宿主调用，不绕过地址白名单。
    /// 导航请求是同步派发给宿主的，因此这里不做额外的异步等待，避免 sync-over-async 死锁。
    /// </summary>
    internal bool NavigateFromAgent(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        _viewModel.ActiveTab.AddressText = url;
        _viewModel.NavigateCommand.Execute(null);
        return _viewModel.IsLoading || _viewModel.ActiveTab.NavigationAddress is { Length: > 0 };
    }
    internal Func<uint, KeyModifiers, bool>? NativeShortcutHandler { get; set; }
    /// <summary>测试用：优先返回当前活动页签的宿主；已释放后返回最近一个真实宿主（不再新建）。</summary>
    internal BrowserViewHost HostForTesting =>
        _hosts.TryGetValue(_viewModel.ActiveTab.Id, out var host) ? host : _lastHost ?? HostFor(_viewModel.ActiveTab);
    internal BrowserViewHost HostForTabForTesting(BrowserTab tab) => HostFor(tab);
    internal int HostCountForTesting => _hosts.Count;
    internal IReadOnlyList<BrowserTab> TabsForTesting => _viewModel.Tabs.ToList();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _viewModel.TabNavigationRequested -= OnNavigationRequested;
        _viewModel.TabActionRequested -= OnActionRequested;
        _viewModel.ZoomRequested -= OnZoomRequested;
        _viewModel.TabCreated -= OnTabCreated;
        _viewModel.TabClosed -= OnTabClosed;
        _viewModel.PropertyChanged -= OnTabSelectionChanged;
        _viewModel.AddressCopyRequested -= OnAddressCopyRequested;
        _viewModel.ExternalOpenRequested -= OnExternalOpenRequested;
        _viewModel.ShareRequested -= OnShareRequested;
        foreach (var tabId in _hosts.Keys.ToArray()) InvalidateFind(tabId, resetDocumentEpoch: true);
        foreach (var host in _hosts.Values) host.Dispose();
        _hosts.Clear();
        _findStarted.Clear();
        _findGeneration.Clear();
        _findDocumentEpoch.Clear();
    }

    internal BrowserPageState CaptureState()
    {
        var tabs = _viewModel.Tabs.Select(tab => tab.CaptureState()).ToArray();
        return new BrowserPageState(BrowserPageState.CurrentVersion, _viewModel.AddressText,
            _viewModel.ActiveTab.NavigationAddress, _viewModel.CurrentFilePath,
            _viewModel.ActiveTab.Id, tabs);
    }

    internal void RestoreState(BrowserPageState? state)
    {
        if (state is null) return;
        if (state.Version is not (1 or 2))
        {
            _viewModel.ResetTabs(new[] { _viewModel.ActiveTab.Id });
            _viewModel.AddressText = "";
            _viewModel.ActiveTab.NavigateCommand.Execute(null);
            _viewModel.ReportStatus("浏览器页面状态版本不受支持，已恢复空页。");
            return;
        }
        if (state.Version == 1)
        {
            // 旧 v1 单页状态：仍然恢复，不丢用户旧配置。
            RestoreIntoTab(_viewModel.ActiveTab, state.FilePath, state.NavigationAddress, state.AddressDraft);
            return;
        }
        // 过滤 null/空白记录并成对归一化 ID：重复 ID 保留首个合法条目，后续重命名而不是丢弃。
        var savedTabs = NormalizeTabs(state.Tabs, state);
        // 整体重建页签：旧页签的宿主被释放，新页签按保存的 id 建独立宿主。
        var restored = _viewModel.ResetTabs(savedTabs.Select(tab => tab.Id).ToArray());
        for (var index = 0; index < savedTabs.Count; index++)
            RestoreIntoTab(restored[index], savedTabs[index].FilePath, savedTabs[index].NavigationAddress, savedTabs[index].AddressDraft);
        var active = restored.FirstOrDefault(tab => tab.Id == state.ActiveTabId) ?? restored[0];
        _viewModel.SelectTab(active);
        UpdateHostVisibility();
    }

    /// <summary>
    /// 把持久化页签记录归一化成“合法且 ID 唯一”的列表：
    /// 过滤 null/空白条目，重复 ID 的首个条目保留原 ID，其余重命名；至少保留一个可恢复条目。
    /// 不因个别坏数据丢弃全部配置。
    /// </summary>
    private static IReadOnlyList<BrowserTabState> NormalizeTabs(IReadOnlyList<BrowserTabState>? tabs, BrowserPageState state)
    {
        var entries = (tabs ?? Array.Empty<BrowserTabState>()).Where(tab => tab is not null).ToList();
        // 先预留全部合法原 ID，生成的替代 ID 不得抢占后面合法原条目的身份。
        var reserved = new HashSet<string>(entries
            .Where(tab => !string.IsNullOrWhiteSpace(tab.Id))
            .Select(tab => tab.Id!), StringComparer.Ordinal);
        var result = new List<BrowserTabState>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        var counter = 0;
        foreach (var tab in entries)
        {
            var id = tab.Id;
            if (string.IsNullOrWhiteSpace(id) || !used.Add(id))
            {
                // 空 ID 或重复 ID：重命名到既未占用、也不与任何合法原 ID 冲突的稳定 ID，保留该条目的草稿/地址。
                do { id = "tab-restored-" + (++counter).ToString(); } while (reserved.Contains(id) || !used.Add(id));
            }
            result.Add(tab with { Id = id });
        }
        if (result.Count == 0)
        {
            // 空列表或全部为坏记录：留下一个可用的空页，同时保留 v1 的顶层字段（若有）。
            result.Add(new BrowserTabState("tab-restored-1", state.AddressDraft ?? "", state.NavigationAddress, state.FilePath, ""));
        }
        return result;
    }

    private static void RestoreIntoTab(BrowserTab tab, string? filePath, string? navigationAddress, string addressDraft)
    {
        if (!string.IsNullOrWhiteSpace(filePath)) tab.PreviewFile(filePath);
        else if (!string.IsNullOrWhiteSpace(navigationAddress))
        {
            tab.AddressText = navigationAddress;
            tab.NavigateCommand.Execute(null);
        }
        tab.AddressText = addressDraft ?? "";
    }

    /// <summary>直接向当前页签推送本地路径进入文件预览模式。</summary>
    public void PreviewFile(string? path) => _viewModel.PreviewFile(path);

    /// <summary>seam 上声明的 x:Name，D 轨据此用 TryPreviewFile 定位（见 docs/archive/progress/progress-e.md）。</summary>
    public const string PreviewControlName = "PreviewBrowser";

    /// <summary>
    /// E 侧入口：D 轨在 Files tab 选中文件时调用，把本地路径推给内置浏览器预览。
    /// 右栏未挂载或 Preview 未启用时返回 false 且不抛异常——调用方据此决定是否给用户提示，
    /// 不要改成抛异常（右栏可整体收起，这是正常状态而非错误）。
    /// </summary>
    public static bool TryPreviewFile(Control? panel, string? path)
    {
        if (panel is null || string.IsNullOrWhiteSpace(path)) return false;
        BrowserView? view;
        try
        {
            // FindControl 在没有 NameScope 的控件上会抛 InvalidOperationException 而不是返回 null，
            // 右栏可整体收起/尚未初始化，这里统一按“不可用”处理。
            view = Avalonia.Controls.NameScope.GetNameScope(panel) is null
                ? null
                : panel.FindControl<BrowserView>(PreviewControlName);
        }
        catch (InvalidOperationException) { return false; }
        if (view is null) return false;
        view.PreviewFile(path);
        return true;
    }

    private BrowserViewHost HostFor(BrowserTab tab)
    {
        if (_hosts.TryGetValue(tab.Id, out var existing)) return existing;
        var host = new BrowserViewHost();
        host.SetUserAgent(_settings.UserAgent);
        host.SetOpenLinksExternally(_siteRules.OpensWebExternally);
        host.OpenExternallyRequested += (_, address) => _ = OpenExternalAsync(address);
        host.AcceleratorHandler = (key, modifiers) => NativeShortcutHandler?.Invoke(key, modifiers) == true;
        host.NavigationStateChanged += (_, state) => _viewModel.ApplyNavigationStateTo(tab, state.CanGoBack, state.CanGoForward, state.Loading, state.Source);
        host.NavigationCompleted += (_, state) =>
        {
            if (state.IsSuccess) _viewModel.RecordVisit(state.Source, state.Title);
        };
        host.ContextMenuActionRequested += (_, action) => HandleContextMenuAction(tab, host, action);
        host.DownloadRequested += (_, request) => _ = HandleDownloadRequestAsync(request);
        host.DownloadProgressChanged += (_, progress) => ApplyDownloadProgress(progress);
        host.PermissionRequested += (_, request) => _ = HandlePermissionRequestAsync(request);
        host.DocumentTitleChanged += (_, title) => _viewModel.ApplyDocumentTitleTo(tab, title);
        host.StatusChanged += (_, message) => _viewModel.ReportStatusTo(tab, message);
        host.RuntimeProbeCompleted += (_, probe) => _viewModel.ApplyRuntimeProbe(probe);
        // 页面弹出新窗口 → 新页签接管同一个 CoreWebView2，保留 opener/postMessage/OAuth 弹窗关系。
        host.NewWindowRequested += (_, request) => _ = HandleNewWindowAsync(request);
        _hosts[tab.Id] = host;
        _hostLayer.Children.Add(host);
        _lastHost = host;
        return host;
    }

    private void ReleaseHost(BrowserTab tab)
    {
        if (!_hosts.TryGetValue(tab.Id, out var host)) return;
        InvalidateFind(tab.Id, resetDocumentEpoch: true);
        _hosts.Remove(tab.Id);
        _hostLayer.Children.Remove(host);
        _findGeneration.Remove(tab.Id);
        _findDocumentEpoch.Remove(tab.Id);
        host.Dispose();
        UpdateHostVisibility();
    }

    private void UpdateHostVisibility()
    {
        _viewModel.RefreshFacade();
        foreach (var entry in _hosts)
            entry.Value.SetOccluded(!ReferenceEquals(entry.Key, _viewModel.ActiveTab.Id));
    }

    private void OnTabSelectionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(BrowserViewModel.ActiveTab)) return;
        var tab = _viewModel.ActiveTab;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_disposed || !ReferenceEquals(tab, _viewModel.ActiveTab)) return;
            var selected = this.GetVisualDescendants().OfType<Button>().FirstOrDefault(button => button.Classes.Contains("browserTab") && ReferenceEquals(button.DataContext, tab));
            if (selected?.Parent is Control row) row.BringIntoView();
        }, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    private void OnTabCreated(object? sender, BrowserTab tab)
    {
        HostFor(tab);
        UpdateHostVisibility();
    }

    private void OnTabClosed(object? sender, BrowserTab tab) => ReleaseHost(tab);

    private void OnNavigationRequested(object? sender, (BrowserTab Tab, BrowserNavigationRequest Request) e)
    {
        // 导航到新文档：旧查找会话与其迟到回调全部失效，同 query 也必须重新 Start。
        _suppressSuggestionsUntil = DateTime.UtcNow.AddSeconds(2);
        _viewModel.CloseSuggestions();
        InvalidateFind(e.Tab.Id, resetDocumentEpoch: true);
        e.Tab.FindStatus = string.Empty;
        var host = HostFor(e.Tab);
        UpdateHostVisibility();
        if (e.Request.Document is not null) host.NavigateToDocument(e.Request.Document);
        else host.Navigate(e.Request.Address);
    }

    private void OnActionRequested(object? sender, (BrowserTab Tab, BrowserTabAction Action) e) => ApplyAction(e.Tab, e.Action);

    private void OnZoomRequested(object? sender, (BrowserTab Tab, double Factor) e)
    {
        if (_hosts.TryGetValue(e.Tab.Id, out var host)) host.SetZoomPercent(e.Factor * 100.0);
    }

    private void OnAddressCopyRequested(object? sender, string address) => _ = CopyAddressAsync(address);
    private void OnExternalOpenRequested(object? sender, string address) => _ = OpenExternalAsync(address);
    private void OnShareRequested(object? sender, string address) => _ = CopyAddressAsync(address);

    private void ApplyAction(BrowserTab tab, BrowserTabAction action)
    {
        if (!_hosts.TryGetValue(tab.Id, out var host)) return;
        switch (action)
        {
            case BrowserTabAction.Back: host.GoBack(); break;
            case BrowserTabAction.Forward: host.GoForward(); break;
            case BrowserTabAction.Reload: host.Reload(); break;
            case BrowserTabAction.Stop: host.Stop(); break;
            case BrowserTabAction.CloseFind:
                InvalidateFind(tab.Id, resetDocumentEpoch: false);
                host.StopFind();
                tab.FindStatus = string.Empty;
                break;
            case BrowserTabAction.OpenDevTools:
                if (!host.OpenDevTools()) _viewModel.ReportStatusTo(tab, "当前页面暂不能打开开发工具。");
                break;
            case BrowserTabAction.FindNext:
            case BrowserTabAction.FindPrevious:
                ApplyFind(tab, host, action);
                break;
        }
    }

    private void ApplyFind(BrowserTab tab, BrowserViewHost host, BrowserTabAction action)
    {
        if (!host.PageFindAvailable)
        {
            tab.FindStatus = "当前运行时无查找";
            return;
        }
        var epoch = _findDocumentEpoch.GetValueOrDefault(tab.Id, "0");
        var started = _findStarted.GetValueOrDefault(tab.Id);
        if (started is null || !string.Equals(started, tab.FindQuery + "\u0000" + epoch, StringComparison.Ordinal))
        {
            if (!host.TryStartFind(tab.FindQuery, out var note)) { tab.FindStatus = note; return; }
            _findStarted[tab.Id] = tab.FindQuery + "\u0000" + epoch;
        }
        if (action == BrowserTabAction.FindNext) host.FindNext();
        else host.FindPrevious();
        // Find 的 MatchCount/ActiveMatchIndex 由运行时异步更新，这里轮询真实值而不是读一次快照。
        var generation = BumpFindGeneration(tab.Id);
        _ = RefreshFindStatusAsync(tab, host, tab.FindQuery, generation);
    }

    private int BumpFindGeneration(string tabId)
    {
        var next = _findGeneration.GetValueOrDefault(tabId, 0) + 1;
        _findGeneration[tabId] = next;
        return next;
    }

    /// <summary>关闭查找/更换 query/导航/关闭 tab/Dispose 时调用：隔离所有已失效的迟到回调。</summary>
    private void InvalidateFind(string tabId, bool resetDocumentEpoch)
    {
        BumpFindGeneration(tabId);
        _findStarted.Remove(tabId);
        if (resetDocumentEpoch) _findDocumentEpoch[tabId] = Guid.NewGuid().ToString("N");
    }

    private async Task RefreshFindStatusAsync(BrowserTab tab, BrowserViewHost host, string query, int generation)
    {
        // Start 完成后计数才填充；先等一小段，避免读到上一个查询的陈旧计数。
        await Task.Delay(400);
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (_findGeneration.GetValueOrDefault(tab.Id, 0) != generation) return;
            if (!string.Equals(tab.FindQuery, query, StringComparison.Ordinal)) return;
            var status = host.FindStatus;
            if (status is { MatchCount: > 0 } value)
            {
                // 本运行时的 ActiveMatchIndex 为 1 基；为 0/负值时保守显示为第 1 个。
                var active = value.ActiveMatchIndex <= 0 ? 1 : value.ActiveMatchIndex;
                tab.FindStatus = $"{active}/{value.MatchCount}";
                return;
            }
            await Task.Delay(150);
        }
        if (_findGeneration.GetValueOrDefault(tab.Id, 0) != generation) return;
        tab.FindStatus = host.PageFindAvailable ? "无匹配" : "当前运行时无查找";
    }

    private async Task CopyAddressAsync(string address)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) { _viewModel.ReportStatus("当前环境没有可用剪贴板。"); return; }
        try
        {
            await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, address);
            _viewModel.ReportStatus("已复制当前地址。");
        }
        catch (Exception error) { _viewModel.ReportStatus("复制失败：" + error.Message); }
    }

    private async Task OpenExternalAsync(string address)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) { _viewModel.ReportStatus("当前环境无法打开外部链接。"); return; }
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri)) { _viewModel.ReportStatus("地址无效，无法外部打开。"); return; }
        try { await topLevel.Launcher.LaunchUriAsync(uri); _viewModel.ReportStatus("已在外部打开。"); }
        catch (Exception error) { _viewModel.ReportStatus("外部打开失败：" + error.Message); }
    }

    private void Address_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Down or Key.Up)
        {
            if (_viewModel.IsSuggestionsOpen && !string.IsNullOrWhiteSpace(_viewModel.AddressText))
            {
                // 首次按下方向键时按当前草稿刷新候选（TextChanged 不触发时也能拿到结果）。
                if (_viewModel.Suggestions.Count == 0) _viewModel.RefreshSuggestions(_viewModel.AddressText);
                _viewModel.MoveSuggestion(e.Key == Key.Down ? 1 : -1);
                e.Handled = true;
                return;
            }
        }
        if (e.Key == Key.Escape && _viewModel.IsSuggestionsOpen)
        {
            _viewModel.CloseSuggestions();
            e.Handled = true;
            return;
        }
        if (e.Key is not (Key.Return or Key.Enter)) return;
        if (_viewModel.IsSuggestionsOpen && _viewModel.SelectedSuggestion is { } selected)
        {
            _viewModel.AcceptSuggestionCommand.Execute(selected);
            e.Handled = true;
            return;
        }
        _viewModel.CloseSuggestions();
        if (_viewModel.NavigateCommand.CanExecute(null)) _viewModel.NavigateCommand.Execute(null);
    }

    private void Address_TextChanged(object? sender, TextChangedEventArgs e)
    {
        var text = _viewModel.AddressText;
        // 导航回写地址时也会触发 TextChanged：此时不重新打开候选，避免刚按 Enter 又弹一层。
        if (_viewModel.IsLoading || DateTime.UtcNow < _suppressSuggestionsUntil)
        {
            _viewModel.CloseSuggestions();
            return;
        }
        if (string.IsNullOrWhiteSpace(text) || (sender is TextBox box && !box.IsFocused))
        {
            _viewModel.CloseSuggestions();
            return;
        }
        if (_viewModel.ActiveTab.IsFileMode) { _viewModel.CloseSuggestions(); return; }
        _viewModel.RefreshSuggestions(text);
    }

    private DateTime _suppressSuggestionsUntil;

    private void Find_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Return or Key.Enter)) return;
        if (_viewModel.FindNextCommand.CanExecute(null)) _viewModel.FindNextCommand.Execute(null);
    }

    private void Download_Click(object? sender, RoutedEventArgs e) => _viewModel.ToggleDownloadsCommand.Execute(null);

    private void CloseDownloads_Click(object? sender, RoutedEventArgs e) => _viewModel.CloseDownloadsCommand.Execute(null);

    private void CancelDownload_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: string id }) return;
        var cancelled = false;
        foreach (var host in _hosts.Values) cancelled |= host.CancelDownload(id);
        _viewModel.ReportStatus(cancelled ? "已取消下载。" : "该下载已结束，无法取消。");
    }

    private void More_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control anchor) return;
        var menu = new ContextMenu();
        var copy = new MenuItem { Header = "复制当前地址" };
        copy.Click += (_, _) => _viewModel.CopyAddressCommand.Execute(null);
        menu.Items.Add(copy);
        var open = new MenuItem { Header = "在默认浏览器打开" };
        open.Click += (_, _) => _viewModel.OpenExternalCommand.Execute(null);
        menu.Items.Add(open);
        var share = new MenuItem { Header = "分享当前地址（复制链接）" };
        share.Click += (_, _) => _viewModel.ShareCommand.Execute(null);
        menu.Items.Add(share);
        var bookmark = new MenuItem { Header = "收藏 / 取消收藏当前页面" };
        bookmark.Click += (_, _) => _viewModel.ToggleBookmarkCommand.Execute(null);
        menu.Items.Add(bookmark);
        menu.Items.Add(new Separator());
        var find = new MenuItem { Header = "页内查找 (Ctrl+F)" };
        find.Click += (_, _) => _viewModel.ToggleFindCommand.Execute(null);
        menu.Items.Add(find);
        var zoomIn = new MenuItem { Header = "放大 (+10%)" };
        zoomIn.Click += (_, _) => _viewModel.ZoomInCommand.Execute(null);
        menu.Items.Add(zoomIn);
        var zoomOut = new MenuItem { Header = "缩小 (-10%)" };
        zoomOut.Click += (_, _) => _viewModel.ZoomOutCommand.Execute(null);
        menu.Items.Add(zoomOut);
        var zoomReset = new MenuItem { Header = $"重置缩放 (当前 {_viewModel.ZoomPercent}%)" };
        zoomReset.Click += (_, _) => _viewModel.ZoomResetCommand.Execute(null);
        menu.Items.Add(zoomReset);
        menu.Items.Add(new Separator());
        var downloads = new MenuItem { Header = "下载列表" };
        downloads.Click += (_, _) => _viewModel.ToggleDownloadsCommand.Execute(null);
        menu.Items.Add(downloads);
        var annotate = new MenuItem { Header = "标注页面区域…" };
        annotate.Click += (_, _) =>
        {
            if (_hosts.TryGetValue(_viewModel.ActiveTab.Id, out var host))
                _ = StartAnnotationAsync(_viewModel.ActiveTab, host);
        };
        menu.Items.Add(annotate);
        var fullView = new MenuItem { Header = _viewModel.FullViewToolTip };
        fullView.Click += (_, _) => _viewModel.ToggleFullViewCommand.Execute(null);
        menu.Items.Add(fullView);
        var history = new MenuItem { Header = "显示最近访问" };
        history.Click += (_, _) => _viewModel.RefreshSuggestions(string.Empty);
        menu.Items.Add(history);
        menu.Items.Add(new Separator());
        var devtools = new MenuItem { Header = "开发者工具" };
        devtools.Click += (_, _) => _viewModel.OpenDevToolsCommand.Execute(null);
        menu.Items.Add(devtools);
        menu.Open(anchor);
    }

    private async void InspectElement_Click(object? sender, RoutedEventArgs e)
    {
        var host = _lastHost ?? (_hosts.TryGetValue(_viewModel.ActiveTab.Id, out var h) ? h : null);
        if (host is null || !host.IsReady)
        {
            _viewModel.ReportStatus("请先加载网页再拾取元素。");
            return;
        }
        const string inspectScript = "(function(){if(window.__fufuInspectorActive){window.__fufuInspectorCleanup?.();return 'Inspector deactivated';}window.__fufuInspectorActive=true;let o=document.createElement('div');o.style.position='fixed';o.style.pointerEvents='none';o.style.border='2px solid #0078D4';o.style.background='rgba(0,120,212,0.2)';o.style.zIndex='999999';document.body.appendChild(o);function onMove(e){let r=e.target?.getBoundingClientRect();if(!r)return;o.style.top=r.top+'px';o.style.left=r.left+'px';o.style.width=r.width+'px';o.style.height=r.height+'px';}function onClick(e){e.preventDefault();e.stopPropagation();let sel=(e.target?.id)?'#'+e.target.id:(e.target?.className)?'.'+e.target.className.split(' ').join('.'):e.target?.tagName?.toLowerCase()||'element';cleanup();navigator.clipboard?.writeText(sel);alert('已拾取元素并复制到剪贴板: '+sel);}function onKey(e){if(e.key==='Escape')cleanup();}function cleanup(){window.__fufuInspectorActive=false;o.remove();document.removeEventListener('mousemove',onMove,true);document.removeEventListener('click',onClick,true);document.removeEventListener('keydown',onKey,true);}window.__fufuInspectorCleanup=cleanup;document.addEventListener('mousemove',onMove,true);document.addEventListener('click',onClick,true);document.addEventListener('keydown',onKey,true);return 'Inspector activated';})();";
        var result = await host.ExecuteScriptAsync(inspectScript);
        _viewModel.ReportStatus(result?.Contains("activated") == true ? "元素检查器已激活：悬停高亮，点击复制选择器，按 Esc 退出。" : "元素检查器已关闭。");
    }

    private async void OpenFile_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is not { } storage) { _viewModel.ReportStatus("当前环境无法打开文件选择器。"); return; }
        try
        {
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择要预览的文件",
                AllowMultiple = false,
            });
            var file = files?.FirstOrDefault();
            if (file is null) return;
            var path = file.TryGetLocalPath();
            if (string.IsNullOrEmpty(path)) { _viewModel.ReportStatus("无法读取该文件的本地路径。"); return; }
            _viewModel.PreviewFile(path);
        }
        catch (Exception error)
        {
            _viewModel.ReportStatus("打开文件失败：" + error.Message);
        }
    }

    private async void OpenRuntimeDownload_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) { _viewModel.ReportStatus("当前环境无法打开外部链接。"); return; }
        try { await topLevel.Launcher.LaunchUriAsync(new Uri(_viewModel.RuntimeLink)); }
        catch (Exception error) { _viewModel.ReportStatus("打开下载页失败：" + error.Message); }
    }

    private async void CopyRuntimeLink_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.Clipboard is not { } clipboard) { _viewModel.ReportStatus("当前环境没有可用剪贴板。"); return; }
        try
        {
            await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, _viewModel.RuntimeLink);
            _viewModel.ReportStatus("已复制下载链接。");
        }
        catch (Exception error) { _viewModel.ReportStatus("复制失败：" + error.Message); }
    }
}
