using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Fufucode.App.Features.Library;

internal enum LibraryExtractionKind
{
    Text = 0,
    Unsupported = 1,
    Failed = 2,
}

/// <summary>一次文本提取结果。Kind != Text 时 Text 为空且 Note 说明原因（原件保留）。</summary>
internal sealed record LibraryExtraction(LibraryExtractionKind Kind, string Text, string? Note)
{
    public static LibraryExtraction Unsupported(string note) => new(LibraryExtractionKind.Unsupported, string.Empty, note);
    public static LibraryExtraction Failed(string note) => new(LibraryExtractionKind.Failed, string.Empty, note);
}

/// <summary>
/// 首版文本提取：纯文本/Markdown/常见代码与配置/JSON/CSV 等按文本读取；
/// 二进制与不支持的格式返回 Unsupported，不复制、不改写、不猜测内容。
/// 索引读取上限之外的字节截断并如实标注，不静默丢弃。
/// </summary>
internal static class LibraryTextExtractor
{
    /// <summary>单文件索引读取上限，超出截断并标注。</summary>
    public const long MaxReadBytes = 16L * 1024 * 1024;

    /// <summary>存入资料库与预览的字符上限。</summary>
    public const int MaxStoredChars = 400_000;

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".text", ".md", ".markdown", ".log", ".csv", ".tsv", ".json", ".jsonl", ".ndjson",
        ".xml", ".yml", ".yaml", ".ini", ".cfg", ".conf", ".toml", ".properties", ".env",
        ".cs", ".ts", ".tsx", ".js", ".mjs", ".cjs", ".jsx", ".py", ".ps1", ".psm1", ".sh", ".bat",
        ".cmd", ".sql", ".html", ".htm", ".css", ".scss", ".tex", ".bib", ".gitignore", ".editorconfig",
        ".razor", ".xaml", ".axaml", ".mermaid", ".svg",
    };

    private static Encoding? _gb18030;
    private static bool _codePagesReady;

    public static bool IsSupportedExtension(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        return TextExtensions.Contains(Path.GetExtension(path));
    }

    public static LibraryExtraction Extract(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return LibraryExtraction.Failed("文件不存在：" + path);
            if (info.Length == 0) return new LibraryExtraction(LibraryExtractionKind.Text, string.Empty, "空文件");
            if (!IsSupportedExtension(path))
                return LibraryExtraction.Unsupported("首版只索引文本类格式：" + (Path.GetExtension(path) is { Length: > 0 } ext ? ext : "(无扩展名)"));

            var truncated = false;
            byte[] bytes;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var length = (int)Math.Min(stream.Length, MaxReadBytes);
                truncated = stream.Length > MaxReadBytes;
                bytes = new byte[length];
                var read = 0;
                while (read < length)
                {
                    var chunk = stream.Read(bytes, read, length - read);
                    if (chunk <= 0) break;
                    read += chunk;
                }
                if (read < length) bytes = bytes[..read];
            }

            if (LooksBinary(bytes)) return LibraryExtraction.Unsupported("二进制内容，首版不索引");

            var text = DecodeText(bytes, out var decodeNote);
            if (text is null) return LibraryExtraction.Failed(decodeNote ?? "无法识别文本编码");

            var notes = new List<string>();
            if (truncated) notes.Add($"超过 {MaxReadBytes / (1024 * 1024)} MB，已按上限截断索引");
            if (decodeNote is not null) notes.Add(decodeNote);
            return new LibraryExtraction(LibraryExtractionKind.Text, Clamp(text), notes.Count == 0 ? null : string.Join("；", notes));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return LibraryExtraction.Failed(error.GetType().Name + "：" + error.Message);
        }
    }

    public static LibraryExtraction FromText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return new LibraryExtraction(LibraryExtractionKind.Text, string.Empty, "空文本");
        return new LibraryExtraction(LibraryExtractionKind.Text, Clamp(text), null);
    }

    public static string Clamp(string text) =>
        text.Length <= MaxStoredChars ? text : text[..MaxStoredChars];

    private static bool LooksBinary(byte[] bytes)
    {
        var limit = Math.Min(bytes.Length, 4096);
        for (var index = 0; index < limit; index++)
            if (bytes[index] == 0) return true;
        return false;
    }

    /// <summary>BOM 优先；否则严格 UTF-8，失败再试 GB18030（中文 Windows 常见编码）。</summary>
    private static string? DecodeText(byte[] bytes, out string? note)
    {
        note = null;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);

        // 读取上限可能在多字节字符中间截断；用 Decoder 且 flush:false，
        // 末尾不完整序列被丢弃，而不是误判为“非 UTF-8”并退化成错误编码。
        try
        {
            return Decode(bytes, new UTF8Encoding(false, throwOnInvalidBytes: true));
        }
        catch (DecoderFallbackException)
        {
            var gb = Gb18030();
            if (gb is null) return null;
            note = "非 UTF-8 文本，已按 GB18030 解码";
            return Decode(bytes, gb);
        }
    }

    private static string Decode(byte[] bytes, Encoding encoding)
    {
        var decoder = encoding.GetDecoder();
        var chars = new char[encoding.GetMaxCharCount(bytes.Length)];
        var count = decoder.GetChars(bytes, 0, bytes.Length, chars, 0, flush: false);
        return new string(chars, 0, count);
    }

    private static Encoding? Gb18030()
    {
        if (_codePagesReady) return _gb18030;
        _codePagesReady = true;
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            _gb18030 = Encoding.GetEncoding("GB18030");
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException)
        {
            _gb18030 = null;
        }
        return _gb18030;
    }
}
