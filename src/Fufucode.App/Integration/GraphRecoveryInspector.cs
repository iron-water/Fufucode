using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Fufucode.Core;
using Fufucode.Core.Automation;
using Fufucode.Plugins.AutoDev.Graph;

namespace Fufucode.App.Integration;

/// <summary>Read actual retained workspaces/process receipts and reconcile managed Git journals.
/// Clean Git never proves absence of external effects; this inspector never grants replay authority.</summary>
internal static class GraphRecoveryInspector
{
    internal static async Task<string> InspectAsync(AppDataLayout layout, GraphProjectService graph,
        GraphDevelopmentHost host, string ticketId, CancellationToken ct = default)
    {
        var snapshot = graph.ReadSnapshot();
        var runs = snapshot.Runs.Where(r => r.TicketId == ticketId).ToArray();
        var workspaces = new List<object>();
        foreach (var entry in snapshot.Entries.Where(e => e.TicketId == ticketId && e.Kind == "run-result" && e.Actor == "executor"))
        {
            using var data = JsonDocument.Parse(entry.Text);
            if (!data.RootElement.TryGetProperty("candidateManifestPath", out var p) || p.ValueKind != JsonValueKind.String) continue;
            var path = Path.GetFullPath(p.GetString()!);
            var root = Path.GetFullPath(Path.Combine(layout.Root, "auto-dev", "workspaces")) + Path.DirectorySeparatorChar;
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new IOException("Recovery manifest is outside the managed sandbox.");
            try
            {
                var retained = await ManagedDevelopmentWorkspace.LoadAsync(path, ct).ConfigureAwait(false);
                workspaces.Add(new { manifest = retained.Manifest, integrity = "verified retained Git identity and candidate hash" });
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException)
            { workspaces.Add(new { manifestPath = path, integrity = "unknown", error = error.Message }); }
        }
        var processes = runs.Select(r => new { r.Id, r.Status,
            audit = ManagedExecutionJournal.Inspect(Path.Combine(layout.Root, "auto-dev", "execution-audit", r.Id)) }).ToArray();
        var integration = await host.RecoverIntegrationsAsync(ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { ticketId, snapshot.ProjectId, workspaces, processes, integration,
            cleanTreeIsNotProofOfNoEffects = true, automaticallyClearedUnknown = false });
    }
}
