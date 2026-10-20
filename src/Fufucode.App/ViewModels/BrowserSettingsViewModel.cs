using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using Fufucode.App.Infrastructure;
using Fufucode.App.Infrastructure.Browser;

namespace Fufucode.App.ViewModels;

/// <summary>
/// 【设置 → 浏览器】的真源：把 <see cref="BrowserSettingsStore"/>（&lt;dataRoot&gt;/browser-settings.json）
/// 投影成可绑定的选项，并在每次改动后原子落盘。所有"已保存/失败"结论来自真实写入结果，
/// 不把内存态当成已落盘。清除浏览数据、下载历史都走真实文件与真实删除结果。
/// </summary>
public sealed partial class BrowserSettingsViewModel : ViewModelBase
{
    private BrowserSettingsStore? _store;
    private string? _dataRoot;
    private bool _suppressPersist;

    public event Action<BrowserSettings>? Changed;

    public BrowserSettingsViewModel()
    {
        AllowedSites = new ObservableCollection<BrowserSitePermission>();
        DownloadEntries = new ObservableCollection<BrowserDownloadEntry>();
    }

    public string SettingsPath => _store?.Path ?? "（未连接数据根，改动仅保留在内存）";
    public bool IsConnected => _store is not null;

    /// <summary>未打包 WebView2 扩展目录；不存在时由 Attach 创建。</summary>
    private string _extensionsDirectory = "";
    public string ExtensionsDirectory => _extensionsDirectory;

    /// <summary>设置文件加载失败的真实原因；null 表示无错误。</summary>
    public string? LoadError => _store?.LastLoadError;
    public bool HasLoadError => !string.IsNullOrEmpty(LoadError);

    private string _status = "";
    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    // ── 顶部主开关 ──
    /// <summary>让智能体控制内置浏览器；关闭时下级配置保持可见但不生效。</summary>
    public bool BrowserUseEnabled
    {
        get => Current.BrowserUseEnabled;
        set => Set(value, _ => { }, v => Persist(s => s with { BrowserUseEnabled = v }));
    }

    // ── 常规 ──
    public int WebUrlOpenTargetIndex
    {
        get => IndexOf(BrowserSettings.WebUrlTargets, Current.WebUrlOpenTarget);
        set => Set(value, _ => { }, v => Persist(s => s with { WebUrlOpenTarget = Pick(BrowserSettings.WebUrlTargets, v, "builtin") }));
    }

    public int LocalUrlOpenTargetIndex
    {
        get => IndexOf(BrowserSettings.WebUrlTargets, Current.LocalUrlOpenTarget);
        set => Set(value, _ => { }, v => Persist(s => s with { LocalUrlOpenTarget = Pick(BrowserSettings.WebUrlTargets, v, "builtin") }));
    }

    public bool ShowFullUrl
    {
        get => Current.ShowFullUrl;
        set => Set(value, _ => { }, v => Persist(s => s with { ShowFullUrl = v }));
    }

    public int ScreenshotModeIndex
    {
        get => IndexOf(BrowserSettings.ScreenshotModes, Current.ScreenshotMode);
        set => Set(value, _ => { }, v => Persist(s => s with { ScreenshotMode = Pick(BrowserSettings.ScreenshotModes, v, "ask") }));
    }

    /// <summary>地址栏搜索模板；{0} 是转义后的搜索词。非法模板在保存时收敛回默认值。</summary>
    public string SearchUrlTemplate
    {
        get => Current.SearchUrlTemplate;
        set => Set(value, _ => { }, v => Persist(s => s with { SearchUrlTemplate = v ?? string.Empty }));
    }

    /// <summary>页面 UA；空 = WebView2 默认。仅影响网页显示，不改变下载/权限判断。</summary>
    public string UserAgent
    {
        get => Current.UserAgent;
        set => Set(value, _ => { }, v => Persist(s => s with { UserAgent = v ?? string.Empty }));
    }

    public bool AnnotationIncludeScreenshot
    {
        get => Current.AnnotationIncludeScreenshot;
        set => Set(value, _ => { }, v => Persist(s => s with { AnnotationIncludeScreenshot = v }));
    }

