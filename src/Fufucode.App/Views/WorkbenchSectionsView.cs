using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Fufucode.App.Features.SettingsExperience;
using Fufucode.App.Infrastructure;
using Fufucode.App.ViewModels;
using Fufucode.App.Features.Plugins;

namespace Fufucode.App.Views;

/// <summary>
/// 设置页各级页面的内容区。按 Windows 设置的语言组织：分组标题 + 独立行卡，
/// 每行是一条设置（标题、说明、右侧控件），需要下钻的行显示进入箭头。
/// 页面 id 与 <see cref="SettingsPageViewModel.SubPages"/> 对应；切换页面时重建，
/// 同一页面内配置变化只刷新取值，不重建控件树。
/// 纪律：所有行必须经 <see cref="SettingsLayout"/> 构建，页面代码不再自画卡片/字号/间距。
/// </summary>
public sealed class WorkbenchSectionsView : UserControl
{
    private bool _refreshing;
    private WorkbenchCustomizationViewModel? _vm;
    private NotificationDeliveryViewModel? _delivery;
    private ViewModels.BrowserSettingsViewModel? _browserSettings;
    private StackPanel? _rows;
    private string? _builtPage;
    private readonly List<Action<WorkbenchAppearance>> _bindings = new();
    private readonly Dictionary<Control, bool> _searchBaseline = new();

    public static readonly StyledProperty<string> PageProperty =
        AvaloniaProperty.Register<WorkbenchSectionsView, string>(nameof(Page), "appearance");
    public static readonly StyledProperty<bool> SearchActiveProperty =
        AvaloniaProperty.Register<WorkbenchSectionsView, bool>(nameof(SearchActive));
    public static readonly StyledProperty<string> SearchTextProperty =
        AvaloniaProperty.Register<WorkbenchSectionsView, string>(nameof(SearchText), "");

    /// <summary>页面 id：appearance / appearance.arrange / functions / functions.names / functions.floating / config。</summary>
    public string Page
    {
        get => GetValue(PageProperty);
        set => SetValue(PageProperty, value);
    }
    /// <summary>搜索中时隐藏与关键词无关的行。</summary>
    public bool SearchActive
    {
        get => GetValue(SearchActiveProperty);
        set => SetValue(SearchActiveProperty, value);
    }
    /// <summary>搜索关键词（小写），用于按行过滤。</summary>
    public string SearchText
    {
        get => GetValue(SearchTextProperty);
        set => SetValue(SearchTextProperty, value);
    }

