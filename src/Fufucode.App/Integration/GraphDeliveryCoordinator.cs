using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Fufucode.App.Abstractions.Graph;
using Fufucode.Plugins.AutoDev.Graph;

namespace Fufucode.App.Integration;

/// <summary>Mechanical delivery at natural checkpoints. No model role, no inbox-driven wakeups.
/// A model proposes validation; the host independently authorizes, executes and binds it to Git.</summary>
internal sealed class GraphDeliveryCoordinator : IDisposable
{
    private readonly GraphProjectService graph;
    private readonly GraphDevelopmentHost host;
    private readonly CancellationTokenSource lifetime = new();
    private readonly HashSet<string> attempted = new(StringComparer.Ordinal);
    private readonly object gate = new();
    private CancellationTokenSource? active;
    private string? activeTicket;
    private bool pending, running, disposed;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal GraphDeliveryCoordinator(GraphProjectService graph, GraphDevelopmentHost host)
    {
        this.graph = graph; this.host = host;
        graph.Changed += Changed;
        Changed();
    }
    private void Changed()
    {
        string? ticket; CancellationTokenSource? source;
        lock (gate) { if (disposed) return; ticket = activeTicket; source = active; }
        if (ticket is not null)
        {
            try { graph.RequireDeliveryAuthorization(ticket); source?.CancelAfter(graph.RemainingExecutionBudget()); }
            catch (UnauthorizedAccessException) { try { source?.Cancel(); } catch (ObjectDisposedException) { } }
            catch (ObjectDisposedException) { }
        }
        lock (gate)
        {
            if (disposed) return;
            pending = true;
            if (running) return;
            running = true;
            _ = Task.Run(DrainAsync);
        }
    }
    private async Task DrainAsync()
    {
        try
        {
            while (true)
            {
                lock (gate) { if (!pending || disposed) return; pending = false; }
                var snapshot = graph.ReadSnapshot();
                if (graph.ReadOptions().AutoPublish)
                {
                    foreach (var version in snapshot.Entries.Where(e => e.Kind == "integration-version" && e.Actor == "host" &&
                        snapshot.Tickets.Any(t => t.Id == e.TicketId && t.Status == "Done")))
                    {
                        using var versionData = JsonDocument.Parse(version.Text);
                        var journal = versionData.RootElement.GetProperty("journalPath").GetString()!;
                        var target = await host.ReadTargetHeadAsync(lifetime.Token).ConfigureAwait(false);
                        lock (gate) { if (!attempted.Add("publish:" + journal + ":" + target)) continue; }
                        try { await host.PublishIntegratedAsync(journal, target, lifetime.Token).ConfigureAwait(false); }
                        catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException)
                        { graph.RecordHostInspection(version.TicketId!, JsonSerializer.Serialize(new { phase = "pending-publication", error = error.Message })); }
                    }
                }
                foreach (var ticket in snapshot.Tickets.Where(t => t.Status == "Review"))
                {
                    var run = snapshot.Runs.Where(r => r.TicketId == ticket.Id && r.Status == "Completed")
                        .OrderByDescending(r => r.StartedUtc).FirstOrDefault();
                    if (run is null) continue;
                    var receipt = snapshot.Entries.FirstOrDefault(e => e.Kind == "run-result" && e.Reference == run.Id && e.Actor == "executor");
                    if (receipt is null) continue;
                    using var receiptData = JsonDocument.Parse(receipt.Text);
                    if (!receiptData.RootElement.TryGetProperty("candidateManifestPath", out var path) || path.ValueKind != JsonValueKind.String) continue;
                    var proposalEntry = snapshot.Entries.Where(e => e.TicketId == ticket.Id && e.Kind == "validation-proposal" && e.Actor == "agent")
                        .OrderByDescending(e => e.CreatedUtc).FirstOrDefault(e =>
                        {
                            try { return JsonSerializer.Deserialize<GraphValidationProposal>(e.Text, Json)?.RunId == run.Id; }
                            catch (JsonException) { return false; }
                        });
                    if (proposalEntry is null) continue;
                    var proposal = JsonSerializer.Deserialize<GraphValidationProposal>(proposalEntry.Text, Json)!;
                    var repairTicketIds = snapshot.Entries.Where(e => e.Kind == "integration-repair" && e.TicketId is not null &&
                        JsonSerializer.Deserialize<GraphIntegrationRepairBinding>(e.Text, Json)?.OriginalTicketId == ticket.Id)
                        .Select(e => e.TicketId!).ToHashSet(StringComparer.Ordinal);
                    var repairRuns = snapshot.Runs.Where(r => repairTicketIds.Contains(r.TicketId) && r.Status == "Completed")
                        .OrderByDescending(r => r.StartedUtc).ToArray();
                    var authorizationVersion = JsonSerializer.Serialize(graph.ReadOptions()) + ":" + string.Join(",",
                        snapshot.Entries.Where(e => e.Kind is "Veto" or "Stop" or "veto-lifted" or "purpose" or "options").Select(e => e.Id + e.Text));
                    var key = run.Id + ":" + proposalEntry.Id + ":" + string.Join(",", repairRuns.Select(r => r.Id)) + ":" + authorizationVersion;
                    var remainingBudget = graph.RemainingExecutionBudget();
                    lock (gate)
                    {
                        if (!attempted.Add(key) || disposed) continue;
                        active = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                        active.CancelAfter(remainingBudget);
                        activeTicket = ticket.Id;
                    }
                    GraphCandidateValidation? candidate = null;
                    try
                    {
                        graph.RequireDeliveryAuthorization(ticket.Id);
                        if (!ticket.Acceptance.All(a => proposal.CoveredCriteria.Contains(a, StringComparer.Ordinal)))
                            throw new InvalidOperationException("Validation proposal does not cover the complete current acceptance contract.");
                        var token = active.Token;
                        candidate = await host.ValidateCandidateAsync(ticket.Id, path.GetString()!,
                            new(proposal.Executable, proposal.Arguments, TimeSpan.FromSeconds(proposal.TimeoutSeconds)), token).ConfigureAwait(false);
                        if (!host.VerifyCurrent(candidate)) throw new InvalidOperationException("Candidate verification did not pass: " + candidate.Receipt.Status + " " + candidate.Receipt.Error);
                        string? repairedJournal = (await host.RecoverIntegrationsAsync(token).ConfigureAwait(false))
                            .Where(r => r.TicketId == ticket.Id && r.RunId == run.Id &&
                                r.Transaction.Status is Fufucode.Core.Automation.ManagedIntegrationStatus.Prepared or Fufucode.Core.Automation.ManagedIntegrationStatus.Integrated)
                            .Select(r => r.Transaction.JournalPath).LastOrDefault();
                        foreach (var repairRun in repairRuns)
                        {
                            var binding = await host.GetIntegrationRepairBindingAsync(repairRun.TicketId, token).ConfigureAwait(false);
                            if (binding?.OriginalTicketId == ticket.Id) { repairedJournal = binding.JournalPath; break; }
                        }
                        if (repairedJournal is null) await host.IntegrateCandidateAsync(candidate, token).ConfigureAwait(false);
                        else await host.ResumeIntegrationAsync(candidate, repairedJournal, token).ConfigureAwait(false);
                        var verified = await host.ValidateIntegratedAsync(candidate,
                            new(proposal.Executable, proposal.Arguments, TimeSpan.FromSeconds(proposal.TimeoutSeconds)), token).ConfigureAwait(false);
                        if (!host.VerifyIntegratedCurrent(verified)) throw new InvalidOperationException("Integrated validation did not pass: " + verified.Receipt.Status + " " + verified.Receipt.Error);
                        await host.CompleteInternalWorkAsync(verified, proposal.CoveredCriteria, "active_goal",
                            "Internal versioned work completed; the project goal and product acceptance remain separate.", token).ConfigureAwait(false);
                        if (graph.ReadOptions().AutoPublish)
                            await host.PublishIntegratedAsync(host.GetIntegrationJournalPath(verified), token).ConfigureAwait(false);
                    }
                    catch (Exception error) when (error is not OutOfMemoryException)
                    {
                        // A diagnostic does not complete work, change its acceptance, or turn a claim into a fact.
                        graph.RecordHostInspection(ticket.Id, JsonSerializer.Serialize(new { phase = "automatic-delivery", runId = run.Id, error = error.Message }));
                        if (candidate is not null && error is not (UnauthorizedAccessException or OperationCanceledException))
                        {
                            try
                            {
                                var journal = host.GetIntegrationJournalPath(candidate);
                                await host.CreateIntegrationRepairAsync(journal, error.Message, lifetime.Token).ConfigureAwait(false);
                            }
                            catch (Exception repairError) when (repairError is InvalidOperationException or IOException or UnauthorizedAccessException)
                            { graph.RecordHostInspection(ticket.Id, JsonSerializer.Serialize(new { phase = "repair-not-started", error = repairError.Message })); }
                        }
                    }
                    finally
                    {
                        lock (gate) { activeTicket = null; active?.Dispose(); active = null; }
                    }
                }
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        { System.Diagnostics.Trace.TraceWarning("AutoDev delivery coordinator: " + error.Message); }
        finally
        {
            lock (gate)
            {
                running = false;
                if (pending && !disposed) { running = true; _ = Task.Run(DrainAsync); }
            }
        }
    }
    public void Dispose()
    {
        CancellationTokenSource? source;
        lock (gate)
        {
            if (disposed) return; disposed = true;
            source = active;
        }
        graph.Changed -= Changed;
        try { source?.Cancel(); } catch (ObjectDisposedException) { }
        lifetime.Cancel();
    }
}
