using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Fufucode.App.Infrastructure;

/// <summary>
/// 布尔到不透明度的映射：true=1、false=0。
/// 配合 Opacity 的过渡，可在展开/出现时做淡入，而不必为每个控件写动画代码。
/// </summary>
public sealed class BoolToOpacityConverter : IValueConverter
{
    public static readonly BoolToOpacityConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? 1d : 0d;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double opacity && opacity > 0.5;
}
