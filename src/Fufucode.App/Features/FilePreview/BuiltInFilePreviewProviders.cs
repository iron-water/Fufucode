using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Fufucode.App.Views;
using SkiaSharp;
using Svg.Skia;

namespace Fufucode.App.Features.FilePreview;

internal abstract class FilePreviewProviderBase : IFilePreviewProvider
{
    protected readonly SafeFileReader Reader = new();
    public abstract FilePreviewKind Kind { get; }
    public abstract bool CanPreview(FilePreviewRequest request);
    public abstract Task<FilePreviewResult> LoadAsync(FilePreviewRequest request, CancellationToken cancellationToken);
    public abstract Control CreateView(FilePreviewResult result);
    public virtual void Dispose() { }

    protected static string Extension(FilePreviewRequest request) =>
        Path.GetExtension(request.Path).TrimStart('.').ToLowerInvariant();

    protected static FilePreviewResult ReadFailure(FilePreviewRequest request, SafeFileReadResult read) =>
        FilePreviewResult.Failure(read.FullPath.Length == 0 ? request.Path : read.FullPath, read.Error ?? "文件读取失败。");

    protected static Border Message(string text) => new()
    {
        Padding = new Thickness(16),
        Child = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap },
    };
}

internal sealed class MermaidFilePreviewProvider : FilePreviewProviderBase
{
    public override FilePreviewKind Kind => FilePreviewKind.Mermaid;
    public override bool CanPreview(FilePreviewRequest request) => Extension(request) is "mmd" or "mermaid";

    public override async Task<FilePreviewResult> LoadAsync(FilePreviewRequest request, CancellationToken cancellationToken)
    {
        var read = await Reader.ReadAsync(request, 2 * 1024 * 1024, cancellationToken);
        if (!read.Success) return ReadFailure(request, read);
        return new(Kind, read.FullPath, read.Size, "Mermaid 图表", read.Encoding, read.IsTruncated, null, read.Text);
    }

    public override Control CreateView(FilePreviewResult result) => new RichMarkdownView
    {
        Markdown = $"```mermaid\n{result.Text}\n```",
        Margin = new Thickness(10),
    };
}

internal sealed class MarkdownFilePreviewProvider : FilePreviewProviderBase
{
    public override FilePreviewKind Kind => FilePreviewKind.Markdown;
    public override bool CanPreview(FilePreviewRequest request) => Extension(request) is "md" or "markdown";

    public override async Task<FilePreviewResult> LoadAsync(FilePreviewRequest request, CancellationToken cancellationToken)
    {
        var read = await Reader.ReadAsync(request, SafeFileReader.DefaultMaxBytes, cancellationToken);
        if (!read.Success) return ReadFailure(request, read);
        return new(Kind, read.FullPath, read.Size, "Markdown", read.Encoding, read.IsTruncated, null, read.Text);
    }

    public override Control CreateView(FilePreviewResult result) => new RichMarkdownView
    {
        Markdown = result.Text ?? string.Empty,
        ImageBasePath = Path.GetDirectoryName(result.Path),
        Margin = new Thickness(10),
    };
}

internal sealed class StructuredFilePreviewProvider : FilePreviewProviderBase
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { "json", "jsonc", "xml", "xsd", "yaml", "yml" };
    public override FilePreviewKind Kind => FilePreviewKind.Structured;
    public override bool CanPreview(FilePreviewRequest request) => Extensions.Contains(Extension(request));

    public override async Task<FilePreviewResult> LoadAsync(FilePreviewRequest request, CancellationToken cancellationToken)
    {
        var read = await Reader.ReadAsync(request, SafeFileReader.DefaultMaxBytes, cancellationToken);
        if (!read.Success) return ReadFailure(request, read);
        try
        {
            var extension = Extension(request);
            var parsed = await Task.Run(() =>
            {
                var nodes = StructuredPreviewControl.Parse(read.Text ?? string.Empty, extension, out var limited, cancellationToken);
                return (Nodes: nodes, Truncated: limited);
            }, cancellationToken);
            var format = extension is "yaml" or "yml" ? "YAML 大纲" : extension.ToUpperInvariant();
            return new(Kind, read.FullPath, read.Size, format, read.Encoding,
                read.IsTruncated || parsed.Truncated, null, read.Text, parsed.Nodes);
        }
        catch (Exception error) when (error is JsonException or XmlException or InvalidOperationException)
        {
            return FilePreviewResult.Failure(read.FullPath, $"结构化数据解析失败：{error.Message}", Kind);
        }
    }

    public override Control CreateView(FilePreviewResult result) => new StructuredPreviewControl(result);
}

internal sealed class CsvFilePreviewProvider : FilePreviewProviderBase
{
    public override FilePreviewKind Kind => FilePreviewKind.Table;
    public override bool CanPreview(FilePreviewRequest request) => Extension(request) is "csv" or "tsv";

