using System;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Fufucode.App.Features.FilePreview;

/// <summary>
/// Shared image viewport. Zoom is image pixels per screen pixel (1 = 100%);
/// vector images use their intrinsic size as pixels. Pan is in viewport DIPs.
/// The owner must Dispose when permanently removing the canvas; detach/reparent is reversible.
/// </summary>
public sealed class ImagePreviewCanvas : Control, IDisposable
{
    public const double MinimumZoom = 0.01;
    public const double MaximumZoom = 64;
    private static readonly IBrush Checkerboard = CreateCheckerboard();
    private ImagePreviewSource? _source;
    private TopLevel? _topLevel;
    private IPointer? _dragPointer;
    private Point _dragOrigin;
    private double _zoom = 1;
    private double _renderScaling = 1;
    private readonly PixelSize _originalSize;
    private readonly Size _sourceSize;

    public ImagePreviewCanvas(IImage source, bool ownsSource = false)
        : this(new ImagePreviewSource(source, ownsSource)) { }

    internal ImagePreviewCanvas(ImagePreviewSource source)
    {
        _source = source;
        _sourceSize = source.Image.Size;
        _originalSize = source.Image is Bitmap bitmap
            ? bitmap.PixelSize
            : new PixelSize(Math.Max(1, (int)Math.Ceiling(_sourceSize.Width)),
                Math.Max(1, (int)Math.Ceiling(_sourceSize.Height)));
        ClipToBounds = true;
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    public event EventHandler? StateChanged;
    public IImage? Source => _source?.Image;
    public PixelSize OriginalSize => _originalSize;
    public bool IsDisposed => _source is null;
    public bool IsFitMode { get; private set; } = true;
    public bool IsCheckerboard { get; private set; } = true;
    public bool IsDragging => _dragPointer is not null;
    public int RotationDegrees { get; private set; }
    public Vector Pan { get; private set; }
    public double RenderScaling => _renderScaling;
    public double Zoom => IsFitMode ? FitZoom : _zoom;
    public double FitZoom
    {
        get
        {
            var extent = RotatedPixelExtent;
            if (Bounds.Width <= 0 || Bounds.Height <= 0) return 1;
            return Math.Min(Bounds.Width * _renderScaling / extent.Width,
                Bounds.Height * _renderScaling / extent.Height);
        }
    }

    public Size RotatedPixelExtent
    {
        get
        {
            var radians = RotationDegrees * Math.PI / 180;
            var cos = Math.Abs(Math.Cos(radians));
            var sin = Math.Abs(Math.Sin(radians));
            return new Size(_originalSize.Width * cos + _originalSize.Height * sin,
                _originalSize.Width * sin + _originalSize.Height * cos);
        }
    }

    public Size DisplayedExtent => new(RotatedPixelExtent.Width * Zoom / _renderScaling,
        RotatedPixelExtent.Height * Zoom / _renderScaling);

    public void SetFitMode()
    {
        EnsureAlive();
        CancelDrag();
        IsFitMode = true;
        Pan = default;
        Changed();
    }

    public void SetOriginalSize()
    {
        EnsureAlive();
        CancelDrag();
        IsFitMode = false;
        _zoom = 1;
        Pan = default;
        Changed();
    }

    public void ZoomBy(double factor) => ZoomAt(factor, new Point(Bounds.Width / 2, Bounds.Height / 2));

    /// <summary>Keep the image point beneath the viewport anchor stationary while zooming.</summary>
    public void ZoomAt(double factor, Point anchor)
    {
        EnsureAlive();
        if (!double.IsFinite(factor) || factor <= 0) throw new ArgumentOutOfRangeException(nameof(factor));
        if (!double.IsFinite(anchor.X) || !double.IsFinite(anchor.Y)) throw new ArgumentOutOfRangeException(nameof(anchor));
        var previous = Zoom;
        // A very small/large viewport can fit outside the manual zoom limits.
        // Clamping must never make a zoom-in shrink, or a zoom-out enlarge the image.
        var next = Math.Clamp(previous * factor, Math.Min(MinimumZoom, previous), Math.Max(MaximumZoom, previous));
        var relative = anchor - new Point(Bounds.Width / 2, Bounds.Height / 2);
        Pan = relative - (relative - Pan) * (next / previous);
        _zoom = next;
        IsFitMode = false;
        Changed();
    }

    public void PanBy(Vector delta)
    {
        EnsureAlive();
        if (!double.IsFinite(delta.X) || !double.IsFinite(delta.Y)) throw new ArgumentOutOfRangeException(nameof(delta));
        if (delta == default) return;
        // Freeze the effective fit scale before entering manual pan mode.
        _zoom = Zoom;
        IsFitMode = false;
        Pan += delta;
        Changed();
    }

    public void RotateBy(int degrees)
    {
        EnsureAlive();
        RotationDegrees = (int)(((long)RotationDegrees + degrees) % 360 + 360) % 360;
        Changed();
    }

    public void ToggleCheckerboard()
    {
        EnsureAlive();
        IsCheckerboard = !IsCheckerboard;
        Changed();
    }

    internal ImagePreviewCanvas CreateSharedCanvas()
    {
        EnsureAlive();
        return new ImagePreviewCanvas(_source!.Retain());
    }

    internal ImagePreviewSource RetainSource()
    {
        EnsureAlive();
        return _source!.Retain();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = new Rect(Bounds.Size);
        using (context.PushClip(bounds))
        {
            context.DrawRectangle(IsCheckerboard ? Checkerboard : Brushes.Black, null, bounds);
            if (Source is not { } image || Bounds.Width <= 0 || Bounds.Height <= 0) return;
            var scale = Zoom / _renderScaling;
            var destination = new Rect(-_originalSize.Width * scale / 2, -_originalSize.Height * scale / 2,
                _originalSize.Width * scale, _originalSize.Height * scale);
            var transform = Matrix.CreateRotation(RotationDegrees * Math.PI / 180)
                * Matrix.CreateTranslation(Bounds.Width / 2 + Pan.X, Bounds.Height / 2 + Pan.Y);
            using (context.PushTransform(transform))
                context.DrawImage(image, new Rect(_sourceSize), destination);
        }
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        Changed();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (IsDisposed) return;
        _topLevel = TopLevel.GetTopLevel(this);
        if (_topLevel is not null) _topLevel.ScalingChanged += OnScalingChanged;
        UpdateScaling();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        CancelDrag();
        if (_topLevel is not null) _topLevel.ScalingChanged -= OnScalingChanged;
        _topLevel = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnScalingChanged(object? sender, EventArgs e) => UpdateScaling();
    private void UpdateScaling()
    {
        _renderScaling = _topLevel?.RenderScaling ?? 1;
        Changed();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (IsDisposed || e.Delta.Y == 0) return;
        ZoomAt(Math.Pow(1.1, Math.Clamp(e.Delta.Y, -10, 10)), e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (IsDisposed || e.Handled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            || (_dragPointer is not null && _dragPointer != e.Pointer)) return;
        Focus();
        CancelDrag();
        _dragOrigin = e.GetPosition(this);
        _dragPointer = e.Pointer;
        e.Pointer.Capture(this);
        Cursor = new Cursor(StandardCursorType.SizeAll);
        Changed();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragPointer != e.Pointer) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            CancelDrag();
            return;
        }
        var current = e.GetPosition(this);
        PanBy(current - _dragOrigin);
        _dragOrigin = current;
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragPointer != e.Pointer) return;
        CancelDrag();
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _dragPointer = null;
        Cursor = new Cursor(StandardCursorType.Hand);
        Changed();
    }

