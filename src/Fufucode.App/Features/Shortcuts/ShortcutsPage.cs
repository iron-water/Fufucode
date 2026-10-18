using Avalonia.Controls.Primitives;
using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Fufucode.App.Views;

namespace Fufucode.App.Features.Shortcuts;

/// <summary>
/// 快捷键管理页（M1）。沿用设置页的分组卡 + 行式条目语言，
/// 但状态与行为全部在本控件 + <see cref="ShortcutManagerViewModel"/> 内，
/// 不触碰共享外壳：调用方只需提供动作目录、可用性函数与存储。
/// </summary>
public sealed class ShortcutsPage : UserControl
{
    private readonly ShortcutManagerViewModel _vm;
    private readonly ShortcutRecorder _recorder = new();
    private StackPanel? _list;
    private ScrollViewer? _scroller;
    private TextBox? _searchBox;
    private ComboBox? _scopeBox;
    private string? _recordingActionId;
    /// <summary>二级页当前展开的分组；null = 一级分组目录页（Win11 设置式下钻）。
    /// 状态由宿主经 <see cref="GroupOpened"/>/<see cref="GroupClosed"/> 与设置页 CurrentPageId 同步。</summary>
    private string? _activeGroup;

    /// <summary>录制/试按期间由宿主注入的输入法组合状态读取器（组合中按键一律忽略）。</summary>
    public Func<bool>? CompositionProbe { get; set; }

    /// <summary>可选的宿主接线：终端输入优先的让路判断等由宿主提供。</summary>
    public Func<Key, bool>? HostOwnsKey { get; set; }

    /// <summary>进入分组二级页时回调宿主（参数 = 组名）。宿主用它更新面包屑。</summary>
    public Action<string>? GroupOpened { get; set; }
    /// <summary>从二级页返回分组目录时回调宿主。</summary>
    public Action? GroupClosed { get; set; }
    /// <summary>当前展开的二级组（宿主读取以渲染标题）。</summary>
    public string? ActiveGroup => _activeGroup;
    /// <summary>宿主同步用：直接设置当前二级组（不触发回调），配合面包屑返回和 headless 截图。</summary>
    public void SyncGroup(string? group)
    {
        if (_activeGroup == group) return;
        _activeGroup = group;
        _recordingActionId = null;
        RenderList();
        FadeContentIn();
    }

    public ShortcutsPage(ShortcutManagerViewModel viewModel)
    {
        _vm = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _vm.Changed += OnViewModelChanged;
        Build();
        _vm.Refresh();
        RenderList();
    }

    /// <summary>当前选中的动作 ID（供宿主接线在真实键盘路由里读取）。</summary>
    public ShortcutManagerViewModel ViewModel => _vm;
    public bool IsRecording => _recordingActionId is not null;

