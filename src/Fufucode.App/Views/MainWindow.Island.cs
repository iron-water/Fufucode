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
    // 会话灵动岛（iOS Dynamic Island 语义）：顶栏中央的活态胶囊。
    // 紧凑态把"谁在跑、跑到哪、跑了多久、花了多少、谁在等"压成一行：
    //   ● 呼吸点(运行/空闲/失败) + 会话名 + 模型 + 状态词 + ⏱用时 + ⚡用量 + ⌄
    // 展开面板：会话头 + 磁贴网格 + 实时活动条，用裸 Popup 承载——
    // Flyout 自带 presenter 会叠一层亚克力壳产生"外圈黑边"，Popup 零壳全权自绘。
    private Border? _islandPill;
    private Border? _islandDot;
    private TextBlock? _islandTitle;
    private TextBlock? _islandModel;
    private Border? _islandSep;
    private TextBlock? _islandActivity;
    private TextBlock? _islandElapsed;
    private TextBlock? _islandUsage;
    private PathIcon? _islandChevron;
    private Border? _islandSatellite;
    private TextBlock? _islandSatCount;
    private Popup? _islandPopup;
    private TextBlock? _islandLiveTitle;
    private TextBlock? _islandLiveMeta;
    private Border? _islandLiveDot;
    private TextBlock? _islandLiveText;
    private Button? _islandStopButton;
    private Button? _islandSteerButton;
    private Border? _islandLiveBar;
    private TextBlock? _islandPanelTitle;
    private TextBlock? _islandPanelSubtitle;
    // Popup 树内画刷（Classes 样式失效，RefreshIsland 显式切色用）。
    private IBrush? _islandBrushAccent;
    private IBrush? _islandBrushCritical;
    private IBrush? _islandBrushTextDim;
    private IBrush? _islandBrushTextSecondary;
    private IBrush? _islandBrushDivider;
    private IBrush? _islandBrushSubtle;

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
        _islandTitle = _islandModel = _islandActivity = _islandElapsed = _islandUsage = _islandSatCount = null;
        _islandChevron = null;
        _islandLiveTitle = _islandLiveMeta = _islandLiveText = _islandPanelTitle = _islandPanelSubtitle = null;
        _islandLiveBar = null; _islandStopButton = _islandSteerButton = null;
        _islandPopup = null;

        if (!UseSessionIsland(settings))
        {
            // 非 Fluent：旧的居中标题回退，岛隐藏。
            CaptionBrandFallback.IsVisible = true;
            return;
        }
        CaptionBrandFallback.IsVisible = false;

        // ── 紧凑胶囊：● 名称 ⌇ 模型 │ 状态 ⏱ ⚡ ⌄ ─────────────────────
        // 每段都是独立信号源，几何/颜色变化即信息，不是装饰。
        _islandDot = new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), VerticalAlignment = VerticalAlignment.Center };
        _islandTitle = new TextBlock { FontSize = 12, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 160, TextTrimming = TextTrimming.CharacterEllipsis };
        // 模型短名：托盘语义——"当前在用什么后端"是一眼要知道的。
        _islandModel = new TextBlock { FontSize = 10.5, VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 110, TextTrimming = TextTrimming.CharacterEllipsis };
        _islandModel.Classes.Add("dim");
        _islandSep = new Border { Width = 1, Height = 14, VerticalAlignment = VerticalAlignment.Center };
        _islandActivity = new TextBlock { FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 200, TextTrimming = TextTrimming.CharacterEllipsis };
        _islandElapsed = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        _islandElapsed.Classes.Add("dim");
        // 用量：用量是持续状态，不只运行时才看。
        _islandUsage = new TextBlock { FontSize = 10.5, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 80, TextTrimming = TextTrimming.CharacterEllipsis };
        _islandUsage.Classes.Add("dim");
        _islandChevron = new PathIcon { Width = 10, Height = 10, VerticalAlignment = VerticalAlignment.Center };
        _islandChevron.Bind(PathIcon.DataProperty, new DynamicResourceExtension("Icon.ChevronDown"));

        var pillContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, VerticalAlignment = VerticalAlignment.Center };
        pillContent.Children.Add(_islandDot);
        pillContent.Children.Add(_islandTitle);
        pillContent.Children.Add(_islandModel);
        pillContent.Children.Add(_islandSep);
        pillContent.Children.Add(_islandActivity);
        pillContent.Children.Add(_islandElapsed);
        pillContent.Children.Add(_islandUsage);
        pillContent.Children.Add(_islandChevron);

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
            if (_islandPopup is not null) _islandPopup.IsOpen = !_islandPopup.IsOpen;
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

        // ── 展开面板：裸 Popup 承载，零系统壳——Flyout 的 presenter 亚克力壳
        // 是"浅灰底外圈黑边"的根因；Popup 只渲染我们给的 islandPanel 一张卡。
        var panel = BuildIslandPanel(actions);
        _islandPopup = new Popup
        {
            Child = panel,
            PlacementTarget = _islandPill,
            Placement = PlacementMode.Bottom,
            IsLightDismissEnabled = true,
        };
        _islandPopup.Opened += (_, _) => RefreshIsland();

        RefreshIsland();
    }

    private Control BuildIslandPanel(IEnumerable<Infrastructure.AppActionDefinition> actions)
    {
        // Popup 是独立 top-level 视觉树：窗口级 Styles（Border.islandPanel 等 class selector）
        // 根本不作用在 popup 树内——一切样式必须显式赋值，不能用 Classes 匹配。
        // 主窗 Resources 里拿实体画刷/圆角/阴影，硬灌给 popup 内控件。
        var menuSurface = Res<IBrush>("MenuSurfaceBrush") ?? new SolidColorBrush(Color.Parse("#FF2C2C2C"));
        var cardStroke = Res<IBrush>("AppCardStroke") ?? new SolidColorBrush(Color.Parse("#3DFFFFFF"));
        var subtleBg = Res<IBrush>("AppSubtleBackground") ?? new SolidColorBrush(Color.Parse("#0DFFFFFF"));
        var hoverBg = Res<IBrush>("AppHover") ?? new SolidColorBrush(Color.Parse("#1AFFFFFF"));
        var textPrimary = Res<IBrush>("AppTextPrimary") ?? Brushes.White;
        var textSecondary = Res<IBrush>("AppTextSecondary") ?? new SolidColorBrush(Color.Parse("#B3FFFFFF"));
        var textDim = Res<IBrush>("AppTextDim") ?? new SolidColorBrush(Color.Parse("#80FFFFFF"));
        var accent = Res<IBrush>("AppAccent") ?? new SolidColorBrush(Color.Parse("#FF4C9BE8"));
        var accentText = Res<IBrush>("AppAccentText") ?? new SolidColorBrush(Color.Parse("#FF4C9BE8"));
        var dividerSubtle = Res<IBrush>("AppDividerSubtle") ?? new SolidColorBrush(Color.Parse("#14FFFFFF"));
        var criticalFg = Res<IBrush>("AppCriticalForeground") ?? new SolidColorBrush(Color.Parse("#FFFF99A4"));
        // 值类型资源用 object 取再 unbox；引用类型走泛型 Res<T>。
        var flyoutShadow = ResObj("FluentFlyoutShadow") as BoxShadows?;
        var overlayRadius = ResObj("OverlayCornerRadius") as CornerRadius? ?? new CornerRadius(8);
        var controlRadius = ResObj("ControlCornerRadius") as CornerRadius? ?? new CornerRadius(4);
        // 存字段给 RefreshIsland 显式切色。
        _islandBrushAccent = accent;
        _islandBrushCritical = criticalFg;
        _islandBrushTextDim = textDim;
        _islandBrushTextSecondary = textSecondary;
        _islandBrushDivider = dividerSubtle;
        _islandBrushSubtle = subtleBg;

        // 头部：subtle 底 + accent 描边的小方块 + 标题 + 副标，右侧实时元信息。
        var backendTile = new Border
        {
            Width = 30, Height = 30, CornerRadius = new CornerRadius(7),
            VerticalAlignment = VerticalAlignment.Center,
            Background = subtleBg,
            BorderBrush = accent,
            BorderThickness = new Thickness(1),
        };
        var backendIcon = new PathIcon
        {
            Width = 15, Height = 15,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Foreground = accentText,
        };
        // Popup 树里 DynamicResource 找不到 Icon.*，显式从主窗取 StreamGeometry。
        if (TryGetResource("Icon.Sparkle", ActualThemeVariant, out var sparkleGeo) && sparkleGeo is Geometry sg)
            backendIcon.Data = sg;
        else if (Avalonia.Application.Current?.TryGetResource("Icon.Sparkle", ActualThemeVariant, out var appSg) == true && appSg is Geometry asg)
            backendIcon.Data = asg;
        backendTile.Child = backendIcon;

        _islandPanelTitle = new TextBlock
        {
            FontSize = 13, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = textPrimary,
        };
        _islandPanelSubtitle = new TextBlock
        {
            FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = textDim,
        };
        var who = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        who.Children.Add(_islandPanelTitle); who.Children.Add(_islandPanelSubtitle);

        _islandLiveMeta = new TextBlock
        {
            FontSize = 11, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
            Foreground = textDim,
        };

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 10 };
        header.Children.Add(backendTile);
        Grid.SetColumn(who, 1); header.Children.Add(who);
        Grid.SetColumn(_islandLiveMeta, 2); header.Children.Add(_islandLiveMeta);

        // 磁贴网格：UniformGrid 4 列等大矩阵。
        var tiles = new Avalonia.Controls.Primitives.UniformGrid
        {
            Columns = 4,
            Margin = new Thickness(0, 14, 0, 12),
        };
        var preferred = new[] { "session.rename", "session.pin", "session.side", "session.fork",
                                "session.search", "session.share", "session.archive", "session.schedule" };
        var allActions = actions.ToList();
        var byId = allActions.Where(a => a.Id.StartsWith("session.", StringComparison.Ordinal))
            .GroupBy(a => a.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        foreach (var action in preferred.Where(byId.ContainsKey).Select(id => byId[id]))
            tiles.Children.Add(BuildIslandTile(action, subtleBg, cardStroke, hoverBg, textSecondary, textPrimary, controlRadius));

        // 实时活动条：仅在真实活动（运行/失败/有排队/有用量）时出现；空闲整条隐藏。
        _islandLiveDot = new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), VerticalAlignment = VerticalAlignment.Center };
        _islandLiveText = new TextBlock
        {
            FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = textSecondary,
        };
        _islandSteerButton = new Button
        {
            Content = "插入", FontSize = 11, Padding = new Thickness(10, 4), MinHeight = 0,
            Background = hoverBg, BorderBrush = cardStroke, BorderThickness = new Thickness(1),
            Foreground = textPrimary, CornerRadius = controlRadius,
        };
        _islandStopButton = new Button
        {
            Content = "停止", FontSize = 11, Padding = new Thickness(10, 4), MinHeight = 0,
            Background = hoverBg, BorderBrush = cardStroke, BorderThickness = new Thickness(1),
            Foreground = textPrimary, CornerRadius = controlRadius,
        };
        var liveRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), ColumnSpacing = 8 };
        liveRow.Children.Add(_islandLiveDot);
        Grid.SetColumn(_islandLiveText, 1); liveRow.Children.Add(_islandLiveText);
        Grid.SetColumn(_islandSteerButton, 2); liveRow.Children.Add(_islandSteerButton);
        Grid.SetColumn(_islandStopButton, 3); liveRow.Children.Add(_islandStopButton);
        _islandLiveBar = new Border
        {
            Child = liveRow,
            Background = subtleBg,
            BorderBrush = dividerSubtle,
            BorderThickness = new Thickness(1),
            CornerRadius = controlRadius,
            Padding = new Thickness(10, 8),
        };

        var body = new StackPanel();
        body.Children.Add(header);
        body.Children.Add(tiles);
        body.Children.Add(_islandLiveBar);

        // 尾指 + 卡：显式赋主窗画刷/圆角/阴影。
        var tail = new Border
        {
            Width = 11, Height = 11,
            HorizontalAlignment = HorizontalAlignment.Center,
            RenderTransform = new RotateTransform(45),
            RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            Margin = new Thickness(0, 0, 0, -6),
            Background = menuSurface,
            BorderBrush = cardStroke,
            BorderThickness = new Thickness(1),
        };

        var card = new Border
        {
            Width = 396, Padding = new Thickness(16, 14), Child = body,
            Background = menuSurface,
            BorderBrush = cardStroke,
            BorderThickness = new Thickness(1),
            CornerRadius = overlayRadius,
        };
        if (flyoutShadow.HasValue) card.BoxShadow = flyoutShadow.Value;

        var inner = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        inner.Children.Add(tail);
        inner.Children.Add(card);
        return inner;

        // 主窗 resource lookup：先主窗，拿不到再 Application。
        T? Res<T>(string key) where T : class
        {
            if (TryGetResource(key, ActualThemeVariant, out var res) && res is T t) return t;
            var app = Avalonia.Application.Current;
            if (app is not null && app.TryGetResource(key, ActualThemeVariant, out var appRes) && appRes is T at) return at;
            return null;
        }
        object? ResObj(string key)
        {
            if (TryGetResource(key, ActualThemeVariant, out var res)) return res;
            var app = Avalonia.Application.Current;
            if (app is not null && app.TryGetResource(key, ActualThemeVariant, out var appRes)) return appRes;
            return null;
        }
    }

    private Control BuildIslandTile(Infrastructure.AppActionDefinition action,
        IBrush subtleBg, IBrush cardStroke, IBrush hoverBg, IBrush textSecondary, IBrush textPrimary, CornerRadius radius)
    {
        // Win10 磁贴语义：整齐等大方格 + 居中图标 + 名称下沉 tooltip。
        // Popup 树里 class selector/资源 lookup 都不生效，画刷与图标几何全部显式赋。
        var iconKey = "Icon." + WorkbenchActionDefaults.Icon(action.Id);
        Geometry? iconData = null;
        if (TryGetResource(iconKey, ActualThemeVariant, out var geo) && geo is Geometry g)
            iconData = g;
        else if (Avalonia.Application.Current?.TryGetResource(iconKey, ActualThemeVariant, out var appGeo) == true && appGeo is Geometry ag)
            iconData = ag;
        var icon = new PathIcon
        {
            Width = 18, Height = 18,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Foreground = textSecondary,
            Data = iconData,
        };
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
            Background = subtleBg,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(1),
            CornerRadius = radius,
            Foreground = textPrimary,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };
        // Hover 用 VisualState 在 popup 里同样不工作——PointerEntered/Exited 手动切背景。
        button.PointerEntered += (_, _) => { button.Background = hoverBg; button.BorderBrush = cardStroke; icon.Foreground = textPrimary; };
        button.PointerExited += (_, _) => { button.Background = subtleBg; button.BorderBrush = Brushes.Transparent; icon.Foreground = textSecondary; };
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
        // 标题：已命名会话用 CurrentName（真实 Name）；未命名不显示裸 hex ID，用「新会话」占位。
        var named = vm?.Sidebar.CurrentName;
        var title = string.IsNullOrWhiteSpace(named) ? "新会话" : named!;
        var queued = vm?.StatusBar.QueueText;
        var outcome = vm?.StatusBar.OutcomeText;
        var failed = outcome?.Contains("失败") == true;
        var model = vm?.StatusBar.ModelText;
        var usage = vm?.StatusBar.UsageText;
        var hasQueue = vm?.StatusBar.HasQueue == true;

        // ── 紧凑胶囊：每段按状态显隐，不是装饰是信号 ──
        _islandTitle?.SetValue(TextBlock.TextProperty, title);
        // 模型短名：取 "vendor/model" 的 model 段，压暗色——常驻状态。
        _islandModel?.SetValue(TextBlock.TextProperty, ShortModelName(model));
        _islandModel?.SetValue(Visual.IsVisibleProperty, !string.IsNullOrEmpty(model));
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
        // 用量段：有用量数据才显示。
        var usageShort = string.IsNullOrEmpty(usage) ? "" : "⚡" + usage.Replace("会话累计：", "").Trim();
        _islandUsage?.SetValue(TextBlock.TextProperty, usageShort);
        _islandUsage?.SetValue(Visual.IsVisibleProperty, !string.IsNullOrEmpty(usage));

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
        // Popup 树内 Classes 失效——dot/liveBar 显式切画刷。
        if (_islandLiveDot is not null)
        {
            _islandLiveDot.Background = running ? _islandBrushAccent
                : failed ? _islandBrushCritical
                : _islandBrushTextDim;
        }
        if (_islandLiveBar is not null)
        {
            _islandLiveBar.BorderBrush = failed ? _islandBrushCritical : _islandBrushDivider;
            if (_islandLiveText is not null)
                _islandLiveText.Foreground = failed ? _islandBrushCritical : _islandBrushTextSecondary;
        }
        // 空闲整条隐藏：活动条只在 running/failed/有排队/有用量 时出现。
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
