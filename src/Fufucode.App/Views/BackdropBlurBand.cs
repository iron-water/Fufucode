using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Avalonia.Skia;
using Avalonia.VisualTree;
using Fufucode.App.Integration;
using SkiaSharp;

namespace Fufucode.App.Views;

/// <summary>
/// 在合成器当前帧内模糊身后的内容，形成环绕目标的环形光晕：
/// 贴着目标边缘全强度，向外在 <see cref="RingThickness"/> 内快速衰减到清晰。
/// </summary>
public sealed class BackdropBlurBand : Control
{
    private CompositionCustomVisual? _visual;
    private readonly List<Visual> _ancestors = [];
    private bool _active;

    internal event Action<bool>? VisualEffectsStateChanged;

    public static readonly StyledProperty<double> BlurRadiusProperty =
        AvaloniaProperty.Register<BackdropBlurBand, double>(nameof(BlurRadius), 14d,
            validate: value => double.IsFinite(value) && value >= 0);

    public double BlurRadius
    {
        get => GetValue(BlurRadiusProperty);
        set => SetValue(BlurRadiusProperty, value);
    }

    public static readonly StyledProperty<double> StrengthProperty =
        AvaloniaProperty.Register<BackdropBlurBand, double>(nameof(Strength), 1d,
            validate: value => double.IsFinite(value) && value >= 0 && value <= 1);

    public double Strength
    {
        get => GetValue(StrengthProperty);
        set => SetValue(StrengthProperty, value);
    }

    /// <summary>环形光晕的厚度：从目标边缘向外衰减到清晰的距离。越小衰减越快。</summary>
    public static readonly StyledProperty<double> RingThicknessProperty =
        AvaloniaProperty.Register<BackdropBlurBand, double>(nameof(RingThickness), 16d,
            validate: value => double.IsFinite(value) && value > 0);

    public double RingThickness
    {
        get => GetValue(RingThicknessProperty);
        set => SetValue(RingThicknessProperty, value);
    }

    public BackdropBlurBand()
    {
        IsHitTestVisible = false;
        Loaded += (_, _) => AttachVisual();
    }

