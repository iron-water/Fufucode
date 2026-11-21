using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Fufucode.Core.Automation;

public sealed partial class AutomationStore
{
    private static void MigrateControlAuthority(SqliteConnection c, SqliteTransaction tx)
    {
        Execute(c, tx, "CREATE TABLE IF NOT EXISTS control_authority(id INTEGER PRIMARY KEY CHECK(id=1),identity TEXT NOT NULL)");
        using var q = ControlSql(c, tx, "INSERT OR IGNORE INTO control_authority VALUES(1,$identity)", ("$identity", Guid.NewGuid().ToString("N")));
        q.ExecuteNonQuery();
    }
    private static string? ReadControlAuthorityIdentity(SqliteConnection c, SqliteTransaction tx)
    {
        using var q = ControlSql(c, tx, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='control_authority'");
        if (Convert.ToInt64(q.ExecuteScalar()) == 0) return null;
        q.CommandText = "SELECT identity FROM control_authority WHERE id=1";
        var value = q.ExecuteScalar() as string;
        if (!Guid.TryParseExact(value, "N", out _)) throw new InvalidOperationException("Workbench has no valid stable authority identity; its owner must repair it.");
        return value;
    }

    /// <summary>Observe an exact original command's receipt. Never calls an Apply method or creates a request.</summary>
    public string? ReadControlReceipt(string kind, string payloadJson)
    {
        T Parse<T>() => JsonSerializer.Deserialize<T>(payloadJson) ?? throw new ArgumentException("Invalid original command.");
        var (command, prefix) = kind switch
        {
            "Apply" => ((object)Parse<ControlCommand>(), ""),
            "Review" => ((object)Parse<ControlReviewCommand>(), "review:"),
            "Reconsider" => ((object)Parse<ControlReconsiderCommand>(), "reconsider:"),
            "Repair" => ((object)Parse<ControlRepairCommand>(), "repair-request:"),
            "Integration" => ((object)Parse<ControlIntegrationCommand>(), "integration-authorization:"),
            "Knowledge" => ((object)Parse<ControlKnowledgeCommand>(), "knowledge:"),
            "Relation" => ((object)Parse<ControlRelationCommand>(), "relation:"),
            "Restructure" => ((object)Parse<ControlRestructureCommand>(), "restructure:"),
            "Stop" => ((object)Parse<ControlStopRunCommand>(), "run-stop:"),
            "StopResolution" => ((object)Parse<ControlStopResolutionCommand>(), "stop-resolution:"),
            _ => throw new ArgumentException("Unknown original command kind.")
        };
        var canonical = JsonSerializer.Serialize(command, command.GetType());
        if (canonical != payloadJson) throw new ArgumentException("Original command JSON must remain exact; no implicit conversion or rebasing.");
        using var json = JsonDocument.Parse(canonical);
        var id = json.RootElement.GetProperty("CommandId").GetString(); ArgumentException.ThrowIfNullOrWhiteSpace(id);
        using var c = Open(); using var q = c.CreateCommand();
        q.CommandText = "SELECT payload_hash,result_json FROM control_receipts WHERE command_id=$id"; q.Parameters.AddWithValue("$id", id);
        using var r = q.ExecuteReader(); if (!r.Read()) return null;
        if (r.GetString(0) != Sha(prefix + canonical)) throw new InvalidOperationException("Original command identity does not match the recorded receipt.");
        return r.GetString(1);
    }
}
