using System.Text.Json;

namespace Fufucode.Core.Automation;

/// <summary>
/// One versioned project goal as submitted by the main control conversation. A goal is a requirement
/// statement plus its acceptance, never a plan and never proof of execution: the plan (work packages)
/// is derived from it later and versioned separately, so a new goal cannot silently delete an
/// existing accepted requirement.
/// </summary>
public sealed record ProjectGoalVersion(string GoalId, long Version, string Statement,
    string[] Acceptance, string SubmittedBy, long SubmittedUtc, string Status)
{
    public string[] Scope { get; init; } = [];
}

public sealed record ProjectGoalCommand(string CommandId, long ExpectedGoalVersion, string SubmittedBy,
    string GoalId, string Statement, string[] Acceptance, DevelopmentMode? ActivateMode = null)
{
    public string[] Scope { get; init; } = [];
}

public sealed record ProjectGoalReceipt(string CommandId, long Revision, string GoalId, long Version,
    string Status, DevelopmentMode Mode);

public sealed partial class AutomationStore
{
    /// <summary>
    /// CONV-3 additive tables. They live in the same project database; the U1 tables, control plan,
    /// decisions and event journal are untouched.
    /// </summary>
    private void MigrateConvGoals()
    {
        using var c = OpenConv();
        using var tx = c.BeginTransaction();
        using var q = ConvSql(c, tx, """
            CREATE TABLE IF NOT EXISTS conv_goals (
              goal_id TEXT PRIMARY KEY, current_version INTEGER NOT NULL, created_utc INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS conv_goal_versions (
              goal_id TEXT NOT NULL, version INTEGER NOT NULL, statement TEXT NOT NULL,
              acceptance_json TEXT NOT NULL, scope_json TEXT NOT NULL, submitted_by TEXT NOT NULL,
              submitted_utc INTEGER NOT NULL, status TEXT NOT NULL, command_id TEXT NOT NULL UNIQUE,
              PRIMARY KEY(goal_id,version));
            CREATE TABLE IF NOT EXISTS conv_goal_receipts (
              command_id TEXT PRIMARY KEY, payload_hash TEXT NOT NULL, result_json TEXT NOT NULL);
            """);
        q.ExecuteNonQuery();
        tx.Commit();
    }

    private int _convGoalsMigrated;
    private void EnsureConvGoalsMigratedOnce()
    {
        if (Interlocked.Exchange(ref _convGoalsMigrated, 1) == 1) return;
        MigrateConvGoals();
    }