    private void Build()
    {
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*") };

        var header = new StackPanel { Spacing = 4, Margin = new Thickness(0, 0, 0, 12) };
        var title = new TextBlock { Text = "键盘快捷键" };
        title.Classes.Add("settingsPageTitle");
        header.Children.Add(title);
        var help = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 0, 0, 0) };
        help.Classes.Add("settingsRowDesc");
        help.Text = "从应用动作目录生成；修改后立即生效，保存后重启保留。冲突会同时列出双方动作，不会静默覆盖。";
        header.Children.Add(help);
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        var filters = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 8) };
        _searchBox = new TextBox
        {
            PlaceholderText = "搜索动作、范围或当前绑定", MinWidth = 260, Margin = new Thickness(0, 0, 8, 0),
        };
        _searchBox.TextChanged += (_, _) => _vm.SearchText = _searchBox.Text ?? string.Empty;
        DockPanel.SetDock(_searchBox, Dock.Left);
        filters.Children.Add(_searchBox);

        _scopeBox = new ComboBox
        {
            ItemsSource = new[] { "全部范围" }.Concat(ShortcutScopeInfo.All.Select(scope => scope.Title())).ToArray(),
            SelectedIndex = 0, MinWidth = 140, Margin = new Thickness(8, 0, 8, 0),
        };
        _scopeBox.SelectionChanged += (_, _) =>
            _vm.ScopeFilter = _scopeBox.SelectedIndex <= 0 ? null : ShortcutScopeInfo.All[_scopeBox.SelectedIndex - 1];
        DockPanel.SetDock(_scopeBox, Dock.Left);
        filters.Children.Add(_scopeBox);

        var revert = new Button { Content = "放弃改动", Margin = new Thickness(8, 0, 0, 0) };
        revert.Click += (_, _) => _vm.Revert();
        var resetAll = new Button { Content = "全部恢复默认", Margin = new Thickness(8, 0, 0, 0) };
        resetAll.Click += (_, _) => _vm.ResetAll();
        var save = new Button { Content = "保存", Classes = { "accent" }, Margin = new Thickness(8, 0, 0, 0) };
        save.Click += (_, _) => _vm.Save();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(revert); buttons.Children.Add(resetAll); buttons.Children.Add(save);
        buttons.Bind(IsVisibleProperty, new Avalonia.Data.Binding(nameof(ShortcutManagerViewModel.IsWritable)) { Source = _vm });
        DockPanel.SetDock(buttons, Dock.Right);
        filters.Children.Add(buttons);

        Grid.SetRow(filters, 1);
        root.Children.Add(filters);

        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        status.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding(nameof(ShortcutManagerViewModel.Status)) { Source = _vm });
        Grid.SetRow(status, 2);
        root.Children.Add(status);

        var error = new Border
        {
            Classes = { "errorCard" }, IsVisible = false, Margin = new Thickness(0, 0, 0, 8),
            Child = new TextBlock { TextWrapping = TextWrapping.Wrap },
        };
        error.Bind(IsVisibleProperty, new Avalonia.Data.Binding(nameof(ShortcutManagerViewModel.HasLoadError)) { Source = _vm });
        ((TextBlock)error.Child!).Bind(TextBlock.TextProperty,
            new Avalonia.Data.Binding(nameof(ShortcutManagerViewModel.LoadError))
            { Source = _vm, StringFormat = "keybindings.json 加载失败（{0}），已回退默认绑定且不会覆盖原文件" });
        Grid.SetRow(error, 2);
        root.Children.Add(error);

        _scroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Transitions = new Avalonia.Animation.Transitions
            {
                new Avalonia.Animation.DoubleTransition
                {
                    Property = OpacityProperty,
                    Duration = TimeSpan.FromMilliseconds(150),
                    Easing = new Avalonia.Animation.Easings.CubicEaseOut(),
                },
            },
        };
        _list = new StackPanel { Spacing = 0 };
        _scroller.Content = _list;
        Grid.SetRow(_scroller, 3);
        root.Children.Add(_scroller);

        Content = root;
        AddHandler(KeyDownEvent, OnRecordingKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnRecordingKeyUp, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        // 窗口失活时按键状态不可信：清掉过期修饰键，但不结束录制。
        LostFocus += (_, _) => _recorder.ReleaseAllHeld();
    }

    private void OnViewModelChanged() => Dispatcher.UIThread.Post(RenderList);

    /// <summary>搜索或范围过滤激活时平铺全部匹配（搜索要一览），否则一级页只列分组入口。</summary>
    private bool IsFiltered => _vm.ScopeFilter is not null || !string.IsNullOrWhiteSpace(_vm.SearchText);

    private void RenderList()
    {
        if (_list is null) return;
        _list.Children.Clear();
        _recordingActionId = null;

        if (_vm.Rows.Count == 0)
        {
            _list.Children.Add(SettingsLayout.Row("没有匹配的动作", "换一个关键词，或把范围切回“全部范围”", null, "Search"));
            return;
        }

        if (_vm.HasConflicts)
        {
            var conflictRows = _vm.Conflicts.Select(conflict => (Control)SettingsLayout.Row(
                conflict.GestureText,
                $"{conflict.ScopeTitle} · 与 {conflict.ActionTitles} 冲突（{conflict.ActionCount} 个动作）",
                null, "Warning")).ToArray();
            _list.Children.Add(SettingsLayout.GroupHeader("冲突（必须解决，不会自动覆盖）"));
            _list.Children.Add(SettingsLayout.Group(conflictRows));
        }

        // ── 无过滤：一级分组目录 / 二级组内列表（Win11 设置式下钻，不铺开几十个动作） ──
        if (!IsFiltered)
        {
            if (_activeGroup is null)
            {
                foreach (var group in _vm.Rows.GroupBy(row => row.Group))
                {
                    var name = group.Key;
                    var count = group.Count();
                    var bound = group.Count(row => row.HasBinding);
                    _list.Children.Add(SettingsLayout.ChevronRow(name, $"{count} 个动作 · {bound} 个已绑定", () => OpenGroup(name), "Keyboard"));
                }
                _list.Children.Add(SettingsLayout.Note("默认绑定沿袭现有产品行为；「全局」范围在终端输入焦点下自动让路，中文输入法组合期间的按键不参与绑定。"));
                return;
            }

            var back = SettingsLayout.Row("键盘快捷键", "返回全部分组", null, "ArrowLeft");
            back.Classes.Add("clickable");
            back.PointerPressed += (_, _) => BackToGroups();
            _list.Children.Add(back);
            var rows = _vm.Rows.Where(row => row.Group == _activeGroup).Select(row => (Control)BuildRow(row)).ToArray();
            _list.Children.Add(SettingsLayout.GroupHeader(_activeGroup));
            _list.Children.Add(SettingsLayout.Group(rows));
            if (rows.Length == 0)
                _list.Children.Add(SettingsLayout.Row("此分组没有动作", null, null, "Keyboard"));
            return;
        }

        // ── 搜索/范围过滤：平铺全部匹配 ──
        foreach (var group in _vm.Rows.GroupBy(row => row.Group))
        {
            _list.Children.Add(SettingsLayout.GroupHeader(group.Key));
            _list.Children.Add(SettingsLayout.Group(group.Select(row => (Control)BuildRow(row)).ToArray()));
        }
        _list.Children.Add(SettingsLayout.Note("默认绑定沿袭现有产品行为；「全局」范围在终端输入焦点下自动让路，中文输入法组合期间的按键不参与绑定。"));
    }

    private void OpenGroup(string group)
    {
        _activeGroup = group;
        _recordingActionId = null;
        RenderList();
        FadeContentIn();
        GroupOpened?.Invoke(group);
    }

    private void BackToGroups()
    {
        _activeGroup = null;
        _recordingActionId = null;
        RenderList();
        FadeContentIn();
        GroupClosed?.Invoke();
    }

    /// <summary>下钻/返回时内容区淡入（与设置页 ActivePageId 切换同一观感）。</summary>
    private void FadeContentIn()
    {
        if (_scroller is null) return;
        _scroller.Opacity = 0;
        var scroller = _scroller;
        Dispatcher.UIThread.Post(() => scroller.Opacity = 1, DispatcherPriority.Render);
    }

    private Control BuildRow(ShortcutRowViewModel row)
    {
        var detailLines = new[]
        {
            row.ConflictText,
            row.ScopeNote,
            row.IsUnavailable ? "当前不可用：" + row.UnavailableReason : null,
        }.Where(text => !string.IsNullOrWhiteSpace(text)).Select(text => text!).ToArray();

        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        var gesture = new TextBlock
        {
            Text = row.HasBinding ? row.EffectiveText : "未绑定",
            FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace"),
            Opacity = row.HasBinding ? 1 : .55, MinWidth = 96,
            VerticalAlignment = VerticalAlignment.Center,
        };
        controls.Children.Add(gesture);

        var edit = new Button { Content = "更改" };
        edit.Click += (_, _) => BeginRecording(row, edit, gesture);
        controls.Children.Add(edit);

        var toggle = new Button { Content = row.IsDisabled ? "启用" : "禁用" };
        toggle.Click += (_, _) =>
        {
            if (row.IsDisabled) _vm.ResetAction(row.Id); else _vm.SetBinding(row.Id, string.Empty);
        };
        controls.Children.Add(toggle);

        var restore = new Button { Content = "恢复默认", IsVisible = !row.IsDefault };
        restore.Click += (_, _) => _vm.ResetAction(row.Id);
        controls.Children.Add(restore);

        var line = SettingsLayout.Row(row.Title, row.HasDescription ? row.Description : row.ConflictText, controls, "Keyboard");
        // SettingsLayout.Row 的说明区是 Grid 第 1 列（Auto,*,Auto）里的 StackPanel；
        // 追加范围/冲突/不可用明细，沿用「一张卡一行」的设置语言，不另建一套布局。
        if (line.Child is Grid grid && grid.Children.OfType<StackPanel>().FirstOrDefault() is { } textPanel)
            foreach (var text in detailLines)
            {
                var detail = new TextBlock { Text = text };
                detail.Classes.Add("settingsRowDesc");
                ToolTip.SetTip(detail, text);
                textPanel.Children.Add(detail);
            }
        line.Tag = row;
        return line;
    }

    private void BeginRecording(ShortcutRowViewModel row, Button button, TextBlock gestureView)
    {
        if (_recordingActionId == row.Id) { CancelRecording(); return; }
        _recordingActionId = row.Id;
        _recorder.Start();
        button.Content = "按组合…";
        gestureView.Text = "等待按键…";
        _vm.Status = "录制中：" + row.Title + "（按 Esc 取消）";
        Focus();
    }
    private void CancelRecording()
    {
        _recorder.Stop();
        _recordingActionId = null;
        RenderList();
    }

    private void OnRecordingKeyDown(object? sender, KeyEventArgs e)
    {
        if (_recordingActionId is not { } actionId) return;
        var composing = CompositionProbe?.Invoke() ?? false;
        var result = _recorder.KeyDown(e.Key, e.KeyModifiers, composing);
        e.Handled = true;

        switch (result.Outcome)
        {
            case ShortcutCaptureOutcome.IgnoredDuringComposition:
                _vm.Status = "输入法组合进行中，已忽略本次按键";
                return;
            case ShortcutCaptureOutcome.Cancelled:
                CancelRecording();
                return;
            case ShortcutCaptureOutcome.ModifiersOnly:
                _vm.Status = "已按下 " + _recorder.PendingModifiers + "，再按一个主键";
                return;
            case ShortcutCaptureOutcome.Pending:
                return;
        }

        var captured = result.Gesture!;
        if (HostOwnsKey?.Invoke(captured.Key) == true && _vm.Rows.FirstOrDefault(r => r.Id == actionId)?.Action.Scope == ShortcutScope.Global)
        {
            _vm.Status = ShortcutRules.TerminalReservedReason;
            CancelRecording();
            return;
        }

        _recordingActionId = null;
        _vm.SetBinding(actionId, captured.Text);
        RenderList();
    }
    /// <summary>录制期间修饰键释放必须回写到录制器，否则会形成幽灵组合。</summary>
    private void OnRecordingKeyUp(object? sender, KeyEventArgs e)
    {
        if (_recordingActionId is null) return;
        _recorder.KeyUp(e.Key);
        e.Handled = true;
        _vm.Status = _recorder.PendingModifiers.Length == 0
            ? "录制中：再按一个组合（按 Esc 取消）"
            : "已按下 " + _recorder.PendingModifiers + "，再按一个主键";
    }

    /// <summary>宿主可注入的自检钩子：模拟一次按键录制（不依赖真实窗口输入）。</summary>
    internal void SimulateRecordingKeyForTesting(Key key, KeyModifiers modifiers, bool composing, bool isKeyUp = false)
    {
        if (_recordingActionId is null) return;
        // 组合状态是每次按键的输入条件，只在本次调用生效，不得粘住后续按键。
        var previous = CompositionProbe;
        CompositionProbe = () => composing;
        var args = new KeyEventArgs
        {
            RoutedEvent = isKeyUp ? KeyUpEvent : KeyDownEvent, Key = key, KeyModifiers = modifiers,
        };
        try
        {
            if (isKeyUp) OnRecordingKeyUp(this, args);
            else OnRecordingKeyDown(this, args);
        }
        finally { CompositionProbe = previous; }
    }

    /// <summary>测试钩子：模拟窗口失活（清掉过期修饰键状态）。</summary>
    internal void SimulateDeactivationForTesting() => _recorder.ReleaseAllHeld();

    internal string? RecordingActionForTesting => _recordingActionId;
    internal string PendingModifiersForTesting => _recorder.PendingModifiers;

    /// <summary>测试钩子：放弃当前录制。</summary>
    internal void CancelRecordingForTesting() => CancelRecording();

    /// <summary>测试钩子：等价于点击一级页的分组入口卡，进入该组二级页（走完整回调链）。</summary>
    internal void OpenGroupForTesting(string group) => OpenGroup(group);

    /// <summary>测试钩子：等价于点击二级页的返回行（走完整回调链）。</summary>
    internal void BackToGroupsForTesting() => BackToGroups();

    /// <summary>测试钩子：直接发起对某一行的录制（等价于点击「更改」）。</summary>
    internal void BeginRecordingForTesting(string actionId)
    {
        if (_vm.Rows.FirstOrDefault(row => row.Id == actionId) is not { } row) return;
        var probe = new TextBlock();
        BeginRecording(row, new Button(), probe);
    }
}
