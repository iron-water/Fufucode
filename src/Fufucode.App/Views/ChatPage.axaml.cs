using System;
using System.Diagnostics;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Fufucode.App.Infrastructure;
using Fufucode.App.ViewModels;

namespace Fufucode.App.Views;

/// <summary>对话页：页头 chips、时间线、输入卡片。Enter/IME 守卫与时间线滚动都在本页内处理。</summary>
public partial class ChatPage : UserControl
{
    private readonly ImeEnterGuard _imeGuard = new();
    private bool _preeditHooked;
    private bool _programmaticScroll;
    private Avalonia.Threading.DispatcherTimer? _scrollTimer;
    private long _programmaticUntilTicks;
    private long _lastUserInputTicks;
    private MainViewModel? _attachedViewModel;

    public ChatPage()
    {
        InitializeComponent();
        InitializeComposerTools();
        ComposerToolbar.SizeChanged += (_, _) => UpdateComposerToolbar();

        // F06：AcceptsReturn 的 TextBox 在自身 OnKeyDown 里处理 Enter 并标记 Handled，
        // XAML 挂的 bubble 处理器根本收不到；必须在 Tunnel 阶段拦截。
        InputBox.AddHandler(InputElement.KeyDownEvent, InputBox_KeyDown, RoutingStrategies.Tunnel);
        InputBox.TemplateApplied += (_, _) => HookInputPreedit();

        Loaded += (_, _) => HookInputPreedit();

        // D-R4-1：会话头与输入区高度会随内容变化（草稿增长、队列条、待恢复提示），
        // 窗口层据此重算抽屉避让带；只发事件，不做任何布局决策。
        ConversationHeader.LayoutUpdated += (_, _) => ContentBandChanged?.Invoke(this, EventArgs.Empty);
        ComposerCard.LayoutUpdated += (_, _) => ContentBandChanged?.Invoke(this, EventArgs.Empty);

        DataContextChanged += (_, _) =>
        {
            if (_attachedViewModel is { } previous)
            { previous.Input.FocusRequested -= OnInputFocusRequested; previous.Input.PropertyChanged -= ComposerChanged; previous.Timeline.Entries.CollectionChanged -= TimelineChanged; previous.Timeline.ScrollToEndSmoothRequested -= AnimateScrollToEnd; }
            _attachedViewModel = DataContext as MainViewModel;
            // 命令填入后聚焦输入框（H3 命令 Flyout / 扩展页「填入」）。
            if (DataContext is MainViewModel vm)
            {
                vm.Input.FocusRequested += OnInputFocusRequested;
                vm.Input.PropertyChanged += ComposerChanged;
                vm.Timeline.Entries.CollectionChanged += TimelineChanged;
                vm.Timeline.ScrollToEndSmoothRequested += AnimateScrollToEnd;
                RefreshComposerTools();
            }
        };
        AddHandler(PointerReleasedEvent, SelectionReleased, RoutingStrategies.Bubble, handledEventsToo: true);

        // 滚动跟随：用户一操作就标记时间，150ms 内不抢滚动；程序化滚动有短暂 latch。
        TimelineScroller.PointerWheelChanged += (_, _) => MarkUserScrollInput();
        TimelineScroller.PointerPressed += (_, _) => MarkUserScrollInput();
        TimelineScroller.KeyDown += (_, _) => MarkUserScrollInput();
    }

    private void MarkUserScrollInput() => _lastUserInputTicks = Stopwatch.GetTimestamp();

