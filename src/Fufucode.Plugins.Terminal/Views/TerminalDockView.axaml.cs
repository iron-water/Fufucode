using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fufucode.App.Infrastructure;
using Fufucode.Plugins.Terminal.Features.TerminalExperience;
using Fufucode.Plugins.Terminal.Infrastructure;
using Fufucode.Plugins.Terminal.ViewModels;

namespace Fufucode.Plugins.Terminal.Views;

public partial class TerminalDockView : UserControl
{
    private readonly ImeEnterGuard _imeGuard = new();
    private readonly TerminalCompositionState _composition = new();
    private TerminalDockViewModel? _dock;
    private TerminalTabViewModel? _tab;
    private bool _preeditHooked;
    private bool _followOutput = true;
    private bool _restoringScroll;
    private long _scrollGeneration;
    private bool _confirmingClose;
    internal Func<TerminalTabViewModel, Task<bool>>? ConfirmCloseForTesting { get; set; }

    /// <summary>Ctrl+Click on an OSC 8 hyperlink; the host decides whether to open it.</summary>
    public event Action<string>? LinkActivated;

    /// <summary>输入法组合预览状态；组合未提交时输入条显示临时文本。</summary>
    internal TerminalCompositionState Composition => _composition;

    /// <summary>宿主用于“复制或中断”快捷键判断命令框是否持有文本选区。</summary>
    internal TextBox CommandInputControl => TerminalInput;

    /// <summary>当前设置（由插件页面注入并持久化）；设置对话框从这里取值。</summary>
    public TerminalSettings CurrentSettings { get; private set; } = TerminalSettings.Default;

    /// <summary>设置对话框点击“应用”后触发；插件页面负责持久化。</summary>
    public event Action<TerminalSettings>? SettingsChanged;

    /// <summary>应用插件设置中的显示项（字号、闪烁光标、响铃）。</summary>
    public void ApplyDisplaySettings(TerminalSettings settings)
    {
        CurrentSettings = settings.Normalize();
        TerminalSurface.FontSizeDip = CurrentSettings.FontSize;
        TerminalSurface.BlinkCursor = CurrentSettings.BlinkCursor;
        _bellVisual = CurrentSettings.Bell == "visual";
        TerminalSurface.InvalidateMeasure();
        TerminalSurface.InvalidateVisual();
        Dispatcher.UIThread.Post(ResizeTerminal, DispatcherPriority.Loaded);
    }

    private bool _bellVisual = true;