    private void CancelDrag()
    {
        var pointer = _dragPointer;
        _dragPointer = null;
        if (pointer?.Captured == this) pointer.Capture(null);
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    private void Changed()
    {
        InvalidateVisual();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void EnsureAlive() => ObjectDisposedException.ThrowIf(IsDisposed, this);

    public void Dispose()
    {
        if (IsDisposed) return;
        CancelDrag();
        if (_topLevel is not null) _topLevel.ScalingChanged -= OnScalingChanged;
        _topLevel = null;
        var source = _source!;
        _source = null;
        source.Release();
        Changed();
    }

    private static IBrush CreateCheckerboard() => new DrawingBrush
    {
        TileMode = TileMode.Tile,
        SourceRect = new RelativeRect(0, 0, 24, 24, RelativeUnit.Absolute),
        DestinationRect = new RelativeRect(0, 0, 24, 24, RelativeUnit.Absolute),
        Stretch = Stretch.None,
        Drawing = new DrawingGroup
        {
            Children =
            {
                new GeometryDrawing { Brush = new SolidColorBrush(Color.FromRgb(224, 224, 224)), Geometry = new RectangleGeometry(new Rect(0, 0, 24, 24)) },
                new GeometryDrawing { Brush = Brushes.White, Geometry = new RectangleGeometry(new Rect(0, 0, 12, 12)) },
                new GeometryDrawing { Brush = Brushes.White, Geometry = new RectangleGeometry(new Rect(12, 12, 12, 12)) },
            },
        },
    };
}

/// <summary>Leases keep a Files-owned bitmap alive while a viewport or the clipboard uses it.</summary>
internal sealed class ImagePreviewSource
{
    private readonly bool _ownsImage;
    private int _references;

    public ImagePreviewSource(IImage image, bool ownsImage)
    {
        ArgumentNullException.ThrowIfNull(image);
        Image = image;
        _ownsImage = ownsImage;
        _references = 1;
        var size = image.Size;
        if (!double.IsFinite(size.Width) || !double.IsFinite(size.Height) || size.Width <= 0 || size.Height <= 0
            || size.Width > int.MaxValue || size.Height > int.MaxValue)
        {
            if (ownsImage && image is IDisposable disposable) disposable.Dispose();
            throw new ArgumentException("图片尺寸无效。", nameof(image));
        }
    }

    public IImage Image { get; }
    public ImagePreviewSource Retain()
    {
        while (true)
        {
            var references = Volatile.Read(ref _references);
            ObjectDisposedException.ThrowIf(references <= 0, this);
            if (Interlocked.CompareExchange(ref _references, references + 1, references) == references) return this;
        }
    }

    public void Release()
    {
        if (Interlocked.Decrement(ref _references) == 0 && _ownsImage && Image is IDisposable disposable)
            disposable.Dispose();
    }
}