    public bool EnableAutofill
    {
        get => Current.EnableAutofill;
        set => Set(value, _ => { }, v => Persist(s => s with { EnableAutofill = v }));
    }

    public bool EnablePasswordAutosave
    {
        get => Current.EnablePasswordAutosave;
        set => Set(value, _ => { }, v => Persist(s => s with { EnablePasswordAutosave = v }));
    }

    // ── 设备自带浏览器扩展 ──

    public bool ExternalBrowserEnabled
    {
        get => Current.ExternalBrowserEnabled;
        set => Set(value, _ => { }, v => Persist(s => s with { ExternalBrowserEnabled = v }));
    }

    public int ExternalBrowserPort
    {
        get => Current.ExternalBrowserPort;
        set => Set(value, _ => { }, v => Persist(s => s with { ExternalBrowserPort = v }));
    }

    public string ExternalBrowserPin
    {
        get => Current.ExternalBrowserPin;
        set => Set(value, _ => { }, v => Persist(s => s with { ExternalBrowserPin = v }));
    }

    private string _externalBrowserStatus = "尚未启动";
    public string ExternalBrowserStatus { get => _externalBrowserStatus; private set => SetProperty(ref _externalBrowserStatus, value); }
    public int ExternalBrowserActualPort { get; private set; }
    public bool ExternalBrowserConnected { get; private set; }
    private string _externalBrowserExtensionPath = "";
    public string ExternalBrowserExtensionPath { get => _externalBrowserExtensionPath; private set => SetProperty(ref _externalBrowserExtensionPath, value); }

    public void ReportExternalBrowser(string status, int actualPort, bool connected, string extensionPath)
    {
        ExternalBrowserStatus = status;
        ExternalBrowserActualPort = actualPort;
        ExternalBrowserConnected = connected;
        ExternalBrowserExtensionPath = extensionPath;
        OnPropertyChanged(nameof(ExternalBrowserActualPort));
        OnPropertyChanged(nameof(ExternalBrowserConnected));
        OnPropertyChanged(nameof(ExternalBrowserExtensionPath));
    }

    [RelayCommand]
    private void RegenerateExternalBrowserPin()
    {
        var pin = Random.Shared.Next(0, 10000).ToString("D4");
        ExternalBrowserPin = pin;
    }

    // ── 下载 ──
    public bool PromptDownloadLocation
    {
        get => Current.PromptDownloadLocation;
        set => Set(value, _ => { }, v => Persist(s => s with { PromptDownloadLocation = v }));
    }

    public bool DownloadHistoryEnabled
    {
        get => Current.DownloadHistoryEnabled;
        set => Set(value, _ => { }, v => Persist(s => s with { DownloadHistoryEnabled = v }));
    }

    public string DefaultDownloadDirectory
    {
        get => Current.DefaultDownloadDirectory;
        set => Set(value, _ => { }, v => Persist(s => s with { DefaultDownloadDirectory = v?.Trim() ?? string.Empty }));
    }

    // ── 权限与智能体控制 ──
    public bool EnableSiteTools
    {
        get => Current.EnableSiteTools;
        set => Set(value, _ => { }, v => Persist(s => s with { EnableSiteTools = v }));
    }

    public int DefaultBrowsePermissionIndex
    {
        get => IndexOf(BrowserSettings.PermissionModes, Current.DefaultBrowsePermission);
        set => Set(value, _ => { }, v => Persist(s => s with { DefaultBrowsePermission = Pick(BrowserSettings.PermissionModes, v, "approval") }));
    }

    public int DefaultDownloadPermissionIndex
    {
        get => IndexOf(BrowserSettings.PermissionModes, Current.DefaultDownloadPermission);
        set => Set(value, _ => { }, v => Persist(s => s with { DefaultDownloadPermission = Pick(BrowserSettings.PermissionModes, v, "approval") }));
    }

