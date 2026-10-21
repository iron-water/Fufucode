using System;
using System.Collections.Generic;
using Avalonia;
using SkiaSharp;

namespace Fufucode.App.Features.Skins.LiquidGlass
{
    internal sealed class LiquidGlassBackdropSnapshot : IDisposable
    {
        internal readonly struct FilteredKey : IEquatable<FilteredKey>
        {
            public FilteredKey(
                int brightnessQ,
                int contrastQ,
                int saturationQ,
                int exposureEvQ,
                int opacityQ,
                int blurSigmaPxQ,
                int cropX,
                int cropY,
                int cropWidth,
                int cropHeight,
                int renderContextId)
            {
                BrightnessQ = brightnessQ;
                ContrastQ = contrastQ;
                SaturationQ = saturationQ;
                ExposureEvQ = exposureEvQ;
                OpacityQ = opacityQ;
                BlurSigmaPxQ = blurSigmaPxQ;
                CropX = cropX;
                CropY = cropY;
                CropWidth = cropWidth;
                CropHeight = cropHeight;
                RenderContextId = renderContextId;
            }

            public int BrightnessQ { get; }
            public int ContrastQ { get; }
            public int SaturationQ { get; }
            public int ExposureEvQ { get; }
            public int OpacityQ { get; }
            public int BlurSigmaPxQ { get; }
            public int CropX { get; }
            public int CropY { get; }
            public int CropWidth { get; }
            public int CropHeight { get; }
            public int RenderContextId { get; }

            public bool Equals(FilteredKey other)
            {
                return BrightnessQ == other.BrightnessQ
                       && ContrastQ == other.ContrastQ
                       && SaturationQ == other.SaturationQ
                       && ExposureEvQ == other.ExposureEvQ
                       && OpacityQ == other.OpacityQ
                       && BlurSigmaPxQ == other.BlurSigmaPxQ
                       && CropX == other.CropX
                       && CropY == other.CropY
                       && CropWidth == other.CropWidth
                       && CropHeight == other.CropHeight
                       && RenderContextId == other.RenderContextId;
            }

            public override bool Equals(object? obj)
            {
                return obj is FilteredKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hashCode = BrightnessQ;
                    hashCode = hashCode * 397 ^ ContrastQ;
                    hashCode = hashCode * 397 ^ SaturationQ;
                    hashCode = hashCode * 397 ^ ExposureEvQ;
                    hashCode = hashCode * 397 ^ OpacityQ;
                    hashCode = hashCode * 397 ^ BlurSigmaPxQ;
                    hashCode = hashCode * 397 ^ CropX;
                    hashCode = hashCode * 397 ^ CropY;
                    hashCode = hashCode * 397 ^ CropWidth;
                    hashCode = hashCode * 397 ^ CropHeight;
                    hashCode = hashCode * 397 ^ RenderContextId;
                    return hashCode;
                }
            }
        }

        internal readonly struct FilteredResult
        {
            public FilteredResult(SKImage image, PixelPoint originInPixels)
            {
                Image = image ?? throw new ArgumentNullException(nameof(image));
                OriginInPixels = originInPixels;
            }

            public SKImage Image { get; }

            public PixelPoint OriginInPixels { get; }
        }

        private int _leases;
        private int _disposeRequested;
        private int _disposed;
        private readonly object _filteredLock = new();
        private Dictionary<FilteredKey, FilteredResult>? _filtered;
        private Queue<FilteredKey>? _filteredOrder;
        private const int MaxFilteredEntries = 8;

        public LiquidGlassBackdropSnapshot(SKImage image, PixelPoint originInPixels, PixelSize pixelSize, double scaling)
        {
            Image = image ?? throw new ArgumentNullException(nameof(image));
            OriginInPixels = originInPixels;
            PixelSize = pixelSize;
            Scaling = scaling;
        }

        public SKImage Image { get; }

        /// <summary>
        /// Snapshot origin in device pixels (TopLevel coordinate space).
        /// </summary>
        public PixelPoint OriginInPixels { get; }

