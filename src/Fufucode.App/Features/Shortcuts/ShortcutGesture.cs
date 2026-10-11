using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Avalonia.Input;

namespace Fufucode.App.Features.Shortcuts;

/// <summary>
/// 一次按键组合的规范化表示。文本形式为「修饰键+主键」，修饰键固定按
/// Ctrl → Alt → Shift → Meta 顺序输出（<c>Ctrl+Shift+P</c>），主键用
/// <see cref="Key"/> 名（<c>K</c>/<c>Enter</c>/<c>F5</c>/<c>OemComma</c>）。
/// </summary>
public sealed record ShortcutGesture
{
    private static readonly string[] ModifierOrder = ["Ctrl", "Alt", "Shift", "Meta"];

    public ShortcutGesture(Key key, KeyModifiers modifiers)
    {
        Key = key;
        Modifiers = modifiers & SupportedModifiers;
        Text = Format(Key, Modifiers);
    }

    public Key Key { get; }
    public KeyModifiers Modifiers { get; }

    /// <summary>规范化文本；空串只可能出现在 <see cref="None"/>。</summary>
    public string Text { get; }

    /// <summary>不绑定任何键。</summary>
    public static ShortcutGesture None { get; } = new(Key.None, KeyModifiers.None);

    public bool IsNone => Key == Key.None;

    private const KeyModifiers SupportedModifiers =
        KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift | KeyModifiers.Meta;

    private static string Format(Key key, KeyModifiers modifiers)
    {
        if (key == Key.None) return string.Empty;
        var builder = new StringBuilder();
        foreach (var name in ModifierOrder)
            if (modifiers.HasFlag(ModifierOf(name)))
                builder.Append(name).Append('+');
        builder.Append(key);
        return builder.ToString();
    }

    private static KeyModifiers ModifierOf(string name) => name switch
    {
        "Ctrl" => KeyModifiers.Control,
        "Alt" => KeyModifiers.Alt,
        "Shift" => KeyModifiers.Shift,
        _ => KeyModifiers.Meta,
    };

    public override string ToString() => Text;

    /// <summary>
    /// 解析用户输入或配置文件里的手势文本。接受大小写无关的修饰键名、
    /// 全角加号与中英文逗号，也接受「Ctrl+K / Ctrl+Shift+P」这类多绑定展示串
    /// （只取第一段）。非法输入返回 null，由调用方给出可见原因。
    /// </summary>
    public static ShortcutGesture? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var normalized = text
            .Replace('，', ',')
            .Replace('＋', '+')
            .Replace('K', 'K');
        var first = normalized.Split('/', 2, StringSplitOptions.TrimEntries)[0].Trim();
        if (first.Length == 0) return null;

        var modifiers = KeyModifiers.None;
        var parts = first.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;

        var keyPart = parts[^1];
        foreach (var modifier in parts[..^1])
        {
            switch (modifier.ToLowerInvariant())
            {
                case "ctrl" or "control" or "ctl": modifiers |= KeyModifiers.Control; break;
                case "alt": modifiers |= KeyModifiers.Alt; break;
                case "shift": modifiers |= KeyModifiers.Shift; break;
                case "meta" or "win" or "windows" or "super" or "cmd": modifiers |= KeyModifiers.Meta; break;
                default: return null;
            }
        }

        if (modifiers == KeyModifiers.None) return null;
        if (!Enum.TryParse<Key>(keyPart, ignoreCase: true, out var key)) return null;
        if (key is Key.None or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin or Key.LWin or Key.RWin)
            return null;

        return new ShortcutGesture(key, modifiers);
    }

    /// <summary>修饰键集合的展示文本；无修饰时返回空串。</summary>
    public static string ModifierText(KeyModifiers modifiers)
    {
        var names = ModifierOrder.Where(name => (modifiers & ModifierOf(name)) != 0).ToArray();
        return string.Join("+", names);
    }

    /// <summary>
    /// 记录状态下按下的修饰键集合。Alt 在 Avalonia 里按左右侧区分，
    /// 记录时必须合并，否则「Alt+K」会被记成含 Alt 但枚举比较失败。
    /// </summary>
    public static bool TryReadModifiers(Key key, KeyModifiers modifiers, out bool isModifierOnly)
    {
        isModifierOnly = key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.LWin or Key.RWin;
        foreach (var name in ModifierOrder)
            if (modifiers.HasFlag(ModifierOf(name))) return true;
        return isModifierOnly;
    }

    /// <summary>规范化比较：文本相同即视为同一手势。</summary>
    public bool SameAs(ShortcutGesture other) => string.Equals(Text, other.Text, StringComparison.Ordinal);

    public static IReadOnlyList<ShortcutGesture> ParseAll(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var result = new List<ShortcutGesture>();
        foreach (var segment in text.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            if (Parse(segment) is { } gesture && result.All(existing => !existing.SameAs(gesture)))
                result.Add(gesture);
        return result;
    }
}
