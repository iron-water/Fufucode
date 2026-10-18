using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Controls.Templates;
using Avalonia.Media;

namespace Fufucode.App.Features.Skins.LiquidGlass
{
    /// <summary>
    /// 液态玻璃表面：在背后绘制 vibrancy→blur→圆角透镜折射→色散→边缘高光管线。
    /// 由 LiquidGlassAvaloniaUI（MIT）裁剪 vendor——去掉交互形变、渐进模糊、自适应亮度、内外阴影扩展项，
    /// 保留 BackdropZoom/Offset、Refraction*、ChromaticAberration、BlurRadius、Vibrancy、TintColor、
    /// SurfaceColor、Highlight*、Shadow* 与 CornerRadius。
    /// 无 Skia 租约或无快照时 DrawFallback 画 SurfaceColor 实色 + 细描边，headless 出图确定。
    /// </summary>
    public class LiquidGlassSurface : ContentControl
    {
        public static readonly StyledProperty<double> BackdropZoomProperty =
            AvaloniaProperty.Register<LiquidGlassSurface, double>(nameof(BackdropZoom), 1.0);

        public static readonly StyledProperty<Vector> BackdropOffsetProperty =
            AvaloniaProperty.Register<LiquidGlassSurface, Vector>(nameof(BackdropOffset), new Vector(0.0, 0.0));

        public static readonly StyledProperty<double> RefractionHeightProperty =
            AvaloniaProperty.Register<LiquidGlassSurface, double>(nameof(RefractionHeight), 12.0);

        public static readonly StyledProperty<double> RefractionAmountProperty =
            AvaloniaProperty.Register<LiquidGlassSurface, double>(nameof(RefractionAmount), 24.0);

        public static readonly StyledProperty<bool> DepthEffectProperty =
            AvaloniaProperty.Register<LiquidGlassSurface, bool>(nameof(DepthEffect), false);

        public static readonly StyledProperty<bool> ChromaticAberrationProperty =
            AvaloniaProperty.Register<LiquidGlassSurface, bool>(nameof(ChromaticAberration), false);

        public static readonly StyledProperty<double> BlurRadiusProperty =
            AvaloniaProperty.Register<LiquidGlassSurface, double>(nameof(BlurRadius), 2.0);

        public static readonly StyledProperty<double> VibrancyProperty =
            AvaloniaProperty.Register<LiquidGlassSurface, double>(nameof(Vibrancy), 1.5);

        public static readonly StyledProperty<double> BrightnessProperty =
            AvaloniaProperty.Register<LiquidGlassSurface, double>(nameof(Brightness), 0.0);

        public static readonly StyledProperty<double> ContrastProperty =
            AvaloniaProperty.Register<LiquidGlassSurface, double>(nameof(Contrast), 1.0);

        public static readonly StyledProperty<double> ExposureEvProperty =
            AvaloniaProperty.Register<LiquidGlassSurface, double>(nameof(ExposureEv), 0.0);

        public static readonly StyledProperty<double> GammaPowerProperty =
            AvaloniaProperty.Register<LiquidGlassSurface, double>(nameof(GammaPower), 1.0);

        public static readonly StyledProperty<double> BackdropOpacityProperty =
            AvaloniaProperty.Register<LiquidGlassSurface, double>(nameof(BackdropOpacity), 1.0);

        public static readonly StyledProperty<Color> TintColorProperty =
            AvaloniaProperty.Register<LiquidGlassSurface, Color>(nameof(TintColor), Colors.Transparent);

        public static readonly StyledProperty<Color> SurfaceColorProperty =
            AvaloniaProperty.Register<LiquidGlassSurface, Color>(nameof(SurfaceColor), Colors.Transparent);

        public static readonly StyledProperty<bool> HighlightEnabledProperty =
            AvaloniaProperty.Register<LiquidGlassSurface, bool>(nameof(HighlightEnabled), true);

        public static readonly StyledProperty<double> HighlightWidthProperty =
            AvaloniaProperty.Register<LiquidGlassSurface, double>(nameof(HighlightWidth), 0.5);

