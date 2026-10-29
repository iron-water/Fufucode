namespace Fufucode.Core.Backend;

/// <summary>
/// 把线性消息历史切分为不可拆分的完整组：一条 Assistant 的 tool_calls 与其后紧邻的
/// 全部 Tool 结果属于同一组；其它消息各自成组。
/// <para>
/// 不变量：组区间覆盖 [start, history.Count)，首尾相接且互不重叠；组只保存整数边界与字符数，
/// 不复制、不缓存任何 <see cref="PureAgentMessage"/> 或工具参数引用，调用结束后不滞留历史对象。
/// 空历史或 start == Count 返回空列表；仅 System 历史返回一个完整组。
/// </para>
/// <para>
/// <see cref="Group.Complete"/> 为 false 表示该组结果缺失、ToolCallId 未知/重复，
/// 或存在孤立 Tool 消息；消费方不得摘要或 fork 不完整组，以免生成不可重放的协议历史。
/// </para>
/// </summary>
public static class NativeHistoryGroups
{
    /// <summary>一个完整组：Start 含、End 不含；Characters 为内容与工具参数的字符总数。</summary>
    public readonly record struct Group(int Start, int End, long Characters, bool Complete);

    /// <summary>
    /// 从 <paramref name="start"/> 起枚举完整组。只读取输入列表，不修改它，也不保留其元素引用。
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="start"/> 超出 0..Count。</exception>
    public static IReadOnlyList<Group> Enumerate(IReadOnlyList<PureAgentMessage> history, int start = 0)
    {
        ArgumentNullException.ThrowIfNull(history);
        if (start < 0 || start > history.Count) throw new ArgumentOutOfRangeException(nameof(start));

        var groups = new List<Group>();
        for (var i = start; i < history.Count;)
        {
            var first = i++;
            var message = history[first];
            long size = SizeOf(message);
            var complete = message.Role != PureAgentRole.Tool;
            if (message.Role == PureAgentRole.Assistant && message.ToolCalls is { Count: > 0 } calls)
            {
                var expected = calls.Select(call => call.Id).ToHashSet(StringComparer.Ordinal);
                complete = expected.Count == calls.Count;
                while (i < history.Count && history[i].Role == PureAgentRole.Tool)
                {
                    var result = history[i++];
                    size += SizeOf(result);
                    if (result.ToolCallId is null || !expected.Remove(result.ToolCallId)) complete = false;
                }
                complete &= expected.Count == 0;
            }
            groups.Add(new Group(first, i, size, complete));
        }
        return groups;
    }

    /// <summary>单条消息参与预算的字符数；与压缩预算口径一致，不序列化、不分配副本。</summary>
    public static long SizeOf(PureAgentMessage message) =>
        (long)(message.Content?.Length ?? 0) +
        (message.ReasoningContent?.Length ?? 0) +
        (message.ToolCalls?.Sum(call => (long)call.ArgumentsJson.Length) ?? 0);
}
