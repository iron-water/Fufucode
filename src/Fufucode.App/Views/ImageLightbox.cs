using System;
using System.Collections.Generic;
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
using Fufucode.App.Features.FilePreview;

namespace Fufucode.App.Views;

/// <summary>Foreground image viewer sharing the same pixel-accurate viewport as Files.</summary>
internal static class ImageLightbox
{
    // Preserve the existing call shape. Borrowed sources remain owned by the caller.
    public static void Show(Visual anchor, IImage source, string? filePath = null) =>
        ShowCanvas(anchor, new ImagePreviewCanvas(source), filePath);

    internal static void Show(Visual anchor, ImagePreviewCanvas source, string? filePath = null) =>
        ShowCanvas(anchor, source.CreateSharedCanvas(), filePath);

    private static void ShowCanvas(Visual anchor, ImagePreviewCanvas canvas, string? filePath)
    {
        try { ShowWindow(anchor, new ImageLightboxWindow(canvas, filePath)); }
        catch { canvas.Dispose(); throw; }
    }

    private static void ShowWindow(Visual anchor, ImageLightboxWindow window)
    {
        if (TopLevel.GetTopLevel(anchor) is Window owner) window.Show(owner);
        else window.Show();
    }
}

/// <summary>Named controls and explicit actions/state allow focused headless and native interaction checks.</summary>
public sealed class ImageLightboxWindow : Window
{
    private readonly TextBlock _status;
    private readonly Button _copy;
    private readonly Button _openExternal;
    private readonly Button _fullscreen;
    private WindowState _previousWindowState = WindowState.Normal;
    private bool _closed;

    public ImageLightboxWindow(ImagePreviewCanvas canvas, string? filePath = null)
    {
        Canvas = canvas;
        FilePath = filePath;
        Name = "ImageLightboxWindow";
        Background = Brushes.Black;
        Foreground = Brushes.White;
        Width = 1100;
        Height = 800;
        MinWidth = 320;
        MinHeight = 240;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowState = WindowState.FullScreen;
        Title = string.IsNullOrWhiteSpace(filePath) ? "图片预览" : Path.GetFileName(filePath);

        var toolbar = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(8),
        };
        Button Action(string name, string label, Action action, string tip)
        {
            var button = new Button
            {
                Name = name,
                Content = label,
                Margin = new Thickness(3),
                Padding = new Thickness(10, 5),
            };
            Avalonia.Automation.AutomationProperties.SetHelpText(button, tip);
            button.Click += (_, _) => action();
            toolbar.Children.Add(button);
            return button;
        }

        Action("ImageZoomOut", "−", () => Canvas.ZoomBy(0.8), "缩小（−）");
        Action("ImageZoomIn", "+", () => Canvas.ZoomBy(1.25), "放大（+）");
        Action("ImageFit", "适配", Canvas.SetFitMode, "适配窗口（F）");
        Action("ImageOriginal", "100%", Canvas.SetOriginalSize, "一个图片像素对应一个屏幕像素（1）");
        Action("ImageRotateLeft", "左转", () => Canvas.RotateBy(-90), "向左旋转 90°");
        Action("ImageRotateRight", "右转", () => Canvas.RotateBy(90), "向右旋转 90°（R）");
        Action("ImageCheckerboard", "棋盘", Canvas.ToggleCheckerboard, "切换透明背景棋盘（B）");
        _copy = Action("ImageCopy", "复制", () => _ = CopyAsync(), "复制原图（Ctrl+C），不包含当前缩放或旋转");
        _openExternal = Action("ImageOpenExternal", "外部打开", () => _ = OpenExternalAsync(), "使用系统默认应用打开原文件");
        _fullscreen = Action("ImageFullScreen", "退出全屏", ToggleFullScreen, "切换全屏（F11）；Escape 关闭预览");
        Action("ImageClose", "关闭", Close, "关闭预览（Escape）");
        _copy.IsEnabled = Canvas.Source is Bitmap;
        _openExternal.IsEnabled = CanOpenExternal;
        if (!CanOpenExternal)
            Avalonia.Automation.AutomationProperties.SetHelpText(_openExternal, string.IsNullOrWhiteSpace(filePath) ? "图片没有本地文件路径。" : "原文件不存在。");

