using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Media;
using Avalonia.Media.Fonts;

namespace Fufucode.App.Infrastructure;

/// <summary>一份字体对同时供正文、菜单和输入使用；汉字缺字先查中文字体，不进入系统字体缓存的任意匹配。</summary>
internal sealed class AppFontCollection : IFontCollection
{
    internal static readonly Uri CollectionKey = new("fonts:FufucodeText");
    private sealed record FontPair(string Latin, string Chinese);
    private readonly ConcurrentDictionary<string, FontPair> _pairs = new(StringComparer.Ordinal);
    private static IFontCollection SystemFonts => FontManager.Current.SystemFonts;
    public Uri Key => CollectionKey;
    public int Count => 0;
    public FontFamily this[int index] => throw new ArgumentOutOfRangeException(nameof(index));

    internal FontFamily Register(string latin, string chinese)
    {
        var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(latin + "\n" + chinese)))[..20];
        _pairs.TryAdd(name, new(latin, chinese));
        return new FontFamily(CollectionKey + "#" + name);
    }

    public bool TryGetGlyphTypeface(string familyName, FontStyle style, FontWeight weight, FontStretch stretch, out GlyphTypeface glyphTypeface)
    {
        if (_pairs.TryGetValue(familyName, out var pair) && TryFace(pair.Latin, style, weight, stretch, out var face))
        {
            glyphTypeface = face;
            return true;
        }
        glyphTypeface = null!;
        return false;
    }

    public bool TryMatchCharacter(int codepoint, FontStyle style, FontWeight weight, FontStretch stretch,
        string? familyName, CultureInfo? culture, out Typeface typeface)
    {
        typeface = default;
        if (familyName is null || !_pairs.TryGetValue(familyName, out var pair)) return false;
        if (IsCjk(codepoint))
        {
            foreach (var name in new[] { pair.Chinese }.Concat(ChineseFallbacks(culture)))
            {
                if (!TryFace(name, style, weight, stretch, out var face) || !face.CharacterToGlyphMap.ContainsGlyph(codepoint)) continue;
                // 返回只有这一个字体的集合，不能让系统再为同一汉字段选择其它缓存字体。
                typeface = new Typeface(new FontFamily(LatinFontCollection.CollectionKey + "#" + name), style, weight, stretch);
                return true;
            }
        }
        else if (IsKana(codepoint))
        {
            foreach (var name in new[] { "Yu Gothic UI", "Meiryo UI", "Noto Sans CJK JP", "Noto Sans JP" })
                if (TryFace(name, style, weight, stretch, out var kanaFace) && kanaFace.CharacterToGlyphMap.ContainsGlyph(codepoint))
                {
                    typeface = new Typeface(new FontFamily(LatinFontCollection.CollectionKey + "#" + name), style, weight, stretch);
                    return true;
                }
        }
        else if (TryFace(pair.Latin, style, weight, stretch, out var face) && face.CharacterToGlyphMap.ContainsGlyph(codepoint))
        {
            typeface = new Typeface(new FontFamily(LatinFontCollection.CollectionKey + "#" + pair.Latin), style, weight, stretch);
            return true;
        }
        return SystemFonts.TryMatchCharacter(codepoint, style, weight, stretch, null,
            culture ?? CultureInfo.CurrentUICulture, out typeface);
    }

    private static bool IsKana(int codepoint) => codepoint is >= 0x3040 and <= 0x30FF or >= 0x31F0 and <= 0x31FF or >= 0xFF66 and <= 0xFF9F;

    internal static bool IsCjk(int codepoint) => codepoint is >= 0x2E80 and <= 0x2FFF or >= 0x3000 and <= 0x303F or
        >= 0x3100 and <= 0x312F or >= 0x31A0 and <= 0x31BF or >= 0x3400 and <= 0x4DBF or
        >= 0x4E00 and <= 0x9FFF or >= 0xF900 and <= 0xFAFF or >= 0xFE10 and <= 0xFE1F or
        >= 0xFE30 and <= 0xFE4F or >= 0xFF01 and <= 0xFF60 or >= 0x20000 and <= 0x323AF;

    internal static IEnumerable<string> ChineseFallbacks(CultureInfo? culture = null)
    {
        var name = (culture ?? CultureInfo.CurrentUICulture).Name;
        var traditional = name.StartsWith("zh-TW", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("zh-HK", StringComparison.OrdinalIgnoreCase) || name.StartsWith("zh-Hant", StringComparison.OrdinalIgnoreCase);
        if (traditional) yield return "Microsoft JhengHei UI";
        yield return "Microsoft YaHei UI";
        yield return "Microsoft YaHei";
        yield return "DengXian";
        yield return "SimSun";
        yield return "SimSun-ExtB";
        yield return "SimSun-ExtG";
        yield return "Noto Sans CJK SC";
        yield return "Noto Sans SC";
        if (!traditional) yield return "Microsoft JhengHei UI";
    }

    internal static bool TryFace(string name, FontStyle style, FontWeight weight, FontStretch stretch,
        [NotNullWhen(true)] out GlyphTypeface? face)
    {
        var found = name == "Inter"
            ? FontManager.Current.TryGetGlyphTypeface(new Typeface(new FontFamily("fonts:Inter#Inter"), style, weight, stretch), out face)
            : SystemFonts.TryGetGlyphTypeface(name, style, weight, stretch, out face);
        if (!found || face is null) return false;
        // 某些变量/集合字体被系统注册成 Thin 或 Heavy，不能冒充普通界面字重。
        if (weight == FontWeight.Normal && face.Weight is < FontWeight.Light or > FontWeight.Medium)
        {
            face = null;
            return false;
        }
        return true;
    }

    public bool TryGetFamilyTypefaces(string familyName, [NotNullWhen(true)] out IReadOnlyList<Typeface>? typefaces)
    {
        if (_pairs.TryGetValue(familyName, out var pair)) return SystemFonts.TryGetFamilyTypefaces(pair.Latin, out typefaces);
        typefaces = null;
        return false;
    }
    public bool TryCreateSyntheticGlyphTypeface(GlyphTypeface face, FontStyle style, FontWeight weight, FontStretch stretch, [NotNullWhen(true)] out GlyphTypeface? synthetic) =>
        SystemFonts.TryCreateSyntheticGlyphTypeface(face, style, weight, stretch, out synthetic);
    public bool TryGetNearestMatch(string familyName, FontStyle style, FontWeight weight, FontStretch stretch, [NotNullWhen(true)] out GlyphTypeface? face) =>
        TryGetGlyphTypeface(familyName, style, weight, stretch, out face);
    public IEnumerator<FontFamily> GetEnumerator() => ((IEnumerable<FontFamily>)Array.Empty<FontFamily>()).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public void Dispose() { }
}
