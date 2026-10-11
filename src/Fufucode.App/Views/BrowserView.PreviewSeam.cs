using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Fufucode.App.ViewModels;

/// <summary>
/// E-seam 接线用的可见性判定：右栏当前 tab 的 PanelId 以 "|preview" 结尾时点亮内置浏览器。
/// 放在 Fufucode.App.ViewModels 命名空间，是为了复用 WorkspacePanelView.axaml 已有的 vm: 前缀，
/// 从而不需要修改工作区面板的视图模型。
/// 只依赖 PanelId 字符串约定，不依赖 D 轨类型，合并冲突面最小。
/// </summary>
public sealed class BrowserPreviewSeam : IValueConverter
{
    /// <summary>D 轨创建 Preview tab 时使用的 PanelId 后缀约定：identity + "|preview"。</summary>
    public const string PreviewPanelIdSuffix = "|preview";

    public static BrowserPreviewSeam Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string panelId && panelId.EndsWith(PreviewPanelIdSuffix, StringComparison.Ordinal);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("Preview 可见性是单向判定。");
}
