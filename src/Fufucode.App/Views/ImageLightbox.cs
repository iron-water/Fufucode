using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;

namespace Fufucode.App.Views;

/// <summary>前台图片预览：缩放、平移、旋转、适配、复制和系统打开。</summary>
internal static class ImageLightbox
{
    public static void Show(Visual anchor, IImage source, string? filePath = null)
    {
        var image = new ImagePreviewSurface(source, filePath);
        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(10),
        };
        var window = new Window
        {
            Background = Brushes.Black,
            Width = 1100,
            Height = 800,
            MinWidth = 520,
            MinHeight = 360,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Title = filePath is null ? "图片预览" : Path.GetFileName(filePath),
        };

        Button Action(string label, Action action, string? tip = null)
        {
            var button = new Button { Content = label, Padding = new Thickness(10, 5) };
            if (tip is not null) ToolTip.SetTip(button, tip);
            button.Click += (_, _) => action();
            return button;
        }

        toolbar.Children.Add(Action("−", () => image.ZoomBy(0.8), "缩小"));
        toolbar.Children.Add(Action("+", () => image.ZoomBy(1.25), "放大"));
        toolbar.Children.Add(Action("适配", image.Fit, "适配窗口"));
        toolbar.Children.Add(Action("原始", image.Original, "原始尺寸"));
        toolbar.Children.Add(Action("↶", () => image.RotateBy(-90), "向左旋转"));
        toolbar.Children.Add(Action("↷", () => image.RotateBy(90), "向右旋转"));
        toolbar.Children.Add(Action("棋盘", image.ToggleCheckerboard, "透明背景棋盘"));
        toolbar.Children.Add(Action("复制", () => _ = image.CopyAsync(window), "复制图片"));
        toolbar.Children.Add(Action("外部打开", () => _ = image.OpenExternalAsync(window), "使用系统默认应用打开"));
        toolbar.Children.Add(Action("关闭", window.Close, "关闭预览"));

        var layout = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        Grid.SetRow(image, 0); Grid.SetRow(toolbar, 1);
        layout.Children.Add(image); layout.Children.Add(toolbar);
        window.Content = layout;
        window.KeyDown += (_, args) => { if (args.Key == Key.Escape) window.Close(); };
        if (TopLevel.GetTopLevel(anchor) is Window owner) window.Show(owner);
        else window.Show();
    }

    private sealed class ImagePreviewSurface : ContentControl
    {
        private readonly Image _image;
        private readonly IImage _source;
        private readonly string? _filePath;
        private readonly ScaleTransform _scale = new(1, 1);
        private readonly RotateTransform _rotation = new(0);
        private readonly TranslateTransform _translation = new(0, 0);
        private readonly Border _surface;
        private Point? _dragOrigin;
        private Vector _pan;
        private double _zoom = 1;
        private int _rotationDegrees;

        public ImagePreviewSurface(IImage source, string? filePath)
        {
            _source = source;
            _filePath = filePath;
            _image = new Image
            {
                Source = source,
                Stretch = Stretch.Uniform,
                RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
                RenderTransform = new TransformGroup { Children = { _scale, _rotation, _translation } },
            };
            _surface = new Border
            {
                Background = Brushes.Black,
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

        public void ZoomBy(double factor)
        {
            _zoom = Math.Clamp(_zoom * factor, 0.1, 8);
            ApplyTransform();
        }

        public void Fit()
        {
            _zoom = 1; _pan = default; ApplyTransform();
        }

        public void Original()
        {
            _zoom = 1; _pan = default; ApplyTransform();
        }

        public void RotateBy(int degrees)
        {
            _rotationDegrees = (_rotationDegrees + degrees) % 360;
            if (_rotationDegrees < 0) _rotationDegrees += 360;
            ApplyTransform();
        }

        public void ToggleCheckerboard() => _surface.Background = _surface.Background == Brushes.Black
            ? CheckerboardBrush() : Brushes.Black;

        public async Task CopyAsync(Window window)
        {
            if (_source is not Bitmap bitmap || window.Clipboard is not { } clipboard) return;
            var data = new DataTransfer();
            data.Add(DataTransferItem.Create(DataFormat.Bitmap, bitmap));
            await clipboard.SetDataAsync(data);
        }

        public async Task OpenExternalAsync(Window window)
        {
            if (string.IsNullOrWhiteSpace(_filePath)) return;
            var item = await window.StorageProvider.TryGetFileFromPathAsync(_filePath);
            if (item is not null) await window.Launcher.LaunchFileAsync(item);
        }

        private void ApplyTransform()
        {
            _scale.ScaleX = _zoom; _scale.ScaleY = _zoom;
            _rotation.Angle = _rotationDegrees;
            _translation.X = _pan.X; _translation.Y = _pan.Y;
        }

        private void OnPointerWheel(object? sender, PointerWheelEventArgs args)
        {
            ZoomBy(args.Delta.Y > 0 ? 1.1 : 1 / 1.1);
            args.Handled = true;
        }

        private void OnPointerPressed(object? sender, PointerPressedEventArgs args)
        {
            if (args.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                _dragOrigin = args.GetPosition(this);
                args.Handled = true;
            }
        }

        private void OnPointerMoved(object? sender, PointerEventArgs args)
        {
            if (_dragOrigin is not { } origin || !args.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            var current = args.GetPosition(this);
            _pan += current - origin; _dragOrigin = current;
            ApplyTransform(); args.Handled = true;
        }

        private void OnPointerReleased(object? sender, PointerReleasedEventArgs args) => _dragOrigin = null;

        private static IBrush CheckerboardBrush() => new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Colors.White, 0), new GradientStop(Color.FromRgb(224, 224, 224), .25),
                new GradientStop(Colors.White, .5), new GradientStop(Color.FromRgb(224, 224, 224), .75),
                new GradientStop(Colors.White, 1),
            },
        };
    }
}
