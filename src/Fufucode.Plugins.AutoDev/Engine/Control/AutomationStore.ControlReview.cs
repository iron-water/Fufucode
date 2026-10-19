using System.Text.Json;

namespace Fufucode.Core.Automation;

public sealed partial class AutomationStore
{
    public ControlReviewReceipt ApplyControlReview(ControlReviewCommand command, long nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command.CommandId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.DecidedBy);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.TaskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.PartId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ExpectedRunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ExpectedHeadSha);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ExpectedEvidenceToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.EvidenceJson);
        if (!Enum.IsDefined(command.Decision)) throw new ArgumentOutOfRangeException(nameof(command.Decision));
        if (command.Decision != AutomationDecision.Accept && string.IsNullOrWhiteSpace(command.Findings))
            throw new ArgumentException("A non-accept review requires explicit findings or a reason.");
        using (var evidence = JsonDocument.Parse(command.EvidenceJson))
            if (evidence.RootElement.ValueKind != JsonValueKind.Object || !evidence.RootElement.EnumerateObject().Any())
                throw new ArgumentException("Review evidence must be a nonempty JSON object.");
        var digest = Sha("review:" + JsonSerializer.Serialize(command));
        using var c = Open(); using var tx = c.BeginTransaction();
        // A lost response can be replayed after the part is accepted and the inbox changes.
        using (var q = ControlSql(c, tx, "SELECT payload_hash,result_json FROM control_receipts WHERE command_id=$id", ("$id", command.CommandId)))
        using (var r = q.ExecuteReader())
            if (r.Read())
            {
                if (r.GetString(0) != digest) throw new InvalidOperationException("Review identity reused with different input.");
                return JsonSerializer.Deserialize<ControlReviewReceipt>(r.GetString(1))!;
            }
        var inbox = ReadControlInbox(c, tx);
        if (inbox.Revision != command.ExpectedRevision)
            throw new InvalidOperationException("Refresh the changed control state before reviewing.");
        var item = inbox.Items.SingleOrDefault(i => i.Kind == "review" && i.ObjectId == command.PartId);
        if (item is not { Actionable: true } || item.Version != command.ExpectedPartRevision ||
            item.EvidenceToken != command.ExpectedEvidenceToken || !ControlReviewAdmissible(c, tx, command.TaskId))
            throw new InvalidOperationException("Review only the current approved, stopped delivery and its exact evidence.");
        var part = ReadControlDecisionParts(c, tx, command.TaskId).SingleOrDefault(p => p.PartId == command.PartId);
        if (part is null) throw new InvalidOperationException("Review task and part do not match.");
        RecordDecision(c, tx, "control-review:" + command.CommandId, command.PartId, command.Decision,
            command.Findings, command.EvidenceJson, command.DecidedBy, "review:" + command.CommandId,
            nowUtc, command.ExpectedRunId, command.ExpectedHeadSha, command.ExpectedPartRevision);
        var next = checked(inbox.Revision + 1);
        using (var q = ControlSql(c, tx, "UPDATE control_state SET revision=$next WHERE id=1 AND revision=$old", ("$next", next), ("$old", inbox.Revision)))
            if (q.ExecuteNonQuery() != 1) throw new InvalidOperationException("Concurrent review decision.");
        var receipt = new ControlReviewReceipt(command.CommandId, next, command.TaskId, command.PartId,
            command.ExpectedPartRevision, command.ExpectedRunId, command.ExpectedHeadSha, command.Decision);
        AppendEvent(c, tx, "control:" + command.CommandId, "control.review.applied", ThreadOfTask(c, tx, command.TaskId),
            command.TaskId, command.PartId, command.ExpectedRunId, JsonSerializer.Serialize(new { command, receipt }), nowUtc);
        using (var q = ControlSql(c, tx, "INSERT INTO control_receipts VALUES($id,$digest,$result)",
            ("$id", command.CommandId), ("$digest", digest), ("$result", JsonSerializer.Serialize(receipt)))) q.ExecuteNonQuery();
        tx.Commit(); return receipt;
    }
}