    /// <summary>网站白名单/权限例外的可编辑文本：每行 "主机名=权限"，写入前逐行解析并过滤非法项。</summary>
    public string AllowedSitesText
    {
        get => _allowedSitesText;
        set => Set(value, v => _allowedSitesText = v, v => Persist(s => s with { AllowedSites = ParseSites(v) }));
    }
    private string _allowedSitesText = "";

    /// <summary>开发者模式：完整 CDP 访问权限。默认关闭，与设置页风险提示一致。</summary>
    public bool EnableFullCdpAccess
    {
        get => Current.EnableFullCdpAccess;
        set => Set(value, _ => { }, v => Persist(s => s with { EnableFullCdpAccess = v }));
    }

    public ObservableCollection<BrowserSitePermission> AllowedSites { get; }
    public ObservableCollection<BrowserDownloadEntry> DownloadEntries { get; }
    public string DownloadHistoryStatus { get; private set; } = "暂无下载记录";

    // ── 清除浏览数据 ──
    public string ClearDataStatus { get; private set; } = "";
    public bool HasClearDataStatus => ClearDataStatus.Length > 0;

    /// <summary>连接数据根：读取真实 <c>browser-settings.json</c>，并把改动回写。</summary>
    public void Attach(string? dataRoot)
    {
        _dataRoot = dataRoot;
        try
        {
            _store = dataRoot is { Length: > 0 } root ? new BrowserSettingsStore(root) : null;
            if (dataRoot is { Length: > 0 })
            {
                try { _extensionsDirectory = BrowserExtensionLoader.EnsureDirectory(BrowserUserDataLayout.Resolve(dataRoot)); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    _extensionsDirectory = BrowserExtensionLoader.ResolveDirectory(BrowserUserDataLayout.Resolve(dataRoot));
                    Status = "浏览器扩展目录不可用：" + error.Message;
                }
            }
        }
        catch (Exception error)
        {
            _store = null;
            Status = "浏览器设置存储不可用：" + error.GetBaseException().Message;
        }
        _suppressPersist = true;
        try
        {
            if (_store is { Current: { } settings })
            {
                _allowedSitesText = FormatSites(settings.AllowedSites);
                AllowedSites.Clear();
                foreach (var site in settings.AllowedSites) AllowedSites.Add(site);
            }
            else
            {
                _allowedSitesText = "";
                AllowedSites.Clear();
            }
            RefreshDownloadHistory();
        }
        finally { _suppressPersist = false; }
        NotifyAll();
    }

    private BrowserSettings Current => _store?.Current ?? BrowserSettings.Default;

    /// <summary>当前生效的完整设置快照（只读），供浏览器视图读取门禁与显示策略。</summary>
    public BrowserSettings CurrentSettings => Current;

    /// <summary>
    /// 一次改动 = 归一化 + 原子落盘 + 广播。先更新内存投影，再真实写入；
    /// 未连接数据根时如实提示"未落盘"，不假装已保存。
    /// </summary>
    private void Set<T>(T value, Action<T> assign, Action<T> persist)
    {
        if (_suppressPersist) { assign(value); return; }
        assign(value);
        persist(value);
    }

    /// <summary>把改动写入磁盘并广播；忽略 <paramref name="unused"/>，只为让调用点保持统一形态。</summary>
    private void Persist(Func<BrowserSettings, BrowserSettings> change)
    {
        _ = change;
        if (_store is null)
        {
            Status = "未连接数据根：改动未写入 browser-settings.json。";
            NotifyAll();
            return;
        }
        var saved = _store.Save(change(_store.Current));
        Status = _store.LastLoadError is { } error
            ? "保存失败：" + error
            : "已保存到 " + _store.Path;
        NotifyAll();
        Changed?.Invoke(saved);
    }

    public void ClearCache() => Clear(BrowserDataScope.Cache, "缓存");
    public void ClearCookies() => Clear(BrowserDataScope.Cookies, "Cookie");
    public void ClearSiteData() => Clear(BrowserDataScope.SiteData, "网站数据");
    public void ClearAll() => Clear(BrowserDataScope.All, "全部浏览数据");

