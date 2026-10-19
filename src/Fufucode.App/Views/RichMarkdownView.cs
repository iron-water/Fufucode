using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using LiveMarkdown.Avalonia;

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
    private bool _diagramPinned;

    public RichMarkdownView()
    {
        // 首次构造时注册数学/Mermaid/SVG 渲染能力；重复注册无副作用。
        _ = EnvironmentReady.Value;

        // 渲染器自身不创建 builder：必须由调用方提供一个 ObservableStringBuilder，
        // 并通过 UpdateProducer 接管，否则 MarkdownBuilder 为 null、渲染器不产出任何内容。
        _renderer.UpdateProducer = new MarkdownUpdateProducer { MarkdownBuilder = _builder };
        Content = _renderer;

        // Mermaid 图表自带的平移/缩放会抢滚轮：页面滚动经过图表时会被误缩放。
        // 这里在隧道阶段拦截：只有先在图表上按过鼠标（pinned）才放行缩放。
        AddHandler(PointerWheelChangedEvent, OnRichWheel, RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, OnRichPressed, RoutingStrategies.Tunnel);
        PointerExited += (_, _) => _diagramPinned = false;

        AttachedToVisualTree += (_, _) =>
        {
            _attached = true;
            RenderCurrent();
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

    private void OnRichPressed(object? sender, PointerPressedEventArgs e) =>
        _diagramPinned = IsWithinDiagram(e.Source as Visual);

    private void OnRichWheel(object? sender, PointerWheelEventArgs e)
    {
        // 先钉住再缩放；未钉住时把滚轮交还给时间线，避免滑动页面时误缩放图表。
        if (IsWithinDiagram(e.Source as Visual) && !_diagramPinned)
        {
            e.Handled = true;
        }
    }

    private static bool IsWithinDiagram(Visual? source)
    {
        for (var current = source; current is not null; current = current.GetVisualParent())
        {
            if (current.GetType().Name == "PanAndZoom") return true;
        }
        return false;
    }

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
            return;
        }

        builder.Clear();
        builder.Append(_markdown);
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
