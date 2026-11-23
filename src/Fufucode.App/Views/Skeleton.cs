using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fufucode.App.Integration;

namespace Fufucode.App.Views;

/// <summary>
/// Microsoft Store 式加载占位：页面形状的暗色骨架，加一道所有骨块同步的斜向高光扫过。
/// 骨块是普通布局里带 <c>skeletonBone</c> 类的 Border，形状随内容区重排；
/// 高光在覆盖层统一绘制并裁剪到骨块，所以多个骨块的扫光永远对齐。
/// 系统减少动画或关闭视觉特效时只保留静态骨架。
/// </summary>
internal sealed class SkeletonSurface : Grid
{
    private readonly ShimmerLayer _shimmer;

    public SkeletonSurface(Control layout)
    {
        IsHitTestVisible = false;
        Focusable = false;
        Children.Add(layout);
        _shimmer = new ShimmerLayer(layout);
        Children.Add(_shimmer);
    }

    /// <summary>宿主在重新显示（例如父层从隐藏恢复）时调用，保证扫光继续。</summary>
    public void Restart() => _shimmer.Kick();

    internal bool IsSweepingForTesting => _shimmer.Running;

    public static Border Bone(double width = double.NaN, double height = 12, bool round = false)
    {
        // 定宽骨块与文字一样从行首开始；不定宽的随容器拉伸。
        var bone = new Border { Width = width, Height = height,
            HorizontalAlignment = double.IsNaN(width) ? HorizontalAlignment.Stretch : HorizontalAlignment.Left };
        bone.Classes.Add("skeletonBone");
        if (round) bone.CornerRadius = new CornerRadius(height / 2);
        return bone;
    }

    /// <summary>一行文字骨块：按可用宽度的比例伸缩，避免宽窗口下骨块过短。</summary>
    public static Control Line(double fraction, double height = 12)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions(FormattableString.Invariant($"{fraction:0.###}*,{1 - fraction:0.###}*")) };
        row.Children.Add(Bone(height: height));
        return row;
    }

    private sealed class ShimmerLayer : Control
    {
        // 1.2s 扫过 + 0.6s 停顿，与 Store 的节奏相近，不抢正文注意力。
        private static readonly TimeSpan Sweep = TimeSpan.FromMilliseconds(1200);
        private static readonly TimeSpan Cycle = TimeSpan.FromMilliseconds(1800);
        private static readonly long Origin = System.Diagnostics.Stopwatch.GetTimestamp();
        public static readonly StyledProperty<IBrush?> HighlightProperty =
            AvaloniaProperty.Register<ShimmerLayer, IBrush?>(nameof(Highlight));
        private readonly Control _layout;

        public ShimmerLayer(Control layout)
        {
            _layout = layout;
            IsHitTestVisible = false;
            Bind(HighlightProperty, new DynamicResourceExtension("AppSkeletonShimmer"));
            AttachedToVisualTree += (_, _) => Kick();
            PropertyChanged += (_, e) => { if (e.Property == IsVisibleProperty && IsVisible) Kick(); };
        }

        public IBrush? Highlight { get => GetValue(HighlightProperty); set => SetValue(HighlightProperty, value); }
        public bool Running { get; private set; }
        private static bool Static => Motion.Reduced || MemoryProfile.DisableVisualEffects;

        public void Kick()
        {
            if (Running || Static || TopLevel.GetTopLevel(this) is not { } top) return;
            Running = true;
            top.RequestAnimationFrame(Frame);
        }

        private void Frame(TimeSpan _)
        {
            // 不可见即停：遮罩隐藏后不再占用帧；再次绘制时由 Render 重新启动。
            if (!IsEffectivelyVisible || TopLevel.GetTopLevel(this) is not { } top || Static)
            {
                Running = false;
                return;
            }
            InvalidateVisual();
            top.RequestAnimationFrame(Frame);
        }

        public override void Render(DrawingContext context)
        {
            if (!Running && !Static) Dispatcher.UIThread.Post(Kick, DispatcherPriority.Render);
            if (Static || Highlight is not ISolidColorBrush highlight || Bounds.Width < 1) return;
            var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(Origin).TotalMilliseconds % Cycle.TotalMilliseconds;
            if (elapsed > Sweep.TotalMilliseconds) return;
            var clip = BoneGeometry();
            if (clip.Children.Count == 0) return;
            var band = Math.Clamp(Bounds.Width * .18, 120, 360);
            var progress = Motion.CubicEase.Ease(elapsed / Sweep.TotalMilliseconds);
            var center = -band + (Bounds.Width + band * 2) * progress;
            var color = highlight.Color;
            // 斜向光带：起止点沿 x 错开一个高度比例，形成 Store 那样略微倾斜的扫光。
            var slant = Math.Min(Bounds.Height, 400) * .35;
            var brush = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(center - band, 0, RelativeUnit.Absolute),
                EndPoint = new RelativePoint(center + band, slant, RelativeUnit.Absolute),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 0),
                    new GradientStop(color, .5),
                    new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1),
                },
            };
            using (context.PushGeometryClip(clip))
                context.FillRectangle(brush, new Rect(Bounds.Size));
        }

        private GeometryGroup BoneGeometry()
        {
            var group = new GeometryGroup();
            foreach (var bone in Bones(_layout))
            {
                if (bone.TranslatePoint(default, this) is not { } origin) continue;
                var radius = bone.CornerRadius.TopLeft;
                group.Children.Add(new RectangleGeometry(new Rect(origin, bone.Bounds.Size)) { RadiusX = radius, RadiusY = radius });
            }
            return group;
        }

        private static IEnumerable<Border> Bones(Control root) => root.GetVisualDescendants().OfType<Border>()
            .Where(border => border.Classes.Contains("skeletonBone") && border.IsEffectivelyVisible && border.Bounds.Width > 0);
    }
}