    public override async Task<FilePreviewResult> LoadAsync(FilePreviewRequest request, CancellationToken cancellationToken)
    {
        var read = await Reader.ReadAsync(request, SafeFileReader.DefaultMaxBytes, cancellationToken);
        if (!read.Success) return ReadFailure(request, read);
        var delimiter = Extension(request) == "tsv" ? '\t' : ',';
        try
        {
            var data = await Task.Run(() => CsvPreviewControl.Parse(read.Text ?? string.Empty, delimiter, cancellationToken), cancellationToken);
            return new(Kind, read.FullPath, read.Size, delimiter == '\t' ? "TSV" : "CSV", read.Encoding,
                read.IsTruncated || data.IsTruncated, null, read.Text, Headers: data.Headers, Rows: data.Rows);
        }
        catch (FormatException error)
        {
            return FilePreviewResult.Failure(read.FullPath, $"表格解析失败：{error.Message}", Kind);
        }
    }

    public override Control CreateView(FilePreviewResult result) => new CsvPreviewControl(result);
}

internal sealed class ImageFilePreviewProvider : FilePreviewProviderBase
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { "png", "jpg", "jpeg", "webp", "gif", "bmp", "svg" };
    public override FilePreviewKind Kind => FilePreviewKind.Image;
    public override bool CanPreview(FilePreviewRequest request) => Extensions.Contains(Extension(request));

    public override async Task<FilePreviewResult> LoadAsync(FilePreviewRequest request, CancellationToken cancellationToken)
    {
        var read = await Reader.ReadAsync(request, 32L * 1024 * 1024, cancellationToken);
        if (!read.Success) return ReadFailure(request, read);
        if (read.Bytes.Length == 0) return FilePreviewResult.Failure(read.FullPath, "图片文件为空。", Kind);
        if (Extension(request) != "svg")
        {
            // 先用 codec 只读尺寸，再解码，避免超大像素图在检查前就分配内存。
            using var codec = SKCodec.Create(new MemoryStream(read.Bytes));
            if (codec is null) return FilePreviewResult.Failure(read.FullPath, "图片解码失败或格式损坏。", Kind);
            if ((long)codec.Info.Width * codec.Info.Height > 64L * 1024 * 1024)
                return FilePreviewResult.Failure(read.FullPath, "图片像素总量超过 64M，已拒绝加载以防止解压炸弹。", Kind);
        }
        if (Extension(request) == "svg")
        {
            try
            {
                var rendered = RenderSvg(read.Bytes, out var error);
                if (rendered is null) return FilePreviewResult.Failure(read.FullPath, error ?? "SVG 无法渲染。", Kind);
                return new(Kind, read.FullPath, read.Size, "SVG", read.Encoding, read.IsTruncated, null, Bytes: rendered);
            }
            catch (Exception error) when (error is XmlException or IOException or ArgumentException or InvalidOperationException)
            {
                return FilePreviewResult.Failure(read.FullPath, $"SVG 解析失败：{error.Message}", Kind);
            }
        }
        return new(Kind, read.FullPath, read.Size, Extension(request).ToUpperInvariant(), read.Encoding,
            read.IsTruncated, null, Bytes: read.Bytes);
    }

    /// <summary>拒绝脚本、事件处理与外部引用；只允许文档内 # 引用，再栅格化为 PNG 交给图片控件。</summary>
    private static byte[]? RenderSvg(byte[] bytes, out string? error)
    {
        error = null;
        using var xml = XmlReader.Create(new MemoryStream(bytes, writable: false), new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 4_000_000,
            IgnoreProcessingInstructions = true,
        });
        var document = XDocument.Load(xml);
        if (document.Root is not { Name.LocalName: "svg" }) { error = "SVG 根元素无效。"; return null; }
        foreach (var element in document.Descendants())
        {
            if (element.Name.LocalName is "script" or "foreignObject" or "iframe" or "audio" or "video" or "canvas")
            { error = "SVG 包含脚本或嵌入内容，已拒绝渲染。"; return null; }
            foreach (var attribute in element.Attributes())
            {
                if (attribute.IsNamespaceDeclaration) continue;
                var local = attribute.Name.LocalName;
                if (local.StartsWith("on", StringComparison.OrdinalIgnoreCase) ||
                    attribute.Value.Contains("javascript:", StringComparison.OrdinalIgnoreCase))
                { error = "SVG 包含脚本事件，已拒绝渲染。"; return null; }
                if (local is "href" or "src" && !attribute.Value.TrimStart().StartsWith('#'))
                { error = "SVG 引用外部资源，已拒绝渲染。"; return null; }
                if (ContainsExternalUrl(attribute.Value))
                { error = "SVG 样式引用外部资源，已拒绝渲染。"; return null; }
            }
            if (element.Name.LocalName == "style" && ContainsExternalUrl(element.Value))
            { error = "SVG 样式引用外部资源，已拒绝渲染。"; return null; }
        }

        using var svg = new SKSvg();
        using var stream = new MemoryStream(bytes, writable: false);
        var picture = svg.Load(stream);
        if (picture is null) { error = "SVG 无法解析。"; return null; }
        var rect = picture.CullRect;
        if (rect.Width <= 0 || rect.Height <= 0) { error = "SVG 没有可绘制内容。"; return null; }
        var scale = Math.Min(1d, Math.Min(4096d / rect.Width, 4096d / rect.Height));
        var width = Math.Max(1, (int)Math.Ceiling(rect.Width * scale));
        var height = Math.Max(1, (int)Math.Ceiling(rect.Height * scale));
        if ((long)width * height > 64L * 1024 * 1024) { error = "SVG 渲染尺寸过大。"; return null; }
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.Scale((float)scale);
            canvas.Translate(-rect.Left, -rect.Top);
            canvas.DrawPicture(picture);
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data?.ToArray();
    }

    private static bool ContainsExternalUrl(string value)
    {
        var index = 0;
        while ((index = value.IndexOf("url(", index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var start = index + 4;
            while (start < value.Length && char.IsWhiteSpace(value[start])) start++;
            var end = value.IndexOf(')', start);
            if (end < 0) return true;
            var target = value[start..end].Trim('\'', '"', ' ');
            if (!target.StartsWith('#')) return true;
            index = end + 1;
        }
        return false;
    }

    public override Control CreateView(FilePreviewResult result)
    {
        try { return new ImagePreviewControl(result.Bytes ?? [], result.Path); }
        catch (Exception error) { return Message($"图片解码失败：{error.Message}"); }
    }
}

internal sealed class TextFilePreviewProvider : FilePreviewProviderBase
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "cs", "fs", "vb", "py", "js", "jsx", "ts", "tsx", "java", "kt", "kts", "go", "rs", "c", "h", "cc", "cpp", "cxx", "hpp", "m", "mm", "swift", "php", "rb", "lua", "dart", "toml", "html", "htm", "css", "scss", "less", "sh", "bash", "zsh", "ps1", "bat", "cmd", "sql", "txt", "log", "ini", "conf", "cfg", "env", "gitignore", "editorconfig", "gradle", "properties", "sln", "csproj", "props", "targets"
    };
    public override FilePreviewKind Kind => FilePreviewKind.Text;
    public override bool CanPreview(FilePreviewRequest request) => Extensions.Contains(Extension(request)) || string.IsNullOrEmpty(Path.GetExtension(request.Path));

    public override async Task<FilePreviewResult> LoadAsync(FilePreviewRequest request, CancellationToken cancellationToken)
    {
        var read = await Reader.ReadAsync(request, SafeFileReader.DefaultMaxBytes, cancellationToken);
        if (!read.Success) return ReadFailure(request, read);
        if (read.Text is null) return FilePreviewResult.Failure(read.FullPath, "文件包含二进制内容，无法按文本预览。", FilePreviewKind.Binary);
        return new(Kind, read.FullPath, read.Size, "源码", read.Encoding, read.IsTruncated, null, read.Text);
    }

    public override Control CreateView(FilePreviewResult result) =>
        new TextPreviewControl(result.Text ?? string.Empty, Path.GetExtension(result.Path));
}

