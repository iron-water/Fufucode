using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Fufucode.App.Views;

namespace Fufucode.App.Features.FilePreview;

/// <summary>Files image view. Permanently removed views must be disposed by their host.</summary>
public sealed class ImagePreviewControl : UserControl, IDisposable
{
    // Compatibility entry point; providers should use the predecoded-image overload.
    public ImagePreviewControl(byte[] bytes) : this(bytes, filePath: null) { }

    public ImagePreviewControl(byte[] bytes, string? filePath)
        : this(Decode(bytes), filePath, ownsSource: true) { }

    /// <param name="ownsSource">Transfer disposal ownership to the view. Otherwise the caller keeps the image alive.</param>
    public ImagePreviewControl(IImage source, string? filePath = null, bool ownsSource = false)
    {
        FilePath = filePath;
        Canvas = new ImagePreviewCanvas(source, ownsSource);
        Content = Canvas;
        // Tunnel runs before the canvas captures the second click for dragging.
        AddHandler(PointerPressedEvent, OnImagePressed, RoutingStrategies.Tunnel);
    }

    public ImagePreviewCanvas Canvas { get; }
    public string? FilePath { get; }
    public double Zoom => Canvas.Zoom;
    public int RotationDegrees => Canvas.RotationDegrees;
    public bool IsCheckerboard => Canvas.IsCheckerboard;
    public bool IsFitMode => Canvas.IsFitMode;
    public bool IsDisposed => Canvas.IsDisposed;
    public PixelSize OriginalSize => Canvas.OriginalSize;
    public Vector Pan => Canvas.Pan;
    public void ZoomBy(double factor) => Canvas.ZoomBy(factor);
    public void SetFitMode() => Canvas.SetFitMode();
    public void SetOriginalSize() => Canvas.SetOriginalSize();
    public void RotateBy(int degrees) => Canvas.RotateBy(degrees);
    public void ToggleCheckerboard() => Canvas.ToggleCheckerboard();
    public void PanBy(Vector delta) => Canvas.PanBy(delta);

    public void OpenLightbox()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        ImageLightbox.Show(this, Canvas, FilePath);
    }

    private void OnImagePressed(object? sender, PointerPressedEventArgs args)
    {
        if (IsDisposed || args.ClickCount != 2 || !args.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        OpenLightbox();
        args.Handled = true;
    }

    private static Bitmap Decode(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        using var stream = new MemoryStream(bytes, writable: false);
        return new Bitmap(stream);
    }

    public void Dispose() => Canvas.Dispose();
}
