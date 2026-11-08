using System.Text.Json;
using Fufucode.App.Abstractions.Graph;
using static Fufucode.Plugins.AutoDev.Graph.GraphStorage;

namespace Fufucode.Plugins.AutoDev.Graph;

/// <summary>Deterministic excerpts of stored facts. This never generates a semantic summary or pretends to be a full snapshot.</summary>
internal static class GraphFocusedContext
{
    private const int ModuleLimit = 24, RelationLimit = 24, TicketLimit = 6;
    private const int RecentLimit = 12, DecisionLimit = 8, SourceLimit = 6, RunLimit = 8;

    internal static string Build(GraphSnapshot snapshot, string? moduleId, string? ticketId)
    {
        var byId = snapshot.Modules.ToDictionary(m => m.Id, StringComparer.Ordinal);
        if (moduleId is not null && !byId.ContainsKey(moduleId)) throw new KeyNotFoundException("Unknown module in selected revision.");
        GraphTicket? ticket = null;
        if (ticketId is not null) ticket = snapshot.Tickets.SingleOrDefault(t => t.Id == ticketId)
            ?? throw new KeyNotFoundException("Unknown ticket in selected revision.");
        var anchors = new List<string>();
        if (moduleId is not null) anchors.Add(moduleId);
        if (ticket is not null) anchors.AddRange(ticket.ModuleIds.Where(byId.ContainsKey));
        anchors = anchors.Distinct(StringComparer.Ordinal).ToList();
        var planning = anchors.Count == 0;
        var selected = new List<GraphModule>();
        var selectedIds = new HashSet<string>(StringComparer.Ordinal);
        void Include(string id)
        {
            if (selected.Count < ModuleLimit && byId.TryGetValue(id, out var module) && selectedIds.Add(id)) selected.Add(module);
        }
        foreach (var id in anchors) Include(id);
        foreach (var id in anchors.Take(ModuleLimit))
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var parent = byId[id].ParentId; parent is not null && seen.Add(parent) && byId.TryGetValue(parent, out var item); parent = item.ParentId)
                Include(parent);
        }
        if (planning)
        {
            foreach (var root in snapshot.Modules.Where(m => m.ParentId is null).OrderBy(m => m.Id, StringComparer.Ordinal)) Include(root.Id);
            // These are deterministic candidates, not an inferred architecture ranking.
            foreach (var candidate in snapshot.Modules.Where(m => m.ParentId is not null)
                .OrderBy(m => byId.TryGetValue(m.ParentId!, out var parent) && parent.ParentId is null ? 0 : 1)
                .ThenBy(m => m.SourcePaths.Any(p => p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || p.EndsWith("pubspec.yaml", StringComparison.OrdinalIgnoreCase)) ? 0 : 1)
                .ThenBy(m => m.Id, StringComparer.Ordinal)) Include(candidate.Id);
        }
        var anchorSet = anchors.Take(ModuleLimit).ToHashSet(StringComparer.Ordinal);
        foreach (var child in snapshot.Modules.Where(m => m.ParentId is not null && anchorSet.Contains(m.ParentId)).OrderBy(m => m.Id, StringComparer.Ordinal)) Include(child.Id);
        var related = snapshot.Relations.Where(r => anchorSet.Contains(r.SourceId) || anchorSet.Contains(r.TargetId))
            .OrderBy(r => r.Id, StringComparer.Ordinal).ToArray();
        foreach (var edge in related)
        {
            Include(edge.SourceId); Include(edge.TargetId);
        }
        var relations = snapshot.Relations.Where(r => selectedIds.Contains(r.SourceId) && selectedIds.Contains(r.TargetId))
            .OrderBy(r => anchorSet.Contains(r.SourceId) || anchorSet.Contains(r.TargetId) ? 0 : 1)
            .ThenBy(r => r.Id, StringComparer.Ordinal).Take(RelationLimit).ToArray();
        var relationIds = relations.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        var tickets = ticket is not null ? [ticket] : snapshot.Tickets
            .Where(t => moduleId is null || t.ModuleIds.Any(selectedIds.Contains))
            .OrderBy(t => t.Status == "NeedsReconciliation" ? 0 : t.Status is "Active" or "Authorized" ? 1 : 2)
            .ThenByDescending(t => t.UpdatedUtc).ThenBy(t => t.Id, StringComparer.Ordinal).Take(TicketLimit).ToArray();
        var ticketIds = tickets.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        bool Relevant(GraphEntry entry) => entry.TicketId is not null && ticketIds.Contains(entry.TicketId)
            || entry.TicketId is null && entry.ModuleId is not null && selectedIds.Contains(entry.ModuleId);
        bool IsDecision(GraphEntry entry) => entry.Kind is "Veto" or "Constraint" or "Gate" or "Stop" or "Takeover" or "Acceptance" or "Replan" or "Plan";
        bool ActiveDecision(GraphEntry entry)
        {
            if (entry.Kind is not ("Veto" or "Constraint" or "Gate" or "Stop")) return false;
            try
            {
                using var data = JsonDocument.Parse(entry.Text);
                return !data.RootElement.TryGetProperty("status", out var status) || status.GetString() is "Active" or "Requested" or "Defer";
            }
            catch (JsonException) { return true; }
        }
        var decisions = snapshot.Entries.Where(e => IsDecision(e) && (e.TicketId is null || ticketIds.Contains(e.TicketId)))
            .OrderByDescending(ActiveDecision).ThenByDescending(e => e.CreatedUtc).ThenBy(e => e.Id, StringComparer.Ordinal).ToArray();
        var includedDecisions = decisions.Take(DecisionLimit).ToArray();
        var recent = snapshot.Entries.Where(e => e.Kind != "source" && !IsDecision(e) && Relevant(e))
            .OrderByDescending(e => e.CreatedUtc).ThenBy(e => e.Id, StringComparer.Ordinal).ToArray();
        var includedRecent = recent.Take(RecentLimit).ToArray();
        bool SourceRelevant(GraphEntry entry)
        {
            if (entry.ModuleId is not null && selectedIds.Contains(entry.ModuleId)) return true;
            try
            {
                using var data = JsonDocument.Parse(entry.Text);
                return data.RootElement.TryGetProperty("entityId", out var entity)
                    && entity.GetString() is { } id && (selectedIds.Contains(id) || relationIds.Contains(id));
            }
            catch (JsonException) { return false; }
        }
        var sources = snapshot.Entries.Where(e => e.Kind == "source").ToArray();
        var relatedSources = sources.Where(SourceRelevant).OrderBy(e => e.ModuleId is null ? 0 : 1).ThenBy(e => e.Id, StringComparer.Ordinal).ToArray();
        var includedSources = relatedSources.Take(SourceLimit).ToArray();
        var relatedRuns = snapshot.Runs.Where(r => ticketIds.Contains(r.TicketId) || ticketId is null && r.Status == "Unknown")
            .OrderBy(r => r.Status == "Unknown" ? 0 : r.Status is "Running" or "StopRequested" ? 1 : 2)
            .ThenByDescending(r => r.StartedUtc).ThenBy(r => r.Id, StringComparer.Ordinal).ToArray();
        var runs = relatedRuns.Take(RunLimit).ToArray();
        var entries = includedDecisions.Concat(includedRecent).Concat(includedSources).ToArray();
        var previews = new LiteralBudget();
        object Module(GraphModule m) => new
        {
            id = m.Id, parentId = m.ParentId, name = previews.Text(m.Name, 160),
            summary = previews.Text(m.Summary, 320), sourcePaths = m.SourcePaths.Take(4).Select(p => previews.Text(p, 256)).ToArray(),
            omittedSourcePaths = Math.Max(0, m.SourcePaths.Length - 4), m.UpdatedUtc
        };
        object Ticket(GraphTicket t) => new
        {
            id = t.Id, title = previews.Text(t.Title, 256), goal = previews.Text(t.Goal, 1536), status = t.Status,
            moduleIds = t.ModuleIds.Take(ModuleLimit).ToArray(), omittedModuleIds = Math.Max(0, t.ModuleIds.Length - ModuleLimit),
            acceptance = t.Acceptance.Take(12).Select(a => previews.Text(a, 320)).ToArray(), omittedAcceptance = Math.Max(0, t.Acceptance.Length - 12),
            sessionId = previews.Text(t.SessionId, 192), t.CreatedUtc, t.UpdatedUtc
        };
        previews.BeginSection(2304);
        var title = previews.Text(snapshot.Title, 192);
        var purpose = previews.Text(snapshot.Purpose, 2048);
        previews.BeginSection(4096);
        var projectedTickets = tickets.Select(Ticket).ToArray();
        previews.BeginSection(6000);
        var projectedModules = selected.Select(Module).ToArray();
        previews.BeginSection(1800);
        var projectedRelations = relations.Select(r => new
        {
            id = r.Id, sourceId = r.SourceId, targetId = r.TargetId,
            kind = r.Kind, label = previews.Text(r.Label, 256)
        }).ToArray();
        object Entry(GraphEntry e) => new
        {
            id = e.Id, ticketId = e.TicketId, moduleId = e.ModuleId,
            kind = e.Kind, actor = e.Actor, text = previews.Text(e.Text, 1024),
            reference = previews.Text(e.Reference, 320), e.CreatedUtc
        };
        previews.BeginSection(2500);
        var projectedDecisions = includedDecisions.Select(Entry).ToArray();
        previews.BeginSection(2000);
        var projectedRecent = includedRecent.Select(Entry).ToArray();
        previews.BeginSection(1500);
        var projectedSources = includedSources.Select(Entry).ToArray();
        var projectedEntries = projectedDecisions.Concat(projectedRecent).Concat(projectedSources).ToArray();
        previews.BeginSection(2000);
        var projectedRuns = runs.Select(r => new
        {
            id = r.Id, ticketId = r.TicketId, status = r.Status,
            sessionId = previews.Text(r.SessionId, 192), detail = previews.Text(r.Detail, 512), r.StartedUtc, r.EndedUtc
        }).ToArray();
        return Serialize(new
        {
            snapshot.ProjectId, snapshot.ProjectPath, snapshot.Revision, snapshot.IsHistorical,
            title, purpose, snapshot.ProjectStatus,
            // Hard controls are never subject to the ordinary preview count/text budget.
            activeControls = decisions.Where(ActiveDecision).ToArray(),
            currentAcceptance = ticket?.Acceptance,
            gitVersions = snapshot.Entries.Where(e => e.Kind == "integration-version" &&
                (ticketId is null || e.TicketId == ticketId)).ToArray(),
            scope = new
            {
                mode = ticketId is not null ? "focused-ticket" : moduleId is not null ? "focused-module" : "planning-candidates",
                requestedModuleId = moduleId, requestedTicketId = ticketId,
                selection = planning ? "Roots and a bounded deterministic set of shallow/manifest candidates; this is not the complete architecture."
                    : "Bound modules, ancestors, immediate children and literal relation neighbours, each subject to explicit limits.",
                completeSnapshot = false,
                literalFactsOnly = true,
                truncationPolicy = "Text previews are verbatim prefixes, never model-generated summaries. Counts below report all excluded records and text."
            },
            modules = projectedModules, relations = projectedRelations, tickets = projectedTickets, entries = projectedEntries, runs = projectedRuns,
            totals = new { modules = snapshot.Modules.Count, relations = snapshot.Relations.Count, tickets = snapshot.Tickets.Count,
                entries = snapshot.Entries.Count, sources = sources.Length, runs = snapshot.Runs.Count },
            omitted = new
            {
                modules = snapshot.Modules.Count - selected.Count, relations = snapshot.Relations.Count - relations.Length,
                tickets = snapshot.Tickets.Count - tickets.Length, entries = snapshot.Entries.Count - entries.Length,
                sources = sources.Length - includedSources.Length, relatedSources = relatedSources.Length - includedSources.Length,
                relatedRecentEntries = recent.Length - includedRecent.Length, decisions = decisions.Length - includedDecisions.Length,
                activeDecisions = 0, runs = snapshot.Runs.Count - runs.Length,
                relatedRuns = relatedRuns.Length - runs.Length, unknownRuns = relatedRuns.Skip(RunLimit).Count(r => r.Status == "Unknown"),
                textFields = previews.OmittedFields, textCharacters = previews.OmittedCharacters
            },
            requiresFullReadBeforeActingOnClippedText = previews.OmittedFields > 0 || decisions.Skip(DecisionLimit).Any(ActiveDecision),
            fullRead = new { tool = "graph_read", arguments = snapshot.IsHistorical ? new Dictionary<string, object> { ["full"] = true, ["revision"] = snapshot.Revision } : new Dictionary<string, object> { ["full"] = true },
                description = "Explicitly request the complete stored snapshot, including all original sources and unabridged records." },
            focusedRead = new { tool = "graph_context", description = "Pass moduleId or ticketId from graph_read to inspect another bounded scope; include revision for a fixed historical version." },
            limits = new { modules = ModuleLimit, relations = RelationLimit, candidateTickets = TicketLimit, recentEntries = RecentLimit,
                decisions = DecisionLimit, sources = SourceLimit, runs = RunLimit, encodedLiteralCharacters = LiteralBudget.Maximum }
        });
    }

    private sealed class LiteralBudget
    {
        internal const int Maximum = 22000;
        private int _remaining = Maximum;
        private int _sectionRemaining = Maximum;
        internal void BeginSection(int allowance) => _sectionRemaining = Math.Min(allowance, _remaining);
        internal int OmittedFields { get; private set; }
        internal long OmittedCharacters { get; private set; }
        internal string? Text(string? value, int limit)
        {
            if (value is null) return null;
            var available = Math.Min(_remaining, _sectionRemaining);
            var count = Math.Min(value.Length, Math.Min(limit, available));
            while (count > 0 && Serialize(value[..count]).Length > available) count /= 2;
            if (count > 0 && char.IsHighSurrogate(value[count - 1])) count--;
            var result = value[..count];
            var encoded = Serialize(result).Length;
            _remaining = Math.Max(0, _remaining - encoded);
            _sectionRemaining = Math.Max(0, _sectionRemaining - encoded);
            if (count != value.Length) { OmittedFields++; OmittedCharacters += value.Length - count; }
            return result;
        }
    }
}
