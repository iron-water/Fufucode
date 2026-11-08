using Fufucode.App.Abstractions.Graph;
using Fufucode.Core.Automation.Rebirth;
using static Fufucode.Plugins.AutoDev.Graph.GraphStorage;

namespace Fufucode.Plugins.AutoDev.Graph;

public sealed partial class GraphProjectService
{
    /// <summary>
    /// Host-only ingestion of actual settled session messages. Never exposed as a model tool.
    /// User text is an input, assistant text is a claim; neither is a run receipt or acceptance.
    /// </summary>
    public void RecordSessionMessage(string? ticketId, string? moduleId, string sessionId,
        string messageId, string role, string text)
    {
        lock (_gate)
        {
            HumanOnly(); CheckAlive();
            sessionId = Required(sessionId, nameof(sessionId));
            messageId = Required(messageId, nameof(messageId));
            if (role is not ("user" or "assistant")) throw new ArgumentException("Only actual user or assistant messages may be recorded.", nameof(role));
            ArgumentNullException.ThrowIfNull(text);
            ProjectContext context;
            if (ticketId is not null)
            {
                var matches = _projects.Values.Where(p => p.Kernel.ReadSnapshot(p.AuthorityId).Tickets.Any(t => t.Id == ticketId)).ToArray();
                if (matches.Length != 1) throw new InvalidOperationException("The session message ticket must belong to one open project.");
                context = matches[0];
                var linked = context.Kernel.Atomic((c, tx) => Convert.ToInt64(Scalar(c, tx,
                    "SELECT COUNT(*) FROM graph_session_links WHERE project_id=$project AND session_id=$session AND ticket_id=$ticket",
                    ("$project", context.AuthorityId), ("$session", sessionId), ("$ticket", ticketId))) > 0);
                if (!linked) throw new InvalidOperationException("Link the actual session to this ticket before ingesting messages.");
            }
            else
            {
                var matches = _projects.Values.Where(p => p.Kernel.Atomic((c, tx) => Convert.ToInt64(Scalar(c, tx,
                    "SELECT COUNT(*) FROM graph_session_links WHERE project_id=$project AND session_id=$session",
                    ("$project", p.AuthorityId), ("$session", sessionId))) > 0)).ToArray();
                if (matches.Length > 1) throw new InvalidOperationException("Session belongs to multiple open projects; provide its ticket scope.");
                context = matches.SingleOrDefault() ?? Current;
            }
            var payload = Serialize(new { ticketId, moduleId, sessionId, messageId, role, text });
            var command = "session-message-" + Hash(sessionId + "\0" + messageId);
            var conflict = false;
            var changed = context.Kernel.Atomic((c, tx) =>
            {
                if (moduleId is not null) Module(context, c, tx, moduleId);
                var prior = Scalar(c, tx, "SELECT payload_hash FROM graph_receipts WHERE project_id=$project AND command_id=$command",
                    ("$project", context.AuthorityId), ("$command", command)) as string;
                if (prior is not null)
                {
                    if (prior == Hash(payload)) return false;
                    Entry(context, c, tx, ticketId, moduleId, "conflict", "Settled session message identity was reused with different content or scope; original retained.",
                        "session:" + sessionId + "/message:" + messageId, "system");
                    Capture(context, c, tx, "Rejected session message identity conflict"); conflict = true; return true;
                }
                Execute(c, tx, "INSERT OR IGNORE INTO graph_session_links VALUES($project,$session,$ticket)",
                    ("$project", context.AuthorityId), ("$session", sessionId), ("$ticket", ticketId ?? ""));
                var source = "session:" + sessionId + "/message:" + messageId;
                if (role == "user")
                {
                    context.Kernel.RecordHumanInput(command + ":input", context.AuthorityId, ticketId, "human", RebirthInputKind.Query, text, Now(),
                        inputId: "input-" + Hash(command)[..24]);
                    var recipient = ticketId is null ? "project-lead" : "";
                    SendOrganizationMessage(command + ":addressed", new GraphMessageDraft(recipient, false, "query", text,
                        ticketId, moduleId, Revision(context, c, tx))
                    {
                        Communication = GraphCommunicationKind.Request,
                        Address = ticketId is null ? null : new GraphMessageAddress(TicketId: ticketId)
                    });
                }
                Entry(context, c, tx, ticketId, moduleId, role == "assistant" ? "claim" : "session-message", text, source,
                    role == "assistant" ? "agent" : "human");
                Execute(c, tx, "INSERT INTO graph_receipts VALUES($project,$command,$hash,$result)",
                    ("$project", context.AuthorityId), ("$command", command), ("$hash", Hash(payload)),
                    ("$result", Serialize(new { status = "recorded", source, role })));
                Capture(context, c, tx, "Recorded actual " + role + " session message");
                return true;
            });
            if (changed)
            {
                Notify();
                if (role == "user") SignalProjectLead(context.Path, "human-message", command);
            }
            if (conflict) throw new InvalidOperationException("Conflicting session message identity; original text and conflict record were retained.");
        }
    }
}
