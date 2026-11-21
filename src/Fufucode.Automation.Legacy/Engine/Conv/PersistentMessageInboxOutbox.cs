using System.Text.Json;
using Fufucode.App.Abstractions.Automation.Harnesses;
using Microsoft.Data.Sqlite;

namespace Fufucode.Core.Automation;

/// <summary>
/// One durable cross-thread message. Identity is bound to project/thread/task/run, the delivery
/// revision and HEAD it refers to, and to the real sender; the order is the durable sequence.
/// </summary>
public sealed record ConvMessageEnvelope(string MessageId, string EventId, string ProjectId,
    string ThreadId, string? TaskId, string? RunId, long? DeliveryRevision, string? HeadSha,
    string Sender, string Recipient, string Kind, string PayloadJson, long OccurredUtc, long Sequence);

/// <summary>
/// A message as a sender presents it. <see cref="EventId"/> is the stable de-duplication identity;
/// the message id is derived from it so a caller cannot invent a second identity for one event.
/// </summary>
public sealed record ConvMessageDraft(string EventId, string ProjectId, string ThreadId,
    string? TaskId, string? RunId, long? DeliveryRevision, string? HeadSha, string Sender,
    string Recipient, string Kind, string PayloadJson, long OccurredUtc);

/// <summary>A bounded, ordered page of messages plus the cursor that follows it.</summary>
public sealed record ConvMessagePage(IReadOnlyList<ConvMessageEnvelope> Items, long NextCursor,
    bool HasMore);

/// <summary>What the durable inbox observed for one delivery attempt.</summary>
public sealed record ConvDeliveryResult(bool Accepted, bool Duplicate, string Code,
    ConvMessageEnvelope? Message, string Detail);

public sealed partial class AutomationStore
{
    /// <summary>
    /// CONV-1 durable inbox/outbox storage. Additive tables in the same project database: the U1
    /// schema version, tables and event journal are unchanged, and de-duplication is enforced by
    /// SQLite uniqueness inside the write transaction rather than by any in-process cache.
    /// </summary>
    private void MigrateConvInbox()
    {
        using var c = OpenConv();
        using var tx = c.BeginTransaction();
        using var q = ConvSql(c, tx, """
            CREATE TABLE IF NOT EXISTS conv_messages (
              message_id TEXT PRIMARY KEY,
              event_id TEXT NOT NULL UNIQUE,
              sequence INTEGER NOT NULL UNIQUE,
              project_id TEXT NOT NULL,
              thread_id TEXT NOT NULL,
              task_id TEXT,
              run_id TEXT,
              delivery_revision INTEGER,
              head_sha TEXT,
              sender TEXT NOT NULL,
              recipient TEXT NOT NULL,
              kind TEXT NOT NULL,
              payload_json TEXT NOT NULL,
              payload_hash TEXT NOT NULL,
              occurred_utc INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS idx_conv_messages_recipient ON conv_messages(recipient, sequence);
            CREATE TABLE IF NOT EXISTS conv_message_cursors (
              consumer TEXT PRIMARY KEY, cursor INTEGER NOT NULL, updated_utc INTEGER NOT NULL);
            """);
        q.ExecuteNonQuery();
        tx.Commit();
    }

