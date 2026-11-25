using System.Globalization;
using System.Text;

namespace Fufucode.Fui;

internal sealed record FuiKeyAction(string Id, string Label, string DefaultChord, string Description);

internal readonly record struct FuiKeyChord(ConsoleKey Key, ConsoleModifiers Modifiers)
{
    internal bool Matches(ConsoleKeyInfo key) =>
        key.Key == Key && Normalize(key.Modifiers) == Modifiers;

    internal string Format()
    {
        var parts = new List<string>(3);
        if (Modifiers.HasFlag(ConsoleModifiers.Control)) parts.Add("ctrl");
        if (Modifiers.HasFlag(ConsoleModifiers.Shift)) parts.Add("shift");
        if (Modifiers.HasFlag(ConsoleModifiers.Alt)) parts.Add("alt");
        parts.Add(KeyName(Key));
        return string.Join('+', parts);
    }

    internal string FormatDisplay() => string.Join("+", Format().Split('+')
        .Select(part => part switch
        {
            "ctrl" => "Ctrl",
            "shift" => "Shift",
            "alt" => "Alt",
            _ => part.ToUpperInvariant()
        }));

    internal static ConsoleModifiers Normalize(ConsoleModifiers modifiers) =>
        modifiers & (ConsoleModifiers.Control | ConsoleModifiers.Shift | ConsoleModifiers.Alt);

    internal static bool TryParse(string? text, out FuiKeyChord chord, out string? error)
    {
        chord = default;
        error = null;
        var value = text?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(value))
        {
            error = "按键不能为空。";
            return false;
        }

        var pieces = value.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (pieces.Length == 0 || pieces.Length > 3)
        {
            error = "按键格式应为 ctrl+p、alt+f4 或 f8。";
            return false;
        }

        var modifiers = ConsoleModifiers.None;
        for (var i = 0; i < pieces.Length - 1; i++)
        {
            switch (pieces[i])
            {
                case "ctrl" or "control":
                    if (modifiers.HasFlag(ConsoleModifiers.Control))
                    {
                        error = "按键包含重复的 Ctrl。";
                        return false;
                    }
                    modifiers |= ConsoleModifiers.Control;
                    break;
                case "shift":
                    if (modifiers.HasFlag(ConsoleModifiers.Shift))
                    {
                        error = "按键包含重复的 Shift。";
                        return false;
                    }
                    modifiers |= ConsoleModifiers.Shift;
                    break;
                case "alt":
                    if (modifiers.HasFlag(ConsoleModifiers.Alt))
                    {
                        error = "按键包含重复的 Alt。";
                        return false;
                    }
                    modifiers |= ConsoleModifiers.Alt;
                    break;
                default:
                    error = $"未知修饰键：{pieces[i]}。";
                    return false;
            }
        }

        var baseName = pieces[^1];
        ConsoleKey key;
        if (baseName.Length == 1 && baseName[0] is >= 'a' and <= 'z')
        {
            key = ConsoleKey.A + (baseName[0] - 'a');
            if (!modifiers.HasFlag(ConsoleModifiers.Control) && !modifiers.HasFlag(ConsoleModifiers.Alt))
            {
                error = "安全动作必须使用 Ctrl+字母、Alt+字母或功能键。";
                return false;
            }
            if (modifiers.HasFlag(ConsoleModifiers.Control) && modifiers.HasFlag(ConsoleModifiers.Alt))
            {
                error = "不支持同时使用 Ctrl 和 Alt；部分终端无法可靠区分。";
                return false;
            }
        }
        else if (baseName is ['f', var digits] && char.IsAsciiDigit(digits))
        {
            var number = digits - '0';
            if (number is < 4 or > 12)
            {
                error = "安全动作只能绑定 F4–F12。";
                return false;
            }
            key = (ConsoleKey)((int)ConsoleKey.F1 + number - 1);
        }
        else
        {
            error = "安全动作只支持字母或 F4–F12。";
            return false;
        }

        chord = new(key, modifiers);
        return true;
    }

    internal static FuiKeyChord FromKey(ConsoleKeyInfo key) =>
        new(key.Key, Normalize(key.Modifiers));

    internal static bool TryFromKey(ConsoleKeyInfo key, out FuiKeyChord chord, out string? error)
    {
        chord = FromKey(key);
        error = null;
        if (!chord.IsSafeShape(out error)) return false;
        return true;
    }

    internal bool IsSafeShape(out string? error)
    {
        error = null;
        if (Key is >= ConsoleKey.A and <= ConsoleKey.Z)
        {
            if (!Modifiers.HasFlag(ConsoleModifiers.Control) && !Modifiers.HasFlag(ConsoleModifiers.Alt))
            {
                error = "安全动作必须使用 Ctrl+字母、Alt+字母或功能键。";
                return false;
            }
            if (Modifiers.HasFlag(ConsoleModifiers.Control) && Modifiers.HasFlag(ConsoleModifiers.Alt))
            {
                error = "不支持同时使用 Ctrl 和 Alt；部分终端无法可靠区分。";
                return false;
            }
            return true;
        }
        if (Key is >= ConsoleKey.F4 and <= ConsoleKey.F12) return true;
        error = "安全动作只支持字母或 F4–F12。";
        return false;
    }

    private static string KeyName(ConsoleKey key) =>
        key is >= ConsoleKey.F1 and <= ConsoleKey.F24
            ? "f" + ((int)key - (int)ConsoleKey.F1 + 1).ToString(CultureInfo.InvariantCulture)
            : key is >= ConsoleKey.A and <= ConsoleKey.Z
                ? ((char)('a' + key - ConsoleKey.A)).ToString()
                : throw new ArgumentOutOfRangeException(nameof(key));
}