    private void Clear(BrowserDataScope scope, string label)
    {
        if (_dataRoot is not { Length: > 0 } root)
        {
            ClearDataStatus = "未连接数据根，无法清除" + label + "。";
            NotifyClear();
            return;
        }
        var report = BrowserUserDataLayout.ClearBrowsingData(root, scope);
        ClearDataStatus = report.Success
            ? $"已清除{label}：{report.Summary}"
            : $"清除{label}未完全成功：{report.Summary}；{string.Join("；", report.Failures.Take(3))}";
        NotifyClear();
    }

    /// <summary>刷新下载历史（真实读取 browser-downloads.json）。</summary>
    public void RefreshDownloadHistory()
    {
        DownloadEntries.Clear();
        if (_dataRoot is not { Length: > 0 } root)
        {
            DownloadHistoryStatus = "未连接数据根，下载历史不可用";
            OnPropertyChanged(nameof(DownloadHistoryStatus));
            OnPropertyChanged(nameof(DownloadEntries));
            return;
        }
        try
        {
            var history = new BrowserDownloadHistory(root);
            foreach (var entry in history.Entries) DownloadEntries.Add(entry);
            DownloadHistoryStatus = history.LastLoadError is { } error
                ? "下载历史加载失败：" + error
                : history.Summary;
        }
        catch (Exception error)
        {
            DownloadHistoryStatus = "下载历史不可用：" + error.GetBaseException().Message;
        }
        OnPropertyChanged(nameof(DownloadHistoryStatus));
        OnPropertyChanged(nameof(DownloadEntries));
    }

    private void NotifyClear()
    {
        OnPropertyChanged(nameof(ClearDataStatus));
        OnPropertyChanged(nameof(HasClearDataStatus));
    }

    private void NotifyAll()
    {
        foreach (var name in new[]
        {
            nameof(IsConnected), nameof(SettingsPath), nameof(LoadError), nameof(HasLoadError), nameof(Status),
            nameof(ExtensionsDirectory),
            nameof(BrowserUseEnabled), nameof(WebUrlOpenTargetIndex), nameof(LocalUrlOpenTargetIndex), nameof(ShowFullUrl),
            nameof(SearchUrlTemplate), nameof(UserAgent), nameof(AnnotationIncludeScreenshot),
            nameof(EnableAutofill), nameof(EnablePasswordAutosave),
            nameof(ExternalBrowserEnabled), nameof(ExternalBrowserPort), nameof(ExternalBrowserPin),
            nameof(ScreenshotModeIndex), nameof(PromptDownloadLocation), nameof(DownloadHistoryEnabled),
            nameof(DefaultDownloadDirectory), nameof(EnableSiteTools), nameof(DefaultBrowsePermissionIndex),
            nameof(DefaultDownloadPermissionIndex), nameof(AllowedSitesText), nameof(EnableFullCdpAccess),
        }) OnPropertyChanged(name);
    }

    private static int IndexOf(string[] values, string? current)
    {
        var index = Array.FindIndex(values, v => string.Equals(v, current, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? 0 : index;
    }

    private static string Pick(string[] values, int index, string fallback) =>
        index >= 0 && index < values.Length ? values[index] : fallback;

    /// <summary>逐行解析 "主机名=权限" 或 "主机名"（默认继承全局浏览权限）。空白行与非法主机名被跳过。</summary>
    internal static IReadOnlyList<BrowserSitePermission> ParseSites(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<BrowserSitePermission>();
        var result = new List<BrowserSitePermission>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in text.Split(new[] { '\r', '\n', ',', ';', '，', '；' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            var parts = line.Split('=', 2);
            var host = parts[0].Trim();
            var permission = parts.Length == 2 ? parts[1].Trim().ToLowerInvariant() : "approval";
            var site = new BrowserSitePermission(host, permission);
            if (!site.IsUsable || !seen.Add(host)) continue;
            result.Add(site.Normalize());
        }
        return result;
    }

    internal static string FormatSites(IReadOnlyList<BrowserSitePermission> sites)
    {
        if (sites is null || sites.Count == 0) return "";
        return string.Join(Environment.NewLine, sites.Select(site => site.Normalize().Host + " = " + site.Normalize().Permission));
    }
}
