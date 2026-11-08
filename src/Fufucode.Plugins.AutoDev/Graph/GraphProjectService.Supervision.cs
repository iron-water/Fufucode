using Fufucode.App.Abstractions.Graph;
using Fufucode.Core.Automation.Rebirth;
using static Fufucode.Plugins.AutoDev.Graph.GraphStorage;

namespace Fufucode.Plugins.AutoDev.Graph;

public sealed partial class GraphProjectService
{
    /// <summary>Objective anomaly evidence only. Never decides technical progress from error counts or self-reported success.</summary>
    private void InspectRepeatedFailure(ProjectContext context, string ticketId)
    {
        var authority = context.Kernel.ReadSnapshot(context.AuthorityId);
        var recent = authority.Runs.Where(r => r.TicketId == ticketId).OrderByDescending(r => r.StartedUtc).Take(4).ToArray();
        if (recent.Length < 2 || recent[0].Status is not (RebirthRunStatus.Failed or RebirthRunStatus.Interrupted)) return;
        var identical = recent.TakeWhile(r => r.Status == recent[0].Status && r.Detail == recent[0].Detail).Count();
        if (identical < 2) return;
        var options = context.Kernel.Atomic((c, tx) => Options(context, c, tx));
        // Thresholds are reactions to identical objective failures, not a cap on repair attempts.
        var pause = options.Supervision == GraphSupervisionLevel.Strict ||
            options.Supervision == GraphSupervisionLevel.Standard && identical >= 3;
        Change(context, "Kernel observed repeated execution failure", (c, tx) =>
        {
            Entry(context, c, tx, ticketId, null, "supervision", Serialize(new
            { level = options.Supervision.ToString(), identicalFailures = identical, action = pause ? "pause-and-replan" : "record-warning",
                sourceRuns = recent.Take(identical).Select(r => r.Id), technicalProgress = "not inferred" }), null, "host");
            return true;
        });
        if (pause) PauseAutonomy(context, "Current human supervision policy paused repeated identical failures");
    }

    /// <summary>Deterministic audit; no model invocation, wall-clock death inference or automatic effect replay.</summary>
    public string InspectWatchdog()
    {
        lock (_gate)
        {
            HumanOnly(); var context = Current;
            var authority = context.Kernel.ReadSnapshot(context.AuthorityId);
            return Serialize(new
            {
                unknownRuns = authority.Runs.Where(r => r.Status == RebirthRunStatus.Unknown).Select(r => r.Id),
                unmatchedRuns = authority.Runs.Where(r => r.Status == RebirthRunStatus.Running &&
                    !context.Runs.Values.Any(m => m.Id == r.Id)).Select(r => r.Id),
                pendingRequests = ReadOrganizationSnapshot().Messages.Where(m => m.RequestStatus == GraphRequestStatus.Open)
                    .Select(m => new { m.Id, m.Content.Address, m.Content.RecipientId }),
                policy = context.Kernel.Atomic((c, tx) => Options(context, c, tx)).Supervision.ToString(),
                automaticReplay = false
            });
        }
    }
}
