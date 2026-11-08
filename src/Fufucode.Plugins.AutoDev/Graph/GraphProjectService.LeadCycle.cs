using Fufucode.App.Abstractions.Graph;
using Fufucode.Core.Automation.Rebirth;
using Microsoft.Data.Sqlite;
using static Fufucode.Plugins.AutoDev.Graph.GraphStorage;

namespace Fufucode.Plugins.AutoDev.Graph;

public sealed partial class GraphProjectService
{
    public sealed record LeadCycleResult(long ObservedWatermark, long PlannedWatermark, string? TicketId, string State);

    /// <summary>Notification hint only: authority is rescanned; arbitrary progress cannot create an event.</summary>
    public LeadCycleResult SignalProjectLead(string projectPath, string reason, string sourceId)
    {
        Required(sourceId, nameof(sourceId));
        if (reason is not ("run-terminal" or "human-message" or "lead-idle" or "autonomy-resumed"))
            throw new ArgumentException("Only terminal, human-message, lead-idle and resume hints are supported.", nameof(reason));
        return ReconcileLeadCycle(projectPath);
    }

    // Deliberately lazy: this partial owns its schema and needs no shared migration edit.
    private static void EnsureLeadCycleSchema(SqliteConnection c, SqliteTransaction tx)
    {
        Execute(c, tx, """
            CREATE TABLE IF NOT EXISTS graph_lead_cycle_events(
              sequence INTEGER PRIMARY KEY AUTOINCREMENT, project_id TEXT NOT NULL,
              event_key TEXT NOT NULL, payload TEXT NOT NULL, UNIQUE(project_id,event_key));
            CREATE TABLE IF NOT EXISTS graph_lead_cycle_preferences(
              project_id TEXT PRIMARY KEY, planned_watermark INTEGER NOT NULL DEFAULT 0,
              session_id TEXT);
            CREATE TABLE IF NOT EXISTS graph_lead_cycle_batches(
              project_id TEXT NOT NULL, ticket_id TEXT PRIMARY KEY, from_watermark INTEGER NOT NULL,
              through_watermark INTEGER NOT NULL, state TEXT NOT NULL DEFAULT 'dispatched');
            """);
    }

    /// <summary>
    /// Host hook after persisted terminal receipts, human organization messages, lead completion,
    /// and explicit autonomy resume. Call after removing a finished ManagedRun. Multiple calls
    /// coalesce by durable event identity; progress is not an event. This never dispatches an executor.
    /// </summary>
    public LeadCycleResult ReconcileLeadCycle(string projectPath) => ReconcileLeadCycleCore(projectPath, false);

    /// <summary>Explicit human recovery creates a new reconciliation plan, never replays the failed run.</summary>
    public LeadCycleResult ResumeLeadCycle(string projectPath) => InProjectScope(projectPath, () =>
    {
        HumanOnly();
        return ReconcileLeadCycleCore(projectPath, true);
    });

