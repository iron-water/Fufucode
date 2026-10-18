using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Fufucode.App.Features.Skins.LiquidGlass
{
    /// <summary>
    /// 把资源字典里的 SolidColorBrush（或裸 Color）解成 LiquidGlassSurface 需要的 Color。
    /// 玻璃面参数色在 LiquidGlassTokens.axaml 里以 brush 形式存（别处也可能直接消费），
    /// adapter 用 DynamicResource/本地资源链取出来后要剥成 Color 才能赋给 SurfaceColor/TintColor。
    /// </summary>
    internal sealed class ColorResourceConverter : IValueConverter
    {
        public static readonly ColorResourceConverter Instance = new();

        private ColorResourceConverter()
        {
        }

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            return value switch
            {
                ISolidColorBrush brush => brush.Color,
                Color color => color,
                _ => Colors.Transparent,
            };
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
