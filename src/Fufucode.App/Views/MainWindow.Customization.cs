using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.Themes.Fluent;
using Avalonia.Platform;
using Fufucode.App.Infrastructure;
using Fufucode.App.ViewModels;

namespace Fufucode.App;

public partial class MainWindow
{
    private WorkbenchCustomizationViewModel _customization = null!;
    private Styles? _customStyles;
    private readonly Dictionary<string, List<TextBlock>> _statusLabels = new(StringComparer.Ordinal);
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTimeOffset _turnStarted;
    // AttachCustomization 挂的会话事件处理器：持引用以便换会话/关闭时解除。
    private Abstractions.IChatSession? _customizationSession;
    private EventHandler<Abstractions.SessionEvent>? _customizationSessionHandler;
    internal WorkbenchCustomizationViewModel CustomizationForTesting => _customization;

    private void InitializeCustomization()
    {
        var root = Program.SelfTestMode ? Program.SelfTestDataRoot ?? Path.Combine(Path.GetTempPath(), "fufucode-selftest-appdata") : _options.CreateAppDataLayout().Root;
        _customization = new(new WorkbenchAppearanceStore(root));
        _customization.LayoutSnapshot = () => _dockLayout;
        RegisterWorkbenchActions();
        _customization.SetActions(_actions.Actions);
        _customization.Applied += ApplyCustomization;
        _actions.Changed += RefreshStatusLabels;
        _statusTimer.Tick += (_, _) => RefreshStatusLabels();
        _statusTimer.Tick += CheckSchedules;
        _statusTimer.Start();
        Closed += (_, _) => _statusTimer.Stop();
        Closed += (_, _) => ReleaseWindowSkin(this);
        if (Application.Current?.PlatformSettings is { } platform)
        {
            platform.ColorValuesChanged += SystemColorsChanged;
            Closed += (_, _) => platform.ColorValuesChanged -= SystemColorsChanged;
        }
        SizeChanged += (_, _) =>
        {
            var width = Bounds.Width > 0 ? Bounds.Width : Width;
            if (CustomTopLeft.Parent is Control left) left.Width = Math.Min(540, width * .38);
            if (CustomTopRight.Parent is Control right) right.Width = Math.Min(420, width * .34);
            if (_customization is not null) BuildActionSurfaces(_customization.Current);
        };
        ApplyCustomization(_customization.Current);
    }
    private void SystemColorsChanged(object? sender, PlatformColorValues colors) => Dispatcher.UIThread.Post(() => { if (!_closed) ApplyCustomization(_customization.Current); });
    private void AttachCustomization(MainViewModel vm)
    {
        if (_customization is null) return;
        vm.Settings.Customization = _customization;
        _customization.AttachProject(_host?.ProjectPath ?? (_options.Mode == Integration.AppSessionMode.Mock ? null : _options.ProjectPath));
        _turnStarted = DateTimeOffset.UtcNow;
        // 先解绑上一份：会话切换后旧 VM/旧会话的回合事件不得再驱动状态标签。
        DetachCustomizationSession();
        _customizationSession = vm.Session;
        _customizationSessionHandler = (_, e) =>
        {
            if (e is Abstractions.SessionEvent.MessageStarted { Role: Abstractions.MessageRole.User }) _turnStarted = DateTimeOffset.UtcNow;
            if (e is Abstractions.SessionEvent.TurnSettled) Dispatcher.UIThread.Post(() => OnCustomizedTurnSettled(vm));
            Dispatcher.UIThread.Post(RefreshStatusLabels);
        };
        _customizationSession.Event += _customizationSessionHandler;
        _chatPage.AttachWorkbenchActions(_actions);
        RecordNavigation();
        ApplyCustomization(_customization.Current);
    }

    /// <summary>解除 <see cref="AttachCustomization"/> 挂的会话事件处理器（重复调用安全）。</summary>
    private void DetachCustomizationSession()
    {
        if (_customizationSession is not null && _customizationSessionHandler is not null)
        {
            _customizationSession.Event -= _customizationSessionHandler;
        }

        _customizationSession = null;
        _customizationSessionHandler = null;
    }
    /// <summary>上一次完整应用的配置签名；同一配置重复调用时直接跳过整套样式重建。</summary>
    private string? _appliedCustomization;