internal static class FuiKeyBindingCatalog
{
    internal const string Commands = "commands";
    internal const string SearchTranscript = "search";
    internal const string ToggleTools = "tools";
    internal const string ToggleThinking = "thoughts";

    internal static readonly FuiKeyAction[] All =
    [
        new(Commands, "打开命令面板", "ctrl+p", "随时打开可搜索的命令面板。"),
        new(SearchTranscript, "搜索 transcript", "ctrl+f", "按正文或角色搜索当前会话。"),
        new(ToggleTools, "切换工具展开", "ctrl+o", "显示或收起工具卡片正文。"),
        new(ToggleThinking, "切换思考显示", "ctrl+t", "显示或隐藏思考块。")
    ];

    private static readonly Dictionary<string, FuiKeyAction> ById =
        All.ToDictionary(action => action.Id, StringComparer.Ordinal);

    internal static IReadOnlyDictionary<string, string> DefaultBindings { get; } =
        new Dictionary<string, string>(All.ToDictionary(action => action.Id, action => action.DefaultChord),
            StringComparer.Ordinal);

    internal static FuiKeyAction? Find(string id) => ById.GetValueOrDefault(id);

    internal static bool TryResolve(IReadOnlyDictionary<string, string> bindings, string action,
        out FuiKeyChord chord, out string? error)
    {
        chord = default;
        error = null;
        if (!ById.TryGetValue(action, out var definition))
        {
            error = $"未知按键动作：{action}。";
            return false;
        }
        var value = bindings.TryGetValue(action, out var configured) ? configured : definition.DefaultChord;
        return FuiKeyChord.TryParse(value, out chord, out error);
    }

    internal static bool TryNormalizeBindings(IReadOnlyDictionary<string, string> source,
        out IReadOnlyDictionary<string, string> normalized, out string? error)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var action in All)
        {
            var value = source.TryGetValue(action.Id, out var configured) ? configured : action.DefaultChord;
            if (!FuiKeyChord.TryParse(value, out var chord, out error))
            {
                normalized = new Dictionary<string, string>();
                error = $"{action.Label}：{error}";
                return false;
            }
            if (IsReserved(chord))
            {
                normalized = new Dictionary<string, string>();
                error = $"{action.Label} 不能使用保留键 {chord.Format()}。";
                return false;
            }
            result[action.Id] = chord.Format();
        }

        foreach (var pair in result)
        {
            var duplicate = result.FirstOrDefault(other =>
                other.Key != pair.Key && string.Equals(other.Value, pair.Value, StringComparison.Ordinal));
            if (duplicate.Key is not null)
            {
                normalized = new Dictionary<string, string>();
                error = $"{ById[pair.Key].Label} 与 {ById[duplicate.Key].Label} 冲突：{pair.Value}。";
                return false;
            }
        }

        normalized = result;
        error = null;
        return true;
    }

    internal static bool TrySet(FuiSettings settings, string action, FuiKeyChord chord, out FuiSettings next,
        out string? error)
    {
        next = settings;
        if (!ById.TryGetValue(action, out var definition))
        {
            error = $"未知按键动作：{action}。";
            return false;
        }
        if (!chord.IsSafeShape(out error)) return false;
        if (IsReserved(chord))
        {
            error = $"{chord.Format()} 是输入、审批或会话控制保留键。";
            return false;
        }
        var text = chord.Format();
        foreach (var other in All)
        {
            if (other.Id == action) continue;
            if (TryResolve(settings.Keybindings, other.Id, out var current, out _) && current == chord)
            {
                error = $"与“{other.Label}”冲突：{text}。";
                return false;
            }
        }

        var bindings = new Dictionary<string, string>(settings.Keybindings, StringComparer.Ordinal)
        {
            [action] = text
        };
        next = settings with { Keybindings = bindings };
        error = null;
        return true;
    }

    internal static bool Matches(FuiSettings settings, ConsoleKeyInfo key, string action) =>
        TryResolve(settings.Keybindings, action, out var chord, out _) && chord.Matches(key);

    internal static string Help(FuiSettings settings)
    {
        var text = new StringBuilder();
        foreach (var action in All)
        {
            var key = TryResolve(settings.Keybindings, action.Id, out var chord, out _)
                ? chord.Format() : "无效";
            text.Append(key).Append(' ').Append(action.Label).Append('\n');
        }
        return text.ToString().TrimEnd();
    }

    private static bool IsReserved(FuiKeyChord chord)
    {
        var key = chord.Key;
        var modifiers = chord.Modifiers;
        if (key is ConsoleKey.Escape or ConsoleKey.Enter or ConsoleKey.Tab or ConsoleKey.PageUp or
            ConsoleKey.PageDown or ConsoleKey.UpArrow or ConsoleKey.DownArrow or ConsoleKey.LeftArrow or
            ConsoleKey.RightArrow or ConsoleKey.F1 or ConsoleKey.F2 or ConsoleKey.F3) return true;
        if (modifiers.HasFlag(ConsoleModifiers.Control) &&
            key is ConsoleKey.A or ConsoleKey.C or ConsoleKey.D or ConsoleKey.E or ConsoleKey.J or ConsoleKey.K or
                ConsoleKey.R or ConsoleKey.U or ConsoleKey.W or ConsoleKey.Y or ConsoleKey.Z) return true;
        if (modifiers.HasFlag(ConsoleModifiers.Alt) && key is ConsoleKey.Y or ConsoleKey.Z) return true;
        return false;
    }
}
