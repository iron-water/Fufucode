using System;
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
            { previous.Input.FocusRequested -= OnInputFocusRequested; previous.Input.PropertyChanged -= ComposerChanged; previous.Timeline.Entries.CollectionChanged -= TimelineChanged; }
            _attachedViewModel = DataContext as MainViewModel;
            // 命令填入后聚焦输入框（H3 命令 Flyout / 扩展页「填入」）。
            if (DataContext is MainViewModel vm)
            {
                vm.Input.FocusRequested += OnInputFocusRequested;
                vm.Input.PropertyChanged += ComposerChanged;
                vm.Timeline.Entries.CollectionChanged += TimelineChanged;
                RefreshComposerTools();
            }
        };
        AddHandler(PointerReleasedEvent, SelectionReleased, RoutingStrategies.Bubble, handledEventsToo: true);
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

    public void ScrollToEnd()
    {
        _programmaticScroll = true;
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
        if (_programmaticScroll || DataContext is not MainViewModel vm)
        {
            return;
        }

        var scroller = (ScrollViewer)sender!;
        var distanceToBottom = scroller.Extent.Height - scroller.Viewport.Height - scroller.Offset.Y;
        vm.Timeline.SetAutoScrollAtBottom(distanceToBottom <= 32);
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
        if (e.Key != Key.Enter)
        {
            return;
        }

        // Shift+Enter 放行给 TextBox 插入换行。
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            return;
        }

        // 计划 §9：中文输入法组合期间 Enter 不误发送（见 ImeEnterGuard）
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
