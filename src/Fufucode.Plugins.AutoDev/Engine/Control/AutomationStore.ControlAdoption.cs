using System.Text;
using Microsoft.Data.Sqlite;

namespace Fufucode.Core.Automation;

public sealed partial class AutomationStore
{
    private static Dictionary<string, long> ReadAdoptedKnowledge(SqliteConnection c, SqliteTransaction? tx, string id, long version)
    {
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        using var q = ControlSql(c, tx, "SELECT fact_key,fact_version FROM control_knowledge_bindings WHERE package_id=$id AND package_version=$v ORDER BY fact_key",
            ("$id", id), ("$v", version));
        using var r = q.ExecuteReader();
        while (r.Read()) result.Add(r.GetString(0), r.GetInt64(1));
        return result;
    }

    private static void ApproveControlKnowledge(SqliteConnection c, SqliteTransaction tx, WorkPackageVersion package)
    {
        if (package.Approved)
        {
            if (!ControlKnowledgeSatisfied(c, tx, package))
                throw new InvalidOperationException("An approved contract requires explicit knowledge adoption, not a repeat approval.");
            return;
        }
        foreach (var key in package.Spec.KnowledgeRefs ?? [])
        {
            var version = CurrentKnowledgeVersion(c, tx, key);
            if (version == 0) continue; // A declared future fact creates a directed request when first published.
            using var q = ControlSql(c, tx, """
                INSERT INTO control_knowledge_bindings VALUES($id,$pv,$key,$fv)
                ON CONFLICT(package_id,package_version,fact_key) DO UPDATE SET fact_version=excluded.fact_version;
                """, ("$id", package.Spec.Id), ("$pv", package.Version), ("$key", key), ("$fv", version));
            q.ExecuteNonQuery();
        }
    }

    private static void CopyControlKnowledge(SqliteConnection c, SqliteTransaction tx, string id)
    {
        var package = CurrentControlPackage(c, tx, id);
        var previous = ReadAdoptedKnowledge(c, tx, id, package.Version - 1);
        foreach (var key in package.Spec.KnowledgeRefs ?? [])
            if (previous.TryGetValue(key, out var version))
            {
                using var q = ControlSql(c, tx, "INSERT INTO control_knowledge_bindings VALUES($id,$pv,$key,$fv)",
                    ("$id", id), ("$pv", package.Version), ("$key", key), ("$fv", version));
                q.ExecuteNonQuery();
            }
        using var old = ControlSql(c, tx,
            "UPDATE control_knowledge_adoptions SET status='superseded' WHERE package_id=$id AND package_version<>$v AND status='pending'",
            ("$id", id), ("$v", package.Version));
        old.ExecuteNonQuery();
    }

    private static bool ControlKnowledgeSatisfied(SqliteConnection c, SqliteTransaction? tx, WorkPackageVersion package)
    {
        if (ControlPackageRetired(c, tx, package.Spec.Id)) return false;
        using var q = ControlSql(c, tx, "SELECT COUNT(*) FROM control_knowledge_adoptions WHERE package_id=$id AND package_version=$v AND status='pending'",
            ("$id", package.Spec.Id), ("$v", package.Version));
        return Convert.ToInt64(q.ExecuteScalar()) == 0;
    }

    private static bool ControlPackageIntegrated(SqliteConnection c, SqliteTransaction? tx, string id, long version)
    {
        using var q = ControlSql(c, tx, """
            SELECT p.part_id,EXISTS(SELECT 1 FROM integration_marks m WHERE m.part_id=p.part_id AND m.revision_number=p.current_revision)
            FROM control_bindings b JOIN control_intents i ON i.intent_id=b.intent_id
            JOIN delivery_parts p ON p.task_id=b.task_id WHERE i.package_id=$id AND i.version=$v
            """, ("$id", id), ("$v", version));
        using var r = q.ExecuteReader();
        var any = false;
        while (r.Read()) { any = true; if (r.GetInt64(1) == 0) return false; }
        return any;
    }

