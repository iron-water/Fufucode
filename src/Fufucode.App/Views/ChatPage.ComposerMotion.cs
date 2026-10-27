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

    private void ApplyComposerProgress(double progress)
    {
        _composerProgress = progress;
        ComposerCard.Opacity = progress;
        ComposerCard.IsHitTestVisible = _composerExpanded;
        ComposerRevealButton.Opacity = 1 - progress;
        ComposerRevealButton.IsHitTestVisible = !_composerExpanded;
        _composerRevealOffset.Y = -6 * progress;
        ComposerBlurBand.Strength = progress;
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
        // 尾部留白与模糊光晕跟随同一进度，不创建底栏或改变聊天视口高度。
        // 光晕环绕整张输入卡（左右上下各向外扩 RingThickness）：
        // 它横跨头像列与正文列，卡片只在正文列，所以左缘要补上头像列宽度再外扩。
        var ring = ComposerBlurBand.RingThickness;
        var cardLeft = ReadingColumn.ColumnDefinitions[0].ActualWidth;
        var cardHeight = ComposerCard.Bounds.Height;
        var cardBottom = ComposerCard.Margin.Bottom;
        if (cardHeight > 0)
        {
            var slide = (1 - _composerProgress) * fullHeight;
            ComposerBlurBand.TargetCornerRadius = ComposerCard.CornerRadius.TopLeft;
            ComposerBlurBand.Height = cardHeight + ring * 2;
            ComposerBlurBand.Margin = new Thickness(
                cardLeft - ring, 0, -ring,
                Math.Max(0, cardBottom - ring) + slide);
            ComposerBlurBand.Opacity = _composerProgress;
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
