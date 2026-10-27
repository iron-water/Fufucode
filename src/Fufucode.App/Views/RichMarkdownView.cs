using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using LiveMarkdown.Avalonia;
using TextMateSharp.Grammars;
using Markdig;

namespace Fufucode.App.Views;

/// <summary>
/// 富文本消息渲染器：Markdown（含表格）、LaTeX 数学（$$…$$ / $…$）、Mermaid 图表、图片与 SVG。
///
/// 以 LiveMarkdown 的 <see cref="MarkdownRenderer"/> 为内核，避免手写解析器反复重建控件——
/// 手写版本每次流式增量都清空重建，是输出闪烁的根因；这里由渲染器自己做增量更新。
/// 外观通过主题资源注入，保持与聊天区其余文字一致。
/// </summary>
public sealed class RichMarkdownView : ContentControl
{
    private static readonly Lazy<bool> EnvironmentReady = new(RegisterRendering);
    private readonly MarkdownRenderer _renderer = new();
    private readonly ObservableStringBuilder _builder = new();
    private string _markdown = string.Empty;
    private bool _attached;

    public RichMarkdownView()
    {
        // 首次构造时注册数学/Mermaid/SVG 渲染能力；重复注册无副作用。
        _ = EnvironmentReady.Value;

        // 渲染器自身不创建 builder：必须由调用方提供一个 ObservableStringBuilder，
        // 并通过 UpdateProducer 接管，否则 MarkdownBuilder 为 null、渲染器不产出任何内容。
        _renderer.UpdateProducer = new MarkdownUpdateProducer { MarkdownBuilder = _builder };
        HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
        _renderer.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
        Content = _renderer;
        ApplyCodeTheme();

        // 图表交互统一在 ApplyDiagramGuard 里禁用：LiveMarkdown 的 PanAndZoom
        // 会抢滚轮，点击还会触发栈溢出崩溃（"无法创建新的堆栈防护页面"）。
        AddHandler(PointerPressedEvent, OnRichPressed, RoutingStrategies.Tunnel);

        // 链接：优先在内置浏览器打开，失败再交给系统默认浏览器。
        _renderer.LinkClick += (_, e) => OpenLink(e.HRef);

        AttachedToVisualTree += (_, _) =>
        {
            _attached = true;
            RenderCurrent();
        };
        // 设置页改强调色 / 切换明暗主题会替换窗口资源，行内代码颜色要跟着重刷。
        ResourcesChanged += (_, _) => ScheduleDiagramGuard();
        ActualThemeVariantChanged += (_, _) =>
        {
            ApplyCodeTheme();
            ScheduleDiagramGuard();
        };
    }

    /// <summary>Markdown 源文本；赋值即交给渲染器做增量更新。</summary>
    public static readonly StyledProperty<string> MarkdownProperty =
        AvaloniaProperty.Register<RichMarkdownView, string>(nameof(Markdown), string.Empty);

    /// <summary>图片相对路径的基准目录（当前项目根）；为空时只解析绝对路径与网络地址。</summary>
    public static readonly StyledProperty<string?> ImageBasePathProperty =
        AvaloniaProperty.Register<RichMarkdownView, string?>(nameof(ImageBasePath));

    public string Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    public string? ImageBasePath
    {
        get => GetValue(ImageBasePathProperty);
        set => SetValue(ImageBasePathProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MarkdownProperty)
        {
            _markdown = change.NewValue as string ?? string.Empty;
            RenderCurrent();
        }
        else if (change.Property == ImageBasePathProperty)
        {
            if (_attached) _renderer.ImageBasePath = change.NewValue as string;
        }
    }

    private void OnRichPressed(object? sender, PointerPressedEventArgs e)
    {
        // 点图片放大：消息里的图通常很小，需要看清时不必另存。
        if (FindImage(e.Source as Visual) is { Source: { } imageSource })
        {
            ShowImagePreview(imageSource);
            e.Handled = true;
        }
    }

    private static Image? FindImage(Visual? source)
    {
        for (var current = source; current is not null; current = current.GetVisualParent())
        {
            if (current is Image image) return image;
        }
        return null;
    }

    private void ShowImagePreview(IImage source) => ImageLightbox.Show(this, source);

    private void OpenLink(Uri? uri)
    {
        if (uri is null) return;
        if (uri.Scheme is "http" or "https")
        {
            if (TopLevel.GetTopLevel(this) is MainWindow window && window.OpenUrlInBrowser(uri.ToString())) return;
            if (TopLevel.GetTopLevel(this)?.Launcher is { } launcher) { _ = launcher.LaunchUriAsync(uri); return; }
        }
        if (TopLevel.GetTopLevel(this)?.Launcher is { } fallback) _ = fallback.LaunchUriAsync(uri);
    }

