using Fufucode.Core.Automation;
using Microsoft.Data.Sqlite;

internal static class MigrationChecks
{
    public static void Run(string root, Action<bool, string> check)
    {
        string Legacy(string name)
        {
            var path = Path.Combine(root, name + ".db");
            using (var seed = new AutomationStore(path))
            {
                seed.CreateThread("thread", "fixture", "codex", "devin/swe-2", root, "saved-session", 1);
                seed.CreateTask("task", "thread", "legacy", new string('a', 40), 1);
                foreach (var part in new[] { "accepted", "blocked" })
                {
                    seed.CreatePart(part, "task", part, 2);
                    seed.RecordDelivery("delivery-" + part, part, "run-" + part, new string('b', 40), "{\"source\":\"unchanged legacy card\"}", "event-" + part, 3);
                    seed.RecordDecision("decision-" + part, part, part == "accepted" ? AutomationDecision.Accept : AutomationDecision.Blocked,
                        "Original finding", "{\"checks\":\"legacy\"}", "original-reviewer", "review-" + part, 4, "run-" + part, new string('b', 40), 1);
                }
                seed.MarkIntegrated("accepted", 1, new string('c', 40), "integration", 5);
            }
            Sql(path, """
                DROP TABLE control_review_reopens;
                CREATE TABLE part_decisions_v1 (
                  decision_id TEXT PRIMARY KEY, part_id TEXT NOT NULL REFERENCES delivery_parts(part_id),
                  revision_number INTEGER NOT NULL, decision INTEGER NOT NULL, findings TEXT,
                  evidence_json TEXT, decided_by TEXT NOT NULL, decided_utc INTEGER NOT NULL,
                  decision_schema_version INTEGER NOT NULL, UNIQUE(part_id,revision_number));
                INSERT INTO part_decisions_v1 SELECT * FROM part_decisions ORDER BY rowid;
                DROP TABLE part_decisions;
                ALTER TABLE part_decisions_v1 RENAME TO part_decisions;
                PRAGMA user_version=1;
                """);
            return path;
        }
        var db = Legacy("legacy-review-schema"); var original = Decisions(db);
        check(Scalar(db, "PRAGMA user_version") == 1, "migration fixture has the actual v1 unique decision schema");
        using (var migrated = new AutomationStore(db))
        {
            check(Scalar(db, "PRAGMA user_version") == 2 && Decisions(db) == original, "schema upgrade preserves every legacy decision field and insertion order");
            check(migrated.IsIntegrated("accepted", 1) && migrated.GetThread("thread").SavedSessionId == "saved-session" &&
                migrated.ListRevisions("blocked").Single().CardJson == "{\"source\":\"unchanged legacy card\"}",
                "migration preserves integration marks, session identity, original run/HEAD and card bytes");
            check(migrated.ListDecisions("blocked").Single().DecidedBy == "original-reviewer", "new reader preserves immutable legacy reviewer identity");
        }
        using (var reopened = new AutomationStore(db))
            check(Decisions(db) == original && reopened.ListDecisions("accepted").Count == 1, "reopening a migrated database does not migrate twice or duplicate history");
        var corrupt = Legacy("legacy-invalid-reference");
        Sql(corrupt, "PRAGMA foreign_keys=OFF; INSERT INTO part_decisions VALUES('invalid','missing-part',1,2,NULL,NULL,'fixture',7,1)");
        var corruptBefore = Decisions(corrupt);
        try { using var rejected = new AutomationStore(corrupt); throw new Exception("Invalid legacy foreign key was migrated"); }
        catch (SqliteException) { check(true, "invalid legacy reference fails the upgrade closed"); }
        check(Scalar(corrupt, "PRAGMA user_version") == 1 && Decisions(corrupt) == corruptBefore &&
            Scalar(corrupt, "SELECT count(*) FROM sqlite_master WHERE name='part_decisions_v2'") == 0,
            "failed upgrade rolls back all schema/data changes and preserves original version/history");
        Sql(corrupt, "DELETE FROM part_decisions WHERE decision_id='invalid'");
        using (var repaired = new AutomationStore(corrupt))
            check(repaired.ListDecisions("accepted").Count == 1 && Scalar(corrupt, "PRAGMA user_version") == 2, "corrected isolated legacy fixture can retry one intact migration");
        var parallel = Legacy("legacy-concurrent-upgrade"); var before = Decisions(parallel);
        using var barrier = new Barrier(2);
        Task Open() => Task.Run(() => { barrier.SignalAndWait(); using var store = new AutomationStore(parallel); });
        Task.WhenAll(Open(), Open()).GetAwaiter().GetResult();
        check(Scalar(parallel, "PRAGMA user_version") == 2 && Decisions(parallel) == before,
            "concurrent v1 openers perform one upgrade without deleting or duplicating decisions");
    }

    private static void Sql(string db, string sql)
    { using var c = new SqliteConnection("Data Source=" + db); c.Open(); using var q = c.CreateCommand(); q.CommandText = sql; q.ExecuteNonQuery(); }
    private static long Scalar(string db, string sql)
    { using var c = new SqliteConnection("Data Source=" + db); c.Open(); using var q = c.CreateCommand(); q.CommandText = sql; return Convert.ToInt64(q.ExecuteScalar()); }
    private static string Decisions(string db)
    {
        using var c = new SqliteConnection("Data Source=" + db); c.Open(); using var q = c.CreateCommand();
        q.CommandText = "SELECT * FROM part_decisions ORDER BY rowid"; using var r = q.ExecuteReader();
        var rows = new List<object[]>(); while (r.Read()) { var row = new object[r.FieldCount]; r.GetValues(row); rows.Add(row); }
        return System.Text.Json.JsonSerializer.Serialize(rows);
    }
}