    /// <summary>
    /// Enqueue one message in a single transaction with its U1 event. The same stable eventId with
    /// identical content is a replay that adds nothing; the same eventId with different content is
    /// refused, so a re-delivery can never silently replace a durable message.
    /// </summary>
    public ConvDeliveryResult EnqueueMessage(ConvMessageDraft draft)
    {
        foreach (var value in new[] { draft.EventId, draft.ProjectId, draft.ThreadId, draft.Sender,
            draft.Recipient, draft.Kind, draft.PayloadJson })
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (draft.DeliveryRevision is < 0)
            throw new ArgumentException("A delivery revision cannot be negative.", nameof(draft));
        if (draft.HeadSha is { } head && !GitCliEvidence.IsSha(head))
            throw new ArgumentException("A message head must be a full commit SHA.", nameof(draft));
        using (var document = JsonDocument.Parse(draft.PayloadJson))
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("A message payload must be one JSON object.", nameof(draft));
        var document2 = Canonical(draft);
        var messageId = "msg-" + Sha(draft.EventId);

        MigrateConvInboxOnce();
        using var c = OpenConv();
        using var tx = c.BeginTransaction();
        using (var probe = ConvSql(c, tx,
            "SELECT message_id,payload_hash,sequence FROM conv_messages WHERE event_id=$event",
            ("$event", draft.EventId)))
        using (var r = probe.ExecuteReader())
        {
            if (r.Read())
            {
                var previous = r.GetString(1);
                var sequence = r.GetInt64(2);
                if (previous != Sha(document2))
                    throw new InvalidOperationException(
                        $"Message event '{draft.EventId}' already exists with different content.");
                tx.Commit();
                return new(true, true, "duplicate", ReadMessage(messageId),
                    $"Event '{draft.EventId}' was already recorded at sequence {sequence}.");
            }
        }
        long next;
        using (var order = ConvSql(c, tx, "SELECT COALESCE(MAX(sequence),0)+1 FROM conv_messages"))
            next = Convert.ToInt64(order.ExecuteScalar());
        using (var insert = ConvSql(c, tx, """
            INSERT INTO conv_messages(message_id,event_id,sequence,project_id,thread_id,task_id,run_id,
                                      delivery_revision,head_sha,sender,recipient,kind,payload_json,
                                      payload_hash,occurred_utc)
            VALUES($message,$event,$sequence,$project,$thread,$task,$run,$revision,$head,$sender,
                   $recipient,$kind,$payload,$hash,$now)
            """, ("$message", messageId), ("$event", draft.EventId), ("$sequence", next),
            ("$project", draft.ProjectId), ("$thread", draft.ThreadId), ("$task", draft.TaskId),
            ("$run", draft.RunId), ("$revision", draft.DeliveryRevision), ("$head", draft.HeadSha),
            ("$sender", draft.Sender), ("$recipient", draft.Recipient), ("$kind", draft.Kind),
            ("$payload", document2), ("$hash", Sha(document2)), ("$now", draft.OccurredUtc)))
            insert.ExecuteNonQuery();
        AppendConvEvent(c, tx, draft.EventId, "conv.message", draft.ThreadId, draft.TaskId, null,
            draft.RunId, document2, draft.OccurredUtc);
        tx.Commit();
        return new(true, false, "queued", ReadMessage(messageId),
            $"Message recorded at sequence {next} for '{draft.Recipient}'.");
    }

