using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace Fufucode.App.ViewModels;

/// <summary>Read-only presentation of the attachment envelope; the submitted text stays intact.</summary>
public sealed record AttachmentPresentation(string Body, IReadOnlyList<AttachedFile> Files)
{
    private static readonly Regex Header = new(@"^(?:图片|文件|文件引用（未提取内容，请使用工具读取）|二进制文件引用（未提取内容）)：(.+)$", RegexOptions.Multiline);
    private static readonly Regex Item = new("\\G<attachment index=\"([0-9]+)\" path=\"([^\"]+)\" chars=\"([0-9]+)\">\\n");
    public static AttachmentPresentation Parse(string text)
    {
        const string start = "\n\n<attached_files>\n";
        const string end = "\n</attached_files>";
        var opening = text.IndexOf(start, StringComparison.Ordinal);
        if (opening < 0) return new(text, Array.Empty<AttachedFile>());
        var cursor = opening + start.Length;
        var files = new List<AttachedFile>();
        if (text.AsSpan(cursor).StartsWith("<attachment index=", StringComparison.Ordinal))
        {
            // Length framing, rather than searching closing tags, preserves arbitrary file prose.
            while (true)
            {
                var match = Item.Match(text, cursor);
                if (!match.Success || !int.TryParse(match.Groups[3].Value, out var length)) return new(text, Array.Empty<AttachedFile>());
                cursor = match.Index + match.Length;
                if (length > text.Length - cursor) return new(text, Array.Empty<AttachedFile>());
                var context = text.Substring(cursor, length);
                cursor += length;
                const string itemEnd = "\n</attachment>";
                if (!text.AsSpan(cursor).StartsWith(itemEnd, StringComparison.Ordinal)) return new(text, Array.Empty<AttachedFile>());
                cursor += itemEnd.Length;
                var path = System.Net.WebUtility.HtmlDecode(match.Groups[2].Value);
                if (!Path.IsPathFullyQualified(path)) return new(text, Array.Empty<AttachedFile>());
                files.Add(new(path, context) { ReferenceNumber = int.TryParse(match.Groups[1].Value, out var number) ? number : files.Count + 1 });
                if (text.AsSpan(cursor).StartsWith(end, StringComparison.Ordinal))
                    return new(text.Remove(opening, cursor + end.Length - opening), files);
                if (cursor >= text.Length || text[cursor++] != '\n') return new(text, Array.Empty<AttachedFile>());
            }
        }
        var closing = text.LastIndexOf(end, StringComparison.Ordinal);
        if (closing < cursor) return new(text, Array.Empty<AttachedFile>());
        var envelope = text.Substring(cursor, closing - cursor);
        foreach (Match match in Header.Matches(envelope))
        {
            var path = match.Groups[1].Value.TrimEnd('\r');
            // Legacy envelopes have no length framing. Require an absolute emitted path header.
            if (Path.IsPathFullyQualified(path)) files.Add(new(path, match.Value));
        }
        if (files.Count == 0) return new(text, Array.Empty<AttachedFile>());
        return new(text.Remove(opening, closing + end.Length - opening), files);
    }
}
