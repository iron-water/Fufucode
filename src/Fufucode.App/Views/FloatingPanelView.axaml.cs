using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Automation;
using Fufucode.App.ViewModels;

namespace Fufucode.App.Views;

/// <summary>
/// 浮窗内容。提醒列表直接绑定通知中心；辅助对话槽位由宿主把侧边聊天控件挂进来，
/// 因此同一个侧边会话既能停在停靠页，也能出现在浮窗里，不产生两份会话。
/// </summary>
public partial class FloatingPanelView : UserControl
{
    private Point? _dragStart;
    private bool _dragging;

    public FloatingPanelView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => BuildStatusAndQuickActions();
        HeaderDragArea.PointerPressed += OnHeaderPressed;
        HeaderDragArea.PointerMoved += OnHeaderMoved;
        HeaderDragArea.PointerReleased += OnHeaderReleased;
        SectionTabs.SelectionChanged += (_, _) => ApplySection();
        DataContextChanged += (_, _) => ApplySection();
        AttachedToVisualTree += (_, _) => ApplySection();
    }

    /// <summary>浮窗请求切到"辅助对话"分区，宿主据此把侧边聊天控件挂进来。</summary>
    public event Action? SideChatRequested;
    internal bool SideChatVisibleForTesting => SideChatSlot.IsVisible;
    internal ContentControl SideChatHost => SideChatSlot;
    internal void ShowSideChatSection() => SectionTabs.SelectedIndex = 1;

    /// <summary>拖动头部时由宿主移动面板（内嵌形态改 Margin，独立形态移动窗口）。</summary>
    public event Action<Point>? DragRequested;
    public event Action<Point>? DragCompleted;

    private void OnHeaderPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.Source is Button) return;
        _dragStart = e.GetPosition(this);
        _dragging = false;
    }

    private void OnHeaderMoved(object? sender, PointerEventArgs e)
    {
        if (_dragStart is not { } start) return;
        var point = e.GetPosition(this);
        if (!_dragging && (Math.Abs(point.X - start.X) + Math.Abs(point.Y - start.Y)) < 4) return;
        _dragging = true; Cursor = new Cursor(StandardCursorType.SizeAll);
        DragRequested?.Invoke(point - start);
    }

    private void OnHeaderReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragStart is null) return;
        var moved = _dragging;
        _dragStart = null; _dragging = false; Cursor = null;
        if (moved) DragCompleted?.Invoke(e.GetPosition(this));
    }

    /// <summary>
    /// 状态行与快捷动作由宿主窗口填充：浮窗要能替代状态栏看状态，
    /// 并把用量、调用轨迹、朗读、变更摘要这些辅助功能也收进来。
    /// </summary>
    private void BuildStatusAndQuickActions()
    {
        if (TopLevel.GetTopLevel(this) is not MainWindow window) return;
        StatusLine.Children.Clear(); QuickActions.Children.Clear();
        foreach (var (id, title) in new[] { ("status.connection", "连接"), ("status.shell", "Shell"), ("status.activity", "活动"), ("status.tokens", "Token") })
        {
            var label = new TextBlock { Text = window.FloatingStatusText(id), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, FontSize = 11 };
            label.Classes.Add("meta");
            ToolTip.SetTip(label, title);
            StatusLine.Children.Add(label);
        }
        foreach (var (icon, title, id) in new[]
        {
            ("Info", "用量详情", "usage"), ("Branch", "查看调用轨迹", "trace"),
            ("Lightbulb", "开始朗读", "speech.start"), ("RecordStop", "停止朗读", "speech.stop"),
            ("Settings", "朗读控制", "speech.settings"),
            ("History", "变更摘要", "summary.toggle"),
        })
        {
            var button = WorkbenchUi.IconButton(icon, title);
            button.Classes.Add("toolButton");
            button.Command = window.FloatingActionCommand(id);
            AutomationProperties.SetName(button, title);
            QuickActions.Children.Add(button);
        }
    }

    private void ApplySection()
    {
        var sideChat = SectionTabs.SelectedIndex == 1;
        NoticeList.IsVisible = !sideChat;
        SideChatSlot.IsVisible = sideChat;
        if (sideChat) SideChatRequested?.Invoke();
    }

    private void OpenCenter_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not FloatingPanelViewModel vm) return;
        vm.Center.Open();
        (TopLevel.GetTopLevel(this) as MainWindow)?.OpenNotificationCenterOverlay();
    }

    private void ClosePanel_Click(object? sender, RoutedEventArgs e)
        => (DataContext as FloatingPanelViewModel)?.ToggleOpen();

    private void Activate_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control control) return;
        if (DataContext is not FloatingPanelViewModel vm) return;
        if (control.DataContext is NotificationItemViewModel item) vm.Center.ActivateCommand.Execute(item);
    }
}
