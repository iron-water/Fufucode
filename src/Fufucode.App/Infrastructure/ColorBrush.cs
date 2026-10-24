using System.Collections.Concurrent;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Fufucode.App.Infrastructure;

/// <summary>
/// 语法高亮给的是颜色值，视图绑的是画刷。同一个颜色在整份 diff 里会被上千个片段复用，
/// 这里按颜色缓存不可变画刷，避免每次渲染都新建对象。
/// </summary>
public static class ColorBrush
{
    private static readonly ConcurrentDictionary<uint, IBrush> Cache = new();

    public static IBrush? For(Color? color) =>
        color is not { } value ? null : Cache.GetOrAdd(value.ToUInt32(), key => new ImmutableSolidColorBrush(Color.FromUInt32(key)));
}
