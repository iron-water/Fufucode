using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fufucode.Core.Backend;

namespace Fufucode.Core.Automation;

/// <summary>Host tool/process receipts. Arguments are hashed, never persisted as potential credentials.
/// An exited shell is not proof that detached children or external effects were rolled back.</summary>
public sealed class ManagedExecutionJournal
{
    private readonly object gate = new();
    private readonly string directory;
    private readonly string ticketId, runId, workspace;
    public ManagedExecutionJournal(string directory, string ticketId, string runId, string workspace)
    {
        this.directory = Path.GetFullPath(directory); this.ticketId = ticketId; this.runId = runId;
        this.workspace = Path.GetFullPath(workspace);
        Directory.CreateDirectory(this.directory);
    }
    public void Record(PureToolCall call, string state, int? processId)
    {
        lock (gate)
        {
            var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(call.Id)));
            var path = Path.Combine(directory, id + ".json");
            var prior = File.Exists(path) ? JsonSerializer.Deserialize<Receipt>(File.ReadAllText(path)) : null;
            long? start = prior?.ProcessStartedUtcTicks;
            if (processId is { } pid)
                try { using var process = System.Diagnostics.Process.GetProcessById(pid); start = process.StartTime.ToUniversalTime().Ticks; }
                catch (ArgumentException) { }
            var receipt = new Receipt(ticketId, runId, workspace, call.Id, call.Name,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(call.ArgumentsJson))), state,
                processId ?? prior?.ProcessId, start, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(receipt));
            File.Move(path + ".tmp", path, true);
        }
    }
    public static object Inspect(string directory)
    {
        if (!Directory.Exists(directory)) return new { receiptsAvailable = false, effectsKnown = false };
        var receipts = Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories)
            .Select(path => JsonSerializer.Deserialize<Receipt>(File.ReadAllText(path)) ?? throw new IOException("Invalid execution receipt"))
            .Select(receipt =>
            {
                bool? alive = receipt.ProcessId is null ? false : null;
                if (receipt.ProcessId is { } pid && receipt.ProcessStartedUtcTicks is { } started)
                {
                    try { using var process = System.Diagnostics.Process.GetProcessById(pid); alive = !process.HasExited && process.StartTime.ToUniversalTime().Ticks == started; }
                    catch (ArgumentException) { alive = false; }
                }
                return new { receipt, matchingProcessAlive = alive,
                    effectsKnown = ProductionToolDispatcher.IsPlanSafeTool(receipt.Tool) && receipt.State is "completed" or "failed" or "cancelled" };
            }).ToArray();
        return new { receiptsAvailable = true, receipts, detachedChildrenProvenAbsent = false, externalEffectsAutomaticallyReplayed = false };
    }

    public sealed record Receipt(string TicketId, string RunId, string Workspace, string CallId, string Tool,
        string ArgumentsHash, string State, int? ProcessId, long? ProcessStartedUtcTicks, long UpdatedUtc);
}
