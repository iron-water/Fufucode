using System;
using System.Threading;
using Fufucode.Plugins.AutoDev.Graph;

namespace Fufucode.App.Integration;

/// <summary>Current human control revokes a running host validation as well as future completion.</summary>
internal sealed class GraphValidationAuthorization : IDisposable
{
    private readonly GraphProjectService graph;
    private readonly string ticket;
    private readonly CancellationTokenSource source;
    internal CancellationToken Token => source.Token;
    internal GraphValidationAuthorization(GraphProjectService graph, string ticket, CancellationToken parent)
    {
        this.graph = graph; this.ticket = ticket;
        source = CancellationTokenSource.CreateLinkedTokenSource(parent);
        source.CancelAfter(graph.RemainingExecutionBudget());
        graph.Changed += Changed;
    }
    private void Changed()
    {
        try { graph.RequireDeliveryAuthorization(ticket); source.CancelAfter(graph.RemainingExecutionBudget()); }
        catch (UnauthorizedAccessException) { try { source.Cancel(); } catch (ObjectDisposedException) { } }
        catch (ObjectDisposedException) { }
    }
    public void Dispose() { graph.Changed -= Changed; source.Dispose(); }
}
