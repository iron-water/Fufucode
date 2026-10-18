using System.Text.Json;
using Fufucode.App.Abstractions.Automation.Harnesses;
using Microsoft.Data.Sqlite;

namespace Fufucode.Core.Automation;

public sealed partial class AutomationStore
{
    private static void MigrateControlRestructure(SqliteConnection c, SqliteTransaction tx) => Execute(c, tx, """
        CREATE TABLE IF NOT EXISTS control_restructures (
          proposal_id TEXT PRIMARY KEY, spec_json TEXT NOT NULL, contracts_json TEXT NOT NULL, state_token TEXT NOT NULL,
          status TEXT NOT NULL, proposed_by TEXT NOT NULL, decided_by TEXT, decision_reason TEXT);
        CREATE TABLE IF NOT EXISTS control_retired_packages (
          package_id TEXT PRIMARY KEY, version INTEGER NOT NULL, proposal_id TEXT NOT NULL REFERENCES control_restructures(proposal_id),
          FOREIGN KEY(package_id,version) REFERENCES control_package_versions(package_id,version));
        CREATE TABLE IF NOT EXISTS control_retired_groups (
          group_id TEXT PRIMARY KEY REFERENCES control_groups(group_id), proposal_id TEXT NOT NULL REFERENCES control_restructures(proposal_id));
        """);

    private static bool ControlPackageRetired(SqliteConnection c, SqliteTransaction? tx, string id)
    {
        using var q = ControlSql(c, tx, "SELECT EXISTS(SELECT 1 FROM control_retired_packages WHERE package_id=$id)", ("$id", id));
        return Convert.ToInt64(q.ExecuteScalar()) != 0;
    }

