using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Fufucode.App.Views;

/// <summary>把左侧导航项的图标键名解析成几何资源（如 "Color" → Icon.Color）。</summary>
public sealed class SettingsIconConverter : IValueConverter
{
    public static readonly SettingsIconConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string key || key.Length == 0) return null;
        if (Application.Current?.TryGetResource("Icon." + key, null, out var geometry) == true && geometry is Geometry resolved)
            return resolved;
        object? fallback = null;
        Application.Current?.TryGetResource("Icon.Settings", null, out fallback);
        return fallback as Geometry;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    /// <summary>把 #RRGGBB 解析成画刷（分类图标的彩色）；空值留给主题默认。</summary>
    public static readonly IValueConverter Brush = new HexBrushConverter();

    private sealed class HexBrushConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is not string hex || hex.Length != 7 || hex[0] != '#') return null;
            try { return new SolidColorBrush(Color.Parse(hex)); }
            catch (FormatException) { return null; }
        }
        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
