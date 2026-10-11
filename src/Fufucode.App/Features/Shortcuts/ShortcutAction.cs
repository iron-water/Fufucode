using System;
using System.Collections.Generic;
using System.Linq;

namespace Fufucode.App.Features.Shortcuts;

/// <summary>
/// 快捷键作用范围：同一手势在不同范围互不冲突。
/// Global 由窗口全局处理器消费；Chat/Browser/Terminal/Editor 只在对应控件
/// 持有焦点时生效——这也是「终端输入优先级不被全局快捷键抢走」的落点：
/// 终端范围绑定独立于全局范围，终端焦点下全局处理器先行让路。
/// </summary>
public enum ShortcutScope
{
    /// <summary>全局范围：窗口中任何位置（终端焦点除外）生效。</summary>
    Global,
    /// <summary>聊天页面：对话输入与消息操作。</summary>
    Chat,
    /// <summary>内置浏览器页面。</summary>
    Browser,
    /// <summary>终端停靠页。</summary>
    Terminal,
    /// <summary>文本编辑控件（复制、撤销等）。</summary>
    Editor,
}

public static class ShortcutScopeInfo
{
    public static string Title(this ShortcutScope scope) => scope switch
    {
        ShortcutScope.Global => "全局",
        ShortcutScope.Chat => "聊天",
        ShortcutScope.Browser => "浏览器",
        ShortcutScope.Terminal => "终端",
        _ => "文本编辑",
    };

    public static string Description(this ShortcutScope scope) => scope switch
    {
        ShortcutScope.Global => "窗口任意位置生效；终端输入焦点下让路给终端。",
        ShortcutScope.Chat => "聊天页面持有焦点时生效。",
        ShortcutScope.Browser => "内置浏览器页面持有焦点时生效。",
        ShortcutScope.Terminal => "终端页持有焦点时生效；优先于同手势的全局绑定。",
        _ => "文本框编辑动作。",
    };

    /// <summary>作用范围的稳定顺序，用于列表分组。</summary>
    public static int Rank(this ShortcutScope scope) => scope switch
    {
        ShortcutScope.Global => 0,
        ShortcutScope.Chat => 1,
        ShortcutScope.Browser => 2,
        ShortcutScope.Terminal => 3,
        _ => 4,
    };

    public static IReadOnlyList<ShortcutScope> All { get; } =
        [ShortcutScope.Global, ShortcutScope.Chat, ShortcutScope.Browser, ShortcutScope.Terminal, ShortcutScope.Editor];
}

/// <summary>
/// 一个可绑定的动作。由调用方从真实动作注册表投影出来——
/// 绝不另建第二份动作清单，动作 ID、标题、可用性都取注册表。
/// </summary>
/// <param name="Id">动作 ID，与动作注册表一致。</param>
/// <param name="Title">动作标题。</param>
/// <param name="Group">动作分组（注册表里的 Group）。</param>
/// <param name="Scope">作用范围。</param>
/// <param name="DefaultGestures">产品默认绑定；可含多个（如命令面板的双绑定）。</param>
/// <param name="Description">动作说明；与标题相同时调用方可传 null。</param>
/// <param name="Category">动作来源分类（全局 / 布局 / 会话 / 文本编辑 …），用于列表分组。</param>
public sealed record ShortcutAction(
    string Id,
    string Title,
    string Group,
    ShortcutScope Scope,
    IReadOnlyList<ShortcutGesture> DefaultGestures,
    string? Description = null,
    string? Category = null)
{
    public string CategoryOrGroup => Category ?? (Group.Length == 0 ? "其他" : Group);
}

/// <summary>动作的手势来源，决定界面上「恢复默认」是否可用。</summary>
public enum ShortcutOrigin
{
    /// <summary>沿用产品默认。</summary>
    Default,
    /// <summary>用户自定义。</summary>
    User,
    /// <summary>用户显式禁用（无绑定）。</summary>
    Disabled,
}
