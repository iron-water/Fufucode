using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Fufucode.Core.Automation;

public sealed partial class AutomationStore
{
    private static void MigrateControlReviewReopens(SqliteConnection c, SqliteTransaction tx) => Execute(c, tx, """
        CREATE TABLE IF NOT EXISTS control_review_reopens (
          reopen_id TEXT PRIMARY KEY, part_id TEXT NOT NULL REFERENCES delivery_parts(part_id),
          revision_number INTEGER NOT NULL, previous_decision_id TEXT NOT NULL UNIQUE REFERENCES part_decisions(decision_id),
          context_json TEXT NOT NULL, reason TEXT NOT NULL, decided_by TEXT NOT NULL, reopened_utc INTEGER NOT NULL);
        """);

    public ControlReconsiderReceipt ApplyControlReconsider(ControlReconsiderCommand command, long nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command.CommandId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.DecidedBy);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.TaskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.PartId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ExpectedDecisionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ExpectedEvidenceToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Reason);
        var digest = Sha("reconsider:" + JsonSerializer.Serialize(command));
        using var c = Open(); using var tx = c.BeginTransaction();
        using (var q = ControlSql(c, tx, "SELECT payload_hash,result_json FROM control_receipts WHERE command_id=$id", ("$id", command.CommandId)))
        using (var r = q.ExecuteReader())
            if (r.Read())
            {
                if (r.GetString(0) != digest) throw new InvalidOperationException("Reconsider identity reused with different input.");
                return JsonSerializer.Deserialize<ControlReconsiderReceipt>(r.GetString(1))!;
            }
        var inbox = ReadControlInbox(c, tx);
        if (inbox.Revision != command.ExpectedRevision) throw new InvalidOperationException("Refresh changed state before reopening review.");
        var item = inbox.Items.SingleOrDefault(i => i.Kind == "reconsider" && i.ObjectId == command.PartId);
        var part = ReadControlDecisionParts(c, tx, command.TaskId).SingleOrDefault(p => p.PartId == command.PartId);
        if (item is not { Actionable: true } || part is null || part.ReviewId != command.ExpectedDecisionId ||
            item.Version != command.ExpectedPartRevision || item.EvidenceToken != command.ExpectedEvidenceToken ||
            part.RunId != command.ExpectedRunId || part.HeadSha != command.ExpectedHeadSha || !ControlReviewAdmissible(c, tx, command.TaskId))
            throw new InvalidOperationException("Reopen only the current stopped, approved blocked/deferred review with exact provenance.");
        var threadId = ThreadOfTask(c, tx, command.TaskId);
        using (var q = ControlSql(c, tx, "SELECT status FROM threads WHERE thread_id=$id", ("$id", threadId)))
            if (Convert.ToInt32(q.ExecuteScalar()) == (int)ThreadStatus.Frozen)
                throw new InvalidOperationException("A frozen thread requires ownership reconciliation before reconsidering.");
        using (var q = ControlSql(c, tx, "SELECT COUNT(*) FROM integration_marks WHERE part_id=$id", ("$id", command.PartId)))
            if (Convert.ToInt64(q.ExecuteScalar()) != 0) throw new InvalidOperationException("Integrated evidence cannot be reopened.");
        using (var q = ControlSql(c, tx, "INSERT INTO control_review_reopens VALUES($id,$part,$v,$previous,$context,$reason,$actor,$now)",
            ("$id", command.CommandId), ("$part", command.PartId), ("$v", command.ExpectedPartRevision),
            ("$previous", command.ExpectedDecisionId), ("$context", item.ContextJson), ("$reason", command.Reason),
            ("$actor", command.DecidedBy), ("$now", nowUtc))) q.ExecuteNonQuery();
        UpdatePart(c, tx, command.PartId, DeliveryPartStatus.Delivered, command.ExpectedPartRevision, command.DecidedBy, nowUtc);
        RefreshTaskStatus(c, tx, command.TaskId);
        SetThreadStatus(c, tx, threadId, ThreadStatus.Active, null);
        var next = checked(inbox.Revision + 1);
        using (var q = ControlSql(c, tx, "UPDATE control_state SET revision=$next WHERE id=1 AND revision=$old", ("$next", next), ("$old", inbox.Revision)))
            if (q.ExecuteNonQuery() != 1) throw new InvalidOperationException("Concurrent review reopening.");
        var receipt = new ControlReconsiderReceipt(command.CommandId, next, command.TaskId, command.PartId, command.ExpectedPartRevision, command.ExpectedDecisionId);
        AppendEvent(c, tx, "control:" + command.CommandId, "control.review.reopened", threadId,
            command.TaskId, command.PartId, command.ExpectedRunId, JsonSerializer.Serialize(new { command, receipt }), nowUtc);
        using (var q = ControlSql(c, tx, "INSERT INTO control_receipts VALUES($id,$digest,$result)",
            ("$id", command.CommandId), ("$digest", digest), ("$result", JsonSerializer.Serialize(receipt)))) q.ExecuteNonQuery();
        tx.Commit(); return receipt;
    }
}
