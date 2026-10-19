using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fufucode.App.Features.SettingsExperience;
using Fufucode.App.Integration;
using Fufucode.Core;

namespace Fufucode.App.ViewModels;

/// <summary>关于页键值行。</summary>
public sealed record KeyValueRow(string Key, string Value);

/// <summary>
/// 侧栏条目。Tier 是当前生效层级（含升降级），OriginTier 是目录中的原始层级；
/// 两者不同即表示该条目已被升降级，<see cref="TierActionLabel"/> 给出反向操作文案。
/// </summary>
public sealed record SettingsCategoryViewModel(string Id, string Title, string SearchTerms,
    string Icon = "Settings", string IconColor = "",
    SettingsNavTier Tier = SettingsNavTier.Other, SettingsNavTier OriginTier = SettingsNavTier.Other)
{
    /// <summary>基础类固定，不参与升降级。</summary>
    public bool CanMoveTier => OriginTier != SettingsNavTier.Basic;

    /// <summary>当前在「其他」时显示升级文案，否则显示收纳文案。</summary>
    public string TierActionLabel => Tier == SettingsNavTier.Other ? "升级到一级侧栏" : "收纳进「其他」";

    public string TierActionTooltip => Tier == SettingsNavTier.Other
        ? "把这个小功能提升为一级侧栏条目"
        : "把这个大功能收纳进「其他」分类";

    /// <summary>升降级方向：true = 升级到一级侧栏（向上箭头）。</summary>
    public bool TierActionToFeatures => Tier == SettingsNavTier.Other;
}

/// <summary>二级设置页：分类内的一层子页（Windows 设置式导航）。</summary>
public sealed record SettingsSubPageViewModel(string Id, string Title, string Description, string SearchTerms = "");
public enum SettingSaveState { Unchanged, Saved, Applied, RestartRequired, Failed }

/// <summary>设置页 VM（M6/W1-a + W2 设置应用链路）：外观即改即生效；
/// 模型/思考在空闲时对当前会话先 set_* 再保存，回合进行中仅保存给新建会话；
/// 代理/端点保存后提示重启生效。密钥绝不进入任何 VM 字符串属性。</summary>
public sealed partial class SettingsPageViewModel : ViewModelBase
{
    public SettingsPageViewModel()
    {
        SubPages =
        [
            new("appearance", "外观", "主题、皮肤、颜色、字体与密度", "主题 皮肤 skin Fluent Metro Material3 Material 3 亮色 暗色 强调色 字号 密度 圆角 阅读宽度 theme accent font"),
            new("appearance.arrange", "前端功能排布", "区域显隐、页面位置与入口位置", "停靠区 页面位置 显隐 状态栏 入口位置 dock layout page"),
            new("appearance.arrange.aux", "辅助栏", "逐个功能选择出现在左辅助、右辅助或不显示", "辅助 left right auxiliary"),
            new("functions", "会话与消息", "输入区、摘要与消息显示", "消息 摘要 头像 名称 输入区 显示 message"),
            new("functions.names", "名称", "用户称呼与助手名称", "名称 用户称呼 助手名称 name"),
            new("functions.floating", "通知提示", "应用内、Windows与通知页的提示渠道", "通知 提醒 渠道 浮动 notifications"),
            new("models.providers", "提供商", "添加提供商并选择其可用模型", "提供商 端点 provider endpoint 模型 model magpie"),
            new("models.providers.add", "添加提供商", "选择预设或自定义端点", "添加 提供商 预设 provider preset"),
            new("models.providers.edit", "选择模型", "勾选该提供商下可用的模型", "模型 选择 勾选 model select"),
            new("marketplace.backend", "后端内核", "运行时与协议后端", "后端 内核 engine backend pi omp codex dsh native"),
            new("marketplace.capability", "智能体能力", "浏览器控制、电脑操控等高级控制", "能力 浏览器控制 电脑操控 capability browser computer"),
            new("marketplace.service", "后台服务", "常驻后台服务", "服务 后台 service remote"),
            new("marketplace.automation", "工作流与自动化", "自动化与任务", "自动化 工作流 automation workflow"),
            new("marketplace.theme", "个性皮肤", "主题与皮肤", "皮肤 主题 theme skin"),
            new("marketplace.tools", "工具扩展", "其他工具插件", "工具 扩展 tools extension"),
        ];
        // 模型清单变化时同步重建选择器的筛选结果与供应商快捷项。
        ModelChoices.CollectionChanged += (_, _) => RebuildFilteredModels();
        // 侧栏目录与三级架构（D2）：先按目录建全部条目，再按搜索词过滤成侧栏分组。
        RebuildCategories();
        UpdateCategoryFilter();
        SyncPlugins(null);
    }