    /// <summary>Read one recipient's messages in durable order, oldest first. Never mutates a cursor.</summary>
    public IReadOnlyList<ConvMessageEnvelope> ReadMessages(string consumer, int limit,
        long? afterSequence = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consumer);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        MigrateConvInboxOnce();
        using var c = OpenConv();
        using var q = ConvSql(c, null, """
            SELECT message_id,event_id,sequence,project_id,thread_id,task_id,run_id,delivery_revision,
                   head_sha,sender,recipient,kind,payload_json,occurred_utc
            FROM conv_messages WHERE recipient=$consumer AND ($after IS NULL OR sequence>$after)
            ORDER BY sequence LIMIT $limit
            """, ("$consumer", consumer), ("$after", afterSequence), ("$limit", limit));
        using var r = q.ExecuteReader();
        var items = new List<ConvMessageEnvelope>();
        while (r.Read()) items.Add(ReadEnvelope(r));
        return items;
    }

    /// <summary>A bounded page that reports whether unread messages remain after it.</summary>
    public ConvMessagePage ReadMessagePage(string consumer, int limit)
    {
        var items = ReadMessages(consumer, limit);
        var next = items.Count == 0 ? ReadMessageCursor(consumer) : items[^1].Sequence;
        return new(items, next, CountMessagesAfter(consumer, next) > 0);
    }

    /// <summary>
    /// Advance a consumer cursor. The cursor only moves toward newer messages; re-acknowledging the
    /// same or an older sequence is idempotent, and a backwards move is refused, so a crash cannot
    /// rewind consumption and re-dispatch what was already handled.
    /// </summary>
    public void AcknowledgeMessages(string consumer, long sequence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consumer);
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);
        MigrateConvInboxOnce();
        using var c = OpenConv();
        using var tx = c.BeginTransaction();
        long current;
        using (var read = ConvSql(c, tx, "SELECT cursor FROM conv_message_cursors WHERE consumer=$c",
            ("$c", consumer)))
            current = read.ExecuteScalar() is long value ? value : 0;
        if (sequence < current)
            throw new InvalidOperationException(
                $"Consumer '{consumer}' is already at {current}; the cursor never moves backwards.");
        if (sequence == current) { tx.Commit(); return; }
        using var update = ConvSql(c, tx, """
            INSERT INTO conv_message_cursors(consumer,cursor,updated_utc) VALUES($c,$cursor,$now)
            ON CONFLICT(consumer) DO UPDATE SET cursor=$cursor, updated_utc=$now
            """, ("$c", consumer), ("$cursor", sequence),
            ("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        update.ExecuteNonQuery();
        tx.Commit();
    }

    public long ReadMessageCursor(string consumer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consumer);
        MigrateConvInboxOnce();
        using var c = OpenConv();
        using var q = ConvSql(c, null, "SELECT cursor FROM conv_message_cursors WHERE consumer=$c",
            ("$c", consumer));
        return q.ExecuteScalar() is long value ? value : 0;
    }

    public long CountMessages(string recipient)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recipient);
        MigrateConvInboxOnce();
        using var c = OpenConv();
        using var q = ConvSql(c, null,
            "SELECT COUNT(*) FROM conv_messages WHERE recipient=$r", ("$r", recipient));
        return Convert.ToInt64(q.ExecuteScalar());
    }

    private long CountMessagesAfter(string recipient, long sequence)
    {
        using var c = OpenConv();
        using var q = ConvSql(c, null,
            "SELECT COUNT(*) FROM conv_messages WHERE recipient=$r AND sequence>$s",
            ("$r", recipient), ("$s", sequence));
        return Convert.ToInt64(q.ExecuteScalar());
    }

    private ConvMessageEnvelope ReadMessage(string messageId)
    {
        using var c = OpenConv();
        using var q = ConvSql(c, null, """
            SELECT message_id,event_id,sequence,project_id,thread_id,task_id,run_id,delivery_revision,
                   head_sha,sender,recipient,kind,payload_json,occurred_utc
            FROM conv_messages WHERE message_id=$id
            """, ("$id", messageId));
        using var r = q.ExecuteReader();
        if (!r.Read()) throw new KeyNotFoundException(messageId);
        return ReadEnvelope(r);
    }

    private static ConvMessageEnvelope ReadEnvelope(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(3), r.GetString(4),
        r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6),
        r.IsDBNull(7) ? null : r.GetInt64(7), r.IsDBNull(8) ? null : r.GetString(8),
        r.GetString(9), r.GetString(10), r.GetString(11), r.GetString(12), r.GetInt64(13), r.GetInt64(2));

    /// <summary>Canonical payload text used for the content hash, so a replay is byte-comparable.</summary>
    private static string Canonical(ConvMessageDraft draft) =>
        "{\"eventId\":\"" + Escape(draft.EventId) + "\",\"projectId\":\"" + Escape(draft.ProjectId) +
        "\",\"threadId\":\"" + Escape(draft.ThreadId) + "\",\"taskId\":\"" + Escape(draft.TaskId) +
        "\",\"runId\":\"" + Escape(draft.RunId) + "\",\"deliveryRevision\":" +
        (draft.DeliveryRevision?.ToString() ?? "null") + ",\"headSha\":\"" + Escape(draft.HeadSha) +
        "\",\"sender\":\"" + Escape(draft.Sender) + "\",\"recipient\":\"" + Escape(draft.Recipient) +
        "\",\"kind\":\"" + Escape(draft.Kind) + "\",\"payload\":" + draft.PayloadJson + "}";

    private static string Escape(string? value) => (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");

    private int _convInboxMigrated;

    private void MigrateConvInboxOnce()
    {
        if (Interlocked.Exchange(ref _convInboxMigrated, 1) == 1) return;
        MigrateConvInbox();
    }
}
