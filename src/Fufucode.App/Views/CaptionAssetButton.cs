using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;

namespace Fufucode.App.Views;

/// <summary>G's complete 46×32 state image is the button surface; the template adds no second hover plate.</summary>
public sealed class CaptionAssetButton : Button
{
    protected override Type StyleKeyOverride => typeof(Button);
    public static readonly StyledProperty<string> KindProperty = AvaloniaProperty.Register<CaptionAssetButton, string>(nameof(Kind), "close");
    public static readonly StyledProperty<bool> UseSkinGlyphProperty = AvaloniaProperty.Register<CaptionAssetButton, bool>(nameof(UseSkinGlyph));
    private static readonly Dictionary<string, Bitmap> Images = new(StringComparer.Ordinal);
    private readonly Image _image = new() { Width = 46, Height = 32 };
    private readonly PathIcon _glyph = new() { Width = 12, Height = 12 };
    public string Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public bool UseSkinGlyph { get => GetValue(UseSkinGlyphProperty); set => SetValue(UseSkinGlyphProperty, value); }
    internal string AssetPathForTesting { get; private set; } = string.Empty;
    internal string? StateForTesting { get; set; }

    public CaptionAssetButton()
    {
        Width = 46; Height = 32; Padding = new Thickness(0); Content = _image;
        Classes.Add("captionButton");
        this.Bind(UseSkinGlyphProperty, new DynamicResourceExtension("AppSkinIconMonochrome"));
        AttachedToVisualTree += (_, _) => RefreshImage();
        ActualThemeVariantChanged += (_, _) => RefreshImage();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == KindProperty || change.Property == IsPointerOverProperty ||
            change.Property == IsPressedProperty || change.Property == IsEnabledProperty || change.Property == UseSkinGlyphProperty)
            RefreshImage();
    }

    internal void RefreshImage()
    {
        if (_image is null) return;
        if (UseSkinGlyph)
        {
            var glyphPath = Kind switch
            {
                "minimize" => "M2,8 H14",
                "maximize" => "M2,2 H14 V14 H2 Z",
                "restore" => "M2,5 H11 V14 H2 Z M5,5 V2 H14 V11 H11",
                _ => "M3,3 L13,13 M13,3 L3,13"
            };
            _glyph.Data = Geometry.Parse(glyphPath).GetWidenedGeometry(new Pen(Brushes.Black, 1.2));
            Content = _glyph;
            AssetPathForTesting = string.Empty;
            return;
        }
        Content = _image;
        var theme = ActualThemeVariant == ThemeVariant.Dark ? "dark" : "light";
        var state = StateForTesting ?? (!IsEffectivelyEnabled ? "disabled" : IsPressed ? "pressed" : IsPointerOver ? "hover" : "normal");
        var path = $"avares://Fufucode.App/Assets/Brand/titlebar/{theme}/{Kind}-{state}@2x.png";
        if (!Images.TryGetValue(path, out var bitmap))
        {
            using var source = AssetLoader.Open(new Uri(path));
            bitmap = new Bitmap(source); Images.Add(path, bitmap);
        }
        AssetPathForTesting = path;
        _image.Source = bitmap;
    }
}
