using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Fufucode.App.Infrastructure;
using Fufucode.App.ViewModels;

namespace Fufucode.App;

public partial class MainWindow
{
    // 会话灵动岛（iOS Dynamic Island 语义）：顶栏中央的活态胶囊。
    // 紧凑态把"谁在跑、跑到哪、跑了多久、花了多少、谁在等"压成一行：
    //   ● 呼吸点(运行/空闲/失败) + 会话名 + 模型 + 状态词 + ⏱用时 + ⚡用量 + ⌄
    // 展开面板 = 主窗 IslandOverlay 里的卡片（不用 Popup：独立视觉树拿不到窗口样式/资源）。
    private Border? _islandPill;
    private Border? _islandDot;
    private TextBlock? _islandTitle;
    private TextBlock? _islandModel;
    private Border? _islandSep;
    private TextBlock? _islandActivity;
    private TextBlock? _islandElapsed;
    private TextBlock? _islandUsage;
    private Border? _islandSatellite;
    private TextBlock? _islandSatCount;
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
        IslandOverlayHost.Content = null;
        IslandOverlay.IsVisible = false;
        _islandPill = null;
        _islandDot = _islandSep = _islandSatellite = _islandLiveDot = null;
        _islandTitle = _islandModel = _islandActivity = _islandElapsed = _islandUsage = _islandSatCount = null;
        _islandLiveMeta = _islandLiveText = _islandPanelTitle = _islandPanelSubtitle = null;
        _islandLiveBar = null; _islandStopButton = _islandSteerButton = null;

        if (!UseSessionIsland(settings))
        {
            CaptionBrandFallback.IsVisible = true;
            return;
        }
        CaptionBrandFallback.IsVisible = false;

