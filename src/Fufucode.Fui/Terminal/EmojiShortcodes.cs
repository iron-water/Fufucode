namespace Fufucode.Fui;

internal readonly record struct EmojiShortcode(string Name, string Glyph);

internal static class EmojiShortcodes
{
    internal static readonly EmojiShortcode[] All =
    [
        new("smile", "😄"), new("grin", "😁"), new("joy", "😂"), new("laughing", "😆"),
        new("wink", "😉"), new("heart_eyes", "😍"), new("thinking", "🤔"), new("neutral_face", "😐"),
        new("cry", "😢"), new("sob", "😭"), new("angry", "😠"), new("rage", "😡"),
        new("thumbsup", "👍"), new("+1", "👍"), new("-1", "👎"), new("thumbsdown", "👎"),
        new("clap", "👏"), new("pray", "🙏"), new("wave", "👋"), new("ok_hand", "👌"),
        new("muscle", "💪"), new("eyes", "👀"), new("fire", "🔥"), new("sparkles", "✨"),
        new("star", "⭐"), new("heart", "❤️"), new("broken_heart", "💔"), new("blue_heart", "💙"),
        new("green_heart", "💚"), new("yellow_heart", "💛"), new("purple_heart", "💜"),
        new("orange_heart", "🧡"), new("white_heart", "🤍"), new("black_heart", "🖤"),
        new("100", "💯"), new("check", "✅"), new("heavy_check_mark", "✔️"), new("x", "❌"),
        new("warning", "⚠️"), new("rocket", "🚀"), new("tada", "🎉"), new("party", "🥳"),
        new("coffee", "☕"), new("beer", "🍺"), new("pizza", "🍕"), new("bug", "🐛"),
        new("computer", "💻"), new("bulb", "💡"), new("memo", "📝"), new("pin", "📌"),
        new("lock", "🔒"), new("unlock", "🔓"), new("zap", "⚡"), new("sun", "☀️"),
        new("moon", "🌙"), new("rainbow", "🌈"), new("cat", "🐱"), new("dog", "🐶"),
        new("monkey", "🐒"), new("robot", "🤖"), new("skull", "💀"), new("ghost", "👻"),
        new("poop", "💩"), new("brain", "🧠"), new("book", "📚"), new("rocket_ship", "🚀")
    ];

    internal static bool IsNameChar(char c) =>
        c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '+' or '-';

    internal static bool IsBoundary(string text, int colon)
    {
        if (colon == 0) return true;
        var c = text[colon - 1];
        return char.IsWhiteSpace(c) || c is '(' or '[' or '{' or '>' or '<' or '"' or '\'';
    }

    internal static EmojiShortcode? Find(string name) =>
        All.FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { Name.Length: > 0 } hit
            ? hit
            : null;

    internal static IReadOnlyList<EmojiShortcode> Suggest(string query, int maximum = 12)
    {
        if (query.Length == 0 || maximum <= 0) return [];
        return All.Where(item => item.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.Name.Length)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Take(maximum)
            .ToArray();
    }

    internal static bool TryTrigger(string text, int cursor, out int start, out int end, out string query)
    {
        start = end = -1;
        query = "";
        cursor = Math.Clamp(cursor, 0, text.Length);
        if (cursor == 0) return false;
        var close = cursor > 0 && text[cursor - 1] == ':';
        var nameEnd = close ? cursor - 1 : cursor;
        var nameStart = nameEnd;
        var limit = Math.Max(0, nameEnd - 42);
        while (nameStart > limit && IsNameChar(text[nameStart - 1])) nameStart--;
        if (nameEnd - nameStart > 42) return false;
        if (nameStart == nameEnd || nameStart == 0 || text[nameStart - 1] != ':') return false;
        var colon = nameStart - 1;
        if (!IsBoundary(text, colon)) return false;
        start = colon;
        end = cursor;
        query = text[nameStart..nameEnd];
        return true;
    }

    internal static string ExpandCompletedShortcodes(string text)
    {
        if (text.IndexOf(':') < 0) return text;
        System.Text.StringBuilder? result = null;
        var copied = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != ':' || !IsBoundary(text, i)) continue;
            var end = i + 1;
            while (end < text.Length && end - i <= 42 && IsNameChar(text[end])) end++;
            if (end == i + 1 || end >= text.Length || text[end] != ':') continue;
            var hit = Find(text[(i + 1)..end]);
            if (hit is null) continue;
            result ??= new(text.Length);
            result.Append(text, copied, i - copied).Append(hit.Value.Glyph);
            copied = end + 1;
            i = end;
        }
        return result is null ? text : result.Append(text, copied, text.Length - copied).ToString();
    }
}
