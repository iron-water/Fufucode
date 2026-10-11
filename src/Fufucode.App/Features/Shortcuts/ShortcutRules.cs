using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Input;

namespace Fufucode.App.Features.Shortcuts;

/// <summary>保留键规则被违反时给出的可见原因。严禁静默覆盖。</summary>
public sealed record ShortcutRuleViolation(string Reason, bool IsReserved);

/// <summary>
/// 保留键与组合键规则。「作用范围之外不生效」不叫保留；
/// 这里的保留指产品级承诺：中文输入法组合、终端输入优先级、
/// 与系统外壳一致的编辑语义，都不能被用户绑定或全局路由抢走。
/// </summary>
public static class ShortcutRules
{
    /// <summary>终端输入优先：这些键在终端范围内必须原样送达 shell，任何全局绑定都不得占用。</summary>
    private static readonly HashSet<Key> TerminalOwnedKeys =
    [
        Key.Enter, Key.Tab, Key.Escape, Key.Back, Key.Delete, Key.Up, Key.Down, Key.Left, Key.Right,
        Key.Home, Key.End, Key.PageUp, Key.PageDown, Key.Insert, Key.Space,
    ];

    /// <summary>中文输入法组合期间的确认/取消键：组合中一律不触发动作。</summary>
    private static readonly HashSet<Key> ImeCompositionKeys = [Key.Enter, Key.Space, Key.Escape];

    /// <summary>
    /// 固定文本编辑语义组合：只保留 Windows 约定的规范写法。
    /// 同一主键的其他修饰键组合（如 Ctrl+Shift+Z、Ctrl+Alt+X）不属固定语义，可自由绑定。
    /// </summary>
    private static readonly HashSet<string> TextEditingGestureTexts = new(StringComparer.Ordinal)
    {
        "Ctrl+A", "Ctrl+C", "Ctrl+V", "Ctrl+X", "Ctrl+Z", "Ctrl+Y",
    };

    /// <summary>Windows 系统外壳保留、应用不得占用的组合。</summary>
    private static readonly HashSet<string> ShellReservedGestureTexts = new(StringComparer.Ordinal)
    {
        "Ctrl+Alt+Delete", "Meta+L", "Meta+D", "Meta+Tab", "Meta+E", "Meta+R", "Alt+Tab", "Alt+Escape", "Alt+F4", "Ctrl+Escape",
    };

    public const string TerminalReservedReason = "终端输入优先：该键在终端范围内必须原样送达 shell，不能被按键绑定占用。";
    public const string ImeReservedReason = "中文输入法组合期间该键用于确认或取消输入，不能被按键绑定占用。";
    public const string BareKeyReason = "无修饰键的按键会抢占普通输入；请至少使用 Ctrl、Alt 或 Shift。";
    public const string EditKeyReason = "该键是固定文本编辑语义（复制、粘贴、撤销等），只在文本编辑范围可用。";
    public const string ShellReservedReason = "该组合由 Windows 系统外壳占用，应用不能绑定。";
    public const string ModifierOnlyReason = "修饰键本身不能作为按键绑定。";
    public const string NoneReason = "请先按下要绑定的按键组合。";

    /// <summary>
    /// 校验一个候选手势在给定指向下是否可用。<paramref name="scope"/> 为动作自身的作用范围，
    /// <paramref name="targetsTerminalInput"/> 表示该动作是否把按键送进终端输入流。
    /// 返回 null 表示通过。
    /// </summary>
    public static ShortcutRuleViolation? Validate(ShortcutGesture gesture, ShortcutScope scope, bool targetsTerminalInput)
    {
        ArgumentNullException.ThrowIfNull(gesture);
        if (gesture.IsNone) return new(NoneReason, false);
        if (gesture.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin)
            return new(ModifierOnlyReason, true);
        if (ShellReservedGestureTexts.Contains(gesture.Text)) return new(ShellReservedReason, true);

        // 终端优先：终端范围内的全部保留键，以及任何会写进终端输入的动作。
        var terminalBound = scope == ShortcutScope.Terminal || targetsTerminalInput;
        if (terminalBound && TerminalOwnedKeys.Contains(gesture.Key) && !HasNonShiftModifier(gesture.Modifiers))
            return new(TerminalReservedReason, true);

        // 输入法组合键：全局/终端以外的范围也不允许无修饰绑定。
        if (ImeCompositionKeys.Contains(gesture.Key) && !HasNonShiftModifier(gesture.Modifiers))
            return new(ImeReservedReason, true);

        // 无修饰键：任何范围都不允许抢占普通输入。
        if (!HasNonShiftModifier(gesture.Modifiers))
            return new(BareKeyReason, false);

        // 固定编辑语义组合：仅文本编辑范围可绑定（Ctrl+Shift+Z、Ctrl+Alt+X 等不在此列）。
        if (TextEditingGestureTexts.Contains(gesture.Text) && scope != ShortcutScope.Editor)
            return new(EditKeyReason, true);

        return null;
    }

    private static bool HasNonShiftModifier(KeyModifiers modifiers) =>
        (modifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) != 0;

    /// <summary>当前手势是否落在输入法组合确认键上（供录制器在组合期间忽略按键）。</summary>
    public static bool IsImeCompositionKey(Key key) => ImeCompositionKeys.Contains(key);

    /// <summary>当前手势是否属于终端自有键（供全局处理器让路判断）。</summary>
    public static bool IsTerminalOwnedKey(Key key) => TerminalOwnedKeys.Contains(key);
}