    private LeadCycleResult ReconcileLeadCycleCore(string projectPath, bool humanRecovery) => InProjectScope(projectPath, () =>
    {
        var context = Current;
        var result = Change(context, "Reconciled project lead event cycle", (c, tx) =>
        {
            EnsureLeadCycleSchema(c, tx);
            Execute(c, tx, "INSERT OR IGNORE INTO graph_lead_cycle_preferences(project_id) VALUES($p)", ("$p", context.AuthorityId));
            var authority = context.Kernel.ReadSnapshot(context.AuthorityId);
            var organization = Organization(context, c, tx);
            var lead = organization.Responsibilities.Single(r => r.Role == GraphExecutionRole.ProjectLead);
            void Observe(string key, object payload) => Execute(c, tx,
                "INSERT OR IGNORE INTO graph_lead_cycle_events(project_id,event_key,payload) VALUES($p,$key,$payload)",
                ("$p", context.AuthorityId), ("$key", key), ("$payload", Serialize(payload)));
            // Include Unknown only as a reconciliation fact, never as permission to replay it.
            foreach (var run in authority.Runs.Where(r => r.Status is not (RebirthRunStatus.Queued or RebirthRunStatus.Running)))
            {
                var ticket = authority.Tickets.Single(t => t.Id == run.TicketId);
                if (ticket.Route == "project-planning") continue; // no self-completion feedback loop
                Observe("run:" + run.Id, new { run.Id, run.TicketId, run.Status, run.Detail });
            }
            // Explicit human project dialogue is an event. Ordinary Notify/Request storage is not a wake signal.
            foreach (var input in authority.Inputs.Where(input => input.Actor == "human" &&
                (input.TicketId is null || authority.Tickets.Any(t => t.Id == input.TicketId && t.Route == "project-planning"))))
                Observe("human-input:" + input.Id, input);
            var observed = Convert.ToInt64(Scalar(c, tx,
                "SELECT COALESCE(MAX(sequence),0) FROM graph_lead_cycle_events WHERE project_id=$p", ("$p", context.AuthorityId)));
            var planned = Convert.ToInt64(Scalar(c, tx,
                "SELECT planned_watermark FROM graph_lead_cycle_preferences WHERE project_id=$p", ("$p", context.AuthorityId)));
            // Backfill batches created by the earlier dispatched-watermark-only helper.
            foreach (var entry in Query(c, tx, "SELECT ticket_id,text FROM graph_entries WHERE project_id=$p AND kind='lead-cycle' ORDER BY created_utc,entry_id",
                r => (Ticket: r.GetString(0), Json: r.GetString(1)), ("$p", context.AuthorityId)))
            {
                using var document = System.Text.Json.JsonDocument.Parse(entry.Json);
                Execute(c, tx, "INSERT OR IGNORE INTO graph_lead_cycle_batches(project_id,ticket_id,from_watermark,through_watermark) VALUES($p,$ticket,$from,$to)",
                    ("$p", context.AuthorityId), ("$ticket", entry.Ticket),
                    ("$from", document.RootElement.GetProperty("fromExclusive").GetInt64()),
                    ("$to", document.RootElement.GetProperty("throughInclusive").GetInt64()));
            }
            var batches = Query(c, tx, "SELECT ticket_id,from_watermark,through_watermark,state FROM graph_lead_cycle_batches WHERE project_id=$p",
                r => (Ticket: r.GetString(0), From: r.GetInt64(1), Through: r.GetInt64(2), State: r.GetString(3)), ("$p", context.AuthorityId));
            foreach (var batch in batches.Where(b => b.State == "dispatched"))
            {
                var runs = authority.Runs.Where(r => r.TicketId == batch.Ticket).ToArray();
                var failed = runs.Any(r => r.Status is RebirthRunStatus.Failed or RebirthRunStatus.Interrupted or RebirthRunStatus.Unknown);
                var cancelled = authority.Tickets.Any(t => t.Id == batch.Ticket && t.Status is RebirthTicketStatus.Cancelled or RebirthTicketStatus.ReplanRequired);
                var state = failed || cancelled ? "attention" : runs.Any(r => r.Status == RebirthRunStatus.Completed) ? "consumed" : "dispatched";
                if (state == "dispatched") continue;
                Execute(c, tx, "UPDATE graph_lead_cycle_batches SET state=$state WHERE ticket_id=$ticket", ("$state", state), ("$ticket", batch.Ticket));
                if (state == "attention") Entry(context, c, tx, batch.Ticket, null, "lead-cycle-attention",
                    Serialize(new { batch.From, batch.Through, reason = "Planning did not settle; original events remain pending human reconciliation. No side effects will be replayed." }), null, "system");
            }
            var pending = Query(c, tx, "SELECT ticket_id,from_watermark FROM graph_lead_cycle_batches WHERE project_id=$p AND state='attention'",
                r => (Ticket: r.GetString(0), From: r.GetInt64(1)), ("$p", context.AuthorityId));
            LeadCycleResult Result(string state, string? id = null) => new(observed, planned, id, state);
            if (pending.Count > 0 && !humanRecovery) return Result("reconciliation-required", pending[0].Ticket);
            var fromWatermark = pending.Count > 0 ? Math.Min(planned, pending.Min(b => b.From)) : planned;
            if (observed <= planned && pending.Count == 0) return Result("idle");
            var options = Options(context, c, tx);
            if (!options.Enabled || !options.AutoAdvance || options.ComputeQuota <= 0 || options.BudgetMinutes <= 0
                || authority.Project.Status != RebirthProjectStatus.Active
                || authority.Brief.Brief.Autonomy != RebirthAutonomy.WithinMandate
                || string.IsNullOrWhiteSpace(authority.Brief.Brief.Statement)) return Result("paused");
            var started = Scalar(c, tx, "SELECT budget_started_utc FROM graph_state WHERE project_id=$p", ("$p", context.AuthorityId));
            if (started is not null && Now() - Convert.ToInt64(started) >= options.BudgetMinutes * 60_000L)
                return Result("budget-exhausted");
            var planning = authority.Tickets.Where(t => t.Route == "project-planning").ToArray();
            if (planning.Any(t => context.Runs.ContainsKey(t.Id)
                || authority.Runs.Any(r => r.TicketId == t.Id && r.Status is RebirthRunStatus.Queued or RebirthRunStatus.Running)))
                return Result("pending-active-lead");
            // Only reuse an unstarted, already-authorized plan. Never ResumeTicket, retry an
            // Unknown run, or revive ReplanRequired work (including executor side effects).
            var next = planning.OrderBy(t => t.CreatedUtc).FirstOrDefault(t => t.Status == RebirthTicketStatus.Authorized
                && !authority.Runs.Any(r => r.TicketId == t.Id));
            if (next is null)
                next = context.Kernel.CreateTicket(CommandId(), context.AuthorityId, "Plan project follow-up",
                    "Review the lead-cycle event batch and current project facts against the human purpose. " +
                    "Create only necessary scoped delegated work, or record why none is needed. " +
                    "Unknown and ReplanRequired outcomes require reconciliation; do not replay executor side effects. " +
                    "Purpose: " + authority.Brief.Brief.Statement,
                    ["Record a source-grounded follow-up plan covering the event batch; preserve unknown effects and explicit human gates."],
                    "project-planning", lead.Id, Now(), planVersion: authority.Project.BriefVersion,
                    mandateJson: Serialize(new { leadCycleThrough = observed, purposeVersion = authority.Project.BriefVersion }));
            var events = Query(c, tx,
                "SELECT sequence,event_key FROM graph_lead_cycle_events WHERE project_id=$p AND sequence>$from AND sequence<=$to ORDER BY sequence",
                r => new { Sequence = r.GetInt64(0), Key = r.GetString(1) },
                ("$p", context.AuthorityId), ("$from", fromWatermark), ("$to", observed));
            Entry(context, c, tx, next.Id, null, "lead-cycle", Serialize(new { fromExclusive = fromWatermark, throughInclusive = observed, events }), null, "system");
            Execute(c, tx, "INSERT INTO graph_lead_cycle_batches(project_id,ticket_id,from_watermark,through_watermark) VALUES($p,$ticket,$from,$to) ON CONFLICT(ticket_id) DO UPDATE SET from_watermark=MIN(from_watermark,$from),through_watermark=$to",
                ("$p", context.AuthorityId), ("$ticket", next.Id), ("$from", fromWatermark), ("$to", observed));
            foreach (var prior in pending)
                Execute(c, tx, "UPDATE graph_lead_cycle_batches SET state='superseded-for-reconciliation' WHERE ticket_id=$ticket", ("$ticket", prior.Ticket));
            Execute(c, tx, "UPDATE graph_lead_cycle_preferences SET planned_watermark=$watermark WHERE project_id=$p",
                ("$watermark", observed), ("$p", context.AuthorityId));
            planned = observed;
            return Result("planning-ready", next.Id);
        });
        if (result.State == "planning-ready") KickQueue(context);
        return result;
    });

