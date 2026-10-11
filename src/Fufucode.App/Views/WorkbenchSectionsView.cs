using System;
using System.Collections.Generic;
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
using Fufucode.App.Infrastructure;
using Fufucode.App.ViewModels;

namespace Fufucode.App.Views;

/// <summary>
/// 设置页各级页面的内容区。按 Windows 设置的语言组织：分组标题 + 一张分组卡，
/// 卡内每行是一条设置（标题、说明、右侧控件），需要下钻的行显示进入箭头。
/// 页面 id 与 <see cref="SettingsPageViewModel.SubPages"/> 对应；切换页面时重建，
/// 同一页面内配置变化只刷新取值，不重建控件树。
/// </summary>
public sealed class WorkbenchSectionsView : UserControl
{
    private bool _refreshing;
    private WorkbenchCustomizationViewModel? _vm;
    private FloatingPanelViewModel? _floating;
    private StackPanel? _rows;
    private string? _builtPage;
    private readonly List<Action<WorkbenchAppearance>> _bindings = new();

    public static readonly StyledProperty<string> PageProperty =
        AvaloniaProperty.Register<WorkbenchSectionsView, string>(nameof(Page), "appearance");
    public static readonly StyledProperty<bool> SearchActiveProperty =
        AvaloniaProperty.Register<WorkbenchSectionsView, bool>(nameof(SearchActive));

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
    public string SearchText { get; set; } = "";