    private void ApplyCustomization(WorkbenchAppearance settings)
    {
        if (_closed) return;
        // 启动期会先由 InitializeCustomization 应用一次、再由 AttachCustomization 应用一次；
        // 样式重建会触发整棵可视化树重新求值（实测约 0.6~0.9 秒），视觉配置相同就不应重复。
        // 布局单独判断：项目布局由 AttachWorkspaceContext 先行恢复，不应为了布局重放整套样式。
        var signature = (settings with { Layout = null }).Serialize();
        if (signature == _appliedCustomization)
        {
            if (settings.Layout is not null && settings.Layout.Serialize() != _dockLayout.Serialize())
            {
                _dockLayout = WorkbenchLayoutState.Restore(settings.Layout.Serialize(), _workbenchPages.KnownPageIds);
                RenderDockPages();
            }
            return;
        }
        _appliedCustomization = signature;
        Fufucode.Core.Diagnostics.StartupProbe.Mark("custom.release-skin.begin", "release previous skin");
        ReleaseWindowSkin(this);
        Fufucode.Core.Diagnostics.StartupProbe.Mark("custom.release-skin.end", "previous skin released");
        if (settings.Layout is not null && settings.Layout.Serialize() != _dockLayout.Serialize())
        {
            _dockLayout = WorkbenchLayoutState.Restore(settings.Layout.Serialize(), _workbenchPages.KnownPageIds);
            RenderDockPages();
            Fufucode.Core.Diagnostics.StartupProbe.Mark("custom.dock-render", "dock pages rendered from saved layout");
        }
        RequestedThemeVariant = SettingsPageViewModel.ThemeVariantFor(settings.Theme);
        // Surface 旧轴下线后：皮肤自己管理材质；Fluent 保留 Mica 基线；
        // LiquidGlass 请求 AcrylicBlur/Blur 让窗后桌面透出（玻璃面折射窗内内容，两者互补）；
        // 其余皮肤不再受 Surface 影响。
        TransparencyLevelHint = settings.SkinResolution.Skin switch
        {
            Features.Skins.SkinId.Fluent => [WindowTransparencyLevel.Mica, WindowTransparencyLevel.None],
            Features.Skins.SkinId.LiquidGlass => [WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Blur, WindowTransparencyLevel.None],
            _ => [WindowTransparencyLevel.None],
        };
        var accent = settings.EffectiveAccent;
        var palette = new ColorPaletteResources { Accent = accent };
        foreach (var key in new[] { "SystemAccentColor", "SystemAccentColorDark1", "SystemAccentColorDark2", "SystemAccentColorDark3", "SystemAccentColorLight1", "SystemAccentColorLight2", "SystemAccentColorLight3" })
            if (palette.TryGetResource(key, ActualThemeVariant, out var color)) Resources[key] = color;
        Color AccentColor(string key) => Resources[key] is Color color ? color : accent;
        var darkTheme = ActualThemeVariant == ThemeVariant.Dark;
        var fill = darkTheme ? AccentColor("SystemAccentColorLight2") : accent;
        var textFill = AccentColor(darkTheme ? "SystemAccentColorLight3" : "SystemAccentColorDark2");
        var contrast = (fill.R * .2126 + fill.G * .7152 + fill.B * .0722) > 155 ? Colors.Black : Colors.White;
        foreach (var key in new[] { "AccentFillColorDefaultBrush", "SystemControlBackgroundAccentBrush", "SystemControlForegroundAccentBrush", "SystemControlHighlightAccentBrush", "SystemControlHighlightAltAccentBrush", "SystemControlRevealFocusVisualBrush", "TextControlBorderBrushFocused", "TabItemHeaderSelectedPipeFill", "AccentButtonBackground" })
            Resources[key] = new SolidColorBrush(fill);
        Resources["AccentTextFillColorPrimaryBrush"] = new SolidColorBrush(textFill);
        Resources["AccentFillColorSecondaryBrush"] = new SolidColorBrush(AccentColor(darkTheme ? "SystemAccentColorLight1" : "SystemAccentColorDark1"));
        Resources["AccentFillColorTertiaryBrush"] = new SolidColorBrush(darkTheme ? accent : AccentColor("SystemAccentColorDark2"));
        Resources["AccentButtonForeground"] = new SolidColorBrush(contrast);
        Resources["AppUserBubble"] = new SolidColorBrush(fill, darkTheme ? .42 : .26);
        Resources["SystemAccentColor"] = accent;
        Resources["AppAccent"] = new SolidColorBrush(fill);
        Resources["AppAccentText"] = new SolidColorBrush(textFill);
        Resources["AppAccentForeground"] = new SolidColorBrush(contrast);
        Resources["AppAccentSubtle"] = new SolidColorBrush(Color.FromArgb(30, accent.R, accent.G, accent.B));
        Resources["UiUserName"] = settings.UserName; Resources["UiAssistantName"] = settings.AssistantName;
        Resources["UiShowUserNames"] = settings.ShowUserNames; Resources["UiShowAssistantNames"] = settings.ShowAssistantNames;
        Resources["UiShowAvatars"] = settings.ShowAvatars; Resources["UiShowMessageTimes"] = settings.ShowMessageTimes;
        Resources["UiShowChangeSummary"] = settings.ShowChangeSummary; Resources["UiShowArtifactSummary"] = settings.ShowArtifactSummary;
        if (_customStyles is not null) Styles.Remove(_customStyles);
        _customStyles = new Styles();
        Add(s => s.OfType<TextBlock>().Class("body"), TextBlock.FontSizeProperty, settings.FontSize);
        Add(s => s.OfType<SelectableTextBlock>().Class("body"), TextBlock.FontSizeProperty, settings.FontSize);
        Add(s => s.OfType<Button>(), TemplatedControl.CornerRadiusProperty, new CornerRadius(settings.Corner));
        foreach (var buttonClass in new[] { "iconButton", "subtle", "accent", "dockTab" })
            Add(s => s.OfType<Button>().Class(buttonClass), TemplatedControl.CornerRadiusProperty, new CornerRadius(settings.Corner));
        Add(s => s.OfType<TextBox>(), TemplatedControl.CornerRadiusProperty, new CornerRadius(settings.Corner));
        Add(s => s.OfType<ComboBox>(), TemplatedControl.CornerRadiusProperty, new CornerRadius(settings.Corner));
        Add(s => s.OfType<Border>().Class("userBubble"), Border.CornerRadiusProperty, new CornerRadius(settings.Corner));
        Add(s => s.OfType<Button>().Class("dockTab"), TemplatedControl.MinHeightProperty, settings.RowHeight - 2);
        Add(s => s.OfType<Button>().Class("dockTab"), TemplatedControl.HeightProperty, settings.RowHeight - 2);
        Add(s => s.OfType<ListBoxItem>(), TemplatedControl.MinHeightProperty, settings.RowHeight);
        Styles.Add(_customStyles);
        Fufucode.Core.Diagnostics.StartupProbe.Mark("custom.styles", "custom styles rebuilt");
        ApplyWindowSkin(this, settings);
        Fufucode.Core.Diagnostics.StartupProbe.Mark("custom.skin", "window skin applied");
        CustomStatusBar.IsVisible = settings.ShowStatusBar;
        ChromeRoot.RowDefinitions[2].Height = new GridLength(settings.ShowStatusBar ? 26 : 0);
        _chatPage.ApplyAppearance(settings);
        foreach (var dock in Docks()) dock.ApplyAppearance(settings);
        RenderFloatingWindows();
        var identity = settings.UseNamesInPrompt && (settings.UserName != "你" || settings.AssistantName != "Fufucode")
            ? $"用户希望称呼为 {settings.UserName}。助手显示名称为 {settings.AssistantName}。" : "";
        if (_vm is not null) _vm.Input.IdentityContext = identity;
        if (_sideChatPage.DataContext is MainViewModel side) { side.Input.IdentityContext = identity; _sideChatPage.ApplyAppearance(settings); }
        Fufucode.Core.Diagnostics.StartupProbe.Mark("custom.docks", "dock appearances applied");
        BuildActionSurfaces(settings);
        Fufucode.Core.Diagnostics.StartupProbe.Mark("custom.actions", "action surfaces rebuilt");
        ApplyWorkspaceLayout();
        Fufucode.Core.Diagnostics.StartupProbe.Mark("custom.layout", "workspace layout applied");

        void Add(Func<Selector?, Selector> selector, AvaloniaProperty property, object value)
        { var style = new Style(selector); style.Setters.Add(new Setter(property, value)); _customStyles.Add(style); }
    }
    private void BuildActionSurfaces(WorkbenchAppearance settings)
    {
        CustomTopLeft.Children.Clear(); CustomTopRight.Children.Clear(); CustomStatusLeft.Children.Clear(); CustomStatusRight.Children.Clear(); CustomTopCenter.Children.Clear();
        _statusLabels.Clear();
        var menus = new Dictionary<UiActionPosition, MenuFlyout>
        { [UiActionPosition.MenuLeft] = new(), [UiActionPosition.MenuRight] = new(), [UiActionPosition.HeaderMenu] = new() };
        // 菜单二级分组：父项（如"复制"）在列表里出现一次，组内动作挂到它的子菜单。
        var subMenus = new Dictionary<string, MenuItem>(StringComparer.Ordinal);
        foreach (var action in _actions.Actions
            .OrderBy(a => settings.Actions.GetValueOrDefault(a.Id, WorkbenchActionDefaults.Placement(a.Id)).Order)
            .ThenBy(action => WorkbenchActionDefaults.SubMenuOrder(action.Id)))
        {
            var placement = settings.Actions.GetValueOrDefault(action.Id, WorkbenchActionDefaults.Placement(action.Id));
            if (placement.Position == UiActionPosition.Hidden) continue;
            // 会话岛专用位置：磁贴由 BuildSessionIsland 统一渲染，不进顶栏/菜单。
            // 非 Fluent 皮肤下岛不存在，这些动作折回左侧功能菜单，保证可达。
            var islandSlot = UseSessionIsland(settings);
            if (placement.Position == UiActionPosition.SessionIsland && islandSlot) continue;
            var effective = placement.Position == UiActionPosition.SessionIsland ? UiActionPosition.MenuLeft : placement.Position;
            if (menus.TryGetValue(effective, out var menu))
            {
                var item = new MenuItem { Header = action.Title, Command = _actions.GetCommand(action.Id) };
                item.Icon = Views.WorkbenchUi.IconButton(WorkbenchActionDefaults.Icon(action.Id), action.Title).Content;
                ToolTip.SetTip(item, action.Description);
                var group = WorkbenchActionDefaults.SubMenu(action.Id);
                if (group is null) { menu.Items.Add(item); continue; }
                var key = effective + "/" + group;
                if (!subMenus.TryGetValue(key, out var parent))
                {
                    parent = new MenuItem { Header = group };
                    parent.Icon = Views.WorkbenchUi.IconButton(
                        group switch
                        {
                            "工作区页面" => "AppsList",
                            "复制" => "Copy",
                            "导出" => "ArrowDownload",
                            "分叉" => "Branch",
                            "项目" => "Folder",
                            "会话管理" => "Chat",
                            "编辑" => "Edit",
                            "视图与布局" => "WindowNew",
                            "语音朗读" => "Speaker",
                            "诊断与系统" => "Wrench",
                            "帮助与全局" => "Info",
                            _ => "Folder"
                        }, group).Content;
                    subMenus.Add(key, parent);
                    menu.Items.Add(parent);
                }
                // 组内第一项（默认项）同时挂到父项上，双击父项等同执行它。
                if (WorkbenchActionDefaults.SubMenuOrder(action.Id) == 0 && parent.Command is null)
                    parent.Command = _actions.GetCommand(action.Id);
                parent.Items.Add(item);
                continue;
            }
            var panel = effective switch
            { UiActionPosition.TopLeft => CustomTopLeft, UiActionPosition.TopRight => CustomTopRight,
              UiActionPosition.TopCenter => CustomTopCenter,
              UiActionPosition.StatusLeft => CustomStatusLeft, _ => CustomStatusRight };
            if (action.Id.StartsWith("status.", StringComparison.Ordinal))
            {
                var label = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 260, TextTrimming = TextTrimming.CharacterEllipsis };
                label.Classes.Add("meta");
                if (!_statusLabels.TryGetValue(action.Id, out var labels)) _statusLabels.Add(action.Id, labels = new());
                labels.Add(label); ToolTip.SetTip(label, action.Title); panel.Children.Add(label);
            }
            else
            {
                var title = action.Title;
                var button = Views.WorkbenchUi.IconButton(WorkbenchActionDefaults.Icon(action.Id), title, 46);
                button.Height = 32; button.MinHeight = 32; button.Classes.Add("captionSlot");
                button.Classes.Add("toolButton");
                button.Command = _actions.GetCommand(action.Id);
                Avalonia.Automation.AutomationProperties.SetAutomationId(button, "action-" + action.Id);
                button.Tag = action.Id;
                panel.Children.Add(button);
            }
        }
        // 顶栏中间放不下时溢出到左辅助菜单，保证功能始终可达。
        // 中间区可用宽度按窗口宽度估算：留出左侧动作、右侧动作与窗口按钮的余量。
        {
            var windowWidth = Bounds.Width > 0 ? Bounds.Width : Width;
            var centerCapacity = Math.Max(1, (int)(Math.Min(520, windowWidth * .35) / 49));
            var centerButtons = CustomTopCenter.Children.OfType<Button>().ToArray();
            foreach (var button in centerButtons.Skip(centerCapacity).Reverse())
            {
                CustomTopCenter.Children.Remove(button);
                var item = new MenuItem { Header = Avalonia.Automation.AutomationProperties.GetName(button), Command = button.Command,
                    Icon = Views.WorkbenchUi.IconButton(WorkbenchActionDefaults.Icon((string)button.Tag!), "").Content };
                Avalonia.Automation.AutomationProperties.SetAutomationId(item, "action-" + button.Tag);
                menus[UiActionPosition.MenuLeft].Items.Insert(0, item);
            }
        }
        foreach (var (position, panel) in new[] { (UiActionPosition.MenuLeft, CustomTopLeft), (UiActionPosition.MenuRight, CustomTopRight) })
        {
            var width = Bounds.Width > 0 ? Bounds.Width : Width;
            var available = position == UiActionPosition.MenuLeft ? Math.Min(540, width * .38) : Math.Min(420, width * .34);
            var capacity = Math.Max(1, (int)((available - 16) / 49) - 1);
            var buttons = panel.Children.OfType<Button>().ToArray();
            var visible = buttons.OrderByDescending(b => b.Tag as string is "settings" or "navigate.back").Take(capacity).ToHashSet();
            foreach (var button in buttons.Where(b => !visible.Contains(b)))
            {
                button.IsVisible = false;
                var item = new MenuItem { Header = Avalonia.Automation.AutomationProperties.GetName(button), Command = button.Command,
                    Icon = Views.WorkbenchUi.IconButton(WorkbenchActionDefaults.Icon((string)button.Tag!), "").Content };
                Avalonia.Automation.AutomationProperties.SetAutomationId(item, "action-" + button.Tag);
                menus[position].Items.Insert(0, item);
            }
            if (menus[position].Items.Count == 0) continue;
            // 左辅助用向下箭头（展开），右辅助用省略号（更多）
            var more = Views.WorkbenchUi.IconButton(position == UiActionPosition.MenuLeft ? "ChevronDown" : "More",
                position == UiActionPosition.MenuLeft ? "左辅助功能" : "右辅助功能", 46);
            more.Height = 32; more.MinHeight = 32; more.Classes.Add("captionSlot");
            more.Flyout = menus[position]; panel.Children.Add(more);
        }
        _chatPage.SetHeaderMenu(menus[UiActionPosition.HeaderMenu]);
        BuildSessionIsland(settings, _actions.Actions);
        RefreshStatusLabels();
    }
    private void RefreshStatusLabels()
    {
        foreach (var pair in _statusLabels)
            foreach (var label in pair.Value) label.Text = StatusText(pair.Key);
        RefreshIsland();
    }
    private string StatusText(string id) => id switch
    {
        "status.connection" => _vm?.StatusBar.ConnectionText ?? "未连接",
        "status.shell" => _terminalPage is null ? "Shell 插件未启用" : _terminalPage.HasRunningTab ? "Shell 已连接" : "Shell 未连接",
        "status.activity" => _vm?.StatusBar.ActivityText ?? "空闲",
        "status.tokens" => _host is null ? "Token：Mock" : "Token：" + (_host.Current.Coordinator.GetUsage().Session.TotalTokens.Value?.ToString() ?? "未知"),
        "status.duration" => "用时 " + (DateTimeOffset.UtcNow - _turnStarted).ToString(@"hh\:mm\:ss"),
        "status.project" => _vm?.Sidebar.ProjectName ?? "Fufucode",
        "status.branch" => _branchLabel,
        _ => id
    };
}
