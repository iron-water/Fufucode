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
using Fufucode.App.Integration;
using Fufucode.Core;
using Fufucode.Pi;

namespace Fufucode.App.ViewModels;

/// <summary>关于页键值行。</summary>
public sealed record KeyValueRow(string Key, string Value);

public sealed record SettingsCategoryViewModel(string Id, string Title, string SearchTerms, string Icon = "Settings", string IconColor = "");

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
        Categories =
        [new("appearance", "外观", "主题 亮色 暗色 强调色 字号 密度 圆角 阅读宽度 停靠区 页面位置 显隐 入口位置 appearance theme layout dock", "Color", "#4CC2FF"),
         new("functions", "功能", "消息 摘要 头像 名称 输入区 显示 选项 功能 function detail", "AppsList", "#4CC2FF"),
         new("config", "配置文件", "JSON 导入 导出 项目覆盖 全局 ui.json config", "Code", "#B48CFF"),
         new("models", "模型与思考", "模型 思考 等级 默认 model thinking", "Brain", "#FF9E4C"),
         new("network", "网络与端点", "代理 自定义端点 API Key 环境变量 模型配置 URL proxy endpoint provider", "Globe", "#6CCB5F"),
         new("notifications", "通知", "完成 失败 权限 问题 审批 提醒 失焦 notification", "Alert", "#FFC83D"),
         new("modules", "工作台模块", "浏览器 启用 停用 内建 模块 browser module", "PuzzlePiece", "#FF99A4"),
         new("shortcuts", "键盘快捷键", "快捷键 按键 绑定 冲突 keyboard shortcuts", "Keyboard", "#8A8A8E"),
         new("storage", "存储", "空间 占用 缓存 日志 保留 清理 storage cache logs", "Folder", "#8A8A8E"),
         new("about", "关于", "版本 数据目录 配置目录 日志 诊断 runtime about", "Info", "#8A8A8E")];
        SubPages =
        [
            new("appearance", "外观", "主题、颜色、字体与密度", "主题 亮色 暗色 强调色 字号 密度 圆角 阅读宽度 theme accent font"),
            new("appearance.arrange", "前端功能排布", "区域显隐、页面位置与入口位置", "停靠区 页面位置 显隐 状态栏 入口位置 dock layout page"),
            new("appearance.arrange.aux", "辅助栏", "逐个功能选择出现在左辅助、右辅助或不显示", "辅助 left right auxiliary"),
            new("functions", "会话与消息", "输入区、摘要与消息显示", "消息 摘要 头像 名称 输入区 显示 message"),
            new("functions.names", "名称", "用户称呼与助手名称", "名称 用户称呼 助手名称 name"),
            new("functions.floating", "浮窗", "浮窗显示、固定与提醒渠道", "浮窗 提醒 渠道 固定 floating"),
        ];
        UpdateCategoryFilter();
    }

    public IReadOnlyList<SettingsCategoryViewModel> Categories { get; }
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
        : SubPages.FirstOrDefault(p => p.Id == CurrentPageId)?.Title ?? CategoryTitle;
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
            }
            return string.Join(" › ", parts);
        }
    }
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
        foreach (var name in new[] { nameof(CurrentPageId), nameof(Breadcrumb), nameof(PageTitle), nameof(BackToolTip), nameof(IsSubPage), nameof(CategorySubPages), nameof(HasSubPages), nameof(ActivePageId), nameof(IsSearching) })
            OnPropertyChanged(name);
        NotifyCategoryVisibility();
    }
    [RelayCommand] private void BackToCategory() => GoBack();
    public bool ShowAppearance => SelectedCategory?.Id == "appearance";
    public bool ShowFunctions => SelectedCategory?.Id == "functions";
    public bool ShowConfigSection => SelectedCategory?.Id == "config";
    /// <summary>外观、功能、配置文件三个分类共用分区控件（含它们的二级页）。</summary>
    public bool ShowWorkbenchSection => ShowAppearance || ShowFunctions || ShowConfigSection;
    public string WorkbenchSectionId => SelectedCategory?.Id switch { "functions" => "functions", "config" => "config", _ => "appearance" };
    private string Search => SearchText.Trim();
    private bool SearchMatches(string terms) => Search.Length == 0 || terms.Contains(Search, StringComparison.OrdinalIgnoreCase);
    private const string LookTerms = "主题 亮色 暗色 强调色 字号 密度 圆角 阅读宽度 预设 appearance theme";
    private const string ArrangeTerms = "停靠区 页面位置 显隐 状态栏 入口位置 按钮 布局 dock layout page status";
    /// <summary>外观页左栏：常规外观（主题、颜色、字体、密度、圆角、阅读宽度）。</summary>
    public bool ShowLookColumn => ShowAppearance && SearchMatches(LookTerms);
    /// <summary>外观页右栏：前端功能排布（停靠、页面位置、入口位置）。</summary>
    public bool ShowArrangementColumn => ShowAppearance && SearchMatches(ArrangeTerms);

    /// <summary>保留为独立左侧页面的五个分区。</summary>
    public bool ShowModels => SelectedCategory?.Id == "models";
    public bool ShowNetwork => SelectedCategory?.Id == "network";
    public bool ShowNotifications => SelectedCategory?.Id == "notifications";
    public bool ShowAbout => SelectedCategory?.Id == "about";
    public bool ShowModules => SelectedCategory?.Id == "modules";
    public bool ShowShortcuts => SelectedCategory?.Id == "shortcuts";
    [ObservableProperty] private Avalonia.Controls.Control? _shortcutsContent;
    public bool ShowStorage => SelectedCategory?.Id == "storage";
    [ObservableProperty] private Avalonia.Controls.Control? _storageContent;
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
    }

    private void UpdateCategoryFilter()
    {
        var term = SearchText.Trim();
        FilteredCategories.Clear();
        foreach (var category in Categories)
            if (term.Length == 0 || (category.Title + " " + category.SearchTerms).Contains(term, StringComparison.OrdinalIgnoreCase))
                FilteredCategories.Add(category);
        if (SelectedCategory is null || !FilteredCategories.Contains(SelectedCategory)) SelectedCategory = FilteredCategories.FirstOrDefault();
        OnPropertyChanged(nameof(HasSearchResults));
        OnPropertyChanged(nameof(SearchSummary));
        NotifyCategoryVisibility();
    }
    private bool MatchesOption(string terms) => SearchText.Trim().Length == 0 || terms.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase);
    private void NotifyCategoryVisibility()
    {
        foreach (var name in new[] { nameof(CategoryTitle), nameof(ShowAppearance), nameof(ShowFunctions), nameof(ShowConfigSection), nameof(ShowWorkbenchSection), nameof(WorkbenchSectionId), nameof(ShowLookColumn), nameof(ShowArrangementColumn), nameof(ShowModels), nameof(ShowNetwork), nameof(ShowNotifications), nameof(ShowAbout), nameof(ShowModules),
                     nameof(ShowStorage), nameof(ShowShortcuts), nameof(ShowThemeRow), nameof(ShowModelRow), nameof(ShowThinkingRow), nameof(ShowProxyRow), nameof(ShowEndpointRow), nameof(ShowEndpointForm), nameof(ShowModelsConfig) })
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
    partial void OnEndpointFormOpenChanged(bool value) { NotifyDraftState(); OnPropertyChanged(nameof(ShowEndpointForm)); }
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
    private void ResetEndpointDraft()
    {
        var endpoint = _context?.Store.Current.Endpoint;
        EndpointProviderName = endpoint?.ProviderName;
        EndpointBaseUrl = endpoint?.BaseUrl;
        EndpointModelId = endpoint?.ModelId;
        EndpointKeyModeIndex = 1;
        EndpointEnvName = endpoint?.ApiKeyEnvironmentVariable;
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
        Func<CancellationToken, Task<PiRpcResponse>>? QueryModels,
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
        Func<string, string, CancellationToken, Task<PiRpcResponse>>? SetModel = null,
        Func<string, CancellationToken, Task<PiRpcResponse>>? SetThinkingLevel = null,
        Func<CancellationToken, Task<PiRpcResponse>>? QueryState = null,
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
    /// <summary>模型下拉项：Value=null 表示「Pi 默认」（settings.selectedModel=null）。</summary>
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
        new("ultra", "ultra（Pi 未支持）", false),
        new("auto", "auto（Pi 未支持）", false),
    ];

    [ObservableProperty]
    private string _currentModelText = "—";

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
                    ? "已保存：新建会话将使用 Pi 默认模型"
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

        if (string.IsNullOrWhiteSpace(modelId))
        {
            error = "请填写模型 ID";
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

        if (generation != _attachmentGeneration) return;

        var endpoint = new CompatibleEndpoint(provider, baseUrl, modelId, envName);
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
        EndpointInfo = "端点已保存（密钥经凭据管理器/环境变量引用，不写入配置文件）";
        RestartNoticeText = "端点已保存，重启会话后生效";
        NetworkState = SettingSaveState.RestartRequired;
        NetworkStateText = "已保存 · 等待重启会话";
        _hasEndpointKeyDraft = false;
        _endpointDraftBaseline = CaptureEndpointDraft();
        NotifyDraftState();
        RefreshModelsConfig();
        SyncModelSelection();
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
        _suppressDraftTracking = true;
        _suppressThemeApply = true;
        ThemeIndex = IndexFor(context.Store.Current.Theme);
        _suppressThemeApply = false;
        var current = context.Store.Current;
        ProxySummary = current.ProxyUrl is { } proxy ? $"已设置（{proxy}）" : "未设置";
        ProxyInput = current.ProxyUrl;
        ProxyError = null;
        EndpointSummary = current.Endpoint is { } ep
            ? $"{ep.ProviderName} · {ep.ModelId}"
            : "未设置";
        EndpointProviderName = current.Endpoint?.ProviderName;
        EndpointBaseUrl = current.Endpoint?.BaseUrl;
        EndpointModelId = current.Endpoint?.ModelId;
        // 已配置端点回填为「使用环境变量」模式（密钥不回填，凭据不进入 VM）。
        EndpointKeyModeIndex = 1;
        EndpointEnvName = current.Endpoint?.ApiKeyEnvironmentVariable;
        _hasEndpointKeyDraft = false;
        _endpointDraftBaseline = CaptureEndpointDraft();
        HasEndpoint = current.Endpoint is not null;
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
        VersionRows.Add(new KeyValueRow("Pi", PiVersion));
        VersionRows.Add(new KeyValueRow("Node", NodeVersion));
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
            ModelChoices.Add(new ModelChoice(null, "Pi 默认"));
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
            ModeText: ctx.IsMock ? "mock 数据演示" : "真实 Pi 会话",
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
}
