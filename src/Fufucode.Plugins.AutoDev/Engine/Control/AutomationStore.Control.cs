using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Fufucode.Core.Automation;

public sealed partial class AutomationStore
{
    // Additive extension in the same U1 database. U1 owns user_version;
    // this does not replace its tasks, decisions, event journal, leases or harness tables.
    private void MigrateControl()
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        Execute(c, tx, """
            CREATE TABLE IF NOT EXISTS control_state (
                id INTEGER PRIMARY KEY CHECK(id=1), revision INTEGER NOT NULL, mode TEXT NOT NULL);
            INSERT OR IGNORE INTO control_state VALUES(1,0,'Manual');
            CREATE TABLE IF NOT EXISTS control_packages (
                package_id TEXT PRIMARY KEY, version INTEGER NOT NULL, approved_version INTEGER);
            CREATE TABLE IF NOT EXISTS control_package_versions (
                package_id TEXT NOT NULL REFERENCES control_packages(package_id),
                version INTEGER NOT NULL, spec_json TEXT NOT NULL,
                PRIMARY KEY(package_id,version));
            CREATE TABLE IF NOT EXISTS control_approvals (
                command_id TEXT PRIMARY KEY, package_id TEXT NOT NULL, version INTEGER NOT NULL,
                decided_by TEXT NOT NULL, mode TEXT NOT NULL, decided_utc INTEGER NOT NULL,
                FOREIGN KEY(package_id,version) REFERENCES control_package_versions(package_id,version));
            CREATE TABLE IF NOT EXISTS control_intents (
                intent_id TEXT PRIMARY KEY, package_id TEXT NOT NULL, version INTEGER NOT NULL,
                status TEXT NOT NULL, created_revision INTEGER NOT NULL,
                UNIQUE(package_id,version),
                FOREIGN KEY(package_id,version) REFERENCES control_package_versions(package_id,version));
            CREATE TABLE IF NOT EXISTS control_receipts (
                command_id TEXT PRIMARY KEY, payload_hash TEXT NOT NULL, result_json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS control_bindings (
                intent_id TEXT PRIMARY KEY REFERENCES control_intents(intent_id),
                run_id TEXT NOT NULL UNIQUE REFERENCES host_requests(run_id),
                thread_id TEXT NOT NULL REFERENCES threads(thread_id),
                task_id TEXT NOT NULL UNIQUE REFERENCES tasks(task_id),
                payload_hash TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS control_legacy_archives (
                source_id TEXT NOT NULL, source_revision INTEGER NOT NULL, content_sha256 TEXT NOT NULL,
                source_json TEXT NOT NULL, decided_by TEXT NOT NULL, captured_utc INTEGER NOT NULL,
                PRIMARY KEY(source_id,source_revision));
            CREATE TABLE IF NOT EXISTS control_groups (
                group_id TEXT PRIMARY KEY, spec_json TEXT NOT NULL, choice_id TEXT);
            CREATE TABLE IF NOT EXISTS control_group_members (
                group_id TEXT NOT NULL REFERENCES control_groups(group_id),
                package_id TEXT NOT NULL REFERENCES control_packages(package_id),
                ordinal INTEGER NOT NULL, PRIMARY KEY(group_id,package_id));
            CREATE TABLE IF NOT EXISTS control_group_choices (
                command_id TEXT PRIMARY KEY, group_id TEXT NOT NULL REFERENCES control_groups(group_id),
                winner TEXT NOT NULL REFERENCES control_packages(package_id),
                evidence_token TEXT NOT NULL, versions_json TEXT NOT NULL, decided_by TEXT NOT NULL,
                decided_utc INTEGER NOT NULL);
            """);
        MigrateControlKnowledge(c, tx);
        MigrateControlRestructure(c, tx);
        MigrateControlIntegration(c, tx);
        MigrateControlReviewReopens(c, tx);
        MigrateControlRepairs(c, tx);
        MigrateControlStops(c, tx);
        MigrateControlAuthority(c, tx);
        tx.Commit();
    }

    /// <summary>
    /// Plan mutation, approval, planning intent, receipt and U1 event commit together.
    /// No subprocess or HostJournal enqueue happens here. Human registration is never an adapter report.
    /// </summary>
    public ControlReceipt ApplyControlCommand(ControlCommand command, long nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command.CommandId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.DecidedBy);
        var payload = JsonSerializer.Serialize(command);
        var hash = Sha(payload);
        using var c = Open();
        using var tx = c.BeginTransaction();
        using (var replay = ControlSql(c, tx,
            "SELECT payload_hash,result_json FROM control_receipts WHERE command_id=$id", ("$id", command.CommandId)))
        using (var r = replay.ExecuteReader())
        {
            if (r.Read())
            {
                if (r.GetString(0) != hash)
                    throw new InvalidOperationException("Control command identity reused with different input.");
                return JsonSerializer.Deserialize<ControlReceipt>(r.GetString(1))!;
            }
        }
        long revision;
        DevelopmentMode mode;
        using (var state = ControlSql(c, tx, "SELECT revision,mode FROM control_state WHERE id=1"))
        using (var r = state.ExecuteReader())
        {
            r.Read(); revision = r.GetInt64(0); mode = Enum.Parse<DevelopmentMode>(r.GetString(1));
        }
        if (revision != command.ExpectedRevision)
            throw new InvalidOperationException("Control state changed; refresh before deciding.");
        var next = checked(revision + 1);
        string? packageId = null;
        long? packageVersion = null;
        switch (command.Action)
        {
            case ControlAction.DefinePackage:
            {
                var spec = command.Package ?? throw new ArgumentException("Package specification is required.");
                ValidatePackage(spec);
                packageId = spec.Id;
                if (ControlPackageRetired(c, tx, packageId)) throw new InvalidOperationException("A superseded identity cannot be reused; define a successor.");
                using var current = ControlSql(c, tx,
                    "SELECT version FROM control_packages WHERE package_id=$id", ("$id", spec.Id));
                var previous = current.ExecuteScalar();
                var version = previous is null ? 0 : Convert.ToInt64(previous);
                if (command.ExpectedPackageVersion != version)
                    throw new InvalidOperationException("Package version changed; refresh before editing.");
                packageVersion = ReplaceControlPackage(c, tx, spec, version, nowUtc);
                break;
            }
            case ControlAction.ApprovePackage:
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(command.PackageId);
                packageId = command.PackageId;
                if (ControlPackageRetired(c, tx, packageId)) throw new InvalidOperationException("A superseded contract cannot be approved again.");
                using var current = ControlSql(c, tx,
                    "SELECT version FROM control_packages WHERE package_id=$id", ("$id", packageId));
                var version = current.ExecuteScalar() ?? throw new KeyNotFoundException(packageId);
                packageVersion = Convert.ToInt64(version);
                if (command.ExpectedPackageVersion != packageVersion)
                    throw new InvalidOperationException("Approval names an obsolete package version.");
                ApproveControlKnowledge(c, tx, CurrentControlPackage(c, tx, packageId));
                using var approve = ControlSql(c, tx, """
                    UPDATE control_packages SET approved_version=version WHERE package_id=$id;
                    INSERT INTO control_approvals(command_id,package_id,version,decided_by,mode,decided_utc)
                    VALUES($cmd,$id,$version,$actor,$mode,$now);
                    """, ("$id", packageId), ("$version", packageVersion.Value), ("$cmd", command.CommandId),
                    ("$actor", command.DecidedBy), ("$mode", mode.ToString()), ("$now", nowUtc));
                approve.ExecuteNonQuery();
                break;
            }
            case ControlAction.SetMode:
                mode = command.Mode ?? throw new ArgumentException("Mode is required.");
                if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(command));
                break;
            default: throw new ArgumentOutOfRangeException(nameof(command));
        }
        using (var update = ControlSql(c, tx,
            "UPDATE control_state SET revision=$next,mode=$mode WHERE id=1 AND revision=$old",
            ("$next", next), ("$mode", mode.ToString()), ("$old", revision)))
            if (update.ExecuteNonQuery() != 1) throw new InvalidOperationException("Concurrent control decision.");
        RefreshControlIntents(c, tx, mode, next);
        // An empty thread id denotes project-level planning. Never invent a real thread/task identity.
        AppendEvent(c, tx, "control:" + command.CommandId, "control." + command.Action,
            "", null, null, null, JsonSerializer.Serialize(new { command, revision = next }), nowUtc);
        var receipt = new ControlReceipt(command.CommandId, next, mode, packageId, packageVersion);
        using (var save = ControlSql(c, tx,
            "INSERT INTO control_receipts(command_id,payload_hash,result_json) VALUES($id,$hash,$result)",
            ("$id", command.CommandId), ("$hash", hash), ("$result", JsonSerializer.Serialize(receipt))))
            save.ExecuteNonQuery();
        tx.Commit();
        return receipt;
    }

    private static long ReplaceControlPackage(SqliteConnection c, SqliteTransaction tx,
        WorkPackageSpec spec, long previousVersion, long nowUtc)
    {
        foreach (var group in ReadControlGroups(c, tx).Where(g => !g.Superseded && g.Spec.Relation == ControlRelation.Pipeline))
        {
            var index = Array.IndexOf(group.Spec.Members, spec.Id);
            if (index > 0 && !spec.DependsOn.Contains(group.Spec.Members[index - 1], StringComparer.Ordinal))
                throw new InvalidOperationException("Changing a pipeline edge requires explicit restructuring of its group.");
        }
        var affected = ValidateControlDependencies(c, tx, spec);
        EnsureControlPackagesStopped(c, tx, affected);
        var next = checked(previousVersion + 1);
        using (var q = ControlSql(c, tx, """
            INSERT INTO control_packages(package_id,version,approved_version) VALUES($id,$version,NULL)
            ON CONFLICT(package_id) DO UPDATE SET version=$version,approved_version=NULL;
            INSERT INTO control_package_versions(package_id,version,spec_json) VALUES($id,$version,$spec);
            """, ("$id", spec.Id), ("$version", next), ("$spec", JsonSerializer.Serialize(spec)))) q.ExecuteNonQuery();
        foreach (var dependent in affected)
        {
            // The dependency contract changed even when the dependent's text did not. Give that
            // plan a fresh identity, rather than reusing a superseded intent/run after reapproval.
            using var q = ControlSql(c, tx, dependent == spec.Id ?
                "UPDATE control_packages SET approved_version=NULL WHERE package_id=$id" : """
                INSERT INTO control_package_versions(package_id,version,spec_json)
                SELECT p.package_id,p.version+1,v.spec_json FROM control_packages p
                JOIN control_package_versions v ON v.package_id=p.package_id AND v.version=p.version
                WHERE p.package_id=$id;
                UPDATE control_packages SET version=version+1,approved_version=NULL WHERE package_id=$id;
                """, ("$id", dependent));
            q.ExecuteNonQuery();
            SupersedeControlIntents(c, tx, dependent, nowUtc);
            CopyControlKnowledge(c, tx, dependent);
        }
        return next;
    }

    private static void RefreshControlIntents(SqliteConnection c, SqliteTransaction tx,
        DevelopmentMode mode, long revision)
    {
        RefreshControlIntentsForGoal(c, tx, mode, revision);
    }

    /// <summary>
    /// Shared intent materialization used by both SetMode and a goal submission that activates a mode.
    /// Passing a null revision re-reads the authoritative revision from the caller's transaction.
    /// </summary>
    internal static void RefreshControlIntentsForGoal(SqliteConnection c, SqliteTransaction tx,
        DevelopmentMode mode, long? revision = null)
    {
        long rev;
        if (revision is { } explicitRevision) rev = explicitRevision;
        else
        {
            using var read = ControlSql(c, tx, "SELECT revision FROM control_state WHERE id=1");
            rev = Convert.ToInt64(read.ExecuteScalar());
        }
        // Dependencies/relations/knowledge are checked by the future trusted binder. These rows are
        // explicitly awaiting_binding, never ready/running/completed and never inserted into host_requests.
        // SemiAutomatic and Automatic both materialize an execution intent per approved package: the
        // mode only changes who makes the review/integration decisions, never whether the approved
        // plan becomes runnable. Manual keeps every intent suspended.
        using var q = ControlSql(c, tx, mode != DevelopmentMode.Manual ? """
            INSERT OR IGNORE INTO control_intents(intent_id,package_id,version,status,created_revision)
            SELECT 'execute:' || package_id || ':' || version,package_id,version,'awaiting_binding',$rev
            FROM control_packages p WHERE approved_version=version
              AND NOT EXISTS(SELECT 1 FROM control_retired_packages r WHERE r.package_id=p.package_id);
            UPDATE control_intents SET status='awaiting_binding' WHERE status='suspended';
            """ : "UPDATE control_intents SET status='suspended' WHERE status='awaiting_binding';",
            ("$rev", rev));
        q.ExecuteNonQuery();
    }

    private static void ValidatePackage(WorkPackageSpec spec)
    {
        foreach (var text in new[] { spec.Id, spec.Title, spec.Goal, spec.Harness, spec.Model })
            ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (spec.Scope is null || spec.Scope.Length == 0 || spec.Scope.Any(string.IsNullOrWhiteSpace) ||
            spec.Acceptance is null || spec.Acceptance.Length == 0 || spec.Acceptance.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Scope and acceptance must have concrete entries.");
        if (spec.Artifact is not ("code" or "evidence")) throw new ArgumentException("Unknown artifact kind.");
        if (spec.DependsOn is null || spec.DependsOn.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Invalid dependencies.");
        if (spec.KnowledgeRefs is { } keys && (keys.Any(string.IsNullOrWhiteSpace) ||
            keys.Distinct(StringComparer.Ordinal).Count() != keys.Length))
            throw new ArgumentException("Knowledge references must be distinct nonempty keys.");
    }

    private static HashSet<string> ValidateControlDependencies(SqliteConnection c, SqliteTransaction tx, WorkPackageSpec spec)
    {
        var edges = new Dictionary<string, string[]>(StringComparer.Ordinal);
        using (var q = ControlSql(c, tx, """
            SELECT v.spec_json FROM control_package_versions v
            JOIN control_packages p ON p.package_id=v.package_id AND p.version=v.version
            WHERE NOT EXISTS(SELECT 1 FROM control_retired_packages r WHERE r.package_id=p.package_id)
            """))
        using (var r = q.ExecuteReader())
            while (r.Read())
            {
                var p = JsonSerializer.Deserialize<WorkPackageSpec>(r.GetString(0))!;
                edges.Add(p.Id, p.DependsOn);
            }
        edges[spec.Id] = spec.DependsOn;
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var done = new HashSet<string>(StringComparer.Ordinal);
        void Visit(string id)
        {
            if (done.Contains(id)) return;
            if (!edges.TryGetValue(id, out var dependencies)) throw new ArgumentException("Missing dependency: " + id);
            if (!visiting.Add(id)) throw new ArgumentException("Package dependencies contain a cycle.");
            foreach (var dependency in dependencies) Visit(dependency);
            visiting.Remove(id); done.Add(id);
        }
        foreach (var id in edges.Keys) Visit(id);
        var affected = new HashSet<string>(StringComparer.Ordinal) { spec.Id };
        bool changed;
        do
        {
            changed = false;
            foreach (var (id, dependencies) in edges)
                if (dependencies.Any(affected.Contains) && affected.Add(id)) changed = true;
        } while (changed);
        return affected;
    }

    public WorkPackageVersion GetWorkPackageVersion(string packageId, long version)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        using var q = ControlSql(c, tx, """
            SELECT v.spec_json,p.approved_version FROM control_package_versions v
            JOIN control_packages p ON p.package_id=v.package_id
            WHERE v.package_id=$id AND v.version=$version
            """, ("$id", packageId), ("$version", version));
        using var r = q.ExecuteReader();
        if (!r.Read()) throw new KeyNotFoundException(packageId);
        var package = new WorkPackageVersion(JsonSerializer.Deserialize<WorkPackageSpec>(r.GetString(0))!, version,
            !r.IsDBNull(1) && r.GetInt64(1) == version);
        r.Close();
        package = package with { AdoptedKnowledge = ReadAdoptedKnowledge(c, tx, packageId, version), Superseded = ControlPackageRetired(c, tx, packageId) };
        tx.Commit();
        return package;
    }

    /// <summary>One read transaction gives clients a coherent mode, revision, package and intent snapshot.</summary>
    public ControlSnapshot ReadControlSnapshot()
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        var snapshot = ReadControlSnapshot(c, tx);
        tx.Commit();
        return snapshot;
    }

    private static ControlSnapshot ReadControlSnapshot(SqliteConnection c, SqliteTransaction tx)
    {
        long revision;
        DevelopmentMode mode;
        using (var q = ControlSql(c, tx, "SELECT revision,mode FROM control_state WHERE id=1"))
        using (var r = q.ExecuteReader())
        { r.Read(); revision = r.GetInt64(0); mode = Enum.Parse<DevelopmentMode>(r.GetString(1)); }
        var packages = new List<WorkPackageVersion>();
        using (var q = ControlSql(c, tx, """
            SELECT v.spec_json,p.version,p.approved_version FROM control_packages p
            JOIN control_package_versions v ON p.package_id=v.package_id AND p.version=v.version ORDER BY p.package_id
            """))
        using (var r = q.ExecuteReader())
            while (r.Read()) packages.Add(new(JsonSerializer.Deserialize<WorkPackageSpec>(r.GetString(0))!,
                r.GetInt64(1), !r.IsDBNull(2) && r.GetInt64(2) == r.GetInt64(1)));
        packages = packages.Select(p => p with { AdoptedKnowledge = ReadAdoptedKnowledge(c, tx, p.Spec.Id, p.Version), Superseded = ControlPackageRetired(c, tx, p.Spec.Id) }).ToList();
        var intents = new List<ControlIntent>();
        using (var q = ControlSql(c, tx, "SELECT intent_id,package_id,version,status,created_revision FROM control_intents ORDER BY intent_id"))
        using (var r = q.ExecuteReader())
            while (r.Read()) intents.Add(new(r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetString(3), r.GetInt64(4)));
        return new(revision, mode, packages, intents)
        {
            Knowledge = ReadControlKnowledge(c, tx), Restructures = ReadControlRestructures(c, tx),
            IntegrationAuthorizations = ReadControlIntegrationAuthorizations(c, tx), Repairs = ReadControlRepairs(c, tx)
        };
    }

    private static SqliteCommand ControlSql(SqliteConnection c, SqliteTransaction? tx,
        string sql, params (string Name, object Value)[] parameters)
    {
        var q = c.CreateCommand(); q.Transaction = tx; q.CommandText = sql;
        foreach (var (name, value) in parameters) q.Parameters.AddWithValue(name, value);
        return q;
    }
}
