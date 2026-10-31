using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using Avalonia.Media;
using Avalonia.Media.Fonts;

namespace Fufucode.App.Infrastructure;

/// <summary>英文候选只匹配本字体的字形，避免系统提前为汉字挑选其它回退字体。</summary>
internal sealed class LatinFontCollection : IFontCollection
{
    internal static readonly Uri CollectionKey = new("fonts:FufucodeLatin");
    public Uri Key => CollectionKey;
    public int Count => 0;
    public FontFamily this[int index] => throw new ArgumentOutOfRangeException(nameof(index));
    private static IFontCollection SystemFonts => FontManager.Current.SystemFonts;

    public bool TryGetGlyphTypeface(string familyName, FontStyle style, FontWeight weight, FontStretch stretch, out GlyphTypeface glyphTypeface)
    {
        if (familyName == "Inter")
            return FontManager.Current.TryGetGlyphTypeface(new Typeface(new FontFamily("fonts:Inter#Inter"), style, weight, stretch), out glyphTypeface!);
        return SystemFonts.TryGetGlyphTypeface(familyName, style, weight, stretch, out glyphTypeface!);
    }

    public bool TryMatchCharacter(int codepoint, FontStyle style, FontWeight weight, FontStretch stretch,
        string? familyName, CultureInfo? culture, out Typeface typeface)
    {
        if (familyName is not null && TryGetGlyphTypeface(familyName, style, weight, stretch, out var face) &&
            face.CharacterToGlyphMap.ContainsGlyph(codepoint))
        {
            typeface = new Typeface(new FontFamily(CollectionKey + "#" + familyName), style, weight, stretch);
            return true;
        }
        typeface = default;
        return false;
    }

    public bool TryGetFamilyTypefaces(string familyName, [NotNullWhen(true)] out IReadOnlyList<Typeface>? typefaces) =>
        SystemFonts.TryGetFamilyTypefaces(familyName, out typefaces);
    public bool TryCreateSyntheticGlyphTypeface(GlyphTypeface face, FontStyle style, FontWeight weight, FontStretch stretch, [NotNullWhen(true)] out GlyphTypeface? synthetic) =>
        SystemFonts.TryCreateSyntheticGlyphTypeface(face, style, weight, stretch, out synthetic);
    public bool TryGetNearestMatch(string familyName, FontStyle style, FontWeight weight, FontStretch stretch, [NotNullWhen(true)] out GlyphTypeface? face) =>
        TryGetGlyphTypeface(familyName, style, weight, stretch, out face);
    public IEnumerator<FontFamily> GetEnumerator() => ((IEnumerable<FontFamily>)Array.Empty<FontFamily>()).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public void Dispose() { }
}
