using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace Fufucode.App.ViewModels;

/// <summary>消息里出现的链接，渲染成可点的引用 chip。</summary>
public sealed class MessageLinkViewModel
{
    public MessageLinkViewModel(string title, string url)
    {
        Title = title;
        Url = url;
        Domain = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;
        // 裸链接的标题就是地址本身，再显示一次域名会重复。
        ShowDomain = !string.Equals(title, url, StringComparison.Ordinal);
    }

    public string Title { get; }
    public string Url { get; }
    public string Domain { get; }
    public bool ShowDomain { get; }
}

/// <summary>消息里出现的相关文件路径；它表示消息提到的路径，不等同于模型已读取的证据。</summary>
public sealed class MessageFileViewModel
{
    public MessageFileViewModel(string path)
    {
        Path = path;
        DisplayName = System.IO.Path.GetFileName(path);
        var folder = System.IO.Path.GetDirectoryName(path) ?? string.Empty;
        Folder = folder.Length > 48 ? "…" + folder[^46..] : folder;
    }

    public string Path { get; }
    public string DisplayName { get; }
    public string Folder { get; }
    public string? ResolvedPath { get; private set; }
    public bool Exists { get; private set; }
    public string StatusText => Exists ? "可预览" : "未在当前项目找到";
    public string SourceText => System.IO.Path.IsPathRooted(Path) ? "绝对路径" : "项目相对路径";

    public void Resolve(string? projectRoot)
    {
        ResolvedPath = null;
        Exists = false;
        if (string.IsNullOrWhiteSpace(projectRoot)) return;
        try
        {
            var candidate = System.IO.Path.IsPathRooted(Path)
                ? System.IO.Path.GetFullPath(Path)
                : System.IO.Path.GetFullPath(System.IO.Path.Combine(projectRoot, Path));
            var root = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(projectRoot));
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!candidate.Equals(root, comparison) && !candidate.StartsWith(root + System.IO.Path.DirectorySeparatorChar, comparison)) return;
            ResolvedPath = candidate;
            Exists = File.Exists(candidate);
        }
        catch (ArgumentException) { }
        catch (NotSupportedException) { }
    }
}

/// <summary>
/// 从消息正文里提取链接与文件路径。
///
/// 只看普通文本段（不含代码块）：代码里的路径是代码内容，不是可导航的引用。
/// 提取是纯函数、无网络，避免渲染消息时产生额外 I/O。
/// </summary>
public static class MessageReferenceExtractor
{
    private const int MaxLinks = 12;
    private const int MaxFiles = 24;

    private static readonly Regex MarkdownLink = new(
        @"\[([^\]\r\n]{1,200})\]\((https?://[^)\s]+)\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex BareUrl = new(
        @"https?://[^\s)\]<>""'，。；]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Windows 绝对路径，或至少带一级目录且有扩展名的相对路径。
    private static readonly Regex FilePath = new(
        @"(?<![\w:/])(?:[A-Za-z]:\\[^\s""'<>|*?，。；]+|(?:\.{0,2}/)?(?:[\w.@-]+/)+[\w.@-]+\.[A-Za-z0-9]{1,8})(?![\w/])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static void Extract(string? text,
        ICollection<MessageLinkViewModel> links,
        ICollection<MessageFileViewModel> files)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        var seenLinks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var existing in links) seenLinks.Add(existing.Url);
        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var existing in files) seenFiles.Add(existing.Path);

        foreach (Match match in MarkdownLink.Matches(text))
        {
            if (links.Count >= MaxLinks) break;
            var url = match.Groups[2].Value.TrimEnd('.', ',');
            if (!seenLinks.Add(url)) continue;
            var title = match.Groups[1].Value.Trim();
            links.Add(new MessageLinkViewModel(string.IsNullOrWhiteSpace(title) ? url : title, url));
        }

        foreach (Match match in BareUrl.Matches(text))
        {
            if (links.Count >= MaxLinks) break;
            var url = match.Value.TrimEnd('.', ',', ';', ':', '!', '?');
            if (!seenLinks.Add(url)) continue;
            links.Add(new MessageLinkViewModel(url, url));
        }

        foreach (Match match in FilePath.Matches(text))
        {
            if (files.Count >= MaxFiles) break;
            var path = match.Value.TrimEnd('.', ',', ';', ':', ')', '，', '。', '；');
            if (path.Contains("://", StringComparison.Ordinal)) continue;
            if (!seenFiles.Add(path)) continue;
            files.Add(new MessageFileViewModel(path));
        }
    }
}
