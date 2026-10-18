using System;
using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;

namespace Fufucode.App.Features.Skins.LiquidGlass
{
    internal sealed class LiquidGlassShadowDrawOperation : ICustomDrawOperation
    {
        private readonly Rect _controlBounds;
        private readonly Rect _operationBounds;
        private readonly LiquidGlassDrawParameters _parameters;

        public LiquidGlassShadowDrawOperation(Rect controlBounds, LiquidGlassDrawParameters parameters)
        {
            _controlBounds = controlBounds;
            _parameters = parameters;

            double radius = Math.Max(0.0, parameters.ShadowRadius);
            Vector offset = parameters.ShadowOffset;
            double pad = radius * 2.0;

            double leftPad = pad + Math.Max(0.0, -offset.X);
            double topPad = pad + Math.Max(0.0, -offset.Y);
            double rightPad = pad + Math.Max(0.0, offset.X);
            double bottomPad = pad + Math.Max(0.0, offset.Y);

            _operationBounds = new Rect(
                -leftPad,
                -topPad,
                controlBounds.Width + leftPad + rightPad,
                controlBounds.Height + topPad + bottomPad);
        }

        public void Dispose()
        {
        }

        public bool HitTest(Point p)
        {
            return false;
        }

        public Rect Bounds
        {
            get => _operationBounds;
        }

        public bool Equals(ICustomDrawOperation? other)
        {
            return false;
        }

        public void Render(ImmediateDrawingContext context)
        {
            ISkiaSharpApiLeaseFeature? leaseFeature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (leaseFeature is null)
                return;

            using ISkiaSharpApiLease lease = leaseFeature.Lease();
            SKCanvas canvas = lease.SkCanvas;

            SKSize size = new((float)_controlBounds.Width, (float)_controlBounds.Height);
            if (size.Width <= 0 || size.Height <= 0)
                return;

            float radius = (float)Clamp(_parameters.ShadowRadius, 0.0, 512.0);
            if (radius <= 0.001f)
                return;

            float opacity = (float)Clamp(_parameters.ShadowOpacity, 0.0, 1.0);
            Color color = _parameters.ShadowColor;
            byte alpha = (byte)Clamp(color.A * opacity, 0.0, 255.0);
            if (alpha <= 0)
                return;

            float offsetX = (float)_parameters.ShadowOffset.X;
            float offsetY = (float)_parameters.ShadowOffset.Y;

            float maxRadius = Math.Min(size.Width, size.Height) * 0.5f;
            float[] cornerRadii = LiquidGlassPathUtils.GetCornerRadii(_parameters.CornerRadius, maxRadius);
            SKRect rect = SKRect.Create(0, 0, size.Width, size.Height);

            using SKPath path = LiquidGlassPathUtils.CreateRoundRectPath(rect, cornerRadii);
            using SKMaskFilter? blur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, radius);

            using SKPaint shadowPaint = new()
            {
                Color = new SKColor(color.R, color.G, color.B, alpha),
                MaskFilter = blur,
                IsAntialias = true
            };

            using SKPaint clearPaint = new()
            {
                BlendMode = SKBlendMode.Clear,
                IsAntialias = true
            };

            float pad = radius * 2.0f;
            SKRect layerBounds = SKRect.Create(-pad, -pad, size.Width + pad * 2.0f, size.Height + pad * 2.0f);

            canvas.SaveLayer(layerBounds, null);

            canvas.Save();
            canvas.Translate(offsetX, offsetY);
            canvas.DrawPath(path, shadowPaint);
            canvas.Restore();

            canvas.DrawPath(path, clearPaint);

            canvas.Restore();
        }

        private static double Clamp(double value, double min, double max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }
    }

}
