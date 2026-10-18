using Microsoft.Data.Sqlite;

namespace Fufucode.Core.Automation;

public sealed partial class AutomationStore
{
    // An original queued request may be explicitly abandoned by a contract replacement.
    // Extra writers on its owner thread/worktree still block every contract mutation.
    private static string? ControlAdditionalWriterBlock(SqliteConnection c, SqliteTransaction tx, string package)
    {
        using var q = ControlSql(c, tx, """
            SELECT CASE WHEN h.status='Unknown' THEN 'waiting_reconciliation:' ELSE 'waiting_stop:' END
              FROM control_writer_bindings b JOIN control_intents i ON i.intent_id=b.intent_id
              JOIN threads owner ON owner.thread_id=b.thread_id JOIN harness_runs h
                ON h.thread_id=b.thread_id OR
                  rtrim(replace(upper(h.worktree),'/',char(92)),char(92))=rtrim(replace(upper(owner.worktree),'/',char(92)),char(92))
              WHERE i.package_id=$id AND h.status IN ('Dispatching','Running','Unknown')
            UNION ALL
            SELECT 'waiting_stop:' FROM control_writer_bindings b JOIN control_intents i ON i.intent_id=b.intent_id
              JOIN threads owner ON owner.thread_id=b.thread_id JOIN host_requests q JOIN threads pending ON pending.thread_id=q.thread_id
              WHERE i.package_id=$id AND q.state IN (0,1) AND (q.thread_id=b.thread_id OR
                rtrim(replace(upper(pending.worktree),'/',char(92)),char(92))=rtrim(replace(upper(owner.worktree),'/',char(92)),char(92)))
              AND NOT EXISTS(SELECT 1 FROM control_writer_bindings own JOIN control_intents intent ON intent.intent_id=own.intent_id
                WHERE own.run_id=q.run_id AND intent.package_id=$id AND q.state=0)
            LIMIT 1
            """, ("$id", package));
        return q.ExecuteScalar() is string prefix ? prefix + package : null;
    }
}
