using System.Text;

namespace Fufucode.Fui;

internal readonly record struct MarkdownImageRef(string Alt, string Target, int Start, int Length);

// Extracts Markdown ![alt](target) and single-line <img src="..."> image references outside inline
// code spans. Decodes bounded data:image/... URIs in memory; never performs remote network fetches.
internal static class MarkdownImages
{
    internal static bool MayContainImage(string text) =>
        text.Contains("![", StringComparison.Ordinal) || text.Contains("<img", StringComparison.OrdinalIgnoreCase);

    internal static bool MayContainDataUri(string text) =>
        text.Contains("data:image/", StringComparison.OrdinalIgnoreCase);

    internal static bool IsRemoteUrl(string target) =>
        target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        target.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
        target.StartsWith("//", StringComparison.Ordinal);

    internal static bool IsDataUri(string target) =>
        target.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase);

    internal static List<MarkdownImageRef> Extract(string line)
    {
        var refs = new List<MarkdownImageRef>();
        var code = 0;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '`')
            {
                var size = 1;
                while (i + size < line.Length && line[i + size] == '`') size++;
                if (code == 0) code = size;
                else if (size == code) code = 0;
                i += size - 1;
                continue;
            }
            if (code != 0 || Escaped(line, i)) continue;

            if (line[i] == '!' && i + 1 < line.Length && line[i + 1] == '[' &&
                TryParseMarkdownImage(line, i, out var md))
            {
                refs.Add(md);
                i = md.Start + md.Length - 1;
                continue;
            }
            if (line[i] == '<' && TryParseHtmlImage(line, i, out var html))
            {
                refs.Add(html);
                i = html.Start + html.Length - 1;
            }
        }
        return refs;
    }

    internal static string CollapseDataUris(string line)
    {
        if (!MayContainDataUri(line)) return line;
        var refs = Extract(line);
        var current = line;
        if (refs.Count != 0)
        {
            StringBuilder? sb = null;
            var copied = 0;
            foreach (var item in refs)
            {
                if (!IsDataUri(item.Target)) continue;
                sb ??= new(line.Length);
                sb.Append(line, copied, item.Start - copied).Append(FormatFallback(item));
                copied = item.Start + item.Length;
            }
            if (sb is not null) current = sb.Append(line, copied, line.Length - copied).ToString();
        }
        return CollapseIncompleteDataUri(current);
    }

    // While an AI reply streams a large inline data:image/... URI, hide the unfinished base64 tail
    // outside inline code spans instead of flooding the terminal with raw base64 text.
    internal static string CollapseIncompleteDataUri(string line)
    {
        if (!MayContainDataUri(line)) return line;
        var code = 0;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '`')
            {
                var size = 1;
                while (i + size < line.Length && line[i + size] == '`') size++;
                if (code == 0) code = size;
                else if (size == code) code = 0;
                i += size - 1;
                continue;
            }
            if (code != 0 || Escaped(line, i)) continue;

            if (line[i] == '!' && i + 1 < line.Length && line[i + 1] == '[')
            {
                var closeBracket = FindClosingBracket(line, i + 1);
                if (closeBracket > 0 && closeBracket + 1 < line.Length && line[closeBracket + 1] == '(')
                {
                    var dest = closeBracket + 2;
                    while (dest < line.Length && char.IsWhiteSpace(line[dest])) dest++;
                    if (dest < line.Length && line[dest] == '<') dest++;
                    if (line.AsSpan(dest).StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) &&
                        !TryParseMarkdownImage(line, i, out _))
                    {
                        var mime = ExtractDataUriMime(line[dest..]);
                        return line[..i] + $"[图片加载中：{mime}…]";
                    }
                }
            }
            else if (line[i] == '<' && i + 5 <= line.Length &&
                line.AsSpan(i, 4).Equals("<img", StringComparison.OrdinalIgnoreCase) &&
                line[i + 4] is ' ' or '\t' &&
                line.AsSpan(i).IndexOf("data:image/", StringComparison.OrdinalIgnoreCase) >= 0 &&
                !TryParseHtmlImage(line, i, out _))
            {
                var idx = line.IndexOf("data:image/", i, StringComparison.OrdinalIgnoreCase);
                var mime = idx >= 0 ? ExtractDataUriMime(line[idx..]) : "image";
                return line[..i] + $"[图片加载中：{mime}…]";
            }
        }
        return line;
    }

    internal static string FormatFallback(MarkdownImageRef item)
    {
        var alt = item.Alt.Trim();
        if (IsDataUri(item.Target))
        {
            var mime = ExtractDataUriMime(item.Target);
            return alt.Length > 0 ? $"[图片：{alt} ({mime})]" : $"[图片：{mime}]";
        }
        if (IsRemoteUrl(item.Target))
            return alt.Length > 0 ? $"[外链图片：{alt} ({item.Target})]" : $"[外链图片：{item.Target}]";
        return alt.Length > 0 ? $"[图片：{alt} ({item.Target})]" : $"[图片：{item.Target}]";
    }

    internal static bool TryDecodeDataUri(string target, out string mimeType, out byte[] bytes)
    {
        mimeType = "";
        bytes = [];
        if (!IsDataUri(target)) return false;
        var comma = target.IndexOf(',');
        if (comma < 12 || comma + 1 >= target.Length) return false;

        var header = target[5..comma];
        var parts = header.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || !parts[0].StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return false;
        mimeType = parts[0].ToLowerInvariant();
        var isBase64 = parts.Skip(1).Any(p => p.Equals("base64", StringComparison.OrdinalIgnoreCase));
        var payload = target[(comma + 1)..];

        if (isBase64)
        {
            var maxBase64 = (long)ProjectFiles.MaxAttachmentBytes * 4 / 3 + 256;
            if (payload.Length > maxBase64) return false;
            var clean = payload.Contains('%') ? SafeUnescape(payload) : payload;
            if (clean is null) return false;
            // Strip ASCII whitespace inside base64 payloads.
            if (clean.IndexOfAny([' ', '\t', '\r', '\n']) >= 0)
            {
                var sb = new StringBuilder(clean.Length);
                foreach (var ch in clean)
                    if (ch is not (' ' or '\t' or '\r' or '\n')) sb.Append(ch);
                clean = sb.ToString();
            }
            try
            {
                var decoded = Convert.FromBase64String(clean);
                if (decoded.Length == 0 || decoded.Length > ProjectFiles.MaxAttachmentBytes) return false;
                bytes = decoded;
                return true;
            }
            catch (FormatException) { return false; }
        }

        if (mimeType == "image/svg+xml")
        {
            if (payload.Length > ProjectFiles.MaxAttachmentBytes * 3) return false;
            var text = payload.Contains('%') ? SafeUnescape(payload) : payload;
            if (text is null) return false;
            var utf8 = Encoding.UTF8.GetBytes(text);
            if (utf8.Length == 0 || utf8.Length > ProjectFiles.MaxAttachmentBytes) return false;
            bytes = utf8;
            return true;
        }
        return false;
    }

    private static string ExtractDataUriMime(string target)
    {
        var end = target.IndexOfAny([';', ',']);
        return end > 5 ? target[5..end].ToLowerInvariant() : "data:image";
    }

    private static string? SafeUnescape(string text)
    {
        try { return Uri.UnescapeDataString(text); }
        catch (UriFormatException) { return null; }
    }

    private static bool TryParseMarkdownImage(string line, int start, out MarkdownImageRef result)
    {
        result = default;
        var closeBracket = FindClosingBracket(line, start + 1);
        if (closeBracket < 0 || closeBracket + 1 >= line.Length || line[closeBracket + 1] != '(') return false;

        var destStart = closeBracket + 2;
        while (destStart < line.Length && char.IsWhiteSpace(line[destStart])) destStart++;
        if (destStart >= line.Length) return false;

        string target;
        int closeParen;
        if (line[destStart] == '<')
        {
            var angleEnd = line.IndexOf('>', destStart + 1);
            if (angleEnd < 0) return false;
            target = line[(destStart + 1)..angleEnd].Trim();
            closeParen = FindClosingParenAfterTitle(line, angleEnd + 1);
            if (closeParen < 0) return false;
        }
        else
        {
            var depth = 0;
            var pos = destStart;
            var targetEnd = -1;
            while (pos < line.Length)
            {
                var ch = line[pos];
                if (ch == '\\' && pos + 1 < line.Length) { pos += 2; continue; }
                if (ch == '(') { depth++; pos++; continue; }
                if (ch == ')')
                {
                    if (depth == 0) { targetEnd = pos; break; }
                    depth--;
                    pos++;
                    continue;
                }
                if (char.IsWhiteSpace(ch) && depth == 0) { targetEnd = pos; break; }
                pos++;
            }
            if (targetEnd <= destStart) return false;
            target = line[destStart..targetEnd];
            closeParen = targetEnd < line.Length && line[targetEnd] == ')'
                ? targetEnd
                : FindClosingParenAfterTitle(line, targetEnd);
            if (closeParen < 0) return false;
        }

        if (target.Length == 0) return false;
        var alt = line[(start + 2)..closeBracket];
        result = new(alt, target, start, closeParen - start + 1);
        return true;
    }

    private static int FindClosingBracket(string line, int open)
    {
        var depth = 0;
        for (var i = open + 1; i < line.Length; i++)
        {
            if (line[i] == '\\') { i++; continue; }
            if (line[i] == '[') depth++;
            else if (line[i] == ']')
            {
                if (depth == 0) return i;
                depth--;
            }
        }
        return -1;
    }

    private static int FindClosingParenAfterTitle(string line, int pos)
    {
        while (pos < line.Length && char.IsWhiteSpace(line[pos])) pos++;
        if (pos < line.Length && line[pos] == ')') return pos;
        if (pos >= line.Length || line[pos] is not ('"' or '\'')) return -1;
        var quote = line[pos++];
        while (pos < line.Length)
        {
            if (line[pos] == '\\') { pos += 2; continue; }
            if (line[pos] == quote) { pos++; break; }
            pos++;
        }
        while (pos < line.Length && char.IsWhiteSpace(line[pos])) pos++;
        return pos < line.Length && line[pos] == ')' ? pos : -1;
    }

    private static bool TryParseHtmlImage(string line, int start, out MarkdownImageRef result)
    {
        result = default;
        if (start + 5 > line.Length || !line.AsSpan(start, 4).Equals("<img", StringComparison.OrdinalIgnoreCase))
            return false;
        if (line[start + 4] is not (' ' or '\t')) return false;

        // Find closing '>' outside quoted attribute values.
        char quote = '\0';
        var end = -1;
        for (var i = start + 5; i < line.Length; i++)
        {
            var ch = line[i];
            if (quote != '\0') { if (ch == quote) quote = '\0'; continue; }
            if (ch is '"' or '\'') { quote = ch; continue; }
            if (ch == '>') { end = i; break; }
        }
        if (end < 0) return false;

        var body = line.AsSpan(start + 5, end - (start + 5));
        string? src = null;
        string alt = "";
        var pos = 0;
        while (pos < body.Length)
        {
            while (pos < body.Length && (char.IsWhiteSpace(body[pos]) || body[pos] == '/')) pos++;
            var nameStart = pos;
            while (pos < body.Length && (char.IsLetterOrDigit(body[pos]) || body[pos] is '-' or '_')) pos++;
            if (pos == nameStart) { pos++; continue; }
            var name = body[nameStart..pos];
            while (pos < body.Length && char.IsWhiteSpace(body[pos])) pos++;
            if (pos >= body.Length || body[pos] != '=') continue;
            pos++;
            while (pos < body.Length && char.IsWhiteSpace(body[pos])) pos++;
            if (pos >= body.Length) break;
            ReadOnlySpan<char> value;
            if (body[pos] is '"' or '\'')
            {
                var q = body[pos++];
                var valStart = pos;
                while (pos < body.Length && body[pos] != q) pos++;
                value = body[valStart..pos];
                if (pos < body.Length) pos++;
            }
            else
            {
                var valStart = pos;
                while (pos < body.Length && !char.IsWhiteSpace(body[pos]) && body[pos] != '/') pos++;
                value = body[valStart..pos];
            }
            if (name.Equals("src", StringComparison.OrdinalIgnoreCase)) src = value.ToString().Trim();
            else if (name.Equals("alt", StringComparison.OrdinalIgnoreCase)) alt = value.ToString();
        }
        if (string.IsNullOrEmpty(src)) return false;
        result = new(alt, src, start, end - start + 1);
        return true;
    }

    private static bool Escaped(string text, int index)
    {
        var count = 0;
        while (index > 0 && text[--index] == '\\') count++;
        return (count & 1) != 0;
    }
}
