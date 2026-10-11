using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;

namespace Fufucode.App.Infrastructure.Browser;

public enum BrowserFilePreviewKind
{
    /// <summary>直接以 file URI 渲染，保留相对资源引用。</summary>
    Html,

    /// <summary>图片：小图内联为 data URI 以便跟随系统主题底色，大图直接 file URI。</summary>
    Image,

    /// <summary>纯文本（txt/log/md 等）：转义后放进 &lt;pre&gt; 渲染，不执行任何脚本。</summary>
    Text,

    /// <summary>不预览，界面显示原因。</summary>
    Unsupported,
}

/// <summary>一次文件预览的分类结果。Address 与 Document 互斥：Document 非空时用 NavigateToString。</summary>
public sealed record BrowserFilePreviewTarget(
    BrowserFilePreviewKind Kind, string? FullPath, string Name, long Length,
    string? Address, string? Document, string? Note)
{
    public bool IsSupported => Kind != BrowserFilePreviewKind.Unsupported;
    public bool NeedsInlineDocument => Document is not null;

    public string KindText => Kind switch
    {
        BrowserFilePreviewKind.Html => "HTML",
        BrowserFilePreviewKind.Image => "图片",
        BrowserFilePreviewKind.Text => "文本",
        _ => "无法预览",
    };

    public static BrowserFilePreviewTarget Reject(string? name, string note) =>
        new(BrowserFilePreviewKind.Unsupported, null, name ?? string.Empty, 0, null, null, note);
}

/// <summary>
/// 本地文件预览分类。只读文件、不写临时文件、不执行文件内容里的脚本。
/// md 目前按纯文本渲染（R4 Markdown 渲染产出后可替换本层实现）。
/// </summary>
internal static class BrowserFilePreview
{
    public const long MaxTextBytes = 512 * 1024;
    public const long MaxInlineImageBytes = 4 * 1024 * 1024;

    private static readonly HashSet<string> HtmlExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".html", ".htm" };

    private static readonly Dictionary<string, string> ImageMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".bmp"] = "image/bmp",
        [".webp"] = "image/webp",
        [".avif"] = "image/avif",
        [".svg"] = "image/svg+xml",
        [".ico"] = "image/x-icon",
    };

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".log", ".md", ".markdown", ".json", ".xml", ".yml", ".yaml", ".toml", ".ini", ".cfg",
        ".csv", ".tsv", ".cs", ".js", ".ts", ".tsx", ".jsx", ".py", ".sh", ".bash", ".ps1", ".css",
        ".sql", ".gitignore", ".editorconfig", ".sln", ".csproj", ".props", ".targets",
    };

    public static BrowserFilePreviewTarget Inspect(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return BrowserFilePreviewTarget.Reject(null, "未提供文件路径。");
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return BrowserFilePreviewTarget.Reject(path, "路径无效：" + error.Message);
        }

        var name = Path.GetFileName(full);
        FileInfo info;
        try { info = new FileInfo(full); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return BrowserFilePreviewTarget.Reject(name, "路径无效：" + error.Message);
        }
        if (!info.Exists) return BrowserFilePreviewTarget.Reject(name, "文件不存在。");

        var extension = info.Extension;
        if (HtmlExtensions.Contains(extension))
            return new(BrowserFilePreviewKind.Html, full, name, info.Length, new Uri(full).AbsoluteUri, null, null);

        if (ImageMimeTypes.TryGetValue(extension, out var mime))
        {
            if (info.Length <= MaxInlineImageBytes)
            {
                var document = BuildImageDocument(full, mime);
                if (document is not null) return new(BrowserFilePreviewKind.Image, full, name, info.Length, null, document, null);
            }
            return new(BrowserFilePreviewKind.Image, full, name, info.Length, new Uri(full).AbsoluteUri, null, null);
        }

        if (TextExtensions.Contains(extension) || extension.Length == 0)
        {
            var document = BuildTextDocument(full, info.Length);
            return document is null
                ? BrowserFilePreviewTarget.Reject(name, "看起来是二进制文件，暂不预览。")
                : new(BrowserFilePreviewKind.Text, full, name, info.Length, null, document, null);
        }

        return BrowserFilePreviewTarget.Reject(name, $"暂不支持预览 {extension} 文件。");
    }

    /// <summary>文本文件转成只读 HTML。超出上限截断并如实标注，不静默丢弃内容。</summary>
    private static string? BuildTextDocument(string path, long length)
    {
        try
        {
            var take = (int)Math.Min(length, MaxTextBytes);
            var bytes = new byte[take];
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var read = stream.Read(bytes, 0, take);
            if (read <= 0) return string.Empty;
            if (Array.IndexOf(bytes, (byte)0, 0, read) >= 0) return null; // 二进制嗅探

            var text = Encoding.UTF8.GetString(bytes, 0, read);
            var truncated = read < length;
            var notice = truncated ? $"\n\n—— 已截断：仅显示前 {MaxTextBytes / 1024} KB，共 {length / 1024} KB ——" : string.Empty;
            return Wrap($"<pre class=\"text\">{WebUtility.HtmlEncode(text)}{WebUtility.HtmlEncode(notice)}</pre>");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>小图内联为 data URI，避免 file 文档跨域限制并让底色跟随系统主题。</summary>
    private static string? BuildImageDocument(string path, string mime)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length == 0) return null;
            var encoded = Convert.ToBase64String(bytes);
            var alt = WebUtility.HtmlEncode(Path.GetFileName(path));
            return Wrap($"<div class=\"image\"><img alt=\"{alt}\" src=\"data:{mime};base64,{encoded}\"/></div>");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or OutOfMemoryException)
        {
            return null;
        }
    }

    private static string Wrap(string body) =>
        "<!doctype html><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">" +
        "<style>" +
        ":root{color-scheme:light dark}" +
        "html,body{margin:0;height:100%}" +
        "body{background:#ffffff;color:#1b1b1b;font-family:'Cascadia Mono',Consolas,'Microsoft YaHei',sans-serif}" +
        "@media (prefers-color-scheme:dark){body{background:#1b1b1b;color:#e6e6e6}}" +
        "pre.text{margin:0;padding:14px 16px;font:12px/1.6 'Cascadia Mono',Consolas,monospace;white-space:pre-wrap;word-break:break-word}" +
        "div.image{min-height:100%;display:flex;align-items:center;justify-content:center;padding:16px;box-sizing:border-box}" +
        "div.image img{max-width:100%;max-height:100%;object-fit:contain}" +
        "</style>" + body;
}
