using Fufucode.App.Abstractions.Graph;
using static Fufucode.Plugins.AutoDev.Graph.GraphStorage;

namespace Fufucode.Plugins.AutoDev.Graph;

/// <summary>Bounded, explicit pages of real graph records. UI snapshots remain complete.</summary>
internal static class GraphReadIndex
{
    internal static string Build(GraphSnapshot snapshot, int moduleOffset, int moduleLimit,
        bool includeEntries, int entryOffset, int entryLimit)
    {
        var textOmitted = 0L;
        var remaining = 14000;
        string Preview(string value, int maximum)
        {
            var take = Math.Min(value.Length, Math.Min(maximum, remaining));
            while (take > 0 && Serialize(value[..take]).Length > remaining) take /= 2;
            if (take > 0 && char.IsHighSurrogate(value[take - 1])) take--;
            var literal = value[..take];
            remaining = Math.Max(0, remaining - Serialize(literal).Length);
            textOmitted += value.Length - take;
            return literal;
        }
        var modules = snapshot.Modules.OrderBy(m => m.ParentId is null ? 0 : 1).ThenBy(m => m.Id, StringComparer.Ordinal)
            .Skip(moduleOffset).Take(moduleLimit).ToArray();
        var moduleIds = modules.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        var related = snapshot.Relations.Where(r => moduleIds.Contains(r.SourceId) || moduleIds.Contains(r.TargetId)).ToArray();
        var relations = related.Take(32).ToArray();
        var tickets = snapshot.Tickets.OrderBy(t => t.Status == "NeedsReconciliation" ? 0 : t.Status == "Active" ? 1 : 2)
            .ThenByDescending(t => t.UpdatedUtc).ThenBy(t => t.Id, StringComparer.Ordinal).Take(8).ToArray();
        var entries = includeEntries ? snapshot.Entries.Skip(entryOffset).Take(entryLimit).ToArray() : [];
        var runs = snapshot.Runs.OrderBy(r => r.Status == "Unknown" ? 0 : r.Status is "Running" or "StopRequested" ? 1 : 2)
            .ThenByDescending(r => r.StartedUtc).ThenBy(r => r.Id, StringComparer.Ordinal).Take(8).ToArray();
        var title = Preview(snapshot.Title, 192); var purpose = Preview(snapshot.Purpose, 1024);
        var projectedTickets = tickets.Select(t => new { t.Id, title = Preview(t.Title, 192), t.Status, t.SessionId,
            moduleIds = t.ModuleIds.Take(12).ToArray(), omittedModuleIds = Math.Max(0, t.ModuleIds.Length - 12) }).ToArray();
        var projectedModules = modules.Select(m => new { m.Id, m.ParentId, name = Preview(m.Name, 128), summary = Preview(m.Summary, 160),
            sourcePaths = m.SourcePaths.Take(3).Select(p => Preview(p, 192)).ToArray(), omittedSourcePaths = Math.Max(0, m.SourcePaths.Length - 3), m.UpdatedUtc }).ToArray();
        var projectedRelations = relations.Select(r => new { r.Id, r.SourceId, r.TargetId, r.Kind, label = Preview(r.Label, 160) }).ToArray();
        var projectedEntries = entries.Select(e => new { e.Id, e.TicketId, e.ModuleId, e.Kind, e.Actor, text = Preview(e.Text, 512),
            reference = e.Reference is null ? null : Preview(e.Reference, 192), e.CreatedUtc }).ToArray();
        return Serialize(new
        {
            snapshot.ProjectId, snapshot.ProjectPath, title, purpose, snapshot.ProjectStatus, snapshot.Revision, snapshot.IsHistorical,
            scope = "bounded-project-index", completeSnapshot = false,
            modules = projectedModules, relations = projectedRelations, tickets = projectedTickets, entries = projectedEntries,
            runs = runs.Select(r => new { r.Id, r.TicketId, r.Status, r.SessionId, r.StartedUtc, r.EndedUtc }).ToArray(),
            totals = new { modules = snapshot.Modules.Count, relations = snapshot.Relations.Count, tickets = snapshot.Tickets.Count,
                entries = snapshot.Entries.Count, runs = snapshot.Runs.Count, revisions = snapshot.Revisions.Count },
            pagination = new
            {
                modules = new { offset = moduleOffset, limit = moduleLimit, returned = modules.Length, total = snapshot.Modules.Count,
                    hasMore = (long)moduleOffset + modules.Length < snapshot.Modules.Count },
                entries = new { included = includeEntries, offset = entryOffset, limit = entryLimit, returned = entries.Length,
                    total = snapshot.Entries.Count, hasMore = !includeEntries && snapshot.Entries.Count > 0 || (long)entryOffset + entries.Length < snapshot.Entries.Count }
            },
            omitted = new { modules = snapshot.Modules.Count - modules.Length, relations = snapshot.Relations.Count - relations.Length,
                relatedRelations = related.Length - relations.Length, tickets = snapshot.Tickets.Count - tickets.Length,
                entries = snapshot.Entries.Count - entries.Length, runs = snapshot.Runs.Count - runs.Length, textCharacters = textOmitted },
            fullRead = new { tool = "graph_read", arguments = snapshot.IsHistorical ? new Dictionary<string, object> { ["full"] = true, ["revision"] = snapshot.Revision }
                : new Dictionary<string, object> { ["full"] = true }, description = "Explicit full:true returns the unabridged stored snapshot. This index contains only literal previews." },
            focusedRead = new { tool = "graph_context", description = "Pass ticketId/moduleId for bounded relevant records and source facts. Pin revision for stable pagination." }
        });
    }
}
