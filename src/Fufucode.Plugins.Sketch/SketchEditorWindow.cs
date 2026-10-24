using System;
using System.IO;
using System.Threading.Tasks;
using Path = System.IO.Path;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Markup.Xaml.MarkupExtensions;

namespace Fufucode.Plugins.Sketch;

public sealed class SketchEditorWindow : Window
{
    private readonly SketchCanvas _canvas = new();
    private readonly TextBlock _toolLabel = new();
    private readonly Border _selectedColor = new();
    private readonly Slider _sizeSlider;
    private SketchTool _tool = SketchTool.Pen;
    private Color _color = Colors.White;
    private bool _confirming;

    public string? ExportedPath { get; private set; }

    public SketchEditorWindow()
    {
        Title = "绘制草图";
        Width = 960;
        Height = 680;
        MinWidth = 720;
        MinHeight = 500;
        CanResize = true;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#202020"));

        _canvas.Tool = _tool;
        _canvas.InkColor = _color;
        _canvas.TextRequested += RequestTextAsync;

        _sizeSlider = new Slider { Minimum = 2, Maximum = 18, Value = 4, Width = 120, VerticalAlignment = VerticalAlignment.Center };
        _sizeSlider.PropertyChanged += (_, args) =>
        {
            if (args.Property == Slider.ValueProperty)
            {
                _canvas.StrokeWidth = _sizeSlider.Value;
                UpdateSizePreview();
            }
        };

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Background = new SolidColorBrush(Color.Parse("#202020")) };
        root.Children.Add(BuildTopBar());
        Grid.SetRow(_canvas, 1);
        root.Children.Add(_canvas);
        var bottom = BuildBottomBar();
        Grid.SetRow(bottom, 2);
        root.Children.Add(bottom);
        Content = root;
        UpdateSizePreview();
    }

    private Control BuildTopBar()
    {
        var bar = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(18, 14, 18, 8) };
        var close = MakeButton("×", "取消绘制", 34);
        close.FontSize = 27;
        close.Click += (_, _) => Close(false);
        bar.Children.Add(close);

        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center };
        AddTool(tools, "⌁", SketchTool.Pointer, "选择");
        AddTool(tools, "〰", SketchTool.Pen, "画笔");
        AddTool(tools, "T", SketchTool.Text, "文字");
        AddTool(tools, "□", SketchTool.Rectangle, "矩形");
        AddTool(tools, "⌫", SketchTool.Eraser, "橡皮");
        Grid.SetColumn(tools, 1);
        bar.Children.Add(tools);

        var history = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Right };
        var undo = MakeButton("↶", "撤销", 32);
        undo.Click += (_, _) => { _canvas.Undo(); };
        var redo = MakeButton("↷", "重做", 32);
        redo.Click += (_, _) => { _canvas.Redo(); };
        history.Children.Add(undo);
        history.Children.Add(redo);
        Grid.SetColumn(history, 2);
        bar.Children.Add(history);
        return bar;
    }

    private Control BuildBottomBar()
    {
        var bar = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(18, 8, 18, 18) };
        var size = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        _toolLabel.Text = "画笔";
        _toolLabel.Foreground = new SolidColorBrush(Color.Parse("#BDBDBD"));
        _toolLabel.FontSize = 12;
        size.Children.Add(_toolLabel);
        size.Children.Add(_sizeSlider);
        var sizeDot = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(9), Background = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
        size.Children.Add(sizeDot);
        bar.Children.Add(size);

        var palette = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var color in new[] { "#FFFFFF", "#9CA3AF", "#A8550E", "#EF2B31", "#FF7518", "#FFB000", "#18A957", "#18A39B", "#14B8D4", "#3478E5", "#5145E5", "#9135DF", "#D6297C" })
            AddColor(palette, Color.Parse(color));
        Grid.SetColumn(palette, 1);
        bar.Children.Add(palette);

        var confirm = MakeButton("✓", "确认草图", 48);
        confirm.FontSize = 22;
        confirm.Background = new SolidColorBrush(Color.Parse("#707070"));
        confirm.Click += async (_, _) => await ConfirmAsync();
        Grid.SetColumn(confirm, 2);
        bar.Children.Add(confirm);
        return bar;
    }

    private void AddTool(Panel panel, string glyph, SketchTool tool, string label)
    {
        var button = MakeButton(glyph, label, 42);
        button.FontSize = tool == SketchTool.Text ? 19 : 22;
        button.Classes.Add("iconButton");
        button.Click += (_, _) => SelectTool(tool);
        panel.Children.Add(button);
    }

    private void AddColor(Panel panel, Color color)
    {
        var button = new Button { Width = 32, Height = 32, Padding = new Thickness(0), Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
        var dot = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Background = new SolidColorBrush(color), BorderBrush = new SolidColorBrush(Color.Parse("#404040")), BorderThickness = new Thickness(1) };
        button.Content = dot;
        button.Click += (_, _) =>
        {
            _color = color;
            _canvas.InkColor = color;
            _selectedColor.Background = new SolidColorBrush(color);
        };
        panel.Children.Add(button);
    }

    private void SelectTool(SketchTool tool)
    {
        _tool = tool;
        _canvas.Tool = tool;
        _toolLabel.Text = tool switch
        {
            SketchTool.Pointer => "选择",
            SketchTool.Pen => "画笔",
            SketchTool.Text => "文字",
            SketchTool.Rectangle => "矩形",
            SketchTool.Eraser => "橡皮",
            _ => "画笔",
        };
    }

    private void UpdateSizePreview()
    {
        if (_sizeSlider is null) return;
        _canvas.StrokeWidth = _sizeSlider.Value;
    }

    private async Task RequestTextAsync(Point point)
    {
        var input = new TextBox { Width = 300, PlaceholderText = "输入要放到草图里的文字…" };
        var cancel = new Button { Content = "取消", IsCancel = true };
        var add = new Button { Content = "添加", IsDefault = true };
        var dialog = new Window
        {
            Title = "添加文字",
            Width = 390,
            Height = 170,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Spacing = 14,
                Margin = new Thickness(18),
                Children =
                {
                    input,
                    new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { cancel, add } },
                },
            },
        };
        cancel.Click += (_, _) => dialog.Close(false);
        add.Click += (_, _) => dialog.Close(true);
        if (await dialog.ShowDialog<bool>(this)) _canvas.AddText(point, input.Text ?? string.Empty);
    }

    private async Task ConfirmAsync()
    {
        if (_confirming) return;
        _confirming = true;
        try
        {
            var directory = Path.Combine(Path.GetTempPath(), "fufucode", "sketch");
            Directory.CreateDirectory(directory);
            ExportedPath = Path.Combine(directory, $"sketch-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.png");
            var pixelSize = new PixelSize(Math.Max(1, (int)Math.Ceiling(_canvas.Bounds.Width * 2)), Math.Max(1, (int)Math.Ceiling(_canvas.Bounds.Height * 2)));
            using var bitmap = new RenderTargetBitmap(pixelSize, new Vector(192, 192));
            bitmap.Render(_canvas);
            await using var stream = File.Create(ExportedPath);
            bitmap.Save(stream, PngBitmapEncoderOptions.Default);
            Close(true);
        }
        catch
        {
            ExportedPath = null;
            _confirming = false;
        }
    }

    private static Button MakeButton(string content, string tooltip, double width)
    {
        var button = new Button
        {
            Content = content,
            Width = width,
            Height = 36,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.Parse("#BDBDBD")),
        };
        ToolTip.SetTip(button, tooltip);
        return button;
    }
}
