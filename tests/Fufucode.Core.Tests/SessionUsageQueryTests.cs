using System.Text.Json;
using Fufucode.Core;
using Fufucode.Pi;

public static class SessionUsageQueryTests
{
    public static void Run()
    {
        static void Check(bool condition, string name)
        {
            if (!condition) throw new Exception(name);
            Console.WriteLine("PASS " + name);
        }
        static PiMessage Message(string role, PiUsage? usage = null) => new(role, [], null, null, usage, default);
        static PiUsage Usage(long? input, long? output, long? total, long? read = 0, long? write = 0) =>
            new(input, output, read, write, total, default);

        var empty = SessionUsageQuery.Query([]);
        Check(empty.Session.TotalTokens.Value is null && empty.Session.Input.KnownSubtotal is null &&
            empty.Session.AssistantMessageCount == 0 && empty.Turns.Count == 0,
            "A8 empty session has unknown usage, not invented zero");

        var messages = new[] { Message("user"), Message("assistant", Usage(10, 2, 19, 7)),
            Message("toolResult", Usage(999, 999, 999)), Message("assistant", Usage(20, 3, 23)),
            Message("user"), Message("assistant", Usage(4, 5, 9)) };
        var report = SessionUsageQuery.Query(messages);
        Check(report.Session.TotalTokens.Value == 51 && report.Session.Input.Value == 34 &&
            report.Session.AssistantMessageCount == 3 && report.Turns.Count == 2 &&
            report.Turns[0].UserMessageIndex == 0 && report.Turns[0].Usage.TotalTokens.Value == 42 &&
            report.Turns[1].UserMessageIndex == 4 && report.Turns[1].Usage.TotalTokens.Value == 9,
            "A8 user anchors group tool-loop assistants and ignore non-assistant usage");
        Check(SessionUsageQuery.Query(messages).Session == report.Session,
            "A8 repeated read-only usage queries do not accumulate totals");

        var missing = SessionUsageQuery.Query([Message("assistant", Usage(10, 2, 12)),
            Message("assistant", Usage(null, 3, null, null))]);
        Check(missing.Session.Input is { Value: null, KnownSubtotal: 10, KnownMessageCount: 1, UnknownMessageCount: 1 } &&
            missing.Session.TotalTokens.Value is null && missing.Session.Output.Value == 5 &&
            missing.Session.CacheRead.Value is null && missing.Session.CacheWrite.Value == 0,
            "A8 missing fields invalidate corresponding totals but preserve known subtotal and measured zero");
        var absent = SessionUsageQuery.Query([Message("user"), Message("assistant")]);
        Check(absent.Session.TotalTokens is { Value: null, KnownSubtotal: null, UnknownMessageCount: 1 } &&
            absent.Turns[0].Usage.Input.Value is null,
            "A8 entirely absent usage remains unknown in session and turn");
        var noTotal = SessionUsageQuery.Query([Message("assistant", Usage(10, 2, null))]);
        Check(noTotal.Session.TotalTokens.Value is null && noTotal.Session.Input.Value == 10,
            "A8 missing provider total is not silently derived");
        var orphan = SessionUsageQuery.Query([Message("assistant", Usage(0, 0, 0)), Message("user")]);
        Check(orphan.Turns.Count == 2 && orphan.Turns[0].TurnNumber == 0 &&
            orphan.Turns[0].UserMessageIndex is null && orphan.Turns[0].Usage.TotalTokens.Value == 0 &&
            orphan.Turns[1].Usage.TotalTokens.Value is null,
            "A8 pre-user assistant uses turn zero and pending user turn remains unknown");

        var timeline = new MessageTimeline();
        timeline.Restore(messages);
        Check(timeline.GetUsage().Session == report.Session, "A8 restored timeline keeps usage projection");
        timeline.Restore(messages);
        Check(timeline.GetUsage().Session == report.Session, "A8 repeated history restore replaces rather than accumulates usage");
        var generation = Guid.NewGuid();
        var start = Message("assistant", Usage(100, 1, 101));
        timeline.Apply(new PiMessageEvent(generation, "message_start", start, default));
        var delta = new PiContentUpdate("text_delta", 0, "x", null, null, null, default);
        timeline.Apply(new PiMessageUpdateEvent(generation, delta, Usage(100, 2, 102), default));
        timeline.Apply(new PiMessageUpdateEvent(generation, delta, Usage(100, 2, 102), default));
        Check(timeline.GetUsage().Session == report.Session && timeline.GetUsage().ExcludedActiveAssistantCount == 1 &&
            timeline.GetUsage().Turns[^1].ExcludedActiveAssistantCount == 1,
            "A8 cumulative stream updates are excluded until final message and explicitly reported");
        timeline.Apply(new PiMessageEvent(generation, "message_end", Message("assistant", Usage(100, 3, 103)), default));
        Check(timeline.GetUsage().Session.TotalTokens.Value == 154 && timeline.GetUsage().Session.Output.Value == 13 &&
            timeline.GetUsage().ExcludedActiveAssistantCount == 0,
            "A8 authoritative final usage is counted exactly once after cumulative updates");

        using var history = JsonDocument.Parse("""{"role":"assistant","content":[],"usage":{"input":2147483648,"output":0,"cacheRead":0,"cacheWrite":0,"totalTokens":2147483648}}""");
        timeline.Restore([PiProtocol.ParseMessage(history.RootElement)]);
        Check(timeline.GetUsage().Session.TotalTokens.Value == 2147483648L,
            "A8 history usage query preserves Int64 provider counters before display mapper narrowing");

        using var branchesJson = JsonDocument.Parse("""
            {"leafId":"new","entries":[
              {"id":"model","parentId":null,"type":"model_change"},
              {"id":"root-user","parentId":"model","type":"message","message":{"role":"user","content":"root"}},
              {"id":"old","parentId":"root-user","type":"message","message":{"role":"assistant","content":[],"usage":{"input":8,"output":2,"cacheRead":0,"cacheWrite":0,"totalTokens":10}}},
              {"id":"second-user","parentId":"old","type":"message","message":{"role":"user","content":"next"}},
              {"id":"second","parentId":"second-user","type":"message","message":{"role":"assistant","content":[],"usage":{"input":16,"output":4,"cacheRead":0,"cacheWrite":0,"totalTokens":20}}},
              {"id":"new","parentId":"root-user","type":"message","message":{"role":"assistant","content":[],"usage":{"input":24,"output":6,"cacheRead":0,"cacheWrite":0,"totalTokens":30}}},
              {"id":"pending-user","parentId":"new","type":"message","message":{"role":"user","content":"pending"}}
            ]}
            """);
        var branches = new SessionHistory();
        branches.ApplyEntriesResponse(branchesJson.RootElement);
        var allBranches = SessionUsageQuery.QueryHistory(branches);
        Check(allBranches.Session.TotalTokens.Value == 60 && allBranches.Session.AssistantMessageCount == 3 &&
            allBranches.Turns.Count == 3 && allBranches.Turns[0].UserEntryId == "root-user" &&
            allBranches.Turns[0].UserMessageIndex == 0 && allBranches.Turns[0].Usage.TotalTokens.Value == 40 &&
            allBranches.Turns[1].UserEntryId == "second-user" && allBranches.Turns[1].UserMessageIndex == 2 &&
            allBranches.Turns[1].Usage.TotalTokens.Value == 20 && allBranches.Turns[2].Usage.TotalTokens.Value is null,
            "A8 persisted turns follow user ancestors across branches and retain unanswered user entries");
        Check(SessionUsageQuery.Query(branches.CurrentBranchMessages).Session.TotalTokens.Value == 30 &&
            SessionUsageQuery.QueryHistory(branches).Session == allBranches.Session,
            "A8 all-entry session usage differs from active branch without double-counting repeated queries");

        using var orphanJson = JsonDocument.Parse("""
            {"leafId":"orphan","entries":[
              {"id":"orphan","parentId":null,"type":"message","message":{"role":"assistant","content":[],"usage":{"totalTokens":7}}}
            ]}
            """);
        var orphanHistory = new SessionHistory();
        orphanHistory.ApplyEntriesResponse(orphanJson.RootElement);
        Check(SessionUsageQuery.QueryHistory(orphanHistory).Turns.Single() is
            { TurnNumber: 0, UserEntryId: null, UserMessageIndex: null, Usage.TotalTokens.Value: 7 },
            "A8 persisted assistant without user ancestor belongs to turn zero");

        using var cycleJson = JsonDocument.Parse("""
            {"leafId":"root","entries":[
              {"id":"root","parentId":null,"type":"message","message":{"role":"user","content":"root"}},
              {"id":"cycle-a","parentId":"cycle-b","type":"message","message":{"role":"assistant","content":[]}},
              {"id":"cycle-b","parentId":"cycle-a","type":"custom"}
            ]}
            """);
        var cycle = new SessionHistory();
        cycle.ApplyEntriesResponse(cycleJson.RootElement);
        var rejected = false;
        try { SessionUsageQuery.QueryHistory(cycle); }
        catch (PiRpcException) { rejected = true; }
        Check(rejected, "A8 inactive branch ancestry cycle is rejected without hanging");
    }
}
