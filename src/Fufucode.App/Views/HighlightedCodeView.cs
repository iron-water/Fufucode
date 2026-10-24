using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.VisualTree;
using LiveMarkdown.Avalonia;
using TextMateSharp.Grammars;

namespace Fufucode.App.Views;

/// <summary>
/// 代码块的语法高亮视图。
///
/// 复用 LiveMarkdown 的 <see cref="CodeBlock"/>（内部走 TextMateSharp 语法与主题），
/// 不再另引高亮库；外层卡片、复制与折叠仍由聊天页自己的模板负责。
/// 语言名按常见别名归一化，模型输出 <c>cs</c>/<c>ts</c>/<c>bash</c> 也能命中语法。
/// </summary>
public sealed class HighlightedCodeView : ContentControl
{
    private readonly CodeBlock _block = new()
    {
        AutoSyntaxHighlight = true,
        IsCodeWrapped = false,
    };

    public static readonly StyledProperty<string> CodeProperty =
        AvaloniaProperty.Register<HighlightedCodeView, string>(nameof(Code), string.Empty);

    public static readonly StyledProperty<string> LanguageProperty =
        AvaloniaProperty.Register<HighlightedCodeView, string>(nameof(Language), string.Empty);

    /// <summary>是否完整展开；收起时限制为 <see cref="CollapsedHeight"/> 并允许内部滚动。</summary>
    public static readonly StyledProperty<bool> IsExpandedProperty =
        AvaloniaProperty.Register<HighlightedCodeView, bool>(nameof(IsExpanded), true);

    /// <summary>收起状态下的最大高度。</summary>
    public static readonly StyledProperty<double> CollapsedHeightProperty =
        AvaloniaProperty.Register<HighlightedCodeView, double>(nameof(CollapsedHeight), 180d);

    private bool _heightAttached;
    private bool _animatingHeight;

    public bool IsExpanded
    {
        get => GetValue(IsExpandedProperty);
        set => SetValue(IsExpandedProperty, value);
    }

    public double CollapsedHeight
    {
        get => GetValue(CollapsedHeightProperty);
        set => SetValue(CollapsedHeightProperty, value);
    }

    public string Code
    {
        get => GetValue(CodeProperty);
        set => SetValue(CodeProperty, value);
    }

    public string Language
    {
        get => GetValue(LanguageProperty);
        set => SetValue(LanguageProperty, value);
    }

    private ScrollViewer? _innerScroll;

