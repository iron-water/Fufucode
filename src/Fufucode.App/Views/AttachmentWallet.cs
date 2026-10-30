using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Fufucode.App.ViewModels;

namespace Fufucode.App.Views;

/// <summary>Three equal attachment cards with a wallet edge to reveal the remaining rows.</summary>
public sealed class AttachmentWallet : StackPanel
{
    public static readonly StyledProperty<IEnumerable<AttachedFile>?> FilesProperty =
        AvaloniaProperty.Register<AttachmentWallet, IEnumerable<AttachedFile>?>(nameof(Files));
    public IEnumerable<AttachedFile>? Files { get => GetValue(FilesProperty); set => SetValue(FilesProperty, value); }
    public Action<AttachedFile>? RemoveFile { get; init; }
    public Action<AttachedFile, int>? ReferenceFile { get; init; }
    private bool _expanded;
    private readonly List<Bitmap> _images = new();
    private AttachedFile[] _snapshot = [];

    public AttachmentWallet()
    {
        Spacing = 4;
        DetachedFromVisualTree += (_, _) => ReleaseImages();
        AttachedToVisualTree += (_, _) => Rebuild();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == FilesProperty) { _expanded = false; Rebuild(); }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var measured = base.MeasureOverride(availableSize);
        return _snapshot.Length == 0 ? measured : new Size(Math.Max(measured.Width, Math.Min(360, availableSize.Width)), measured.Height);
    }

    private void ReleaseImages() { foreach (var image in _images) image.Dispose(); _images.Clear(); }
    private void Rebuild()
    {
        ReleaseImages();
        Children.Clear();
        _snapshot = Files?.ToArray() ?? [];
        IsVisible = _snapshot.Length > 0;
        var count = _expanded ? _snapshot.Length : Math.Min(3, _snapshot.Length);
        for (var index = 0; index < count; index += 3)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*"), ColumnSpacing = 6 };
            for (var column = 0; column < 3 && index + column < count; column++)
            {
                var file = _snapshot[index + column];
                var card = CreateCard(file, index + column + 1);
                Grid.SetColumn(card, column);
                row.Children.Add(card);
            }
            Children.Add(row);
        }
        if (_snapshot.Length > 3)
        {
            var edge = new Button
            {
                Content = _expanded ? "收起附件 ▴" : $"展开其余 {_snapshot.Length - 3} 个附件 ▾",
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center,
                MinHeight = 22, Padding = new Thickness(6, 2), FontSize = 12,
            };
            edge.Classes.Add("subtle");
            edge.Click += (_, _) => { _expanded = !_expanded; Rebuild(); };
            Children.Add(edge);
        }
    }

    private static string FileSize(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return "文件不可用";
            return info.Length >= 1024 * 1024 ? $"{info.Length / (1024d * 1024):0.#} MB" : info.Length >= 1024 ? $"{info.Length / 1024d:0.#} KB" : $"{info.Length} B";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return "文件不可用"; }
    }

    private Control CreateCard(AttachedFile file, int number)
    {
        if (file.ReferenceNumber > 0) number = file.ReferenceNumber;
        var content = new Grid { RowDefinitions = new RowDefinitions("*,Auto"), Height = 94 };
        var extension = Path.GetExtension(file.Path).TrimStart('.').ToUpperInvariant();
        Control preview = new TextBlock
        {
            Text = (extension.Length == 0 ? "FILE" : extension) + "\n" + FileSize(file.Path), FontSize = 12,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        if (extension is "PNG" or "JPG" or "JPEG" or "GIF" or "WEBP" or "BMP")
        {
            try
            {
                using var stream = File.OpenRead(file.Path);
                var bitmap = Bitmap.DecodeToWidth(stream, 240);
                _images.Add(bitmap);
                preview = new Image { Source = bitmap, Stretch = Stretch.UniformToFill, ClipToBounds = true };
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            { /* A missing historical file remains an informative file card. */ }
        }
        var open = new Button { Content = preview, Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        open.Classes.Add("subtle");
        open.Click += (_, _) =>
        {
            if (preview is Image { Source: { } source })
            {
                try
                {
                    using var stream = File.OpenRead(file.Path);
                    using var canvas = new Features.FilePreview.ImagePreviewCanvas(new Bitmap(stream), ownsSource: true);
                    ImageLightbox.Show(this, canvas, file.Path);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                { ImageLightbox.Show(this, source); }
            }
            else if (File.Exists(file.Path))
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(file.Path) { UseShellExecute = true }); }
                catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            }
        };
        content.Children.Add(open);
        if (RemoveFile is not null)
        {
            var remove = new Button { Content = "×", Width = 22, Height = 22, MinHeight = 0, Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
            Avalonia.Automation.AutomationProperties.SetName(remove, "移除附件 " + number);
            remove.Click += (_, _) => RemoveFile(file);
            content.Children.Add(remove);
        }
        var label = new TextBlock
        {
            Text = $"{number:00} · {file.Name}", FontSize = 12, Margin = new Thickness(5, 3),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Control caption = label;
        if (ReferenceFile is not null)
        {
            var reference = new Button { Content = label, Padding = new Thickness(0), MinHeight = 22, HorizontalAlignment = HorizontalAlignment.Stretch };
            reference.Classes.Add("subtle");
            reference.Click += (_, _) => ReferenceFile(file, number);
            Avalonia.Automation.AutomationProperties.SetName(reference, "在光标位置引用附件 " + number);
            caption = reference;
        }
        Grid.SetRow(caption, 1); content.Children.Add(caption);
        var card = new Border
        {
            Child = content, CornerRadius = new CornerRadius(6), ClipToBounds = true,
            BorderThickness = new Thickness(1), MinWidth = 0,
        };
        card.Bind(Border.BackgroundProperty, new DynamicResourceExtension("AppSurfaceBackground"));
        card.Bind(Border.BorderBrushProperty, new DynamicResourceExtension("AppCardStroke"));
        Avalonia.Automation.AutomationProperties.SetName(card, $"附件 {number}，{file.Name}");
        return card;
    }
}