    private void AttachVisual()
    {
        if (_visual is not null ||
            ElementComposition.GetElementVisual(this)?.Compositor is not { } compositor) return;
        _visual = compositor.CreateCustomVisual(new BlurHandler());
        _visual.ClipToBounds = false;
        ElementComposition.SetElementChildVisual(this, _visual);
        for (var ancestor = this.GetVisualParent(); ancestor is not null; ancestor = ancestor.GetVisualParent())
        {
            _ancestors.Add(ancestor);
            ancestor.PropertyChanged += AncestorChanged;
        }
        MemoryProfile.VisualEffectsChanged += VisualEffectsChanged;
        UpdateVisual();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _visual?.SendHandlerMessage(new BlurState(default, default, 0, 0, 16, false));
        SetActive(false);
        foreach (var ancestor in _ancestors) ancestor.PropertyChanged -= AncestorChanged;
        _ancestors.Clear();
        MemoryProfile.VisualEffectsChanged -= VisualEffectsChanged;
        ElementComposition.SetElementChildVisual(this, null);
        _visual = null;
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty || change.Property == BlurRadiusProperty ||
            change.Property == StrengthProperty || change.Property == IsVisibleProperty ||
            change.Property == RingThicknessProperty)
            UpdateVisual();
    }

    private void AncestorChanged(object? sender, AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property == IsVisibleProperty || change.Property == Window.WindowStateProperty)
            UpdateVisual();
    }

    private void VisualEffectsChanged()
    {
        if (Dispatcher.UIThread.CheckAccess()) UpdateVisual();
        else Dispatcher.UIThread.Post(UpdateVisual);
    }

    private void UpdateVisual()
    {
        if (_visual is null) return;
        var halo = Math.Ceiling(BlurRadius * 3) + 2;
        var sample = new Rect(0, 0, Bounds.Width + halo * 2, Bounds.Height + halo * 2);
        var output = new Rect(halo, halo, Bounds.Width, Bounds.Height);
        _visual.Offset = new Vector3D(-halo, -halo, 0);
        _visual.Size = new Vector(sample.Width, sample.Height);
        var active = IsEffectivelyVisible && !MemoryProfile.DisableVisualEffects &&
            TopLevel.GetTopLevel(this) is not Window { WindowState: WindowState.Minimized } &&
            Bounds.Width > 0 && Bounds.Height > 0 && BlurRadius > 0 && Strength > 0;
        _visual.Visible = active;
        _visual.SendHandlerMessage(new BlurState(sample, output, (float)BlurRadius,
            (float)Strength, (float)RingThickness, active));
        SetActive(active);
    }

    private void SetActive(bool active)
    {
        if (_active == active) return;
        _active = active;
        VisualEffectsStateChanged?.Invoke(active);
    }

    private readonly record struct BlurState(Rect Sample, Rect Output, float Radius, float Strength,
        float Ring, bool Active);

    private sealed class BlurHandler : CompositionCustomVisualHandler
    {
        private BlurState _state;

        public override void OnMessage(object message)
        {
            if (message is not BlurState state) return;
            _state = state;
            Invalidate();
        }

        public override Rect GetRenderBounds() => _state.Sample;

        public override void OnRender(ImmediateDrawingContext context)
        {
            if (!_state.Active || MemoryProfile.DisableVisualEffects) return;
            if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } feature) return;

            using var lease = feature.Lease();
            var canvas = lease.SkCanvas;
            var output = _state.Output;
            var rect = new SKRect((float)output.Left, (float)output.Top,
                (float)output.Right, (float)output.Bottom);
            using var blur = SKImageFilter.CreateBlur(_state.Radius, _state.Radius, SKShaderTileMode.Clamp);

            // 环形遮罩：四边各一条由内向外衰减的线性渐变，合并成围绕目标的软边光晕。
            // 目标内部（被不透明卡片覆盖）保持全强度，向外在 Ring 像素内衰减到 0。
            var alpha = (byte)(255 * Math.Clamp(_state.Strength, 0f, 1f));
            var solid = new SKColor(0, 0, 0, alpha);
            var ring = Math.Max(1f, _state.Ring);
            var innerLeft = rect.Left + ring;
            var innerTop = rect.Top + ring;
            var innerRight = Math.Max(innerLeft, rect.Right - ring);
            var innerBottom = Math.Max(innerTop, rect.Bottom - ring);

            using var topShader = SKShader.CreateLinearGradient(
                new SKPoint(0, rect.Top), new SKPoint(0, innerTop),
                [SKColors.Transparent, solid], [0f, 1f], SKShaderTileMode.Clamp);
            using var bottomShader = SKShader.CreateLinearGradient(
                new SKPoint(0, innerBottom), new SKPoint(0, rect.Bottom),
                [solid, SKColors.Transparent], [0f, 1f], SKShaderTileMode.Clamp);
            using var leftShader = SKShader.CreateLinearGradient(
                new SKPoint(rect.Left, 0), new SKPoint(innerLeft, 0),
                [SKColors.Transparent, solid], [0f, 1f], SKShaderTileMode.Clamp);
            using var rightShader = SKShader.CreateLinearGradient(
                new SKPoint(innerRight, 0), new SKPoint(rect.Right, 0),
                [solid, SKColors.Transparent], [0f, 1f], SKShaderTileMode.Clamp);

            using var topFilter = SKImageFilter.CreateShader(topShader);
            using var bottomFilter = SKImageFilter.CreateShader(bottomShader);
            using var leftFilter = SKImageFilter.CreateShader(leftShader);
            using var rightFilter = SKImageFilter.CreateShader(rightShader);
            using var mask = SKImageFilter.CreateMerge(
                [topFilter, bottomFilter, leftFilter, rightFilter]);
            using var blurred = SKImageFilter.CreateBlendMode(SKBlendMode.DstIn, blur, mask);
            using var original = SKImageFilter.CreateBlendMode(SKBlendMode.SrcOut, mask, null);
            using var transition = SKImageFilter.CreateArithmetic(0, 1, 1, 0, true, original, blurred);
            using var replace = new SKPaint { BlendMode = SKBlendMode.Src };

            var saveCount = canvas.Save();
            try
            {
                canvas.ClipRect(rect);
                // 在时间线的透明绘制层内，将清晰内容渐变替换为模糊内容；
                // 保留真实 alpha，不添加底色，也不把模糊文字叠到清晰文字上。
                canvas.SaveLayer(new SKCanvasSaveLayerRec
                    { Bounds = rect, Backdrop = transition, Paint = replace });
            }
            finally
            {
                canvas.RestoreToCount(saveCount);
            }
        }
    }
}
