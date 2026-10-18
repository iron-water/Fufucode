using Fufucode.Core.Automation;

namespace Fufucode.Conv.Tests;

/// <summary>
/// CONV-1 durable crazy-inbox: one stable eventId is one message, a replay creates nothing, and the
/// consumer cursor only moves forward. Everything is proven against the real SQLite journal, not an
/// in-process cache.
/// </summary>
internal static class InboxChecks
{
    public static void Run(string root, Action<bool, string> check)
    {
        var scenario = ConvFixture.New(root, "inbox");
        using var store = new AutomationStore(scenario.Database);

        ConvMessageDraft Draft(string eventId, string recipient, string payload,
            long? revision = null, string? head = null) =>
            new(eventId, "project", "thread-1", "task-1", "run-1", revision, head, "sender-a",
                recipient, "delivery", payload, ConvFixture.Now());

        void Refused(Action action, string label)
        {
            try { action(); check(false, label + " (accepted)"); }
            catch (Exception) { check(true, label); }
        }

        var eventsBefore = store.ListEventsAfter(0).Count;
        var first = store.EnqueueMessage(Draft("conv/evt-1", "control", "{\"kind\":\"intent\"}",
            revision: 1, head: new string('a', 40)));
        check(first.Accepted && !first.Duplicate && first.Message is not null,
            "inbox: a first message is accepted");
        check(first.Message!.Sequence > 0 && first.Message.EventId == "conv/evt-1",
            "inbox: the accepted message carries its stable identity and order");
        check(store.ListEventsAfter(0).Count == eventsBefore + 1,
            "inbox: accepting a message appends exactly one durable event");

        var replay = store.EnqueueMessage(Draft("conv/evt-1", "control", "{\"kind\":\"intent\"}",
            revision: 1, head: new string('a', 40)));
        check(replay.Accepted && replay.Duplicate && replay.Message!.Sequence == first.Message.Sequence,
            "inbox: replaying the same eventId returns the stored message");
        check(store.CountMessages("control") == 1 &&
              store.ListEventsAfter(0).Count == eventsBefore + 1,
            "inbox: a replay writes no second row and no second event");

        Refused(() => store.EnqueueMessage(Draft("conv/evt-1", "control", "{\"kind\":\"changed\"}",
            revision: 1, head: new string('a', 40))),
            "inbox: the same eventId with different content is refused");
        Refused(() => store.EnqueueMessage(Draft("conv/evt-bad-json", "control", "not json")),
            "inbox: a payload that is not a JSON object is refused");
        Refused(() => store.EnqueueMessage(Draft("conv/evt-bad-head", "control", "{}", head: "abc1234")),
            "inbox: a head that is not a full SHA is refused");
        Refused(() => store.EnqueueMessage(Draft(" ", "control", "{}")),
            "inbox: an empty eventId is refused");

        for (var i = 2; i <= 5; i++)
            store.EnqueueMessage(Draft($"conv/evt-{i}", "control", $"{{\"index\":{i}}}"));
        store.EnqueueMessage(Draft("conv/evt-other", "observer", "{\"index\":99}"));
        check(store.CountMessages("control") == 5 && store.CountMessages("observer") == 1,
            "inbox: recipients see only their own messages");

        var page = store.ReadMessagePage("control", 2);
        check(page.Items.Count == 2 && page.HasMore && page.NextCursor == page.Items[^1].Sequence,
            "inbox: a bounded page reports that more messages follow");
        check(page.Items.Zip(page.Items.Skip(1)).All(pair => pair.First.Sequence < pair.Second.Sequence),
            "inbox: messages are read in strict order");
        var all = store.ReadMessages("control", 100);
        check(all.Count == 5 && all.Zip(all.Skip(1)).All(pair => pair.First.Sequence < pair.Second.Sequence),
            "inbox: the full read is ordered and complete");
        check(store.ReadMessages("observer", 100).Single().Recipient == "observer",
            "inbox: one recipient's read does not leak another's message");

        check(store.ReadMessageCursor("control") == 0, "inbox: a consumer starts with no cursor");
        store.AcknowledgeMessages("control", all[1].Sequence);
        check(store.ReadMessageCursor("control") == all[1].Sequence,
            "inbox: acknowledging advances the durable cursor");
        store.AcknowledgeMessages("control", all[1].Sequence);
        check(store.ReadMessageCursor("control") == all[1].Sequence,
            "inbox: acknowledging the same cursor again is idempotent");
        Refused(() => store.AcknowledgeMessages("control", all[0].Sequence),
            "inbox: a cursor can never move backwards");
        store.AcknowledgeMessages("control", all[^1].Sequence);
        check(store.ReadMessageCursor("control") == all[^1].Sequence,
            "inbox: the cursor advances to the newest consumed message");

        // A second store on the same file is a different process view: the durable rows, the
        // de-duplication and the cursor all survive without any in-memory state.
        using (var reopened = new AutomationStore(scenario.Database))
        {
            check(reopened.CountMessages("control") == 5 &&
                  reopened.ReadMessageCursor("control") == all[^1].Sequence,
                "inbox: a reopened store reads the same rows and cursor");
            var durableReplay = reopened.EnqueueMessage(Draft("conv/evt-1", "control",
                "{\"kind\":\"intent\"}", revision: 1, head: new string('a', 40)));
            check(durableReplay.Duplicate && reopened.CountMessages("control") == 5,
                "inbox: de-duplication survives a process restart");
        }
    }
}
