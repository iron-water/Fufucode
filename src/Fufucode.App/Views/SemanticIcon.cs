using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;

namespace Fufucode.App.Views;

/// <summary>Resource lookup belongs to the visible icon's window, including floating windows.</summary>
public sealed class SemanticIcon : PathIcon
{
    protected override Type StyleKeyOverride => typeof(PathIcon);
    public static readonly StyledProperty<string> IconNameProperty = AvaloniaProperty.Register<SemanticIcon, string>(nameof(IconName), "AppsList");
    public static readonly StyledProperty<string?> CategoryColorProperty = AvaloniaProperty.Register<SemanticIcon, string?>(nameof(CategoryColor));
    public static readonly StyledProperty<bool> MonochromeProperty = AvaloniaProperty.Register<SemanticIcon, bool>(nameof(Monochrome));
    /// <summary>调用方显式要求单色（如 Win11 设置侧栏）：覆盖 Monochrome 绑定。</summary>
    public static readonly StyledProperty<bool> ForceMonochromeProperty = AvaloniaProperty.Register<SemanticIcon, bool>(nameof(ForceMonochrome));
    public string IconName { get => GetValue(IconNameProperty); set => SetValue(IconNameProperty, value); }
    public string? CategoryColor { get => GetValue(CategoryColorProperty); set => SetValue(CategoryColorProperty, value); }
    public bool Monochrome { get => GetValue(MonochromeProperty); set => SetValue(MonochromeProperty, value); }
    public bool ForceMonochrome { get => GetValue(ForceMonochromeProperty); set => SetValue(ForceMonochromeProperty, value); }

    public SemanticIcon()
    {
        this.Bind(MonochromeProperty, new DynamicResourceExtension("AppSkinIconMonochrome"));
        Refresh();
    }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IconNameProperty || change.Property == MonochromeProperty
            || change.Property == CategoryColorProperty || change.Property == ForceMonochromeProperty)
            Refresh();
    }
    private void Refresh()
    {
        this.Bind(DataProperty, new DynamicResourceExtension("Icon." + IconName));
        // Win11 观感：Monochrome（皮肤级）或 ForceMonochrome（调用方）抑制彩色分类图标；
        // 未抑制时才允许彩色，否则统一次级文字色。
        if (ForceMonochrome || Monochrome || CategoryColor is not { } color || !Color.TryParse(color, out var parsed))
        {
            this.Bind(ForegroundProperty, new DynamicResourceExtension("AppTextSecondary"));
            return;
        }
        Foreground = new SolidColorBrush(parsed);
    }
}
