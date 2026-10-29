using Fufucode.Core.Backend;

namespace Fufucode.NativeCore.Tests;

/// <summary>Case 06 helper: complete tool-call groups, incomplete groups, empty/system-only safety.</summary>
internal static class GroupBoundaryTests
{
    public static void Run(Checks checks)
    {
        checks.Equal(0, NativeHistoryGroups.Enumerate([]).Count, "empty history has no groups");

        var system = new PureAgentMessage(PureAgentRole.System, "sys");
        var systemGroups = NativeHistoryGroups.Enumerate([system]);
        checks.Equal(1, systemGroups.Count, "system-only history is one group");
        checks.Check(systemGroups[0] is { Start: 0, End: 1, Complete: true }, "system group spans the only message");

        var assistant = new PureAgentMessage(PureAgentRole.Assistant, "call",
            ToolCalls: [new PureToolCall("a", "read_file", "{}"), new PureToolCall("b", "grep_search", "{}")]);
        var resultA = new PureAgentMessage(PureAgentRole.Tool, "ra", ToolCallId: "a");
        var resultB = new PureAgentMessage(PureAgentRole.Tool, "rb", ToolCallId: "b");
        var user = new PureAgentMessage(PureAgentRole.User, "next");

        var complete = NativeHistoryGroups.Enumerate([system, assistant, resultA, resultB, user]);
        checks.Equal(3, complete.Count, "system + one tool group + user");
        checks.Check(complete[1] is { Start: 1, End: 4, Complete: true }, "assistant and both results are one complete group");
        checks.Equal((long)"call".Length + 2 + 2 + "ra".Length + "rb".Length, complete[1].Characters,
            "group characters include content and tool argument text");
        checks.Equal(0, complete[0].Start, "first group starts at zero");
        checks.Equal(5, complete[^1].End, "groups cover the full history");
        for (var i = 1; i < complete.Count; i++)
            checks.Equal(complete[i - 1].End, complete[i].Start, "groups are contiguous");

        var missing = NativeHistoryGroups.Enumerate([assistant, resultA, user]);
        checks.Check(!missing[0].Complete, "missing one tool result marks the group incomplete");
        checks.Equal(2, missing.Count, "following user message remains its own group");
        checks.Equal(2, missing[1].Start, "incomplete group still ends before the user message");

        var unknownId = new PureAgentMessage(PureAgentRole.Tool, "x", ToolCallId: "zzz");
        checks.Check(!NativeHistoryGroups.Enumerate([assistant, resultA, resultB, unknownId])[0].Complete,
            "unknown tool result id marks the group incomplete");

        checks.Check(!NativeHistoryGroups.Enumerate([new PureAgentMessage(PureAgentRole.Tool, "orphan", ToolCallId: "a")])[0]
            .Complete, "orphan tool result is never a complete group");

        var duplicateCalls = new PureAgentMessage(PureAgentRole.Assistant, "",
            ToolCalls: [new PureToolCall("a", "read_file", "{}"), new PureToolCall("a", "read_file", "{}")]);
        checks.Check(!NativeHistoryGroups.Enumerate([duplicateCalls, resultA])[0].Complete,
            "duplicate tool call ids are incomplete");

        var offset = NativeHistoryGroups.Enumerate([system, assistant, resultA, resultB, user], start: 1);
        checks.Equal(2, offset.Count, "start offset skips earlier groups");
        checks.Equal(1, offset[0].Start, "offset group keeps absolute indices");

        checks.Throws<ArgumentOutOfRangeException>(() => NativeHistoryGroups.Enumerate([system], start: 2),
            "start beyond history is rejected");
        checks.Throws<ArgumentOutOfRangeException>(() => NativeHistoryGroups.Enumerate([system], start: -1),
            "negative start is rejected");
    }
}
