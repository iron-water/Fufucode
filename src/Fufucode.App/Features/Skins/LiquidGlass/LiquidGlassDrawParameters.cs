using Avalonia;
using Avalonia.Media;

namespace Fufucode.App.Features.Skins.LiquidGlass
{
    internal enum LiquidGlassDrawPass
    {
        Lens,
        Highlight,
        InteractiveHighlight
    }

    internal struct LiquidGlassDrawParameters
    {
        public CornerRadius CornerRadius { get; set; }

        public double BackdropZoom { get; set; }
        public Vector BackdropOffset { get; set; }

        public double RefractionHeight { get; set; }
        public double RefractionAmount { get; set; }
        public bool DepthEffect { get; set; }
        public bool ChromaticAberration { get; set; }

        public double BlurRadius { get; set; }
        public double Vibrancy { get; set; }
        public double Brightness { get; set; }
        public double Contrast { get; set; }
        public double ExposureEv { get; set; }
        public double GammaPower { get; set; }
        public double BackdropOpacity { get; set; }

        public Color TintColor { get; set; }
        public Color SurfaceColor { get; set; }

        // Progressive blur：top-edge soft-fade（iOS scroll edge effect 语义）——在 lens pass 末尾
        // 叠加蒙版让玻璃顶部向内容渐隐，而不是硬切边。start/end 是高度归一化 0..1。
        public bool ProgressiveBlurEnabled { get; set; }
        public double ProgressiveBlurStart { get; set; }
        public double ProgressiveBlurEnd { get; set; }
        public Color ProgressiveTintColor { get; set; }
        public double ProgressiveTintIntensity { get; set; }

        // Interactive highlight：按压时指尖径向发光。progress 0..1 由 InteractiveSurface 弹簧动画驱动。
        public double InteractiveProgress { get; set; }
        public Point InteractivePosition { get; set; }

        // Adaptive luminance：玻璃底下内容平均亮度（0..1，>0.5 偏亮）——由 Adapter 周期性采样
        // snapshot 得到，供玻璃上文字/图标选反色用。不驱动 shader，给上层样式决策。
        public double AdaptiveLuminance { get; set; }

        public bool HighlightEnabled { get; set; }
        public double HighlightWidth { get; set; }
        public double HighlightBlurRadius { get; set; }
        public double HighlightOpacity { get; set; }
        public double HighlightAngleDegrees { get; set; }
        public double HighlightFalloff { get; set; }

        // 动态状态机（规范 §7.2/§7.3）：Rest→Hover→Press 由弹簧驱动。
        //   HoverProgress  0..1：边缘光 +20%、折射 ×1.15、内部环境晕 0.08
        //   PressProgress  0..1：折射 ×1.35、模糊 ×0.90、中心光斑 0.20
        //   PointerLocal   指针在控件内的局部坐标（光源被指针吸引 lerp 0.35；最近边缘镜面斑）
        //   PointerPresence 0..1：指针在场强度（离开时弹簧淡出）
        public double HoverProgress { get; set; }
        public double PressProgress { get; set; }
        public Point PointerLocal { get; set; }
        public double PointerPresence { get; set; }

        public bool ShadowEnabled { get; set; }
        public double ShadowRadius { get; set; }
        public Vector ShadowOffset { get; set; }
        public Color ShadowColor { get; set; }
        public double ShadowOpacity { get; set; }
    }
}
