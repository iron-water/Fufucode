using Fufucode.App.Abstractions.Graph;
using Microsoft.Data.Sqlite;
using static Fufucode.Plugins.AutoDev.Graph.GraphStorage;
namespace Fufucode.Plugins.AutoDev.Graph;

internal static class GraphOrganizationStorage
{
    // The JSON envelope stays additive: old topic/status records remain readable claims.
    // Legacy review flags came from message delivery, not Kernel authority, and cannot gate completion.
    internal static GraphOrganizationSnapshot ReadSnapshot(string json)
    {
        var state = Deserialize<GraphOrganizationSnapshot>(json);
        return state with
        {
            Controls = state.Controls ?? [],
            Barriers = state.Barriers.Select(b => b with { RequiresReview = false }).ToArray()
        };
    }

    internal static string Metadata(GraphOrganizationSnapshot state) => "v2:" + Serialize(state with { Messages = [] });

    internal static GraphOrganizationSnapshot ReadStored(SqliteConnection c, SqliteTransaction tx, string json) =>
        json.StartsWith("v2:", StringComparison.Ordinal) ? WithMail(c, tx, ReadSnapshot(json[3..])) : ReadSnapshot(json);

    internal static void Ensure(SqliteConnection c, SqliteTransaction tx) => Execute(c, tx, """
        CREATE TABLE IF NOT EXISTS graph_organization(project_id TEXT PRIMARY KEY REFERENCES rb_projects(project_id),version INTEGER NOT NULL,state_json TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS graph_organization_commands(project_id TEXT NOT NULL,command_id TEXT NOT NULL,payload_hash TEXT NOT NULL,result_json TEXT NOT NULL,PRIMARY KEY(project_id,command_id));
        CREATE TABLE IF NOT EXISTS graph_mail_versions(project_id TEXT NOT NULL,message_id TEXT NOT NULL,version INTEGER NOT NULL,body TEXT NOT NULL,PRIMARY KEY(project_id,message_id,version));
        CREATE TABLE IF NOT EXISTS graph_org_revision(project_id TEXT NOT NULL,revision INTEGER NOT NULL,org_version INTEGER NOT NULL,PRIMARY KEY(project_id,revision));
        """);

    internal static void StoreMail(SqliteConnection c, SqliteTransaction tx, GraphOrganizationSnapshot state, IEnumerable<GraphOrganizationMessage> changed)
    {
        foreach (var m in changed) Execute(c, tx, "INSERT INTO graph_mail_versions VALUES($p,$id,$v,$body)",
            ("$p", state.ProjectId), ("$id", m.Id), ("$v", state.Version), ("$body", Serialize(m)));
    }

    internal static GraphOrganizationSnapshot WithMail(SqliteConnection c, SqliteTransaction tx, GraphOrganizationSnapshot state) => state with
    {
        Messages = Query(c, tx, """
            SELECT body FROM (SELECT body, ROW_NUMBER() OVER(PARTITION BY message_id ORDER BY version DESC) AS n
              FROM graph_mail_versions WHERE project_id=$p AND version<=$v) WHERE n=1
            """, r => Deserialize<GraphOrganizationMessage>(r.GetString(0)), ("$p", state.ProjectId), ("$v", state.Version)).OrderBy(m => m.Sequence).ToArray()
    };
}