    private async void Settings_Click(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        var profiles = ShellProfiles.All;
        var shell = new ComboBox
        {
            ItemsSource = profiles.Select(profile => ShellProfiles.Resolve(profile) is null
                ? profile.DisplayName + "（未安装）"
                : profile.DisplayName).ToArray(),
            SelectedIndex = Math.Max(0, profiles.ToList().FindIndex(profile => profile.Id == CurrentSettings.ShellProfile)),
            MinWidth = 300
        };
        var overrideBox = new TextBox { Text = CurrentSettings.CommandOverride, PlaceholderText = "留空使用所选 shell 的默认命令" };
        var font = new NumericUpDown { Minimum = 9, Maximum = 28, Value = (decimal)CurrentSettings.FontSize, Increment = 1 };
        var scrollback = new NumericUpDown { Minimum = 0, Maximum = 100000, Value = CurrentSettings.ScrollbackLines, Increment = 500 };
        var bell = new ComboBox
        {
            ItemsSource = TerminalSettings.BellModes,
            SelectedItem = CurrentSettings.Bell
        };
        var blink = new CheckBox { Content = "光标闪烁", IsChecked = CurrentSettings.BlinkCursor };
        var links = new CheckBox { Content = "Ctrl+Click 打开 OSC 8 链接", IsChecked = CurrentSettings.OpenLinks };
        var confirm = new CheckBox { Content = "关闭运行中的终端前确认", IsChecked = CurrentSettings.ConfirmClose };
        var cancel = new Button { Content = "取消", MinWidth = 80 };
        var apply = new Button { Content = "应用", MinWidth = 80, IsDefault = true };
        var dialog = new Window
        {
            Title = "终端设置", Width = 460, Height = 470, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(20), Spacing = 10, Children =
                {
                    new TextBlock { Text = "默认 shell（新终端生效）" }, shell, overrideBox,
                    new TextBlock { Text = "字号（DIP）" }, font,
                    new TextBlock { Text = "回滚行数" }, scrollback,
                    new TextBlock { Text = "响铃" }, bell,
                    blink, links, confirm,
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Children = { cancel, apply }
                    }
                }
            }
        };
        cancel.Click += (_, _) => dialog.Close(false);
        apply.Click += (_, _) => dialog.Close(true);
        if (!await Fufucode.App.Views.SkinDialog.ShowAsync<bool>(dialog, owner)) return;
        var selected = profiles[Math.Clamp(shell.SelectedIndex, 0, profiles.Count - 1)];
        var settings = (CurrentSettings with
        {
            ShellProfile = selected.Id,
            CommandOverride = overrideBox.Text ?? string.Empty,
            FontSize = (double)(font.Value ?? 13),
            ScrollbackLines = (int)(scrollback.Value ?? 1500),
            Bell = bell.SelectedItem as string ?? "visual",
            BlinkCursor = blink.IsChecked == true,
            OpenLinks = links.IsChecked == true,
            ConfirmClose = confirm.IsChecked == true
        }).Normalize();
        ApplyDisplaySettings(settings);
        SettingsChanged?.Invoke(settings);
    }

    internal Func<string, KeyEventArgs, bool>? ShortcutMatches
    {
        get => TerminalSurface.ShortcutMatches;
        set => TerminalSurface.ShortcutMatches = value;
    }
    internal Task PasteToDraftAsync() => TerminalSurface.PasteToDraftAsync();
    internal void UpdateShortcutLabels(Func<string, string> binding)
    {
        ToolTip.SetTip(CopyOutputButton, Label("复制选中的输出", "terminal.copy"));
        ToolTip.SetTip(FindButton, Label("查找输出", "terminal.find"));
        string Label(string text, string id) => string.IsNullOrEmpty(binding(id)) ? text : text + "（" + binding(id) + "）";
    }
    internal void FindOutput() => OpenFind();
    internal Task CopyOrInterruptAsync()
    {
        if (TerminalInput.IsFocused) { _dock?.SelectedTab?.Interrupt(); return Task.CompletedTask; }
        if (TerminalSurface.HasSelection) return TerminalSurface.CopySelectionAsync();
        _dock?.SelectedTab?.Interrupt(); return Task.CompletedTask;
    }

    private void RestartTab_Click(object? sender, RoutedEventArgs e)
    {
        if (TerminalTabs.SelectedItem is TerminalTabViewModel tab) tab.RestartSessionCommand.Execute(null);
    }

    private async void RenameTab_Click(object? sender, RoutedEventArgs e)
    {
        if (TerminalTabs.SelectedItem is not TerminalTabViewModel tab) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        var box = new TextBox { Text = tab.Title, MinWidth = 260 };
        var cancel = new Button { Content = "取消", MinWidth = 80 };
        var apply = new Button { Content = "重命名", MinWidth = 96, IsDefault = true };
        var dialog = new Window
        {
            Title = "重命名终端页签", Width = 380, Height = 170, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(20), Spacing = 14, Children =
                {
                    box,
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Children = { cancel, apply }
                    }
                }
            }
        };
        cancel.Click += (_, _) => dialog.Close(false);
        apply.Click += (_, _) => dialog.Close(true);
        dialog.Opened += (_, _) => { box.Focus(); box.SelectAll(); };
        if (await Fufucode.App.Views.SkinDialog.ShowAsync<bool>(dialog, owner) && !string.IsNullOrWhiteSpace(box.Text))
            tab.Title = box.Text.Trim();
    }

    private void RestartTerminal_Click(object? sender, RoutedEventArgs e)
    {
        _dock?.SelectedTab?.RestartSessionCommand.Execute(null);
        TerminalSurface.FocusForInput();
    }

    private async void CloseTerminal_Click(object? sender, RoutedEventArgs e) => await CloseSelectedAsync();
    internal async Task CloseSelectedAsync()
    {
        if (_confirmingClose || _dock?.SelectedTab is not { } tab) return;
        var dock = _dock;
        _confirmingClose = true;
        try
        {
            if (tab.IsRunning && !await ConfirmCloseAsync(tab)) return;
            dock.CloseTerminal(tab);
        }
        finally { _confirmingClose = false; }
    }

    private Task<bool> ConfirmCloseAsync(TerminalTabViewModel tab)
    {
        if (ConfirmCloseForTesting is { } test) return test(tab);
        if (TopLevel.GetTopLevel(this) is not Window owner) return Task.FromResult(false);
        var cancel = new Button { Content = "取消", MinWidth = 80 };
        var close = new Button { Content = "结束并关闭", MinWidth = 100 };
        var dialog = new Window { Title = "关闭终端", Width = 400, Height = 190, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel { Margin = new Thickness(20), Spacing = 16, Children =
            {
                new TextBlock { Text = $"关闭 {tab.Title} 将结束该终端及其运行进程。", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Children = { cancel, close } }
            } } };
        cancel.Click += (_, _) => dialog.Close(false);
        close.Click += (_, _) => dialog.Close(true);
        dialog.Opened += (_, _) => cancel.Focus();
        return Fufucode.App.Views.SkinDialog.ShowAsync<bool>(dialog, owner);
    }

    public TerminalDockView()
    {
        InitializeComponent();
        TerminalInput.AddHandler(InputElement.KeyDownEvent, OnInputKeyDown, RoutingStrategies.Tunnel);
        TerminalInput.TemplateApplied += (_, _) => HookPreedit();
        FindBox.AddHandler(InputElement.KeyDownEvent, OnFindKeyDown, RoutingStrategies.Tunnel);
        // 焦点确立与字符捕获原子同步：字符先到、焦点后到时由输出面在同一个处理里接手，
        // 快速输入不会丢首字符；命令框/查找框自己的文本输入不受影响。
        AddHandler(InputElement.TextInputEvent, OnDockTextInput, RoutingStrategies.Tunnel);
        // 点击停靠页空白处（页签条留白、面板底）同样把键盘焦点交给输出面，
        // 使“点击终端面板后立刻输入”不因焦点停留在别处而丢首字符。
        AddHandler(InputElement.PointerPressedEvent, OnDockPointerPressed, RoutingStrategies.Tunnel);
        TerminalSurface.SelectionChanged += (_, _) => { _dock?.RefreshOutputSelection(); TerminalSurface.InvalidateVisual(); };
        TerminalSurface.FindRequested += (_, _) => OpenFind();
        TerminalSurface.FindDismissRequested += (_, _) => CloseFind();
        TerminalSurface.PasteCompleted += (_, _) => TerminalInput.Focus();
        TerminalSurface.LinkActivated += (_, uri) => LinkActivated?.Invoke(uri);
        _composition.Changed += UpdateCompositionPreview;
        TerminalContextMenu.Opening += OnContextMenuOpening;
        TerminalSurface.PointerPressed += (_, e) => _surfacePointer = e.GetPosition(TerminalSurface);
        Loaded += (_, _) => { HookPreedit(); ResizeTerminal(); };
        SizeChanged += (_, _) => ResizeTerminal();
        OutputScroller.SizeChanged += (_, _) => ResizeTerminal();
        DataContextChanged += (_, _) => AttachDock();
        AttachedToVisualTree += (_, _) => AttachDock();
        DetachedFromVisualTree += (_, _) => DetachDock();
        OutputScroller.ScrollChanged += (_, _) =>
        {
            if (!_restoringScroll)
            {
                _followOutput = OutputScroller.Extent.Height - OutputScroller.Viewport.Height - OutputScroller.Offset.Y < 36;
                if (_tab is not null) { _tab.FollowOutput = _followOutput; _tab.ScrollOffsetY = OutputScroller.Offset.Y; }
            }
            TerminalSurface.InvalidateVisual();
        };
    }
    private void AttachDock()
    {
        DetachDock();
        _dock = DataContext as TerminalDockViewModel;
        if (_dock is null) return;
        _dock.PropertyChanged += OnDockChanged;
        AttachTab();
    }
    private void DetachDock()
    {
        _scrollGeneration++;
        if (_tab is not null && !_restoringScroll)
        {
            _tab.ScrollOffsetY = OutputScroller.Offset.Y;
            _tab.FollowOutput = _followOutput;
        }
        if (_dock is not null) _dock.PropertyChanged -= OnDockChanged;
        if (_tab is not null) { _tab.Buffer.Changed -= OnOutputChanged; _tab.PropertyChanged -= OnTabChanged; }
        _dock = null; _tab = null;
    }
    private void OnDockChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TerminalDockViewModel.SelectedTab)) AttachTab();
        if (e.PropertyName is nameof(TerminalDockViewModel.SelectedTab) or nameof(TerminalDockViewModel.IsVisible)) Dispatcher.UIThread.Post(ResizeTerminal, DispatcherPriority.Loaded);
    }
    private void AttachTab()
    {
        if (_tab is not null)
        {
            if (!_restoringScroll)
            {
                _tab.ScrollOffsetY = OutputScroller.Offset.Y;
                _tab.FollowOutput = _followOutput;
            }
            _tab.Buffer.Changed -= OnOutputChanged;
            _tab.PropertyChanged -= OnTabChanged;
            _tab.BellRequested -= OnBellRequested;
        }
        _tab = _dock?.SelectedTab;
        if (_tab is not null) { _tab.Buffer.Changed += OnOutputChanged; _tab.PropertyChanged += OnTabChanged; _tab.BellRequested += OnBellRequested; }
        _followOutput = _tab?.FollowOutput ?? true;
        // Find bar and output-selection state are per tab; re-project them after every switch.
        SyncFindState();
        _dock?.RefreshOutputSelection();
        _restoringScroll = true;
        var targetTab = _tab;
        var generation = ++_scrollGeneration;
        Dispatcher.UIThread.Post(() =>
        {
            if (generation != _scrollGeneration || !ReferenceEquals(targetTab, _tab)) return;
            ResizeTerminal();
            if (targetTab?.FollowOutput == true) OutputScroller.ScrollToEnd();
            else OutputScroller.Offset = new Vector(0, targetTab?.ScrollOffsetY ?? 0);
            _restoringScroll = false;
        }, DispatcherPriority.Loaded);
    }
    private void OnTabChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(TerminalTabViewModel.SearchStatus)) return;
        if (_dock?.SelectedTab is not { } tab || !tab.IsSearchOpen) return;
        if (tab.Search.Current is { } match) ScrollToRow(match.Row);
    }
    private void OnBellRequested()
    {
        switch (_tab?.Settings.Bell)
        {
            case "beep": Console.Beep(880, 90); break;
            case "none": break;
            default: TerminalSurface.FlashBell(); break; // “visual”
        }
    }

    private void OnOutputChanged()
    {
        var targetTab = _tab;
        var generation = _scrollGeneration;
        if (targetTab?.FollowOutput != true) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (generation == _scrollGeneration && ReferenceEquals(targetTab, _tab) && targetTab.FollowOutput) OutputScroller.ScrollToEnd();
        }, DispatcherPriority.Loaded);
    }
    private void ResizeTerminal()
    {
        if (_dock?.IsVisible != true || Bounds.Width < 100 || Bounds.Height < 100) return;
        _dock.SelectedTab?.Resize((int)Math.Max(10, OutputScroller.Viewport.Width / TerminalSurface.CellWidth), (int)Math.Max(3, OutputScroller.Viewport.Height / TerminalSurface.CellHeight));
    }
    private void HookPreedit()
    {
        if (_preeditHooked) return;
        var presenter = TerminalInput.GetVisualDescendants().OfType<TextPresenter>().FirstOrDefault();
        if (presenter is null) return;
        _preeditHooked = true;
        _imeGuard.OnPreeditChanged(presenter.PreeditText);
        _composition.Update(presenter.PreeditText);
        presenter.PropertyChanged += (_, e) =>
        {
            if (e.Property != TextPresenter.PreeditTextProperty) return;
            var preedit = e.NewValue as string;
            _imeGuard.OnPreeditChanged(preedit);
            _composition.Update(preedit);
        };
    }

    /// <summary>
    /// 终端输入接收端的捕获路径：字符到达时输出面还不是焦点元素（焦点仍在停靠条按钮、
    /// 页签或尚未提交的焦点切换中）时，在同一步里聚焦输出面并写入该字符。
    /// 命令框/查找框的文本输入与输出面自己已聚焦的情况一律放行，避免重复写入或抢占编辑器。
    /// </summary>
    private void OnDockTextInput(object? sender, TextInputEventArgs e)
    {
        if (e.Handled || string.IsNullOrEmpty(e.Text)) return;
        if (e.Source is TextBox) return;
        if (TerminalSurface.IsFocused) return;
        if (TerminalSurface.CaptureInput(e.Text)) e.Handled = true;
    }

    /// <summary>
    /// 停靠页空白处的左键点击把键盘焦点交给输出面。命令框、查找框、按钮与页签保留自己的
    /// 焦点语义（编辑器、键盘可达性、页签切换），不在此处抢占。
    /// </summary>
    private void OnDockPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.Source is not Visual source) return;
        if (source is TextBox || source.FindAncestorOfType<TextBox>() is not null) return;
        if (source is Button || source.FindAncestorOfType<Button>() is not null) return;
        if (source is ListBoxItem || source.FindAncestorOfType<ListBoxItem>() is not null) return;
        TerminalSurface.FocusForInput();
    }

    private void UpdateCompositionPreview()
    {
        CompositionPreview.IsVisible = _composition.IsComposing;
        CompositionPreviewText.Text = _composition.IsComposing ? "组合中：" + _composition.PreviewText : string.Empty;
    }

    /// <summary>右键菜单开启时同步各项可用性：无选区不可复制，链接菜单只在链节上出现。</summary>
    internal void SyncContextMenuState()
    {
        ContextCopyItem.IsEnabled = TerminalSurface.HasSelection;
        ContextOpenLinkItem.IsVisible = _contextLink is { Length: > 0 };
    }

    private string? _contextLink;

    /// <summary>记录右键位置上的 OSC 8 链接：菜单打开时按输出面自己的坐标解析。</summary>
    private void OnContextMenuOpening(object? sender, CancelEventArgs e)
    {
        _contextLink = TerminalSurface.LinkAt(_surfacePointer);
        SyncContextMenuState();
    }

    private Point _surfacePointer;

    private void ContextSelectAll_Click(object? sender, RoutedEventArgs e) => TerminalSurface.SelectAll();

    private void ContextOpenLink_Click(object? sender, RoutedEventArgs e)
    {
        if (_contextLink is { Length: > 0 } link) LinkActivated?.Invoke(link);
    }

    private void ContextCopy_Click(object? sender, RoutedEventArgs e) => _ = TerminalSurface.CopySelectionAsync();

    private void ContextPaste_Click(object? sender, RoutedEventArgs e) => _ = TerminalSurface.PasteToDraftAsync();

    /// <summary>清屏是纯本地操作：清空屏幕与滚动缓冲并把光标归位，不向 shell 写入任何字节。</summary>
    private void ContextClear_Click(object? sender, RoutedEventArgs e)
    {
        _dock?.SelectedTab?.Buffer.Append(TerminalControlSequences.ClearScreen);
        TerminalSurface.FocusForInput();
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            if (!_imeGuard.ShouldSwallowEnter()) _dock?.SelectedTab?.SendInput();
        }
        else if ((ShortcutMatches?.Invoke("terminal.copy-or-interrupt", e) ??
            (e.Key == Key.C && e.KeyModifiers == KeyModifiers.Control)) && string.IsNullOrEmpty(TerminalInput.SelectedText))
        {
            _dock?.SelectedTab?.Interrupt(); e.Handled = true;
        }
        else if (e.Key is Key.Up or Key.Down && _dock?.SelectedTab is { } tab)
        {
            HandleHistoryKey(e, tab);
        }
    }
    private void HandleHistoryKey(KeyEventArgs e, TerminalTabViewModel tab)
    {
        // Only the command draft browses local history; the surface still forwards arrows to the shell.
        // Multi-line drafts keep caret movement unless the caret already sits on the first/last line.
        var text = tab.InputText ?? string.Empty;
        var caret = Math.Clamp(TerminalInput.CaretIndex, 0, text.Length);
        var onFirstLine = text.AsSpan(0, caret).IndexOf('\n') < 0;
        var onLastLine = text.AsSpan(caret).IndexOf('\n') < 0;
        var handled = e.Key == Key.Up ? onFirstLine && tab.HistoryPrevious() : onLastLine && tab.HistoryNext();
        if (!handled) return;
        TerminalInput.CaretIndex = TerminalInput.Text?.Length ?? 0;
        e.Handled = true;
    }

    private void CopyOutput_Click(object? sender, RoutedEventArgs e) => _ = TerminalSurface.CopySelectionAsync();
    internal Task CopySelectionAsync() => TerminalSurface.CopySelectionAsync();

    private void ToggleFind_Click(object? sender, RoutedEventArgs e)
    {
        if (_dock?.SelectedTab is not { } tab) return;
        if (tab.IsSearchOpen) CloseFind(); else OpenFind();
    }

    private void OpenFind()
    {
        if (_dock?.SelectedTab is not { } tab) return;
        tab.IsSearchOpen = true;
        SyncFindState();
        if (tab.SearchQuery.Length > 0) { tab.RunSearch(); if (tab.Search.Current is { } match) ScrollToRow(match.Row); }
        Dispatcher.UIThread.Post(() => { if (tab.IsSearchOpen) { FindBox.Focus(); FindBox.SelectAll(); } }, DispatcherPriority.Loaded);
    }

    private void CloseFind()
    {
        if (_dock?.SelectedTab is { } tab)
        {
            tab.IsSearchOpen = false;
            tab.Search.Clear();
            tab.SearchStatus = string.Empty;
        }
        SyncFindState();
        TerminalSurface.InvalidateVisual();
        TerminalSurface.Focus();
    }

    private void SyncFindState()
    {
        var tab = _dock?.SelectedTab;
        var open = tab?.IsSearchOpen == true;
        TerminalSurface.FindActive = open;
        FindButton.Classes.Set("active", open);
        FindCaseButton.Classes.Set("active", tab?.SearchMatchCase == true);
    }

    private void CloseFind_Click(object? sender, RoutedEventArgs e) => CloseFind();

    private void ToggleFindCase_Click(object? sender, RoutedEventArgs e)
    {
        if (_dock?.SelectedTab is not { } tab) return;
        tab.SearchMatchCase = !tab.SearchMatchCase;
        SyncFindState();
        if (tab.Search.Current is { } match) ScrollToRow(match.Row);
        TerminalSurface.InvalidateVisual();
    }

    private void FindPrevious_Click(object? sender, RoutedEventArgs e) => FindStep(-1);
    private void FindNext_Click(object? sender, RoutedEventArgs e) => FindStep(1);

    private void FindStep(int direction)
    {
        if (_dock?.SelectedTab is not { } tab || !tab.IsSearchOpen) return;
        var match = tab.MoveSearch(direction);
        if (match is { } hit) ScrollToRow(hit.Row);
        TerminalSurface.InvalidateVisual();
    }

    private void OnFindKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; CloseFind(); return; }
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        FindStep(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
    }

    private void ReturnToBottom_Click(object? sender, RoutedEventArgs e) => ReturnToBottom();

    internal void ReturnToBottom()
    {
        var tab = _dock?.SelectedTab ?? _tab;
        if (tab is not null) tab.FollowOutput = true;
        _followOutput = true;
        OutputScroller.ScrollToEnd();
        if (tab is not null) tab.ScrollOffsetY = OutputScroller.Offset.Y;
        TerminalSurface.InvalidateVisual();
    }

    /// <summary>Scroll a fixed-cell row into view; programmatic scrolling never re-enables follow output.</summary>
    internal void ScrollToRow(int row)
    {
        var centered = 8 + row * TerminalSurface.CellHeight - Math.Max(0, (OutputScroller.Viewport.Height - TerminalSurface.CellHeight) / 2);
        var max = Math.Max(0, OutputScroller.Extent.Height - OutputScroller.Viewport.Height);
        OutputScroller.Offset = new Vector(0, Math.Clamp(centered, 0, max));
        if (_tab is not null) _tab.ScrollOffsetY = OutputScroller.Offset.Y;
        TerminalSurface.InvalidateVisual();
    }
}
