using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace Fufucode.App.Features.Skins.LiquidGlass
{
    /// <summary>
    /// 内容层卡片的 Standard Material（HIG Materials §1.3：thin/regular 材质用模糊+鲜明度
    /// 在内容层内部建立区分）。与 chrome 用的 LiquidGlassAdapter 不同，这里不改宿主在父
    /// 容器里的位置——把宿主 Border 的 Child 移进一个 LiquidGlassSurface 再放回，Border
    /// 只剩透明外壳。
    /// 在 Apple Inset Grouped 体系下，优先整组 Border.settingsGroup 整体包裹为单一连续玻璃板。
    /// </summary>
    public sealed class LiquidGlassMaterialAdapter : IDisposable
    {
        private readonly Border _host;
        private readonly Control? _originalChild;
        private readonly Thickness _originalPadding;
        private readonly LiquidGlassSurface _surface;
        private readonly EventHandler _themeChanged;
        private bool _disposed;

        private LiquidGlassMaterialAdapter(Border host)
        {
            _host = host;
            _originalChild = host.Child;
            _originalPadding = host.Padding;

            var corner = host.CornerRadius.TopLeft > 0 ? host.CornerRadius : new CornerRadius(16);
            _surface = new LiquidGlassSurface
            {
                BlurRadius = 28.0,
                Vibrancy = 1.45,
                RefractionHeight = 8.0,
                RefractionAmount = 2.0,
                ChromaticAberration = true,
                DepthEffect = false,
                HighlightEnabled = true,
                HighlightWidth = 0.75,
                HighlightOpacity = 0.55,
                HighlightAngle = 90.0,
                HighlightFalloff = 2.0,
                ShadowEnabled = true,
                ShadowRadius = 24.0,
                ShadowOpacity = 0.08,
                ProgressiveBlurEnabled = false,
                BackdropOpacity = 1.0,
                CornerRadius = corner,
                Padding = host.Padding,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
            };
            _surface.Classes.Add("glassMaterial");

            host.Child = null;
            host.Padding = default;
            _surface.Content = _originalChild;
            host.Child = _surface;
            host.Classes.Add("glassMaterialHost");

            ApplyColors();
            _themeChanged = (_, _) => ApplyColors();
            host.ActualThemeVariantChanged += _themeChanged;
        }

        public Border Host => _host;

        /// <summary>该 Border 是否应套内容层材质（设置组卡片、设置面板、输入区卡片）。</summary>
        public static bool IsMaterialHost(Border border)
        {
            if (border.Classes.Contains("settingsGroup")) return true;
            if (border.Classes.Contains("settingsPanel")) return true;
            if (border.Classes.Contains("composerCard")) return true;
            if (border.Classes.Contains("settingsRow"))
            {
                // 若外层已经有 settingsGroup，整组已有一体化玻璃板，行内不再重复套
                var parent = border.Parent;
                while (parent is not null)
                {
                    if (parent is Border b && b.Classes.Contains("settingsGroup")) return false;
                    parent = parent.Parent;
                }
                return true;
            }
            return false;
        }

        public static LiquidGlassMaterialAdapter? TryAttach(Border host)
        {
            ArgumentNullException.ThrowIfNull(host);
            if (host.Child is LiquidGlassSurface) return null; // 已经包过
            return new LiquidGlassMaterialAdapter(host);
        }

        private void ApplyColors()
        {
            var variant = _host.ActualThemeVariant;
            _surface.SurfaceColor = Resolve("LiquidGlassMaterialColor", variant,
                variant == ThemeVariant.Dark ? Color.FromArgb(0x47, 0x1C, 0x1C, 0x1E) : Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF));
            _surface.TintColor = Colors.Transparent;
        }

        private Color Resolve(string key, ThemeVariant variant, Color fallback)
        {
            if (_host.TryFindResource(key, variant, out var value))
            {
                return value switch
                {
                    ISolidColorBrush brush => brush.Color,
                    Color color => color,
                    _ => fallback,
                };
            }
            return fallback;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _host.ActualThemeVariantChanged -= _themeChanged;
            _host.Classes.Remove("glassMaterialHost");
            if (ReferenceEquals(_host.Child, _surface))
            {
                _surface.Content = null;
                _host.Child = _originalChild;
                _host.Padding = _originalPadding;
            }
        }
    }
}
