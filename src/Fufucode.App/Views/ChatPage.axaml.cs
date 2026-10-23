using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
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

    /// <summary>已经播放过进入动画的条目。虚拟化在滚动时会重建容器，不能重复播放。</summary>
    private readonly System.Collections.Generic.HashSet<string> _animatedEntries = new(StringComparer.Ordinal);

    /// <summary>输入区可见性是否已经初始化过；首次应用外观不播动画。</summary>
    private bool _composerVisibilityInitialized;
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

        Loaded += (_, _) => { HookInputPreedit(); RefreshSessionContext(); };
        SessionProjectChip.Click += async (_, _) =>
        {
            if (SessionContextRequested?.Invoke() is not { CanChangeProject: true } info) return;
            if (ChangeProjectRequested is null) return;
            await ChangeProjectRequested();
        };

        // D-R4-1：会话头与输入区高度会随内容变化（草稿增长、队列条、待恢复提示），
        // 窗口层据此重算抽屉避让带；只发事件，不做任何布局决策。
        ConversationHeader.LayoutUpdated += (_, _) => ContentBandChanged?.Invoke(this, EventArgs.Empty);
        ComposerCard.LayoutUpdated += (_, _) => ContentBandChanged?.Invoke(this, EventArgs.Empty);

        DataContextChanged += (_, _) =>
        {
            PluginContentBridge.Reset();
            _animatedEntries.Clear();
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
                // 富内容插件只拿到这两个窄回调：提交一段文本、填入输入框。
                PluginContentBridge.SubmitAsync = text => vm.Input.SubmitExternalAsync(text);
                PluginContentBridge.InsertDraft = text => vm.Input.DraftText = text;
                RefreshComposerPluginMenu();
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

        // 虚拟化滚动会重新实例化容器；按条目 id 记账，避免来回滑动时反复淡入闪烁。
        if (control.DataContext is TimelineEntryViewModel entry && !_animatedEntries.Add(entry.Id)) return;
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
        _composerVisibilityInitialized = true;
        Motion.SetVisible(ComposerCard, true, offsetY: 8);
        Motion.SetVisible(ComposerRevealButton, false);
        InputBox.Focus();
        if (DataContext is MainViewModel { Timeline.IsAtBottom: true })
            TimelineScroller.ScrollToEnd();
    }

    /// <summary>展开/收起某个文件的内联 diff。</summary>
    private void OnToggleFileDiff(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: TurnFileChangeViewModel file }) file.ToggleDiff();
    }

    /// <summary>复制工具输出原文，便于在别处继续处理。</summary>
    private async void OnCopyToolOutput(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: ToolCallBlockViewModel tool }) return;
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        try { await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, tool.Output ?? string.Empty); }
        catch (Exception) { }
    }

    /// <summary>引用 chip：链接在内置浏览器打开，失败退回系统浏览器。</summary>
    private void OnOpenMessageLink(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: MessageLinkViewModel link }) return;
        if (!Uri.TryCreate(link.Url, UriKind.Absolute, out var uri)) return;

        if (TopLevel.GetTopLevel(this) is MainWindow window && window.OpenUrlInBrowser(link.Url)) return;
        if (TopLevel.GetTopLevel(this)?.Launcher is { } launcher) _ = launcher.LaunchUriAsync(uri);
    }

    /// <summary>引用 chip：文件在侧栏预览；相对路径按当前项目根解析。</summary>
    private void OnOpenMessageFile(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: MessageFileViewModel file }) return;
        if (DataContext is not MainViewModel vm) return;

        var path = file.Path;
        if (!Path.IsPathRooted(path) && !string.IsNullOrWhiteSpace(vm.ProjectRootPath))
            path = Path.Combine(vm.ProjectRootPath, path);

        vm.WorkspacePanel.OpenFilePreview(path);
        if (TopLevel.GetTopLevel(this) is MainWindow window) window.OpenDockPage("files", DockZone.Right);
    }

    /// <summary>
    /// 点击工具组：先切换展开；如果展开的是仍在运行的组，说明用户想看最新执行，
    /// 直接回到底部并继续跟随流式输出。非运行中的组只展开，不移动视点。
    /// </summary>
    private void OnToolGroupClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: ToolGroupViewModel group }) return;

        if (!group.IsExpanded)
        {
            var offset = TimelineScroller.Offset.Y;
            var followLatest = group.IsActive;
            group.ToggleExpanded();
            if (followLatest) ScrollToEnd();
            else RestoreScrollOffset(offset);
            return;
        }

        group.ToggleExpanded();
        var expandedOffset = TimelineScroller.Offset.Y;
        RestoreScrollOffset(expandedOffset);
    }

    /// <summary>
    /// 展开/收起代码块会显著改变某一条的高度；Avalonia 会把焦点控件带回视野，
    /// 表现为整页跳回这段输出的开头。这里在布局完成后恢复原来的滚动位置。
    /// </summary>
    private void OnBlockLayoutToggled(object? sender, RoutedEventArgs e) =>
        RestoreScrollOffset(TimelineScroller.Offset.Y);

    private void RestoreScrollOffset(double offset)
    {
        var atBottom = DataContext is MainViewModel vm && vm.Timeline.IsAtBottom;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (atBottom) ScrollToEnd();
            else TimelineScroller.Offset = new Vector(TimelineScroller.Offset.X, offset);
        }, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>流式过程中新出现的工具卡：轻微淡入，避免突然插入。</summary>
    private void OnTransientBlockLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control control) return;
        control.Loaded -= OnTransientBlockLoaded;
        Motion.AnimateIn(control, offsetY: 3, Motion.FastDuration);
    }

    private void UpdateComposerToolbar()
    {
        var narrow = ComposerToolbar.Bounds.Width < 520;
        foreach (var control in ComposerToolbar.Children) Grid.SetRow(control, narrow ? 1 : 0);
        Grid.SetRow(ComposerModes, 0);
        Grid.SetColumn(ComposerModes, narrow ? 0 : 4);
        Grid.SetColumnSpan(ComposerModes, narrow ? 6 : 1);
        // 项目 chip 始终在第一行；窄窗时模式组换到第二行，避免两者重叠。
        Grid.SetRow(SessionContextBar, 0);
        Grid.SetColumn(SessionContextBar, narrow ? 0 : 3);
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

    /// <summary>超过该字符数的粘贴转为会话临时附件，避免长文本挤占输入框。</summary>
    private const int LargePasteThreshold = 600;

    /// <summary>附件内嵌文本上限；超过时只保留前缀并明确说明截断。</summary>
    internal const int MaxEmbeddedPasteChars = 200_000;

    // Tunnel 阶段调用（见构造函数注释）：先于 TextBox 自身处理。
    private void InputBox_KeyDown(object? sender, KeyEventArgs e)
    {
        // 大段粘贴：先接管 Ctrl+V，读完剪贴板再决定是普通粘贴还是转为附件。
        if (e.Key == Key.V &&
            (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Alt)) == KeyModifiers.Control)
        {
            e.Handled = true;
            _ = PasteComposerAsync();
            return;
        }

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
            // RelayCommand.Execute 不检查 CanExecute；空白输入必须在这里拦住，
            // 否则回车会直接发出一个空消息。
            var steering = e.KeyModifiers.HasFlag(KeyModifiers.Control) && vm.Input.IsTurnActive;
            if (steering ? !vm.Input.CanSteer : !vm.Input.CanSend)
            {
                e.Handled = true;
                return;
            }

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

    /// <summary>
    /// 构造大段粘贴附件的上下文。内容直接内嵌，确保 Agent 不依赖读取 workspace 外的临时文件；
    /// 超过上限时保留前缀并明确标注截断与完整文件位置。
    /// </summary>
    internal static string PastedAttachmentContext(string text, string path)
    {
        if (text.Length <= MaxEmbeddedPasteChars) return "粘贴内容：\n" + text;
        return "粘贴内容（已截断）：\n" + text[..MaxEmbeddedPasteChars] +
               $"\n\n[粘贴内容过长，仅内嵌前 {MaxEmbeddedPasteChars:N0} 字符；完整内容已保存到 {path}]";
    }

    /// <summary>
    /// Ctrl+V 的统一入口：短文本走 TextBox 原生粘贴；长文本写成会话临时文件并作为附件引用。
    /// 临时文件只落在系统临时目录，不写入项目工作区。
    /// </summary>
    private async Task PasteComposerAsync()
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            InputBox.Paste();
            return;
        }

        string? text = null;
        try { text = await clipboard.TryGetTextAsync(); }
        catch { /* 剪贴板不可读时退回原生粘贴 */ }

        if (string.IsNullOrEmpty(text) || text.Length <= LargePasteThreshold)
        {
            InputBox.Paste();
            return;
        }

        try
        {
            var directory = Path.Combine(Path.GetTempPath(), "fufucode", "pasted");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory,
                $"pasted-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.txt");
            await File.WriteAllTextAsync(path, text, new UTF8Encoding(false));
            if (DataContext is MainViewModel vm)
            {
                // 直接构造附件上下文：临时文件在 workspace 外，不能只给 Agent 一个读不到的路径。
                vm.Input.AddAttachment(new AttachedFile(path, PastedAttachmentContext(text, path)));
                vm.Timeline.AddNotification($"粘贴内容较长（{text.Length:N0} 字符），已转为附件，发送时一并引用。");
                OnInputFocusRequested();
            }
        }
        catch (Exception error)
        {
            ReportActionError("大段粘贴转附件失败：" + error.GetBaseException().Message);
            InputBox.Paste();
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