        public static readonly StyledProperty<double> HighlightBlurRadiusProperty =
            AvaloniaProperty.Register<LiquidGlassSurface, double>(nameof(HighlightBlurRadius), 0.25);

        public static readonly StyledProperty<double> HighlightOpacityProperty =
            AvaloniaProperty.Register<LiquidGlassSurface, double>(nameof(HighlightOpacity), 0.5);

        public static readonly StyledProperty<double> HighlightAngleProperty =
            AvaloniaProperty.Register<LiquidGlassSurface, double>(nameof(HighlightAngle), 45.0);

        public static readonly StyledProperty<double> HighlightFalloffProperty =
            AvaloniaProperty.Register<LiquidGlassSurface, double>(nameof(HighlightFalloff), 1.0);

        public static readonly StyledProperty<bool> ShadowEnabledProperty =
            AvaloniaProperty.Register<LiquidGlassSurface, bool>(nameof(ShadowEnabled), true);

        public static readonly StyledProperty<double> ShadowRadiusProperty =
            AvaloniaProperty.Register<LiquidGlassSurface, double>(nameof(ShadowRadius), 24.0);

        public static readonly StyledProperty<Vector> ShadowOffsetProperty =
            AvaloniaProperty.Register<LiquidGlassSurface, Vector>(nameof(ShadowOffset), new Vector(0.0, 4.0));

        public static readonly StyledProperty<Color> ShadowColorProperty =
            AvaloniaProperty.Register<LiquidGlassSurface, Color>(nameof(ShadowColor), Color.FromArgb(26, 0, 0, 0));

        public static readonly StyledProperty<double> ShadowOpacityProperty =
            AvaloniaProperty.Register<LiquidGlassSurface, double>(nameof(ShadowOpacity), 1.0);

        // Progressive blur：顶部向内容渐隐（iOS scroll edge soft-fade 语义）。
        public static readonly StyledProperty<bool> ProgressiveBlurEnabledProperty =
            AvaloniaProperty.Register<LiquidGlassSurface, bool>(nameof(ProgressiveBlurEnabled), false);

        public static readonly StyledProperty<double> ProgressiveBlurStartProperty =
            AvaloniaProperty.Register<LiquidGlassSurface, double>(nameof(ProgressiveBlurStart), 0.0);

        public static readonly StyledProperty<double> ProgressiveBlurEndProperty =
            AvaloniaProperty.Register<LiquidGlassSurface, double>(nameof(ProgressiveBlurEnd), 0.4);

        public static readonly StyledProperty<Color> ProgressiveTintColorProperty =
            AvaloniaProperty.Register<LiquidGlassSurface, Color>(nameof(ProgressiveTintColor), Colors.Transparent);

        public static readonly StyledProperty<double> ProgressiveTintIntensityProperty =
            AvaloniaProperty.Register<LiquidGlassSurface, double>(nameof(ProgressiveTintIntensity), 0.0);

        // Adaptive luminance：玻璃底下内容亮度采样值（Adapter 周期性写入；0..1）。
        public static readonly StyledProperty<double> AdaptiveLuminanceProperty =
            AvaloniaProperty.Register<LiquidGlassSurface, double>(nameof(AdaptiveLuminance), 0.5);