    /// <summary>保留旧分类ID兼容导航，内容已迁移为独立通知渠道。</summary>
    public void AttachNotificationDelivery(NotificationDeliveryViewModel? delivery)
    {
        _delivery = delivery;
        if (delivery is not null)
        {
            delivery.Changed += () => Dispatcher.UIThread.Post(Refresh);
            delivery.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(NotificationDeliveryViewModel.WindowsDeliveryStatus)) Dispatcher.UIThread.Post(Refresh);
            };
        }
        if (Page.StartsWith("functions", StringComparison.Ordinal)) Render();
    }

    /// <summary>浏览器设置真源（BRW-1）：卡片从这里取值并回写 browser-settings.json。</summary>
    public void AttachBrowserSettings(ViewModels.BrowserSettingsViewModel? browserSettings)
    {
        _browserSettings = browserSettings;
        if (browserSettings is not null)
            browserSettings.Changed += _ => Dispatcher.UIThread.Post(Refresh);
        if (string.Equals(Page, "browser", StringComparison.Ordinal)) Render();
    }

    /// <summary>二级页跳转请求（分类首页的入口行点击时触发）。</summary>
    public event Action<string>? PageRequested;
    /// <summary>真实动作请求（功能状态页的“打开终端/文件/用量”等入口）。</summary>
    public event Action<string>? ActionRequested;

    public WorkbenchSectionsView()
    {
        // 插件市场安装/停用皮肤后，外观页的皮肤选择器即时反映可用集合。
        Fufucode.Core.Plugins.PluginSkinRegistry.Changed += () =>
            Dispatcher.UIThread.Post(() => { if (Page.StartsWith("appearance", StringComparison.Ordinal)) Render(); });
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null)
            {
                _vm.PropertyChanged -= Changed;
                _vm.ActionsChanged -= RebuildFromHost;
                _vm.PagesChanged -= RebuildFromHost;
            }
            _vm = DataContext as WorkbenchCustomizationViewModel;
            if (_vm is not null)
            {
                _vm.PropertyChanged += Changed;
                _vm.ActionsChanged += RebuildFromHost;
                _vm.PagesChanged += RebuildFromHost;
            }
            Render();
        };
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PageProperty || change.Property == SearchActiveProperty || change.Property == SearchTextProperty) Render();
    }

    private void Changed(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(WorkbenchCustomizationViewModel.Current)) return;
        Dispatcher.UIThread.Post(Refresh);
    }

    private void RebuildFromHost() => Dispatcher.UIThread.Post(Render);

    private void Render()
    {
        if (_vm is not { } vm) { Content = null; _rows = null; _builtPage = null; return; }
        var rows = new StackPanel { Spacing = 0 };
        _bindings.Clear();
        _searchBaseline.Clear();
        _firstGroup = true;
        switch (Page)
        {
            case "appearance": RenderAppearance(vm, rows); break;
            case "appearance.arrange": RenderArrangement(vm, rows); break;
            case "appearance.arrange.aux": RenderAuxiliary(vm, rows); break;
            case "functions": RenderConversation(vm, rows); break;
            case "functions.names": RenderNames(vm, rows); break;
            case "functions.floating": RenderFloating(vm, rows); break;
            case "browser": RenderBrowserSettings(vm, rows); break;
            case "terminal" or "files" or "automation" or "usage": RenderFeatureStatus(vm, rows); break;
            default: RenderConfig(vm, rows); break;
        }
        _rows = rows;
        _builtPage = Page;
        RefreshValues(vm.Current);
        Content = rows;
    }

    private void Refresh()
    {
        if (_vm is not { } vm) return;
        if (_rows is null || !string.Equals(_builtPage, Page, StringComparison.Ordinal)) { Render(); return; }
        RefreshValues(vm.Current);
    }
    private void RefreshValues(WorkbenchAppearance current)
    {
        _refreshing = true;
        try
        {
            foreach (var binding in _bindings) binding(current);
            ApplySearchFilter();
        }
        finally { _refreshing = false; }
    }

    private void ApplySearchFilter()
    {
        if (_rows is null) return;
        var query = SearchText.Trim();
        if (query.Length == 0)
        {
            foreach (var child in _rows.Children.OfType<Control>()) RestoreVisibility(child);
            return;
        }

        var pendingHeaders = new List<Control>();
        foreach (var child in _rows.Children.OfType<Control>())
        {
            if (child.Classes.Contains("settingsGroupHeader") || child.Classes.Contains("settingsGroupToggle"))
            {
                pendingHeaders.Add(child);
                continue;
            }

            var visible = FilterSearchControl(child, query);
            child.IsVisible = visible;
            foreach (var header in pendingHeaders) header.IsVisible = visible;
            pendingHeaders.Clear();
        }
        foreach (var header in pendingHeaders) header.IsVisible = false;
    }

    private bool FilterSearchControl(Control control, string query)
    {
        if (!_searchBaseline.TryGetValue(control, out var baseline))
        {
            baseline = control.IsVisible;
            _searchBaseline[control] = baseline;
        }

        if (control.Classes.Contains("settingsRow"))
        {
            var terms = SettingsLayout.GetSearchTerms(control) ?? "";
            return baseline && terms.Contains(query, StringComparison.OrdinalIgnoreCase);
        }

        // LiquidGlass 的 Group 是 Border.settingsGroup 包一层 settingsGroupList；
        // 搜索必须递归到包裹层内部，否则所有分组都会被当成“非行式控件”整体保留。
        if (control is Border { Child: Control grouped } && control.Classes.Contains("settingsGroup"))
        {
            var visible = FilterSearchControl(grouped, query);
            grouped.IsVisible = visible;
            return visible && baseline;
        }

        if (control is Panel panel && panel.Classes.Contains("settingsGroupList"))
        {
            var visible = false;
            foreach (var child in panel.Children.OfType<Control>())
            {
                var childVisible = FilterSearchControl(child, query);
                child.IsVisible = childVisible;
                visible |= childVisible;
            }
            return visible;
        }

        // 非行式控件（说明卡、JSON 编辑区等）按自身可见性保留，不参与词过滤。
        return baseline;
    }

    private void RestoreVisibility(Control control)
    {
        if (_searchBaseline.TryGetValue(control, out var baseline)) control.IsVisible = baseline;
        if (control is Border { Child: Control grouped } && control.Classes.Contains("settingsGroup"))
            RestoreVisibility(grouped);
        if (control is Panel panel)
            foreach (var child in panel.Children.OfType<Control>()) RestoreVisibility(child);
    }

    // ================= 页面 =================

    private void RenderAppearance(WorkbenchCustomizationViewModel vm, StackPanel rows)
    {
        var settings = vm.Current;

        var skinNote = new TextBlock { Classes = { "secondary" }, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 8) };
        _bindings.Add(current => { skinNote.IsVisible = current.SkinResolution.FellBack; skinNote.Text = current.SkinResolution.Reason; });

        Group(rows, "主题与风格",
            ChoiceRow(vm, "主题", null, new[] { "跟随系统", "浅色", "深色" }, s => s.Theme, (s, v) => s with { Theme = v }, "Color", "system", "light", "dark"),
            ChoiceRow(vm, "皮肤", "Fluent 为默认；其他皮肤使用各自的颜色与控件形状。", Features.Skins.SkinCatalog.All.Select(s => s.DisplayName).ToArray(), s => s.SkinResolution.Definition.StorageKey, (s, v) => s with { Skin = v }, "Color", Features.Skins.SkinCatalog.All.Select(s => s.StorageKey).ToArray()),
            skinNote,
            // “视觉风格”旧轴已下线：flat/classic 由 Metro/Luna 真皮肤覆盖，glass（iOS 液态玻璃）是未做的新皮肤，
            // 不再以半成品占位。Surface 字段保留读取以兼容旧配置。
            CustomChoiceRow("预设", "一次性套用一组取值，之后仍可单独微调", new[] { "标准", "紧凑", "阅读" }, PresetOf, value => vm.Preset(value), "ArrowClockwise", "standard", "compact", "reading"));

        var colorGroupStart = rows.Children.Count;
        Group(rows, "主题色与强调色",
            FluentOnly(ToggleRow("跟随系统主题色", "Fluent 使用此选项；Metro 与 Material 3 使用各自固定主题色。", s => s.Accent == "system",
                value => vm.Change(s => s with { Accent = value ? "system" : Hex(s.EffectiveAccent) }))),
            FluentOnly(ColorPickerRow(vm,
                "自定义主题色", "UI 通用颜色：选中、链接、主按钮；格式 #RRGGBB",
                s => s.Accent == "system" ? Hex(s.EffectiveAccent) : s.Accent,
                (s, value) => s with { Accent = value })),
            EmphasisPickerRow(vm));
        var colorGroup = rows.Children.Skip(colorGroupStart).ToArray();
        _bindings.Add(current =>
        {
            foreach (var control in colorGroup)
                control.IsVisible = current.SkinResolution.Skin == Features.Skins.SkinId.Fluent;
        });

        var baseGroupStart = rows.Children.Count;
        Group(rows, "底色（Fluent 暗色）",
            CustomChoiceRow("窗口底色", "最底层的深灰背景",
                WorkbenchAppearance.BaseBackgroundLabels.ToArray(), s => s.BaseBackground,
                value => vm.Change(s => s with { BaseBackground = value }), "Color",
                WorkbenchAppearance.BaseBackgroundChoices.ToArray()),
            CustomChoiceRow("一般栏", "标题栏、侧栏、停靠区等常用栏的浅灰",
                WorkbenchAppearance.BaseBarLabels.ToArray(), s => s.BaseBar,
                value => vm.Change(s => s with { BaseBar = value }), "Color",
                WorkbenchAppearance.BaseBarChoices.ToArray()),
            CustomChoiceRow("高亮", "悬停与选中态；从不太亮到更亮",
                WorkbenchAppearance.BaseHighlightLabels.ToArray(), s => s.BaseHighlight,
                value => vm.Change(s => s with { BaseHighlight = value }), "Color",
                WorkbenchAppearance.BaseHighlightChoices.ToArray()),
            SettingsLayout.Note("仅 Fluent 暗色生效；每一档只给三个备选，避免把层级关系选反。"));
        var baseGroup = rows.Children.Skip(baseGroupStart).ToArray();
        _bindings.Add(current =>
        {
            foreach (var control in baseGroup)
                control.IsVisible = current.SkinResolution.Skin == Features.Skins.SkinId.Fluent;
        });

        Group(rows, "字体与密度",
            ChoiceRow(vm, "字号", null, new[] { "11", "12", "13", "14", "16", "18", "20", "24" },
                s => ((int)Math.Round(s.FontSize)).ToString(), (s, v) => s with { FontSize = double.Parse(v) }, "Document", "11", "12", "13", "14", "16", "18", "20", "24"),
            ChoiceRow(vm, "密度", "行高与列表间距", new[] { "紧凑", "舒适", "宽松" }, s => s.Density, (s, v) => s with { Density = v }, "AppsList", "compact", "cozy", "airy"),
            FluentOnly(ChoiceRow(vm, "圆角", null, new[] { "无", "中", "大" }, s => s.Radius, (s, v) => s with { Radius = v }, "PanelRight", "none", "mid", "high")),
            ChoiceRow(vm, "阅读宽度", "正文的最大阅读宽度；代码与表格不受限", new[] { "窄", "标准", "宽", "自由" }, s => s.ReadingWidth, (s, v) => s with { ReadingWidth = v }, "Document", "narrow", "standard", "wide", "free"));
        var arrangeEntry = SettingsLayout.ChevronRow("前端功能排布", "区域显隐、页面位置、辅助栏与入口位置",
            () => PageRequested?.Invoke("appearance.arrange"), "PanelLeft");
        // 跨组下钻入口也沿用 8px 行距。不要再加组级 top margin：
        // 那会让同一页出现两套节奏（2026-10-21 截图指出的间距不一致）。
        rows.Children.Add(SettingsLayout.Group(arrangeEntry));

        Control FluentOnly(Control row)
        {
            _bindings.Add(current => row.IsVisible = row.IsEnabled = current.SkinResolution.Skin == Features.Skins.SkinId.Fluent);
            ToolTip.SetTip(row, "此项用于 Fluent；当前皮肤使用自己的颜色和形状。原设置会保留。");
            return row;
        }
    }

    private void RenderArrangement(WorkbenchCustomizationViewModel vm, StackPanel rows)
    {
        Group(rows, "区域显隐",
            ToggleRow("左侧边栏", "隐藏保留已打开页面与内容", _ => true, visible => vm.SetZoneVisible(DockZone.Left, visible), () => !vm.Layout.Hidden[DockZone.Left]),
            ToggleRow("右侧边栏", null, _ => true, visible => vm.SetZoneVisible(DockZone.Right, visible), () => !vm.Layout.Hidden[DockZone.Right]),
            ToggleRow("底部面板", "只有关闭最后一个终端页签才会终止进程", _ => true, visible => vm.SetZoneVisible(DockZone.Bottom, visible), () => !vm.Layout.Hidden[DockZone.Bottom]),
            ToggleRow("底部状态栏", "连接、Shell、活动与 Token 等运行状态，默认关闭", s => s.ShowStatusBar, value => vm.Change(s => s with { ShowStatusBar = value })));

        // 页面清单来自宿主真实注册表（含插件页），不在设置页维护第二份硬编码列表。
        var pageRows = vm.Pages
            .GroupBy(page => page.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .Select(page => (Control)CustomChoiceRow(page.Title, null,
                new[] { "左停靠区", "右停靠区", "底停靠区", "隐藏" },
                s => s.LayoutPageZone(page.Id),
                value => MovePage(vm, page.Id, value), null, "0", "1", "2", "3"))
            .ToList();
        pageRows.Add(ActionRow("恢复默认布局", "恢复页面默认位置与区域显隐，保留颜色与名称", "恢复",
            () => { vm.ResetLayout(); vm.Status = "已恢复默认布局，保留颜色与名称"; }, "settings-layout-reset"));
        Group(rows, "页面位置", pageRows.ToArray());

        var auxEntry = SettingsLayout.ChevronRow("辅助栏", "逐个功能选择出现在左辅助、右辅助，或不出现",
            () => PageRequested?.Invoke("appearance.arrange.aux"), "AppsList");
        rows.Children.Add(SettingsLayout.Group(auxEntry));

        var entryRows = vm.Actions.Select(action => PlacementRow(vm, action)).Cast<Control>().ToArray();
        var entryBody = SettingsLayout.Group(entryRows);
        rows.Children.Add(SettingsLayout.CollapsibleGroupHeader("入口位置", entryBody, entryRows.Length, _firstGroup));
        _firstGroup = false;
        rows.Children.Add(entryBody);
    }

    /// <summary>辅助栏页：逐个功能选左辅助/右辅助/不显示。</summary>
    private void RenderAuxiliary(WorkbenchCustomizationViewModel vm, StackPanel rows)
    {
        var choices = new[] { "左辅助", "右辅助", "不显示" };
        var values = new[] { UiActionPosition.MenuLeft, UiActionPosition.MenuRight, UiActionPosition.Hidden };
        var groups = vm.Actions
            .Where(action => IsAuxiliaryCandidate(vm, action))
            .GroupBy(action => action.Group)
            .OrderBy(group => group.Key, StringComparer.CurrentCulture);

        foreach (var group in groups)
        {
            var groupRows = group.Select(action => (Control)AuxRow(vm, action, choices, values)).ToArray();
            Group(rows, group.Key, groupRows);
        }
        rows.Children.Add(SettingsLayout.Note("只列出可以放进辅助栏的功能。顶栏常驻按钮、状态栏与编辑类动作不在此列；" +
            "完整的位置与顺序在「前端功能排布」里调整。"));
    }

    /// <summary>能否进辅助栏：默认位置在左/右辅助，或已被用户改到辅助栏的动作。</summary>
    private static bool IsAuxiliaryCandidate(WorkbenchCustomizationViewModel vm, AppActionDefinition action)
    {
        if (action.Id.StartsWith("edit.", StringComparison.Ordinal)) return false;
        if (action.Id.StartsWith("status.", StringComparison.Ordinal)) return false;
        var placement = vm.Current.Actions.GetValueOrDefault(action.Id) ?? WorkbenchActionDefaults.Placement(action.Id);
        return placement.Position is UiActionPosition.MenuLeft or UiActionPosition.MenuRight;
    }

    private Control AuxRow(WorkbenchCustomizationViewModel vm, AppActionDefinition action, string[] choices, UiActionPosition[] values)
    {
        var combo = new ComboBox
        {
            ItemsSource = choices, MinWidth = 110, MaxWidth = 110,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        var applying = false;
        combo.SelectionChanged += (_, _) =>
        {
            if (_refreshing || applying || combo.SelectedIndex < 0) return;
            applying = true;
            try
            {
                var placement = vm.Current.Actions.GetValueOrDefault(action.Id) ?? WorkbenchActionDefaults.Placement(action.Id);
                vm.SetPlacement(action.Id, values[combo.SelectedIndex], placement.Order);
            }
            finally { applying = false; }
        };
        _bindings.Add(current =>
        {
            var placement = current.Actions.GetValueOrDefault(action.Id) ?? WorkbenchActionDefaults.Placement(action.Id);
            var index = Math.Max(0, Array.IndexOf(values, placement.Position));
            applying = true;
            try { if (combo.SelectedIndex != index) combo.SelectedIndex = index; }
            finally { applying = false; }
        });
        // 说明与标题相同时不重复显示
        var description = string.Equals(action.Description, action.Title, StringComparison.Ordinal) ? null : action.Description;
        var row = SettingsLayout.Row(action.Title, description, combo, WorkbenchActionDefaults.Icon(action.Id));
        // 子菜单归属提示：这些动作在菜单里是收在父项下的
        if (WorkbenchActionDefaults.SubMenu(action.Id) is { } parent)
            ToolTip.SetTip(row, "在「" + parent + "」子菜单内");
        return row;
    }

    private void RenderConversation(WorkbenchCustomizationViewModel vm, StackPanel rows)
    {
        Group(rows, "消息",
            ToggleRow("会话头", "显示标题、项目与活动摘要", s => s.ShowSessionHeader, value => vm.Change(s => s with { ShowSessionHeader = value })),
            ToggleRow("消息操作", "复制、引用、编辑与更多操作常显", s => s.MessageActions == "show", value => vm.Change(s => s with { MessageActions = value ? "show" : "hover" }), null, "常显时始终可见；关闭后悬停出现"),
            ToggleRow("逐轮变更摘要", "本轮写入的文件清单", s => s.ShowChangeSummary, value => vm.Change(s => s with { ShowChangeSummary = value })),
            ToggleRow("逐轮生成产物", "本轮产出的文件链接", s => s.ShowArtifactSummary, value => vm.Change(s => s with { ShowArtifactSummary = value })));
        Group(rows, "头像与名称",
            ToggleRow("头像", null, s => s.ShowAvatars, value => vm.Change(s => s with { ShowAvatars = value })),
            ToggleRow("用户名称", null, s => s.ShowUserNames, value => vm.Change(s => s with { ShowUserNames = value })),
            ToggleRow("助手名称", null, s => s.ShowAssistantNames, value => vm.Change(s => s with { ShowAssistantNames = value })),
            ToggleRow("消息时间", "未知时间不显示占位值", s => s.ShowMessageTimes, value => vm.Change(s => s with { ShowMessageTimes = value })));
        Group(rows, "输入区",
            ChoiceRow(vm, "输入区宽度", "跟随阅读宽度时与正文同宽；沉浸式会收起输入区，只在底部留一个上箭头按钮", new[] { "全宽", "跟随阅读宽度", "沉浸式" }, s => s.Composer, (s, v) => s with { Composer = v }, "Edit", "wide", "reading", "hidden"));
    }

    private void RenderNames(WorkbenchCustomizationViewModel vm, StackPanel rows)
    {
        Group(rows, "显示名称",
            TextRow(vm, "用户称呼", "显示在你的消息旁", s => s.UserName, (s, v) => s with { UserName = v }),
            TextRow(vm, "助手名称", "显示在助手消息旁", s => s.AssistantName, (s, v) => s with { AssistantName = v }));
        Group(rows, "会话命名",
            ChoiceRow(vm, "自动取名", "有第一条消息后自动命名；你自己改的名字始终优先。子代理未安装时自动退回截取首条 prompt。",
                new[] { "子代理取名", "截取首条消息" }, s => s.SessionNaming,
                (s, v) => s with { SessionNaming = v }, "Edit", "subagent", "mechanical"),
            ToggleRow("把名称偏好发送给模型", "让模型知道如何称呼你", s => s.UseNamesInPrompt, value => vm.Change(s => s with { UseNamesInPrompt = value })));
    }

    private void RenderFloating(WorkbenchCustomizationViewModel vm, StackPanel rows)
    {
        if (_delivery is not { } delivery)
        {
            Group(rows, "通知提示", SettingsLayout.Row("通知提示尚未就绪", "会话建立后可用", null));
            return;
        }
        Group(rows, "提醒渠道",
            HostToggleRow("应用内提示", "短时浮层提示", () => delivery.DeliversToToast, value => delivery.DeliversToToast = value),
            HostToggleRow("通知页提示", "显示最新提醒和页签未读标记；页面可随辅助栏浮动", () => delivery.DeliversToPage, value => delivery.DeliversToPage = value),
            HostToggleRow("Windows 系统通知", delivery.WindowsDeliveryStatus + "；失败自动回退应用内提示", () => delivery.DeliversToWindows, value => delivery.DeliversToWindows = value));
        rows.Children.Add(SettingsLayout.Note(delivery.DeliverySummary));
        if (delivery.SaveError is { } error) rows.Children.Add(SettingsLayout.Note("提醒设置未保存：" + error));
    }

    private void RenderBrowserSettings(WorkbenchCustomizationViewModel vm, StackPanel rows)
    {
        var browser = _browserSettings;
        if (browser is null)
        {
            Group(rows, "浏览器", SettingsLayout.Row("浏览器设置尚未就绪", "会话建立后可用", null));
            return;
        }

        // ── 顶部卡片：让智能体控制内置浏览器（主开关） ──
        Group(rows, "",
            HostToggleRow("让智能体控制内置浏览器",
                "允许 AI 会话中的模型直接驱动内置浏览器浏览、分析网页并提取数据；关闭后浏览器仍可手动使用。",
                () => browser.BrowserUseEnabled, value => browser.BrowserUseEnabled = value, "Globe"));

        // ── 常规 ──
        Group(rows, "常规",
            HostChoiceRow("网页链接打开位置", "聊天与消息中的网页链接默认在何处打开",
                new[] { "内置浏览器", "系统默认浏览器" }, () => browser.WebUrlOpenTargetIndex, v => browser.WebUrlOpenTargetIndex = v, "Globe"),
            HostChoiceRow("本地文件打开位置", "本地 HTML 与项目预览默认在何处打开",
                new[] { "内置浏览器", "系统默认浏览器" }, () => browser.LocalUrlOpenTargetIndex, v => browser.LocalUrlOpenTargetIndex = v, "Document"),
            HostToggleRow("显示完整网址", "在地址栏中显示完整协议、路径与查询参数；关闭时只显示主机名",
                () => browser.ShowFullUrl, value => browser.ShowFullUrl = value, "Search"),
            HostChoiceRow("截图策略", "向模型提供网页上下文时的截图策略",
                new[] { "始终包含", "询问", "从不" }, () => browser.ScreenshotModeIndex, v => browser.ScreenshotModeIndex = v, "Document"),
            HostTextFieldRow("搜索地址模板", "地址栏输入普通文字时使用的搜索 URL；{0} 会被替换为搜索词",
                () => browser.SearchUrlTemplate, v => browser.SearchUrlTemplate = v),
            HostTextFieldRow("页面 User-Agent", "留空使用 WebView2 默认值；填写后所有页签立即生效",
                () => browser.UserAgent, v => browser.UserAgent = v),
            HostToggleRow("表单自动填充", "允许 WebView2 内置的姓名、地址、电话等自动填充",
                () => browser.EnableAutofill, value => browser.EnableAutofill = value, "Person"),
            HostToggleRow("密码自动保存", "允许 WebView2 内置密码管理器保存站点密码",
                () => browser.EnablePasswordAutosave, value => browser.EnablePasswordAutosave = value, "Key"),
            HostToggleRow("标注时附带区域截图", "关闭后只发送网页地址、元素与文字说明",
                () => browser.AnnotationIncludeScreenshot, value => browser.AnnotationIncludeScreenshot = value, "ArrowDownload"),
            BuildBrowserClearRow(browser),
            BuildBrowserHistoryRow(browser));

        // ── 下载 ──
        Group(rows, "下载",
            HostTextFieldRow("默认下载位置", "留空时沿用 WebView2 默认下载目录；输入绝对路径即写入配置",
                () => browser.DefaultDownloadDirectory, v => browser.DefaultDownloadDirectory = v),
            HostToggleRow("下载前询问保存位置", "对内置浏览器发起的下载弹出文件保存对话框",
                () => browser.PromptDownloadLocation, value => browser.PromptDownloadLocation = value, "ArrowDownload"),
            HostToggleRow("记录下载历史", "把真实发生的下载写入 browser-downloads.json",
                () => browser.DownloadHistoryEnabled, value => browser.DownloadHistoryEnabled = value, "History"),
            BuildBrowserDownloadHistoryRow(browser));

        // ── 权限与智能体控制 ──
        Group(rows, "权限与智能体控制",
            HostToggleRow("启用站点工具（WebMCP）", "允许智能体发现并调用公开网页的扩展能力与 WebMCP",
                () => browser.EnableSiteTools, value => browser.EnableSiteTools = value, "PuzzlePiece"),
            HostChoiceRow("智能体浏览权限", "智能体访问外部站点时的默认授权策略",
                new[] { "需要批准", "始终允许", "禁止访问" }, () => browser.DefaultBrowsePermissionIndex, v => browser.DefaultBrowsePermissionIndex = v, "ShieldCheckmark"),
            HostChoiceRow("智能体下载权限", "智能体通过网页下载外部文件时的默认权限",
                new[] { "需要批准", "始终允许", "禁止下载" }, () => browser.DefaultDownloadPermissionIndex, v => browser.DefaultDownloadPermissionIndex = v, "ArrowDownload"),
            BuildBrowserSitesRow(browser),
            SettingsLayout.Note("浏览器扩展目录：" + browser.ExtensionsDirectory +
                "（放入含 manifest.json 的未打包扩展，重开浏览器页后加载）"));

        // ── 开发者模式 ──
        Group(rows, "开发者模式",
            BuildCdpRow(vm),
            SettingsLayout.Note("完整 CDP 访问权限允许智能体调用任意 Chrome DevTools Protocol 方法，可读写页面 DOM、网络与存储；仅在信任当前页面与模型时开启。"));

        // ── 设备自带浏览器扩展 ──
        Group(rows, "设备自带浏览器扩展",
            HostToggleRow("启用外部浏览器控制桥", "启动本机回环服务，允许 Chrome/Edge 扩展把页面引用发到对话并接受智能体控制",
                () => browser.ExternalBrowserEnabled, value => browser.ExternalBrowserEnabled = value, "PuzzlePiece"),
            HostTextFieldRow("扩展端口", "扩展连接的本机端口；被占用时实际端口见下方状态",
                () => browser.ExternalBrowserPort.ToString(), value =>
                {
                    if (int.TryParse(value, out var port)) browser.ExternalBrowserPort = port;
                }),
            HostTextFieldRow("配对 PIN", "4 位数字；写入扩展目录的 config.json，并在扩展弹窗中用于配对",
                () => browser.ExternalBrowserPin, value => browser.ExternalBrowserPin = value),
            BuildExternalBrowserStatusRow(browser));

        if (browser.HasLoadError)
            rows.Children.Add(SettingsLayout.Note("browser-settings.json 加载失败：" + browser.LoadError + "；已用默认值，原文件未覆盖。", warning: true));
        rows.Children.Add(SettingsLayout.Note(browser.Status));
        rows.Children.Add(SettingsLayout.Note("配置文件：" + browser.SettingsPath));
    }

    /// <summary>CDP 行：开关取真源 browser-settings.json；插件未装时禁用并注明原因，不渲染假开关。</summary>
    private Control BuildCdpRow(WorkbenchCustomizationViewModel vm)
    {
        var browser = _browserSettings;
        var toggle = new ToggleSwitch
        {
            OnContent = "", OffContent = "", VerticalAlignment = VerticalAlignment.Center, IsEnabled = false,
        };
        AutomationProperties.SetAutomationId(toggle, "settings-browser-cdp-toggle");
        var applying = false;
        toggle.IsCheckedChanged += (_, _) =>
        {
            if (_refreshing || applying || !toggle.IsEnabled) return;
            if (browser is null) return;
            applying = true;
            try { browser.EnableFullCdpAccess = toggle.IsChecked == true; }
            finally { applying = false; }
        };

        var state = new TextBlock { VerticalAlignment = VerticalAlignment.Center, MinWidth = 20 };
        state.Classes.Add("settingsRowDesc");
        var note = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0),
        };
        note.Classes.Add("settingsRowDesc");
        AutomationProperties.SetAutomationId(note, "settings-browser-cdp-note");

        void Sync()
        {
            var enabled = PluginAccess.AdvancedControlEnabled(vm);
            toggle.IsEnabled = enabled;
            applying = true;
            try { toggle.IsChecked = enabled && (browser?.EnableFullCdpAccess == true); }
            finally { applying = false; }
            state.Text = enabled ? (browser?.EnableFullCdpAccess == true ? "开" : "关") : "不可用";
            note.Text = PluginAccess.Status(vm);
            note.IsVisible = !enabled;
        }

        _bindings.Add(_ => Sync());
        Sync();

        var host = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        host.Children.Add(state);
        host.Children.Add(toggle);
        var layout = new StackPanel();
        layout.Children.Add(SettingsLayout.Row("启用完整 CDP 访问权限 (Chrome DevTools Protocol)",
            "允许 Agent 通过 CDP 深入探测并调试浏览器内部 DOM、网络与性能", host));
        layout.Children.Add(note);
        return layout;
    }

    /// <summary>
    /// 功能入口分类：不伪造设置行，只显示宿主真实状态，并把用户送到真正能操作它的地方。
    /// 状态与动作都来自宿主注入的 <see cref="WorkbenchCustomizationViewModel.FeatureStatus"/>。
    /// </summary>
    private void RenderFeatureStatus(WorkbenchCustomizationViewModel vm, StackPanel rows)
    {
        var status = vm.StatusOf(Page);
        var badge = new Border { VerticalAlignment = VerticalAlignment.Center };
        badge.Classes.Add("badge");
        badge.Child = new TextBlock { Text = status.State, Classes = { "caption" } };

        Group(rows, "运行状态",
            SettingsLayout.Row("当前状态", status.Detail, badge, IconForFeature(Page)));

        if (!string.IsNullOrWhiteSpace(status.ActionLabel))
        {
            Group(rows, "打开功能",
                SettingsLayout.ChevronRow(status.ActionLabel!,
                    "执行真实入口；未安装或未启用时由宿主给出原因。",
                    () =>
                    {
                        if (status.ActionId is { Length: > 0 } action) ActionRequested?.Invoke(action);
                        else if (status.PageId is { Length: > 0 } page) PageRequested?.Invoke(page);
                    },
                    IconForFeature(Page)));
        }

        Group(rows, "配置位置",
            SettingsLayout.ChevronRow("安装与启用",
                "在插件市场管理插件形态的功能；内建工作台页面在“工作台模块”中启停。",
                () => PageRequested?.Invoke("marketplace"), "PuzzlePiece"),
            SettingsLayout.ChevronRow("工作台模块",
                "查看内建页面的启停状态与保存结果。",
                () => PageRequested?.Invoke("modules"), "AppsList"));
    }

    private static string IconForFeature(string pageId) => pageId switch
    {
        "terminal" => "Terminal",
        "files" => "Folder",
        "automation" => "Automation",
        "usage" => "History",
        _ => "Info",
    };

    private void RenderConfig(WorkbenchCustomizationViewModel vm, StackPanel rows)
    {
        Group(rows, "",
            SettingsLayout.Row("当前范围", vm.ScopePath, TextButton("切换范围", vm.HasProject ? (vm.ProjectScope ? "改到全局" : "改到此项目覆盖") : "无项目", () => vm.ProjectScope = !vm.ProjectScope), "Folder"),
            SettingsLayout.Row("撤销与重做", "每次修改可回退一步", IconButtons(
                ("ArrowUndo", "撤销 UI 配置", vm.Undo), ("ArrowRedo", "重做 UI 配置", vm.Redo))),
            SettingsLayout.Row("导入与导出", "配置文件为 JSON，可备份或迁移", IconButtons(
                ("ArrowUpload", "导入配置 JSON 文件", () => _ = ImportAsync(vm)),
                ("ArrowDownload", "导出配置 JSON 文件", () => _ = ExportAsync(vm)),
                ("ArrowClockwise", "放弃改动并从文件重新加载", vm.Reload))),
            SettingsLayout.Row("重置", "恢复布局只还原位置与显隐；恢复全部默认设置同时重置颜色与名称", ResetControls(vm)));

        var json = new TextBox
        {
            AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, MinHeight = 240, MaxHeight = 420,
            FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace"),
            HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 8, 0, 8)
        };
        json.Bind(TextBox.TextProperty, new Avalonia.Data.Binding(nameof(vm.Json)) { Mode = Avalonia.Data.BindingMode.TwoWay });
        rows.Children.Add(SettingsLayout.GroupHeader("JSON"));
        var apply = new Button { Content = "应用 JSON", HorizontalAlignment = HorizontalAlignment.Left };
        apply.Click += (_, _) => vm.ApplyJson();
        var copy = new Button { Content = "复制", Margin = new Thickness(8, 0, 0, 0) };
        copy.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
                await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, vm.Json);
        };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8), Children = { apply, copy } };
        var card = new Border { Child = new StackPanel { Children = { json, actions } } };
        card.Classes.Add("settingsPanel");
        rows.Children.Add(card);
    }

    // ================= 行构件 =================

    private bool _firstGroup = true;

    private void Group(StackPanel rows, string title, params Control[] groupRows)
    {
        if (groupRows.Length == 0) return;
        // 组标题也是搜索语义词：搜索“消息”“下载”等应保留该组下可解释的行。
        if (!string.IsNullOrEmpty(title))
        {
            foreach (var row in groupRows)
            {
                var terms = SettingsLayout.GetSearchTerms(row);
                SettingsLayout.SetSearchTerms(row, string.Join(" ", new[] { terms, title }.Where(value => !string.IsNullOrWhiteSpace(value))));
            }
        }
        // 即使首组没有标题，也消费掉 first：后续第一个有标题的组不应再被当成页首组，
        // 否则会出现 0px 与 32px 组间隔混用的“页面不齐”。
        if (!string.IsNullOrEmpty(title))
            rows.Children.Add(SettingsLayout.GroupHeader(title, _firstGroup));
        _firstGroup = false;
        rows.Children.Add(SettingsLayout.Group(groupRows));
    }

    /// <summary>下拉行：改动写入外观配置。icon 为行首图标键，null 表示不显示。</summary>
    private Control ChoiceRow(WorkbenchCustomizationViewModel vm, string label, string? description, string[] labels,
        Func<WorkbenchAppearance, string> get, Func<WorkbenchAppearance, string, WorkbenchAppearance> set,
        string? icon, params string[] values)
        => BuildChoice(label, description, labels, get, value => vm.Change(current => set(current, value)), icon, values);

    /// <summary>下拉行：改动由调用方处理（页面位置、预设等）。</summary>
    private Control CustomChoiceRow(string label, string? description, string[] labels,
        Func<WorkbenchAppearance, string> get, Action<string> apply, string? icon, params string[] values)
        => BuildChoice(label, description, labels, get, apply, icon, values);

    private Control BuildChoice(string label, string? description, string[] labels,
        Func<WorkbenchAppearance, string> get, Action<string> apply, string? icon, string[] values)
    {
        var combo = new ComboBox { ItemsSource = labels, MinWidth = 150, MaxWidth = 220, VerticalAlignment = VerticalAlignment.Center };
        var applying = false;
        combo.SelectionChanged += (_, _) =>
        {
            if (_refreshing || applying || combo.SelectedIndex < 0) return;
            applying = true;
            try { apply(values[combo.SelectedIndex]); }
            finally { applying = false; }
        };
        _bindings.Add(current =>
        {
            var index = Math.Max(0, Array.IndexOf(values, get(current)));
            applying = true;
            try { if (combo.SelectedIndex != index) combo.SelectedIndex = index; }
            finally { applying = false; }
        });
        return SettingsLayout.Row(label, description, combo, icon);
    }

    private Control ToggleRow(string label, string? description, Func<WorkbenchAppearance, bool> get, Action<bool> set,
        Func<bool>? getOverride = null, string? tooltip = null)
    {
        var control = new ToggleSwitch { OnContent = "", OffContent = "", VerticalAlignment = VerticalAlignment.Center };
        if (tooltip is not null) ToolTip.SetTip(control, tooltip);
        var applying = false;
        control.IsCheckedChanged += (_, _) =>
        {
            if (_refreshing || applying) return;
            applying = true;
            try { set(control.IsChecked == true); }
            finally { applying = false; }
        };
        _bindings.Add(current =>
        {
            applying = true;
            try { control.IsChecked = getOverride?.Invoke() ?? get(current); }
            finally { applying = false; }
        });
        return SettingsLayout.ToggleRow(label, description, control);
    }

    private Control TextRow(WorkbenchCustomizationViewModel vm, string label, string? description,
        Func<WorkbenchAppearance, string> get, Func<WorkbenchAppearance, string, WorkbenchAppearance> set)
    {
        var input = new TextBox { MaxLength = 80, Width = 180, MaxWidth = 220, VerticalAlignment = VerticalAlignment.Center };
        input.KeyDown += (_, e) => { if (e.Key != Key.Enter) return; Apply(); e.Handled = true; };
        input.LostFocus += (_, _) => Apply();
        void Apply()
        {
            var value = (input.Text ?? "").Trim();
            if (string.IsNullOrEmpty(value) || string.Equals(value, get(vm.Current), StringComparison.Ordinal)) return;
            vm.Change(current => set(current, value));
        }
        _bindings.Add(current => { if (!input.IsFocused && input.Text != get(current)) input.Text = get(current); });
        return SettingsLayout.Row(label, description, input);
    }

    private Control ColorPickerRow(WorkbenchCustomizationViewModel vm, string title, string description,
        Func<WorkbenchAppearance, string> get, Func<WorkbenchAppearance, string, WorkbenchAppearance> set)
    {
        var picker = new ColorPicker { IsAlphaEnabled = false, VerticalAlignment = VerticalAlignment.Center };
        picker.ColorChanged += (_, _) =>
        {
            if (_refreshing) return;
            var color = Hex(picker.Color);
            if (color.Equals(get(vm.Current), StringComparison.OrdinalIgnoreCase)) return;
            vm.Change(s => set(s, color));
        };
        _bindings.Add(current => { var color = Color.Parse(get(current)); if (picker.Color != color) picker.Color = color; });

        var custom = new TextBox { Width = 110, MaxWidth = 140, PlaceholderText = "#RRGGBB", VerticalAlignment = VerticalAlignment.Center };
        custom.KeyDown += (_, e) => { if (e.Key != Key.Enter) return; Commit(); e.Handled = true; };
        custom.LostFocus += (_, _) => Commit();
        _bindings.Add(current => { if (!custom.IsFocused) custom.Text = get(current); });

        void Commit()
        {
            var text = custom.Text?.Trim();
            if (string.IsNullOrEmpty(text)) { custom.Text = get(vm.Current); return; }
            var value = text.StartsWith('#') ? text : "#" + text;
            if (value.Length != 7 || !value.Skip(1).All(Uri.IsHexDigit))
            {
                vm.Status = "颜色格式应为 #RRGGBB，例如 #0078D4";
                return;
            }
            var normalized = value.ToUpperInvariant();
            custom.Text = normalized;
            if (normalized.Equals(get(vm.Current), StringComparison.OrdinalIgnoreCase)) return;
            vm.Change(s => set(s, normalized));
        }

        var host = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { custom, picker } };
        return SettingsLayout.Row(title, description, host);
    }

    private Control EmphasisPickerRow(WorkbenchCustomizationViewModel vm) =>
        ColorPickerRow(vm,
            "强调色",
            "只用于模型输出中的 Markdown 粗体/标记，默认黄色；格式 #RRGGBB",
            s => s.Emphasis,
            (s, value) => s with { Emphasis = value });

    private Control PlacementRow(WorkbenchCustomizationViewModel vm, AppActionDefinition action)
    {
        var positions = Enum.GetValues<UiActionPosition>();
        var titles = positions.Select(WorkbenchActionDefaults.PositionTitle).ToArray();
        var selector = new ComboBox { ItemsSource = titles, MinWidth = 125, MaxWidth = 180, VerticalAlignment = VerticalAlignment.Center };
        var order = new NumericUpDown { Minimum = -1000, Maximum = 1000, Width = 80, FormatString = "0", VerticalAlignment = VerticalAlignment.Center };
        var applying = false;
        void Apply()
        {
            if (_refreshing || applying || selector.SelectedIndex < 0) return;
            applying = true;
            try { vm.SetPlacement(action.Id, positions[selector.SelectedIndex], (int)(order.Value ?? 0)); }
            finally { applying = false; }
        }
        selector.SelectionChanged += (_, _) => Apply();
        order.ValueChanged += (_, _) => Apply();
        _bindings.Add(current =>
        {
            var placement = current.Actions.GetValueOrDefault(action.Id) ?? WorkbenchActionDefaults.Placement(action.Id);
            applying = true;
            try
            {
                var index = Array.IndexOf(positions, placement.Position);
                if (selector.SelectedIndex != index) selector.SelectedIndex = index;
                if ((int)(order.Value ?? 0) != placement.Order) order.Value = placement.Order;
            }
            finally { applying = false; }
        });
        var host = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { selector, order } };
        ToolTip.SetTip(host, action.Description);
        return SettingsLayout.Row(action.Title, action.Description, host, WorkbenchActionDefaults.Icon(action.Id));
    }

    /// <summary>动作行：标题说明在左，按钮在右（按钮文字与标题不重复）。</summary>
    private Control ActionRow(string label, string? description, string buttonText, Action action, string automationId)
    {
        var button = new Button { Content = buttonText, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAutomationId(button, automationId);
        button.Click += (_, _) => action();
        return SettingsLayout.Row(label, description, button);
    }

    // ================= 浏览器设置行（BRW-1：真源为 BrowserSettingsViewModel） =================

    private Control HostToggleRow(string label, string? description, Func<bool> get, Action<bool> set, string? icon = null)
    {
        var control = new ToggleSwitch { OnContent = "", OffContent = "", VerticalAlignment = VerticalAlignment.Center };
        var applying = false;
        control.IsCheckedChanged += (_, _) =>
        {
            if (_refreshing || applying) return;
            applying = true;
            try { set(control.IsChecked == true); }
            finally { applying = false; }
        };
        _bindings.Add(_ =>
        {
            applying = true;
            try { control.IsChecked = get(); }
            finally { applying = false; }
        });
        return SettingsLayout.ToggleRow(label, description, control, icon);
    }

    private Control HostChoiceRow(string label, string? description, string[] labels,
        Func<int> get, Action<int> set, string? icon)
    {
        var combo = new ComboBox { ItemsSource = labels, MinWidth = 160, MaxWidth = 220, VerticalAlignment = VerticalAlignment.Center };
        var applying = false;
        combo.SelectionChanged += (_, _) =>
        {
            if (_refreshing || applying || combo.SelectedIndex < 0) return;
            applying = true;
            try { set(combo.SelectedIndex); }
            finally { applying = false; }
        };
        _bindings.Add(_ =>
        {
            var index = Math.Clamp(get(), 0, labels.Length - 1);
            applying = true;
            try { if (combo.SelectedIndex != index) combo.SelectedIndex = index; }
            finally { applying = false; }
        });
        return SettingsLayout.Row(label, description, combo, icon);
    }

    private Control HostTextFieldRow(string label, string? description, Func<string> get, Action<string> set, int width = 220)
    {
        var input = new TextBox { Width = width, MaxWidth = 320, VerticalAlignment = VerticalAlignment.Center };
        input.KeyDown += (_, e) => { if (e.Key != Key.Enter) return; Commit(); e.Handled = true; };
        input.LostFocus += (_, _) => Commit();
        void Commit()
        {
            var value = (input.Text ?? "").Trim();
            if (string.Equals(value, get(), StringComparison.Ordinal)) return;
            set(value);
        }
        _bindings.Add(_ => { if (!input.IsFocused && input.Text != get()) input.Text = get(); });
        return SettingsLayout.Row(label, description, input);
    }

    /// <summary>清除浏览数据：每个范围一个真实按钮，结果来自实际删除报告。</summary>
    private Control BuildBrowserClearRow(ViewModels.BrowserSettingsViewModel browser)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        status.Classes.Add("settingsRowDesc");
        AutomationProperties.SetAutomationId(status, "browser-clear-status");
        void UpdateStatus()
        {
            status.Text = browser.ClearDataStatus;
            status.IsVisible = browser.HasClearDataStatus;
        }
        void Add(string text, Action action, string automationId)
        {
            var button = new Button { Content = text, Padding = new Thickness(10, 4), VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetAutomationId(button, automationId);
            button.Click += (_, _) => { action(); UpdateStatus(); };
            panel.Children.Add(button);
        }
        Add("缓存", browser.ClearCache, "browser-clear-cache");
        Add("Cookie", browser.ClearCookies, "browser-clear-cookies");
        Add("网站数据", browser.ClearSiteData, "browser-clear-sitedata");
        Add("全部", browser.ClearAll, "browser-clear-all");
        _bindings.Add(_ => UpdateStatus());
        UpdateStatus();

        var layout = new StackPanel();
        layout.Children.Add(SettingsLayout.Row("清除浏览数据", "清除内置浏览器缓存、Cookie 与 LocalStorage 网站数据；被页面占用的文件会如实报告失败", panel, "History"));
        layout.Children.Add(status);
        return layout;
    }

    /// <summary>浏览历史管理：真实读取下载历史文件，提供刷新与清理已失效条目。</summary>
    private Control BuildBrowserHistoryRow(ViewModels.BrowserSettingsViewModel browser)
    {
        var summary = new TextBlock { TextWrapping = TextWrapping.Wrap };
        summary.Classes.Add("settingsRowDesc");
        AutomationProperties.SetAutomationId(summary, "browser-history-status");

        var host = new StackPanel { Spacing = 4 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        var refresh = new Button { Content = "刷新", Padding = new Thickness(10, 4) };
        AutomationProperties.SetAutomationId(refresh, "browser-history-refresh");
        refresh.Click += (_, _) => { browser.RefreshDownloadHistory(); Update(); };
        var forget = new Button { Content = "清理失效条目", Padding = new Thickness(10, 4) };
        AutomationProperties.SetAutomationId(forget, "browser-history-prune");
        forget.Click += (_, _) => { browser.RefreshDownloadHistory(); Update(); };
        buttons.Children.Add(refresh); buttons.Children.Add(forget);
        host.Children.Add(buttons);
        host.Children.Add(summary);

        void Update() => summary.Text = browser.DownloadHistoryStatus;
        _bindings.Add(_ => Update());
        Update();
        return SettingsLayout.Row("浏览历史管理", "真实读取 browser-downloads.json；删除条目不删除磁盘文件", host, "History");
    }

    private Control BuildBrowserSitesRow(ViewModels.BrowserSettingsViewModel browser)
    {
        var editor = new TextBox
        {
            AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, MinHeight = 64, MaxHeight = 140,
            Width = 260, MaxWidth = 320, VerticalAlignment = VerticalAlignment.Center,
            PlaceholderText = "example.com = approval",
        };
        AutomationProperties.SetAutomationId(editor, "browser-site-allowlist");
        editor.LostFocus += (_, _) => Commit();
        editor.KeyDown += (_, e) => { if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return; Commit(); e.Handled = true; };
        void Commit()
        {
            var value = editor.Text ?? "";
            if (string.Equals(value, browser.AllowedSitesText, StringComparison.Ordinal)) return;
            browser.AllowedSitesText = value;
        }
        _bindings.Add(_ => { if (!editor.IsFocused) editor.Text = browser.AllowedSitesText; });
        return SettingsLayout.Row("智能体网站白名单与权限例外",
            "每行一个站点，格式“主机名 = approval|allow|block”；留空表示全部站点沿用全局默认权限", editor, "ShieldCheckmark");
    }

    /// <summary>外部浏览器扩展状态：显示真实运行端口、连接状态与扩展目录，可直接打开目录。</summary>
    private Control BuildExternalBrowserStatusRow(ViewModels.BrowserSettingsViewModel browser)
    {
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        status.Classes.Add("settingsRowDesc");
        var path = new TextBlock { TextWrapping = TextWrapping.Wrap };
        path.Classes.Add("settingsRowDesc");
        var open = new Button { Content = "打开扩展目录" };
        AutomationProperties.SetAutomationId(open, "external-browser-open-extension");
        open.Click += (_, _) =>
        {
            var directory = browser.ExternalBrowserExtensionPath;
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(directory) { UseShellExecute = true }); }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        };
        var copy = new Button { Content = "复制配对信息" };
        copy.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
            var text = $"Fufucode 外部浏览器扩展\n端口：{browser.ExternalBrowserActualPort}\nPIN：{browser.ExternalBrowserPin}\n扩展目录：{browser.ExternalBrowserExtensionPath}";
            try { await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, text); }
            catch (Exception error) when (error is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(open);
        buttons.Children.Add(copy);
        var panel = new StackPanel { Spacing = 6, MinWidth = 260 };
        panel.Children.Add(status);
        panel.Children.Add(path);
        panel.Children.Add(buttons);

        void Sync()
        {
            status.Text = browser.ExternalBrowserStatus;
            path.Text = "扩展目录：" + (string.IsNullOrWhiteSpace(browser.ExternalBrowserExtensionPath)
                ? "（由应用启动后生成）"
                : browser.ExternalBrowserExtensionPath);
            open.IsEnabled = !string.IsNullOrWhiteSpace(browser.ExternalBrowserExtensionPath) &&
                             Directory.Exists(browser.ExternalBrowserExtensionPath);
        }
        browser.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(browser.ExternalBrowserStatus) or nameof(browser.ExternalBrowserConnected) or
                nameof(browser.ExternalBrowserActualPort) or nameof(browser.ExternalBrowserExtensionPath))
                Sync();
        };
        Sync();
        return SettingsLayout.Row("扩展连接状态",
            "在 Chrome/Edge 的扩展页选择“加载已解压的扩展程序”，指向上面的目录；扩展会自动读取 config.json 并配对",
            panel, "PuzzlePiece");
    }

    /// <summary>下载历史行：真实读取 browser-downloads.json。</summary>
    private Control BuildBrowserDownloadHistoryRow(ViewModels.BrowserSettingsViewModel browser)
    {
        var count = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        count.Classes.Add("settingsRowDesc");
        AutomationProperties.SetAutomationId(count, "browser-download-history");
        _bindings.Add(_ =>
        {
            browser.RefreshDownloadHistory();
            count.Text = browser.DownloadHistoryStatus;
        });
        return SettingsLayout.Row("下载历史记录", "记录真实发生的下载；只保存文件名与路径，不保存内容", count, "ArrowDownload");
    }

    private Control ResetControls(WorkbenchCustomizationViewModel vm)
    {
        var resetScope = new ComboBox
        {
            MinWidth = 150, Margin = new Thickness(0, 0, 8, 0), SelectedIndex = -1,
            PlaceholderText = "选择范围", VerticalAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(resetScope, "「恢复布局」只还原位置与显隐；「恢复全部默认设置」同时重置颜色、名称与密度");
        resetScope.Items.Add(new ComboBoxItem { Content = "仅恢复布局" });
        resetScope.Items.Add(new ComboBoxItem { Content = "恢复全部默认设置" });
        var reset = new Button { Content = "执行", MinWidth = 72, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAutomationId(reset, "settings-reset-apply");
        resetScope.SelectionChanged += (_, _) => reset.Content = resetScope.SelectedIndex switch { 1 => "恢复全部", 0 => "恢复布局", _ => "执行" };
        reset.Click += async (_, _) =>
        {
            if (resetScope.SelectedIndex == 0) { vm.ResetLayout(); vm.Status = "已恢复默认布局，保留颜色与名称"; return; }
            if (resetScope.SelectedIndex != 1) { vm.Status = "请先选择重置范围：仅恢复布局，或恢复全部默认设置"; return; }
            await ConfirmResetAsync(vm, reset);
        };
        var host = new StackPanel { Orientation = Orientation.Horizontal, Children = { resetScope, reset } };
        return host;
    }

    private static Control IconButtons(params (string Icon, string Title, Action Action)[] buttons)
    {
        var host = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        foreach (var (icon, title, action) in buttons)
        {
            var button = WorkbenchUi.IconButton(icon, title);
            button.Click += (_, _) => action();
            host.Children.Add(button);
        }
        return host;
    }

    private static Button TextButton(string title, string content, Action action)
    {
        var button = new Button { Content = content, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(button, title);
        button.Click += (_, _) => action();
        return button;
    }

    private static void MovePage(WorkbenchCustomizationViewModel vm, string pageId, string value)
        => vm.EditLayout(l =>
        {
            if (value == "3") { l.PinnedPages.Remove(pageId); l.Close(pageId); }
            else l.Move(pageId, (DockZone)int.Parse(value));
        });

    private static string Hex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    private static string PresetOf(WorkbenchAppearance s) =>
        s.Density == "compact" && s.Radius == "none" ? "compact"
        : s.Density == "airy" && s.ReadingWidth == "narrow" ? "reading" : "standard";

    private async Task ConfirmResetAsync(WorkbenchCustomizationViewModel vm, Control returnFocus)
    {
        var yes = new Button { Content = "恢复全部默认设置", Classes = { "accent" } };
        var no = new Button { Content = "取消" };
        AutomationProperties.SetAutomationId(no, "settings-reset-cancel");
        var dialog = new Window
        {
            Title = "恢复全部默认设置", Width = 460, Height = 200, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(20), Spacing = 16,
                Children =
                {
                    new TextBlock { Text = "将同时重置颜色、名称、密度、圆角和布局。此操作可以用「撤销 UI 配置」回退一步。", TextWrapping = TextWrapping.Wrap },
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right, Children = { no, yes } }
                }
            }
        };
        dialog.Opened += (_, _) => no.Focus();
        no.Click += (_, _) => { vm.Status = "已取消，未改动设置"; dialog.Close(); };
        yes.Click += (_, _) => { vm.Reset(); vm.Status = "已恢复全部默认设置"; dialog.Close(); };
        dialog.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            vm.Status = "已取消，未改动设置"; dialog.Close(); e.Handled = true;
        };
        try
        {
            if (TopLevel.GetTopLevel(this) is Window parent) await Fufucode.App.Views.SkinDialog.ShowAsync(dialog, parent);
            else dialog.Show();
        }
        catch (InvalidOperationException) { return; }
        returnFocus.Focus();
    }

    private async Task ImportAsync(WorkbenchCustomizationViewModel vm)
    {
        try
        {
            var top = TopLevel.GetTopLevel(this); if (top is null) return;
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "导入 UI 配置", FileTypeFilter = [new("JSON") { Patterns = ["*.json"] }] });
            if (files.Count == 0) return;
            using var stream = await files[0].OpenReadAsync(); using var reader = new System.IO.StreamReader(stream);
            vm.Json = await reader.ReadToEndAsync(); vm.ApplyJson();
        }
        catch (Exception error) { vm.Json = ""; WorkbenchErrors.Report(this, error); }
    }
    private async Task ExportAsync(WorkbenchCustomizationViewModel vm)
    {
        try
        {
            var top = TopLevel.GetTopLevel(this); if (top is null) return;
            var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = "导出 UI 配置", SuggestedFileName = "fufucode-ui.json", DefaultExtension = "json" });
            if (file is null) return;
            using var stream = await file.OpenWriteAsync(); stream.SetLength(0); using var writer = new System.IO.StreamWriter(stream);
            await writer.WriteAsync(vm.Json);
        }
        catch (Exception error) { WorkbenchErrors.Report(this, error); }
    }
}

internal static class WorkbenchErrors
{
    internal static void Report(Control control, Exception error)
    {
        if (TopLevel.GetTopLevel(control)?.DataContext is MainViewModel vm) vm.Timeline.AddNotification(error.GetBaseException().Message);
    }
}
