using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fufucode.App.Features.Backend;
using Fufucode.Core;
using Fufucode.Core.Backend;
using Fufucode.Core.Plugins;

namespace Fufucode.App.ViewModels;

public sealed partial class PluginItemViewModel : ObservableObject
{
    public PluginDefinition Definition { get; }
    public string Id => Definition.Id;
    public string Name => Definition.Name;
    public string Description => Definition.Description;
    public string Version => Definition.Version;
    public string Author => Definition.Author;
    public string CategoryName => Definition.Category switch
    {
        PluginCategory.BackendEngine => "后端内核",
        PluginCategory.AgentCapability => "智能体能力",
        PluginCategory.Service => "后台服务",
        PluginCategory.Automation => "工作流与自动化",
        PluginCategory.Theme => "个性皮肤",
        _ => "工具扩展",
    };
    public string PermissionsSummary => Definition.RequiredPermissions.Count > 0
        ? "权限需求：" + string.Join("、", Definition.RequiredPermissions)
        : "无需特殊系统权限";
    public bool HasPermissions => Definition.RequiredPermissions.Count > 0;
    public bool IsCoreBuiltin => Definition.IsCoreBuiltin;
    [ObservableProperty] private string _statusMessage = "";

    private bool _isInstalled;
    public bool IsInstalled
    {
        get => _isInstalled;
        set
        {
            if (SetProperty(ref _isInstalled, value))
            {
                OnPropertyChanged(nameof(StatusBadgeText));
                OnPropertyChanged(nameof(ActionButtonText));
                OnPropertyChanged(nameof(CanUninstall));
            }
        }
    }

    private bool _isEnabled;
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (SetProperty(ref _isEnabled, value))
            {
                OnPropertyChanged(nameof(StatusBadgeText));
            }
        }
    }

    public string StatusBadgeText => !IsInstalled ? "未安装" : IsEnabled ? "已启用" : "已停用";
    public string ActionButtonText => !IsInstalled ? "安装" : IsCoreBuiltin ? "核心内置" : "卸载";
    public bool CanUninstall => !IsCoreBuiltin && IsInstalled;
    public bool IsSubagent => string.Equals(Id, "workspace.subagents", StringComparison.OrdinalIgnoreCase);
    public string SubagentTier => NativeSubagentRegistry.Tier.ToString().ToLowerInvariant();
    public bool ShowSubagentTier => IsSubagent && IsInstalled && IsEnabled;
    public string SubagentTierLabel => "当前档位：" + SubagentTier;

    public void RefreshSubagentTier() => OnPropertyChanged(nameof(SubagentTier));

    public PluginItemViewModel(PluginDefinition definition, bool isInstalled, bool isEnabled)
    {
        Definition = definition;
        _isInstalled = isInstalled;
        _isEnabled = isEnabled;
    }
}

/// <summary>市场一级页的分类入口卡：分类名 + 总数 + 已装数。</summary>
public sealed record MarketplaceGroupViewModel(string Title, int Count, int InstalledCount)
{
    /// <summary>分类 id（进二级页的路由段），与 MarketplaceGroupTitle 反向对应。</summary>
    public string Id => Title switch
    {
        "后端内核" => "backend",
        "智能体能力" => "capability",
        "后台服务" => "service",
        "工作流与自动化" => "automation",
        "个性皮肤" => "theme",
        _ => "tools",
    };
    public string Summary => $"{Count} 个插件 · {InstalledCount} 个已安装";
}

