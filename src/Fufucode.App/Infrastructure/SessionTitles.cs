using System;
using System.Collections.Generic;
using System.Linq;
using Fufucode.App.Abstractions;
using Fufucode.Core;

namespace Fufucode.App.Infrastructure;

/// <summary>
/// 会话标题规则：
/// 1. 用户亲手改的名字最高优先（<see cref="SessionPresentation.NameUserSet"/>）；
/// 2. 没有用户消息时显示「新会话」，不再显示一串 id；
/// 3. 有用户消息且尚未自动取名时，按设置选择：子代理取名，或机械截取首条用户 prompt。
/// 自动取名只尝试一次；失败时退回机械命名，不会把 id 当作标题。
/// </summary>
public static class SessionTitles
{
    public const string NewSession = "新会话";
    private const int MechanicalLength = 16;

    /// <summary>列表标题：没有用户消息一律「新会话」；有消息时用会话名（自动或用户）。</summary>
    public static string Display(SessionRecord session) =>
        string.IsNullOrWhiteSpace(session.Name) ? NewSession : session.Name!;

    /// <summary>机械命名：取首条用户 prompt 的前若干字并加省略号。</summary>
    public static string Mechanical(string prompt)
    {
        var text = Collapse(prompt);
        if (text.Length == 0) return NewSession;
        return text.Length <= MechanicalLength ? text : text[..MechanicalLength].TrimEnd() + "…";
    }

    /// <summary>子代理取名的提示词：只要求返回一个短标题。</summary>
    public static string NamingPrompt(string prompt) =>
        "为下面这段用户请求起一个简短中文会话标题，不超过 14 个字，" +
        "只输出标题本身，不要引号、句号、编号、前缀或任何解释。\n\n用户请求：\n" + Collapse(prompt, 600);

    /// <summary>把子代理回复收敛成可用标题；不合格时返回 null。</summary>
    public static string? SanitizeGenerated(string? reply)
    {
        if (string.IsNullOrWhiteSpace(reply)) return null;
        var text = reply.Trim().Split('\n')[0].Trim();
        text = text.Trim('"', '“', '”', '\'', '「', '」', '《', '》', '*', '`', '-', ' ');
        if (text.StartsWith("标题", StringComparison.Ordinal))
            text = text["标题".Length..].TrimStart(':', '：', ' ');
        text = text.Trim();
        if (text.Length == 0) return null;
        // 纯数字/纯 id 形式不是标题（避免把会话 id 再取回来）。
        if (text.All(c => char.IsDigit(c) || c is ' ' or '-' or '_' or '.')) return null;
        return text.Length <= 24 ? text : text[..24].TrimEnd() + "…";
    }

    /// <summary>从历史消息里找第一条用户文本（只取文本块，忽略工具与附件）。</summary>
    public static string? FirstUserText(IEnumerable<ChatMessage> messages)
    {
        foreach (var message in messages)
        {
            if (message.Role != MessageRole.User) continue;
            var text = string.Join(' ', message.Blocks.OfType<TextBlock>().Select(block => block.Text));
            if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
        }
        return null;
    }

    private static string Collapse(string text, int max = 2000)
    {
        var collapsed = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length <= max ? collapsed : collapsed[..max];
    }
}