internal sealed class BinaryFilePreviewProvider : FilePreviewProviderBase
{
    public override FilePreviewKind Kind => FilePreviewKind.Binary;
    public override bool CanPreview(FilePreviewRequest request) => true;

    public override async Task<FilePreviewResult> LoadAsync(FilePreviewRequest request, CancellationToken cancellationToken)
    {
        var read = await Reader.ReadAsync(request, 64 * 1024, cancellationToken, allowTruncate: true);
        if (!read.Success) return ReadFailure(request, read);
        var signature = Signature(read.Bytes);
        var shown = Math.Min(read.Bytes.Length, 1024);
        var hex = string.Join(" ", read.Bytes.Take(shown).Select(x => x.ToString("X2")));
        return new(Kind, read.FullPath, read.Size, $"Hex · {signature}", null,
            read.IsTruncated || read.Bytes.Length > shown, null, hex, Bytes: read.Bytes);
    }

    public override Control CreateView(FilePreviewResult result) => new ScrollViewer
    {
        Content = new TextBlock { Text = result.Text, FontFamily = new FontFamily("Cascadia Mono,Consolas"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12) },
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
    };

    private static string Signature(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47 })) return "PNG";
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xD8, 0xFF })) return "JPEG";
        if (bytes.AsSpan().StartsWith(new byte[] { (byte)'G', (byte)'I', (byte)'F' })) return "GIF";
        if (bytes.AsSpan().StartsWith(new byte[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F' }) && bytes.Length > 11 && bytes.AsSpan(8).StartsWith(new byte[] { (byte)'W', (byte)'E', (byte)'B', (byte)'P' })) return "WebP";
        if (bytes.AsSpan().StartsWith(new byte[] { (byte)'B', (byte)'M' })) return "BMP";
        return "未知签名";
    }
}