    /// <summary>
    /// Record one new goal version. The command id makes a retry a replay, not a second version. Only
    /// the exact expected current version is accepted, so two conversations cannot silently overwrite
    /// each other's requirement; the previous version is closed as 'superseded' rather than deleted.
    /// </summary>
    public ProjectGoalReceipt ApplyProjectGoal(ProjectGoalCommand command, long nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command.CommandId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.SubmittedBy);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.GoalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Statement);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ActivateMode is null ? "goal" : "goal");
        EnsureConvGoalsMigratedOnce();
        var payload = JsonSerializer.Serialize(command);
        var digest = Sha("goal:" + payload);
        using var c = OpenConv();
        using var tx = c.BeginTransaction();
        using (var replay = ConvSql(c, tx,
            "SELECT payload_hash,result_json FROM conv_goal_receipts WHERE command_id=$id",
            ("$id", command.CommandId)))
        using (var r = replay.ExecuteReader())
        {
            if (r.Read())
            {
                if (r.GetString(0) != digest)
                    throw new InvalidOperationException("Goal command identity reused with different input.");
                return JsonSerializer.Deserialize<ProjectGoalReceipt>(r.GetString(1))!;
            }
        }
        long current;
        using (var head = ConvSql(c, tx, "SELECT current_version FROM conv_goals WHERE goal_id=$id",
            ("$id", command.GoalId)))
            current = head.ExecuteScalar() is long value ? value : 0;
        if (current != command.ExpectedGoalVersion)
            throw new InvalidOperationException("The project goal changed; refresh before submitting a new version.");
        var next = checked(current + 1);
        using (var write = ConvSql(c, tx, """
            INSERT INTO conv_goals(goal_id,current_version,created_utc) VALUES($id,$v,$now)
              ON CONFLICT(goal_id) DO UPDATE SET current_version=$v;
            UPDATE conv_goal_versions SET status='superseded' WHERE goal_id=$id AND status='active';
            INSERT INTO conv_goal_versions(goal_id,version,statement,acceptance_json,scope_json,
              submitted_by,submitted_utc,status,command_id)
            VALUES($id,$v,$statement,$accept,$scope,$by,$now,'active',$cmd);
            """,
            ("$id", command.GoalId), ("$v", next), ("$now", nowUtc), ("$statement", command.Statement),
            ("$accept", JsonSerializer.Serialize(command.Acceptance)),
            ("$scope", JsonSerializer.Serialize(command.Scope)), ("$by", command.SubmittedBy),
            ("$cmd", command.CommandId)))
            write.ExecuteNonQuery();
        // Read the mode on the caller's own connection/transaction. A nested connection would try to
        // acquire a second write lock while this transaction is still open, which SQLite refuses.
        var mode = ReadControlSnapshot(c, tx).Mode;
        // Submitting a goal through the main control conversation activates the requested mode in the
        // same transaction as the goal version. Automatic is only ever entered as an explicit
        // ActivateMode on a real goal; this never flips an existing manual/paused project implicitly.
        if (command.ActivateMode is { } requested && requested != mode)
        {
            using var activate = ConvSql(c, tx, """
                UPDATE control_state SET revision=revision+1,mode=$mode WHERE id=1;
                """, ("$mode", requested.ToString()));
            activate.ExecuteNonQuery();
            mode = requested;
            // Materialize (or suspend) the execution intents for the new mode so an approved plan is
            // actually runnable after activation, exactly like an explicit SetMode command.
            RefreshControlIntentsForGoal(c, tx, mode);
        }
        var receipt = new ProjectGoalReceipt(command.CommandId, next, command.GoalId, next, "active", mode);
        AppendConvEvent(c, tx, "conv:goal:" + command.CommandId, "control.goal.submitted", "", null, null,
            null, JsonSerializer.Serialize(receipt), nowUtc);
        using (var save = ConvSql(c, tx, "INSERT INTO conv_goal_receipts VALUES($id,$hash,$json)",
            ("$id", command.CommandId), ("$hash", digest), ("$json", JsonSerializer.Serialize(receipt))))
            save.ExecuteNonQuery();
        tx.Commit();
        return receipt;
    }

    public IReadOnlyList<ProjectGoalVersion> ListProjectGoals(string? goalId = null)
    {
        EnsureConvGoalsMigratedOnce();
        using var c = OpenConv();
        using var q = ConvSql(c, null, """
            SELECT goal_id,version,statement,acceptance_json,scope_json,submitted_by,submitted_utc,status
            FROM conv_goal_versions WHERE ($id IS NULL OR goal_id=$id) ORDER BY goal_id,version
            """, ("$id", (object?)goalId ?? DBNull.Value));
        using var r = q.ExecuteReader();
        var goals = new List<ProjectGoalVersion>();
        while (r.Read())
            goals.Add(new(r.GetString(0), r.GetInt64(1), r.GetString(2),
                JsonSerializer.Deserialize<string[]>(r.GetString(3)) ?? [], r.GetString(5),
                r.GetInt64(6), r.GetString(7))
            { Scope = JsonSerializer.Deserialize<string[]>(r.GetString(4)) ?? [] });
        return goals;
    }

    public ProjectGoalVersion? CurrentProjectGoal(string goalId)
    {
        EnsureConvGoalsMigratedOnce();
        using var c = OpenConv();
        using var q = ConvSql(c, null, """
            SELECT v.goal_id,v.version,v.statement,v.acceptance_json,v.scope_json,v.submitted_by,
                   v.submitted_utc,v.status FROM conv_goal_versions v
            JOIN conv_goals g ON g.goal_id=v.goal_id AND g.current_version=v.version
            WHERE v.goal_id=$id
            """, ("$id", goalId));
        using var r = q.ExecuteReader();
        if (!r.Read()) return null;
        return new(r.GetString(0), r.GetInt64(1), r.GetString(2),
            JsonSerializer.Deserialize<string[]>(r.GetString(3)) ?? [], r.GetString(5), r.GetInt64(6),
            r.GetString(7)) { Scope = JsonSerializer.Deserialize<string[]>(r.GetString(4)) ?? [] };
    }
}
