using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
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

    public HighlightedCodeView()
    {
        Content = _block;
        // 自带标题栏（语言名 + 换行/复制）与聊天页卡片头部重复，挂载后隐藏，
        // 复制与折叠仍由聊天页自己的卡片提供。模板若变更，最多表现为多一条标题栏。
        _block.Loaded += (_, _) => HideBuiltInHeader();
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