        static LiquidGlassSurface()
        {
            // TemplatedControl defaults ClipToBounds=true, which would clip the shadow pass to the control bounds.
            // We clip the content via the template's Border and clip shader passes explicitly.
            ClipToBoundsProperty.OverrideDefaultValue<LiquidGlassSurface>(false);

            AffectsRender<LiquidGlassSurface>(
                CornerRadiusProperty,
                BackdropZoomProperty,
                BackdropOffsetProperty,
                RefractionHeightProperty,
                RefractionAmountProperty,
                DepthEffectProperty,
                ChromaticAberrationProperty,
                BlurRadiusProperty,
                VibrancyProperty,
                BrightnessProperty,
                ContrastProperty,
                ExposureEvProperty,
                GammaPowerProperty,
                BackdropOpacityProperty,
                TintColorProperty,
                SurfaceColorProperty,
                ProgressiveBlurEnabledProperty,
                ProgressiveBlurStartProperty,
                ProgressiveBlurEndProperty,
                ProgressiveTintColorProperty,
                ProgressiveTintIntensityProperty,
                AdaptiveLuminanceProperty,
                ShadowEnabledProperty,
                ShadowRadiusProperty,
                ShadowOffsetProperty,
                ShadowColorProperty,
                ShadowOpacityProperty);

            TemplateProperty.OverrideDefaultValue<LiquidGlassSurface>(CreateDefaultTemplate());
        }

        internal LiquidGlassFrontOverlay? FrontOverlay { get; private set; }

        // InteractiveSurface 的按压高光层；基类模板也有此占位，InteractiveSurface 经 NameScope 找到。
        internal LiquidGlassInteractiveOverlay? InteractiveOverlay { get; private set; }

        public Control? Child
        {
            get => Content as Control;
            set => Content = value;
        }

        public double BackdropZoom
        {
            get => GetValue(BackdropZoomProperty);
            set => SetValue(BackdropZoomProperty, value);
        }

        public Vector BackdropOffset
        {
            get => GetValue(BackdropOffsetProperty);
            set => SetValue(BackdropOffsetProperty, value);
        }

        public double RefractionHeight
        {
            get => GetValue(RefractionHeightProperty);
            set => SetValue(RefractionHeightProperty, value);
        }

        public double RefractionAmount
        {
            get => GetValue(RefractionAmountProperty);
            set => SetValue(RefractionAmountProperty, value);
        }

        public bool DepthEffect
        {
            get => GetValue(DepthEffectProperty);
            set => SetValue(DepthEffectProperty, value);
        }

        public bool ChromaticAberration
        {
            get => GetValue(ChromaticAberrationProperty);
            set => SetValue(ChromaticAberrationProperty, value);
        }

        public double BlurRadius
        {
            get => GetValue(BlurRadiusProperty);
            set => SetValue(BlurRadiusProperty, value);
        }

        public double Vibrancy
        {
            get => GetValue(VibrancyProperty);
            set => SetValue(VibrancyProperty, value);
        }

        public double Brightness
        {
            get => GetValue(BrightnessProperty);
            set => SetValue(BrightnessProperty, value);
        }

        public double Contrast
        {
            get => GetValue(ContrastProperty);
            set => SetValue(ContrastProperty, value);
        }

        public double ExposureEv
        {
            get => GetValue(ExposureEvProperty);
            set => SetValue(ExposureEvProperty, value);
        }

        public double GammaPower
        {
            get => GetValue(GammaPowerProperty);
            set => SetValue(GammaPowerProperty, value);
        }

        public double BackdropOpacity
        {
            get => GetValue(BackdropOpacityProperty);
            set => SetValue(BackdropOpacityProperty, value);
        }

        public Color TintColor
        {
            get => GetValue(TintColorProperty);
            set => SetValue(TintColorProperty, value);
        }

        public Color SurfaceColor
        {
            get => GetValue(SurfaceColorProperty);
            set => SetValue(SurfaceColorProperty, value);
        }

        public bool HighlightEnabled
        {
            get => GetValue(HighlightEnabledProperty);
            set => SetValue(HighlightEnabledProperty, value);
        }

        public double HighlightWidth
        {
            get => GetValue(HighlightWidthProperty);
            set => SetValue(HighlightWidthProperty, value);
        }

        public double HighlightBlurRadius
        {
            get => GetValue(HighlightBlurRadiusProperty);
            set => SetValue(HighlightBlurRadiusProperty, value);
        }

        public double HighlightOpacity
        {
            get => GetValue(HighlightOpacityProperty);
            set => SetValue(HighlightOpacityProperty, value);
        }

