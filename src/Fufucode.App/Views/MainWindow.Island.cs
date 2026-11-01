using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.VisualTree;
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
    private Avalonia.Controls.Shapes.Path? _islandBackend;
    private Border? _islandBackendCircle;
    private Border? _islandSatellite;
    private TextBlock? _islandSatCount;
    private TextBlock? _islandLiveMeta;
    private Border? _islandLiveDot;
    private TextBlock? _islandLiveText;
    private Button? _islandStopButton;
    private Button? _islandSteerButton;
    private Panel? _statusVisualHost;
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
        if (!_islandMotionInitialized)
        {
            _islandMotionInitialized = true;
            IslandOverlayHost.RenderTransform = _islandScale;
            IslandOverlayHost.RenderTransformOrigin = new RelativePoint(0.5, 0, RelativeUnit.Relative);
            AddHandler(KeyDownEvent, (_, e) =>
            {
                if (e.Key != Key.Escape || !_islandExpanded) return;
                SetIslandExpanded(false);
                e.Handled = true;
            }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            Closed += (_, _) => SetIslandExpanded(false, animate: false);
            IslandOverlay.DetachedFromVisualTree += (_, _) => SetIslandExpanded(false, animate: false);
            PropertyChanged += (_, change) =>
            {
                if (change.Property == IsVisibleProperty && !IsVisible)
                    SetIslandExpanded(false, animate: false);
            };
        }
        SetIslandExpanded(false, animate: false);
        IslandHost.Children.Clear();
        IslandOverlayHost.Content = null;
        IslandOverlay.IsVisible = false;
        _islandPill = null;
        _islandDot = _islandSatellite = _islandLiveDot = null;
        _islandTitle = _islandModel = _islandSatCount = null;
        _islandBackend = null;
        _islandBackendCircle = null;
        _islandLiveMeta = _islandLiveText = _islandPanelTitle = _islandPanelSubtitle = null;
        _statusVisualHost = null;
        _islandStopButton = _islandSteerButton = null;

        if (!UseSessionIsland(settings))
        {
            CaptionBrandFallback.IsVisible = true;
            return;
        }
        CaptionBrandFallback.IsVisible = false;

        // ── 紧凑胶囊：● 后端图标 · 会话名 · 模型 ⌄ ─────────────────────
        // 状态点=会话状态；后端图标=当前内核（形状区分，不靠颜色）；末位展开。
        _islandDot = new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(5), VerticalAlignment = VerticalAlignment.Center };
        // 后端标识：非 FUMO 用形状图标；FUMO 暂用"圆框 F"占位，后续统一视觉资产。
        _islandBackend = new Avalonia.Controls.Shapes.Path
        {
            Width = 16, Height = 16, Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _islandBackend.Bind(Avalonia.Controls.Shapes.Shape.FillProperty, new DynamicResourceExtension("AppTextSecondary"));
        var fumoLetter = new TextBlock { Text = "F", FontSize = 10.5, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        fumoLetter.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("AppTextSecondary"));
        _islandBackendCircle = new Border
        {
            Width = 18, Height = 18, CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(1.2),
            VerticalAlignment = VerticalAlignment.Center, Child = fumoLetter, IsVisible = false,
        };
        _islandBackendCircle.Bind(Border.BorderBrushProperty, new DynamicResourceExtension("AppTextSecondary"));
        var backendHost = new Panel { VerticalAlignment = VerticalAlignment.Center, Children = { _islandBackendCircle, _islandBackend } };
        _islandTitle = new TextBlock { FontSize = 14, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 220, TextTrimming = TextTrimming.CharacterEllipsis };
        _islandModel = new TextBlock { FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 170, TextTrimming = TextTrimming.CharacterEllipsis };
        _islandModel.Classes.Add("dim");
        // 会话岛不再显示下拉箭头（用户 2026-10-29 要求删除，后续可能在该位置放别的图标）。
        // 需要新图标时在这里追加到 pillContent，不要再恢复 chevron。

        var pillContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        pillContent.Children.Add(_islandDot);
        pillContent.Children.Add(backendHost);
        pillContent.Children.Add(_islandTitle);
        pillContent.Children.Add(_islandModel);

        // 胶囊为 Border：PointerPressed 标记 handled，顶栏 PointerPressed→BeginMoveDrag
        // 的冒泡监听不会触发（默认不订阅 handledEventsToo），点击岛不误判为拖窗。
        _islandPill = new Border
        {
            Child = pillContent,
            Padding = new Thickness(18, 0),
            Height = 48,
            CornerRadius = new CornerRadius(24),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };
        _islandPill.Classes.Add("sessionIsland");
        Avalonia.Automation.AutomationProperties.SetAutomationId(_islandPill, "session-island");
        Avalonia.Automation.AutomationProperties.SetName(_islandPill, "会话岛");
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

        IslandOverlayHost.Content = BuildIslandPanel();

        RefreshIsland();
    }

    private bool _islandExpanded;
    private int _islandMotionVersion;
    private double _islandProgress;
    private readonly ScaleTransform _islandScale = new(0.97, 0.97);
    private bool _islandMotionInitialized;

    private void ToggleIslandPanel() => SetIslandExpanded(!_islandExpanded);

    private void IslandScrim_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        SetIslandExpanded(false);

    private void SetIslandExpanded(bool expanded, bool animate = true)
    {
        _islandExpanded = expanded;
        var version = ++_islandMotionVersion;
        var from = _islandProgress;
        var target = expanded ? 1d : 0d;
        IslandOverlay.IsVisible = true;
        IslandOverlayHost.IsHitTestVisible = expanded;
        void Apply(double progress)
        {
            _islandProgress = progress;
            IslandOverlayHost.Opacity = progress;
            _islandScale.ScaleX = _islandScale.ScaleY = 0.97 + 0.03 * progress;
        }
        var reduced = Views.Motion.Reduced || Integration.MemoryProfile.DisableVisualEffects;
        if (!animate || reduced || Math.Abs(target - from) < 0.001)
        {
            Apply(target);
            IslandOverlay.IsVisible = expanded;
            return;
        }
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var duration = (expanded ? Views.Motion.SlowDuration : Views.Motion.StandardDuration).TotalMilliseconds;
        void Frame(TimeSpan _)
        {
            if (version != _islandMotionVersion) return;
            var progress = Views.Motion.Reduced || Integration.MemoryProfile.DisableVisualEffects
                ? 1 : Math.Clamp(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds / duration, 0, 1);
            Apply(from + (target - from) * (1 - Math.Pow(1 - progress, 3)));
            if (progress < 1) RequestAnimationFrame(Frame);
            else IslandOverlay.IsVisible = _islandExpanded;
        }
        RequestAnimationFrame(Frame);
    }

    private Control BuildIslandPanel()
    {
        // ── 卡 1：状态卡（默认卡，永远存在）──────────────────────────
        // 标题 + 副标（模型 · 项目 · 分支）常驻；右侧是实时元信息。
        _islandPanelTitle = new TextBlock { FontSize = 14, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        _islandPanelSubtitle = new TextBlock { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
        _islandPanelSubtitle.Classes.Add("meta");
        var who = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        who.Children.Add(_islandPanelTitle);
        who.Children.Add(_islandPanelSubtitle);

        _islandLiveMeta = new TextBlock { FontSize = 11, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        _islandLiveMeta.Classes.Add("meta");

        var statusHeader = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 10 };
        statusHeader.Children.Add(who);
        Grid.SetColumn(_islandLiveMeta, 1); statusHeader.Children.Add(_islandLiveMeta);

        // 状态可视区：暂时只放"状态点 + 一句话状态"，后续替换为动画/图表。
        _islandLiveDot = new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), VerticalAlignment = VerticalAlignment.Center };
        _islandLiveDot.Classes.Add("islandStatusDot");
        _islandLiveText = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        _islandLiveText.Classes.Add("meta");
        var statusPlaceholder = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 8, VerticalAlignment = VerticalAlignment.Center };
        statusPlaceholder.Children.Add(_islandLiveDot);
        Grid.SetColumn(_islandLiveText, 1); statusPlaceholder.Children.Add(_islandLiveText);

        _statusVisualHost = new Panel { Height = 64, Children = { statusPlaceholder } };
        // TODO(状态卡设计)：这里放动画/图表，见后续讨论

        var statusBody = new StackPanel { Spacing = 8 };
        statusBody.Children.Add(statusHeader);
        statusBody.Children.Add(_statusVisualHost);
        var statusCard = new Border { Child = statusBody };
        statusCard.Classes.Add("islandWidget");

        // ── 卡 2：会话管理卡（整卡可点，箭头仅装饰）──────────────────
        var manageTitle = new TextBlock { Text = "会话", FontSize = 13, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        var manageChevron = new PathIcon { Width = 12, Height = 12, VerticalAlignment = VerticalAlignment.Center };
        manageChevron.Bind(PathIcon.DataProperty, new DynamicResourceExtension("Icon.ChevronRight"));
        manageChevron.Bind(PathIcon.ForegroundProperty, new DynamicResourceExtension("AppTextDim"));
        var manageHeader = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        manageHeader.Children.Add(manageTitle);
        Grid.SetColumn(manageChevron, 1); manageHeader.Children.Add(manageChevron);

        var manageButtons = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var (id, icon, tip) in new[]
                 {
                     ("session.rename", "Icon.Edit", "重命名"),
                     ("session.pin", "Icon.Pin", "置顶"),
                     ("session.side", "Icon.Chat", "新建侧边聊天"),
                     ("session.fork", "Icon.Branch", "分叉"),
                     ("session.schedule", "Icon.Calendar", "添加计划任务"),
                     ("toggle.left", "Icon.PanelLeft", "打开侧栏"),
                 })
            manageButtons.Children.Add(BuildIslandManagementButton(id, icon, tip));

        var manageBody = new StackPanel { Spacing = 8 };
        manageBody.Children.Add(manageHeader);
        manageBody.Children.Add(manageButtons);
        var manageCard = new Border
        {
            Child = manageBody,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };
        manageCard.Classes.Add("islandWidget");
        // 整卡作为"会话"入口；点击落在内部图标按钮上时由按钮自己处理。
        manageCard.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton != Avalonia.Input.MouseButton.Left) return;
            if (e.Source is Visual source && source.FindAncestorOfType<Button>(includeSelf: true) is not null) return;
            _actions.GetCommand("toggle.left").Execute(null);
            e.Handled = true;
        };

        // ── 卡 3：快捷磁贴行（插入 / 停止 / 新建会话 / 更多 + 设置）──
        _islandSteerButton = BuildIslandTileButton("Icon.Send", "插入");
        _islandStopButton = BuildIslandTileButton("Icon.RecordStop", "停止");
        var newSessionTile = BuildIslandTileButton("Icon.Add", "新建会话", "session.new");
        var moreTile = BuildIslandTileButton("Icon.More", "更多", "commands");
        var settingsTile = BuildIslandTileButton("Icon.Settings", "设置", "settings", 36);
        var quickRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,Auto,*,Auto"), ColumnSpacing = 8 };
        quickRow.Children.Add(_islandSteerButton);
        Grid.SetColumn(_islandStopButton, 1); quickRow.Children.Add(_islandStopButton);
        Grid.SetColumn(newSessionTile, 2); quickRow.Children.Add(newSessionTile);
        Grid.SetColumn(moreTile, 3); quickRow.Children.Add(moreTile);
        Grid.SetColumn(settingsTile, 5); quickRow.Children.Add(settingsTile);
        var quickCard = new Border { Child = quickRow };
        quickCard.Classes.Add("islandWidget");

        // ── 竖向堆叠：同宽同边距，卡片间距 10 ───────────────────────
        var widgets = new StackPanel { Spacing = 10 };
        widgets.Children.Add(statusCard);
        widgets.Children.Add(manageCard);
        widgets.Children.Add(quickCard);

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

        var card = new Border { Width = 396, Padding = new Thickness(12), Child = widgets };
        card.Classes.Add("islandPanel");

        var stack = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        stack.Children.Add(tail);
        stack.Children.Add(card);
        return stack;
    }

    /// <summary>会话管理卡里的纯图标按钮：命令复用动作注册表，文字只进 ToolTip。</summary>
    private Button BuildIslandManagementButton(string actionId, string iconKey, string toolTip)
    {
        var icon = new PathIcon { Width = 16, Height = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        icon.Bind(PathIcon.DataProperty, new DynamicResourceExtension(iconKey));
        var button = new Button
        {
            Content = icon,
            Width = 38,
            Height = 36,
            Margin = new Thickness(2),
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Command = _actions.GetCommand(actionId),
            FocusAdorner = null,
        };
        button.Classes.Add("islandTile");
        ToolTip.SetTip(button, toolTip);
        Avalonia.Automation.AutomationProperties.SetAutomationId(button, "island-action-" + actionId);
        Avalonia.Automation.AutomationProperties.SetName(button, toolTip);
        return button;
    }

    /// <summary>快捷磁贴：56×56 纯图标方块；命令可为空（插入/停止由 RefreshIsland 绑定）。</summary>
    private Button BuildIslandTileButton(string iconKey, string toolTip, string? actionId = null, double size = 56)
    {
        var icon = new PathIcon
        {
            Width = size >= 56 ? 20 : 16,
            Height = size >= 56 ? 20 : 16,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        icon.Bind(PathIcon.DataProperty, new DynamicResourceExtension(iconKey));
        var button = new Button
        {
            Content = icon,
            Width = size,
            Height = size,
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            FocusAdorner = null,
        };
        if (actionId is not null) button.Command = _actions.GetCommand(actionId);
        button.Classes.Add("islandTileSmall");
        ToolTip.SetTip(button, toolTip);
        Avalonia.Automation.AutomationProperties.SetAutomationId(button, "island-tile-" + toolTip);
        Avalonia.Automation.AutomationProperties.SetName(button, toolTip);
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
        if (_islandPill is not null)
            _islandPill.MaxWidth = Math.Max(170, windowWidth - 600);
        _islandTitle?.SetValue(TextBlock.TextProperty, title);
        _islandModel?.SetValue(TextBlock.TextProperty, ShortModelName(model));
        _islandModel?.SetValue(Visual.IsVisibleProperty, !string.IsNullOrEmpty(model) && !narrow);
        if (_islandDot is not null)
        {
            _islandDot.Classes.Remove("running"); _islandDot.Classes.Remove("failed"); _islandDot.Classes.Remove("idle");
            _islandDot.Classes.Add(running ? "running" : failed ? "failed" : "idle");
        }
        var showLive = running || failed;
        UpdateIslandBackendIcon(vm?.CurrentBackendId, vm?.CurrentBackendName);

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
        _islandPanelSubtitle?.SetValue(TextBlock.TextProperty, string.Join("  ", parts));
        _islandLiveMeta?.SetValue(TextBlock.TextProperty,
            (showLive ? "⏱ " + (DateTimeOffset.UtcNow - _turnStarted).ToString(@"mm\:ss") : activity) + (string.IsNullOrEmpty(usage) ? "" : "\n" + usage.Replace("会话累计：", "")));
        _islandLiveText?.SetValue(TextBlock.TextProperty,
            running ? $"{activity} · {queued ?? "执行中"}"
            : failed ? outcome
            : string.IsNullOrEmpty(usage) ? activity
            : "上次回合 " + usage.Replace("会话累计：", ""));
        if (_islandLiveDot is not null)
        {
            _islandLiveDot.Classes.Remove("running"); _islandLiveDot.Classes.Remove("failed"); _islandLiveDot.Classes.Remove("idle");
            _islandLiveDot.Classes.Add(running ? "running" : failed ? "failed" : "idle");
        }
        // 磁贴常驻：停止只在运行中可用；插入沿用现有"运行中 + 可插入"门控。
        _islandStopButton?.SetValue(InputElement.IsEnabledProperty, running);
        if (vm is not null)
        {
            _islandStopButton?.SetValue(Button.CommandProperty, vm.Input.StopCommand);
            _islandSteerButton?.SetValue(Button.CommandProperty, vm.Input.SteerCommand);
            _islandSteerButton?.SetValue(InputElement.IsEnabledProperty, running && vm.Input.CanSteer);
        }
        else
        {
            _islandSteerButton?.SetValue(InputElement.IsEnabledProperty, false);
        }
    }

    /// <summary>先查主窗资源，再回落 Application（图标表挂在应用级样式）。</summary>
    private Geometry? FindGeometry(string key)
    {
        if (TryGetResource(key, ActualThemeVariant, out var local) && local is Geometry localGeometry)
            return localGeometry;
        var app = Avalonia.Application.Current;
        if (app is not null && app.TryGetResource(key, ActualThemeVariant, out var shared) && shared is Geometry sharedGeometry)
            return sharedGeometry;
        return null;
    }

    /// <summary>胶囊里的后端图标：按内核选形状，不依赖颜色区分。</summary>
    private void UpdateIslandBackendIcon(string? backendId, string? backendName)
    {
        if (_islandBackend is null) return;
        var isFumo = string.IsNullOrWhiteSpace(backendId) || backendId == "fumo";
        // FUMO：圆框 F 占位；其它后端：形状图标（官方高清图标资产未入库，先用图标表近似）。
        if (_islandBackendCircle is not null) _islandBackendCircle.IsVisible = isFumo;
        _islandBackend.IsVisible = !isFumo;
        if (!isFumo)
        {
            var key = backendId switch
            {
                "pi" => "Icon.PlugConnected",
                "omp" => "Icon.Speedometer",
                "codex" => "Icon.Code",
                "dsh" => "Icon.Globe",
                "claude" => "Icon.Brain",
                _ => "Icon.Sparkle",
            };
            _islandBackend.SetValue(Avalonia.Controls.Shapes.Path.DataProperty, FindGeometry(key));
        }
        Avalonia.Automation.AutomationProperties.SetName(_islandBackendCircle ?? (Control)_islandBackend,
            "后端：" + (string.IsNullOrWhiteSpace(backendName) ? "FUMO" : backendName));
    }

    /// <summary>"vendor/model-name" → "model-name"；无斜杠原样返回。</summary>
    private static string? ShortModelName(string? model)
    {
        if (string.IsNullOrEmpty(model)) return model;
        var slash = model.IndexOf('/');
        return slash >= 0 && slash + 1 < model.Length ? model[(slash + 1)..] : model;
    }
}