    /// <summary>由设置页注入浮窗 VM；未注入时功能页只显示说明。</summary>
    public void AttachFloating(FloatingPanelViewModel? floating)
    {
        _floating = floating;
        if (floating is not null)
        {
            floating.HostChanged += () => Dispatcher.UIThread.Post(Refresh);
            floating.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(FloatingPanelViewModel.WindowsDeliveryStatus)) Dispatcher.UIThread.Post(Refresh);
            };
        }
        if (Page.StartsWith("functions", StringComparison.Ordinal)) Render();
    }

    /// <summary>二级页跳转请求（分类首页的入口行点击时触发）。</summary>
    public event Action<string>? PageRequested;

    public WorkbenchSectionsView()
    {
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.PropertyChanged -= Changed;
            _vm = DataContext as WorkbenchCustomizationViewModel;
            if (_vm is not null) _vm.PropertyChanged += Changed;
            Render();
        };
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PageProperty || change.Property == SearchActiveProperty) Render();
    }

    private void Changed(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(WorkbenchCustomizationViewModel.Current)) return;
        Dispatcher.UIThread.Post(Refresh);
    }

    private void Render()
    {
        if (_vm is not { } vm) { Content = null; _rows = null; _builtPage = null; return; }
        var rows = new StackPanel { Spacing = 0 };
        _bindings.Clear();
        _firstGroup = true;
        switch (Page)
        {
            case "appearance": RenderAppearance(vm, rows); break;
            case "appearance.arrange": RenderArrangement(vm, rows); break;
            case "appearance.arrange.aux": RenderAuxiliary(vm, rows); break;
            case "functions": RenderConversation(vm, rows); break;
            case "functions.names": RenderNames(vm, rows); break;
            case "functions.floating": RenderFloating(vm, rows); break;
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
        try { foreach (var binding in _bindings) binding(current); }
        finally { _refreshing = false; }
    }

    // ================= 页面 =================

    private void RenderAppearance(WorkbenchCustomizationViewModel vm, StackPanel rows)
    {
        var settings = vm.Current;

        Group(rows, "主题与风格",
            ChoiceRow(vm, "主题", null, new[] { "跟随系统", "浅色", "深色" }, s => s.Theme, (s, v) => s with { Theme = v }, "Color", "system", "light", "dark"),
            ChoiceRow(vm, "视觉风格", "液态玻璃需要系统合成器支持，不支持时自动退回实色", new[] { "当前风格", "液态玻璃", "纯平面", "拟物" }, s => s.Surface, (s, v) => s with { Surface = v }, "Sparkle", "fluent", "glass", "flat", "classic"),
            CustomChoiceRow("预设", "一次性套用一组取值，之后仍可单独微调", new[] { "标准", "紧凑", "阅读" }, PresetOf, value => vm.Preset(value), "ArrowClockwise", "standard", "compact", "reading"));

        Group(rows, "颜色",
            ToggleRow("跟随系统强调色", "关闭后可自选任意颜色", s => s.Accent == "system",
                value => vm.Change(s => s with { Accent = value ? "system" : Hex(s.EffectiveAccent) })),
            SwatchRow(vm),
            ColorPickerRow(vm));

        Group(rows, "字体与密度",
            ChoiceRow(vm, "字号", null, new[] { "11", "12", "13", "14", "16", "18", "20", "24" },
                s => ((int)Math.Round(s.FontSize)).ToString(), (s, v) => s with { FontSize = double.Parse(v) }, "Document", "11", "12", "13", "14", "16", "18", "20", "24"),
            ChoiceRow(vm, "密度", "行高与列表间距", new[] { "紧凑", "舒适", "宽松" }, s => s.Density, (s, v) => s with { Density = v }, "AppsList", "compact", "cozy", "airy"),
            ChoiceRow(vm, "圆角", null, new[] { "无", "中", "大" }, s => s.Radius, (s, v) => s with { Radius = v }, "PanelRight", "none", "mid", "high"),
            ChoiceRow(vm, "阅读宽度", "正文的最大阅读宽度；代码与表格不受限", new[] { "窄", "标准", "自由" }, s => s.ReadingWidth, (s, v) => s with { ReadingWidth = v }, "Document", "narrow", "standard", "free"));
        Group(rows, "",
            SettingsLayout.ChevronRow("前端功能排布", "区域显隐、页面位置、辅助栏与入口位置", () => PageRequested?.Invoke("appearance.arrange"), "PanelLeft"));
    }

    private void RenderArrangement(WorkbenchCustomizationViewModel vm, StackPanel rows)
    {
        Group(rows, "区域显隐",
            ToggleRow("左侧边栏", "隐藏保留已打开页面与内容", _ => true, visible => vm.EditLayout(l => l.Hidden[DockZone.Left] = !visible), () => !vm.Layout.Hidden[DockZone.Left]),
            ToggleRow("右侧边栏", null, _ => true, visible => vm.EditLayout(l => l.Hidden[DockZone.Right] = !visible), () => !vm.Layout.Hidden[DockZone.Right]),
            ToggleRow("底部面板", "只有关闭最后一个终端页签才会终止进程", _ => true, visible => vm.EditLayout(l => l.Hidden[DockZone.Bottom] = !visible), () => !vm.Layout.Hidden[DockZone.Bottom]),
            ToggleRow("底部状态栏", "连接、Shell、活动与 Token 等运行状态，默认关闭", s => s.ShowStatusBar, value => vm.Change(s => s with { ShowStatusBar = value })));

        var pageRows = new List<Control>
        {
            CustomChoiceRow("会话管理", null, new[] { "左停靠区", "右停靠区", "底停靠区", "隐藏" }, s => s.LayoutPageZone("overview"), value => MovePage(vm, "overview", value), null, "0", "1", "2", "3"),
            CustomChoiceRow("文件", null, new[] { "左停靠区", "右停靠区", "底停靠区", "隐藏" }, s => s.LayoutPageZone("files"), value => MovePage(vm, "files", value), null, "0", "1", "2", "3"),
            CustomChoiceRow("变更", null, new[] { "左停靠区", "右停靠区", "底停靠区", "隐藏" }, s => s.LayoutPageZone("changes"), value => MovePage(vm, "changes", value), null, "0", "1", "2", "3"),
            CustomChoiceRow("文件预览", "无模块依赖的核心页，默认可用", new[] { "左停靠区", "右停靠区", "底停靠区", "隐藏" }, s => s.LayoutPageZone("preview"), value => MovePage(vm, "preview", value), null, "0", "1", "2", "3"),
            CustomChoiceRow("浏览器", null, new[] { "左停靠区", "右停靠区", "底停靠区", "隐藏" }, s => s.LayoutPageZone("browser"), value => MovePage(vm, "browser", value), null, "0", "1", "2", "3"),
            CustomChoiceRow("终端", null, new[] { "左停靠区", "右停靠区", "底停靠区", "隐藏" }, s => s.LayoutPageZone("terminal"), value => MovePage(vm, "terminal", value), null, "0", "1", "2", "3"),
            CustomChoiceRow("侧边聊天", "独立会话；向主会话传递内容需要显式操作", new[] { "左停靠区", "右停靠区", "底停靠区", "隐藏" }, s => s.LayoutPageZone("sidechat"), value => MovePage(vm, "sidechat", value), null, "0", "1", "2", "3"),
        };
        pageRows.Add(ActionRow("恢复默认布局", "恢复页面默认位置与区域显隐，保留颜色与名称", "恢复",
            () => { vm.ResetLayout(); vm.Status = "已恢复默认布局，保留颜色与名称"; }, "settings-layout-reset"));
        Group(rows, "页面位置", pageRows.ToArray());

        Group(rows, "",
            SettingsLayout.ChevronRow("辅助栏", "逐个功能选择出现在左辅助、右辅助，或不出现", () => PageRequested?.Invoke("appearance.arrange.aux"), "AppsList"));

        var entryRows = vm.Actions.Select(action => PlacementRow(vm, action)).Cast<Control>().ToArray();
        var entryBody = SettingsLayout.Group(entryRows);
        rows.Children.Add(SettingsLayout.CollapsibleGroupHeader("入口位置", entryBody, entryRows.Length));
        rows.Children.Add(entryBody);
    }

    /// <summary>
    /// 辅助栏：逐个功能选择出现在左辅助、右辅助，或不显示。
    /// 只处理能进辅助栏的动作；页面位置与顺序仍由"前端功能排布"的完整清单控制。
    /// </summary>
    private void RenderAuxiliary(WorkbenchCustomizationViewModel vm, StackPanel rows)
    {
        var choices = new[] { "左辅助", "右辅助", "不显示" };
        var values = new[] { UiActionPosition.MenuLeft, UiActionPosition.MenuRight, UiActionPosition.Hidden };
        var groups = vm.Actions
            .Where(IsAuxiliaryCandidate)
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
    private static bool IsAuxiliaryCandidate(AppActionDefinition action)
    {
        if (action.Id.StartsWith("edit.", StringComparison.Ordinal)) return false;
        if (action.Id.StartsWith("status.", StringComparison.Ordinal)) return false;
        if (action.Id.StartsWith("copy.", StringComparison.Ordinal) && action.Id == "copy.log") return true;
        var placement = WorkbenchActionDefaults.Placement(action.Id);
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
            ChoiceRow(vm, "输入区宽度", "跟随阅读宽度时输入区与正文同宽", new[] { "全宽", "跟随阅读宽度", "隐藏" }, s => s.Composer, (s, v) => s with { Composer = v }, "Edit", "wide", "reading", "hidden"));
    }

    private void RenderNames(WorkbenchCustomizationViewModel vm, StackPanel rows)
    {
        var settings = vm.Current;
        Group(rows, "显示名称",
            TextRow(vm, "用户称呼", "显示在你的消息旁", s => s.UserName, (s, v) => s with { UserName = v }),
            TextRow(vm, "助手名称", "显示在助手消息旁", s => s.AssistantName, (s, v) => s with { AssistantName = v }));
        Group(rows, "模型提示",
            ToggleRow("把名称偏好发送给模型", "让模型知道如何称呼你", s => s.UseNamesInPrompt, value => vm.Change(s => s with { UseNamesInPrompt = value })));
        _ = settings;
    }

    private void RenderFloating(WorkbenchCustomizationViewModel vm, StackPanel rows)
    {
        _ = vm;
        if (_floating is not { } floating)
        {
            Group(rows, "浮窗", SettingsLayout.Row("浮窗尚未就绪", "会话建立后可用", null));
            return;
        }
        Group(rows, "浮窗",
            HostToggleRow("显示浮窗", "关闭后提醒改由浮层投递，通知列表仍可查看", () => floating.IsOpen, value => floating.IsOpen = value),
            HostToggleRow("固定到应用内", "固定时只在应用内显示；取消固定后可脱离成独立窗口", () => floating.Pinned, value => floating.Pinned = value));
        Group(rows, "提醒渠道",
            HostToggleRow("在浮层提醒", "应用内浮层提示", () => floating.DeliversToToast, value => floating.DeliversToToast = value),
            HostToggleRow("在浮窗提醒", "事件写入浮窗列表", () => floating.DeliversToFloating, value => floating.DeliversToFloating = value),
            HostToggleRow("Windows 系统通知", floating.WindowsDeliveryStatus + "；失败自动回退应用内提醒", () => floating.DeliversToWindows, value => floating.DeliversToWindows = value));
        rows.Children.Add(SettingsLayout.Note(floating.DeliverySummary));
    }

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
        var card = new Border { Child = new StackPanel { Children = { json, actions } }, Padding = new Thickness(16, 12) };
        card.Classes.Add("settingsGroup");
        rows.Children.Add(card);
    }

    // ================= 行构件 =================

    private bool _firstGroup = true;

    private void Group(StackPanel rows, string title, params Control[] groupRows)
    {
        if (groupRows.Length == 0) return;
        if (!string.IsNullOrEmpty(title))
        {
            var header = SettingsLayout.GroupHeader(title);
            // Windows 设置在页标题下直接进第一个分组，不留大段上边距。
            if (_firstGroup) header.Classes.Add("first");
            _firstGroup = false;
            rows.Children.Add(header);
        }
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

    private Control HostToggleRow(string label, string? description, Func<bool> get, Action<bool> set)
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

    private Control SwatchRow(WorkbenchCustomizationViewModel vm)
    {
        var colors = new WrapPanel();
        foreach (var hex in new[] { "#0078D4", "#087E66", "#B14670", "#7B51B5", "#B15B00", "#6B7075" })
        {
            var swatch = new Button { Width = 30, Height = 30, Margin = new Thickness(0, 0, 6, 0), Background = new SolidColorBrush(Color.Parse(hex)) };
            swatch.Classes.Add("accentSwatch");
            ToolTip.SetTip(swatch, hex); AutomationProperties.SetName(swatch, "强调色 " + hex);
            swatch.Click += (_, _) => vm.Change(s => s with { Accent = hex });
            _bindings.Add(current => swatch.Classes.Set("selected", current.Accent.Equals(hex, StringComparison.OrdinalIgnoreCase)));
            colors.Children.Add(swatch);
        }
        return SettingsLayout.Row("强调色", "选择预设颜色，或在右侧输入十六进制值", colors);
    }

    private Control ColorPickerRow(WorkbenchCustomizationViewModel vm)
    {
        var picker = new ColorPicker { IsAlphaEnabled = false, VerticalAlignment = VerticalAlignment.Center };
        picker.ColorChanged += (_, _) =>
        {
            if (_refreshing) return;
            var accent = Hex(picker.Color);
            if (accent.Equals(vm.Current.Accent, StringComparison.OrdinalIgnoreCase)) return;
            vm.Change(s => s with { Accent = accent });
        };
        _bindings.Add(current => { var color = current.EffectiveAccent; if (picker.Color != color) picker.Color = color; });

        var custom = new TextBox { Width = 110, MaxWidth = 140, PlaceholderText = "#RRGGBB", Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        custom.KeyDown += (_, e) => { if (e.Key != Key.Enter) return; CommitAccent(vm, custom); e.Handled = true; };
        custom.LostFocus += (_, _) => CommitAccent(vm, custom);
        _bindings.Add(current => { if (!custom.IsFocused) custom.Text = current.Accent; });

        var host = new StackPanel { Orientation = Orientation.Horizontal, Children = { custom, picker } };
        return SettingsLayout.Row("自定义颜色", "任意颜色；格式 #RRGGBB", host);
    }

    private Control PlacementRow(WorkbenchCustomizationViewModel vm, AppActionDefinition action)
    {
        var positions = Enum.GetValues<UiActionPosition>();
        var titles = positions.Select(WorkbenchActionDefaults.PositionTitle).ToArray();
        var selector = new ComboBox { ItemsSource = titles, MinWidth = 125, MaxWidth = 180, VerticalAlignment = VerticalAlignment.Center };
        var order = new NumericUpDown { Minimum = -1000, Maximum = 1000, Width = 80, FormatString = "0", Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
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
        var host = new StackPanel { Orientation = Orientation.Horizontal, Children = { selector, order } };
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

    private static void CommitAccent(WorkbenchCustomizationViewModel vm, TextBox custom)
    {
        var text = custom.Text?.Trim();
        if (string.IsNullOrEmpty(text)) { custom.Text = vm.Current.Accent; return; }
        var value = text.StartsWith('#') ? text : "#" + text;
        if (value.Length != 7 || !value.Skip(1).All(Uri.IsHexDigit))
        {
            vm.Status = "颜色格式应为 #RRGGBB，例如 #0078D4";
            return;
        }
        var normalized = "#" + value[1..].ToUpperInvariant();
        custom.Text = normalized;
        if (normalized.Equals(vm.Current.Accent, StringComparison.OrdinalIgnoreCase)) return;
        vm.Change(s => s with { Accent = normalized });
    }

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
            if (TopLevel.GetTopLevel(this) is Window parent) await dialog.ShowDialog(parent);
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
