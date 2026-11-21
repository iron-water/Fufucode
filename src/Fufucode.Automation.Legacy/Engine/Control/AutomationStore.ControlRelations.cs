using System.Text.Json;
using Fufucode.App.Abstractions.Automation.Harnesses;
using Microsoft.Data.Sqlite;

namespace Fufucode.Core.Automation;

public sealed partial class AutomationStore
{
    /// <summary>Human planning/selection, never a delivery, acceptance or integration report.</summary>
    public ControlRelationReceipt ApplyControlRelation(ControlRelationCommand command, long nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command.CommandId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.DecidedBy);
        var digest = Sha("relation:" + JsonSerializer.Serialize(command));
        using var c = Open(); using var tx = c.BeginTransaction();
        using (var q = ControlSql(c, tx, "SELECT payload_hash,result_json FROM control_receipts WHERE command_id=$id",
            ("$id", command.CommandId)))
        using (var r = q.ExecuteReader())
            if (r.Read())
            {
                if (r.GetString(0) != digest) throw new InvalidOperationException("Relation command reused with different input.");
                return JsonSerializer.Deserialize<ControlRelationReceipt>(r.GetString(1))!;
            }
        var state = ReadControlSnapshot(c, tx);
        if (state.Revision != command.ExpectedRevision) throw new InvalidOperationException("Refresh the changed control state.");
        string groupId;
        string? winner = null;
        switch (command.Action)
        {
            case ControlRelationAction.DefineGroup:
            {
                var spec = command.Group ?? throw new ArgumentException("Group specification is required.");
                ArgumentException.ThrowIfNullOrWhiteSpace(spec.Id);
                if (!Enum.IsDefined(spec.Relation) || spec.Members is null || spec.Members.Length < 2 ||
                    spec.Members.Any(string.IsNullOrWhiteSpace) || spec.Members.Distinct(StringComparer.Ordinal).Count() != spec.Members.Length)
                    throw new ArgumentException("A group requires a known relation and at least two distinct packages.");
                var members = spec.Members.Select(id => CurrentControlPackage(c, tx, id)).ToArray();
                if (members.Any(p => p.Superseded || p.Approved || ControlPackageHasBinding(c, tx, p.Spec.Id)))
                    throw new InvalidOperationException("Define relations before approval or execution; running work requires explicit restructuring.");
                if (spec.Relation == ControlRelation.Competitive)
                {
                    if (!GitCliEvidence.IsSha(spec.Baseline ?? "") || spec.Criteria is null || spec.Criteria.Length == 0 ||
                        spec.Criteria.Any(string.IsNullOrWhiteSpace))
                        throw new ArgumentException("Competition needs a committed Git baseline and concrete shared criteria.");
                    if (ReadControlGroups(c, tx).Any(g => !g.Superseded && g.Spec.Relation == ControlRelation.Competitive &&
                        g.Spec.Members.Intersect(spec.Members, StringComparer.Ordinal).Any()))
                        throw new InvalidOperationException("A package cannot join multiple competitive groups.");
                }
                using (var q = ControlSql(c, tx, "INSERT INTO control_groups(group_id,spec_json) VALUES($id,$json)",
                    ("$id", spec.Id), ("$json", JsonSerializer.Serialize(spec)))) q.ExecuteNonQuery();
                for (var i = 0; i < spec.Members.Length; i++)
                {
                    using var q = ControlSql(c, tx, "INSERT INTO control_group_members VALUES($group,$package,$ordinal)",
                        ("$group", spec.Id), ("$package", spec.Members[i]), ("$ordinal", i));
                    q.ExecuteNonQuery();
                }
                if (spec.Relation == ControlRelation.Pipeline)
                    for (var i = 1; i < members.Length; i++)
                    {
                        var package = CurrentControlPackage(c, tx, spec.Members[i]);
                        var dependencies = package.Spec.DependsOn.Append(members[i - 1].Spec.Id).Distinct(StringComparer.Ordinal).ToArray();
                        if (!dependencies.SequenceEqual(package.Spec.DependsOn))
                            ReplaceControlPackage(c, tx, package.Spec with { DependsOn = dependencies }, package.Version, nowUtc);
                    }
                groupId = spec.Id;
                break;
            }
            case ControlRelationAction.SelectWinner:
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(command.GroupId);
                ArgumentException.ThrowIfNullOrWhiteSpace(command.Winner);
                var group = ReadControlGroups(c, tx).SingleOrDefault(g => g.Spec.Id == command.GroupId)
                    ?? throw new KeyNotFoundException(command.GroupId);
                if (group.Superseded) throw new InvalidOperationException("A superseded relationship cannot select a winner.");
                if (group.Spec.Relation != ControlRelation.Competitive) throw new ArgumentException("Only competition has a winner.");
                var candidate = group.Members.SingleOrDefault(p => p.PackageId == command.Winner)
                    ?? throw new ArgumentException("Winner must belong to this group.");
                if (!candidate.Approved || !candidate.Accepted || candidate.EvidenceToken != command.ExpectedEvidenceToken ||
                    !ControlGroupBaselineSatisfied(c, tx, group.Spec, candidate.TaskId!))
                    throw new InvalidOperationException("Selection requires the current accepted outcome at the common baseline.");
                if (ControlGroupHasIntegration(c, tx, group.Spec.Id))
                    throw new InvalidOperationException("An integrated group outcome cannot be replaced by a new selection.");
                var versions = group.Members.ToDictionary(p => p.PackageId, p => p.Version, StringComparer.Ordinal);
                using (var q = ControlSql(c, tx, """
                    INSERT INTO control_group_choices VALUES($cmd,$group,$winner,$token,$versions,$by,$now);
                    UPDATE control_groups SET choice_id=$cmd WHERE group_id=$group;
                    """, ("$cmd", command.CommandId), ("$group", group.Spec.Id), ("$winner", candidate.PackageId),
                    ("$token", candidate.EvidenceToken), ("$versions", JsonSerializer.Serialize(versions)),
                    ("$by", command.DecidedBy), ("$now", nowUtc))) q.ExecuteNonQuery();
                groupId = group.Spec.Id; winner = candidate.PackageId;
                break;
            }
            default: throw new ArgumentOutOfRangeException(nameof(command));
        }
        var next = checked(state.Revision + 1);
        using (var q = ControlSql(c, tx, "UPDATE control_state SET revision=$next WHERE id=1", ("$next", next))) q.ExecuteNonQuery();
        RefreshControlIntents(c, tx, state.Mode, next);
        var receipt = new ControlRelationReceipt(command.CommandId, next, groupId, winner);
        AppendEvent(c, tx, "control:" + command.CommandId, "control.relation." + command.Action,
            "", null, null, null, JsonSerializer.Serialize(new { command, receipt }), nowUtc);
        using (var q = ControlSql(c, tx, "INSERT INTO control_receipts VALUES($id,$digest,$result)",
            ("$id", command.CommandId), ("$digest", digest), ("$result", JsonSerializer.Serialize(receipt)))) q.ExecuteNonQuery();
        tx.Commit(); return receipt;
    }

    public IReadOnlyList<ControlGroupState> ReadControlGroups()
    {
        using var c = Open(); using var tx = c.BeginTransaction();
        var groups = ReadControlGroups(c, tx); tx.Commit(); return groups;
    }

    private static WorkPackageVersion CurrentControlPackage(SqliteConnection c, SqliteTransaction? tx, string id)
    {
        using var q = ControlSql(c, tx, """
            SELECT v.spec_json,p.version,p.approved_version FROM control_packages p
            JOIN control_package_versions v ON v.package_id=p.package_id AND v.version=p.version WHERE p.package_id=$id
            """, ("$id", id));
        using var r = q.ExecuteReader();
        if (!r.Read()) throw new KeyNotFoundException(id);
        var package = new WorkPackageVersion(JsonSerializer.Deserialize<WorkPackageSpec>(r.GetString(0))!, r.GetInt64(1),
            !r.IsDBNull(2) && r.GetInt64(2) == r.GetInt64(1));
        r.Close();
        return package with { AdoptedKnowledge = ReadAdoptedKnowledge(c, tx, id, package.Version), Superseded = ControlPackageRetired(c, tx, id) };
    }

    private static ControlPackageOutcome ReadControlOutcome(SqliteConnection c, SqliteTransaction? tx, string id)
    {
        var package = CurrentControlPackage(c, tx, id);
        string? taskId;
        using (var q = ControlSql(c, tx, """
            SELECT b.task_id FROM control_bindings b JOIN control_intents i ON i.intent_id=b.intent_id
            WHERE i.package_id=$id AND i.version=$version
            """, ("$id", id), ("$version", package.Version))) taskId = q.ExecuteScalar() as string;
        var parts = new List<(string Id, long Revision, int Status, string? Run, string? Head, bool Integrated)>();
        if (taskId is not null)
        {
            using var q = ControlSql(c, tx, """
                SELECT p.part_id,p.current_revision,p.status,r.run_id,r.head_sha,
                  EXISTS(SELECT 1 FROM integration_marks m WHERE m.part_id=p.part_id AND m.revision_number=p.current_revision)
                FROM delivery_parts p LEFT JOIN delivery_revisions r ON r.part_id=p.part_id AND r.revision_number=p.current_revision
                WHERE p.task_id=$task ORDER BY p.part_id
                """, ("$task", taskId));
            using var r = q.ExecuteReader();
            while (r.Read()) parts.Add((r.GetString(0), r.GetInt64(1), r.GetInt32(2),
                r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.GetInt32(5) == 1));
        }
        // Integration marks do not change the selection token as a multi-part integration proceeds.
        var token = Sha(JsonSerializer.Serialize(new { id, package.Version, package.Approved, taskId,
            parts = parts.Select(p => new { p.Id, p.Revision, p.Status, p.Run, p.Head }) }));
        var accepted = parts.Count > 0 && parts.All(p => p.Status == (int)DeliveryPartStatus.Accepted && p.Revision > 0 && p.Head is not null);
        var integrated = accepted && parts.All(p => p.Integrated);
        return new(id, package.Version, package.Approved, taskId, token,
            accepted && ControlKnowledgeSatisfied(c, tx, package), integrated);
    }

    private static IReadOnlyList<ControlGroupState> ReadControlGroups(SqliteConnection c, SqliteTransaction? tx)
    {
        var rows = new List<(ControlGroupSpec Spec, string? Winner, string? Token, string? Versions, bool Superseded)>();
        using (var q = ControlSql(c, tx, """
            SELECT g.spec_json,s.winner,s.evidence_token,s.versions_json,
              EXISTS(SELECT 1 FROM control_retired_groups r WHERE r.group_id=g.group_id) FROM control_groups g
            LEFT JOIN control_group_choices s ON s.command_id=g.choice_id ORDER BY g.group_id
            """))
        using (var r = q.ExecuteReader())
            while (r.Read()) rows.Add((JsonSerializer.Deserialize<ControlGroupSpec>(r.GetString(0))!,
                r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetInt64(4) != 0));
        return rows.Select(row =>
        {
            var members = row.Spec.Members.Select(id => ReadControlOutcome(c, tx, id)).ToArray();
            var versions = row.Versions is null ? null : JsonSerializer.Deserialize<Dictionary<string, long>>(row.Versions);
            var winner = members.SingleOrDefault(m => m.PackageId == row.Winner);
            var current = !row.Superseded && winner is { Approved: true, Accepted: true } && winner.EvidenceToken == row.Token &&
                versions is not null && members.All(m => versions.TryGetValue(m.PackageId, out var v) && v == m.Version);
            return new ControlGroupState(row.Spec, row.Winner, current, members) { Superseded = row.Superseded };
        }).ToArray();
    }

    private static bool ControlGroupBaselineSatisfied(SqliteConnection c, SqliteTransaction? tx,
        ControlGroupSpec group, string taskId)
    {
        if (group.Relation != ControlRelation.Competitive) return true;
        using var q = ControlSql(c, tx, "SELECT base_sha FROM tasks WHERE task_id=$id", ("$id", taskId));
        return string.Equals(q.ExecuteScalar() as string, group.Baseline, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ControlGroupHasIntegration(SqliteConnection c, SqliteTransaction tx, string id)
    {
        using var q = ControlSql(c, tx, """
            SELECT EXISTS(SELECT 1 FROM control_group_members m JOIN control_intents i ON i.package_id=m.package_id
              JOIN control_bindings b ON b.intent_id=i.intent_id JOIN delivery_parts p ON p.task_id=b.task_id
              JOIN integration_marks k ON k.part_id=p.part_id WHERE m.group_id=$id)
            """, ("$id", id));
        return Convert.ToInt64(q.ExecuteScalar()) != 0;
    }

    private static bool ControlRelationsAdmitBinding(SqliteConnection c, SqliteTransaction? tx, string id, string taskId) =>
        ReadControlGroups(c, tx).Where(g => !g.Superseded && g.Spec.Members.Contains(id, StringComparer.Ordinal))
            .All(g => ControlGroupBaselineSatisfied(c, tx, g.Spec, taskId));

    public bool CanRequestControlIntegration(string packageId)
    {
        using var c = Open(); using var tx = c.BeginTransaction();
        var outcome = ReadControlOutcome(c, tx, packageId);
        var result = outcome.TaskId is not null && ControlIntegrationAdmissible(c, tx, outcome.TaskId, requireAuthorization: false);
        tx.Commit(); return result;
    }

    public bool CanIntegrateControlPackage(string packageId)
    {
        using var c = Open(); using var tx = c.BeginTransaction();
        var outcome = ReadControlOutcome(c, tx, packageId);
        var result = outcome.TaskId is not null && ControlIntegrationAdmissible(c, tx, outcome.TaskId);
        tx.Commit(); return result;
    }

    private static bool ControlIntegrationAdmissible(SqliteConnection c, SqliteTransaction tx, string taskId,
        string? partId = null, long? revision = null, bool requireAuthorization = true)
    {
        string? packageId;
        using (var q = ControlSql(c, tx, """
            SELECT i.package_id FROM control_bindings b JOIN control_intents i ON i.intent_id=b.intent_id WHERE b.task_id=$id
            """, ("$id", taskId))) packageId = q.ExecuteScalar() as string;
        if (packageId is null) return true; // Legacy U1 tasks are not silently adopted into control groups.
        if (partId is not null)
        {
            using var q = ControlSql(c, tx, "SELECT current_revision FROM delivery_parts WHERE part_id=$part AND task_id=$task",
                ("$part", partId), ("$task", taskId));
            if (q.ExecuteScalar() is not long currentRevision || currentRevision != revision) return false;
        }
        var current = CurrentControlPackage(c, tx, packageId);
        if (!ControlReviewWriterStopped(c, tx, packageId)) return false;
        var outcome = ReadControlOutcome(c, tx, packageId);
        if (current.Spec.Artifact != "code" || outcome.TaskId != taskId || !outcome.Approved || !outcome.Accepted ||
            !ControlDependenciesSatisfied(c, tx, current.Spec, taskId)) return false;
        using (var q = ControlSql(c, tx, """
            SELECT EXISTS(SELECT 1 FROM control_writer_bindings b JOIN host_requests q ON q.run_id=b.run_id
              LEFT JOIN harness_runs h ON h.run_id=b.run_id WHERE b.task_id=$task
              AND (q.state IN (0,1) OR h.status IN ('Dispatching','Running','Unknown')))
            """, ("$task", taskId)))
            if (Convert.ToInt64(q.ExecuteScalar()) != 0) return false;
        foreach (var group in ReadControlGroups(c, tx).Where(g => !g.Superseded && g.Spec.Members.Contains(packageId, StringComparer.Ordinal)))
        {
            if (!ControlGroupBaselineSatisfied(c, tx, group.Spec, taskId)) return false;
            if (group.Spec.Relation == ControlRelation.Competitive && (!group.SelectionCurrent || group.Winner != packageId)) return false;
            if (group.Spec.Relation == ControlRelation.Complementary && group.Members.Any(m => !m.Approved || !m.Accepted)) return false;
        }
        return !requireAuthorization || HasCurrentControlIntegrationAuthorization(c, tx, current, outcome);
    }
}
