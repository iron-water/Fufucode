using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Fufucode.App.Infrastructure;

/// <summary>把 IEnumerable&lt;string&gt; 拼成逗号分隔文本（设置页模型 id 列表）。</summary>
public sealed class StringJoinConverter : IValueConverter
{
    public static readonly StringJoinConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is System.Collections.Generic.IEnumerable<string> items
            ? string.Join(", ", items)
            : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