        public double HighlightAngle
        {
            get => GetValue(HighlightAngleProperty);
            set => SetValue(HighlightAngleProperty, value);
        }

        public double HighlightFalloff
        {
            get => GetValue(HighlightFalloffProperty);
            set => SetValue(HighlightFalloffProperty, value);
        }

        public bool ShadowEnabled
        {
            get => GetValue(ShadowEnabledProperty);
            set => SetValue(ShadowEnabledProperty, value);
        }

        public double ShadowRadius
        {
            get => GetValue(ShadowRadiusProperty);
            set => SetValue(ShadowRadiusProperty, value);
        }

        public Vector ShadowOffset
        {
            get => GetValue(ShadowOffsetProperty);
            set => SetValue(ShadowOffsetProperty, value);
        }

        public Color ShadowColor
        {
            get => GetValue(ShadowColorProperty);
            set => SetValue(ShadowColorProperty, value);
        }

        public double ShadowOpacity
        {
            get => GetValue(ShadowOpacityProperty);
            set => SetValue(ShadowOpacityProperty, value);
        }

        public bool ProgressiveBlurEnabled
        {
            get => GetValue(ProgressiveBlurEnabledProperty);
            set => SetValue(ProgressiveBlurEnabledProperty, value);
        }

        public double ProgressiveBlurStart
        {
            get => GetValue(ProgressiveBlurStartProperty);
            set => SetValue(ProgressiveBlurStartProperty, value);
        }

        public double ProgressiveBlurEnd
        {
            get => GetValue(ProgressiveBlurEndProperty);
            set => SetValue(ProgressiveBlurEndProperty, value);
        }

        public Color ProgressiveTintColor
        {
            get => GetValue(ProgressiveTintColorProperty);
            set => SetValue(ProgressiveTintColorProperty, value);
        }

        public double ProgressiveTintIntensity
        {
            get => GetValue(ProgressiveTintIntensityProperty);
            set => SetValue(ProgressiveTintIntensityProperty, value);
        }

        public double AdaptiveLuminance
        {
            get => GetValue(AdaptiveLuminanceProperty);
            set => SetValue(AdaptiveLuminanceProperty, value);
        }

        public override void Render(DrawingContext context)
        {
            if (LiquidGlassBackdropProvider.IsCapturing)
                return;

            if (Bounds.Width <= 0 || Bounds.Height <= 0)
                return;

            LiquidGlassDrawParameters parameters = CreateDrawParameters();
            Rect controlBounds = new(0, 0, Bounds.Width, Bounds.Height);

            if (ShadowEnabled && ShadowOpacity > 0.001 && ShadowColor.A > 0 && ShadowRadius > 0.001)
                context.Custom(new LiquidGlassShadowDrawOperation(controlBounds, parameters));

            LiquidGlassBackdropProvider.EnsureSnapshot(this);
            LiquidGlassBackdropSnapshot? snapshot = LiquidGlassBackdropProvider.TryGetSnapshot(this);

            context.Custom(new LiquidGlassDrawOperation(controlBounds, parameters, snapshot, LiquidGlassDrawPass.Lens));
        }

        internal LiquidGlassDrawParameters CreateDrawParameters()
        {
            return new LiquidGlassDrawParameters
            {
                CornerRadius = CornerRadius,
                BackdropZoom = BackdropZoom,
                BackdropOffset = BackdropOffset,
                RefractionHeight = RefractionHeight,
                RefractionAmount = RefractionAmount,
                DepthEffect = DepthEffect,
                ChromaticAberration = ChromaticAberration,
                BlurRadius = BlurRadius,
                Vibrancy = Vibrancy,
                Brightness = Brightness,
                Contrast = Contrast,
                ExposureEv = ExposureEv,
                GammaPower = GammaPower,
                BackdropOpacity = BackdropOpacity,
                TintColor = TintColor,
                SurfaceColor = SurfaceColor,
                HighlightEnabled = HighlightEnabled,
                HighlightWidth = HighlightWidth,
                HighlightBlurRadius = HighlightBlurRadius,
                HighlightOpacity = HighlightOpacity,
                HighlightAngleDegrees = HighlightAngle,
                HighlightFalloff = HighlightFalloff,
                ProgressiveBlurEnabled = ProgressiveBlurEnabled,
                ProgressiveBlurStart = ProgressiveBlurStart,
                ProgressiveBlurEnd = ProgressiveBlurEnd,
                ProgressiveTintColor = ProgressiveTintColor,
                ProgressiveTintIntensity = ProgressiveTintIntensity,
                AdaptiveLuminance = AdaptiveLuminance,
                ShadowEnabled = ShadowEnabled,
                ShadowRadius = ShadowRadius,
                ShadowOffset = ShadowOffset,
                ShadowColor = ShadowColor,
                ShadowOpacity = ShadowOpacity
            };
        }

        protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
        {
            base.OnApplyTemplate(e);
            FrontOverlay = e.NameScope.Find<LiquidGlassFrontOverlay>("PART_FrontOverlay");
            InteractiveOverlay = e.NameScope.Find<LiquidGlassInteractiveOverlay>("PART_InteractiveOverlay");
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            if (IsSubscriberOnlyRenderProperty(change.Property))
                LiquidGlassBackdropProvider.NotifySubscriberOnlyInvalidation(this);

            if (change.Property == CornerRadiusProperty
                || change.Property == HighlightEnabledProperty
                || change.Property == HighlightWidthProperty
                || change.Property == HighlightBlurRadiusProperty
                || change.Property == HighlightOpacityProperty
                || change.Property == HighlightAngleProperty
                || change.Property == HighlightFalloffProperty)
            {
                FrontOverlay?.InvalidateVisual();
            }
        }

        private static bool IsSubscriberOnlyRenderProperty(AvaloniaProperty property) =>
            property == TintColorProperty
            || property == SurfaceColorProperty
            || property == HighlightEnabledProperty
            || property == HighlightWidthProperty
            || property == HighlightBlurRadiusProperty
            || property == HighlightOpacityProperty
            || property == HighlightAngleProperty
            || property == HighlightFalloffProperty
            || property == ShadowEnabledProperty
            || property == ShadowRadiusProperty
            || property == ShadowOffsetProperty
            || property == ShadowColorProperty
            || property == ShadowOpacityProperty;

        private static FuncControlTemplate CreateDefaultTemplate()
        {
            return new FuncControlTemplate<LiquidGlassSurface>((_, ns) =>
            {
                ContentPresenter presenter = new ContentPresenter
                {
                    Name = "PART_ContentPresenter",
                    [~ContentPresenter.ContentProperty] = new TemplateBinding(ContentProperty),
                    [~ContentPresenter.ContentTemplateProperty] = new TemplateBinding(ContentTemplateProperty),
                    [~ContentPresenter.VerticalContentAlignmentProperty] = new TemplateBinding(VerticalContentAlignmentProperty),
                    [~ContentPresenter.HorizontalContentAlignmentProperty] = new TemplateBinding(HorizontalContentAlignmentProperty)
                }.RegisterInNameScope(ns);

                LiquidGlassFrontOverlay frontOverlay = new LiquidGlassFrontOverlay
                {
                    Name = "PART_FrontOverlay",
                    IsHitTestVisible = false
                }.RegisterInNameScope(ns);

                LiquidGlassInteractiveOverlay interactiveOverlay = new LiquidGlassInteractiveOverlay
                {
                    Name = "PART_InteractiveOverlay",
                    IsHitTestVisible = false
                }.RegisterInNameScope(ns);

                Grid grid = new();
                grid.Children.Add(presenter);
                grid.Children.Add(frontOverlay);
                grid.Children.Add(interactiveOverlay);

                return new Border
                {
                    ClipToBounds = true,
                    [~Border.CornerRadiusProperty] = new TemplateBinding(CornerRadiusProperty),
                    Child = grid
                };
            });
        }
    }
}