    /// <summary>消息/回合首次挂到可视树时播放进入动画；流式刷新不会重播。</summary>
    private void OnMessageEntryLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control control) return;
        control.Loaded -= OnMessageEntryLoaded;
        Motion.AnimateIn(control);
    }

    /// <summary>变更卡的"查看变更"：打开工作区的变更面板。变更来源由 WorkspacePanel 自己判断。</summary>
    private void OnViewChanges(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not MainWindow window) return;
        window.OpenDockPage("changes", DockZone.Right);
    }

    /// <summary>撤销本轮文件改动：回到每个文件在本回合首次写入前的状态。</summary>
    private void OnUndoChanges(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: AssistantMessageViewModel message })
            message.ChangeCard.Undo();
    }

    /// <summary>展开/收起变更卡的文件列表。</summary>
    private void OnToggleChangeList(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: AssistantMessageViewModel message })
            message.ChangeCard.ToggleExpanded();
    }

    /// <summary>沉浸式模式下展开被收起的输入区，并把焦点交给输入框。</summary>
    private void OnExpandComposer(object? sender, RoutedEventArgs e)
    {
        ComposerCard.IsVisible = true;
        ComposerRevealButton.IsVisible = false;
        InputBox.Focus();
        if (DataContext is MainViewModel { Timeline.IsAtBottom: true })
            TimelineScroller.ScrollToEnd();
    }

    /// <summary>展开/收起某个文件的内联 diff。</summary>
    private void OnToggleFileDiff(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: TurnFileChangeViewModel file }) file.ToggleDiff();
    }

    private void UpdateComposerToolbar()
    {
        var narrow = ComposerToolbar.Bounds.Width < 520;
        foreach (var control in ComposerToolbar.Children) Grid.SetRow(control, narrow ? 1 : 0);
        Grid.SetRow(ComposerModes, 0);
        Grid.SetColumn(ComposerModes, narrow ? 0 : 3);
        Grid.SetColumnSpan(ComposerModes, narrow ? 5 : 1);
        ComposerModes.HorizontalAlignment = narrow ? Avalonia.Layout.HorizontalAlignment.Left : Avalonia.Layout.HorizontalAlignment.Right;
        ComposerModes.Margin = narrow ? new Avalonia.Thickness(0, 0, 0, 6) : new Avalonia.Thickness(0, 0, 8, 0);
    }

    private void OnInputFocusRequested()
    {
        InputBox.Focus();
        InputBox.CaretIndex = InputBox.Text?.Length ?? 0;
    }

    /// <summary>命令 Flyout 条目点击后关闭弹层并把焦点交还输入框。</summary>
    private void CommandFlyoutItem_Click(object? sender, RoutedEventArgs e)
    {
        (CommandFlyoutButton.Flyout as Flyout)?.Hide();
        OnInputFocusRequested();
    }

    /// <summary>供窗口/自测定位输入框（名称保持在 ChatPage 的 namescope 内）。</summary>
    public TextBox Input => InputBox;

    /// <summary>
    /// D-R4-1 抽屉避让带：窄窗抽屉必须压在「会话头之下、输入区之上」的带内，
    /// 否则会盖住页头动作区与 composer。两个值都相对本页顶端度量；
    /// 未布局完成（Bounds 尚未产生）时为 0，调用方按 0 处理即退化为不避让。
    /// </summary>
    internal double HeaderBandHeight => ConversationHeader.IsVisible ? ConversationHeader.Bounds.Height : 0;

    internal double BottomReserveHeight
    {
        get
        {
            if (Bounds.Height <= 0) return 0;
            if (!ComposerCard.IsVisible) return 0;
            // 定制布局可增加中间容器，按实际视觉坐标定位输入区。
            var top = ComposerCard.TranslatePoint(default, this)?.Y;
            return top is { } y ? Math.Max(0, Bounds.Height - y) : 0;
        }
    }

    /// <summary>会话头/输入区几何变化通知（窗口层重算抽屉避让带）。</summary>
    internal event EventHandler? ContentBandChanged;

    /// <summary>
    /// 显式"回到最新"：220ms 缓出滚动，比瞬移更容易看清落点。
    /// 流式自动跟随仍走 <see cref="ScrollToEnd"/> 的瞬时滚动，两者互不打断。
    /// </summary>
    private void AnimateScrollToEnd() =>
        AnimateScrollTo(Math.Max(0, TimelineScroller.Extent.Height - TimelineScroller.Viewport.Height));

    /// <summary>
    /// 缓出滚动到目标偏移。滚动期间标记为程序化滚动，
    /// 避免被"用户滚动"判定打断自动跟随。
    /// </summary>
    private void AnimateScrollTo(double target)
    {
        if (MotionPreference.ReducedMotion)
        {
            TimelineScroller.Offset = new Vector(TimelineScroller.Offset.X, target);
            return;
        }

        var start = TimelineScroller.Offset.Y;
        var distance = target - start;
        if (Math.Abs(distance) < 4)
        {
            TimelineScroller.Offset = new Vector(TimelineScroller.Offset.X, target);
            return;
        }

        _scrollTimer?.Stop();
        var startedAt = Stopwatch.GetTimestamp();
        const double durationMs = 220;
        var timer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _scrollTimer = timer;
        timer.Tick += (_, _) =>
        {
            var elapsed = (Stopwatch.GetTimestamp() - startedAt) * 1000.0 / Stopwatch.Frequency;
            var t = Math.Clamp(elapsed / durationMs, 0, 1);
            var eased = 1 - Math.Pow(1 - t, 3);
            _programmaticScroll = true;
            _programmaticUntilTicks = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * 0.12);
            TimelineScroller.Offset = new Vector(TimelineScroller.Offset.X, start + distance * eased);
            if (t < 1) return;
            timer.Stop();
            if (ReferenceEquals(_scrollTimer, timer)) _scrollTimer = null;
        };
        timer.Start();
    }

    public void ScrollToEnd()
    {
        _programmaticScroll = true;
        // 不能立即复位：ScrollChanged 是异步到达的，立即复位会把程序化滚动误判为用户滚动。
        _programmaticUntilTicks = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * 0.12);
        try
        {
            TimelineScroller.ScrollToEnd();
        }
        finally
        {
            _programmaticScroll = false;
        }
    }

    private void ToggleRightPanel_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.ToggleRightPanel();
        }
    }

    private void TimelineScroller_ScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_programmaticScroll ||
            Stopwatch.GetTimestamp() < _programmaticUntilTicks ||
            Stopwatch.GetTimestamp() - _lastUserInputTicks < Stopwatch.Frequency * 0.15 ||
            DataContext is not MainViewModel vm)
        {
            return;
        }

        var scroller = (ScrollViewer)sender!;
        var distanceToBottom = scroller.Extent.Height - scroller.Viewport.Height - scroller.Offset.Y;
        vm.Timeline.SetAutoScrollAtBottom(distanceToBottom <= 40);
        vm.Timeline.SetBackToLatestVisible(distanceToBottom > 120);
    }

    /// <summary>把输入框模板内 TextPresenter 的真实组合状态（PreeditText）接入回车守卫。</summary>
    private void HookInputPreedit()
    {
        if (_preeditHooked)
        {
            return;
        }

        var presenter = InputBox.GetVisualDescendants()
            .OfType<Avalonia.Controls.Presenters.TextPresenter>()
            .FirstOrDefault();
        if (presenter is null)
        {
            return;
        }

        _preeditHooked = true;
        _imeGuard.OnPreeditChanged(presenter.PreeditText);
        presenter.PropertyChanged += (_, args) =>
        {
            if (args.Property == Avalonia.Controls.Presenters.TextPresenter.PreeditTextProperty)
            {
                _imeGuard.OnPreeditChanged(args.NewValue as string);
            }
        };
    }

    // Tunnel 阶段调用（见构造函数注释）：先于 TextBox 自身处理。
    private void InputBox_KeyDown(object? sender, KeyEventArgs e)
    {
        // UI-1 CJK 输入法双重互锁，第二重：VK 229 在 Avalonia 里映射为 Key.ImeProcessed
        // （对齐 Web 侧 keyCode === 229）。合成键不进入发送分支，直接吞掉——即使它一旦
        // 被归一化成 Enter 也不会误发。
        if (e.Key == Key.ImeProcessed)
        {
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Enter)
        {
            return;
        }

        // Shift+Enter 放行给 TextBox 插入换行。
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            return;
        }

        // 计划 §9 + UI-1，第一重：组合态（preedit 时间窗）命中时 Enter 不发送。
        // 此处 key 已排除 229（第二重已在上面返回），故 isComposing 由 PreeditText 时间窗
        // 等价表达，调用组合态判定即可；纯逻辑组合等价于 ShouldSwallowEnter(keyCode, isComposing)。
        if (_imeGuard.ShouldSwallowEnter())
        {
            e.Handled = true;
            return;
        }

        if (DataContext is MainViewModel vm)
        {
            // P2-17：Ctrl+Enter —— 运行中插入（steer），空闲等同普通发送（Immediate）；
            // 与普通 Enter 经过同一个 ImeEnterGuard（组合中不会走到这里）。
            if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                if (vm.Input.IsTurnActive)
                {
                    vm.Input.SteerCommand.Execute(null);
                }
                else
                {
                    vm.Input.SendCommand.Execute(null);
                }
            }
            else
            {
                vm.Input.SendCommand.Execute(null);
            }

            e.Handled = true;
        }
    }

    /// <summary>页头内联重命名：Enter 保存、Esc 取消。</summary>
    private void RenameBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            vm.Sidebar.SaveNameCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            vm.Sidebar.CancelRenameCommand.Execute(null);
            e.Handled = true;
        }
    }
}
