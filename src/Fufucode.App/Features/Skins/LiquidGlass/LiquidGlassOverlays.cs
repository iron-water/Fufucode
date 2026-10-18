using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Fufucode.App.Features.Skins.LiquidGlass
{
    /// <summary>前置高光层：边缘高光 draw op 挂这里，确保压在内容之上渲染。</summary>
    internal sealed class LiquidGlassFrontOverlay : Control
    {
        public override void Render(DrawingContext context)
        {
            if (LiquidGlassBackdropProvider.IsCapturing)
                return;

            if (TemplatedParent is not LiquidGlassSurface surface)
                return;

            if (!surface.HighlightEnabled)
                return;

            Rect bounds = new(0, 0, Bounds.Width, Bounds.Height);
            LiquidGlassDrawParameters parameters = surface.CreateDrawParameters();

            context.Custom(new LiquidGlassDrawOperation(bounds, parameters, null, LiquidGlassDrawPass.Highlight));
        }
    }

    /// <summary>按压高光层：InteractiveSurface 的指尖径向发光，压在 FrontOverlay 之上。</summary>
    internal sealed class LiquidGlassInteractiveOverlay : Control
    {
        public override void Render(DrawingContext context)
        {
            if (LiquidGlassBackdropProvider.IsCapturing)
                return;

            if (TemplatedParent is not LiquidGlassInteractiveSurface surface)
                return;

            if (!surface.InteractiveHighlightEnabled)
                return;

            double progress = surface.GetInteractiveHighlightProgress();
            if (progress <= 0.001)
                return;

            Rect bounds = new(0, 0, Bounds.Width, Bounds.Height);
            LiquidGlassDrawParameters parameters = surface.CreateDrawParameters();
            parameters.InteractiveProgress = progress;
            parameters.InteractivePosition = surface.GetInteractiveHighlightPosition();

            context.Custom(new LiquidGlassDrawOperation(bounds, parameters, null, LiquidGlassDrawPass.InteractiveHighlight));
        }
    }
}
