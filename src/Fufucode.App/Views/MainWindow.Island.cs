using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using Fufucode.App.Infrastructure;
using Fufucode.App.ViewModels;

namespace Fufucode.App;

public partial class MainWindow
{
    // 会话灵动岛：顶栏中央的"活态显示"（iOS Dynamic Island 语义）。
    // 紧凑态 = 呼吸圆点 + 会话标题 + 运行态 + 用时 + 展开提示；
    // 卫星点 = 次级活动（排队数）的微缩呈现；点展开态 = 会话详情 + 动作磁贴 + 实时活动条。
    // 几何变化本身即信号：运行/空闲/失败改变圆点颜色与胶囊内容，而非整行重排。
    private Border? _islandPill;
    private Border? _islandDot;
    private TextBlock? _islandTitle;
    private Border? _islandSep;
    private TextBlock? _islandActivity;
    private TextBlock? _islandElapsed;
    private Border? _islandSatellite;
    private TextBlock? _islandSatCount;
    private Flyout? _islandFlyout;
    private TextBlock? _islandLiveTitle;
    private TextBlock? _islandLiveMeta;
    private Border? _islandLiveDot;
    private TextBlock? _islandLiveText;
    private Button? _islandStopButton;
    private Button? _islandSteerButton;
    private Border? _islandLiveBar;
    private TextBlock? _islandPanelTitle;
    private TextBlock? _islandPanelSubtitle;

    /// <summary>当前皮肤是否使用会话岛（仅 Fluent 实现了专属岛视觉）。</summary>
    private bool UseSessionIsland(WorkbenchAppearance settings) =>
        settings.SkinResolution.Skin == Features.Skins.SkinId.Fluent;