public sealed partial class SettingsPageViewModel
{
    // ════════════════════ 手机远程控制 ════════════════════
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RemoteToggleButtonText))]
    private bool _isRemoteRunning;

    [ObservableProperty] private string? _remotePairingUrl;
    [ObservableProperty] private string? _remotePin;
    [ObservableProperty] private string? _remoteQrSvg;
    [ObservableProperty] private bool _showRemoteQr;
    [ObservableProperty] private string _remoteStatusText = "未启动";

    public string RemoteToggleButtonText => IsRemoteRunning ? "停止服务" : "启动服务";
    public Func<(Fufucode.App.Abstractions.IChatSession Session, string Title)>? RemoteSession { get; set; }
    public Func<IReadOnlyList<Fufucode.App.Abstractions.ChatMessage>> RemoteHistory { get; set; } = () => [];

    public bool ShowRemoteCard => ShowNetwork && MatchesOption("网络与端点 手机 远程 控制 remote phone pairing PIN 二维码");

    [RelayCommand]
    public async Task ToggleRemoteControlAsync()
    {
        var dataRoot = _context?.DataRoot ?? Program.SelfTestDataRoot;
        if (!Features.Plugins.PluginAccess.IsPluginActive(dataRoot, Features.Plugins.PluginAccess.RemoteControlPluginId))
        {
            RemoteStatusText = "未安装：请在插件市场中安装“手机远程控制服务”。";
            return;
        }

        var host = PluginPackageManager.ActiveRemoteControlService;
        if (host is null)
        {
            RemoteStatusText = "远程控制服务未激活";
            return;
        }

        if (host.IsRunning)
        {
            host.Stop();
            IsRemoteRunning = false;
            RemotePairingUrl = null;
            RemotePin = null;
            RemoteQrSvg = null;
            ShowRemoteQr = false;
            RemoteStatusText = "未启动";
        }
        else
        {
            try
            {
                if (RemoteSession is null)
                    throw new InvalidOperationException("当前没有可绑定的会话。");
                var current = RemoteSession();
                host.Attach(current.Session, current.Title);
                host.LoadHistory(RemoteHistory());
                var status = await host.StartAsync(0);
                IsRemoteRunning = true;
                RemotePairingUrl = status.PairingUrl;
                RemotePin = status.Pin;
                RemoteQrSvg = host.PairingQrSvg;
                RemoteStatusText = $"已在端口 {status.Port} 启动";
            }
            catch (Exception ex)
            {
                RemoteStatusText = "启动失败：" + ex.Message;
            }
        }
    }

    [RelayCommand]
    public void ToggleRemoteQr()
    {
        ShowRemoteQr = !ShowRemoteQr;
    }

    // ════════════════════ 默认 Agent 内核后端（NativeCSharp 为原生核心，四后端为插件） ════════════════════
    [ObservableProperty] private int _selectedBackendIndex = 0; // 0=NativeCSharp, 1=Pi, 2=OMP, 3=Codex, 4=DSH, 5=Claude
    [ObservableProperty] private string _backendStatusText = "当前默认：FUMO（原生内核 · 纯 C# 实现 · 零外部依赖）";

    /// <summary>默认 Agent 内核只在「插件市场 → 后端内核」二级页出现；
    /// 新建聊天不再弹窗，直接使用这里的默认值。</summary>
    public bool ShowBackendCard => ShowMarketplaceGroup && MarketplaceGroupId == "backend";

    public IReadOnlyList<string> BackendEngineChoices { get; } =
    [
        "FUMO (默认原生内核 · 纯 C# · 紫色)",
        "Pi (后端插件 · 绿色)",
        "OMP (后端插件 · 橙色)",
        "Codex (后端插件 · 蓝色)",
        "DSH (后端插件 · 青色)",
        "Claude (后端插件 · 暖橙红)",
    ];

    private static EngineId EngineForIndex(int value) => value switch
    {
        1 => EngineId.Pi,
        2 => EngineId.Omp,
        3 => EngineId.Codex,
        4 => EngineId.Dsh,
        5 => EngineId.Claude,
        _ => EngineId.NativeCSharp,
    };

    partial void OnSelectedBackendIndexChanged(int value)
    {
        var engine = EngineForIndex(value);
        UpdateBackendStatus(engine);

        if (_context?.DataRoot is { } root && Directory.Exists(root))
        {
            try
            {
                var store = new BackendPreferenceStore(new AppDataLayout(root));
                store.SetDefault(engine);
            }
            catch { }
        }
    }

    private void UpdateBackendStatus(EngineId engine)
    {
        var badge = BackendBadgeCatalog.For(engine);
        BackendStatusText = $"当前默认：{badge.DisplayName}（{badge.BadgeColor}）" + BackendPluginSuffix(engine);
    }

    /// <summary>后端选择必须如实反映插件载荷状态；未安装时明确说明新会话会回退。</summary>
    private string BackendPluginSuffix(EngineId engine)
    {
        var pluginId = engine switch
        {
            EngineId.Pi => "backend.pi",
            EngineId.Omp => "backend.omp",
            EngineId.Codex => "backend.codex",
            EngineId.Dsh => "backend.dsh",
            EngineId.Claude => "backend.claude",
            _ => "core.csharp",
        };
        if (_pluginStore is null) return " · 插件状态未接线";
        var state = _pluginStore.GetState(pluginId);
        if (state.IsInstalled && state.IsEnabled) return " · 插件已启用";
        return state.IsInstalled
            ? " · 插件已停用；新建会话将回退 FUMO"
            : " · 插件未安装；新建会话将回退 FUMO";
    }

    private void SyncBackendPreference(string? dataRoot)
    {
        if (dataRoot is not null && Directory.Exists(dataRoot))
        {
            try
            {
                var store = new BackendPreferenceStore(new AppDataLayout(dataRoot));
                SelectedBackendIndex = store.DefaultBackend switch
                {
                    EngineId.Pi => 1,
                    EngineId.Omp => 2,
                    EngineId.Codex => 3,
                    EngineId.Dsh => 4,
                    EngineId.Claude => 5,
                    _ => 0,
                };
            }
            catch { }
        }
    }

    // ════════════════════ 插件市场（Plugin Marketplace） ════════════════════
    private PluginStateStore? _pluginStore;
    public ObservableCollection<PluginItemViewModel> MarketplacePlugins { get; } = new();

    /// <summary>市场首页（一级）：说明 + 状态 + 分类入口卡。</summary>
    public bool ShowMarketplace => SelectedCategory?.Id == "marketplace" && CurrentPageId.Length == 0;
    /// <summary>市场二级页：单个分类的插件列表。</summary>
    public bool ShowMarketplaceGroup => SelectedCategory?.Id == "marketplace" && CurrentPageId.StartsWith("marketplace.", StringComparison.Ordinal);

    /// <summary>当前二级页的分类 id（marketplace.{category} 的后缀）。</summary>
    public string? MarketplaceGroupId =>
        ShowMarketplaceGroup ? CurrentPageId["marketplace.".Length..] : null;

    /// <summary>一级页的分类入口卡：按插件目录顺序（后端内核→能力→服务→自动化→皮肤→工具）。</summary>
    public IReadOnlyList<MarketplaceGroupViewModel> MarketplaceGroups =>
        MarketplacePlugins
            .GroupBy(p => p.CategoryName)
            .OrderBy(g => GroupOrder(g.Key))
            .Select(g => new MarketplaceGroupViewModel(g.Key, g.Count(), g.Count(p => p.IsInstalled)))
            .ToArray();

    private static int GroupOrder(string title) => title switch
    {
        "后端内核" => 0,
        "智能体能力" => 1,
        "后台服务" => 2,
        "工作流与自动化" => 3,
        "个性皮肤" => 4,
        _ => 5,
    };

    /// <summary>二级页当前分类的插件列表。</summary>
    public IEnumerable<PluginItemViewModel> MarketplaceGroupPlugins =>
        MarketplaceGroupId is { } id
            ? MarketplacePlugins.Where(p => p.CategoryName == MarketplaceGroupTitle(id))
            : Enumerable.Empty<PluginItemViewModel>();

    /// <summary>分类 id → 中文分类名（与 PluginItemViewModel.CategoryName 同口径）。</summary>
    private static string MarketplaceGroupTitle(string id) => id switch
    {
        "backend" => "后端内核",
        "capability" => "智能体能力",
        "service" => "后台服务",
        "automation" => "工作流与自动化",
        "theme" => "个性皮肤",
        _ => "工具扩展",
    };

    [RelayCommand]
    public void OpenMarketplaceGroup(MarketplaceGroupViewModel? group)
    {
        if (group is null) return;
        OpenPage("marketplace." + group.Id);
    }

    /// <summary>
    /// 插件市场状态存储；mock 模式（无 dataRoot）为 null，此时能力一律按未安装处理，
    /// 不渲染假装可用的 CDP 开关。
    /// </summary>
    internal PluginStateStore? MarketplaceStore => _pluginStore;

    /// <summary>能力门禁网关；未接线数据根时为 null。</summary>
    private PluginCapabilityGateway? _capabilityGateway;

    internal PluginCapabilityGateway? CapabilityGateway => _capabilityGateway;

    /// <summary>插件状态变化（安装/卸载/启停）后刷新设置页可见状态。</summary>
    /// <summary>其它宿主实例写入 plugins.json：先刷新本地快照，再由 Changed 统一投影。</summary>
    private void OnGlobalPluginStateChanged() => _pluginStore?.Reload();

    private void OnPluginStoreChanged()
    {
        // 插件状态可能由其它宿主实例写入；市场行、能力门禁和页面状态必须
        // 从同一份 plugins.json 重新投影，不能只刷新能力文案而留下旧 badge/toggle。
        if (_pluginStore is not null)
        {
            foreach (var item in MarketplacePlugins)
            {
                var state = _pluginStore.GetState(item.Id);
                item.IsInstalled = state.IsInstalled;
                item.IsEnabled = state.IsEnabled;
            }
        }
        NotifyCapabilityState();
    }

    private void NotifyCapabilityState()
    {
        foreach (var item in MarketplacePlugins.Where(item => item.IsSubagent))
        {
            item.RefreshSubagentTier();
        }
        OnPropertyChanged(nameof(IsBrowserUseInstalled));
        OnPropertyChanged(nameof(BrowserUseAdvancedEnabled));
        OnPropertyChanged(nameof(BrowserUseUnavailableMessage));
        OnPropertyChanged(nameof(BrowserUseStatusText));
        OnPropertyChanged(nameof(IsComputerUseInstalled));
        OnPropertyChanged(nameof(ComputerUseStatusText));
        OnPropertyChanged(nameof(IsReverseLabInstalled));
        OnPropertyChanged(nameof(ReverseLabEnabled));
        OnPropertyChanged(nameof(ReverseLabStatusText));
        OnPropertyChanged(nameof(IsCdpActive));
        OnPropertyChanged(nameof(CdpStatusText));
    }

    private void SyncPlugins(string? dataRoot)
    {
        if (_packageManager is not null)
        {
            _ = _packageManager.DisposeAsync();
            _packageManager = null;
        }
        if (_pluginStore is not null) _pluginStore.Changed -= OnPluginStoreChanged;
        PluginStateNotifier.Changed -= OnGlobalPluginStateChanged;
        if (dataRoot is not null && Directory.Exists(dataRoot))
        {
            _pluginStore = new PluginStateStore(new AppDataLayout(dataRoot));
            _pluginStore.Changed += OnPluginStoreChanged;
            PluginStateNotifier.Changed += OnGlobalPluginStateChanged;
            _capabilityGateway = new PluginCapabilityGateway(_pluginStore);
        }
        else
        {
            _pluginStore = null;
            _capabilityGateway = null;
        }

        MarketplacePlugins.Clear();
        foreach (var def in PluginMarketplaceCatalog.All)
        {
            var state = _pluginStore?.GetState(def.Id)
                ?? new PluginState(def.Id, def.DefaultInstalled, def.DefaultEnabled, DateTime.UtcNow);
            MarketplacePlugins.Add(new PluginItemViewModel(def, state.IsInstalled, state.IsEnabled));
        }
        NotifyCapabilityState();
        UpdateBackendStatus(EngineForIndex(SelectedBackendIndex));
        OnPropertyChanged(nameof(MarketplaceGroups));
    }

    // ════════════════════ 能力门禁（Browser Use / Computer Use） ════════════════════

    /// <summary>Browser Use 插件是否已安装（安装与启用是两个不同状态）。</summary>
    public bool IsBrowserUseInstalled => _capabilityGateway?.Status(PluginCapability.BrowserControl).IsInstalled ?? false;

    /// <summary>CDP 接口与 Browser Use 动作契约是否真的解锁（已安装且已启用）。</summary>
    public bool IsCdpActive => _capabilityGateway?.IsAvailable(PluginCapability.BrowserControl) ?? false;

    /// <summary>浏览器高级控制专栏是否可交互；未安装或已停用时为 false，不渲染假开关。</summary>
    public bool BrowserUseAdvancedEnabled => IsCdpActive;

    /// <summary>未解锁时解释真实原因，并给出插件市场入口指向。</summary>
    public string? BrowserUseUnavailableMessage =>
        IsCdpActive ? null : "未安装：请在插件市场中安装 Browser Use 扩展";

    /// <summary>浏览器控制的真实状态文本（安装/启用分开）。</summary>
    public string BrowserUseStatusText =>
        _capabilityGateway?.Status(PluginCapability.BrowserControl).Reason
        ?? "未安装：请在插件市场中安装 Browser Use 扩展";

    /// <summary>Computer Use 插件是否已安装。</summary>
    public bool IsComputerUseInstalled =>
        _capabilityGateway?.Status(PluginCapability.ComputerControl).IsInstalled ?? false;

    /// <summary>电脑操控的真实状态文本。</summary>
    public string ComputerUseStatusText =>
        _capabilityGateway?.Status(PluginCapability.ComputerControl).Reason
        ?? "未安装：请在插件市场中安装 Computer Use 扩展";

    /// <summary>Seep 逆向工程工作台插件是否已安装（安装与启用是两个不同状态）。</summary>
    public bool IsReverseLabInstalled =>
        _capabilityGateway?.Status(PluginCapability.ReverseLab).IsInstalled ?? false;

    /// <summary>Seep 工作台工具链是否真的解锁（已安装且已启用）。未解锁时不渲染可用开关。</summary>
    public bool ReverseLabEnabled =>
        _capabilityGateway?.IsAvailable(PluginCapability.ReverseLab) ?? false;

    /// <summary>Seep 工作台的真实状态文本（未安装 / 已停用 / 已启用分开表述）。</summary>
    public string ReverseLabStatusText =>
        _capabilityGateway?.Status(PluginCapability.ReverseLab).Reason
        ?? "未安装：请在插件市场中安装 Seep 逆向工程工作台";

    /// <summary>CDP 访问真实状态文本。</summary>
    public string CdpStatusText => BrowserUseStatusText;

    private PluginPackageManager? _packageManager;

    /// <summary>
    /// 设置页只持有一个包管理器：每次安装/启停都新建会在全局状态通知上累积订阅，
    /// 并让同一数据根出现多个“活动插件”所有者。
    /// </summary>
    private PluginPackageManager ResolvePackageManager()
    {
        if (_packageManager is not null) return _packageManager;
        var root = _context?.DataRoot ?? Program.SelfTestDataRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var layout = new AppDataLayout(root);
        var gateway = _capabilityGateway ?? new PluginCapabilityGateway(_pluginStore ?? new PluginStateStore(layout));
        _packageManager = new PluginPackageManager(layout, new PluginToolRegistry(gateway), _pluginStore);
        return _packageManager;
    }

    [RelayCommand]
    public void InstallOrUninstallPlugin(PluginItemViewModel? item)
    {
        if (item is null || item.IsCoreBuiltin) return;
        var manager = ResolvePackageManager();
        if (item.IsInstalled)
        {
            try
            {
                manager.UninstallPackage(item.Id);
                item.IsInstalled = false;
                item.IsEnabled = false;
                item.StatusMessage = "已物理卸载";
            }
            catch (Exception ex)
            {
                item.StatusMessage = "卸载失败：" + ex.GetBaseException().Message;
            }
        }
        else
        {
            try
            {
                manager.InstallPackage(item.Id);
                item.IsInstalled = true;
                item.IsEnabled = true;
                if (item.IsSubagent && _context is { } context)
                {
                    context.Store.Update(current => current with { SubagentTier = "faster" });
                    NativeSubagentRegistry.Tier = SubagentTier.Faster;
                    item.RefreshSubagentTier();
                }
                item.StatusMessage = "已物理安装并激活";
            }
            catch (Exception ex)
            {
                item.IsInstalled = false;
                item.IsEnabled = false;
                item.StatusMessage = "安装失败：" + ex.GetBaseException().Message;
            }
        }
        NotifyCapabilityState();
    }

    private void SetSubagentTier(PluginItemViewModel item, string tier)
    {
        if (!item.IsSubagent || !item.IsInstalled || !item.IsEnabled) return;
        try
        {
            _context?.Store.Update(current => current with { SubagentTier = tier });
            NativeSubagentRegistry.Tier = tier == "stronger" ? SubagentTier.Stronger : SubagentTier.Faster;
            item.RefreshSubagentTier();
        }
        catch (Exception ex) { item.StatusMessage = "档位保存失败：" + ex.GetBaseException().Message; }
    }

    [RelayCommand]
    public void SetSubagentFaster(PluginItemViewModel? item)
    {
        if (item is not null) SetSubagentTier(item, "faster");
    }

    [RelayCommand]
    public void SetSubagentStronger(PluginItemViewModel? item)
    {
        if (item is not null) SetSubagentTier(item, "stronger");
    }

    [RelayCommand]
    public void TogglePluginEnabled(PluginItemViewModel? item)
    {
        if (item is null || !item.IsInstalled || item.IsCoreBuiltin) return;
        var next = !item.IsEnabled;
        var manager = ResolvePackageManager();
        try
        {
            manager.SetPackageEnabled(item.Id, next);
            item.IsEnabled = next;
            item.StatusMessage = next ? "已启用" : "已停用";
        }
        catch (Exception ex)
        {
            item.StatusMessage = "操作失败：" + ex.GetBaseException().Message;
        }
        NotifyCapabilityState();
    }
}
