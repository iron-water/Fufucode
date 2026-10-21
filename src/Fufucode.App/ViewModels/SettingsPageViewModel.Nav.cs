using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using Fufucode.App.Features.SettingsExperience;

namespace Fufucode.App.ViewModels;

/// <summary>
/// 设置侧栏三级架构（D2）与「相关设置」卡片（D3）。
/// 目录 → 升降级偏好 → 分组渲染都在这里收口；主 VM 只负责页面内容与持久化设置。
/// </summary>
public sealed partial class SettingsPageViewModel
{
    /// <summary>当前分类的「相关设置」跳转卡片。</summary>
    public ObservableCollection<SettingsRelatedLink> RelatedLinks { get; } = new();
    public bool HasRelatedSettings => RelatedLinks.Count > 0;

    private SettingsNavPreferenceStore _navStore = new(null);

    /// <summary>侧栏条目重建（搜索或升降级）后触发，供视图按 Id 重新同步选中项。</summary>
    public event EventHandler? NavigationRebuilt;

    /// <summary>按 Id 选中侧栏分类（视图层点击时调用）。
    /// 「其他」二级卡或相关链接指向的条目不在一级侧栏时，只切 SelectedCategory，
    /// 不往 FilteredCategories 塞——那等于把二级功能提升成一级菜单。</summary>
    [RelayCommand]
    public void SelectCategory(string? id)
    {
        if (id is null) return;
        var category = FilteredCategories.FirstOrDefault(item => item.Id == id);
        // 目标不在一级侧栏（Other 层条目）：直接从目录构建选中，侧栏结构不动。
        if (category is null && SettingsNavCatalog.Find(id) is { } definition)
            category = ToCategory(definition, _navStore.Current);
        if (category is not null) SelectedCategory = category;
    }

    internal void RaiseNavigationRebuilt() => NavigationRebuilt?.Invoke(this, EventArgs.Empty);

    /// <summary>注入侧栏偏好存储（组合根在 Attach 时给出数据根目录）。</summary>
    public void AttachNavPreference(string? dataRoot)
    {
        _navStore = new SettingsNavPreferenceStore(dataRoot);
        RebuildCategories();
    }

    /// <summary>自测接缝：当前生效的升降级偏好。</summary>
    internal SettingsNavPreference NavPreference => _navStore.Current;

    /// <summary>"其他"占位项 id（不对应目录里任何真实条目，作为一级菜单的杂项入口）。</summary>
    public const string OtherHubId = "other";

    /// <summary>按目录 + 偏好重建全部侧栏条目（所有真实功能平铺 + 「其他」占位）。</summary>
    private void RebuildCategories()
    {
        Categories.Clear();
        foreach (var item in EnumerateSidebarItems(_navStore.Current))
            Categories.Add(item);
    }

    /// <summary>
    /// 一级侧栏条目流：Basic + Features 平铺（功能区/大功能是一级），
    /// Other 层的小功能收进末尾的「其他」占位项，点占位项在内容区列二级卡。
    /// </summary>
    internal static IEnumerable<SettingsCategoryViewModel> EnumerateSidebarItems(SettingsNavPreference preference)
    {
        foreach (var definition in SettingsNavCatalog.All)
        {
            var effective = preference.EffectiveTier(definition.Id);
            if (effective is SettingsNavTier.Basic or SettingsNavTier.Features)
                yield return ToCategory(definition, preference);
        }
        yield return OtherHubItem();
    }

    /// <summary>「其他」占位一级条目。</summary>
    private static SettingsCategoryViewModel OtherHubItem() =>
        new(OtherHubId, "其他", "通知 配置 用量 工作台模块 会话 消息 other misc",
            "AppsList", "#8A8A8E", SettingsNavTier.Basic, SettingsNavTier.Basic);

    /// <summary>「其他」页右侧列出的小功能入口（当前生效 tier 仍为 Other 的条目）。</summary>
    public IReadOnlyList<SettingsCategoryViewModel> OtherHubItems =>
        SettingsNavCatalog.All
            .Where(d => _navStore.Current.EffectiveTier(d.Id) == SettingsNavTier.Other)
            .Select(d => ToCategory(d, _navStore.Current))
            .ToArray();