/// <summary>工作台形状的骨架：会话列表、会话头、交替的消息与输入卡。用于会话启动与切换。</summary>
internal static class WorkbenchSkeleton
{
    public static SkeletonSurface Create()
    {
        var root = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        var sidebar = new StackPanel { Width = 232, Spacing = 14, Margin = new Thickness(20, 24, 16, 0) };
        sidebar.Children.Add(SkeletonSurface.Bone(96, 14));
        for (var i = 0; i < 7; i++)
        {
            var row = new StackPanel { Spacing = 6 };
            row.Children.Add(SkeletonSurface.Line(i % 3 == 0 ? .9 : i % 3 == 1 ? .7 : .8, 12));
            row.Children.Add(SkeletonSurface.Line(.45, 10));
            sidebar.Children.Add(row);
        }
        root.Children.Add(sidebar);

        var chat = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Margin = new Thickness(24, 0) };
        var header = new StackPanel { Spacing = 8, MaxWidth = 768, Margin = new Thickness(0, 22, 0, 0) };
        header.Children.Add(SkeletonSurface.Bone(220, 16));
        header.Children.Add(SkeletonSurface.Bone(140, 10));
        chat.Children.Add(header);

        var column = new StackPanel { Spacing = 28, MaxWidth = 768, Margin = new Thickness(0, 36, 0, 0) };
        column.Children.Add(UserTurn(.55));
        column.Children.Add(AssistantTurn(new[] { .95, .88, .7 }));
        column.Children.Add(UserTurn(.4));
        column.Children.Add(AssistantTurn(new[] { .92, .6 }));
        Grid.SetRow(column, 1); chat.Children.Add(column);

        var composer = SkeletonSurface.Bone(height: 96);
        composer.Classes.Add("block");
        composer.MaxWidth = 768;
        composer.Margin = new Thickness(0, 0, 0, 24);
        Grid.SetRow(composer, 2); chat.Children.Add(composer);
        Grid.SetColumn(chat, 1); root.Children.Add(chat);

        var surface = new SkeletonSurface(root);
        // 窄窗只保留会话列，与工作台的响应式一致。
        surface.SizeChanged += (_, e) => sidebar.IsVisible = e.NewSize.Width >= 900;
        return surface;
    }

    private static Control UserTurn(double fraction)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions(FormattableString.Invariant($"{1 - fraction:0.###}*,{fraction:0.###}*")) };
        var bubble = SkeletonSurface.Bone(height: 36);
        bubble.Classes.Add("block");
        Grid.SetColumn(bubble, 1); row.Children.Add(bubble);
        return row;
    }

    private static Control AssistantTurn(double[] lines)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 12 };
        var avatar = SkeletonSurface.Bone(28, 28, round: true);
        avatar.VerticalAlignment = VerticalAlignment.Top;
        row.Children.Add(avatar);
        var text = new StackPanel { Spacing = 10, Margin = new Thickness(0, 6, 0, 0) };
        foreach (var line in lines) text.Children.Add(SkeletonSurface.Line(line));
        Grid.SetColumn(text, 1); row.Children.Add(text);
        return row;
    }
}