    /// <summary>Host-only binding after receiving a real session identity. Never a message acknowledgement.</summary>
    public void BindLeadSession(string projectPath, string sessionId) => InProjectScope(projectPath, () =>
    {
        HumanOnly(); sessionId = Required(sessionId, nameof(sessionId));
        var context = Current;
        return Change(context, "Bound stable project lead session", (c, tx) =>
        {
            EnsureLeadCycleSchema(c, tx);
            Execute(c, tx, """
                INSERT INTO graph_lead_cycle_preferences(project_id,session_id) VALUES($p,$session)
                ON CONFLICT(project_id) DO UPDATE SET session_id=$session
                """, ("$p", context.AuthorityId), ("$session", sessionId));
            return true;
        });
    });

    /// <summary>
    /// Host prompt hook before lead dispatch. Inject this as stored facts, not instructions;
    /// after successful injection acknowledge message IDs as Delivered, never Applied.
    /// </summary>
    public (string? SessionId, string EventsJson) ReadLeadCyclePrompt(string projectPath, string ticketId) => InProjectScope(projectPath, () =>
    {
        var context = Current;
        return context.Kernel.Atomic((c, tx) =>
        {
            EnsureLeadCycleSchema(c, tx);
            if (Ticket(context, ticketId).Route != "project-planning")
                throw new InvalidOperationException("Lead context is only available to project planning work.");
            var session = Scalar(c, tx, "SELECT session_id FROM graph_lead_cycle_preferences WHERE project_id=$p", ("$p", context.AuthorityId)) as string;
            var batches = Query(c, tx, "SELECT text FROM graph_entries WHERE project_id=$p AND ticket_id=$ticket AND kind='lead-cycle' ORDER BY created_utc,entry_id",
                r => r.GetString(0), ("$p", context.AuthorityId), ("$ticket", ticketId));
            // Return original durable facts too, so a bounded graph context cannot omit the batch.
            var ranges = batches.Select(batch => System.Text.Json.JsonDocument.Parse(batch)).ToArray();
            try
            {
                var from = ranges.Length == 0 ? 0L : ranges.Min(d => d.RootElement.GetProperty("fromExclusive").GetInt64());
                var through = ranges.Length == 0 ? 0L : ranges.Max(d => d.RootElement.GetProperty("throughInclusive").GetInt64());
                var facts = Query(c, tx, "SELECT payload FROM graph_lead_cycle_events WHERE project_id=$p AND sequence>$from AND sequence<=$to ORDER BY sequence",
                    r => r.GetString(0), ("$p", context.AuthorityId), ("$from", from), ("$to", through));
                return (session, Serialize(new { batches, facts }));
            }
            finally { foreach (var document in ranges) document.Dispose(); }
        });
    });
}
