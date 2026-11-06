using Microsoft.Data.Sqlite;
using static Fufucode.Plugins.AutoDev.Graph.GraphStorage;
namespace Fufucode.Plugins.AutoDev.Graph;

internal static class GraphOrganizationStorage
{
    internal static void Ensure(SqliteConnection c, SqliteTransaction tx) => Execute(c, tx, """
        CREATE TABLE IF NOT EXISTS graph_organization(project_id TEXT PRIMARY KEY REFERENCES rb_projects(project_id),version INTEGER NOT NULL,state_json TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS graph_organization_commands(project_id TEXT NOT NULL,command_id TEXT NOT NULL,payload_hash TEXT NOT NULL,result_json TEXT NOT NULL,PRIMARY KEY(project_id,command_id));
        """);
}
