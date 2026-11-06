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
    private Avalonia.Controls.Shapes.Path? _islandBackend;
    private Border? _islandBackendCircle;
    private Border? _islandSatellite;
    private TextBlock? _islandSatCount;
    private TextBlock? _islandLiveMeta;
    private Border? _islandLiveDot;
    private TextBlock? _islandLiveText;
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
            CornerRadius = new CornerRadius(14),
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
        // ── 卡 1：状态占位 ──────────────────────────────────────
        _islandPanelTitle = new TextBlock { FontSize = 14, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        _islandPanelSubtitle = new TextBlock { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
        _islandPanelSubtitle.Classes.Add("meta");
        var who = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        who.Children.Add(_islandPanelTitle);
        who.Children.Add(_islandPanelSubtitle);

        _islandLiveMeta = new TextBlock { FontSize = 12, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        _islandLiveMeta.Classes.Add("meta");
        var statusHeader = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 10 };
        statusHeader.Children.Add(who);
        Grid.SetColumn(_islandLiveMeta, 1); statusHeader.Children.Add(_islandLiveMeta);

        _islandLiveDot = new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), VerticalAlignment = VerticalAlignment.Center };
        _islandLiveDot.Classes.Add("islandStatusDot");
        _islandLiveText = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        _islandLiveText.Classes.Add("meta");
        var statusPlaceholder = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 8, VerticalAlignment = VerticalAlignment.Bottom };
        statusPlaceholder.Children.Add(_islandLiveDot);
        Grid.SetColumn(_islandLiveText, 1); statusPlaceholder.Children.Add(_islandLiveText);

        var waveform = new Views.IslandWaveform { Height = 48 };
        Avalonia.Automation.AutomationProperties.SetAutomationId(waveform, "island-waveform");
        // TODO(真实吞吐)：当前用运行活跃度驱动，接 PerfTracer 后改为 token/s
        _statusVisualHost = new Panel
        {
            Height = 48,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Children = { waveform },
        };
        var statusBody = new Grid { RowDefinitions = new RowDefinitions("Auto,48,Auto"), RowSpacing = 8 };
        statusBody.Children.Add(statusHeader);
        Grid.SetRow(_statusVisualHost, 1); statusBody.Children.Add(_statusVisualHost);
        Grid.SetRow(statusPlaceholder, 2); statusBody.Children.Add(statusPlaceholder);
        var statusCard = new Border { Tag = "状态", Child = statusBody };
        statusCard.Classes.Add("islandWidget");

        // ── 卡 2：会话占位 ──────────────────────────────────────
        var sessionLabel = new TextBlock { Text = "卡片二", FontSize = 14, VerticalAlignment = VerticalAlignment.Top };
        sessionLabel.Classes.Add("meta");
        var refreshIcon = new PathIcon { Width = 14, Height = 14, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        refreshIcon.Bind(PathIcon.DataProperty, new DynamicResourceExtension("Icon.ArrowClockwise"));
        refreshIcon.Bind(PathIcon.ForegroundProperty, new DynamicResourceExtension("AppTextDim"));
        // 刷新仅作视觉占位，不接命令。
        var refreshPlaceholder = new Border
        {
            Width = 24, Height = 24, CornerRadius = new CornerRadius(12),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Child = refreshIcon,
        };
        refreshPlaceholder.Bind(Border.BackgroundProperty, new DynamicResourceExtension("AppSubtleBackground"));
        var sessionBody = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        sessionBody.Children.Add(sessionLabel);
        Grid.SetColumn(refreshPlaceholder, 1); sessionBody.Children.Add(refreshPlaceholder);
        var sessionCard = new Border { Height = 72, Tag = "会话", Child = sessionBody };
        sessionCard.Classes.Add("islandWidget");

        // ── 卡 3：任务占位 ──────────────────────────────────────
        var taskLabel = new TextBlock { Text = "Graph 项目地图", FontSize = 14, VerticalAlignment = VerticalAlignment.Top };
        taskLabel.Classes.Add("meta");
        var graphEntry = new Button { Content = taskLabel, Command = _actions.GetCommand("graph"),
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
        Avalonia.Automation.AutomationProperties.SetAutomationId(graphEntry, "island-graph");
        var taskCard = new Border { Height = 72, Tag = "Graph", Child = graphEntry };
        taskCard.Classes.Add("islandWidget");

        // 候选卡片，用户后续拖/点加入上方三卡。
        var candidates = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        for (var i = 0; i < 4; i++)
        {
            var addIcon = new PathIcon { Width = 20, Height = 20 };
            addIcon.Bind(PathIcon.DataProperty, new DynamicResourceExtension("Icon.Add"));
            var tile = new Button { Width = 56, Height = 56, Content = addIcon, FocusAdorner = null };
            tile.Classes.Add("islandTileSmall");
            Avalonia.Automation.AutomationProperties.SetName(tile, "候选卡片" + (i + 1));
            candidates.Children.Add(tile);
        }

        var gearIcon = new PathIcon { Width = 16, Height = 16 };
        gearIcon.Bind(PathIcon.DataProperty, new DynamicResourceExtension("Icon.Settings"));
        var gear = new Button
        {
            Width = 36, Height = 36, Content = gearIcon, FocusAdorner = null,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        gear.Classes.Add("islandGear");
        ToolTip.SetTip(gear, "岛设置");
        Avalonia.Automation.AutomationProperties.SetName(gear, "岛设置");
        var bottomRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        bottomRow.Children.Add(candidates);
        Grid.SetColumn(gear, 1); bottomRow.Children.Add(gear);

        // 竖向顺序：三张展示卡（间距 10）→ 候选磁贴行（间距 8，右下角岛设置）。
        var cards = new StackPanel { Spacing = 10 };
        cards.Children.Add(statusCard);
        cards.Children.Add(sessionCard);
        cards.Children.Add(taskCard);
        bottomRow.Margin = new Thickness(0, 8, 0, 0);
        var widgets = new StackPanel { Spacing = 0 };
        widgets.Children.Add(cards);
        widgets.Children.Add(bottomRow);

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
        if (vm?.IsGraphPage == true && _graphWorkspace is { } graph)
        {
            var snapshot = graph.Model.Snapshot;
            title = snapshot.Title;
            activity = graph.Model.ProjectStatus;
            running = snapshot.Runs.Any(run => run.Status is "Running" or "Queued" or "Stopping");
            model = "Graph / " + activity;
            failed = snapshot.Runs.Any(run => run.Status == "Unknown");
            usage = null;
            hasQueue = false;
        }

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
        if (_statusVisualHost?.Children is { Count: > 0 } visuals && visuals[0] is Views.IslandWaveform waveform)
        {
            var waiting = activity.Contains("等待审批", StringComparison.Ordinal) ||
                activity.Contains("等待授权", StringComparison.Ordinal) ||
                activity.Contains("等待确认", StringComparison.Ordinal) ||
                activity.Contains("审批中", StringComparison.Ordinal);
            waveform.State = waiting ? Views.IslandWaveformState.Waiting
                : running ? Views.IslandWaveformState.Running
                : failed ? Views.IslandWaveformState.Failed
                : Views.IslandWaveformState.Idle;
            if (waveform.State == Views.IslandWaveformState.Running)
                waveform.Push(0.55 + (Random.Shared.NextDouble() - 0.5) * 0.1);
            else if (waveform.State == Views.IslandWaveformState.Idle)
                waveform.Push(0);
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
        if (_islandDot is not null)
        {
            _islandDot.IsVisible = !isFumo;
            if (_islandBackendCircle is not null)
                _islandBackendCircle.BorderBrush = _islandDot.Background;
        }
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