    public ObservableCollection<SettingsCategoryViewModel> Categories { get; } = new();
    public IReadOnlyList<SettingsSubPageViewModel> SubPages { get; } = [];
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowThemeRow))]
    private WorkbenchCustomizationViewModel? _customization;
    public ObservableCollection<SettingsCategoryViewModel> FilteredCategories { get; } = new();
    [ObservableProperty] private IReadOnlyList<BuiltInModuleViewModel> _builtInModules = Array.Empty<BuiltInModuleViewModel>();
    public void AttachBuiltInModules(IReadOnlyList<BuiltInModuleViewModel> modules) => BuiltInModules = modules;
    public event EventHandler? ReturnRequested;
    public event EventHandler? DraftsDiscarded;

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private SettingsCategoryViewModel? _selectedCategory;
    [ObservableProperty] private NotificationPreferencesViewModel? _notificationPreferences;
    [ObservableProperty] private SettingSaveState _appearanceState;
    [ObservableProperty] private string? _appearanceStateText;
    [ObservableProperty] private SettingSaveState _modelsState;
    [ObservableProperty] private string? _modelsStateText;
    [ObservableProperty] private SettingSaveState _networkState;
    [ObservableProperty] private string? _networkStateText;
    [ObservableProperty] private bool _isModelApplying;
    [ObservableProperty] private bool _isEndpointSaving;
    // ── 性能与内存（默认全部关闭 = 高性能模式） ──
    [ObservableProperty] private bool _softwareRendering;
    [ObservableProperty] private bool _disableVisualEffects;
    [ObservableProperty] private bool _reduceTerminalScrollback;
    [ObservableProperty] private SettingSaveState _performanceState;
    [ObservableProperty] private string? _performanceStateText;
    private bool _suppressPerformanceSave;
    private EndpointDraft? _endpointDraftBaseline;
    private bool _hasEndpointKeyDraft;
    private bool _suppressDraftTracking;
    private int _attachmentGeneration;

    private sealed record EndpointDraft(string? Provider, string? Url, string? Model, int KeyMode, string? EnvironmentName);
    public bool HasNotificationPreferences => NotificationPreferences is not null;
    public bool HasSearchResults => FilteredCategories.Count > 0;
    public string SearchSummary => SearchText.Trim().Length == 0 ? "只显示本产品可用的设置" : $"找到 {FilteredCategories.Count} 个分类";
    public string CategoryTitle => SelectedCategory?.Title ?? "没有匹配的设置";

    /// <summary>当前二级页 id；空串表示停在分类首页。</summary>
    [ObservableProperty] private string _currentPageId = "";
    /// <summary>标题栏文字：二级页显示子页名，分类首页显示分类名。</summary>
    public string PageTitle => CurrentPageId.Length == 0
        ? CategoryTitle
        : SubPages.FirstOrDefault(p => p.Id == CurrentPageId)?.Title
            ?? SubPageFallbackTitle(CurrentPageId)
            ?? CategoryTitle;
    /// <summary>面包屑：按 id 的点号层级逐级展开（外观 › 前端功能排布 › 辅助栏）。</summary>
    public string Breadcrumb
    {
        get
        {
            if (CurrentPageId.Length == 0) return CategoryTitle;
            var parts = new List<string> { CategoryTitle };
            var prefix = "";
            foreach (var segment in CurrentPageId.Split('.'))
            {
                prefix = prefix.Length == 0 ? segment : prefix + "." + segment;
                var page = SubPages.FirstOrDefault(p => p.Id == prefix);
                if (page is not null) parts.Add(page.Title);
                else if (SubPageFallbackTitle(prefix) is { } fallback) parts.Add(fallback);
            }
            return string.Join(" › ", parts);
        }
    }

    /// <summary>动态子页的标题回退：shortcuts.{group} 直接用组名。</summary>
    private static string? SubPageFallbackTitle(string id) =>
        id.StartsWith("shortcuts.", StringComparison.Ordinal)
            ? Uri.UnescapeDataString(id["shortcuts.".Length..])
            : null;
    public bool IsSubPage => CurrentPageId.Length > 0;
    /// <summary>分区控件当前应渲染的页面：停在分类首页时用分类 id。</summary>
    public string ActivePageId => CurrentPageId.Length > 0 ? CurrentPageId : SelectedCategory?.Id ?? "appearance";
    public bool IsSearching => SearchText.Trim().Length > 0;
    /// <summary>该分类下可跳转的二级页（配置文件只有一页，不显示入口）。</summary>
    public IReadOnlyList<SettingsSubPageViewModel> CategorySubPages =>
        SubPages.Where(page => page.Id == SelectedCategory?.Id ||
            page.Id.StartsWith((SelectedCategory?.Id ?? "") + ".", StringComparison.Ordinal)).ToArray();
    public bool HasSubPages => CategorySubPages.Count > 1;

    public void OpenPage(string id)
    {
        CurrentPageId = id;
        NotifyPageState();
    }
    public void GoBack()
    {
        CurrentPageId = "";
        NotifyPageState();
    }
    private void NotifyPageState()
    {
        foreach (var name in new[] { nameof(CurrentPageId), nameof(Breadcrumb), nameof(PageTitle), nameof(BackToolTip), nameof(IsSubPage), nameof(CategorySubPages), nameof(HasSubPages), nameof(ActivePageId), nameof(IsSearching), nameof(ShowMarketplace), nameof(ShowMarketplaceGroup), nameof(MarketplaceGroupId), nameof(MarketplaceGroupPlugins), nameof(ShowPinSection) })
            OnPropertyChanged(name);
        NotifyCategoryVisibility();
    }
    [RelayCommand] private void BackToCategory() => GoBack();
    /// <summary>外观首页：只在没有进入任何子页时显示。</summary>
    public bool ShowAppearance => SelectedCategory?.Id == "appearance" && CurrentPageId.Length == 0;
    public bool ShowBrowserSettings => SelectedCategory?.Id == "browser";
    /// <summary>会话与消息首页：只在没有进入任何子页时显示。</summary>
    public bool ShowFunctions => SelectedCategory?.Id == "functions" && CurrentPageId.Length == 0;
    public bool ShowConfigSection => SelectedCategory?.Id == "config";
    /// <summary>外观、浏览器、功能、配置文件共用分区控件（含它们的二级页）。</summary>
    public bool ShowWorkbenchSection => ShowAppearance || ShowFunctions || ShowConfigSection || ShowBrowserSettings;
    public string WorkbenchSectionId => SelectedCategory?.Id switch { "functions" => "functions", "config" => "config", "browser" => "browser", _ => "appearance" };
    private string Search => SearchText.Trim();
    private bool SearchMatches(string terms) => Search.Length == 0 || terms.Contains(Search, StringComparison.OrdinalIgnoreCase);
    private const string LookTerms = "主题 皮肤 skin Fluent Metro Material3 Material 3 亮色 暗色 强调色 字号 密度 圆角 阅读宽度 预设 appearance theme";
    private const string ArrangeTerms = "停靠区 页面位置 显隐 状态栏 入口位置 按钮 布局 dock layout page status";
    /// <summary>外观页左栏：常规外观（主题、颜色、字体、密度、圆角、阅读宽度）。</summary>
    public bool ShowLookColumn => ShowAppearance && SearchMatches(LookTerms);
    /// <summary>外观页右栏：前端功能排布（停靠、页面位置、入口位置）。</summary>
    public bool ShowArrangementColumn => ShowAppearance && SearchMatches(ArrangeTerms);

    /// <summary>保留为独立左侧页面的五个分区。</summary>
    /// <summary>模型与思考首页：只在没有进入任何子页时显示。</summary>
    public bool ShowModels => SelectedCategory?.Id == "models" && CurrentPageId.Length == 0;
    /// <summary>模型与思考 → 提供商（第二层）。</summary>
    public bool ShowProviderList => IsModelsCategory && CurrentPageId == "models.providers";
    /// <summary>模型与思考 → 提供商 → 选择模型（第三层）。</summary>
    public bool ShowProviderEditor => IsModelsCategory && CurrentPageId == "models.providers.edit";
    /// <summary>模型与思考 → 提供商 → 添加提供商（预设选择）。</summary>
    public bool ShowPresetPicker => IsModelsCategory && CurrentPageId == "models.providers.add";
    private bool IsModelsCategory => SelectedCategory?.Id == "models";

    /// <summary>是否处于提供商子页面（第二/三层）。此时主分类内容整页隐藏，由独立宿主渲染。</summary>
    public bool IsProviderPageOpen => IsModelsCategory
        && CurrentPageId is "models.providers" or "models.providers.add" or "models.providers.edit";
    public bool ShowNetwork => SelectedCategory?.Id == "network";
    public bool ShowNotifications => SelectedCategory?.Id == "notifications";
    public bool ShowAbout => SelectedCategory?.Id == "about";
    public bool ShowModules => SelectedCategory?.Id == "modules";
    public bool ShowShortcuts => SelectedCategory?.Id == "shortcuts";
    [ObservableProperty] private Avalonia.Controls.Control? _shortcutsContent;

    /// <summary>
    /// 快捷键页的组下钻：组名 → shortcuts.{group}，返回时清空。
    /// 走 CurrentPageId 让面包屑/标题/淡入动画与市场页子页同一条链路。
    /// </summary>
    public void OpenShortcutsGroup(string group) => OpenPage("shortcuts." + Uri.EscapeDataString(group));
    public void CloseShortcutsGroup() => GoBack();

    /// <summary>快捷键页当前二级组名（null = 一级分组目录页）。</summary>
    public string? ShortcutsGroupId =>
        ShowShortcuts && CurrentPageId.StartsWith("shortcuts.", StringComparison.Ordinal)
            ? Uri.UnescapeDataString(CurrentPageId["shortcuts.".Length..])
            : null;

    public bool ShowStorage => SelectedCategory?.Id == "storage";
    public bool ShowPerformance => SelectedCategory?.Id == "performance";
    [ObservableProperty] private Avalonia.Controls.Control? _storageContent;
    /// <summary>「其他」收纳桶页：右侧列二级功能入口卡，点击进入对应设置页。</summary>
    /// <summary>「其他」桶页：选中占位项时列出小功能入口卡。</summary>
    public bool ShowOtherHub => SelectedCategory?.Id == OtherHubId;
    /// <summary>目录中已登记但内容页尚未接线的分类（终端/文件管理/自动开发/用量）。</summary>
    public bool ShowCategoryPlaceholder =>
        SelectedCategory is { } category && SettingsNavCatalog.Find(category.Id) is { HasContent: false };
    public bool ShowThemeRow => Customization is null && ShowAppearance && MatchesOption("外观 主题 亮色 暗色 跟随系统 appearance theme");
    public bool ShowModelRow => ShowModels && MatchesOption("模型与思考 模型 默认 model");
    public bool ShowThinkingRow => ShowModels && MatchesOption("模型与思考 思考 等级 默认 thinking");
    public bool ShowProxyRow => ShowNetwork && MatchesOption("网络与端点 代理 网络 proxy URL");
    public bool ShowEndpointRow => ShowNetwork && MatchesOption("网络与端点 自定义端点 API Key 环境变量 endpoint provider URL");
    public bool ShowModelsConfig => ShowNetwork && MatchesOption("网络与端点 模型配置 models.json provider");
    public bool CanChangeModel => !IsMock && !IsModelApplying;

    public bool HasProxyDraft => _context is not null && NormalizeDraft(ProxyInput) != _context.Store.Current.ProxyUrl;
    public bool HasEndpointDraft => _hasEndpointKeyDraft || (_endpointDraftBaseline is not null && CaptureEndpointDraft() != _endpointDraftBaseline);
    public bool HasUnsavedChanges => HasProxyDraft || HasEndpointDraft;
    public bool IsSettingsBusy => IsModelApplying || IsEndpointSaving;
    public bool ShowEndpointForm => ShowEndpointRow && EndpointFormOpen;
    public string UnsavedChangesText => string.Join("、", new[] { HasProxyDraft ? "代理草稿" : null, HasEndpointDraft ? "端点草稿" : null }.Where(x => x is not null));

    public void AttachNotificationPreferences(NotificationPreferencesViewModel preferences) => NotificationPreferences = preferences;
    partial void OnNotificationPreferencesChanged(NotificationPreferencesViewModel? value) => OnPropertyChanged(nameof(HasNotificationPreferences));
    partial void OnIsModelApplyingChanged(bool value) { OnPropertyChanged(nameof(CanChangeModel)); OnPropertyChanged(nameof(IsSettingsBusy)); }
    partial void OnIsEndpointSavingChanged(bool value) => OnPropertyChanged(nameof(IsSettingsBusy));
    partial void OnSearchTextChanged(string value) => UpdateCategoryFilter();
    partial void OnSelectedCategoryChanged(SettingsCategoryViewModel? value)
    {
        // 切分类回到该分类首页，避免停在上一分类的二级页。
        if (value is not null && CurrentPageId.Length > 0 &&
            !(CurrentPageId == value.Id || CurrentPageId.StartsWith(value.Id + ".", StringComparison.Ordinal)))
            CurrentPageId = "";
        NotifyPageState();
        RebuildRelatedLinks();
        OnPropertyChanged(nameof(CurrentCategoryCanPin));
        OnPropertyChanged(nameof(CurrentCategoryIsPinned));
    }

    private void UpdateCategoryFilter()
    {
        var term = SearchText.Trim();
        var previousId = SelectedCategory?.Id ?? SettingsNavCatalog.DefaultCategoryId;
        FilteredCategories.Clear();
        if (term.Length == 0)
        {
            // 无搜索：一级侧栏只列「基础类 + 大功能 + 『其他』占位」；
            // Other 里的二级功能收纳在占位项的内容区。
            foreach (var item in EnumerateSidebarItems(_navStore.Current))
                FilteredCategories.Add(item);
        }
        else
        {
            // 跨层级搜索：命中条目（含 Other）按目录顺序平铺，方便定位。
            foreach (var definition in SettingsNavCatalog.All)
            {
                if (!SettingsNavLayout.Matches(definition, term)) continue;
                FilteredCategories.Add(ToCategory(definition, _navStore.Current));
            }
        }
        SelectedCategory = FilteredCategories.FirstOrDefault(category => category.Id == previousId)
            ?? FilteredCategories.FirstOrDefault();
        OnPropertyChanged(nameof(HasSearchResults));
        OnPropertyChanged(nameof(SearchSummary));
        NotifyCategoryVisibility();
        RaiseNavigationRebuilt();
    }
    private bool MatchesOption(string terms) => SearchText.Trim().Length == 0 || terms.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase);
    private void NotifyCategoryVisibility()
    {
        foreach (var name in new[] { nameof(CategoryTitle), nameof(ShowAppearance), nameof(ShowFunctions), nameof(ShowConfigSection), nameof(ShowWorkbenchSection), nameof(WorkbenchSectionId), nameof(ShowLookColumn), nameof(ShowArrangementColumn), nameof(ShowModels), nameof(ShowProviderList), nameof(ShowProviderEditor), nameof(ShowPresetPicker), nameof(IsProviderPageOpen), nameof(ProviderSummary), nameof(ShowNetwork), nameof(ShowNotifications), nameof(ShowAbout), nameof(ShowModules),
                     nameof(ShowStorage), nameof(ShowPerformance), nameof(ShowShortcuts), nameof(ShowOtherHub), nameof(OtherHubItems),nameof(ShowCategoryPlaceholder), nameof(ShowThemeRow), nameof(ShowModelRow), nameof(ShowThinkingRow), nameof(ShowProxyRow), nameof(ShowEndpointRow), nameof(ShowEndpointForm), nameof(ShowModelsConfig), nameof(ShowRemoteCard), nameof(ShowBackendCard), nameof(ShowMarketplace), nameof(ShowMarketplaceGroup), nameof(MarketplaceGroupId), nameof(MarketplaceGroupPlugins) })
            OnPropertyChanged(name);
    }

    [RelayCommand] private void ReturnToWorkspace() => ReturnRequested?.Invoke(this, EventArgs.Empty);
    /// <summary>页头返回：有二级页时先回分类首页，否则回工作区（对齐 Windows 设置的返回语义）。</summary>
    [RelayCommand] private void BackOrReturn()
    {
        if (IsSubPage) { GoBack(); return; }
        ReturnRequested?.Invoke(this, EventArgs.Empty);
    }
    public string BackToolTip => IsSubPage ? "返回上一级" : "返回工作区";
    public void SetEndpointKeyDraftPresence(bool hasDraft) { _hasEndpointKeyDraft = hasDraft; NotifyDraftState(); }
    partial void OnProxyInputChanged(string? value) => NotifyDraftState();
    partial void OnEndpointBaseUrlChanged(string? value) => NotifyDraftState();
    partial void OnEndpointModelIdChanged(string? value) => NotifyDraftState();
    partial void OnEndpointEnvNameChanged(string? value) => NotifyDraftState();
    partial void OnEndpointFormOpenChanged(bool value)
{
    NotifyDraftState();
    OnPropertyChanged(nameof(ShowEndpointForm));
    // 打开表单即拉取模型，避免用户面对一个空白的手填 ID 输入框。
    if (value) _ = LoadEndpointModelsAsync();
}
    private static string? NormalizeDraft(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    private EndpointDraft CaptureEndpointDraft() => new(NormalizeDraft(EndpointProviderName), NormalizeDraft(EndpointBaseUrl),
        NormalizeDraft(EndpointModelId), EndpointKeyModeIndex, NormalizeDraft(EndpointEnvName));
    private void NotifyDraftState()
    {
        if (_suppressDraftTracking) return;
        OnPropertyChanged(nameof(HasProxyDraft));
        OnPropertyChanged(nameof(HasEndpointDraft));
        OnPropertyChanged(nameof(HasUnsavedChanges));
        OnPropertyChanged(nameof(UnsavedChangesText));
    }
    /// <summary>本机 Magpie 网关的默认地址（OpenAI 兼容，不校验密钥）。</summary>
    private const string MagpieBaseUrl = "http://127.0.0.1:3425/v1";
    private const string MagpieProviderName = "magpie";
    private const string MagpieEnvName = "MAGPIE_API_KEY";

    /// <summary>
    /// 修正历史遗留的 selectedModel：
    /// 1. 旧版本会把端点提供方错误地拼到模型 id 前面（如 magpie/antigravity/x）；
    /// 2. 指向已不在模型清单里的值会让下拉只剩这一项。
    /// 这里只做能确定的修正，无法判断时清空让用户重选。
    /// </summary>
    private void RepairSelectedModel(AppSettings current)
    {
        if (_context is not { } ctx) return;
        if (current.Endpoint is not { } endpoint) return;
        var saved = current.SelectedModel;
        if (string.IsNullOrWhiteSpace(saved)) return;

        var models = endpoint.AvailableModels;
        if (models.Count == 0) return;

        if (models.Contains(saved, StringComparer.Ordinal)) return;

        // 去掉被误加的 provider 前缀。
        var prefix = endpoint.ProviderName + "/";
        if (saved.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            var stripped = saved[prefix.Length..];
            if (models.Contains(stripped, StringComparer.Ordinal))
            {
                TryUpdateStore(s => s with { SelectedModel = stripped }, out _);
                return;
            }
        }

        // 已不存在于当前端点：退回端点默认模型，避免下拉只剩一个失效项。
        TryUpdateStore(s => s with { SelectedModel = endpoint.ModelId }, out _);
    }

    /// <summary>从保存的端点恢复提供商列表与模型勾选状态。</summary>
    private void LoadProvidersFromSettings(CompatibleEndpoint? endpoint)
    {
        EndpointProviders.Clear();
        if (endpoint is not null)
        {
            var provider = new EndpointProviderItem(
                endpoint.ProviderName, endpoint.BaseUrl, endpoint.ApiKeyEnvironmentVariable);
            foreach (var id in endpoint.AvailableModels)
            {
                var option = new EndpointModelOption(id, id,
                    string.Equals(id, endpoint.ModelId, StringComparison.Ordinal) ||
                    endpoint.Models is { Count: > 1 });
                option.SelectionChanged = NotifyModelSelectionChanged;
                provider.Models.Add(option);
            }
            provider.NotifyModelsChanged();
            EndpointProviders.Add(provider);
        }
        RebuildFilteredProviders();
        OnPropertyChanged(nameof(HasEndpointProviders));
        OnPropertyChanged(nameof(ProviderSummary));
    }

    private void ResetEndpointDraft()
    {
        var endpoint = _context?.Store.Current.Endpoint;
        // 未配置端点时预填本机 Magpie：用户只需确认模型即可保存。
        // 已配置端点时始终回填用户自己的值，不覆盖。
        EndpointProviderName = endpoint?.ProviderName ?? MagpieProviderName;
        EndpointBaseUrl = endpoint?.BaseUrl ?? MagpieBaseUrl;
        EndpointModelId = endpoint?.ModelId;
        EndpointKeyModeIndex = 1;
        EndpointEnvName = endpoint?.ApiKeyEnvironmentVariable ?? MagpieEnvName;
        _hasEndpointKeyDraft = false;
        _endpointDraftBaseline = CaptureEndpointDraft();
    }
    public void DiscardUnsavedChanges()
    {
        _suppressDraftTracking = true;
        ProxyInput = _context?.Store.Current.ProxyUrl;
        ResetEndpointDraft();
        ProxyError = null;
        EndpointError = null;
        _suppressDraftTracking = false;
        DraftsDiscarded?.Invoke(this, EventArgs.Empty);
        NotifyDraftState();
    }

    /// <summary>设置页的运行时依赖；mock 模式下只读数据不可得的回调为 null。
    /// Store 必须是当前活动会话的存储（真实模式 = host.Manager.Settings；mock = App.SettingsStore）。</summary>
    public sealed record Context(
        AppSettingsStore Store,
        bool IsMock,
        Func<string?> CurrentModel,
        Func<CancellationToken, Task<RpcResponse>>? QueryModels,
        string? AgentDirectory,
        string? DataRoot,
        string? LogsDirectory,
        Func<string?> ApprovalText,
        Func<string?> ApprovalDiagnostic,
        Func<IReadOnlyList<string>> ActiveExtensionPaths,
        Func<string?> LastStartupFailure,
        Func<string> ProjectPath,
        Func<string, Task> OpenFolder,
        Func<string, Task> CopyText,
        Func<string> PiVersion,
        Func<string> NodeVersion,
        // ── W2 设置应用链路（真实模式提供；mock/无活会话为 null → 仅保存）──
        Func<bool>? IsTurnActive = null,
        Func<string, string, CancellationToken, Task<RpcResponse>>? SetModel = null,
        Func<string, CancellationToken, Task<RpcResponse>>? SetThinkingLevel = null,
        Func<CancellationToken, Task<RpcResponse>>? QueryState = null,
        Func<Task>? RestartSession = null,
        Action<string, string>? SaveCredential = null,
        Func<string, ValueTask<string?>>? ResolveCredential = null,
        Action<string?>? ModelApplied = null);

    private Context? _context;
    /// <summary>自测接缝：读取当前注入的运行上下文（如 get_state 核对）。</summary>
    internal Context? AttachedContext => _context;
    private bool _suppressThemeApply;
    private bool _suppressApply;
    private CancellationTokenSource? _queryCts;

    public bool IsMock => _context?.IsMock ?? true;

    // ── 外观 ──
    /// <summary>0=跟随系统 1=亮色 2=暗色。</summary>
    [ObservableProperty]
    private int _themeIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAppearanceError))]
    private string? _appearanceError;

    public bool HasAppearanceError => AppearanceError is not null;

    partial void OnThemeIndexChanged(int value)
    {
        if (_suppressThemeApply || _context is null)
        {
            return;
        }

        var theme = value switch { 1 => "light", 2 => "dark", _ => "system" };
        try
        {
            var next = _context.Store.Update(current => current with { Theme = theme });
            if (Avalonia.Application.Current is { } app)
            {
                app.RequestedThemeVariant = ThemeVariantFor(next.Theme);
            }

            AppearanceError = null;
            AppearanceState = SettingSaveState.Applied;
            AppearanceStateText = "已保存 · 当前已应用";
        }
        catch (Exception ex)
        {
            // 落盘失败（如 newer-version 文件保护）：卡片内报错且不改主题。
            AppearanceError = $"主题保存失败：{ex.GetBaseException().Message}";
            AppearanceState = SettingSaveState.Failed;
            AppearanceStateText = "失败回退 · 沿用已保存的主题";
            _suppressThemeApply = true;
            ThemeIndex = IndexFor(_context.Store.Current.Theme);
            _suppressThemeApply = false;
        }
    }

    public static ThemeVariant ThemeVariantFor(string theme) => theme switch
    {
        "light" => ThemeVariant.Light,
        "dark" => ThemeVariant.Dark,
        _ => ThemeVariant.Default,
    };

    private static int IndexFor(string theme) => theme switch
    {
        "light" => 1,
        "dark" => 2,
        _ => 0,
    };

    // ── 模型与思考（W2 接通 A 的应用链路） ──
    /// <summary>模型下拉项：Value=null 表示「跟随默认」（settings.selectedModel=null）。</summary>
    public sealed record ModelChoice(string? Value, string Label);

    /// <summary>思考等级下拉项：Value=null 表示「默认」（跟随 Pi/模型默认）。</summary>
    public sealed record ThinkingChoice(string? Value, string Label, bool IsSupported = true);

    private static readonly ThinkingChoice[] ThinkingChoicesStatic =
    [
        new(null, "默认"),
        new("off", "off"),
        new("minimal", "minimal"),
        new("low", "low"),
        new("medium", "medium"),
        new("high", "high"),
        new("xhigh", "xhigh"),
        new("max", "max"),
        new("ultra", "ultra（当前后端未支持）", false),
        new("auto", "auto（当前后端未支持）", false),
    ];

    [ObservableProperty]
    private string _currentModelText = "—";
    public string? DataRoot => _context?.DataRoot ?? Program.SelfTestDataRoot;

    public ObservableCollection<string> ModelRows { get; } = new();
    public ObservableCollection<ModelChoice> ModelChoices { get; } = new();
    public IReadOnlyList<ThinkingChoice> ThinkingChoices => ThinkingChoicesStatic;

    [ObservableProperty]
    private ModelChoice? _selectedModel;

    [ObservableProperty]
    private ThinkingChoice? _selectedThinking;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasModelsError))]
    private string? _modelsError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasModelsInfo))]
    private string? _modelsInfo;

    public bool HasModelsError => ModelsError is not null;
    public bool HasModelsInfo => ModelsInfo is not null;
    public bool HasModelRows => ModelRows.Count > 0;
    public bool ModelSectionAvailable => !IsMock;

    partial void OnSelectedModelChanged(ModelChoice? value)
    {
        if (_suppressApply || value is null)
        {
            return;
        }

        _ = ApplyModelAsync(value.Value);
    }

    partial void OnSelectedThinkingChanged(ThinkingChoice? value)
    {
        if (value is { IsSupported: false }) { ModelsError = "当前 Pi 版本不支持 " + value.Value + "，未发送或保存此档位"; return; }
        if (_suppressApply || value is null)
        {
            return;
        }

        _ = ApplyThinkingAsync(value.Value);
    }

    /// <summary>模型变更：空闲且有活会话 → 先 RPC 后保存；进行中 → 仅保存给新建会话。</summary>
    private async Task ApplyModelAsync(string? value)
    {
        if (_context is not { } ctx)
        {
            return;
        }

        ModelsError = null;
        ModelsInfo = null;
        ModelsStateText = null;
        if (ctx.IsTurnActive?.Invoke() == true || value is null || ctx.SetModel is null)
        {
            if (TryUpdateStore(s => s with { SelectedModel = value }, out var saveError))
            {
                ModelsInfo = value is null
                    ? "已保存：新建会话将使用默认模型"
                    : ctx.SetModel is null && ctx.IsTurnActive?.Invoke() != true
                        ? "已保存，将用于新建会话"
                        : "已保存，将用于新建会话；当前会话未切换";
                ModelsState = SettingSaveState.Saved;
                ModelsStateText = "已保存 · 当前会话未切换";
            }
            else
            {
                ModelsError = saveError;
                ModelsState = SettingSaveState.Failed;
                ModelsStateText = "失败回退 · 沿用已保存的默认模型";
                RevertModelSelection(ctx.Store.Current.SelectedModel);
            }

            return;
        }

        var separator = value.IndexOf('/');
        if (separator <= 0 || separator == value.Length - 1)
        {
            ModelsError = "模型缺少提供方身份，无法切换。";
            ModelsState = SettingSaveState.Failed;
            ModelsStateText = "失败回退 · 当前会话未切换";
            RevertModelSelection(ctx.Store.Current.SelectedModel);
            return;
        }
        var provider = value[..separator];
        var modelId = value[(separator + 1)..];
        var generation = _attachmentGeneration;
        IsModelApplying = true;
        var previousRuntime = await QueryCurrentModelAsync(ctx) ?? ctx.CurrentModel();
        try
        {
            var response = await ctx.SetModel(provider, modelId, CancellationToken.None);
            if (generation != _attachmentGeneration) return;
            if (!response.Success)
            {
                ModelsError = response.Error ?? "set_model 失败";
                ModelsState = SettingSaveState.Failed;
                ModelsStateText = "失败回退 · 当前会话未切换";
                RevertModelSelection(ctx.Store.Current.SelectedModel);
                return;
            }

            if (!TryUpdateStore(s => s with { SelectedModel = value }, out var saveError))
            {
                ModelsError = saveError;
                ModelsState = SettingSaveState.Failed;
                ModelsStateText = await RestoreRuntimeModelAsync(ctx, previousRuntime);
                RevertModelSelection(ctx.Store.Current.SelectedModel);
                return;
            }

            var applied = await QueryCurrentModelAsync(ctx) ?? value;
            CurrentModelText = applied;
            ctx.ModelApplied?.Invoke(applied);
            ModelsState = SettingSaveState.Applied;
            ModelsStateText = "已保存 · 当前已应用：" + applied;
        }
        catch (Exception ex)
        {
            if (generation != _attachmentGeneration) return;
            ModelsError = ex.GetBaseException().Message;
            ModelsState = SettingSaveState.Failed;
            ModelsStateText = "应用失败 · 沿用已保存的默认值；运行状态需核对";
            RevertModelSelection(ctx.Store.Current.SelectedModel);
        }
        finally { if (generation == _attachmentGeneration) IsModelApplying = false; }
    }

    /// <summary>思考等级变更：与模型同规则（空闲先 RPC 后保存；进行中仅保存）。</summary>
    private async Task ApplyThinkingAsync(string? level)
    {
        if (_context is not { } ctx)
        {
            return;
        }

        ModelsError = null;
        ModelsInfo = null;
        ModelsStateText = null;
        if (ctx.IsTurnActive?.Invoke() == true || level is null || ctx.SetThinkingLevel is null)
        {
            if (TryUpdateStore(s => s with { ThinkingLevel = level }, out var saveError))
            {
                ModelsInfo = level is null
                    ? "已保存：新建会话将使用默认思考等级"
                    : ctx.SetThinkingLevel is null && ctx.IsTurnActive?.Invoke() != true
                        ? "已保存，将用于新建会话"
                        : "已保存，将用于新建会话；当前会话未切换";
                ModelsState = SettingSaveState.Saved;
                ModelsStateText = "已保存 · 当前会话未切换";
            }
            else
            {
                ModelsError = saveError;
                ModelsState = SettingSaveState.Failed;
                ModelsStateText = "失败回退 · 沿用已保存的默认思考等级";
                RevertThinkingSelection(ctx.Store.Current.ThinkingLevel);
            }

            return;
        }

        var generation = _attachmentGeneration;
        IsModelApplying = true;
        var previousRuntime = await QueryThinkingLevelAsync(ctx);
        try
        {
            var response = await ctx.SetThinkingLevel(level, CancellationToken.None);
            if (generation != _attachmentGeneration) return;
            if (!response.Success)
            {
                ModelsError = response.Error ?? "set_thinking_level 失败";
                ModelsState = SettingSaveState.Failed;
                ModelsStateText = "失败回退 · 当前会话未切换";
                RevertThinkingSelection(ctx.Store.Current.ThinkingLevel);
                return;
            }

            if (!TryUpdateStore(s => s with { ThinkingLevel = level }, out var saveError))
            {
                ModelsError = saveError;
                ModelsState = SettingSaveState.Failed;
                ModelsStateText = await RestoreThinkingLevelAsync(ctx, previousRuntime);
                RevertThinkingSelection(ctx.Store.Current.ThinkingLevel);
            }
            else { ModelsState = SettingSaveState.Applied; ModelsStateText = "已保存 · 当前已应用"; }
        }
        catch (Exception ex)
        {
            if (generation != _attachmentGeneration) return;
            ModelsError = ex.GetBaseException().Message;
            ModelsState = SettingSaveState.Failed;
            ModelsStateText = "应用失败 · 沿用已保存的默认值；运行状态需核对";
            RevertThinkingSelection(ctx.Store.Current.ThinkingLevel);
        }
        finally { if (generation == _attachmentGeneration) IsModelApplying = false; }
    }

    private bool TryUpdateStore(Func<AppSettings, AppSettings> change, out string? error)
    {
        try
        {
            _context!.Store.Update(change);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            error = ex.GetBaseException().Message;
            return false;
        }
    }

    private async Task<string> RestoreRuntimeModelAsync(Context ctx, string? previous)
    {
        var separator = previous?.IndexOf('/') ?? -1;
        if (previous is null || separator <= 0 || separator == previous.Length - 1 || ctx.SetModel is null)
            return "保存失败 · 当前模型已改变，旧运行值不可用，未执行回退";
        try
        {
            var restored = await ctx.SetModel(previous[..separator], previous[(separator + 1)..], CancellationToken.None);
            if (!restored.Success) return "保存失败 · 当前模型已改变，运行值回退失败";
            var actual = await QueryCurrentModelAsync(ctx);
            if (actual != previous) return "保存失败 · 已请求回退，当前运行值未核实";
            CurrentModelText = actual;
            ctx.ModelApplied?.Invoke(actual);
            return "失败回退 · 当前模型已恢复，默认值未改写";
        }
        catch (Exception) { return "保存失败 · 当前模型已改变，运行值回退失败"; }
    }

    private static async Task<string?> QueryThinkingLevelAsync(Context ctx)
    {
        if (ctx.QueryState is null) return null;
        try
        {
            var state = await ctx.QueryState(CancellationToken.None);
            return state.Success && state.Data is { } data ? ReadString(data, "thinkingLevel") : null;
        }
        catch (Exception) { return null; }
    }

    private static async Task<string> RestoreThinkingLevelAsync(Context ctx, string? previous)
    {
        if (previous is null || ctx.SetThinkingLevel is null) return "保存失败 · 思考等级已改变，旧运行值不可用，未执行回退";
        try
        {
            var restored = await ctx.SetThinkingLevel(previous, CancellationToken.None);
            if (!restored.Success) return "保存失败 · 思考等级运行值回退失败";
            return await QueryThinkingLevelAsync(ctx) == previous ? "失败回退 · 思考等级已恢复，默认值未改写" : "保存失败 · 已请求回退，当前运行值未核实";
        }
        catch (Exception) { return "保存失败 · 思考等级运行值回退失败"; }
    }

    /// <summary>set_model 成功后用 get_state 确认当前模型（adapter.CurrentModel 不自动刷新）。</summary>
    private static async Task<string?> QueryCurrentModelAsync(Context ctx)
    {
        if (ctx.QueryState is null)
        {
            return null;
        }

        try
        {
            var state = await ctx.QueryState(CancellationToken.None);
            if (!state.Success || state.Data is not { } data ||
                !data.TryGetProperty("model", out var model) || model.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var provider = ReadString(model, "provider");
            var id = ReadString(model, "id");
            return id is null ? null : provider is null ? id : $"{provider}/{id}";
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void RevertModelSelection(string? saved)
    {
        _suppressApply = true;
        var choice = ModelChoices.FirstOrDefault(c => c.Value == saved);
        if (choice is null && saved is not null)
        {
            choice = new ModelChoice(saved, saved);
            ModelChoices.Add(choice);
        }

        SelectedModel = choice ?? ModelChoices.FirstOrDefault(c => c.Value is null);
        _suppressApply = false;
    }

    private void RevertThinkingSelection(string? saved)
    {
        _suppressApply = true;
        SelectedThinking = ThinkingChoicesStatic.FirstOrDefault(c => c.Value == saved)
            ?? ThinkingChoicesStatic[0];
        _suppressApply = false;
    }

    /// <summary>重启成功 reattach 后：若已存模型与当前会话模型不同且空闲，对当前会话应用。</summary>
    internal void ApplySavedModelIfIdle()
    {
        if (_context is not { } ctx || ctx.IsMock || ctx.SetModel is null)
        {
            return;
        }

        var saved = ctx.Store.Current.SelectedModel;
        if (saved is null || saved == ctx.CurrentModel() || ctx.IsTurnActive?.Invoke() == true)
        {
            return;
        }

        _ = ApplyModelAsync(saved);
    }

    // ── 网络与端点 ──
    [ObservableProperty]
    private string _proxySummary = "未设置";

    [ObservableProperty]
    private string? _proxyInput;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProxyError))]
    private string? _proxyError;

    public bool HasProxyError => ProxyError is not null;

    [ObservableProperty]
    private string _endpointSummary = "未设置";

    [ObservableProperty] private string? _endpointProviderName;
    [ObservableProperty] private string? _endpointBaseUrl;
    [ObservableProperty] private string? _endpointModelId;
    /// <summary>0=输入 API Key（写凭据管理器） 1=使用已有环境变量。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EndpointKeyModeIsKey))]
    private int _endpointKeyModeIndex;
    // ── 提供商（模型与思考 → 提供商 → 选择模型，三层逐级进入） ──

    /// <summary>已配置的提供商。每个提供商自带 Base URL 与模型勾选清单。</summary>
    public ObservableCollection<EndpointProviderItem> EndpointProviders { get; } = new();

    /// <summary>提供商列表的搜索词（按名称或 Base URL 过滤）。</summary>
    [ObservableProperty] private string _providerFilter = string.Empty;

    /// <summary>按搜索词过滤后的提供商，供列表绑定。</summary>
    public ObservableCollection<EndpointProviderItem> FilteredEndpointProviders { get; } = new();

    public bool HasFilteredEndpointProviders => FilteredEndpointProviders.Count > 0;
    /// <summary>有提供商但搜索无匹配：显示明确空状态，而不是误报"还没有配置提供商"。</summary>
    public bool ShowNoProviderMatch =>
        EndpointProviders.Count > 0 && FilteredEndpointProviders.Count == 0;

    partial void OnProviderFilterChanged(string value) => RebuildFilteredProviders();

    private void RebuildFilteredProviders()
    {
        var filter = ProviderFilter.Trim();
        FilteredEndpointProviders.Clear();
        foreach (var provider in EndpointProviders)
        {
            if (filter.Length > 0 &&
                !provider.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
                !provider.BaseUrl.Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;
            FilteredEndpointProviders.Add(provider);
        }
        OnPropertyChanged(nameof(HasFilteredEndpointProviders));
        OnPropertyChanged(nameof(ShowNoProviderMatch));
    }

    /// <summary>正在第三层编辑的提供商；为 null 表示尚未选中。</summary>
    [ObservableProperty] private EndpointProviderItem? _selectedEndpointProvider;
    [ObservableProperty] private bool _isLoadingEndpointModels;
    [ObservableProperty] private string? _endpointModelsError;

    public bool HasSelectedEndpointProvider => SelectedEndpointProvider is not null;
    public bool HasEndpointProviders => EndpointProviders.Count > 0;

    public ObservableCollection<EndpointModelOption> AvailableEndpointModels =>
        SelectedEndpointProvider?.Models ?? _emptyModels;
    private readonly ObservableCollection<EndpointModelOption> _emptyModels = new();

    public bool HasAvailableEndpointModels => SelectedEndpointProvider?.HasModels == true;
    public string EndpointSelectedCountText => SelectedEndpointProvider?.SelectedCountText ?? "已选 0 / 0";

    partial void OnSelectedEndpointProviderChanged(EndpointProviderItem? value)
    {
        if (value is not null)
        {
            EndpointProviderName = value.Name;
            EndpointBaseUrl = value.BaseUrl;
            EndpointEnvName = value.ApiKeyEnvironmentVariable;
            EndpointModelId = value.PrimaryModelId();
            EndpointKeyModeIndex = 1;
        }
        NotifyProviderSelectionChanged();
    }

    private void NotifyProviderSelectionChanged()
    {
        OnPropertyChanged(nameof(HasSelectedEndpointProvider));
        OnPropertyChanged(nameof(AvailableEndpointModels));
        OnPropertyChanged(nameof(HasAvailableEndpointModels));
        OnPropertyChanged(nameof(EndpointSelectedCountText));
    }

    private void NotifyModelSelectionChanged()
    {
        SelectedEndpointProvider?.NotifyModelsChanged();
        OnPropertyChanged(nameof(EndpointSelectedCountText));
    }

    /// <summary>「模型与思考」首页 → 提供商列表（第二层）。</summary>
    [RelayCommand]
    private void OpenProviderList() => OpenPage("models.providers");

    /// <summary>首页显示已配置提供商的摘要。</summary>
    public string ProviderSummary => EndpointProviders.Count == 0
        ? "未配置；点此添加提供商并选择模型"
        : $"{EndpointProviders.Count} 个提供商：" + string.Join("、", EndpointProviders.Select(p => p.DisplayName));

    // ── 添加提供商：先选预设，再填必要配置 ──

    /// <summary>添加提供商的预设筛选词。</summary>
    [ObservableProperty] private string _presetFilter = string.Empty;

    /// <summary>按筛选词过滤后的预设。</summary>
    public ObservableCollection<ProviderPreset> FilteredPresets { get; } = new();

    /// <summary>第二层「添加提供商」：进入预设选择页。</summary>
    [RelayCommand]
    private void BeginAddProvider()
    {
        PresetFilter = string.Empty;
        RebuildFilteredPresets();
        OpenPage("models.providers.add");
    }

    partial void OnPresetFilterChanged(string value) => RebuildFilteredPresets();

    /// <summary>重建预设列表：按名称、描述与分类过滤。</summary>
    private void RebuildFilteredPresets()
    {
        var filter = PresetFilter.Trim();
        FilteredPresets.Clear();
        foreach (var preset in ProviderPreset.All)
        {
            if (filter.Length > 0 &&
                !preset.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
                !preset.Description.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
                !preset.BaseUrl.Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;
            FilteredPresets.Add(preset);
        }
        OnPropertyChanged(nameof(HasFilteredPresets));
    }

    public bool HasFilteredPresets => FilteredPresets.Count > 0;

    /// <summary>选中预设：用它的名称、地址与密钥变量建立提供商，并进入第三层。</summary>
    [RelayCommand]
    private void ChoosePreset(ProviderPreset? preset)
    {
        if (preset is null) return;
        var provider = new EndpointProviderItem(preset.Name, preset.BaseUrl,
            preset.ApiKeyEnvironmentVariable);
        EndpointProviders.Add(provider);
        RebuildFilteredProviders();
        OnPropertyChanged(nameof(HasEndpointProviders));
        OnPropertyChanged(nameof(ProviderSummary));

        // 本地与自定义端点允许空密钥；官方 API 需要用户填写。
        EndpointKeyModeIndex = preset.RequiresKey ? 0 : 1;
        SelectedPreset = preset;
        EnterProviderEditor(provider);
    }

    /// <summary>当前编辑的提供商来源预设（用于显示"必填"提示）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsApiKeyRequired))]
    [NotifyPropertyChangedFor(nameof(IsBaseUrlEditable))]
    private ProviderPreset? _selectedPreset;

    public bool IsApiKeyRequired => SelectedPreset?.RequiresKey == true;
    public bool IsBaseUrlEditable => SelectedPreset is null || SelectedPreset.Id == "custom";

    /// <summary>第二层点击某个提供商：进入第三层编辑它。</summary>
    [RelayCommand]
    private void EditProvider(EndpointProviderItem? provider)
    {
        if (provider is null) return;
        EnterProviderEditor(provider);
    }

    private void EnterProviderEditor(EndpointProviderItem provider)
    {
        SelectedEndpointProvider = provider;
        EndpointError = null;
        EndpointInfo = null;
        OpenPage("models.providers.edit");
        // 进入即拉取模型，用户不用再点一次刷新。
        if (!provider.HasModels) _ = LoadEndpointModelsAsync();
    }

    /// <summary>第三层「取消 / 返回」：回到提供商列表。</summary>
    [RelayCommand]
    private void CancelEditProvider()
    {
        // 从预设页取消时移除尚未配置的占位条目，避免留下空提供商。
        if (CurrentPageId == "models.providers.add" && SelectedEndpointProvider is { HasModels: false } empty)
        {
            EndpointProviders.Remove(empty);
            SelectedEndpointProvider = null;
            RebuildFilteredProviders();
            OnPropertyChanged(nameof(HasEndpointProviders));
        }
        SelectedEndpointProvider = null;
        OpenPage("models.providers");
    }

    [RelayCommand]
    private void RemoveProvider(EndpointProviderItem? provider)
    {
        if (provider is null) return;
        EndpointProviders.Remove(provider);
        if (ReferenceEquals(SelectedEndpointProvider, provider)) SelectedEndpointProvider = null;
        RebuildFilteredProviders();
        OnPropertyChanged(nameof(HasEndpointProviders));
        OnPropertyChanged(nameof(ProviderSummary));
        NotifyProviderSelectionChanged();
    }

    /// <summary>第三层：从当前提供商的 Base URL 拉取模型清单。</summary>
    [RelayCommand]
    private async Task LoadEndpointModelsAsync()
    {
        if (IsLoadingEndpointModels || SelectedEndpointProvider is not { } provider) return;
        var baseUrl = provider.BaseUrl?.Trim();
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            EndpointModelsError = "请先填写 Base URL。";
            return;
        }

        IsLoadingEndpointModels = true;
        EndpointModelsError = null;
        try
        {
            using var catalog = new Fufucode.Core.Backend.ModelCatalogClient();
            var apiKey = string.IsNullOrWhiteSpace(provider.ApiKeyEnvironmentVariable)
                ? null
                : Environment.GetEnvironmentVariable(provider.ApiKeyEnvironmentVariable);
            var models = await catalog.ListAsync(baseUrl, apiKey);

            // 保留已有勾选；首次拉取默认全选。
            var previous = provider.Models.ToDictionary(o => o.Id, o => o.IsSelected, StringComparer.Ordinal);
            provider.Models.Clear();
            foreach (var model in models)
            {
                var option = new EndpointModelOption(model.Id, model.DisplayName,
                    previous.TryGetValue(model.Id, out var selected) ? selected : true);
                option.SelectionChanged = NotifyModelSelectionChanged;
                provider.Models.Add(option);
            }
            provider.NotifyModelsChanged();
            NotifyProviderSelectionChanged();

            if (provider.Models.Count == 0)
            {
                EndpointModelsError = "端点返回了空模型列表。";
                return;
            }

            EndpointModelId = provider.PrimaryModelId();
        }
        catch (Exception error)
        {
            provider.Models.Clear();
            provider.NotifyModelsChanged();
            NotifyProviderSelectionChanged();
            EndpointModelsError = "获取模型列表失败：" + error.GetBaseException().Message;
        }
        finally
        {
            IsLoadingEndpointModels = false;
        }
    }

    [RelayCommand]
    private void SelectAllEndpointModels()
    {
        if (SelectedEndpointProvider is not { } provider) return;
        foreach (var option in provider.Models) option.SetSelectedSilently(true);
        provider.NotifyModelsChanged();
        OnPropertyChanged(nameof(EndpointSelectedCountText));
        EndpointModelId = provider.PrimaryModelId();
    }

    [RelayCommand]
    private void ClearEndpointModels()
    {
        if (SelectedEndpointProvider is not { } provider) return;
        foreach (var option in provider.Models) option.SetSelectedSilently(false);
        provider.NotifyModelsChanged();
        OnPropertyChanged(nameof(EndpointSelectedCountText));
    }

    [ObservableProperty] private string? _endpointEnvName;
    [ObservableProperty] private bool _endpointFormOpen;
    [ObservableProperty] private bool _hasEndpoint;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEndpointError))]
    private string? _endpointError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEndpointInfo))]
    private string? _endpointInfo;

    [ObservableProperty]
    private string? _endpointKeyTargetText;

    public bool HasEndpointError => EndpointError is not null;
    public bool HasEndpointInfo => EndpointInfo is not null;
    public bool EndpointKeyModeIsKey => EndpointKeyModeIndex == 0;

    // ── 待重启 InfoBar（代理/端点变更复用扩展页的“重启会话后生效”模式） ──
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRestartNotice))]
    private string? _restartNoticeText;

    public bool HasRestartNotice => RestartNoticeText is not null;

    partial void OnEndpointProviderNameChanged(string? value) { UpdateKeyTargetText(); NotifyDraftState(); }
    partial void OnEndpointKeyModeIndexChanged(int value) { UpdateKeyTargetText(); NotifyDraftState(); }

    private void UpdateKeyTargetText()
    {
        if (EndpointKeyModeIndex == 0)
        {
            var derived = DeriveEnvName(EndpointProviderName);
            EndpointKeyTargetText = derived is null
                ? "保存位置：Windows 凭据管理器（先填提供方名称）"
                : $"保存位置：Windows 凭据管理器（{derived}）";
        }
        else
        {
            EndpointKeyTargetText = "将从指定环境变量读取，不写入凭据管理器";
        }
    }

    /// <summary>API Key 模式的目标环境变量名：FUFUCODE_&lt;提供方大写，非字母数字→_&gt;_API_KEY。</summary>
    internal static string? DeriveEnvName(string? providerName)
    {
        if (string.IsNullOrWhiteSpace(providerName))
        {
            return null;
        }

        var chars = providerName.Trim().ToUpperInvariant()
            .Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_').ToArray();
        return "FUFUCODE_" + new string(chars) + "_API_KEY";
    }

    private bool TryValidateEndpointFields(string provider, string baseUrl, string modelId,
        string envName, out string? error)
    {
        if (string.IsNullOrWhiteSpace(provider) ||
            !provider.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
        {
            error = "提供方名称只能包含字母、数字、- _ .";
            return false;
        }

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0 || uri.Query.Length > 0)
        {
            error = "Base URL 必须是 http/https 绝对地址（不含用户名密码与查询串）";
            return false;
        }

        if (string.IsNullOrWhiteSpace(modelId) && !AvailableEndpointModels.Any(o => o.IsSelected))
        {
            error = "请至少选择一个模型";
            return false;
        }

        if (string.IsNullOrWhiteSpace(envName) ||
            !(char.IsAsciiLetter(envName[0]) || envName[0] == '_') ||
            !envName.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
        {
            error = "环境变量名不合法（字母或 _ 开头，仅字母/数字/_）";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>models.json 已有同名 provider 且归属标记不是它 → 拒绝覆盖（与 Core InstallEndpoint 一致）。</summary>
    private bool HasForeignProviderConflict(string providerName)
    {
        if (_context?.AgentDirectory is not { } dir)
        {
            return false;
        }

        var snapshot = PiModelsConfig.Read(dir);
        if (!snapshot.Providers.Any(p => string.Equals(p.Name, providerName, StringComparison.Ordinal)))
        {
            return false;
        }

        var marker = Path.Combine(dir, "fufucode-provider.txt");
        var owned = File.Exists(marker) &&
            string.Equals(File.ReadAllText(marker).Trim(), providerName, StringComparison.Ordinal);
        return !owned;
    }

    /// <summary>保存自定义端点。apiKey 由视图传入（密码框），不存任何 VM 属性。</summary>
    internal async Task SaveEndpointAsync(string? apiKey)
    {
        if (IsEndpointSaving) return;
        IsEndpointSaving = true;
        try { await SaveEndpointCoreAsync(apiKey); }
        finally { IsEndpointSaving = false; }
    }

    private async Task SaveEndpointCoreAsync(string? apiKey)
    {
        if (_context is not { } ctx)
        {
            return;
        }
        var generation = _attachmentGeneration;

        EndpointError = null;
        EndpointInfo = null;
        NetworkState = SettingSaveState.Failed;
        NetworkStateText = "未保存 · 当前端点配置保持原值";
        // 右侧表单是当前提供商的编辑视图：保存前先写回该提供商对象。
        if (SelectedEndpointProvider is { } current)
        {
            current.Name = EndpointProviderName?.Trim() ?? current.Name;
            current.BaseUrl = EndpointBaseUrl?.Trim() ?? current.BaseUrl;
            if (EndpointKeyModeIndex == 1 && !string.IsNullOrWhiteSpace(EndpointEnvName))
                current.ApiKeyEnvironmentVariable = EndpointEnvName.Trim();
        }

        var provider = EndpointProviderName?.Trim() ?? string.Empty;
        var baseUrl = EndpointBaseUrl?.Trim() ?? string.Empty;
        var modelId = EndpointModelId?.Trim() ?? string.Empty;
        var envName = EndpointKeyModeIndex == 0
            ? DeriveEnvName(provider) ?? string.Empty
            : EndpointEnvName?.Trim() ?? string.Empty;
        var credentialWritten = false;

        if (!TryValidateEndpointFields(provider, baseUrl, modelId, envName, out var error))
        {
            EndpointError = error;
            return;
        }

        if (HasForeignProviderConflict(provider))
        {
            EndpointError = "models.json 已存在同名 provider（非本应用写入）；请改用其他提供方名称";
            return;
        }

        if (EndpointKeyModeIndex == 0)
        {
            if (string.IsNullOrEmpty(apiKey))
            {
                EndpointError = "请输入 API Key";
                return;
            }

            if (ctx.SaveCredential is null)
            {
                EndpointError = "当前环境不支持写入凭据";
                return;
            }

            try
            {
                ctx.SaveCredential(envName, apiKey);
                credentialWritten = true;
            }
            catch (Exception ex)
            {
                EndpointError = "凭据写入失败：" + ex.GetBaseException().Message;
                return;
            }
        }

        // 本机回环端点（Magpie / Ollama / LM Studio 等）不校验密钥，
        // 强制要求环境变量存在只会挡住用户；这类端点跳过解析检查。
        var isLoopbackEndpoint = Uri.TryCreate(baseUrl, UriKind.Absolute, out var endpointUri)
                                 && endpointUri.IsLoopback;
        if (!isLoopbackEndpoint)
        {
            try
            {
                if (ctx.ResolveCredential is not null && await ctx.ResolveCredential(envName) is null)
                {
                    EndpointError = EndpointKeyModeIndex == 0
                        ? "凭据写入后仍无法解析，未保存端点"
                        : $"环境变量 {envName} 当前不可解析，未保存端点";
                    return;
                }
            }
            catch (Exception resolutionError) { EndpointError = "凭据解析失败：" + resolutionError.GetBaseException().Message; return; }
        }

        if (generation != _attachmentGeneration) return;

        // 保存当前提供商勾选的模型清单；会话的模型下拉直接来自它。
        var selectedModels = SelectedEndpointProvider?.SelectedModelIds() ?? [];
        var endpoint = new CompatibleEndpoint(provider, baseUrl,
            selectedModels.FirstOrDefault() ?? modelId, envName,
            selectedModels.Length > 0 ? selectedModels : null);
        if (!TryUpdateStore(s => s with
            {
                Endpoint = endpoint,
                SelectedModel = $"{provider}/{modelId}",
            }, out var saveError))
        {
            EndpointError = saveError;
            NetworkStateText = credentialWritten ? "配置保存失败 · 凭据已写入，未做凭据回退" : "失败回退 · 已保存的端点配置未改变";
            return;
        }

        EndpointSummary = $"{provider} · {modelId}";
        HasEndpoint = true;
        EndpointInfo = $"提供商「{provider}」已保存（{selectedModels.Length} 个模型；密钥不写入配置文件）";
        RestartNoticeText = "提供商已保存，重启会话后生效";
        OnPropertyChanged(nameof(ProviderSummary));
        NetworkState = SettingSaveState.RestartRequired;
        NetworkStateText = "已保存 · 等待重启会话";
        _hasEndpointKeyDraft = false;
        _endpointDraftBaseline = CaptureEndpointDraft();
        NotifyDraftState();
        RefreshModelsConfig();
        SyncModelSelection();
        // 端点已变更：立即重新拉取模型列表，让会话的模型下拉同步为新的清单。
        _ = RefreshModelsAsync();
        // 保存成功后回到提供商列表（第三层 → 第二层）。
        SelectedEndpointProvider = null;
        OnPropertyChanged(nameof(ProviderSummary));
        OpenPage("models.providers");
    }

    [RelayCommand]
    private void ToggleEndpointForm() => EndpointFormOpen = !EndpointFormOpen;

    /// <summary>移除自定义端点：Endpoint=null（Core 下次启动按归属标记清理 models.json）。
    /// 凭据管理器中的密钥不删除（无删除 API）。</summary>
    [RelayCommand]
    private void RemoveEndpoint()
    {
        if (_context is not { } ctx)
        {
            return;
        }

        EndpointError = null;
        var previous = ctx.Store.Current.Endpoint;
        if (!TryUpdateStore(s => s with
            {
                Endpoint = null,
                // 若选中模型属于被移除端点则一并清掉，避免下次新建会话 set_model 失败。
                SelectedModel = previous is { } ep &&
                    s.SelectedModel == $"{ep.ProviderName}/{ep.ModelId}" ? null : s.SelectedModel,
            }, out var saveError))
        {
            EndpointError = saveError;
            NetworkState = SettingSaveState.Failed;
            NetworkStateText = "失败回退 · 已保存的端点配置未改变";
            return;
        }

        EndpointSummary = "未设置";
        HasEndpoint = false;
        EndpointInfo = "已移除端点；凭据管理器中的密钥保留（无删除接口），可手动清理";
        RestartNoticeText = "端点已移除，重启会话后生效";
        NetworkState = SettingSaveState.RestartRequired;
        NetworkStateText = "已保存 · 等待重启会话";
        ResetEndpointDraft();
        DraftsDiscarded?.Invoke(this, EventArgs.Empty);
        NotifyDraftState();
        RefreshModelsConfig();
        SyncModelSelection();
    }

    [RelayCommand]
    private void SaveProxy()
    {
        if (_context is not { } ctx)
        {
            return;
        }

        var value = string.IsNullOrWhiteSpace(ProxyInput) ? null : ProxyInput.Trim();
        if (!TryUpdateStore(s => s with { ProxyUrl = value }, out var error))
        {
            ProxyError = $"代理保存失败：{error}";
            NetworkState = SettingSaveState.Failed;
            NetworkStateText = "失败回退 · 已保存的代理未改变";
            NotifyDraftState();
            return;
        }

        ProxyError = null;
        ProxySummary = value is null ? "未设置" : $"已设置（{value}）";
        RestartNoticeText = "代理已保存，重启会话后生效";
        NetworkState = SettingSaveState.RestartRequired;
        NetworkStateText = "已保存 · 等待重启会话";
        NotifyDraftState();
    }

    [RelayCommand]
    private void ClearProxy()
    {
        ProxyInput = null;
        SaveProxy();
    }

    /// <summary>待重启 InfoBar 的「立即重启」：复用窗口安全重启流程及其守卫。</summary>
    [RelayCommand]
    private async Task RestartNowAsync()
    {
        if (_context?.RestartSession is { } restart)
        {
            try { await restart(); }
            catch (Exception error)
            {
                NetworkState = SettingSaveState.Failed;
                NetworkStateText = "重启失败 · 已保存的配置保留，尚未应用";
                RestartNoticeText = "重启未完成：" + error.GetBaseException().Message;
            }
        }
    }

    private void SyncModelSelection()
    {
        _suppressApply = true;
        var saved = _context?.Store.Current.SelectedModel;
        var choice = ModelChoices.FirstOrDefault(c => c.Value == saved);
        if (choice is null && saved is not null)
        {
            // 已存模型不在本次可用列表（如端点未加载）：保留该项以免下拉显示落空。
            choice = new ModelChoice(saved, saved);
            ModelChoices.Add(choice);
        }

        SelectedModel = choice ?? ModelChoices.FirstOrDefault(c => c.Value is null);
        _suppressApply = false;
    }

    // ── 模型选择器：关键字快捷 + Pin 置顶 ──

    /// <summary>模型选择器的搜索词。</summary>
    [ObservableProperty] private string _modelPickerFilter = string.Empty;

    /// <summary>按搜索词筛选后的模型，置顶项排在最前。</summary>
    public ObservableCollection<ModelPickerItem> FilteredModelChoices { get; } = new();

    /// <summary>内置关键字快捷项（用户可逐个关闭，关闭状态持久化）。</summary>
    private static readonly string[] BuiltInKeywords =
    [
        "claude", "gpt", "gemini", "glm",
        "deepseek", "grok", "qwen", "free",
        "opus", "sonnet", "flash", "reasoning",
    ];

    /// <summary>当前可见的关键字快捷项：覆盖最常搜的几类模型，最多三行。</summary>
    public ObservableCollection<ModelKeywordItem> ModelSearchKeywords { get; } = new();

    public bool HasVisibleKeywords => ModelSearchKeywords.Count > 0;

    /// <summary>有被关闭过的关键字时，才显示"恢复"入口。</summary>
    public bool HasDismissedKeywords =>
        _context?.Store.Current.DismissedModelKeywords is { Count: > 0 };

    /// <summary>按设置重建关键字快捷区（已关闭的不显示）。</summary>
    private void RebuildSearchKeywords()
    {
        var dismissed = _context?.Store.Current.DismissedModelKeywords ?? [];
        ModelSearchKeywords.Clear();
        foreach (var keyword in BuiltInKeywords)
        {
            if (dismissed.Contains(keyword, StringComparer.OrdinalIgnoreCase)) continue;
            ModelSearchKeywords.Add(new ModelKeywordItem(keyword, isDismissed: false));
        }
        OnPropertyChanged(nameof(HasVisibleKeywords));
        OnPropertyChanged(nameof(HasDismissedKeywords));
    }

    /// <summary>关闭一个关键字快捷项：从快捷区移除并写回设置。</summary>
    [RelayCommand]
    private void DismissSearchKeyword(ModelKeywordItem? item)
    {
        if (item is null || _context is not { } ctx) return;
        var dismissed = (ctx.Store.Current.DismissedModelKeywords ?? []).ToList();
        if (!dismissed.Contains(item.Keyword, StringComparer.OrdinalIgnoreCase))
            dismissed.Add(item.Keyword);
        if (TryUpdateStore(s => s with { DismissedModelKeywords = dismissed.ToArray() }, out _))
            RebuildSearchKeywords();
    }

    /// <summary>恢复全部已关闭的关键字（供选择器底部入口使用）。</summary>
    [RelayCommand]
    private void RestoreSearchKeywords()
    {
        if (_context is null) return;
        if (TryUpdateStore(s => s with { DismissedModelKeywords = null }, out _))
            RebuildSearchKeywords();
    }

    public bool HasFilteredModelChoices => FilteredModelChoices.Count > 0;
    public string ModelPickerSummary =>
        $"{FilteredModelChoices.Count} / {ModelChoices.Count(choice => choice.Value is not null)} 个模型";

    /// <summary>
    /// 聚合提供商标识：Magpie 本身就是一个提供商，其下的模型不再按 id 前缀拆成多个提供商。
    /// </summary>
    public string ModelPickerProviderLabel => _context?.Store.Current.Endpoint is { } endpoint
        ? $"提供商：{endpoint.ProviderName}"
        : "未配置提供商";

    partial void OnModelPickerFilterChanged(string value) => RebuildFilteredModels();

    /// <summary>重建筛选结果：置顶优先，然后按显示名排序。</summary>
    private void RebuildFilteredModels()
    {
        var pinned = _context?.Store.Current.PinnedModels ?? [];
        var filter = ModelPickerFilter.Trim();
        var items = new List<ModelPickerItem>();
        foreach (var choice in ModelChoices)
        {
            if (choice.Value is null) continue;
            if (filter.Length > 0 &&
                !choice.Label.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
                !choice.Value.Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;
            items.Add(new ModelPickerItem(choice.Value, choice.Label,
                pinned.Contains(choice.Value, StringComparer.Ordinal)));
        }

        FilteredModelChoices.Clear();
        foreach (var item in items
                     .OrderByDescending(item => item.IsPinned)
                     .ThenBy(item => item.Label, StringComparer.OrdinalIgnoreCase))
            FilteredModelChoices.Add(item);

        OnPropertyChanged(nameof(HasFilteredModelChoices));
        OnPropertyChanged(nameof(ModelPickerSummary));
    }

    /// <summary>选择模型并关闭选择器。</summary>
    [RelayCommand]
    private void ChooseModel(ModelPickerItem? item)
    {
        if (item is null) return;
        var choice = ModelChoices.FirstOrDefault(c => string.Equals(c.Value, item.Value, StringComparison.Ordinal));
        if (choice is not null) SelectedModel = choice;
    }

    /// <summary>快捷关键字：填入搜索框并立即筛选。</summary>
    [RelayCommand]
    private void ApplySearchKeyword(string? keyword) =>
        ModelPickerFilter = string.IsNullOrWhiteSpace(keyword) ? string.Empty : keyword;

    /// <summary>置顶/取消置顶某个模型；写回 settings 并重排列表。</summary>
    [RelayCommand]
    private void ToggleModelPin(ModelPickerItem? item)
    {
        if (item is null || _context is not { } ctx) return;
        var current = (ctx.Store.Current.PinnedModels ?? []).ToList();
        var index = current.FindIndex(value => string.Equals(value, item.Value, StringComparison.Ordinal));
        if (index >= 0) current.RemoveAt(index);
        else current.Insert(0, item.Value);

        if (TryUpdateStore(s => s with { PinnedModels = current.ToArray() }, out var error))
        {
            EndpointError = null;
            RebuildFilteredModels();
        }
        else
        {
            EndpointError = "置顶保存失败：" + error;
        }
    }

    public ObservableCollection<PiProviderInfo> Providers { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasModelsConfigStatus))]
    private string? _modelsConfigStatus;

    public bool HasModelsConfigStatus => ModelsConfigStatus is not null;
    public bool HasProviders => Providers.Count > 0;

    // ── 关于 ──
    [ObservableProperty] private string _appVersion = "—";
    [ObservableProperty] private string _dotNetVersion = "—";
    [ObservableProperty] private string _avaloniaVersion = "—";
    [ObservableProperty] private string _piVersion = "mock 模式不可用";
    [ObservableProperty] private string _nodeVersion = "mock 模式不可用";
    public ObservableCollection<KeyValueRow> VersionRows { get; } = new();

    [ObservableProperty] private string _dataRootText = "—";
    [ObservableProperty] private string _agentDirectoryText = "—";
    [ObservableProperty] private string _logsDirectoryText = "—";
    [ObservableProperty] private string? _settingsLoadError;
    [ObservableProperty] private bool _hasSettingsLoadError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCopyStatus))]
    private string? _copyStatus;

    [ObservableProperty] private string? _appVersionTooltip;

    public bool HasCopyStatus => CopyStatus is not null;
    public bool CanOpenFolders => _context?.OpenFolder is not null && !IsMock;

    /// <summary>组合根注入运行上下文并触发首次填充。</summary>
    public void Attach(Context context)
    {
        _attachmentGeneration++;
        _queryCts?.Cancel();
        _context = context;
        // 侧栏三级架构的升降级偏好随数据根目录持久化（mock 无 dataRoot 时仅内存态）。
        RebuildSearchKeywords();
        AttachNavPreference(context.DataRoot);
        SyncBackendPreference(context.DataRoot);
        SyncPlugins(context.DataRoot);
        _suppressDraftTracking = true;
        _suppressThemeApply = true;
        ThemeIndex = IndexFor(context.Store.Current.Theme);
        _suppressThemeApply = false;
        var current = context.Store.Current;
        var performance = current.Performance ?? new PerformanceSettings();
        _suppressPerformanceSave = true;
        SoftwareRendering = performance.SoftwareRendering;
        DisableVisualEffects = performance.DisableVisualEffects;
        ReduceTerminalScrollback = performance.ReduceTerminalScrollback;
        _suppressPerformanceSave = false;
        MemoryProfile.Apply(performance);
        PerformanceState = SettingSaveState.Unchanged;
        PerformanceStateText = null;
        ProxySummary = current.ProxyUrl is { } proxy ? $"已设置（{proxy}）" : "未设置";
        ProxyInput = current.ProxyUrl;
        ProxyError = null;
        EndpointSummary = current.Endpoint is { } ep
            ? $"{ep.ProviderName} · {ep.ModelId}"
            : "未设置";
        // 端点草稿只有一个来源：ResetEndpointDraft（未配置时预填本机 Magpie）。
        // 这里若再各自赋值，会覆盖预填值，导致表单只剩占位符。
        ResetEndpointDraft();
        HasEndpoint = current.Endpoint is not null;
        LoadProvidersFromSettings(current.Endpoint);
        RepairSelectedModel(current);
        EndpointError = null;
        EndpointInfo = null;
        UpdateKeyTargetText();
        // 重启后重新 Attach：待重启提示随之清除。
        RestartNoticeText = null;
        ModelsError = null;
        ModelsInfo = null;
        AppearanceState = SettingSaveState.Unchanged;
        ModelsState = SettingSaveState.Unchanged;
        NetworkState = SettingSaveState.Unchanged;
        AppearanceStateText = null;
        ModelsStateText = null;
        NetworkStateText = null;
        IsModelApplying = false;
        _suppressApply = true;
        SelectedThinking = ThinkingChoicesStatic.FirstOrDefault(c => c.Value == current.ThinkingLevel)
            ?? ThinkingChoicesStatic[0];
        _suppressApply = false;
        SyncModelSelection();
        SettingsLoadError = context.Store.LastLoadError;
        HasSettingsLoadError = SettingsLoadError is not null;

        RefreshBuildChannel();
        AppVersionTooltip = DiagnosticsSummary.AppVersion();
        AppVersion = ShortVersion(AppVersionTooltip);
        DotNetVersion = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
        AvaloniaVersion = DiagnosticsSummary.AvaloniaVersion();
        PiVersion = context.IsMock ? "mock 模式不可用" : context.PiVersion();
        NodeVersion = context.IsMock ? "mock 模式不可用" : context.NodeVersion();
        DataRootText = context.DataRoot ?? "mock 模式不可用";
        AgentDirectoryText = context.AgentDirectory ?? "mock 模式不可用";
        LogsDirectoryText = context.LogsDirectory ?? "mock 模式不可用";
        VersionRows.Clear();
        VersionRows.Add(new KeyValueRow(".NET 运行时", DotNetVersion));
        VersionRows.Add(new KeyValueRow("Avalonia", AvaloniaVersion));
        if (!string.IsNullOrEmpty(PiVersion))
            VersionRows.Add(new KeyValueRow("Pi 后端插件", PiVersion));
        if (!string.IsNullOrEmpty(NodeVersion))
            VersionRows.Add(new KeyValueRow("Node 运行时", NodeVersion));
        CurrentModelText = context.IsMock
            ? "mock 模式无真实模型信息"
            : context.CurrentModel() ?? "未知";
        OnPropertyChanged(nameof(IsMock));
        OnPropertyChanged(nameof(ModelSectionAvailable));
        OnPropertyChanged(nameof(CanChangeModel));
        OnPropertyChanged(nameof(CanOpenFolders));
        _suppressDraftTracking = false;
        DraftsDiscarded?.Invoke(this, EventArgs.Empty);
        NotifyDraftState();
        RefreshModelsConfig();
        // 进入设置页自动拉取模型列表；填充/回填经 _suppressApply 抑制，不会触发 set_* 或写设置。
        if (!context.IsMock && context.QueryModels is not null)
        {
            _ = RefreshModelsAsync();
        }
        _ = CheckPendingOrInterruptedUpdateAsync();
        if (context.Store.Current.AutoCheckUpdates)
        {
            _ = CheckForUpdatesAsync();
        }
    }

    /// <summary>查询可用模型（真实会话；容错解析 get_available_models）。</summary>
    [RelayCommand]
    private async Task RefreshModelsAsync()
    {
        if (_context is not { QueryModels: { } query } ctx)
        {
            return;
        }

        _queryCts?.Cancel();
        _queryCts?.Dispose();
        _queryCts = new CancellationTokenSource();
        var cancellation = _queryCts.Token;
        var generation = _attachmentGeneration;
        try
        {
            var response = await query(cancellation);
            if (generation != _attachmentGeneration || cancellation.IsCancellationRequested) return;
            if (!response.Success || response.Data is not { } data)
            {
                ModelsError = response.Error ?? "get_available_models 未返回数据";
                return;
            }

            ModelRows.Clear();
            ModelChoices.Clear();
            ModelChoices.Add(new ModelChoice(null, "跟随默认"));
            if (data.TryGetProperty("models", out var models) &&
                models.ValueKind == JsonValueKind.Array)
            {
                foreach (var model in models.EnumerateArray())
                {
                    var provider = ReadString(model, "provider");
                    var id = ReadString(model, "id") ?? ReadString(model, "name");
                    var name = ReadString(model, "name");
                    if (id is null)
                    {
                        continue;
                    }

                    var value = provider is null ? id : $"{provider}/{id}";
                    var label = value + (name is not null && name != id ? $"（{name}）" : string.Empty);
                    ModelRows.Add(label);
                    ModelChoices.Add(new ModelChoice(value, label));
                }
            }

            RebuildFilteredModels();
            SyncModelSelection();
            ModelsError = ModelRows.Count == 0 ? "模型列表为空或形状不符" : null;
            OnPropertyChanged(nameof(HasModelRows));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (generation == _attachmentGeneration) ModelsError = ex.GetBaseException().Message;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
    }

    /// <summary>InformationalVersion → 显示用 "1.0.0 (abcdef0)"；无元数据时原样。</summary>
    internal static string ShortVersion(string informational)
    {
        var plus = informational.IndexOf('+');
        if (plus < 0)
        {
            return informational;
        }

        var version = informational[..plus];
        var hash = informational[(plus + 1)..];
        return hash.Length > 7 ? $"{version} ({hash[..7]})" : $"{version} ({hash})";
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>读取 AgentDirectory/models.json 的 provider 列表（密钥一律脱敏）。</summary>
    [RelayCommand]
    private void RefreshModelsConfig()
    {
        Providers.Clear();
        if (_context?.AgentDirectory is not { } agentDir)
        {
            ModelsConfigStatus = "mock 模式不可用";
            OnPropertyChanged(nameof(HasProviders));
            return;
        }

        var snapshot = PiModelsConfig.Read(agentDir);
        if (!snapshot.FileExists)
        {
            ModelsConfigStatus = $"models.json 不存在（{snapshot.Path}）";
        }
        else if (snapshot.Error is { } error)
        {
            ModelsConfigStatus = error;
        }
        else
        {
            foreach (var provider in snapshot.Providers)
            {
                Providers.Add(provider);
            }

            ModelsConfigStatus = Providers.Count == 0 ? "models.json 中没有 provider" : null;
        }

        OnPropertyChanged(nameof(HasProviders));
    }

    [RelayCommand]
    private async Task OpenFolderAsync(string? path)
    {
        if (path is not null && _context?.OpenFolder is { } open)
        {
            await open(path);
        }
    }

    [RelayCommand]
    private async Task CopyDiagnosticsAsync()
    {
        if (_context is not { } ctx || ctx.CopyText is null)
        {
            return;
        }

        var settings = ctx.Store.Current;
        var summary = DiagnosticsSummary.Build(new DiagnosticsSummary.Input(
            ProjectPath: ctx.ProjectPath(),
            ModeText: ctx.IsMock ? "mock 数据演示" : "真实后端会话",
            ApprovalText: ctx.ApprovalText?.Invoke(),
            ApprovalDiagnostic: ctx.ApprovalDiagnostic?.Invoke(),
            ActiveExtensionPaths: ctx.ActiveExtensionPaths?.Invoke() ?? [],
            LastStartupFailure: ctx.LastStartupFailure?.Invoke(),
            AgentDirectory: ctx.AgentDirectory,
            DataRoot: ctx.DataRoot,
            LogsDirectory: ctx.LogsDirectory,
            Settings: settings,
            SettingsLoadError: ctx.Store.LastLoadError,
            PiVersion: PiVersion,
            NodeVersion: NodeVersion));
        await ctx.CopyText(summary);
        CopyStatus = "已复制到剪贴板";
    }

    // ── 性能与内存 ──

    partial void OnSoftwareRenderingChanged(bool value) => SavePerformance(restartRequired: true);
    partial void OnDisableVisualEffectsChanged(bool value) => SavePerformance();
    partial void OnReduceTerminalScrollbackChanged(bool value) => SavePerformance();

    /// <summary>把三个开关写回 settings.json，并立即更新运行时读数。</summary>
    private void SavePerformance(bool restartRequired = false)
    {
        if (_suppressPerformanceSave || _context is null) return;
        try
        {
            var next = _context.Store.Update(current => current with
            {
                Performance = new PerformanceSettings(
                    SoftwareRendering, DisableVisualEffects, ReduceTerminalScrollback),
            });
            MemoryProfile.Apply(next.Performance);
            PerformanceState = restartRequired ? SettingSaveState.RestartRequired : SettingSaveState.Applied;
            PerformanceStateText = restartRequired ? "已保存 · 重启后生效" : "已保存 · 已应用";
        }
        catch (Exception ex)
        {
            PerformanceState = SettingSaveState.Failed;
            PerformanceStateText = "保存失败：" + ex.GetBaseException().Message;
        }
    }

    [RelayCommand]
    private void ApplyLowMemoryPreset()
    {
        _suppressPerformanceSave = true;
        SoftwareRendering = true;
        DisableVisualEffects = true;
        ReduceTerminalScrollback = true;
        _suppressPerformanceSave = false;
        SavePerformance(restartRequired: true);
    }

    [RelayCommand]
    private void ApplyHighPerformancePreset()
    {
        _suppressPerformanceSave = true;
        SoftwareRendering = false;
        DisableVisualEffects = false;
        ReduceTerminalScrollback = false;
        _suppressPerformanceSave = false;
        SavePerformance(restartRequired: true);
    }
}
