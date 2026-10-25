using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;

namespace Fufucode.App.Features.FilePreview;

/// <summary>
/// 安全 PDF provider：验证签名、解析页数和常见 PDF text operators，输出页级文本。
/// 页面视觉 rasterizer 仍由后续受控 WebView2/PDF 插件提供，避免伪造页面图像或新增核心依赖。
/// </summary>
public sealed class PdfPreviewPlugin : IFilePreviewPlugin
{
    private static readonly Regex PageObject = new(@"/Type\s*/Page(?:\s|/|>)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex LiteralText = new(@"\((?<text>(?:\\.|[^\\)])*)\)\s*T[Jj]", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly SafeFileReader _reader = new();

    public FilePreviewPluginDescriptor Descriptor { get; } = new(
        "document.pdf", "PDF 文档", FilePreviewPluginMediaKind.Document,
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "pdf" }, true, true);
    public FilePreviewKind Kind => FilePreviewKind.Structured;
    public bool CanHandle(FilePreviewRequest request) =>
        string.Equals(Path.GetExtension(request.Path), ".pdf", StringComparison.OrdinalIgnoreCase);
    public bool CanPreview(FilePreviewRequest request) => CanHandle(request);

    public async Task<FilePreviewResult> LoadAsync(FilePreviewRequest request, CancellationToken cancellationToken)
    {
        var read = await _reader.ReadAsync(request, 64L * 1024 * 1024, cancellationToken);
        if (!read.Success) return FilePreviewResult.Failure(request.Path, read.Error ?? "PDF 读取失败。", Kind);
        if (read.Bytes.Length < 5 || !Encoding.ASCII.GetString(read.Bytes, 0, 5).Equals("%PDF-", StringComparison.Ordinal))
            return FilePreviewResult.Failure(read.FullPath, "文件不是有效的 PDF（缺少 %PDF- 文件签名）。", Kind);

        var raw = Encoding.Latin1.GetString(read.Bytes);
        var pages = Math.Min(200, PageObject.Matches(raw).Count);
        var extracted = ExtractText(raw, cancellationToken);
        var tree = new List<FilePreviewTreeNode>
        {
            new("文档", $"{pages} 页 · {read.Size:N0} 字节", extracted.Count > 0 ? extracted : null),
        };
        return new(Kind, read.FullPath, read.Size, $"PDF · {pages} 页 · 文本提取", null,
            read.IsTruncated, null, string.Join("\n\n", extracted.Select(x => x.Value)), tree);
    }

    public Control CreateView(FilePreviewResult result) => new PdfPreviewView(result);
    public void Dispose() { }

    private static List<FilePreviewTreeNode> ExtractText(string raw, CancellationToken cancellationToken)
    {
        var result = new List<FilePreviewTreeNode>();
        var pageIndex = 0;
        foreach (Match page in PageObject.Matches(raw))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (result.Count >= 200) break;
            var start = Math.Max(0, page.Index - 4096);
            var length = Math.Min(8192, raw.Length - start);
            var slice = raw.Substring(start, length);
            var text = string.Join(" ", LiteralText.Matches(slice).Select(match => Unescape(match.Groups["text"].Value)).Where(text => text.Length > 0));
            result.Add(new FilePreviewTreeNode($"第 {++pageIndex} 页", string.IsNullOrWhiteSpace(text) ? "未提取到文本" : text));
        }
        if (result.Count == 0) result.Add(new FilePreviewTreeNode("页面", "未识别到页面对象，可能是扫描 PDF 或压缩对象流。"));
        return result;
    }

    private static string Unescape(string value) => value
        .Replace(@"\n", "\n", StringComparison.Ordinal)
        .Replace(@"\r", "\r", StringComparison.Ordinal)
        .Replace(@"\t", "\t", StringComparison.Ordinal)
        .Replace(@"\(", "(", StringComparison.Ordinal)
        .Replace(@"\)", ")", StringComparison.Ordinal)
        .Replace(@"\\", "\\", StringComparison.Ordinal);
}

internal sealed class PdfPreviewView : Border
{
    public PdfPreviewView(FilePreviewResult result)
    {
        Padding = new Avalonia.Thickness(12);
        Child = new ScrollViewer
        {
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = "PDF 文本预览", Classes = { "bodyStrong" } },
                    new TextBlock { Text = "当前 provider 提取页面文本；扫描页视觉渲染由 PDF 页面插件提供。", Classes = { "caption" }, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    new TreeView { ItemsSource = result.Tree },
                },
            },
        };
    }
}
