using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace Fufucode.App.Features.Skins.LiquidGlass
{
    /// <summary>
    /// 应用内"壁纸"：iOS/macOS 的 Liquid Glass 观感来自玻璃底下有带色差的内容可模糊、
    /// 可折射。平滑渐变被模糊后还是同一条渐变，玻璃完全不可见；这里画几团大尺度柔光
    /// 色斑，给 snapshot 提供低频但有明暗/色相起伏的素材。只在 LiquidGlass 租约期挂在
    /// ChromeRoot 最底层，不参与命中测试。
    /// </summary>
    internal sealed class LiquidGlassWallpaper : Control
    {
        private readonly record struct Blob(double X, double Y, double Radius, Color Color);

        // 位置/半径为窗口宽高的比例；颜色取 iOS 系统色系，亮/暗各一套。
        private static readonly Blob[] DarkBlobs =
        [
            new(0.08, 0.10, 0.55, Color.FromArgb(255, 0x2F, 0x6B, 0xFF)),
            new(0.92, 0.18, 0.45, Color.FromArgb(255, 0xBF, 0x3A, 0xF2)),
            new(0.55, 0.62, 0.50, Color.FromArgb(255, 0x00, 0xA8, 0xC8)),
            new(0.18, 0.95, 0.42, Color.FromArgb(255, 0xFF, 0x5E, 0x7A)),
            new(0.85, 0.92, 0.40, Color.FromArgb(255, 0xFF, 0x9F, 0x0A)),
        ];

        private static readonly Blob[] LightBlobs =
        [
            new(0.08, 0.10, 0.55, Color.FromArgb(255, 0x7F, 0xB2, 0xFF)),
            new(0.92, 0.18, 0.45, Color.FromArgb(255, 0xD9, 0x9C, 0xFF)),
            new(0.55, 0.62, 0.50, Color.FromArgb(255, 0x7F, 0xE0, 0xE6)),
            new(0.18, 0.95, 0.42, Color.FromArgb(255, 0xFF, 0xA8, 0xB8)),
            new(0.85, 0.92, 0.40, Color.FromArgb(255, 0xFF, 0xCC, 0x80)),
        ];

        public LiquidGlassWallpaper()
        {
            IsHitTestVisible = false;
            Focusable = false;
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == BoundsProperty || change.Property == ThemeVariantScope.ActualThemeVariantProperty)
                InvalidateVisual();
        }

        public override void Render(DrawingContext context)
        {
            var w = Bounds.Width;
            var h = Bounds.Height;
            if (w <= 0 || h <= 0) return;

            var dark = ActualThemeVariant == ThemeVariant.Dark;
            var baseColor = dark ? Color.FromArgb(255, 0x0B, 0x0D, 0x16) : Color.FromArgb(255, 0xEE, 0xF0, 0xF6);
            context.FillRectangle(new SolidColorBrush(baseColor), new Rect(0, 0, w, h));

            var scale = Math.Max(w, h);
            foreach (var blob in dark ? DarkBlobs : LightBlobs)
            {
                var r = blob.Radius * scale;
                var rect = new Rect(blob.X * w - r, blob.Y * h - r, r * 2, r * 2);
                var core = blob.Color;
                var brush = new RadialGradientBrush
                {
                    GradientStops =
                    {
                        new GradientStop(Color.FromArgb((byte)(dark ? 200 : 210), core.R, core.G, core.B), 0.0),
                        new GradientStop(Color.FromArgb((byte)(dark ? 90 : 110), core.R, core.G, core.B), 0.45),
                        new GradientStop(Color.FromArgb(0, core.R, core.G, core.B), 1.0),
                    },
                };
                context.DrawEllipse(brush, null, rect);
            }
        }
    }
}