        public PixelSize PixelSize { get; }

        public double Scaling { get; }

        public bool TryGetFiltered(FilteredKey key, out FilteredResult result)
        {
            lock (_filteredLock)
            {
                if (_filtered is null)
                {
                    result = default;
                    return false;
                }

                return _filtered.TryGetValue(key, out result);
            }
        }

        public void StoreFiltered(FilteredKey key, FilteredResult result)
        {
            lock (_filteredLock)
            {
                _filtered ??= new Dictionary<FilteredKey, FilteredResult>();
                _filteredOrder ??= new Queue<FilteredKey>();

                if (_filtered.TryGetValue(key, out FilteredResult existing))
                {
                    existing.Image.Dispose();
                    _filtered[key] = result;
                    return;
                }

                _filtered[key] = result;
                _filteredOrder.Enqueue(key);

                while (_filtered.Count > MaxFilteredEntries && _filteredOrder.Count > 0)
                {
                    FilteredKey evictKey = _filteredOrder.Dequeue();
                    if (_filtered.TryGetValue(evictKey, out FilteredResult evictValue) && !evictKey.Equals(key))
                    {
                        _filtered.Remove(evictKey);
                        evictValue.Image.Dispose();
                    }
                }
            }
        }

        /// <summary>
        /// 采样 snapshot 里指定 DIP 区域的平均感知亮度（BT.601 加权 0..1）。
        /// 供 AdaptiveLuminance 用——玻璃底下内容亮→玻璃上文字压暗，暗→提亮。
        /// clip 取屏幕坐标 DIP 矩形（相对 TopLevel），换算成 snapshot 内像素坐标后降采样。
        /// </summary>
        public double SampleLuminance(Rect dipRect)
        {
            // DIP 矩形 → snapshot 像素坐标
            double sx0 = (dipRect.X * Scaling - OriginInPixels.X);
            double sy0 = (dipRect.Y * Scaling - OriginInPixels.Y);
            double sx1 = (dipRect.Right * Scaling - OriginInPixels.X);
            double sy1 = (dipRect.Bottom * Scaling - OriginInPixels.Y);

            int x0 = Math.Max(0, (int)Math.Floor(sx0));
            int y0 = Math.Max(0, (int)Math.Floor(sy0));
            int x1 = Math.Min(Image.Width, (int)Math.Ceiling(sx1));
            int y1 = Math.Min(Image.Height, (int)Math.Ceiling(sy1));
            if (x1 <= x0 || y1 <= y0)
                return 0.5;

            int w = Math.Min(x1 - x0, 32);   // 降采样上限 32x32，成本恒定
            int h = Math.Min(y1 - y0, 32);
            int stepX = Math.Max(1, (x1 - x0) / w);
            int stepY = Math.Max(1, (y1 - y0) / h);

            using SKBitmap bitmap = new();
            SKImageInfo info = new(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
            using SKSurface? surface = SKSurface.Create(info);
            if (surface is null)
                return 0.5;

            // 把 crop 区域缩绘到 32x32 采样面
            SKRect src = new(x0, y0, x1, y1);
            SKRect dst = new(0, 0, w, h);
            surface.Canvas.DrawImage(Image, src, dst);
            surface.Canvas.Flush();

            using SKImage? sample = surface.Snapshot();
            if (sample is null)
                return 0.5;

            using SKPixmap? pixmap = sample.PeekPixels();
            if (pixmap is null)
                return 0.5;

            // Bgra8888 4 字节/像素；GetPixelSpan 给整块字节视图，无 unsafe。
            ReadOnlySpan<byte> pixels = pixmap.GetPixelSpan<byte>();
            int stride = pixmap.RowBytes;
            double sum = 0.0;
            int count = 0;
            for (int y = 0; y < h; y++)
            {
                int rowBase = y * stride;
                for (int x = 0; x < w; x++)
                {
                    int o = rowBase + x * 4;
                    byte b = pixels[o + 0];
                    byte g = pixels[o + 1];
                    byte r = pixels[o + 2];
                    // BT.601 感知亮度
                    sum += (0.299 * r + 0.587 * g + 0.114 * b) / 255.0;
                    count++;
                }
            }

            return count == 0 ? 0.5 : sum / count;
        }

        /// <summary>
        /// 采样 snapshot 指定 DIP 区域的平均 alpha（0..1）。
        /// 玻璃底下若整片透明（alpha≈0），lens shader 采透明像素得到的输出近透明，
        /// 叠加 SurfaceColor 就成了"实色块"——RenderLens 用这个值做兜底：
        /// bounds 平均 alpha 极低时改走 DrawFallback 的磨砂渐变。
        /// </summary>
        public double SampleAverageAlpha(Rect dipRect)
        {
            double sx0 = (dipRect.X * Scaling - OriginInPixels.X);
            double sy0 = (dipRect.Y * Scaling - OriginInPixels.Y);
            double sx1 = (dipRect.Right * Scaling - OriginInPixels.X);
            double sy1 = (dipRect.Bottom * Scaling - OriginInPixels.Y);

            int x0 = Math.Max(0, (int)Math.Floor(sx0));
            int y0 = Math.Max(0, (int)Math.Floor(sy0));
            int x1 = Math.Min(Image.Width, (int)Math.Ceiling(sx1));
            int y1 = Math.Min(Image.Height, (int)Math.Ceiling(sy1));
            if (x1 <= x0 || y1 <= y0)
                return 0.0;

            int w = Math.Min(x1 - x0, 16);
            int h = Math.Min(y1 - y0, 16);

            using SKBitmap bitmap = new();
            SKImageInfo info = new(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
            using SKSurface? surface = SKSurface.Create(info);
            if (surface is null)
                return 0.0;

            SKRect src = new(x0, y0, x1, y1);
            SKRect dst = new(0, 0, w, h);
            surface.Canvas.DrawImage(Image, src, dst);
            surface.Canvas.Flush();

            using SKImage? sample = surface.Snapshot();
            if (sample is null)
                return 0.0;

            using SKPixmap? pixmap = sample.PeekPixels();
            if (pixmap is null)
                return 0.0;

            ReadOnlySpan<byte> pixels = pixmap.GetPixelSpan<byte>();
            int stride = pixmap.RowBytes;
            double sum = 0.0;
            int count = 0;
            for (int y = 0; y < h; y++)
            {
                int rowBase = y * stride;
                for (int x = 0; x < w; x++)
                {
                    int o = rowBase + x * 4;
                    sum += pixels[o + 3] / 255.0;
                    count++;
                }
            }

            return count == 0 ? 0.0 : sum / count;
        }

        public bool TryAddLease()
        {
            while (true)
            {
                if (System.Threading.Volatile.Read(ref _disposed) != 0)
                    return false;

                int current = System.Threading.Volatile.Read(ref _leases);
                if (System.Threading.Interlocked.CompareExchange(ref _leases, current + 1, current) == current)
                {
                    if (System.Threading.Volatile.Read(ref _disposed) != 0)
                    {
                        ReleaseLease();
                        return false;
                    }

                    return true;
                }
            }
        }

        public void ReleaseLease()
        {
            int remaining = System.Threading.Interlocked.Decrement(ref _leases);
            if (remaining < 0)
                return;

            if (remaining == 0 && System.Threading.Volatile.Read(ref _disposeRequested) != 0)
                Dispose();
        }

        public void RequestDispose()
        {
            System.Threading.Volatile.Write(ref _disposeRequested, 1);
            if (System.Threading.Volatile.Read(ref _leases) == 0)
                Dispose();
        }

        public void Dispose()
        {
            if (System.Threading.Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            lock (_filteredLock)
            {
                if (_filtered is not null)
                {
                    foreach (KeyValuePair<FilteredKey, FilteredResult> pair in _filtered)
                        pair.Value.Image.Dispose();
                    _filtered = null;
                    _filteredOrder = null;
                }
            }

            Image.Dispose();
        }
    }
}