    public HighlightedCodeView()
    {
        Content = _block;

        Transitions = new Avalonia.Animation.Transitions
        {
            new Avalonia.Animation.DoubleTransition
            {
                Property = MaxHeightProperty,
                Duration = Motion.StandardDuration,
                Easing = Motion.FluentEasing,
            },
        };
        AttachedToVisualTree += (_, _) =>
        {
            _heightAttached = true;
            ApplyHeight(animate: false);
        };
        SizeChanged += (_, _) =>
        {
            // 展开且动画结束后才解除限制；动画进行中改 MaxHeight 会把过渡打断。
            if (_heightAttached && IsExpanded && !_animatingHeight) MaxHeight = double.PositiveInfinity;
        };
        // 自带标题栏（语言名 + 换行/复制）与聊天页卡片头部重复，挂载后隐藏，
        // 复制与折叠仍由聊天页自己的卡片提供。模板若变更，最多表现为多一条标题栏。
        _block.Loaded += (_, _) => { HideBuiltInHeader(); HookInnerScroll(); };

        // 代码块内部自带 ScrollViewer；鼠标经过时它会抢走滚轮，导致滑动页面像在代码块里跳。
        // 与 Mermaid 一致：先在代码块上按一下，才允许内部滚动。
        AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        PointerExited += (_, _) => ReleaseInnerScroll();
        ApplyTheme();
        ActualThemeVariantChanged += (_, _) => ApplyTheme();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == CodeProperty)
        {
            _block.Code = change.NewValue as string ?? string.Empty;
        }
        else if (change.Property == LanguageProperty)
        {
            _block.Language = Normalize(change.NewValue as string);
        }
        else if (change.Property == IsExpandedProperty || change.Property == CollapsedHeightProperty)
        {
            ApplyHeight(animate: _heightAttached && !Motion.Reduced);
        }
    }

    /// <summary>在收起的预览高度与完整内容高度之间做过渡。</summary>
    private void ApplyHeight(bool animate)
    {
        if (!_heightAttached) return;

        if (!animate)
        {
            MaxHeight = IsExpanded ? double.PositiveInfinity : CollapsedHeight;
            return;
        }

        _animatingHeight = true;
        if (IsExpanded)
        {
            var target = Math.Max(CollapsedHeight, MeasureContentHeight());
            MaxHeight = CollapsedHeight;
            Avalonia.Threading.Dispatcher.UIThread.Post(
                () => MaxHeight = target,
                Avalonia.Threading.DispatcherPriority.Background);
            Avalonia.Threading.DispatcherTimer.RunOnce(
                () => { _animatingHeight = false; if (IsExpanded) MaxHeight = double.PositiveInfinity; },
                Motion.StandardDuration + TimeSpan.FromMilliseconds(30));
            return;
        }

        var current = Math.Max(Bounds.Height, MeasureContentHeight());
        MaxHeight = current;
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () => MaxHeight = CollapsedHeight,
            Avalonia.Threading.DispatcherPriority.Background);
        Avalonia.Threading.DispatcherTimer.RunOnce(
            () => _animatingHeight = false,
            Motion.StandardDuration + TimeSpan.FromMilliseconds(30));
    }

    private double MeasureContentHeight()
    {
        var width = Bounds.Width > 0 ? Bounds.Width : double.PositiveInfinity;
        _block.Measure(new Size(width, double.PositiveInfinity));
        return Math.Max(0, _block.DesiredSize.Height);
    }

    private void HookInnerScroll()
    {
        if (_innerScroll is not null) return;
        _innerScroll = _block.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        ReleaseInnerScroll();
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_innerScroll is null) return;
        _innerScroll.IsHitTestVisible = true;
    }

    private void ReleaseInnerScroll()
    {
        if (_innerScroll is not null) _innerScroll.IsHitTestVisible = false;
    }

    private void HideBuiltInHeader()
    {
        if (_block.GetVisualChildren().OfType<StackPanel>().FirstOrDefault() is not { } root) return;
        if (root.Children.OfType<Border>().FirstOrDefault() is { } header) header.IsVisible = false;
    }

    private void ApplyTheme() =>
        _block.ColorTheme = ActualThemeVariant == ThemeVariant.Light
            ? ThemeName.LightPlus
            : ThemeName.DarkPlus;

    /// <summary>把围栏里常见的短写/别名映射到 TextMate 的语法名。</summary>
    private static string Normalize(string? language)
    {
        var name = (language ?? string.Empty).Trim().ToLowerInvariant();
        // 有些模型写成 ```csharp title=... 或 ```js,ts，只取第一段。
        var split = name.IndexOfAny([' ', ',', '{']);
        if (split > 0) name = name[..split];

        return name switch
        {
            "cs" or "c#" => "csharp",
            "ts" => "typescript",
            "js" => "javascript",
            "jsx" => "javascriptreact",
            "tsx" => "typescriptreact",
            "py" => "python",
            "rb" => "ruby",
            "sh" or "shell" or "zsh" => "shellscript",
            "ps1" or "powershell" => "powershell",
            "yml" => "yaml",
            "md" => "markdown",
            "c++" or "cc" or "hpp" => "cpp",
            "c" or "h" => "c",
            "rs" => "rust",
            "kt" => "kotlin",
            "golang" => "go",
            "html" or "htm" => "html",
            "jsonc" => "json",
            _ => name,
        };
    }
}