    /// <summary>
    /// 构建/重建会话岛。Fluent 皮肤下接管顶栏中央（隐藏旧的"品牌+裸标题"回退块）；
    /// 其它皮肤回退为普通标题，SessionIsland 位置的动作经 BuildActionSurfaces 折回左菜单。
    /// </summary>
    private void BuildSessionIsland(WorkbenchAppearance settings, IEnumerable<Infrastructure.AppActionDefinition> actions)
    {
        IslandHost.Children.Clear();
        _islandPill = null;
        _islandDot = _islandSep = _islandSatellite = _islandLiveDot = null;
        _islandTitle = _islandActivity = _islandElapsed = _islandSatCount = null;
        _islandLiveTitle = _islandLiveMeta = _islandLiveText = _islandPanelTitle = _islandPanelSubtitle = null;
        _islandLiveBar = null; _islandStopButton = _islandSteerButton = null;
        _islandFlyout = null;

        if (!UseSessionIsland(settings))
        {
            // 非 Fluent：旧的居中标题回退，岛隐藏。
            CaptionBrandFallback.IsVisible = true;
            return;
        }
        CaptionBrandFallback.IsVisible = false;

        // ── 紧凑胶囊 ──────────────────────────────────────────────
        _islandDot = new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), VerticalAlignment = VerticalAlignment.Center };
        _islandTitle = new TextBlock { FontSize = 12, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 170, TextTrimming = TextTrimming.CharacterEllipsis };
        _islandSep = new Border { Width = 1, Height = 14, VerticalAlignment = VerticalAlignment.Center };
        _islandActivity = new TextBlock { FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 220, TextTrimming = TextTrimming.CharacterEllipsis };
        _islandElapsed = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        var chevron = new PathIcon { Width = 11, Height = 11, VerticalAlignment = VerticalAlignment.Center };
        chevron.Bind(PathIcon.DataProperty, new DynamicResourceExtension("Icon.ChevronDown"));

        var pillContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        pillContent.Children.Add(_islandDot);
        pillContent.Children.Add(_islandTitle);
        pillContent.Children.Add(_islandSep);
        pillContent.Children.Add(_islandActivity);
        pillContent.Children.Add(_islandElapsed);
        pillContent.Children.Add(chevron);

        // 胶囊为 Border：PointerPressed 标记 handled，顶栏 PointerPressed→BeginMoveDrag
        // 的冒泡监听不会触发（默认不订阅 handledEventsToo），点击岛不误判为拖窗。
        _islandPill = new Border
        {
            Child = pillContent,
            Padding = new Thickness(12, 0),
            Height = 32,
            CornerRadius = new CornerRadius(16),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };
        _islandPill.Classes.Add("sessionIsland");
        Avalonia.Automation.AutomationProperties.SetAutomationId(_islandPill, "session-island");
        Avalonia.Automation.AutomationProperties.SetName(_islandPill, "会话岛");
        ToolTip.SetTip(_islandPill, "会话岛 · 点击展开会话详情与快捷操作");
        _islandPill.PointerPressed += (_, e) => e.Handled = true;
        _islandPill.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton != Avalonia.Input.MouseButton.Left) return;
            _islandFlyout?.ShowAt(_islandPill);
        };

        // ── 卫星点（次级活动：排队数）────────────────────────────
        _islandSatCount = new TextBlock { FontSize = 9, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _islandSatellite = new Border
        {
            Width = 22, Height = 22, CornerRadius = new CornerRadius(11),
            Child = _islandSatCount, Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _islandSatellite.Classes.Add("islandSatellite");

        var compact = new StackPanel { Orientation = Orientation.Horizontal, Children = { _islandPill, _islandSatellite } };
        IslandHost.Children.Add(compact);

        // ── 展开面板（Fluent 浮出层：MenuSurfaceBrush + 尾指 + 磁贴网格 + 实时活动条）──
        var panel = BuildIslandPanel(actions);
        _islandFlyout = new Flyout { Content = panel, Placement = PlacementMode.Bottom, ShowMode = FlyoutShowMode.Transient };
        _islandFlyout.Opened += (_, _) => RefreshIsland();

        RefreshIsland();
    }

    private Border BuildIslandPanel(IEnumerable<Infrastructure.AppActionDefinition> actions)
    {
        // 头部：后端块 + 标题 + 副标（模型 · 项目 · 分支），右侧实时元信息（用时 · token）。
        var backendTile = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(6), VerticalAlignment = VerticalAlignment.Center };
        backendTile.Classes.Add("islandBackendTile");
        var backendIcon = new PathIcon { Width = 13, Height = 13, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        backendIcon.Bind(PathIcon.DataProperty, new DynamicResourceExtension("Icon.Sparkle"));
        backendTile.Child = backendIcon;

        _islandPanelTitle = new TextBlock { FontSize = 13, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        _islandPanelSubtitle = new TextBlock { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
        _islandPanelSubtitle.Classes.Add("meta");
        var who = new StackPanel { Spacing = 1 };
        who.Children.Add(_islandPanelTitle); who.Children.Add(_islandPanelSubtitle);

        _islandLiveMeta = new TextBlock { FontSize = 11, TextAlignment = TextAlignment.Right };
        _islandLiveMeta.Classes.Add("meta");

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 10 };
        header.Children.Add(backendTile);
        Grid.SetColumn(who, 1); header.Children.Add(who);
        Grid.SetColumn(_islandLiveMeta, 2); header.Children.Add(_islandLiveMeta);

        // 磁贴网格：会话级动作精选 4×2（按使用频率排，非字母序）。
        var tiles = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*,*"), RowDefinitions = new RowDefinitions("Auto,Auto"), ColumnSpacing = 6, RowSpacing = 6, Margin = new Thickness(0, 12, 0, 12) };
        var preferred = new[] { "session.rename", "session.pin", "session.side", "session.fork",
                                "session.search", "session.share", "session.archive", "session.schedule" };
        var byId = actions.Where(a => a.Id.StartsWith("session.", StringComparison.Ordinal))
            .GroupBy(a => a.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var islandActions = preferred.Where(byId.ContainsKey).Select(id => byId[id]).ToArray();
        int index = 0;
        foreach (var action in islandActions)
        {
            var tile = BuildIslandTile(action);
            Grid.SetColumn(tile, index % 4); Grid.SetRow(tile, index / 4);
            tiles.Children.Add(tile);
            index++;
        }

        // 实时活动条：呼吸点 + 当前状态 + 插入/停止。失败时整条升 critical 色。
        _islandLiveDot = new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), VerticalAlignment = VerticalAlignment.Center };
        _islandLiveText = new TextBlock { FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        _islandLiveText.Classes.Add("meta");
        _islandSteerButton = new Button { Content = "插入", FontSize = 11, Padding = new Thickness(10, 4), MinHeight = 0 };
        _islandSteerButton.Classes.Add("islandBarButton");
        _islandStopButton = new Button { Content = "停止", FontSize = 11, Padding = new Thickness(10, 4), MinHeight = 0 };
        _islandStopButton.Classes.Add("islandBarButton");
        var liveRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), ColumnSpacing = 8 };
        liveRow.Children.Add(_islandLiveDot);
        Grid.SetColumn(_islandLiveText, 1); liveRow.Children.Add(_islandLiveText);
        Grid.SetColumn(_islandSteerButton, 2); liveRow.Children.Add(_islandSteerButton);
        Grid.SetColumn(_islandStopButton, 3); liveRow.Children.Add(_islandStopButton);
        _islandLiveBar = new Border { Child = liveRow };
        _islandLiveBar.Classes.Add("islandLiveBar");

        var body = new StackPanel();
        body.Children.Add(header);
        body.Children.Add(tiles);
        body.Children.Add(_islandLiveBar);

        var card = new Border { Width = 380, Padding = new Thickness(14), Child = body };
        card.Classes.Add("islandPanel");
        return card;
    }

    private Control BuildIslandTile(Infrastructure.AppActionDefinition action)
    {
        var icon = new PathIcon { Width = 15, Height = 15, HorizontalAlignment = HorizontalAlignment.Center };
        icon.Bind(PathIcon.DataProperty, new DynamicResourceExtension("Icon." + WorkbenchActionDefaults.Icon(action.Id)));
        var label = new TextBlock { Text = action.Title, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var content = new StackPanel { Spacing = 5, HorizontalAlignment = HorizontalAlignment.Center };
        content.Children.Add(icon); content.Children.Add(label);
        var button = new Button
        {
            Content = content,
            Padding = new Thickness(4, 9, 4, 8),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Command = _actions.GetCommand(action.Id),
        };
        button.Classes.Add("islandTile");
        ToolTip.SetTip(button, action.Description);
        Avalonia.Automation.AutomationProperties.SetAutomationId(button, "island-" + action.Id);
        return button;
    }

    /// <summary>每秒刷新岛与展开面板的活态（由 _statusTimer.Tick 统一驱动）。</summary>
    private void RefreshIsland()
    {
        if (_islandPill is null) return;
        var vm = _vm;
        var running = vm?.StatusBar.IsRunning == true;
        var activity = vm?.StatusBar.ActivityText ?? "空闲";
        var title = vm?.Sidebar.SelectedSession?.Title ?? vm?.Sidebar.CurrentName ?? "会话";
        var queued = vm?.StatusBar.QueueText;
        var outcome = vm?.StatusBar.OutcomeText;
        var failed = outcome?.Contains("失败") == true;

        // 紧凑胶囊
        _islandTitle?.SetValue(TextBlock.TextProperty, title);
        if (_islandDot is not null)
        {
            _islandDot.Classes.Remove("running"); _islandDot.Classes.Remove("failed"); _islandDot.Classes.Remove("idle");
            _islandDot.Classes.Add(running ? "running" : failed ? "failed" : "idle");
        }
        var showLive = running || failed;
        _islandSep?.SetValue(Visual.IsVisibleProperty, showLive);
        _islandActivity?.SetValue(Visual.IsVisibleProperty, showLive);
        _islandElapsed?.SetValue(Visual.IsVisibleProperty, showLive);
        _islandActivity?.SetValue(TextBlock.TextProperty, activity);
        _islandElapsed?.SetValue(TextBlock.TextProperty, showLive ? "⏱ " + (DateTimeOffset.UtcNow - _turnStarted).ToString(@"mm\:ss") : "");

        // 卫星点：有排队时亮起并显示数量
        var hasQueue = vm?.StatusBar.HasQueue == true;
        _islandSatellite?.SetValue(Visual.IsVisibleProperty, hasQueue);
        _islandSatCount?.SetValue(TextBlock.TextProperty, queued?.Replace("队列：", "") ?? "");

        // 展开面板
        _islandPanelTitle?.SetValue(TextBlock.TextProperty, title);
        _islandPanelSubtitle?.SetValue(TextBlock.TextProperty, $"{vm?.StatusBar.ModelText ?? ""} · {vm?.Sidebar.ProjectName ?? "Fufucode"} · {_branchLabel.Replace("Git：", "")}");
        var usage = vm?.StatusBar.UsageText;
        _islandLiveMeta?.SetValue(TextBlock.TextProperty,
            (showLive ? "⏱ " + (DateTimeOffset.UtcNow - _turnStarted).ToString(@"mm\:ss") : activity) + (string.IsNullOrEmpty(usage) ? "" : "\n" + usage.Replace("会话累计：", "")));
        _islandLiveText?.SetValue(TextBlock.TextProperty,
            running ? $"{activity} · {queued ?? "执行中"}"
            : failed ? outcome
            : string.IsNullOrEmpty(usage) ? "会话空闲，无进行中的活动" : "上次回合 " + usage.Replace("会话累计：", ""));
        if (_islandLiveDot is not null)
        {
            _islandLiveDot.Classes.Remove("running"); _islandLiveDot.Classes.Remove("failed"); _islandLiveDot.Classes.Remove("idle");
            _islandLiveDot.Classes.Add(running ? "running" : failed ? "failed" : "idle");
        }
        _islandLiveBar?.Classes.Remove("failed");
        if (failed) _islandLiveBar?.Classes.Add("failed");
        _islandStopButton?.SetValue(Visual.IsVisibleProperty, running);
        _islandSteerButton?.SetValue(Visual.IsVisibleProperty, running);
        if (vm is not null)
        {
            _islandStopButton?.SetValue(Button.CommandProperty, vm.Input.StopCommand);
            _islandSteerButton?.SetValue(Button.CommandProperty, vm.Input.SteerCommand);
            _islandSteerButton?.SetValue(InputElement.IsEnabledProperty, vm.Input.CanSteer);
        }
    }
}