        // ── 紧凑胶囊：● 名称 · 模型 │ 状态 ⏱ ⚡ ⌄ ─────────────────────
        _islandDot = new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), VerticalAlignment = VerticalAlignment.Center };
        _islandTitle = new TextBlock { FontSize = 12, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 170, TextTrimming = TextTrimming.CharacterEllipsis };
        _islandModel = new TextBlock { FontSize = 10.5, VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 120, TextTrimming = TextTrimming.CharacterEllipsis };
        _islandModel.Classes.Add("dim");
        _islandSep = new Border { Width = 1, Height = 14, VerticalAlignment = VerticalAlignment.Center };
        _islandActivity = new TextBlock { FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 200, TextTrimming = TextTrimming.CharacterEllipsis };
        _islandElapsed = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        _islandElapsed.Classes.Add("dim");
        _islandUsage = new TextBlock { FontSize = 10.5, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 90, TextTrimming = TextTrimming.CharacterEllipsis };
        _islandUsage.Classes.Add("dim");
        var chevron = new Avalonia.Controls.Shapes.Path { Width = 10, Height = 10, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center };
        chevron.Bind(Avalonia.Controls.Shapes.Path.DataProperty, new DynamicResourceExtension("Icon.ChevronDown"));
        chevron.Bind(Avalonia.Controls.Shapes.Shape.FillProperty, new DynamicResourceExtension("AppTextDim"));

        var pillContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, VerticalAlignment = VerticalAlignment.Center };
        pillContent.Children.Add(_islandDot);
        pillContent.Children.Add(_islandTitle);
        pillContent.Children.Add(_islandModel);
        pillContent.Children.Add(_islandSep);
        pillContent.Children.Add(_islandActivity);
        pillContent.Children.Add(_islandElapsed);
        pillContent.Children.Add(_islandUsage);
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
            ToggleIslandPanel();
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

        IslandOverlayHost.Content = BuildIslandPanel(actions);

        RefreshIsland();
    }

    private void ToggleIslandPanel() => IslandOverlay.IsVisible = !IslandOverlay.IsVisible;

    private void IslandScrim_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        IslandOverlay.IsVisible = false;

    private Control BuildIslandPanel(IEnumerable<Infrastructure.AppActionDefinition> actions)
    {
        // 头部：后端块 + 标题 + 副标（模型 · 项目 · 分支），右侧实时元信息。
        var backendTile = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(7), VerticalAlignment = VerticalAlignment.Center };
        backendTile.Classes.Add("islandBackendTile");
        var backendIcon = new PathIcon { Width = 15, Height = 15, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        backendIcon.Bind(PathIcon.DataProperty, new DynamicResourceExtension("Icon.Sparkle"));
        backendTile.Child = backendIcon;

        _islandPanelTitle = new TextBlock { FontSize = 13, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        _islandPanelSubtitle = new TextBlock { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
        _islandPanelSubtitle.Classes.Add("meta");
        var who = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        who.Children.Add(_islandPanelTitle); who.Children.Add(_islandPanelSubtitle);

        _islandLiveMeta = new TextBlock { FontSize = 11, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        _islandLiveMeta.Classes.Add("meta");

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 10 };
        header.Children.Add(backendTile);
        Grid.SetColumn(who, 1); header.Children.Add(who);
        Grid.SetColumn(_islandLiveMeta, 2); header.Children.Add(_islandLiveMeta);

        // 磁贴网格：UniformGrid 4 列等大矩阵（Win10 磁贴语义）。
        var tiles = new Avalonia.Controls.Primitives.UniformGrid
        {
            Columns = 4,
            Margin = new Thickness(0, 14, 0, 12),
        };
        var preferred = new[] { "session.rename", "session.pin", "session.side", "session.fork",
                                "session.search", "session.share", "session.archive", "session.schedule" };
        var byId = actions.Where(a => a.Id.StartsWith("session.", StringComparison.Ordinal))
            .GroupBy(a => a.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        foreach (var action in preferred.Where(byId.ContainsKey).Select(id => byId[id]))
            tiles.Children.Add(BuildIslandTile(action));

        // 实时活动条：仅在真实活动（运行/失败/有排队/有用量）时出现；空闲整条隐藏。
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

        // 尾指：旋转菱形指向胶囊，与卡片同底色同描边。
        var tail = new Border
        {
            Width = 11, Height = 11,
            HorizontalAlignment = HorizontalAlignment.Center,
            RenderTransform = new RotateTransform(45),
            RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            Margin = new Thickness(0, 0, 0, -6),
        };
        tail.Classes.Add("islandPanelTail");

        var card = new Border { Width = 396, Padding = new Thickness(16, 14), Child = body };
        card.Classes.Add("islandPanel");

        var stack = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        stack.Children.Add(tail);
        stack.Children.Add(card);
        return stack;
    }

    private Control BuildIslandTile(Infrastructure.AppActionDefinition action)
    {
        // Win10 磁贴语义：整齐等大方格 + 居中图标 + 名称下沉 tooltip。
        var icon = new PathIcon { Width = 18, Height = 18, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        icon.Bind(PathIcon.DataProperty, new DynamicResourceExtension("Icon." + WorkbenchActionDefaults.Icon(action.Id)));
        var button = new Button
        {
            Content = icon,
            Height = 44,
            Margin = new Thickness(3),
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Command = _actions.GetCommand(action.Id),
        };
        button.Classes.Add("islandTile");
        button.FocusAdorner = null;
        var tip = string.IsNullOrEmpty(action.Description) || action.Description == action.Title
            ? action.Title : $"{action.Title}\n{action.Description}";
        ToolTip.SetTip(button, tip);
        Avalonia.Automation.AutomationProperties.SetAutomationId(button, "island-" + action.Id);
        Avalonia.Automation.AutomationProperties.SetName(button, action.Title);
        return button;
    }

    /// <summary>每秒刷新岛与展开面板的活态（由 _statusTimer.Tick 统一驱动）。</summary>
    private void RefreshIsland()
    {
        if (_islandPill is null) return;
        var vm = _vm;
        var running = vm?.StatusBar.IsRunning == true;
        var activity = vm?.StatusBar.ActivityText ?? "空闲";
        var named = vm?.Sidebar.CurrentName;
        var title = string.IsNullOrWhiteSpace(named) ? "新会话" : named!;
        var queued = vm?.StatusBar.QueueText;
        var outcome = vm?.StatusBar.OutcomeText;
        var failed = outcome?.Contains("失败") == true;
        var model = vm?.StatusBar.ModelText;
        var usage = vm?.StatusBar.UsageText;
        var hasQueue = vm?.StatusBar.HasQueue == true;

        // ── 紧凑胶囊 ──
        // 窄窗分级收缩：先丢模型，再丢用量，最后只留"点 + 名称 + 展开"。
        // 中间列本身也裁剪，三者合起来保证顶栏永不互相压字。
        var windowWidth = Bounds.Width > 0 ? Bounds.Width : Width;
        var narrow = windowWidth < 1000;
        var tighter = windowWidth < 900;
        var minimal = windowWidth < 820;
        if (_islandPill is not null)
            _islandPill.MaxWidth = Math.Max(150, windowWidth - 560);
        _islandTitle?.SetValue(TextBlock.TextProperty, title);
        _islandModel?.SetValue(TextBlock.TextProperty, ShortModelName(model));
        _islandModel?.SetValue(Visual.IsVisibleProperty, !string.IsNullOrEmpty(model) && !narrow);
        if (_islandDot is not null)
        {
            _islandDot.Classes.Remove("running"); _islandDot.Classes.Remove("failed"); _islandDot.Classes.Remove("idle");
            _islandDot.Classes.Add(running ? "running" : failed ? "failed" : "idle");
        }
        var showLive = (running || failed) && !minimal;
        _islandSep?.SetValue(Visual.IsVisibleProperty, showLive);
        _islandActivity?.SetValue(Visual.IsVisibleProperty, showLive);
        _islandElapsed?.SetValue(Visual.IsVisibleProperty, showLive);
        _islandActivity?.SetValue(TextBlock.TextProperty, activity);
        _islandElapsed?.SetValue(TextBlock.TextProperty, showLive ? "⏱ " + (DateTimeOffset.UtcNow - _turnStarted).ToString(@"mm\:ss") : "");
        var usageShort = string.IsNullOrEmpty(usage) ? "" : "⚡" + usage.Replace("会话累计：", "").Trim();
        _islandUsage?.SetValue(TextBlock.TextProperty, usageShort);
        _islandUsage?.SetValue(Visual.IsVisibleProperty, !string.IsNullOrEmpty(usage) && !tighter);

        // 卫星点：有排队时亮起并显示数量
        _islandSatellite?.SetValue(Visual.IsVisibleProperty, hasQueue);
        _islandSatCount?.SetValue(TextBlock.TextProperty, queued?.Replace("队列：", "") ?? "");

        // ── 展开面板 ──
        _islandPanelTitle?.SetValue(TextBlock.TextProperty, title);
        var project = vm?.Sidebar.ProjectName;
        var branch = _branchLabel.Replace("Git：", "");
        var parts = new List<string>(3);
        if (!string.IsNullOrEmpty(model)) parts.Add(model);
        if (!string.IsNullOrEmpty(project)) parts.Add(project);
        if (!string.IsNullOrEmpty(branch) && branch != "未查询") parts.Add(branch);
        _islandPanelSubtitle?.SetValue(TextBlock.TextProperty, string.Join(" · ", parts));
        _islandLiveMeta?.SetValue(TextBlock.TextProperty,
            (showLive ? "⏱ " + (DateTimeOffset.UtcNow - _turnStarted).ToString(@"mm\:ss") : activity) + (string.IsNullOrEmpty(usage) ? "" : "\n" + usage.Replace("会话累计：", "")));
        _islandLiveText?.SetValue(TextBlock.TextProperty,
            running ? $"{activity} · {queued ?? "执行中"}"
            : failed ? outcome
            : string.IsNullOrEmpty(usage) ? ""
            : "上次回合 " + usage.Replace("会话累计：", ""));
        if (_islandLiveDot is not null)
        {
            _islandLiveDot.Classes.Remove("running"); _islandLiveDot.Classes.Remove("failed"); _islandLiveDot.Classes.Remove("idle");
            _islandLiveDot.Classes.Add(running ? "running" : failed ? "failed" : "idle");
        }
        _islandLiveBar?.Classes.Remove("failed");
        if (failed) _islandLiveBar?.Classes.Add("failed");
        var liveBarVisible = running || failed || hasQueue || !string.IsNullOrEmpty(usage);
        _islandLiveBar?.SetValue(Visual.IsVisibleProperty, liveBarVisible);
        _islandStopButton?.SetValue(Visual.IsVisibleProperty, running);
        _islandSteerButton?.SetValue(Visual.IsVisibleProperty, running);
        if (vm is not null)
        {
            _islandStopButton?.SetValue(Button.CommandProperty, vm.Input.StopCommand);
            _islandSteerButton?.SetValue(Button.CommandProperty, vm.Input.SteerCommand);
            _islandSteerButton?.SetValue(InputElement.IsEnabledProperty, vm.Input.CanSteer);
        }
    }

    /// <summary>"vendor/model-name" → "model-name"；无斜杠原样返回。</summary>
    private static string? ShortModelName(string? model)
    {
        if (string.IsNullOrEmpty(model)) return model;
        var slash = model.IndexOf('/');
        return slash >= 0 && slash + 1 < model.Length ? model[(slash + 1)..] : model;
    }
}
