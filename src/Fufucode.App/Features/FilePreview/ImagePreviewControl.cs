using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Fufucode.App.Features.FilePreview;

/// <summary>只读图片视图：适配/原始尺寸、缩放、旋转、平移和棋盘背景。</summary>
public sealed class ImagePreviewControl : UserControl
{
    private readonly Border _surface;
    private readonly Image _image;
    private readonly ScaleTransform _scale = new(1, 1);
    private readonly RotateTransform _rotation = new(0);
    private readonly TranslateTransform _translation = new(0, 0);
    private readonly Bitmap _bitmap;
    private Point? _dragOrigin;
    private Vector _pan;

    public ImagePreviewControl(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        _bitmap = new Bitmap(stream);
        _image = new Image
        {
            Source = _bitmap,
            Stretch = Stretch.Uniform,
            RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            RenderTransform = new TransformGroup { Children = { _scale, _rotation, _translation } },
        };
        _surface = new Border
        {
            Background = Brushes.Transparent,
            Child = new ScrollViewer
            {
                Content = _image,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            },
        };
        Content = _surface;
        AddHandler(InputElement.PointerWheelChangedEvent, OnPointerWheel, RoutingStrategies.Bubble);
        AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Bubble);
        AddHandler(PointerMovedEvent, OnPointerMoved, RoutingStrategies.Bubble);
        AddHandler(PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Bubble);
    }

    public double Zoom { get; private set; } = 1;
    public int RotationDegrees { get; private set; }
    public bool IsCheckerboard { get; private set; }
    public bool IsFitMode { get; private set; } = true;
    public PixelSize OriginalSize => _bitmap.PixelSize;

    public void ZoomBy(double factor)
    {
        Zoom = Math.Clamp(Zoom * factor, 0.1, 8);
        IsFitMode = false;
        ApplyTransform();
    }

    public void SetFitMode()
    {
        Zoom = 1;
        IsFitMode = true;
        _pan = default;
        ApplyTransform();
    }

    public void SetOriginalSize()
    {
        Zoom = 1;
        IsFitMode = false;
        _pan = default;
        ApplyTransform();
    }

    public void RotateBy(int degrees)
    {
        RotationDegrees = (RotationDegrees + degrees) % 360;
        if (RotationDegrees < 0) RotationDegrees += 360;
        ApplyTransform();
    }

    public void ToggleCheckerboard()
    {
        IsCheckerboard = !IsCheckerboard;
        _surface.Background = IsCheckerboard ? CheckerboardBrush() : Brushes.Transparent;
    }

    private void ApplyTransform()
    {
        _scale.ScaleX = Zoom;
        _scale.ScaleY = Zoom;
        _rotation.Angle = RotationDegrees;
        _translation.X = _pan.X;
        _translation.Y = _pan.Y;
    }

    private void OnPointerWheel(object? sender, PointerWheelEventArgs args)
    {
        if (args.Delta.Y == 0) return;
        ZoomBy(args.Delta.Y > 0 ? 1.1 : 1 / 1.1);
        args.Handled = true;
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs args)
    {
        if (!args.Handled && args.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _dragOrigin = args.GetPosition(this);
            args.Handled = true;
        }
    }

    private void OnPointerMoved(object? sender, PointerEventArgs args)
    {
        if (_dragOrigin is not { } origin || !args.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var current = args.GetPosition(this);
        _pan += current - origin;
        _dragOrigin = current;
        IsFitMode = false;
        ApplyTransform();
        args.Handled = true;
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs args) => _dragOrigin = null;

    private static IBrush CheckerboardBrush() => new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Colors.White, 0),
            new GradientStop(Color.FromRgb(224, 224, 224), 0.25),
            new GradientStop(Colors.White, 0.5),
            new GradientStop(Color.FromRgb(224, 224, 224), 0.75),
            new GradientStop(Colors.White, 1),
        },
    };
}
