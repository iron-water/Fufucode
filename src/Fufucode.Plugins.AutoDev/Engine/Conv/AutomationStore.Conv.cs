using Microsoft.Data.Sqlite;

namespace Fufucode.Core.Automation;

/// <summary>
/// CONV-1 additive extension in the same U1 project database. It only adds new tables and helpers
/// for the managed-worktree lifecycle and the cross-thread inbox; U1's schema version, tables,
/// decisions, event journal, leases and harness journal are left exactly as they were.
/// </summary>
public sealed partial class AutomationStore
{
    /// <summary>One connection on the project database, for the CONV-1 partial implementations.</summary>
    internal SqliteConnection OpenConv() => Open();

    /// <summary>Append a deduplicated CONV event in the caller's transaction.</summary>
    internal long AppendConvEvent(SqliteConnection connection, SqliteTransaction tx, string eventId,
        string type, string threadId, string? taskId, string? partId, string? runId,
        string payloadJson, long occurredUtc) =>
        AppendEvent(connection, tx, eventId, type, threadId, taskId, partId, runId, payloadJson,
            occurredUtc);

    /// <summary>One read of a CONV table in a transaction owned by the caller.</summary>
    internal static SqliteCommand ConvSql(SqliteConnection connection, SqliteTransaction? tx,
        string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }
}
