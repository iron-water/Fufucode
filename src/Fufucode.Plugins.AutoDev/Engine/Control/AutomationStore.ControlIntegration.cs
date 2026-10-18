using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Fufucode.Core.Automation;

public sealed partial class AutomationStore
{
    private static void MigrateControlIntegration(SqliteConnection c, SqliteTransaction tx) => Execute(c, tx, """
        CREATE TABLE IF NOT EXISTS control_integration_authorizations (
          authorization_id TEXT PRIMARY KEY, package_id TEXT NOT NULL, package_version INTEGER NOT NULL,
          task_id TEXT NOT NULL REFERENCES tasks(task_id), evidence_token TEXT NOT NULL, context_json TEXT NOT NULL,
          authorized_by TEXT NOT NULL, status TEXT NOT NULL, revoked_by TEXT, reason TEXT,
          FOREIGN KEY(package_id,package_version) REFERENCES control_package_versions(package_id,version));
        CREATE UNIQUE INDEX IF NOT EXISTS control_integration_authorized_once
          ON control_integration_authorizations(package_id,package_version,task_id,evidence_token) WHERE status='authorized';
        """);

    /// <summary>Explicit human authorization. Does not merge Git, create an integration mark or launch a process.</summary>
    public ControlIntegrationReceipt ApplyControlIntegration(ControlIntegrationCommand command, long nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command.CommandId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.DecidedBy);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.PackageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ExpectedEvidenceToken);
        var digest = Sha("integration-authorization:" + JsonSerializer.Serialize(command));
        using var c = Open(); using var tx = c.BeginTransaction();
        using (var q = ControlSql(c, tx, "SELECT payload_hash,result_json FROM control_receipts WHERE command_id=$id", ("$id", command.CommandId)))
        using (var r = q.ExecuteReader())
            if (r.Read())
            {
                if (r.GetString(0) != digest) throw new InvalidOperationException("Integration decision identity reused with different input.");
                return JsonSerializer.Deserialize<ControlIntegrationReceipt>(r.GetString(1))!;
            }
        long revision;
        using (var q = ControlSql(c, tx, "SELECT revision FROM control_state WHERE id=1")) revision = Convert.ToInt64(q.ExecuteScalar());
        if (command.ExpectedRevision != revision) throw new InvalidOperationException("Refresh the changed control state before authorizing integration.");
        var id = command.CommandId; string status;
        if (command.Action == ControlIntegrationAction.Authorize)
        {
            var candidate = ReadControlIntegrationCandidate(c, tx, command.PackageId);
            if (candidate is not { Eligible: true, Integrated: false } || candidate.Package.Version != command.ExpectedPackageVersion ||
                candidate.EvidenceToken != command.ExpectedEvidenceToken)
                throw new InvalidOperationException("Authorize the current eligible contract and concrete reviewed evidence only.");
            if (candidate.Authorized) throw new InvalidOperationException("This exact evidence already has an active authorization.");
            using var q = ControlSql(c, tx, "INSERT INTO control_integration_authorizations VALUES($id,$package,$v,$task,$token,$context,$actor,'authorized',NULL,NULL)",
                ("$id", id), ("$package", command.PackageId), ("$v", candidate.Package.Version), ("$task", candidate.TaskId),
                ("$token", candidate.EvidenceToken), ("$context", candidate.ContextJson), ("$actor", command.DecidedBy)); q.ExecuteNonQuery();
            status = "authorized";
        }
        else if (command.Action == ControlIntegrationAction.Revoke)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(command.AuthorizationId); RequireKnowledgeText(command.Reason, "Revocation reason");
            id = command.AuthorizationId;
            using var q = ControlSql(c, tx, """
                UPDATE control_integration_authorizations SET status='revoked',revoked_by=$actor,reason=$reason
                WHERE authorization_id=$id AND package_id=$package AND package_version=$v AND evidence_token=$token AND status='authorized'
                """, ("$id", id), ("$package", command.PackageId), ("$v", command.ExpectedPackageVersion),
                ("$token", command.ExpectedEvidenceToken), ("$actor", command.DecidedBy), ("$reason", command.Reason!));
            if (q.ExecuteNonQuery() != 1) throw new InvalidOperationException("The pinned authorization is missing, revoked or mismatched.");
            status = "revoked";
        }
        else throw new ArgumentOutOfRangeException(nameof(command));
        var next = checked(revision + 1);
        using (var q = ControlSql(c, tx, "UPDATE control_state SET revision=$next WHERE id=1 AND revision=$old", ("$next", next), ("$old", revision)))
            if (q.ExecuteNonQuery() != 1) throw new InvalidOperationException("Concurrent integration decision.");
        var receipt = new ControlIntegrationReceipt(command.CommandId, next, id, status);
        AppendEvent(c, tx, "control:" + command.CommandId, "control.integration." + command.Action,
            "", null, null, null, JsonSerializer.Serialize(new { command, receipt }), nowUtc);
        using (var q = ControlSql(c, tx, "INSERT INTO control_receipts VALUES($id,$digest,$result)",
            ("$id", command.CommandId), ("$digest", digest), ("$result", JsonSerializer.Serialize(receipt)))) q.ExecuteNonQuery();
        tx.Commit(); return receipt;
    }

    public ControlIntegrationCandidate? ReadControlIntegrationCandidate(string packageId)
    {
        using var c = Open(); using var tx = c.BeginTransaction();
        var candidate = ReadControlIntegrationCandidate(c, tx, packageId); tx.Commit(); return candidate;
    }

    private static ControlIntegrationCandidate? ReadControlIntegrationCandidate(SqliteConnection c, SqliteTransaction tx, string id)
    {
        var package = CurrentControlPackage(c, tx, id); var outcome = ReadControlOutcome(c, tx, id);
        if (outcome.TaskId is null || package.Spec.Artifact != "code" || package.Superseded) return null;
        var context = ControlIntegrationContext(c, tx, package, outcome);
        var token = Sha(context);
        var eligible = ControlIntegrationAdmissible(c, tx, outcome.TaskId, requireAuthorization: false);
        return new(package, outcome.TaskId, token, context, eligible,
            eligible && HasControlIntegrationAuthorization(c, tx, package, outcome.TaskId, token), outcome.Integrated);
    }

    private static bool HasCurrentControlIntegrationAuthorization(SqliteConnection c, SqliteTransaction tx,
        WorkPackageVersion package, ControlPackageOutcome outcome) => outcome.TaskId is not null &&
        HasControlIntegrationAuthorization(c, tx, package, outcome.TaskId, Sha(ControlIntegrationContext(c, tx, package, outcome)));

    private static bool HasControlIntegrationAuthorization(SqliteConnection c, SqliteTransaction tx,
        WorkPackageVersion package, string task, string token)
    {
        using var q = ControlSql(c, tx, """
            SELECT EXISTS(SELECT 1 FROM control_integration_authorizations WHERE package_id=$id AND package_version=$v
              AND task_id=$task AND evidence_token=$token AND status='authorized')
            """, ("$id", package.Spec.Id), ("$v", package.Version), ("$task", task), ("$token", token));
        return Convert.ToInt64(q.ExecuteScalar()) != 0;
    }

    private static string ControlIntegrationContext(SqliteConnection c, SqliteTransaction tx,
        WorkPackageVersion package, ControlPackageOutcome outcome)
    {
        var parts = ReadControlDecisionParts(c, tx, outcome.TaskId!, ControlReviewWriterStopped(c, tx, package.Spec.Id));
        var groups = ReadControlGroups(c, tx).Where(g => !g.Superseded && g.Spec.Members.Contains(package.Spec.Id, StringComparer.Ordinal))
            .Select(g => new
            {
                g.Spec, g.Winner, g.SelectionCurrent,
                members = g.Members.Where(m => g.Spec.Relation != ControlRelation.Independent || m.PackageId == package.Spec.Id)
                    .Select(m => new { outcome = new { m.PackageId, m.Version, m.Approved, m.Accepted, m.EvidenceToken },
                        contract = CurrentControlPackage(c, tx, m.PackageId),
                        evidence = m.TaskId is null ? [] : ReadControlDecisionParts(c, tx, m.TaskId, ControlReviewWriterStopped(c, tx, m.PackageId)) })
            }).ToArray();
        var dependencies = package.Spec.DependsOn.Order(StringComparer.Ordinal).Select(id =>
        {
            var dependency = ReadControlOutcome(c, tx, id);
            return new { outcome = dependency, contract = CurrentControlPackage(c, tx, id),
                evidence = dependency.TaskId is null ? [] : ReadControlDecisionParts(c, tx, dependency.TaskId, ControlReviewWriterStopped(c, tx, id)),
                integrations = dependency.TaskId is null ? [] : ReadControlIntegrationMarks(c, tx, dependency.TaskId) };
        }).ToArray();
        var taskDependencies = new List<ControlTaskDependency>();
        using (var q = ControlSql(c, tx, """
            SELECT d.depends_on_task_id,d.depends_on_part_id,d.requires_integration,t.base_sha,t.head_sha,t.run_id
            FROM task_dependencies d JOIN tasks t ON t.task_id=d.depends_on_task_id
            WHERE d.task_id=$task ORDER BY d.depends_on_task_id,d.depends_on_part_id
            """, ("$task", outcome.TaskId!)))
        using (var r = q.ExecuteReader()) while (r.Read()) taskDependencies.Add(new(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetInt32(2) != 0,
            r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5)));
        // Pin actual U1 dependency parts too, even when the task HEAD remains unchanged.
        var dependencyEvidence = taskDependencies.Select(dep => new { dep.TaskId, dep.PartId,
            evidence = ReadControlDecisionParts(c, tx, dep.TaskId).Where(p => dep.PartId is null || p.PartId == dep.PartId).ToArray(),
            integrations = ReadControlIntegrationMarks(c, tx, dep.TaskId).Where(p => dep.PartId is null || p.PartId == dep.PartId).ToArray() }).ToArray();
        string? threadId; string? baseline;
        using (var q = ControlSql(c, tx, "SELECT thread_id,base_sha FROM tasks WHERE task_id=$task", ("$task", outcome.TaskId!)))
        using (var r = q.ExecuteReader()) { r.Read(); threadId = r.GetString(0); baseline = r.GetString(1); }
        // Own marks intentionally do not alter the token: one authorized bundle can be integrated part by part.
        var boundKnowledge = BuildControlKnowledgePrompt(c, tx, package.Spec.Id, package.Version);
        return JsonSerializer.Serialize(new { package, outcome.TaskId, threadId, baseline, parts, groups, boundKnowledge, dependencies, taskDependencies, dependencyEvidence });
    }

    private sealed record ControlTaskDependency(string TaskId, string? PartId, bool RequiresIntegration,
        string BaseSha, string? HeadSha, string? RunId);

    private sealed record ControlDecisionIntegration(string PartId, long Revision, string MainSha);
    private static IReadOnlyList<ControlDecisionIntegration> ReadControlIntegrationMarks(SqliteConnection c, SqliteTransaction tx, string task)
    {
        var marks = new List<ControlDecisionIntegration>();
        using var q = ControlSql(c, tx, "SELECT m.part_id,m.revision_number,m.main_sha FROM integration_marks m JOIN delivery_parts p ON p.part_id=m.part_id WHERE p.task_id=$task ORDER BY m.part_id,m.revision_number", ("$task", task));
        using var r = q.ExecuteReader(); while (r.Read()) marks.Add(new(r.GetString(0), r.GetInt64(1), r.GetString(2)));
        return marks;
    }

    private sealed record ControlDecisionPart(string PartId, string Title, long Revision, DeliveryPartStatus Status,
        string? RunId, string? HeadSha, string? CardJson, string? ReviewId, string? ReviewEvidenceJson, string? ReviewFindings, string? Reviewer);

    private static IReadOnlyList<ControlDecisionPart> ReadControlDecisionParts(SqliteConnection c, SqliteTransaction tx, string task, bool includeCards = true)
    {
        var parts = new List<ControlDecisionPart>();
        using var q = ControlSql(c, tx, """
            SELECT p.part_id,p.title,p.current_revision,p.status,r.run_id,r.head_sha,CASE WHEN $cards=1 THEN r.card_json ELSE NULL END
              ,d.decision_id,CASE WHEN $cards=1 THEN d.evidence_json ELSE NULL END,
              CASE WHEN $cards=1 THEN d.findings ELSE NULL END,d.decided_by
            FROM delivery_parts p LEFT JOIN delivery_revisions r ON r.part_id=p.part_id AND r.revision_number=p.current_revision
            LEFT JOIN part_decisions d ON d.decision_id=(SELECT decision_id FROM part_decisions
              WHERE part_id=p.part_id AND revision_number=p.current_revision ORDER BY rowid DESC LIMIT 1)
            WHERE p.task_id=$task ORDER BY p.part_id
            """, ("$task", task), ("$cards", includeCards ? 1 : 0));
        using var r = q.ExecuteReader(); while (r.Read()) parts.Add(new(r.GetString(0), r.GetString(1), r.GetInt64(2), (DeliveryPartStatus)r.GetInt32(3),
            r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6),
            r.IsDBNull(7) ? null : r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8), r.IsDBNull(9) ? null : r.GetString(9), r.IsDBNull(10) ? null : r.GetString(10)));
        return parts;
    }

    private static IReadOnlyList<ControlIntegrationAuthorization> ReadControlIntegrationAuthorizations(SqliteConnection c, SqliteTransaction tx)
    {
        var rows = new List<ControlIntegrationAuthorization>();
        using (var q = ControlSql(c, tx, "SELECT authorization_id,package_id,package_version,task_id,evidence_token,context_json,authorized_by,status,revoked_by,reason FROM control_integration_authorizations ORDER BY authorization_id"))
        using (var r = q.ExecuteReader()) while (r.Read()) rows.Add(new(r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetString(3), r.GetString(4),
            r.GetString(5), r.GetString(6), r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8), r.IsDBNull(9) ? null : r.GetString(9), false));
        return rows.Select(row =>
        {
            var candidate = ReadControlIntegrationCandidate(c, tx, row.PackageId);
            return row with { ContractCurrent = candidate is { Eligible: true } && candidate.Package.Version == row.PackageVersion &&
                candidate.TaskId == row.TaskId && candidate.EvidenceToken == row.EvidenceToken };
        }).ToArray();
    }
}
