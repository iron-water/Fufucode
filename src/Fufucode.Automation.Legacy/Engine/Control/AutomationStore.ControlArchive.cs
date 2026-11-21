using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Fufucode.Core.Automation;

public sealed partial class AutomationStore
{
    public const int MaximumLegacyControlBytes = 8 * 1024 * 1024;

    /// <summary>
    /// Capture a schema-1 parallel_control.py document as immutable history. Keep its full source
    /// text, including groups, knowledge and claimed execution/decisions/integration, without
    /// replacing any U1 fact or creating a thread, task, approval, intent or queue row.
    /// </summary>
    public ControlArchiveReceipt ArchiveLegacyControl(LegacyControlArchiveCommand command, long nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command.CommandId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.DecidedBy);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.SourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Json);
        if (command.SourceRevision < 0 || Encoding.UTF8.GetByteCount(command.Json) > MaximumLegacyControlBytes)
            throw new ArgumentException("Legacy source exceeds its size or revision limit.");
        var contentHash = Sha(command.Json).ToLowerInvariant();
        if (!string.Equals(contentHash, command.ContentSha256, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Legacy source content does not match the declared UTF-8 text SHA256.");
        ValidateLegacyControl(command);
        var digest = Sha("legacy-archive:" + JsonSerializer.Serialize(command));
        using var c = Open();
        using var tx = c.BeginTransaction();
        using (var q = ControlSql(c, tx,
            "SELECT payload_hash,result_json FROM control_receipts WHERE command_id=$id", ("$id", command.CommandId)))
        using (var r = q.ExecuteReader())
        {
            if (r.Read())
            {
                if (r.GetString(0) != digest) throw new InvalidOperationException("Archive command reused with different input.");
                return JsonSerializer.Deserialize<ControlArchiveReceipt>(r.GetString(1))!;
            }
        }
        var current = ReadControlSnapshot(c, tx);
        if (current.Revision != command.ExpectedRevision || current.Mode != DevelopmentMode.Manual)
            throw new InvalidOperationException("History capture requires the current revision and explicit manual mode.");
        using (var q = ControlSql(c, tx, """
            SELECT content_sha256 FROM control_legacy_archives WHERE source_id=$source AND source_revision=$version
            """, ("$source", command.SourceId), ("$version", command.SourceRevision)))
            if (q.ExecuteScalar() is not null)
                throw new InvalidOperationException("That source/version was already captured; replay its original command.");
        using (var q = ControlSql(c, tx, """
            INSERT INTO control_legacy_archives(source_id,source_revision,content_sha256,source_json,decided_by,captured_utc)
            VALUES($source,$version,$hash,$json,$by,$now);
            UPDATE control_state SET revision=revision+1 WHERE id=1;
            """, ("$source", command.SourceId), ("$version", command.SourceRevision), ("$hash", contentHash),
            ("$json", command.Json), ("$by", command.DecidedBy), ("$now", nowUtc))) q.ExecuteNonQuery();
        var receipt = new ControlArchiveReceipt(command.CommandId, checked(current.Revision + 1),
            command.SourceId, command.SourceRevision, contentHash, "history_only");
        AppendEvent(c, tx, "control:" + command.CommandId, "control.legacy.archived", "", null, null, null,
            JsonSerializer.Serialize(receipt), nowUtc);
        using (var q = ControlSql(c, tx, "INSERT INTO control_receipts VALUES($cmd,$hash,$json)",
            ("$cmd", command.CommandId), ("$hash", digest), ("$json", JsonSerializer.Serialize(receipt)))) q.ExecuteNonQuery();
        tx.Commit();
        return receipt;
    }

    private static void ValidateLegacyControl(LegacyControlArchiveCommand command)
    {
        using var source = JsonDocument.Parse(command.Json);
        var root = source.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new ArgumentException("Legacy source must be an object.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (root.EnumerateObject().Any(p => !names.Add(p.Name)))
            throw new ArgumentException("Legacy source has ambiguous duplicate root properties.");
        if (!root.TryGetProperty("schemaVersion", out var schema) || schema.ValueKind != JsonValueKind.Number ||
            !schema.TryGetInt32(out var version) || version != 1 ||
            !root.TryGetProperty("revision", out var revision) || revision.ValueKind != JsonValueKind.Number ||
            !revision.TryGetInt64(out var value) || value != command.SourceRevision ||
            !root.TryGetProperty("mode", out var mode) || mode.ValueKind != JsonValueKind.String ||
            mode.GetString() is not ("manual" or "parallel" or "automatic"))
            throw new ArgumentException("Unsupported legacy schema, mode or source revision.");
        foreach (var name in new[] { "packages", "groups", "decisions", "knowledge", "questions", "intents", "receipts" })
            if (!root.TryGetProperty(name, out var item) || item.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Legacy source has no valid " + name + " object.");
        if (!root.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("Legacy source has no valid event history.");
    }

    /// <summary>Read-only reconciliation export. The legacy section is history, not restored runtime authority.</summary>
    public ControlShadowExport ExportControlShadow()
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        var result = ExportControlShadow(c, tx); tx.Commit(); return result;
    }

    /// <summary>Client state and inbox share one SQLite snapshot, including the U1 event watermark.</summary>
    public ControlClientSnapshot ReadControlClientSnapshot(bool includeGraph = false)
    {
        using var c = Open(); using var tx = c.BeginTransaction();
        var state = ExportControlShadow(c, tx); var inbox = ReadControlInbox(c, tx);
        var graph = includeGraph ? ReadControlGraph(c, tx) : null;
        var running = includeGraph ? ReadControlRunningExecutions(c, tx) : null;
        var stops = includeGraph ? ReadControlRunStops(c, tx) : null;
        var authority = includeGraph ? ReadControlAuthorityIdentity(c, tx) : null;
        tx.Commit(); return new("fufucode-control-client-v1", state, inbox) { Graph = graph, RunningExecutions = running, RunStops = stops, AuthorityIdentity = authority };
    }

    private ControlShadowExport ExportControlShadow(SqliteConnection c, SqliteTransaction tx)
    {
        var current = ReadControlSnapshot(c, tx);
        var archives = new List<LegacyControlArchive>();
        using (var q = ControlSql(c, tx, """
            SELECT source_id,source_revision,content_sha256,source_json,decided_by,captured_utc
            FROM control_legacy_archives ORDER BY source_id,source_revision
            """))
        using (var r = q.ExecuteReader())
            while (r.Read()) archives.Add(new(r.GetString(0), r.GetInt64(1), r.GetString(2),
                r.GetString(3), r.GetString(4), r.GetInt64(5)));
        using var cursor = ControlSql(c, tx, "SELECT COALESCE(MAX(cursor),0) FROM automation_events");
        var watermark = Convert.ToInt64(cursor.ExecuteScalar());
        var groups = ReadControlGroups(c, tx);
        return new("fufucode-control-shadow-v1", current, watermark, archives) { Groups = groups };
    }
}