    /// <summary>
    /// 代码块配色跟随窗口明暗：浅色主题用浅色 TextMate 主题，避免整块深色代码
    /// 在浅色页面里像一块补丁（深色主题保持 DarkPlus）。
    /// </summary>
    private void ApplyCodeTheme()
    {
        _renderer.CodeBlockColorTheme = ActualThemeVariant == ThemeVariant.Light
            ? ThemeName.LightPlus
            : ThemeName.DarkPlus;
    }

    /// <summary>
    /// 禁用图表控件的指针交互。
    ///
    /// PanAndZoom 会抢走滚轮，导致鼠标停在图表上时页面滑不动；点击它还会触发
    /// 栈溢出崩溃（Windows 报"无法创建新的堆栈防护页面"）。
    /// 图表本身仍然正常显示，只是不再支持平移/缩放。
    /// </summary>
    private void ApplyDiagramGuard()
    {
        foreach (var visual in this.GetVisualDescendants())
        {
            if (visual.GetType().Name != "PanAndZoom") continue;
            if (visual is InputElement element) element.IsHitTestVisible = false;
        }
    }

    /// <summary>
    /// 行内代码（反引号内容）的配色。库默认给的是偏暗的金色，这里统一改成
    /// 可配置的强调色 AppEmphasis，跟随设置页「强调色」。
    /// </summary>
    private void ApplyInlineCodeColor()
    {
        var brush = ResolveEmphasisBrush();
        if (brush is null) return;
        foreach (var block in this.GetVisualDescendants().OfType<TextBlock>())
        {
            if (block.Inlines is { } inlines) Paint(inlines, brush);
        }

        static void Paint(InlineCollection inlines, IBrush brush)
        {
            foreach (var inline in inlines)
            {
                switch (inline)
                {
                    case CodeInline code:
                        code.Foreground = brush;
                        break;
                    case Bold bold:
                        Paint(bold.Inlines, brush);
                        break;
                    case Italic italic:
                        Paint(italic.Inlines, brush);
                        break;
                    case Span span:
                        Paint(span.Inlines, brush);
                        break;
                }
            }
        }
    }

    private IBrush? ResolveEmphasisBrush()
    {
        if (this.TryGetResource("AppEmphasis", ActualThemeVariant, out var value) && value is IBrush brush)
            return brush;
        var app = Application.Current;
        return app is not null && app.TryGetResource("AppEmphasis", app.ActualThemeVariant, out var appValue) &&
               appValue is IBrush appBrush
            ? appBrush
            : null;
    }

    /// <summary>渲染器增量更新后统一收尾：图表守卫 + 行内代码配色。改色/换肤后也要重刷。</summary>
    private void ScheduleDiagramGuard() =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            ApplyDiagramGuard();
            ApplyInlineCodeColor();
        }, Avalonia.Threading.DispatcherPriority.Loaded);

    /// <summary>把当前 Markdown 交给渲染器。属性可能在构造完成前被赋值，因此要能安全地延后渲染。</summary>
    private void RenderCurrent()
    {
        if (!_attached) return;

        var builder = _builder;
        var current = builder.ToString();
        if (string.Equals(current, _markdown, StringComparison.Ordinal)) return;

        // 增量追加时只补差异部分，避免整段重建。
        if (_markdown.StartsWith(current, StringComparison.Ordinal))
        {
            builder.Append(_markdown[current.Length..]);
            ScheduleDiagramGuard();
            return;
        }

        builder.Clear();
        builder.Append(_markdown);
        ScheduleDiagramGuard();
    }

    /// <summary>
    /// 注册渲染扩展。Mermaid 与数学扩展通过管道构建器挂载；
    /// SVG 解码器注册到图片加载器，使 ![](x.svg) 能渲染。
    /// </summary>
    private static bool RegisterRendering()
    {
        try
        {
            // 节点注册与管道配置都必须在首次解析前完成；静态 Lazy 在创建渲染器之前触发。
            MarkdownNode.Register<MathInlineNode>();
            MarkdownNode.Register<MathBlockNode>();
            MarkdownNode.Register<MermaidBlockNode>();

            MarkdownRenderer.ConfigurePipeline += pipeline =>
            {
                // 裸 URL 也渲染成可点链接；表格/任务列表/删除线等由默认管道提供。
                pipeline.UseAutoLinks();
                pipeline.UseMermaid();
                // \( \) 与 \[ \] 也支持；$ 与 $$ 由默认解析器提供。
                pipeline.UseExtendedMathematics();
            };
            RegisterSvg();
            return true;
        }
        catch (Exception error)
        {
            System.Diagnostics.Debug.WriteLine("Rich markdown extensions failed: " + error.Message);
            return false;
        }
    }

    private static void RegisterSvg()
    {
        AsyncImageLoader.DefaultDecoders =
        [
            LiveMarkdown.Avalonia.SvgImageDecoder.Shared,
            DefaultBitmapDecoder.Shared,
        ];
    }
}