    /// <summary>Proposal and human approval are separate transactions; neither launches a process or fabricates a result.</summary>
    public ControlRestructureReceipt ApplyControlRestructure(ControlRestructureCommand command, long nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command.CommandId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.DecidedBy);
        var digest = Sha("restructure:" + JsonSerializer.Serialize(command));
        using var c = Open(); using var tx = c.BeginTransaction();
        using (var q = ControlSql(c, tx, "SELECT payload_hash,result_json FROM control_receipts WHERE command_id=$id", ("$id", command.CommandId)))
        using (var r = q.ExecuteReader())
            if (r.Read())
            {
                if (r.GetString(0) != digest) throw new InvalidOperationException("Restructure command reused with different input.");
                return JsonSerializer.Deserialize<ControlRestructureReceipt>(r.GetString(1))!;
            }
        long revision; DevelopmentMode mode;
        using (var q = ControlSql(c, tx, "SELECT revision,mode FROM control_state WHERE id=1"))
        using (var r = q.ExecuteReader()) { r.Read(); revision = r.GetInt64(0); mode = Enum.Parse<DevelopmentMode>(r.GetString(1)); }
        if (command.ExpectedRevision != revision) throw new InvalidOperationException("Refresh the changed control state.");
        var id = command.CommandId; var status = "pending";
        if (command.Action == ControlRestructureAction.Propose)
        {
            var spec = command.Spec ?? throw new ArgumentException("A concrete successor proposal is required.");
            var plan = BuildRestructurePlan(c, tx, spec);
            using var q = ControlSql(c, tx, "INSERT INTO control_restructures VALUES($id,$spec,$contracts,$token,'pending',$actor,NULL,NULL)",
                ("$id", id), ("$spec", JsonSerializer.Serialize(spec)),
                ("$contracts", JsonSerializer.Serialize(spec.Sources.Keys.Order(StringComparer.Ordinal).Select(key => CurrentControlPackage(c, tx, key)).ToArray())),
                ("$token", plan.Token), ("$actor", command.DecidedBy));
            q.ExecuteNonQuery();
        }
        else if (command.Action is ControlRestructureAction.Approve or ControlRestructureAction.Reject)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(command.ProposalId); id = command.ProposalId;
            ControlRestructureSpec spec; string token;
            using (var q = ControlSql(c, tx, "SELECT spec_json,state_token,status FROM control_restructures WHERE proposal_id=$id", ("$id", id)))
            using (var r = q.ExecuteReader())
            {
                if (!r.Read()) throw new KeyNotFoundException(id);
                if (r.GetString(2) != "pending") throw new InvalidOperationException("This restructure is already decided.");
                spec = JsonSerializer.Deserialize<ControlRestructureSpec>(r.GetString(0))!; token = r.GetString(1);
            }
            if (command.Action == ControlRestructureAction.Approve)
            {
                var plan = BuildRestructurePlan(c, tx, spec);
                if (plan.Token != token) throw new InvalidOperationException("The proposal's contracts or evidence changed; propose again.");
                foreach (var package in plan.Affected)
                    if (KnowledgeWriterBlock(c, tx, package) is { } blocked)
                        throw new InvalidOperationException("Restructure blocked: " + blocked);
                EnsureControlPackagesStopped(c, tx, plan.Affected);
                ApplyRestructurePlan(c, tx, id, spec, plan, nowUtc);
                status = "approved";
            }
            else { RequireKnowledgeText(command.Reason, "Rejection reason"); status = "rejected"; }
            using var decision = ControlSql(c, tx, "UPDATE control_restructures SET status=$status,decided_by=$actor,decision_reason=$reason WHERE proposal_id=$id",
                ("$status", status), ("$actor", command.DecidedBy), ("$reason", (object?)command.Reason ?? DBNull.Value), ("$id", id));
            decision.ExecuteNonQuery();
        }
        else throw new ArgumentOutOfRangeException(nameof(command));
        var next = checked(revision + 1);
        using (var q = ControlSql(c, tx, "UPDATE control_state SET revision=$next WHERE id=1 AND revision=$old", ("$next", next), ("$old", revision)))
            if (q.ExecuteNonQuery() != 1) throw new InvalidOperationException("Concurrent restructure decision.");
        RefreshControlIntents(c, tx, mode, next);
        var receipt = new ControlRestructureReceipt(command.CommandId, next, id, status);
        AppendEvent(c, tx, "control:" + command.CommandId, "control.restructure." + command.Action,
            "", null, null, null, JsonSerializer.Serialize(new { command, receipt }), nowUtc);
        using (var q = ControlSql(c, tx, "INSERT INTO control_receipts VALUES($id,$digest,$json)",
            ("$id", command.CommandId), ("$digest", digest), ("$json", JsonSerializer.Serialize(receipt)))) q.ExecuteNonQuery();
        tx.Commit(); return receipt;
    }

    private sealed record RestructurePlan(Dictionary<string, WorkPackageSpec> Changes, HashSet<string> Affected, string Token);

    private static RestructurePlan BuildRestructurePlan(SqliteConnection c, SqliteTransaction tx, ControlRestructureSpec spec)
    {
        RequireKnowledgeText(spec.Reason, "Restructure rationale");
        if (spec.Sources is null || spec.Sources.Count == 0 || spec.Replacements is null || spec.Replacements.Length == 0 ||
            spec.GoalMappings is null || spec.Groups is null || spec.GroupMappings is null)
            throw new ArgumentException("Sources, successors and explicit goal/group mappings are required.");
        if (spec.GoalMappings.Any(m => m is null) || spec.GroupMappings.Any(m => m is null) || spec.Groups.Any(g => g is null || g.Members is null))
            throw new ArgumentException("Mappings and relationship specifications cannot be null.");
        var packages = new Dictionary<string, WorkPackageVersion>(StringComparer.Ordinal);
        var ids = new List<string>();
        using (var q = ControlSql(c, tx, "SELECT package_id FROM control_packages ORDER BY package_id"))
        using (var r = q.ExecuteReader()) while (r.Read()) ids.Add(r.GetString(0));
        foreach (var id in ids) { var package = CurrentControlPackage(c, tx, id); if (!package.Superseded) packages.Add(id, package); }
        foreach (var (id, version) in spec.Sources)
            if (!packages.TryGetValue(id, out var package) || package.Version != version || HasControlIntegration(c, tx, id))
                throw new InvalidOperationException("A source contract is missing, obsolete, superseded or already integrated.");
        foreach (var replacement in spec.Replacements)
        {
            if (replacement is null) throw new ArgumentException("A successor specification is required.");
            ValidatePackage(replacement);
            if (ids.Contains(replacement.Id, StringComparer.Ordinal)) throw new InvalidOperationException("Successors must have new identities, including no reuse of historical IDs.");
        }
        var replacements = spec.Replacements.ToDictionary(p => p.Id, StringComparer.Ordinal);
        if (spec.GoalMappings.Select(m => m.SourceId).Distinct(StringComparer.Ordinal).Count() != spec.Sources.Count ||
            spec.GoalMappings.Length != spec.Sources.Count) throw new ArgumentException("Every source goal needs exactly one explicit successor mapping.");
        var mapping = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var goal in spec.GoalMappings)
        {
            RequireKnowledgeText(goal.Reason, "Goal carryover rationale");
            if (!spec.Sources.ContainsKey(goal.SourceId) || goal.SuccessorIds is null || goal.SuccessorIds.Length == 0 ||
                goal.SuccessorIds.Distinct(StringComparer.Ordinal).Count() != goal.SuccessorIds.Length || goal.SuccessorIds.Any(id => !replacements.ContainsKey(id)))
                throw new ArgumentException("A source goal must map to concrete successors in this proposal.");
            mapping.Add(goal.SourceId, goal.SuccessorIds);
        }
        if (replacements.Keys.Except(mapping.Values.SelectMany(v => v), StringComparer.Ordinal).Any())
            throw new ArgumentException("Every successor must carry a declared source goal.");

        var trial = packages.Where(p => !spec.Sources.ContainsKey(p.Key)).ToDictionary(p => p.Key, p => p.Value.Spec, StringComparer.Ordinal);
        var changed = new HashSet<string>(spec.Sources.Keys, StringComparer.Ordinal);
        bool more;
        do
        {
            more = false;
            foreach (var package in packages.Values)
                if (package.Spec.DependsOn.Any(changed.Contains) && changed.Add(package.Spec.Id)) more = true;
        } while (more);
        var changes = new Dictionary<string, WorkPackageSpec>(StringComparer.Ordinal);
        foreach (var id in changed.Except(spec.Sources.Keys, StringComparer.Ordinal))
        {
            if (HasControlDelivery(c, tx, id)) throw new InvalidOperationException("A dependent already has evidence; include it explicitly among the sources rather than silently remapping it.");
            var package = trial[id];
            changes[id] = trial[id] = package with { DependsOn = package.DependsOn.SelectMany(d => mapping.TryGetValue(d, out var targets) ? targets : [d]).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray() };
        }
        foreach (var replacement in replacements.Values) trial.Add(replacement.Id, replacement);
        ValidateRestructureGraph(trial);
        var activeGroups = ReadControlGroups(c, tx).Where(g => !g.Superseded).ToArray();
        var oldGroups = activeGroups.Where(g => g.Spec.Members.Any(spec.Sources.ContainsKey)).ToArray();
        if (spec.GroupMappings.Length != oldGroups.Length || spec.GroupMappings.Select(m => m.SourceGroupId).Distinct(StringComparer.Ordinal).Count() != oldGroups.Length)
            throw new ArgumentException("Every affected group requires an explicit new relationship mapping.");
        var newGroups = spec.Groups.ToDictionary(g => g.Id, StringComparer.Ordinal);
        var expectedMembers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in oldGroups)
        {
            var map = spec.GroupMappings.SingleOrDefault(m => m.SourceGroupId == group.Spec.Id)
                ?? throw new ArgumentException("An affected relationship is not mapped.");
            RequireKnowledgeText(map.Reason, "Relationship change rationale");
            if (map.SuccessorGroupIds is null || map.SuccessorGroupIds.Distinct(StringComparer.Ordinal).Count() != map.SuccessorGroupIds.Length ||
                map.SuccessorGroupIds.Any(id => !newGroups.ContainsKey(id))) throw new ArgumentException("Relationship successors must be explicitly defined.");
            var expected = group.Spec.Members.SelectMany(id => mapping.TryGetValue(id, out var targets) ? targets : [id]).Distinct(StringComparer.Ordinal).ToArray();
            if (map.SuccessorGroupIds.Length == 0)
            {
                if (expected.Length > 1) throw new ArgumentException("A multi-member relationship cannot disappear without replacement.");
            }
            else
            {
                if (expected.Except(map.SuccessorGroupIds.SelectMany(id => newGroups[id].Members), StringComparer.Ordinal).Any())
                    throw new ArgumentException("Replacement relationships must retain all mapped members.");
                expectedMembers.UnionWith(expected);
            }
        }
        if (newGroups.Keys.Except(spec.GroupMappings.SelectMany(m => m.SuccessorGroupIds), StringComparer.Ordinal).Any() ||
            !expectedMembers.SetEquals(newGroups.Values.SelectMany(g => g.Members)))
            throw new ArgumentException("New relationships must exactly cover the explicitly replaced relationships.");
        foreach (var group in newGroups.Values)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(group.Id);
            if (ReadControlGroups(c, tx).Any(g => g.Spec.Id == group.Id)) throw new InvalidOperationException("A successor group needs a new identity.");
            if (!Enum.IsDefined(group.Relation) || group.Members is null || group.Members.Length < 2 ||
                group.Members.Distinct(StringComparer.Ordinal).Count() != group.Members.Length || group.Members.Any(id => !trial.ContainsKey(id)))
                throw new ArgumentException("Invalid successor relationship members.");
            if (group.Relation == ControlRelation.Competitive && (!GitCliEvidence.IsSha(group.Baseline ?? "") ||
                group.Criteria is null || group.Criteria.Length == 0 || group.Criteria.Any(string.IsNullOrWhiteSpace)))
                throw new ArgumentException("Competition requires a common committed baseline and concrete criteria.");
            if (group.Relation == ControlRelation.Pipeline)
                for (var i = 1; i < group.Members.Length; i++)
                    if (!trial[group.Members[i]].DependsOn.Contains(group.Members[i - 1], StringComparer.Ordinal))
                        throw new ArgumentException("Successor pipeline edges must be part of the explicit package contracts.");
        }
        var competing = activeGroups.Where(g => !oldGroups.Contains(g)).Select(g => g.Spec).Concat(newGroups.Values)
            .Where(g => g.Relation == ControlRelation.Competitive).SelectMany(g => g.Members);
        if (competing.GroupBy(id => id, StringComparer.Ordinal).Any(g => g.Count() > 1))
            throw new ArgumentException("A package cannot join multiple successor competitions.");
        var affected = changed.Concat(oldGroups.SelectMany(g => g.Spec.Members)).ToHashSet(StringComparer.Ordinal);
        if (affected.Any(id => HasControlIntegration(c, tx, id))) throw new InvalidOperationException("An integrated outcome cannot be replaced through relationship restructuring.");
        // Pin relevant prerequisites too. Unrelated work does not invalidate a waiting proposal.
        var context = affected.ToHashSet(StringComparer.Ordinal);
        void AddAncestors(string id)
        {
            foreach (var dependency in trial[id].DependsOn)
                if (context.Add(dependency)) AddAncestors(dependency);
        }
        foreach (var id in trial.Keys.Where(id => affected.Contains(id) || replacements.ContainsKey(id)).ToArray()) AddAncestors(id);
        var token = Sha(JsonSerializer.Serialize(new
        {
            contracts = context.Where(packages.ContainsKey).Order(StringComparer.Ordinal).Select(id => new
            {
                package = packages[id], outcome = ReadControlOutcome(c, tx, id).EvidenceToken,
                facts = (packages[id].Spec.KnowledgeRefs ?? []).Order(StringComparer.Ordinal).Select(key => new { key, version = CurrentKnowledgeVersion(c, tx, key) })
            }).ToArray(),
            groups = activeGroups.Where(g => g.Spec.Members.Any(context.Contains)).OrderBy(g => g.Spec.Id, StringComparer.Ordinal).ToArray()
        }));
        return new(changes, affected, token);
    }

    private static void ValidateRestructureGraph(Dictionary<string, WorkPackageSpec> trial)
    {
        var visiting = new HashSet<string>(StringComparer.Ordinal); var done = new HashSet<string>(StringComparer.Ordinal);
        void Visit(string id)
        {
            if (done.Contains(id)) return;
            if (!trial.TryGetValue(id, out var package)) throw new ArgumentException("Missing or superseded dependency: " + id);
            if (!visiting.Add(id)) throw new ArgumentException("Successor dependencies contain a cycle.");
            foreach (var dependency in package.DependsOn) Visit(dependency);
            visiting.Remove(id); done.Add(id);
        }
        foreach (var id in trial.Keys) Visit(id);
    }

    private static bool HasControlDelivery(SqliteConnection c, SqliteTransaction tx, string id) => ControlEvidenceExists(c, tx, id, false);
    private static bool HasControlIntegration(SqliteConnection c, SqliteTransaction tx, string id) => ControlEvidenceExists(c, tx, id, true);
    private static bool ControlEvidenceExists(SqliteConnection c, SqliteTransaction tx, string id, bool integrated)
    {
        using var q = ControlSql(c, tx, """
            SELECT EXISTS(SELECT 1 FROM control_intents i JOIN control_bindings b ON b.intent_id=i.intent_id
              JOIN delivery_parts p ON p.task_id=b.task_id WHERE i.package_id=$id AND p.current_revision>0
              AND ($integrated=0 OR EXISTS(SELECT 1 FROM integration_marks m WHERE m.part_id=p.part_id)))
            """, ("$id", id), ("$integrated", integrated ? 1 : 0));
        return Convert.ToInt64(q.ExecuteScalar()) != 0;
    }

    private static void ApplyRestructurePlan(SqliteConnection c, SqliteTransaction tx, string proposalId,
        ControlRestructureSpec spec, RestructurePlan plan, long nowUtc)
    {
        foreach (var (id, version) in spec.Sources)
        {
            SupersedeControlIntents(c, tx, id, nowUtc);
            using var q = ControlSql(c, tx, """
                INSERT INTO control_retired_packages VALUES($id,$v,$proposal);
                UPDATE control_knowledge_adoptions SET status='superseded' WHERE package_id=$id AND status='pending';
                """, ("$id", id), ("$v", version), ("$proposal", proposalId)); q.ExecuteNonQuery();
        }
        foreach (var mapping in spec.GroupMappings)
        {
            using var q = ControlSql(c, tx, "INSERT INTO control_retired_groups VALUES($id,$proposal)", ("$id", mapping.SourceGroupId), ("$proposal", proposalId));
            q.ExecuteNonQuery();
        }
        foreach (var (id, replacement) in plan.Changes)
        {
            var current = CurrentControlPackage(c, tx, id);
            using var q = ControlSql(c, tx, """
                INSERT INTO control_package_versions VALUES($id,$v,$spec);
                UPDATE control_packages SET version=$v,approved_version=NULL WHERE package_id=$id;
                """, ("$id", id), ("$v", checked(current.Version + 1)), ("$spec", JsonSerializer.Serialize(replacement))); q.ExecuteNonQuery();
            SupersedeControlIntents(c, tx, id, nowUtc); CopyControlKnowledge(c, tx, id);
        }
        foreach (var replacement in spec.Replacements)
        {
            using var q = ControlSql(c, tx, """
                INSERT INTO control_packages VALUES($id,1,NULL);
                INSERT INTO control_package_versions VALUES($id,1,$spec);
                """, ("$id", replacement.Id), ("$spec", JsonSerializer.Serialize(replacement))); q.ExecuteNonQuery();
        }
        foreach (var group in spec.Groups)
        {
            using (var q = ControlSql(c, tx, "INSERT INTO control_groups(group_id,spec_json) VALUES($id,$spec)", ("$id", group.Id), ("$spec", JsonSerializer.Serialize(group)))) q.ExecuteNonQuery();
            for (var i = 0; i < group.Members.Length; i++)
            {
                using var q = ControlSql(c, tx, "INSERT INTO control_group_members VALUES($group,$id,$ordinal)", ("$group", group.Id), ("$id", group.Members[i]), ("$ordinal", i)); q.ExecuteNonQuery();
            }
        }
    }

    private static IReadOnlyList<ControlRestructureState> ReadControlRestructures(SqliteConnection c, SqliteTransaction tx)
    {
        var rows = new List<(string Id, ControlRestructureSpec Spec, WorkPackageVersion[] Contracts, string Token, string Status, string Actor, string? By, string? Reason)>();
        using (var q = ControlSql(c, tx, "SELECT proposal_id,spec_json,contracts_json,state_token,status,proposed_by,decided_by,decision_reason FROM control_restructures ORDER BY proposal_id"))
        using (var r = q.ExecuteReader()) while (r.Read()) rows.Add((r.GetString(0), JsonSerializer.Deserialize<ControlRestructureSpec>(r.GetString(1))!,
            JsonSerializer.Deserialize<WorkPackageVersion[]>(r.GetString(2))!, r.GetString(3), r.GetString(4), r.GetString(5),
            r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7)));
        return rows.Select(row =>
        {
            var current = false; string? blocked = null;
            if (row.Status == "pending")
                try
                {
                    var plan = BuildRestructurePlan(c, tx, row.Spec); current = plan.Token == row.Token;
                    if (current) blocked = plan.Affected.Select(id => KnowledgeWriterBlock(c, tx, id)).FirstOrDefault(reason => reason is not null);
                }
                catch (Exception e) when (e is ArgumentException or InvalidOperationException or KeyNotFoundException) { current = false; }
            return new ControlRestructureState(row.Id, row.Spec, row.Contracts, row.Status, row.Actor, row.By, row.Reason, current, blocked);
        }).ToArray();
    }
}