        _status = new TextBlock
        {
            Name = "ImagePreviewStatus",
            Foreground = Brushes.White,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(12, 0, 12, 10),
        };
        var footer = new StackPanel { Children = { toolbar, _status } };
        var layout = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        Canvas.Name = "ImagePreviewCanvas";
        Grid.SetRow(footer, 1);
        layout.Children.Add(Canvas);
        layout.Children.Add(footer);
        Content = layout;
        Canvas.StateChanged += OnCanvasStateChanged;
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        Opened += (_, _) => { Canvas.Focus(); RefreshStatus(); };
        Closed += (_, _) =>
        {
            _closed = true;
            Canvas.StateChanged -= OnCanvasStateChanged;
            Canvas.Dispose();
        };
        RefreshStatus();
    }

    public ImagePreviewCanvas Canvas { get; }
    public string? FilePath { get; }
    public bool CanOpenExternal => !string.IsNullOrWhiteSpace(FilePath) && File.Exists(FilePath);
    public bool IsFullScreen => WindowState == WindowState.FullScreen;
    public string? LastError { get; private set; }
    public string StatusText => _status.Text ?? string.Empty;

    public void ToggleFullScreen()
    {
        if (IsFullScreen) WindowState = _previousWindowState;
        else
        {
            _previousWindowState = WindowState;
            WindowState = WindowState.FullScreen;
        }
        _fullscreen.Content = IsFullScreen ? "退出全屏" : "全屏";
    }

    public async Task<bool> CopyAsync()
    {
        if (_closed) return false;
        if (Canvas.Source is not Bitmap)
            return ReportError("当前图片类型不支持直接复制。请使用外部应用复制。");
        if (Clipboard is not { } clipboard) return ReportError("系统剪贴板不可用。");

        // The clipboard owns this lease until its data is replaced, so closing either
        // viewport cannot invalidate a lazily requested bitmap on the clipboard.
        var data = new ImageClipboardData(Canvas.RetainSource());
        _copy.IsEnabled = false;
        var transferred = false;
        try
        {
            await clipboard.SetDataAsync(data);
            transferred = true;
            await clipboard.FlushAsync();
            ReportSuccess("已复制原图。");
            return true;
        }
        catch (Exception error)
        {
            if (!transferred) data.Dispose();
            return ReportError($"复制图片失败：{error.Message}");
        }
        finally
        {
            if (!_closed) _copy.IsEnabled = true;
        }
    }

    public async Task<bool> OpenExternalAsync()
    {
        if (_closed) return false;
        if (string.IsNullOrWhiteSpace(FilePath)) return ReportError("图片没有本地文件路径，无法外部打开。");
        if (!File.Exists(FilePath)) return ReportError("原文件不存在，无法外部打开。");
        _openExternal.IsEnabled = false;
        try
        {
            using var item = await StorageProvider.TryGetFileFromPathAsync(FilePath);
            if (_closed) return false;
            if (item is null) return ReportError("无法访问原文件。");
            if (!await Launcher.LaunchFileAsync(item)) return ReportError("系统未能打开图片，请检查默认应用。");
            ReportSuccess("已请求系统默认应用打开原文件。");
            return true;
        }
        catch (Exception error)
        {
            return ReportError($"外部打开失败：{error.Message}");
        }
        finally
        {
            if (!_closed) _openExternal.IsEnabled = CanOpenExternal;
        }
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key == Key.Escape) { Close(); args.Handled = true; return; }
        if (args.Key == Key.F11) { ToggleFullScreen(); args.Handled = true; return; }
        if (args.Key == Key.C && (args.KeyModifiers.HasFlag(KeyModifiers.Control) || args.KeyModifiers.HasFlag(KeyModifiers.Meta)))
        {
            _ = CopyAsync();
            args.Handled = true;
            return;
        }
        if ((args.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta | KeyModifiers.Alt)) != 0) return;
        switch (args.Key)
        {
            case Key.Add:
            case Key.OemPlus: Canvas.ZoomBy(1.25); break;
            case Key.Subtract:
            case Key.OemMinus: Canvas.ZoomBy(0.8); break;
            case Key.F: Canvas.SetFitMode(); break;
            case Key.D1:
            case Key.NumPad1: Canvas.SetOriginalSize(); break;
            case Key.R: Canvas.RotateBy(args.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -90 : 90); break;
            case Key.B: Canvas.ToggleCheckerboard(); break;
            case Key.Left: Canvas.PanBy(new Vector(-32, 0)); break;
            case Key.Right: Canvas.PanBy(new Vector(32, 0)); break;
            case Key.Up: Canvas.PanBy(new Vector(0, -32)); break;
            case Key.Down: Canvas.PanBy(new Vector(0, 32)); break;
            default: return;
        }
        args.Handled = true;
    }

    private void OnCanvasStateChanged(object? sender, EventArgs e)
    {
        if (_closed) return;
        LastError = null;
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        var size = Canvas.OriginalSize;
        _status.Text = $"{size.Width} × {size.Height}  ·  {Canvas.Zoom:P0}  ·  {Canvas.RotationDegrees}°"
            + (Canvas.IsFitMode ? "  ·  适配" : string.Empty);
        _status.Foreground = Brushes.White;
    }

    private bool ReportError(string message)
    {
        LastError = message;
        if (!_closed)
        {
            _status.Text = message;
            _status.Foreground = Brushes.LightSalmon;
        }
        return false;
    }

    private void ReportSuccess(string message)
    {
        LastError = null;
        if (!_closed) { _status.Text = message; _status.Foreground = Brushes.White; }
    }

    private sealed class ImageClipboardData : IDataTransfer, IAsyncDataTransfer
    {
        private readonly DataTransfer _data = new();
        private ImagePreviewSource? _source;

        public ImageClipboardData(ImagePreviewSource source)
        {
            _source = source;
            _data.Add(DataTransferItem.Create(DataFormat.Bitmap, (Bitmap)source.Image));
        }

        public IReadOnlyList<DataFormat> Formats => _data.Formats;
        IReadOnlyList<IDataTransferItem> IDataTransfer.Items => ((IDataTransfer)_data).Items;
        IReadOnlyList<IAsyncDataTransferItem> IAsyncDataTransfer.Items => ((IAsyncDataTransfer)_data).Items;
        public void Dispose() => System.Threading.Interlocked.Exchange(ref _source, null)?.Release();
    }
}
