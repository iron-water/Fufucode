using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fufucode.App.Abstractions;
using Fufucode.App.Infrastructure;
using Fufucode.App.ViewModels;
using Fufucode.App.Views;
using AbTextBlock = Fufucode.App.Abstractions.TextBlock;
using AvTextBlock = Avalonia.Controls.TextBlock;

namespace Fufucode.App.SelfTest;

/// <summary>
/// Wave5-R4 阶段1：Markdown 行内渲染器的断言，外加真实 ChatPage 模板的端到端截图。
/// </summary>
public static class MarkdownSelfTest
{
    private const string AssistantMarkdown =
        "### 改动说明\n- 修正 **队列条** 的 `clear_queue` 语义\n- 新增 `ToastNotificationService`，*仅失焦时* 提醒\n- 详见 [发布说明](https://example.com/releases)";

    private const string UserMarkdown = "请用 **中文** 回复，构建命令是 `dotnet build Fufucode.sln`";

    public static async Task RunAsync(Action<string> pass, Action<string> fail)
    {
        void Check(bool ok, string label)
        {
            if (ok)
            {
                pass("W5-R4 Markdown：" + label);
            }
            else
            {
                fail("W5-R4 Markdown：" + label);
            }
        }

        var directory = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "tmp", "w5r4"));
        Directory.CreateDirectory(directory);

        var host = new AvTextBlock { FontSize = 14 };
        List<Inline> inlines;
        try
        {
            inlines = MarkdownTextBlock.Parse(host, AssistantMarkdown).ToList();
            Check(true, "解析标题+列表+粗体+行内代码+斜体+链接不抛异常");
        }
        catch (Exception error)
        {
            Check(false, "解析抛异常：" + error.Message);
            return;
        }

        Check(inlines.Count > 1, $"解析生成多段行内内容（{inlines.Count} 段）");

        var rendered = string.Concat(inlines.Select(SegmentText));
        Check(!rendered.Contains("**", StringComparison.Ordinal) &&
              !rendered.Contains('`') &&
              !rendered.Contains("](", StringComparison.Ordinal),
            "星号/反引号/链接语法符不再原样直出");
        Check(rendered.Contains("改动说明", StringComparison.Ordinal) &&
              rendered.Contains("clear_queue", StringComparison.Ordinal) &&
              rendered.Contains("发布说明", StringComparison.Ordinal),
            "解析后文本内容无丢失");
        Check(rendered.Contains("• ", StringComparison.Ordinal), "列表项渲染项目符号");

        Check(inlines.OfType<Run>().Any(run =>
                  run.FontFamily.ToString().Contains("Cascadia", StringComparison.OrdinalIgnoreCase)),
              "行内代码段带等宽 FontFamily 属性");
        Check(inlines.OfType<Run>().Any(run => run.Background is not null),
              "行内代码段带浅背景（取自 AppCodeBackground 令牌）");
        Check(inlines.OfType<Bold>().Any(), "粗体生成 Bold 行内元素");
        Check(inlines.OfType<Italic>().Any(), "斜体生成 Italic 行内元素");

        var h1 = FirstHeadingRun(host, "# 一级标题");
        var h3 = FirstHeadingRun(host, "### 三级标题");
        Check(h1 is not null && h1.FontSize >= 20,
              "一级标题字号 20 且加粗（实测 " + (h1?.FontSize ?? 0).ToString("F1") + "）");
        Check(h1 is not null && h3 is not null && h1.FontSize > h3.FontSize && h3.FontSize > 14,
              "标题层级字号递减且不小于正文");
        Check(inlines.OfType<InlineUIContainer>().Any(), "链接以可点击行内控件容器承载");

        Check(MarkdownTextBlock.IsSafeUrl("https://example.com") && MarkdownTextBlock.IsSafeUrl("http://a.example"),
              "链接协议白名单放行 http/https");
        Check(!MarkdownTextBlock.IsSafeUrl("javascript:alert(1)") &&
              !MarkdownTextBlock.IsSafeUrl("file:///C:/Windows/system32") &&
              !MarkdownTextBlock.IsSafeUrl("data:text/html,placeholder") &&
              !MarkdownTextBlock.IsSafeUrl(string.Empty),
              "链接协议白名单拒绝 javascript/file/data");

        var fence = MarkdownTextBlock.Parse(host, "```csharp\nvar x = 1;\n```").ToList();
        var fenceText = string.Concat(fence.OfType<Run>().Select(run => run.Text));
        Check(fenceText.Contains("var x = 1;", StringComparison.Ordinal) &&
              !fenceText.Contains("```", StringComparison.Ordinal),
              "围栏代码块保留内容并去掉围栏标记");

        var snake = string.Concat(MarkdownTextBlock.Parse(host, "snake_case_name 与 参数_名").Select(SegmentText));
        Check(snake.Contains("snake_case_name", StringComparison.Ordinal), "snake_case 下划线不被误判为斜体");

        Check(MarkdownTextBlock.Parse(host, null).Count == 0 &&
              MarkdownTextBlock.Parse(host, string.Empty).Count == 0,
              "null/空内容解析为空集合不报错");

        var fuzz = new[]
        {
            "**未闭合", "`未闭合", "[链接没写完](http", "**a**b**c**", "___", "```", "\n\n\n",
            "#", "- ", "[]()",
        };
        var fuzzFailed = new List<string>();
        foreach (var sample in fuzz)
        {
            try
            {
                MarkdownTextBlock.Parse(host, sample);
            }
            catch (Exception error)
            {
                fuzzFailed.Add(sample + "→" + error.GetType().Name);
            }
        }

        Check(fuzzFailed.Count == 0, "畸形/截断输入全部不抛异常" + (fuzzFailed.Count == 0 ? string.Empty : "：" + string.Join("; ", fuzzFailed)));

        var applied = new AvTextBlock();
        MarkdownTextBlock.SetMarkdown(applied, "**粗体** 与 `code`");
        MarkdownTextBlock.Apply(applied);
        var appliedCount = applied.Inlines?.Count ?? 0;
        Check(appliedCount >= 2, "附加属性把解析结果写回 TextBlock.Inlines（" + appliedCount + " 段）");

        var window = new MainWindow();
        window.Show();
        try
        {
            window.FindControl<TransitioningContentControl>("PageHost")!.PageTransition = null;
            await window.NavigateForTestingAsync(AppPage.Chat);
            var vm = (MainViewModel)window.DataContext!;
            vm.Timeline.LoadHistory(new[]
            {
                new ChatMessage("md-user", MessageRole.User, null,
                    new MessageBlock[] { new AbTextBlock(UserMarkdown) }, null, true),
                new ChatMessage("md-assistant", MessageRole.Assistant, "fixture-model",
                    new MessageBlock[] { new AbTextBlock(AssistantMarkdown) }, null, true),
            });
            await Ready();

            var renderedBlocks = window.GetVisualDescendants().OfType<AvTextBlock>()
                .Where(block => block.Inlines is not null && block.Inlines.Count > 0)
                .ToList();
            Check(renderedBlocks.Any(block => block.Inlines!.OfType<Run>().Any(IsMonoRun)),
              "真实 ChatPage 模板中消息正文按行内内容渲染，等宽代码段存在");
            var productText = string.Concat(renderedBlocks.SelectMany(block => block.Inlines is null
                    ? Enumerable.Empty<Inline>()
                    : block.Inlines)
                .Select(SegmentText));
            Check(productText.Contains("队列条", StringComparison.Ordinal) &&
                  !productText.Contains("**", StringComparison.Ordinal),
              "真实模板里星号不再直出且内容完整");

            Save("markdown.png");
            foreach (var variant in new[] { Avalonia.Styling.ThemeVariant.Light, Avalonia.Styling.ThemeVariant.Dark })
            {
                var prior = Avalonia.Application.Current!.RequestedThemeVariant;
                Avalonia.Application.Current.RequestedThemeVariant = variant;
                await Ready();
                Save("markdown-" + variant.ToString().ToLowerInvariant() + ".png");
                Avalonia.Application.Current.RequestedThemeVariant = prior;
                await Ready();
            }

            Check(File.Exists(Path.Combine(directory, "markdown.png")), "Markdown 截图已落盘 tmp/w5r4/markdown.png");
        }
        finally
        {
            window.Close();
            await Task.Delay(80);
        }

        async Task Ready()
        {
            await Task.Delay(120);
            await Dispatcher.Yield(DispatcherPriority.Background);
            window.UpdateLayout();
        }

        void Save(string name)
        {
            using var frame = window.CaptureRenderedFrame();
            if (frame is null)
            {
                fail("W5-R4 Markdown：无头截图帧缺失 " + name);
                return;
            }

            frame.Save(Path.Combine(directory, name), PngBitmapEncoderOptions.Default);
        }
    }

    private static Run? FirstHeadingRun(AvTextBlock host, string markdown) =>
        MarkdownTextBlock.Parse(host, markdown)
            .OfType<Bold>()
            .SelectMany(bold => bold.Inlines is null ? Enumerable.Empty<Inline>() : bold.Inlines)
            .OfType<Run>()
            .FirstOrDefault();

    private static bool IsMonoRun(Run run) =>
        run.FontFamily.ToString().Contains("Cascadia", StringComparison.OrdinalIgnoreCase) ||
        run.FontFamily.ToString().Contains("Consolas", StringComparison.OrdinalIgnoreCase);

    /// <summary>把行内元素还原为纯文本，用于断言语法符号是否消失。</summary>
    private static string SegmentText(Inline inline) => inline switch
    {
        Run run => run.Text ?? string.Empty,
        Span span => string.Concat((span.Inlines ?? Enumerable.Empty<Inline>()).Select(SegmentText)),
        InlineUIContainer container => container.Child is AvTextBlock child ? child.Text ?? string.Empty : string.Empty,
        LineBreak => "\n",
        _ => string.Empty,
    };
}
