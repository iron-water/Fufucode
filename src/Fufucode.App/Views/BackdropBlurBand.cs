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

    /// <summary>目标卡片的圆角半径；遮罩沿这个圆角描一圈，而不是矩形。</summary>
    public static readonly StyledProperty<double> TargetCornerRadiusProperty =
        AvaloniaProperty.Register<BackdropBlurBand, double>(nameof(TargetCornerRadius), 12d,
            validate: value => double.IsFinite(value) && value >= 0);

    public double TargetCornerRadius
    {
        get => GetValue(TargetCornerRadiusProperty);
        set => SetValue(TargetCornerRadiusProperty, value);
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
        _visual?.SendHandlerMessage(new BlurState(default, default, 0, 0, 16, 12, 0, 0, false));
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
            change.Property == RingThicknessProperty || change.Property == TargetCornerRadiusProperty)
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
        var ring = (float)RingThickness;
        _visual.SendHandlerMessage(new BlurState(sample, output, (float)BlurRadius,
            (float)Strength, ring, (float)TargetCornerRadius,
            (float)Math.Max(0, output.Width - ring * 2), (float)Math.Max(0, output.Height - ring * 2),
            active));
        SetActive(active);
    }

    private void SetActive(bool active)
    {
        if (_active == active) return;
        _active = active;
        VisualEffectsStateChanged?.Invoke(active);
    }

    private readonly record struct BlurState(Rect Sample, Rect Output, float Radius, float Strength,
        float Ring, float Corner, float CardWidth, float CardHeight, bool Active);

    private sealed class BlurHandler : CompositionCustomVisualHandler
    {
        /// <summary>圆角矩形距离场：贴边全强度，向外 Ring 像素按平方衰减。</summary>
        private const string MaskSksl = """
            uniform float2 uSize;
            uniform float2 uCard;
            uniform float uRadius;
            uniform float uRing;
            uniform half uAlpha;

            half4 main(float2 p) {
                float2 c = uSize * 0.5;
                float2 h = max(uCard * 0.5, float2(0.0));
                float r = min(uRadius, min(h.x, h.y));
                float2 q = abs(p - c) - (h - float2(r, r));
                float d = length(max(q, float2(0.0))) + min(max(q.x, q.y), 0.0) - r;
                float t = clamp(d / max(uRing, 0.001), 0.0, 1.0);
                float a = (1.0 - t) * (1.0 - t) * uAlpha;
                // 预乘白色：遮罩只当 alpha 用，不能是黑色，否则会把黑叠到背景上。
                return half4(a, a, a, a);
            }
            """;

        private BlurState _state;
        private SKRuntimeEffect? _maskEffect;
        private SKShader? _maskShader;
        private (float W, float H, float R, float Ring, float Alpha) _maskKey;

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

            // 沿卡片圆角描一圈的距离场遮罩。
            // 之前用四条矩形渐变，交叉处会叠出十字、圆角处切成直角——必须用 SDF。
            var ring = Math.Max(1f, _state.Ring);
            var alpha = Math.Clamp(_state.Strength, 0f, 1f);
            var key = (rect.Width, rect.Height, _state.Corner, ring, alpha);
            if (_maskShader is null || _maskKey != key)
            {
                _maskEffect ??= SKRuntimeEffect.CreateShader(MaskSksl, out _);
                if (_maskEffect is null) return;
                using var uniforms = new SKRuntimeEffectUniforms(_maskEffect);
                uniforms["uSize"] = new[] { rect.Width, rect.Height };
                uniforms["uCard"] = new[] { _state.CardWidth, _state.CardHeight };
                uniforms["uRadius"] = _state.Corner;
                uniforms["uRing"] = ring;
                uniforms["uAlpha"] = alpha;
                _maskShader?.Dispose();
                _maskShader = _maskEffect.ToShader(uniforms);
                _maskKey = key;
            }
            if (_maskShader is null) return;
            using var mask = SKImageFilter.CreateShader(_maskShader);
            // 只把「按遮罩淡出的模糊」这一层，SrcOver 叠回原背景：
            // 遮罩为 0 处完全透明 → 背景原样保留，不会出现灰带或黑块。
            using var blurred = SKImageFilter.CreateBlendMode(SKBlendMode.DstIn, blur, mask);
            using var transition = SKImageFilter.CreateBlendMode(SKBlendMode.SrcOver, blurred, null);
            using var replace = new SKPaint { BlendMode = SKBlendMode.SrcOver };

            var saveCount = canvas.Save();
            try
            {
                // 外边界用圆角矩形裁剪硬性保证贴合卡片圆角；
                // 只靠距离场遮罩时，边缘仍会按控件矩形收口。
                var outerRadius = (float)(_state.Corner + ring);
                using (var outer = new SKRoundRect(rect, outerRadius))
                {
                    canvas.ClipRoundRect(outer, SKClipOperation.Intersect, antialias: true);
                }
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
