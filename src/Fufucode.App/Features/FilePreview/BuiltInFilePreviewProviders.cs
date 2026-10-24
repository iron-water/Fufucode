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
            var tree = extension is "json" or "jsonc" ? ParseJson(read.Text ?? string.Empty) :
                extension is "xml" or "xsd" ? ParseXml(read.Text ?? string.Empty) : ParseYaml(read.Text ?? string.Empty);
            return new(Kind, read.FullPath, read.Size, extension.ToUpperInvariant(), read.Encoding,
                read.IsTruncated, null, read.Text, tree);
        }
        catch (Exception error) when (error is JsonException or XmlException or InvalidOperationException)
        {
            return FilePreviewResult.Failure(read.FullPath, $"结构化数据解析失败：{error.Message}", Kind);
        }
    }

    public override Control CreateView(FilePreviewResult result) => new TreeView
    {
        ItemsSource = result.Tree,
        Margin = new Thickness(10),
    };

    private static IReadOnlyList<FilePreviewTreeNode> ParseJson(string text)
    {
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 64, AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        return [JsonNode("root", document.RootElement, 0)];
    }

    private static FilePreviewTreeNode JsonNode(string label, JsonElement element, int depth)
    {
        if (depth >= 64) return new(label, "深度上限");
        return element.ValueKind switch
        {
            JsonValueKind.Object => new(label, "{}", element.EnumerateObject().Take(1000).Select(x => JsonNode(x.Name, x.Value, depth + 1)).ToArray()),
            JsonValueKind.Array => new(label, $"[{element.GetArrayLength()}]", element.EnumerateArray().Take(1000).Select((x, i) => JsonNode($"[{i}]", x, depth + 1)).ToArray()),
            JsonValueKind.String => new(label, element.GetString()),
            _ => new(label, element.ToString()),
        };
    }

    private static IReadOnlyList<FilePreviewTreeNode> ParseXml(string text)
    {
        var root = XDocument.Parse(text, LoadOptions.PreserveWhitespace).Root ?? throw new XmlException("XML 没有根节点。");
        return [XmlNode(root, 0)];
    }

    private static FilePreviewTreeNode XmlNode(XElement element, int depth)
    {
        if (depth >= 64) return new(element.Name.LocalName, "深度上限");
        var attributes = element.Attributes().Take(100).Select(x => new FilePreviewTreeNode("@" + x.Name.LocalName, x.Value));
        var children = element.Elements().Take(1000).Select(x => XmlNode(x, depth + 1));
        var text = string.Concat(element.Nodes().OfType<XText>().Select(x => x.Value)).Trim();
        return new(element.Name.LocalName, text.Length > 0 ? text : null, attributes.Concat(children).ToArray());
    }

    private static IReadOnlyList<FilePreviewTreeNode> ParseYaml(string text)
    {
        var roots = new List<FilePreviewTreeNode>();
        var stack = new Stack<(int Indent, FilePreviewTreeNode Node)>();
        foreach (var raw in text.Split('\n').Take(10000))
        {
            var line = raw.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#')) continue;
            var indent = line.Length - line.TrimStart().Length;
            var content = line.Trim();
            var separator = content.IndexOf(':');
            var label = separator > 0 ? content[..separator].Trim() : content;
            var value = separator > 0 ? content[(separator + 1)..].Trim() : null;
            var node = new FilePreviewTreeNode(label, value);
            while (stack.Count > 0 && stack.Peek().Indent >= indent) stack.Pop();
            if (stack.Count == 0) roots.Add(node);
            else
            {
                var parent = stack.Pop();
                var children = (parent.Node.Children ?? []).Append(node).ToArray();
                var replacement = parent.Node with { Children = children };
                if (stack.Count == 0)
                {
                    var index = roots.IndexOf(parent.Node);
                    if (index >= 0) roots[index] = replacement;
                }
                else
                {
                    var grand = stack.Pop();
                    stack.Push((grand.Indent, grand.Node with { Children = (grand.Node.Children ?? []).Select(x => ReferenceEquals(x, parent.Node) ? replacement : x).ToArray() }));
                }
                node = replacement;
            }
            stack.Push((indent, node));
        }
        return roots;
    }
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
        var records = Parse(read.Text ?? string.Empty, delimiter).Take(10000).ToArray();
        var headers = records.FirstOrDefault() ?? [];
        var rows = records.Skip(1).Select(x => new FilePreviewTableRow(x)).ToArray();
        return new(Kind, read.FullPath, read.Size, delimiter == '\t' ? "TSV" : "CSV", read.Encoding,
            read.IsTruncated, null, read.Text, Headers: headers, Rows: rows);
    }

    public override Control CreateView(FilePreviewResult result)
    {
        var grid = new Grid { Margin = new Thickness(10), RowSpacing = 1, ColumnSpacing = 1 };
        var columns = Math.Min(32, result.Headers?.Count ?? 0);
        for (var i = 0; i < columns; i++) grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(220)));
        AddRow(grid, result.Headers ?? [], 0, true);
        var rows = result.Rows ?? [];
        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++) AddRow(grid, rows[rowIndex].Cells, rowIndex + 1, false);
        return new ScrollViewer { Content = grid, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };

        void AddRow(Grid owner, IReadOnlyList<string> cells, int rowIndex, bool header)
        {
            owner.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (var column = 0; column < Math.Min(columns, cells.Count); column++)
            {
                var text = new TextBlock { Text = cells[column], Padding = new Thickness(8, 5), TextWrapping = TextWrapping.NoWrap };
                if (header) text.FontWeight = FontWeight.Bold;
                Grid.SetRow(text, rowIndex); Grid.SetColumn(text, column); owner.Children.Add(text);
            }
        }
    }

    private static IEnumerable<IReadOnlyList<string>> Parse(string text, char delimiter)
    {
        var cells = new List<string>(); var value = new StringBuilder(); var quoted = false;
        foreach (var character in text)
        {
            if (character == '"') { quoted = !quoted; continue; }
            if (!quoted && character == delimiter) { cells.Add(value.ToString()); value.Clear(); continue; }
            if (!quoted && character == '\n') { cells.Add(value.ToString().TrimEnd('\r')); yield return cells.ToArray(); cells.Clear(); value.Clear(); continue; }
            value.Append(character);
        }
        if (value.Length > 0 || cells.Count > 0) { cells.Add(value.ToString()); yield return cells.ToArray(); }
    }
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
            using var decoded = SKBitmap.Decode(read.Bytes);
            if (decoded is null) return FilePreviewResult.Failure(read.FullPath, "图片解码失败或格式损坏。", Kind);
            if ((long)decoded.Width * decoded.Height > 64L * 1024 * 1024)
                return FilePreviewResult.Failure(read.FullPath, "图片像素总量超过 64M，已拒绝加载以防止解压炸弹。", Kind);
        }
        if (Extension(request) == "svg")
        {
            var svg = read.Text ?? Encoding.UTF8.GetString(read.Bytes);
            if (svg.Contains("<script", StringComparison.OrdinalIgnoreCase) || svg.Contains("<!entity", StringComparison.OrdinalIgnoreCase) ||
                svg.Contains("http://", StringComparison.OrdinalIgnoreCase) || svg.Contains("https://", StringComparison.OrdinalIgnoreCase) ||
                svg.Contains("data:", StringComparison.OrdinalIgnoreCase))
                return FilePreviewResult.Failure(read.FullPath, "SVG 包含脚本或外部资源，已拒绝渲染。", Kind);
        }
        return new(Kind, read.FullPath, read.Size, Extension(request).ToUpperInvariant(), read.Encoding,
            read.IsTruncated, null, Bytes: read.Bytes);
    }

    public override Control CreateView(FilePreviewResult result)
    {
        try { return new ImagePreviewControl(result.Bytes ?? []); }
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
        if (read.Text is null) return FilePreviewResult.Failure(read.FullPath, "文件不是可读文本，已切换为二进制预览。", FilePreviewKind.Binary);
        return new(Kind, read.FullPath, read.Size, "源码", read.Encoding, read.IsTruncated, null, read.Text);
    }

    public override Control CreateView(FilePreviewResult result) => new TextPreviewControl(result.Text ?? string.Empty);
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
        var hex = string.Join(" ", read.Bytes.Take(256).Select(x => x.ToString("X2")));
        return new(Kind, read.FullPath, read.Size, $"Hex · {signature}", null, read.IsTruncated, null, hex, Bytes: read.Bytes);
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
