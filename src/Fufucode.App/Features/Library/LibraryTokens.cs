using System;
using System.Collections.Generic;
using System.Text;

namespace Fufucode.App.Features.Library;

/// <summary>
/// 轻量全文索引分词：ASCII/数字按单词小写化，汉字按单字 + 相邻双字（bigram）建 token。
/// 不依赖 FTS5 或外部分词库：SQLite 默认 tokenizer 会把整段汉字当成一个 token，
/// 对中文检索几乎无效；bigram 方案命中率可接受且行为完全可测。
/// </summary>
internal static class LibraryTokens
{
    /// <summary>单条目 token 上限，防止超大文件把索引撑爆；超出部分如实计入索引覆盖率说明。</summary>
    public const int MaxTokensPerDocument = 60000;

    public static IReadOnlyList<string> Tokenize(string? text)
    {
        var tokens = new List<string>();
        if (string.IsNullOrEmpty(text)) return tokens;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var word = new StringBuilder();
        var previousCjk = (string?)null;

        void FlushWord()
        {
            if (word.Length == 0) return;
            var token = word.ToString().ToLowerInvariant();
            word.Clear();
            if (seen.Add(token)) tokens.Add(token);
        }

        void EmitCjk(Rune rune)
        {
            var current = rune.ToString();
            if (seen.Add(current)) tokens.Add(current);
            if (previousCjk is not null)
            {
                var bigram = previousCjk + current;
                if (seen.Add(bigram)) tokens.Add(bigram);
            }
            previousCjk = current;
        }

        foreach (var rune in text.EnumerateRunes())
        {
            if (tokens.Count >= MaxTokensPerDocument) break;
            if (IsCjk(rune))
            {
                FlushWord();
                EmitCjk(rune);
                continue;
            }
            if (Rune.IsLetterOrDigit(rune))
            {
                previousCjk = null;
                word.Append(rune.ToString());
                continue;
            }
            FlushWord();
            previousCjk = null;
        }
        FlushWord();
        return tokens;
    }

    /// <summary>查询分词与文档分词同一套规则，保证 token 对齐。</summary>
    public static IReadOnlyList<string> TokenizeQuery(string? text)
    {
        var tokens = Tokenize(text);
        return tokens.Count == 0 ? Array.Empty<string>() : tokens;
    }

    private static bool IsCjk(Rune rune)
    {
        var value = rune.Value;
        return value is >= 0x3400 and <= 0x4DBF   // CJK 扩展 A
            or >= 0x4E00 and <= 0x9FFF            // CJK 统一表意文字
            or >= 0xF900 and <= 0xFAFF            // 兼容表意文字
            or >= 0x3040 and <= 0x30FF            // 日文假名（中文资料里常见）
            or >= 0xAC00 and <= 0xD7AF            // 谚文
            or >= 0x20000 and <= 0x2FA1F;         // 扩展 B 及以后
    }
}
