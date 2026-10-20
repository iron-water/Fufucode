using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using LiveMarkdown.Avalonia;

namespace Fufucode.App.Views;

/// <summary>
/// 工具结果的图片缩略图：data URI、本地路径、网络地址都能显示，点击进全屏预览。
///
/// 只做"看一眼"的用途，尺寸固定上限，不参与文档排版。
/// 解码失败不抛异常，也不显示坏图占位——工具输出仍以文本形式保留。
/// </summary>
public sealed class ToolImageView : ContentControl
{
    private readonly Image _image = new()
    {
        Stretch = Stretch.Uniform,
        MaxWidth = 240,
        MaxHeight = 160,
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
    };

    public static readonly StyledProperty<string?> SourceTextProperty =
        AvaloniaProperty.Register<ToolImageView, string?>(nameof(SourceText));

    public string? SourceText
    {
        get => GetValue(SourceTextProperty);
        set => SetValue(SourceTextProperty, value);
    }

    public ToolImageView()
    {
        Content = _image;
        Cursor = new Cursor(StandardCursorType.Hand);
        PointerPressed += OnPressed;
    }

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_image.Source is not { } source) return;
        ImageLightbox.Show(this, source);
        e.Handled = true;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceTextProperty) Load(change.NewValue as string);
    }

    private void Load(string? text)
    {
        _image.Source = null;
        if (string.IsNullOrWhiteSpace(text)) return;

        var value = text.Trim();
        try
        {
            if (value.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
            {
                var comma = value.IndexOf(',');
                if (comma < 0 || !value[..comma].Contains("base64", StringComparison.OrdinalIgnoreCase)) return;
                var bytes = Convert.FromBase64String(value[(comma + 1)..]);
                _image.Source = new Bitmap(new MemoryStream(bytes));
                return;
            }

            if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                AsyncImageLoader.SetSource(_image, value);
                return;
            }

            if (File.Exists(value)) _image.Source = new Bitmap(value);
        }
        catch (Exception)
        {
            // 坏图不影响工具卡其余内容。
            _image.Source = null;
        }
    }
}
