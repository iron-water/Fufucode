using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Fufucode.App.ViewModels;

namespace Fufucode.App.Views;

public partial class ChatPage
{
    private readonly TranslateTransform _composerOffset = new();
    private readonly TranslateTransform _composerRevealOffset = new();
    private int _composerAnimationVersion;
    private bool _composerVisibilityInitialized;
    private bool _composerExpanded = true;
    private double _composerProgress = 1;

    private void InitializeComposerMotion()
    {
        ComposerCard.RenderTransform = _composerOffset;
        ComposerRevealButton.RenderTransform = _composerRevealOffset;
        ComposerCard.SizeChanged += (_, _) => { UpdateComposerClearance(); UpdateComposerResponsive(); };
        _inputMinHeight = InputBox.MinHeight;
        InputBox.SizeChanged += OnInputSizeChanged;
        foreach (var button in new Control[] { SendButton, StopButton, RestartButton })
            button.PropertyChanged += (_, change) =>
            {
                if (change.Property == IsVisibleProperty && change.NewValue is true) MorphIn(button);
            };
        ComposerCard.PropertyChanged += (_, change) =>
        {
            if (change.Property == IsVisibleProperty || change.Property == MarginProperty ||
                change.Property == Border.CornerRadiusProperty)
                UpdateComposerClearance();
        };
    }

    private void SetComposerExpanded(bool expanded, bool animate = true)
    {
        if (_composerVisibilityInitialized && expanded == _composerExpanded && animate) return;
        var initialized = _composerVisibilityInitialized;
        _composerVisibilityInitialized = true;
        _composerExpanded = expanded;
        var version = ++_composerAnimationVersion;
        var topLevel = TopLevel.GetTopLevel(this);
        var target = expanded ? 1d : 0d;
        if (!animate || !initialized || Motion.Reduced || topLevel is null)
        {
            ApplyComposerProgress(target);
            FinishComposerMotion();
            return;
        }

        // 保持卡片自身的自然尺寸，只把它滑出视口，文字和工具栏不随折叠被挤压。
        ComposerCard.IsVisible = true;
        ComposerRevealButton.IsVisible = true;
        if (!expanded && ComposerCard.IsKeyboardFocusWithin) ComposerRevealButton.Focus();
        ApplyComposerProgress(_composerProgress);
        var from = _composerProgress;
        var duration = (expanded ? Motion.SlowDuration : Motion.StandardDuration).TotalMilliseconds *
            Math.Abs(target - from);
        if (duration < 1)
        {
            ApplyComposerProgress(target);
            FinishComposerMotion();
            return;
        }

        // 使用窗口的动画帧，避免后台优先级计时器被布局和文本渲染饿死。
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        void Frame(TimeSpan _)
        {
            if (version != _composerAnimationVersion) return;
            var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var progress = Motion.Reduced ? 1 : Math.Clamp(elapsed / duration, 0, 1);
            ApplyComposerProgress(from + (target - from) * (1 - Math.Pow(1 - progress, 3)));
            if (progress < 1) topLevel.RequestAnimationFrame(Frame);
            else FinishComposerMotion();
        }
        topLevel.RequestAnimationFrame(Frame);
    }

    private double _inputMinHeight;
    private IDisposable? _inputHeightAnimation;
    private bool _inputHeightHeld;

    /// <summary>
    /// 输入框变矮（发送清空、删除多行）时从原高度缓降到新高度，输入卡与时间线留白随之平滑移动，
    /// 而不是一帧跳 24px。变高保持即时：限制增高会裁掉或滚动正在输入的文字。
    /// </summary>
    private void OnInputSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (_inputHeightHeld || !e.HeightChanged || e.PreviousSize.Height <= 0) return;
        var from = e.PreviousSize.Height;
        var to = e.NewSize.Height;
        if (to >= from - 1 || Motion.Reduced || Integration.MemoryProfile.DisableVisualEffects || !_composerExpanded) return;
        _inputHeightAnimation?.Dispose();
        _inputHeightHeld = true;
        InputBox.MinHeight = from;
        _inputHeightAnimation = Motion.AnimateValue(from, Math.Max(to, _inputMinHeight), Motion.Base,
            height => InputBox.MinHeight = height, () =>
            {
                _inputHeightHeld = false;
                _inputHeightAnimation = null;
                InputBox.MinHeight = _inputMinHeight;
            }, TopLevel.GetTopLevel(this));
    }

    /// <summary>发送/停止/重发共用一个位置：新出现的按钮淡入，切换读作一次交叉淡化而不是硬切。</summary>
    private static void MorphIn(Control button)
    {
        if (Motion.Reduced || Integration.MemoryProfile.DisableVisualEffects) return;
        _ = Motion.AnimateValue(0, 1, Motion.Base, value => button.Opacity = value,
            () => button.Opacity = 1, TopLevel.GetTopLevel(button));
    }

    private void ApplyComposerProgress(double progress)
    {
        _composerProgress = progress;
        ComposerCard.Opacity = progress;
        ComposerCard.IsHitTestVisible = _composerExpanded;
        ComposerRevealButton.Opacity = 1 - progress;
        ComposerRevealButton.IsHitTestVisible = !_composerExpanded;
        _composerRevealOffset.Y = -6 * progress;
        UpdateComposerClearance();
    }

    private void FinishComposerMotion()
    {
        ComposerCard.IsVisible = _composerExpanded;
        ComposerRevealButton.IsVisible = !_composerExpanded;
        UpdateComposerClearance();
    }

    /// <summary>
    /// 窄栏（侧边聊缩到最小宽度）下收起模型/思考等模式按钮，只留附加与发送：
    /// 这些按钮是固定宽度，保留会把工具栏挤出卡片。
    /// </summary>
    private void UpdateComposerResponsive()
    {
        var width = ComposerCard.Bounds.Width;
        if (width <= 0) return;
        var narrow = width < 320;
        if (ComposerModes.IsVisible == !narrow) return;
        ComposerModes.IsVisible = !narrow;
    }

    private void UpdateComposerClearance()
    {
        var fullHeight = ComposerCard.Bounds.Height + ComposerCard.Margin.Bottom;
        _composerOffset.Y = (1 - _composerProgress) * fullHeight;
        var clearance = ComposerCard.IsVisible ? fullHeight * _composerProgress : 0;
        var top = Appearance.Density switch { "compact" => 12, "airy" => 24, _ => 20 };
        var bottom = Appearance.Density switch { "compact" => 28, "airy" => 40, _ => 32 };
        var padding = new Thickness(0, top, 0, clearance + bottom);
        if (TimelineItems.Padding != padding)
        {
            var follow = DataContext is MainViewModel { Timeline.IsAtBottom: true };
            if (follow)
                _programmaticUntilTicks = System.Diagnostics.Stopwatch.GetTimestamp() +
                    (long)(System.Diagnostics.Stopwatch.Frequency * 0.12);
            TimelineItems.Padding = padding;
            if (follow && TopLevel.GetTopLevel(this) is not null)
                Dispatcher.UIThread.Post(() =>
                {
                    if (TopLevel.GetTopLevel(this) is not null &&
                        DataContext is MainViewModel { Timeline.IsAtBottom: true }) ScrollToEnd();
                }, DispatcherPriority.Loaded);
        }
        BackToLatestHost.Margin = new Thickness(0, 0, 20, clearance + 10);
        ContentBandChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        SetComposerExpanded(_composerExpanded, animate: false);
        base.OnDetachedFromVisualTree(e);
    }
}
