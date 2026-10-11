using System;
using System.Collections.Generic;
using System.Linq;

namespace Fufucode.App.Features.Shortcuts;

/// <summary>一个手势被多个动作占用时的冲突记录，必须同时列出双方动作。</summary>
public sealed record ShortcutConflict(ShortcutGesture Gesture, ShortcutScope Scope, IReadOnlyList<string> ActionIds)
{
    public string GestureText => Gesture.Text;
}

/// <summary>
/// 冲突检测。同一手势在**同一作用范围**内被多个动作占用才算冲突；
/// 不同范围（例如全局 Ctrl+P 与终端 Ctrl+P）互不冲突。
/// 结果必须是完整的双方列表，界面不得静默覆盖。
/// </summary>
public static class ShortcutConflicts
{
    /// <summary>
    /// 检测一组「动作 → 生效绑定」的冲突。<paramref name="effective"/> 必须是
    /// 已合并用户改动与默认值后的最终绑定表。
    /// </summary>
    public static IReadOnlyList<ShortcutConflict> Detect(
        IEnumerable<(ShortcutAction Action, IReadOnlyList<ShortcutGesture> Gestures)> effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        var buckets = new Dictionary<(string Gesture, ShortcutScope Scope), List<string>>();
        var order = new List<(string Gesture, ShortcutScope Scope)>();

        foreach (var (action, gestures) in effective)
            foreach (var gesture in gestures)
            {
                if (gesture.IsNone) continue;
                var key = (gesture.Text, action.Scope);
                if (!buckets.TryGetValue(key, out var owners))
                {
                    owners = [];
                    buckets[key] = owners;
                    order.Add(key);
                }
                if (!owners.Contains(action.Id, StringComparer.Ordinal)) owners.Add(action.Id);
            }

        var conflicts = new List<ShortcutConflict>();
        foreach (var key in order)
        {
            var owners = buckets[key];
            if (owners.Count < 2) continue;
            var gesture = ShortcutGesture.Parse(key.Gesture) ?? ShortcutGesture.None;
            conflicts.Add(new ShortcutConflict(gesture, key.Scope, owners));
        }
        return conflicts;
    }

    /// <summary>
    /// 只针对一个待落定的手势检测冲突，用于编辑时给出「与哪些动作冲突」。
    /// 与 <see cref="Detect"/> 使用**同一范围规则**：只有同范围的动作才算冲突，
    /// 跨范围允许。候选手势所属范围由 <paramref name="candidateScope"/> 指定。
    /// <paramref name="exceptActionId"/> 是要改绑的动作自身，不参与冲突。
    /// </summary>
    public static IReadOnlyList<ShortcutConflict> DetectFor(
        IEnumerable<(ShortcutAction Action, IReadOnlyList<ShortcutGesture> Gestures)> effective,
        ShortcutGesture candidate,
        ShortcutScope candidateScope,
        string exceptActionId)
    {
        ArgumentNullException.ThrowIfNull(effective);
        if (candidate.IsNone) return [];
        var owners = new List<string>();
        foreach (var (action, gestures) in effective)
        {
            if (string.Equals(action.Id, exceptActionId, StringComparison.Ordinal)) continue;
            if (action.Scope != candidateScope) continue;
            if (gestures.Any(gesture => gesture.SameAs(candidate)) && !owners.Contains(action.Id, StringComparer.Ordinal))
                owners.Add(action.Id);
        }
        return owners.Count == 0 ? [] : [new ShortcutConflict(candidate, candidateScope, owners)];
    }
}
