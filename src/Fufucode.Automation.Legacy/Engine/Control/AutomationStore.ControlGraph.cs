using Microsoft.Data.Sqlite;

namespace Fufucode.Core.Automation;

public sealed partial class AutomationStore
{
    // Metadata only; never copies active cards, runtime environment or model output into the graph.
    private static ControlGraphSnapshot ReadControlGraph(SqliteConnection c, SqliteTransaction tx)
    {
        var threads = new List<(string Id, string Project, string Harness, string Model, ThreadStatus Status)>();
        using (var q = ControlSql(c, tx, "SELECT thread_id,project_id,harness,model,status FROM threads ORDER BY created_utc,thread_id"))
        using (var r = q.ExecuteReader()) while (r.Read()) threads.Add((r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), (ThreadStatus)r.GetInt32(4)));
        var nodes = new List<ControlThreadNode>();
        using var hasRuns = ControlSql(c, tx, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='harness_runs'");
        var runsExist = Convert.ToInt64(hasRuns.ExecuteScalar()) == 1;
        foreach (var thread in threads)
        {
            var tasks = new List<(string Id, string Title, AutomationTaskStatus Status)>();
            using (var q = ControlSql(c, tx, "SELECT task_id,title,status FROM tasks WHERE thread_id=$id ORDER BY created_utc,task_id", ("$id", thread.Id)))
            using (var r = q.ExecuteReader()) while (r.Read()) tasks.Add((r.GetString(0), r.GetString(1), (AutomationTaskStatus)r.GetInt32(2)));
            var taskNodes = new List<ControlTaskNode>();
            foreach (var task in tasks)
            {
                var parts = new List<ControlPartNode>();
                using var q = ControlSql(c, tx, """
                    SELECT p.part_id,p.title,p.status,p.current_revision,r.run_id,r.head_sha,
                      EXISTS(SELECT 1 FROM integration_marks m WHERE m.part_id=p.part_id AND m.revision_number=p.current_revision)
                    FROM delivery_parts p LEFT JOIN delivery_revisions r ON r.part_id=p.part_id AND r.revision_number=p.current_revision
                    WHERE p.task_id=$id ORDER BY p.part_id
                    """, ("$id", task.Id));
                using var r = q.ExecuteReader();
                while (r.Read()) parts.Add(new(r.GetString(0), r.GetString(1), (DeliveryPartStatus)r.GetInt32(2), r.GetInt64(3),
                    r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5), r.GetInt32(6) != 0));
                taskNodes.Add(new(task.Id, task.Title, task.Status, parts));
            }
            var running = false;
            if (runsExist)
            {
                using var q = ControlSql(c, tx, "SELECT EXISTS(SELECT 1 FROM harness_runs WHERE thread_id=$id AND status IN ('Dispatching','Running'))", ("$id", thread.Id));
                running = Convert.ToInt64(q.ExecuteScalar()) == 1;
            }
            nodes.Add(new(thread.Id, thread.Project, thread.Harness, thread.Model, thread.Status, running, taskNodes));
        }
        var edges = new List<ControlDependencyEdge>();
        using (var q = ControlSql(c, tx, "SELECT depends_on_task_id,task_id,depends_on_part_id,requires_integration FROM task_dependencies ORDER BY task_id,depends_on_task_id"))
        using (var r = q.ExecuteReader()) while (r.Read()) edges.Add(new(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetInt32(3) != 0));
        // Package dependencies resolve only actually bound tasks, never invented planned threads.
        using (var q = ControlSql(c, tx, """
            SELECT source.task_id,target.task_id,upstream.spec_json
            FROM control_packages p JOIN control_package_versions v ON v.package_id=p.package_id AND v.version=p.version
            JOIN control_intents i ON i.package_id=p.package_id AND i.version=p.version JOIN control_bindings target ON target.intent_id=i.intent_id,
            json_each(v.spec_json,'$.DependsOn') dependency
            JOIN control_packages up ON up.package_id=dependency.value
            JOIN control_package_versions upstream ON upstream.package_id=up.package_id AND upstream.version=up.version
            JOIN control_intents ui ON ui.package_id=up.package_id AND ui.version=up.version JOIN control_bindings source ON source.intent_id=ui.intent_id
            """))
        using (var r = q.ExecuteReader()) while (r.Read())
        {
            var spec = System.Text.Json.JsonSerializer.Deserialize<WorkPackageSpec>(r.GetString(2))!;
            edges.Add(new(r.GetString(0), r.GetString(1), null, spec.Artifact == "code"));
        }
        return new(nodes, edges.Distinct().ToArray());
    }
}
