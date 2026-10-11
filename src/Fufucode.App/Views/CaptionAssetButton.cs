using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;

namespace Fufucode.App.Views;

/// <summary>G's complete 46×32 state image is the button surface; the template adds no second hover plate.</summary>
public sealed class CaptionAssetButton : Button
{
    public static readonly StyledProperty<string> KindProperty = AvaloniaProperty.Register<CaptionAssetButton, string>(nameof(Kind), "close");
    private static readonly Dictionary<string, Bitmap> Images = new(StringComparer.Ordinal);
    private readonly Image _image = new() { Width = 46, Height = 32 };
    public string Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }
    internal string AssetPathForTesting { get; private set; } = string.Empty;
    internal string? StateForTesting { get; set; }

    public CaptionAssetButton()
    {
        Width = 46; Height = 32; Padding = new Thickness(0); Content = _image;
        Classes.Add("captionButton");
        AttachedToVisualTree += (_, _) => RefreshImage();
        ActualThemeVariantChanged += (_, _) => RefreshImage();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == KindProperty || change.Property == IsPointerOverProperty ||
            change.Property == IsPressedProperty || change.Property == IsEnabledProperty)
            RefreshImage();
    }

    internal void RefreshImage()
    {
        if (_image is null) return;
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