    private static void CreateKnowledgeAdoptions(SqliteConnection c, SqliteTransaction tx)
    {
        var ids = new List<string>();
        using (var q = ControlSql(c, tx, "SELECT package_id FROM control_packages p WHERE approved_version=version AND NOT EXISTS(SELECT 1 FROM control_retired_packages r WHERE r.package_id=p.package_id)"))
        using (var r = q.ExecuteReader()) while (r.Read()) ids.Add(r.GetString(0));
        foreach (var id in ids)
        {
            var package = CurrentControlPackage(c, tx, id);
            if (ControlPackageIntegrated(c, tx, id, package.Version)) continue; // Finished integration is immutable history.
            foreach (var key in package.Spec.KnowledgeRefs ?? [])
            {
                var latest = CurrentKnowledgeVersion(c, tx, key);
                if (latest <= package.AdoptedKnowledge.GetValueOrDefault(key)) continue;
                using var q = ControlSql(c, tx, """
                    UPDATE control_knowledge_adoptions SET status='superseded' WHERE package_id=$id AND package_version=$v
                      AND fact_key=$key AND fact_version<>$fv AND status='pending';
                    INSERT OR IGNORE INTO control_knowledge_adoptions VALUES($id,$v,$key,$fv,'pending',NULL);
                    """, ("$id", id), ("$v", package.Version), ("$key", key), ("$fv", latest));
                q.ExecuteNonQuery();
            }
        }
    }

    private static IReadOnlyList<ControlKnowledgeAdoption> ReadKnowledgeAdoptions(SqliteConnection c, SqliteTransaction tx)
    {
        var rows = new List<ControlKnowledgeAdoption>();
        using (var q = ControlSql(c, tx, "SELECT package_id,package_version,fact_key,fact_version,status,decided_by FROM control_knowledge_adoptions ORDER BY package_id,package_version,fact_key,fact_version"))
        using (var r = q.ExecuteReader()) while (r.Read()) rows.Add(new(r.GetString(0), r.GetInt64(1), r.GetString(2),
            r.GetInt64(3), r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5), null));
        return rows.Select(row => row.Status != "pending" ? row : row with
        { BlockedReason = KnowledgeWriterBlock(c, tx, row.PackageId) }).ToArray();
    }

    private static string? KnowledgeWriterBlock(SqliteConnection c, SqliteTransaction tx, string id)
    {
        var affected = ValidateControlDependencies(c, tx, CurrentControlPackage(c, tx, id).Spec);
        foreach (var package in affected)
        {
            if (!ControlPackageHasBinding(c, tx, package)) continue;
            if (ControlAdditionalWriterBlock(c, tx, package) is { } blocked) return blocked;
            using var q = ControlSql(c, tx, """
                SELECT q.state,h.status FROM control_writer_bindings b JOIN control_intents i ON i.intent_id=b.intent_id
                LEFT JOIN host_requests q ON q.run_id=b.run_id LEFT JOIN harness_runs h ON h.run_id=b.run_id WHERE i.package_id=$id
                """, ("$id", package));
            using var r = q.ExecuteReader();
            while (r.Read())
            {
                if (r.IsDBNull(0) || (!r.IsDBNull(1) && r.GetString(1) == "Unknown") || (r.GetInt64(0) == 1 && r.IsDBNull(1)))
                    return "waiting_reconciliation:" + package;
                if (r.GetInt64(0) == 1 || !r.IsDBNull(1) && r.GetString(1) is "Dispatching" or "Running")
                    return "waiting_stop:" + package;
            }
        }
        return null;
    }

    private static string BuildControlKnowledgePrompt(SqliteConnection c, SqliteTransaction tx, string id, long version)
    {
        var adopted = ReadAdoptedKnowledge(c, tx, id, version);
        if (adopted.Count == 0) return "";
        var result = new StringBuilder("\nApproved public knowledge (bound versions, preserve source):\n");
        foreach (var (key, v) in adopted)
        {
            using var q = ControlSql(c, tx, "SELECT text,source FROM control_knowledge_facts WHERE fact_key=$key AND version=$v", ("$key", key), ("$v", v));
            using var r = q.ExecuteReader();
            if (!r.Read()) throw new InvalidOperationException("Bound knowledge evidence is missing.");
            result.Append(key).Append(" @ ").Append(v).Append(" [").Append(r.GetString(1)).Append("]: ").Append(r.GetString(0)).Append('\n');
        }
        return result.ToString();
    }
}
