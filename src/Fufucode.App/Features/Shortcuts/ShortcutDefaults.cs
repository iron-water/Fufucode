using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Input;

namespace Fufucode.App.Features.Shortcuts;

/// <summary>
/// 默认绑定表与范围分类：把当前产品里实际生效的按键行为固化成可配置文件。
/// 保守默认——这里列出的绑定与既有行为一致，用户不配置时行为不变。
/// 未列出的动作没有默认绑定（可在界面里自行指定）。
/// </summary>
public static class ShortcutDefaults
{
    /// <summary>明确把按键写进终端输入的动作，规则校验时按终端优先处理。</summary>
    private static readonly HashSet<string> TerminalInputActions = new(StringComparer.Ordinal)
    {
        "terminal", "terminal.copy", "terminal.paste", "terminal.interrupt", "terminal.new",
    };

    /// <summary>动作 ID → 作用范围。未列出的动作默认全局范围。</summary>
    private static readonly Dictionary<string, ShortcutScope> Scopes = new(StringComparer.Ordinal)
    {
        ["commands"] = ShortcutScope.Global,
        ["files"] = ShortcutScope.Global,
        ["toggle.bottom"] = ShortcutScope.Global,
        ["navigate.back"] = ShortcutScope.Global,
        ["session.search"] = ShortcutScope.Chat,
        ["session.new"] = ShortcutScope.Chat,
        ["session.side"] = ShortcutScope.Chat,
        ["browser"] = ShortcutScope.Browser,
        ["terminal"] = ShortcutScope.Terminal,
        ["edit.undo"] = ShortcutScope.Editor,
        ["edit.redo"] = ShortcutScope.Editor,
        ["edit.cut"] = ShortcutScope.Editor,
        ["edit.copy"] = ShortcutScope.Editor,
        ["edit.paste"] = ShortcutScope.Editor,
        ["edit.delete"] = ShortcutScope.Editor,
        ["edit.selectAll"] = ShortcutScope.Editor,
    };

    /// <summary>
    /// 动作 ID → 默认绑定。与当前源码中的硬编码行为一致：
    /// 命令面板 Ctrl+K / Ctrl+Shift+P、文件 Ctrl+P、底部面板 Ctrl+J、返回 Alt+左。
    /// </summary>
    private static readonly Dictionary<string, string[]> Bindings = new(StringComparer.Ordinal)
    {
        ["commands"] = ["Ctrl+K", "Ctrl+Shift+P"],
        ["files"] = ["Ctrl+P"],
        ["toggle.bottom"] = ["Ctrl+J"],
        ["navigate.back"] = ["Alt+Left"],
    };

    public static ShortcutScope ScopeOf(string id) =>
        Scopes.TryGetValue(id, out var scope) ? scope : ShortcutScope.Global;

    public static IReadOnlyList<ShortcutGesture> DefaultGestures(string id) =>
        Bindings.TryGetValue(id, out var texts)
            ? texts.Select(ShortcutGesture.Parse).OfType<ShortcutGesture>().ToArray()
            : [];

    public static bool TargetsTerminalInput(string id) => TerminalInputActions.Contains(id);
}