    /// <summary>把目录条目 + 生效层级转成侧栏条目。</summary>
    internal static SettingsCategoryViewModel ToCategory(SettingsNavDefinition definition, SettingsNavPreference preference) =>
        new(definition.Id, definition.Title, definition.SearchTerms, definition.Icon, definition.IconColor,
            preference.EffectiveTier(definition.Id), definition.Tier);

    /// <summary>升降级命令：基础类条目不可移动；偏好写盘后立即重排侧栏。</summary>
    [RelayCommand]
    private void ToggleNavTier(SettingsCategoryViewModel? category)
    {
        if (category is null || !SettingsNavPreference.CanMove(category.Id)) return;
        _navStore.Save(_navStore.Current.Toggle(category.Id));
        RebuildCategories();
        UpdateCategoryFilter();
        OnPropertyChanged(nameof(CurrentCategoryIsPinned));
        OnPropertyChanged(nameof(CurrentCategoryCanPin));
    }

    /// <summary>当前选中分类能否被钉到一级侧栏（或从一级侧栏收纳回「其他」）。</summary>
    public bool CurrentCategoryCanPin =>
        SelectedCategory is { } cat && SettingsNavPreference.CanMove(cat.Id);
    /// <summary>Pin 开关块只在分类一级页渲染；进入二级页后隐藏。</summary>
    public bool ShowPinSection => CurrentCategoryCanPin && !IsSubPage;

    /// <summary>当前分类是否已在一级侧栏（基础类固定算已在一级；大功能段算钉住；Other 未钉住）。</summary>
    public bool CurrentCategoryIsPinned =>
        SelectedCategory is { } cat && cat.Id != OtherHubId &&
        _navStore.Current.EffectiveTier(cat.Id) != SettingsNavTier.Other;

    /// <summary>切换当前分类是否钉在一级侧栏（功能页内的"固定到侧栏"开关）。</summary>
    [RelayCommand]
    private void ToggleCurrentPin()
    {
        if (SelectedCategory is not { } cat || !SettingsNavPreference.CanMove(cat.Id)) return;
        _navStore.Save(_navStore.Current.Toggle(cat.Id));
        RebuildCategories();
        UpdateCategoryFilter();
        OnPropertyChanged(nameof(CurrentCategoryIsPinned));
    }

    /// <summary>
    /// 统一设置页跳转：一级分类 id 走 SelectCategory；子页 id 先选父分类再下钻。
    /// 供功能状态页、相关设置和命令面板复用，避免“只设置了子页 id 但分类没切”。
    /// </summary>
    public void NavigateTo(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        if (SearchText.Length > 0) SearchText = string.Empty;
        var categoryId = SettingsNavCatalog.Find(id)?.Id
            ?? SettingsNavCatalog.Find(id.Split('.')[0])?.Id;
        if (categoryId is not null) SelectCategory(categoryId);
        if (!string.Equals(id, categoryId, StringComparison.Ordinal)) OpenPage(id);
    }

    /// <summary>通知策略页直达通知渠道（应用内/通知页/Windows），两个持久化真源不再各藏一处。</summary>
    [RelayCommand]
    private void OpenNotificationChannels() => NavigateTo("functions.floating");

    /// <summary>「相关设置」卡片：选中对应分类，二级页 id 时同时下钻。</summary>
    [RelayCommand]
    private void OpenRelated(SettingsRelatedLink? link)
    {
        if (link is null) return;
        var categoryId = SettingsRelatedSettings.CategoryOf(link.Id);
        if (SearchText.Length > 0) SearchText = string.Empty; // 保证目标分类可见
        // SelectCategory 已处理「其他」二级功能不在侧栏时的目录回退。
        SelectCategory(categoryId);
        if (SelectedCategory?.Id != categoryId) return;
        if (link.Id != categoryId) OpenPage(link.Id);
        else GoBack();
    }

    private void RebuildRelatedLinks()
    {
        RelatedLinks.Clear();
        foreach (var link in SettingsRelatedSettings.For(SelectedCategory?.Id))
            RelatedLinks.Add(link);
        OnPropertyChanged(nameof(HasRelatedSettings));
    }
}
