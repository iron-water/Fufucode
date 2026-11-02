using Fufucode.App.Abstractions.Graph;
using Fufucode.Core.Automation.Rebirth;
using Microsoft.Data.Sqlite;
using static Fufucode.Plugins.AutoDev.Graph.GraphStorage;

namespace Fufucode.Plugins.AutoDev.Graph;

/// <summary>Durable Graph projection and managed execution over the single Rebirth project authority.</summary>
public sealed partial class GraphProjectService : IGraphProjectService
{
    private sealed class ProjectContext : IDisposable
    {
        internal required string HostId;
        internal required string Path;
        internal required string AuthorityId;
        internal required RebirthKernel Kernel;
        internal required FileStream OwnerLock;
        internal readonly CancellationTokenSource Lifetime = new();
        internal readonly Dictionary<string, ManagedRun> Runs = new(StringComparer.Ordinal);
        internal readonly SemaphoreSlim ScanGate = new(1, 1);
        internal CancellationTokenSource? AutonomyCancellation;
        internal Task? Pump;
        internal Task? Scan;
        internal CancellationTokenSource? ScanCancellation;
        public void Dispose()
        {
            Lifetime.Dispose(); ScanGate.Dispose(); AutonomyCancellation?.Dispose(); Kernel.Dispose(); OwnerLock.Dispose();
        }
    }

    private sealed class ManagedRun
    {
        internal required string Id;
        internal required string TicketId;
        internal required CancellationTokenSource Cancellation;
        internal readonly TaskCompletionSource Finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task Task => Finished.Task;
        internal bool StopRequested;
        internal long LastProgressUtc;
    }

    private readonly object _gate = new();
    private readonly string _dataRoot;
    private readonly IGraphRunExecutor _executor;
    private readonly Dictionary<string, ProjectContext> _projects = new(StringComparer.Ordinal);
    private ProjectContext? _current;
    private bool _disposed;
    private bool _disposing;
    private string _actor = "human";
    private int _notificationSuppression;
    private bool _notificationPending;

    internal T InProjectScope<T>(string projectPath, Func<T> action)
    {
        lock (_gate)
        {
            CheckAlive();
            if (!_projects.TryGetValue(PathKey(projectPath), out var bound))
                throw new InvalidOperationException("The tool's project is not open in this execution owner.");
            var selected = _current;
            _notificationSuppression++;
            _current = bound;
            try { return action(); }
            finally
            {
                _current = selected;
                _notificationSuppression--;
                if (_notificationSuppression == 0 && _notificationPending) { _notificationPending = false; Notify(); }
            }
        }
    }
    private string? _agentCommand;
    private ProjectContext? _mutationContext;
    private SqliteConnection? _connection;
    private SqliteTransaction? _transaction;
    public event Action? Changed;

    public GraphProjectService(string dataRoot, IGraphRunExecutor executor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        _dataRoot = Canonical(dataRoot);
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    public void SetProject(string projectId, string projectPath)
    {
        lock (_gate)
        {
            CheckAlive();
            var binding = GraphProjectIdentity.Resolve(_dataRoot, projectId, projectPath);
            var key = PathKey(binding.ProjectPath);
            if (_projects.TryGetValue(key, out var existing))
            { existing.HostId = projectId; _current = existing; Notify(); return; }
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(binding.Database)!);
            // A second Graph execution owner must not mistake a live run for a crashed run.
            var ownerLock = new FileStream(binding.Database + ".graph-owner.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            RebirthKernel? kernel = null;
            try
            {
                kernel = new RebirthKernel(binding.Database);
                var context = new ProjectContext { HostId = projectId, Path = binding.ProjectPath,
                    AuthorityId = binding.AuthorityId, Kernel = kernel, OwnerLock = ownerLock };
                kernel.Atomic((c, tx) =>
                {
                    Migrate(c, tx);
                    if (Scalar(c, tx, "SELECT project_id FROM rb_projects WHERE project_id=$id", ("$id", context.AuthorityId)) is null)
                        kernel.EnsureProjectShell(Id("register"), context.AuthorityId, System.IO.Path.GetFileName(context.Path), Now());
                    Execute(c, tx, """
                        INSERT OR IGNORE INTO graph_state(project_id,project_path,options_json)
                        VALUES($id,$path,$options)
                        """, ("$id", context.AuthorityId), ("$path", context.Path), ("$options", Serialize(new GraphOptions())));
                    foreach (var run in kernel.ReadSnapshot(context.AuthorityId).Runs.Where(r => r.Executor == "graph" && r.Status is RebirthRunStatus.Running or RebirthRunStatus.Queued))
                        kernel.CompleteRun(Id("recovery"), run.Id, RebirthRunOutcome.Unknown, "owner-restarted",
                            "The execution owner restarted before receiving a terminal receipt. Reconcile side effects before retrying.", Now());
                    var recoveredOptions = Options(context, c, tx);
                    Execute(c, tx, "UPDATE graph_state SET autonomy=0,options_json=$options WHERE project_id=$id",
                        ("$options", Serialize(recoveredOptions with { AutoAdvance = false })), ("$id", context.AuthorityId));
                    Capture(context, c, tx, "Project opened; automatic execution requires explicit resume");
                    return true;
                });
                _projects.Add(key, context); _current = context;
            }
            catch { kernel?.Dispose(); ownerLock.Dispose(); throw; }
            Notify();
        }
    }

    private ProjectContext Current
    {
        get { CheckAlive(); return _current ?? throw new InvalidOperationException("Select a project first."); }
    }
    private void CheckAlive()
    {
        if (_disposed || _disposing) throw new ObjectDisposedException(nameof(GraphProjectService));
    }

    private void Notify()
    {
        if (_notificationSuppression != 0) { _notificationPending = true; return; }
        if (Changed is null) return;
        foreach (Action subscriber in Changed.GetInvocationList())
            try { subscriber(); } catch { /* A view callback cannot roll back committed authority. */ }
    }

    private T Change<T>(ProjectContext context, string summary, Func<SqliteConnection, SqliteTransaction, T> action)
    {
        if (_mutationContext == context) return action(_connection!, _transaction!);
        var result = context.Kernel.Atomic((c, tx) =>
        {
            _mutationContext = context; _connection = c; _transaction = tx;
            try
            {
                var value = action(c, tx);
                Capture(context, c, tx, summary);
                return value;
            }
            finally { _mutationContext = null; _connection = null; _transaction = null; }
        });
        Notify();
        return result;
    }

    private static long Revision(ProjectContext context, SqliteConnection c, SqliteTransaction tx) =>
        Convert.ToInt64(Scalar(c, tx, "SELECT revision FROM graph_state WHERE project_id=$id", ("$id", context.AuthorityId)));

    private static GraphOptions Options(ProjectContext context, SqliteConnection c, SqliteTransaction tx) =>
        Deserialize<GraphOptions>((string)Scalar(c, tx, "SELECT options_json FROM graph_state WHERE project_id=$id", ("$id", context.AuthorityId))!);

    public GraphOptions ReadOptions()
    {
        lock (_gate) { var context = Current; return context.Kernel.Atomic((c, tx) => Options(context, c, tx)); }
    }

    public GraphSnapshot ReadSnapshot(long? revision = null)
    {
        lock (_gate)
        {
            var context = Current;
            return context.Kernel.Atomic((c, tx) =>
            {
                var authority = context.Kernel.ReadSnapshot(context.AuthorityId);
                if (!revision.HasValue && authority.Project.Revision != Convert.ToInt64(Scalar(c, tx,
                    "SELECT kernel_revision FROM graph_state WHERE project_id=$id", ("$id", context.AuthorityId))))
                    Capture(context, c, tx, "Rebirth authority changed");
                var version = revision ?? Revision(context, c, tx);
                var json = Scalar(c, tx, "SELECT snapshot_json FROM graph_revisions WHERE project_id=$id AND version=$version",
                    ("$id", context.AuthorityId), ("$version", version)) as string
                    ?? throw new KeyNotFoundException("Unknown Graph revision: " + version);
                var snapshot = Deserialize<GraphSnapshot>(json);
                if (!revision.HasValue)
                {
                    // Live progress is an explicitly sampled preview, not an architecture revision.
                    // Structural/history snapshots remain immutable and the terminal receipt holds full output.
                    var progress = Query(c, tx, """
                        SELECT entry_id,ticket_id,text,reference,created_utc FROM graph_entries
                        WHERE project_id=$id AND kind='progress'
                        """, r => new GraphEntry(r.GetString(0), r.GetString(1), null, "progress", "executor", r.GetString(2),
                            r.IsDBNull(3) ? null : r.GetString(3), r.GetInt64(4)), ("$id", context.AuthorityId));
                    var entries = snapshot.Entries.ToDictionary(e => e.Id, StringComparer.Ordinal);
                    foreach (var entry in progress) entries[entry.Id] = entry;
                    snapshot = snapshot with
                    {
                        Entries = entries.Values.OrderBy(e => e.CreatedUtc).ThenBy(e => e.Id).ToArray(),
                        Runs = snapshot.Runs.Select(run => run.Status is "Running" or "StopRequested"
                            ? run with { Detail = progress.SingleOrDefault(e => e.Reference == run.Id)?.Text ?? run.Detail } : run).ToArray()
                    };
                }
                var navigation = Query(c, tx, "SELECT version,summary,created_utc FROM graph_revisions WHERE project_id=$id ORDER BY version DESC",
                    r => new GraphRevision(r.GetInt64(0), r.GetString(1), r.GetInt64(2)), ("$id", context.AuthorityId));
                return snapshot with { ProjectId = context.HostId, IsHistorical = revision.HasValue, Revisions = navigation };
            });
        }
    }

    private static GraphSnapshot Capture(ProjectContext context, SqliteConnection c, SqliteTransaction tx, string summary)
    {
        var authority = context.Kernel.ReadSnapshot(context.AuthorityId);
        var version = checked(Revision(context, c, tx) + 1);
        var now = Now();
        Execute(c, tx, """
            UPDATE graph_state SET revision=$version,kernel_revision=$kernel WHERE project_id=$id
            """, ("$version", version), ("$kernel", authority.Project.Revision), ("$id", context.AuthorityId));
        Execute(c, tx, "INSERT INTO graph_revisions VALUES($id,$version,$summary,$now,'')",
            ("$id", context.AuthorityId), ("$version", version), ("$summary", summary), ("$now", now));
        var modules = Query(c, tx, "SELECT module_json FROM graph_modules WHERE project_id=$id ORDER BY module_id",
            r => Deserialize<GraphModule>(r.GetString(0)), ("$id", context.AuthorityId));
        var relations = Query(c, tx, "SELECT relation_json FROM graph_relations WHERE project_id=$id ORDER BY relation_id",
            r => Deserialize<GraphRelation>(r.GetString(0)), ("$id", context.AuthorityId));
        var tickets = authority.Tickets.Select(ticket => new GraphTicket(ticket.Id, ticket.Title, ticket.Goal, ticket.Status.ToString(),
            Query(c, tx, "SELECT module_id FROM graph_ticket_modules WHERE ticket_id=$id ORDER BY module_id", r => r.GetString(0), ("$id", ticket.Id)).ToArray(),
            ticket.Acceptance, Scalar(c, tx, "SELECT session_id FROM graph_ticket_links WHERE ticket_id=$id", ("$id", ticket.Id)) as string,
            ticket.CreatedUtc, ticket.UpdatedUtc)).ToArray();
        var entries = Query(c, tx, """
            SELECT entry_id,ticket_id,module_id,kind,actor,text,reference,created_utc FROM graph_entries
            WHERE project_id=$id ORDER BY created_utc,entry_id
            """, r => new GraphEntry(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2),
                r.GetString(3), r.GetString(4), r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6), r.GetInt64(7)), ("$id", context.AuthorityId));
        entries.AddRange(Query(c, tx, """
            SELECT entity_id,source_path,source_kind,content_hash,detail,observed_utc,version FROM graph_sources WHERE project_id=$id
            """, r => new GraphEntry("source-" + Hash(r.GetString(0) + ":" + r.GetString(1) + ":" + r.GetString(2))[..24], null,
                modules.Any(m => m.Id == r.GetString(0)) ? r.GetString(0) : null, "source", "scanner",
                Serialize(new { entityId = r.GetString(0), kind = r.GetString(2), hash = r.GetString(3), detail = r.GetString(4), version = r.GetInt64(6) }),
                r.GetString(1), r.GetInt64(5)), ("$id", context.AuthorityId)));
        entries.AddRange(authority.Evidence.Select(e => new GraphEntry(e.Id, e.TicketId, null, "evidence", e.Verifier, Serialize(e), e.Reference, e.CreatedUtc)));
        entries.AddRange(authority.Decisions.Select(d => new GraphEntry(d.Id, d.TicketId, null, d.Kind.ToString(), d.Actor, Serialize(d), d.Target, d.CreatedUtc)));
        entries.AddRange(authority.Inputs.Select(i => new GraphEntry(i.Id, i.TicketId, null, "input:" + i.Kind, i.Actor, i.Text, i.State.ToString(), i.CreatedUtc)));
        entries.AddRange(authority.Attention.Select(a => new GraphEntry(a.Id, a.TicketId, null, "attention", "system", Serialize(a), null, a.CreatedUtc)));
        var revisions = Query(c, tx, "SELECT version,summary,created_utc FROM graph_revisions WHERE project_id=$id ORDER BY version DESC",
            r => new GraphRevision(r.GetInt64(0), r.GetString(1), r.GetInt64(2)), ("$id", context.AuthorityId));
        var runs = authority.Runs.Select(run =>
        {
            var link = Query(c, tx, "SELECT session_id,stop_requested FROM graph_run_links WHERE run_id=$id",
                r => (Session: r.IsDBNull(0) ? null : r.GetString(0), Stop: r.GetInt32(1) != 0), ("$id", run.Id)).SingleOrDefault();
            return new GraphRun(run.Id, run.TicketId, link.Stop && run.Status is RebirthRunStatus.Running or RebirthRunStatus.Queued
                ? "StopRequested" : run.Status.ToString(), link.Session, run.Detail, run.StartedUtc, run.EndedUtc);
        }).ToArray();
        var options = Options(context, c, tx);
        var autonomy = Convert.ToInt64(Scalar(c, tx, "SELECT autonomy FROM graph_state WHERE project_id=$id", ("$id", context.AuthorityId))) != 0;
        var status = !options.Enabled ? "Disabled" : string.IsNullOrWhiteSpace(authority.Brief.Brief.Statement) ? "NeedsPurpose"
            : autonomy ? "Running" : authority.Project.Status == RebirthProjectStatus.Active ? "Paused" : authority.Project.Status.ToString();
        var snapshot = new GraphSnapshot(context.HostId, context.Path, authority.Project.Title, authority.Brief.Brief.Statement,
            status, version, false, modules, relations, tickets, entries.OrderBy(e => e.CreatedUtc).ThenBy(e => e.Id).ToArray(), revisions, runs);
        Execute(c, tx, "UPDATE graph_revisions SET snapshot_json=$json WHERE project_id=$id AND version=$version",
            ("$json", Serialize(snapshot)), ("$id", context.AuthorityId), ("$version", version));
        return snapshot;
    }

    private void Entry(ProjectContext context, SqliteConnection c, SqliteTransaction tx, string? ticketId, string? moduleId,
        string kind, string text, string? reference = null, string? actor = null)
    {
        Execute(c, tx, """
            INSERT INTO graph_entries VALUES($id,$project,$ticket,$module,$kind,$actor,$text,$reference,$now,$version)
            """, ("$id", Id("entry")), ("$project", context.AuthorityId), ("$ticket", ticketId), ("$module", moduleId),
            ("$kind", kind), ("$actor", actor ?? _actor), ("$text", text), ("$reference", reference), ("$now", Now()),
            ("$version", Revision(context, c, tx) + 1));
    }

    internal string ExecuteAgentCommand(string commandId, string payload, long? expectedRevision, Func<object?> action)
    {
        lock (_gate)
        {
            var context = Current;
            if (_agentCommand is not null) throw new InvalidOperationException("Nested model commands are not supported.");
            var mismatch = false;
            var json = context.Kernel.Atomic((c, tx) =>
            {
                EnsureAgentMandate(context, c, tx);
                var authorityRevision = context.Kernel.ReadSnapshot(context.AuthorityId).Project.Revision;
                if (authorityRevision != Convert.ToInt64(Scalar(c, tx, "SELECT kernel_revision FROM graph_state WHERE project_id=$id", ("$id", context.AuthorityId))))
                    Capture(context, c, tx, "Rebirth authority changed");
                var receipt = Query(c, tx, "SELECT payload_hash,result_json FROM graph_receipts WHERE project_id=$project AND command_id=$command",
                    r => (Hash: r.GetString(0), Json: r.GetString(1)), ("$project", context.AuthorityId), ("$command", commandId)).SingleOrDefault();
                if (receipt.Json is not null)
                {
                    if (receipt.Hash != Hash(payload))
                    {
                        Entry(context, c, tx, null, null, "conflict", "Command identity reused with different arguments", commandId, "system");
                        Capture(context, c, tx, "Rejected command identity conflict"); mismatch = true; return "";
                    }
                    return receipt.Json;
                }
                if (expectedRevision.HasValue && expectedRevision.Value != Revision(context, c, tx))
                {
                    Entry(context, c, tx, null, null, "conflict", Serialize(new { expectedRevision, actualRevision = Revision(context, c, tx), commandId }), null, "system");
                    Capture(context, c, tx, "Rejected stale Graph command"); mismatch = true; return "";
                }
                _actor = "agent"; _agentCommand = commandId;
                _mutationContext = context; _connection = c; _transaction = tx;
                try
                {
                    var value = Serialize(action());
                    Capture(context, c, tx, "Agent command: " + commandId);
                    Execute(c, tx, "INSERT INTO graph_receipts VALUES($project,$command,$hash,$json)",
                        ("$project", context.AuthorityId), ("$command", commandId), ("$hash", Hash(payload)), ("$json", value));
                    return value;
                }
                finally { _actor = "human"; _agentCommand = null; _mutationContext = null; _connection = null; _transaction = null; }
            });
            Notify();
            if (mismatch) throw new InvalidOperationException("Graph command conflict; refresh the snapshot and use a new command identity.");
            KickQueue(context);
            return json;
        }
    }

    private static void EnsureAgentMandate(ProjectContext context, SqliteConnection c, SqliteTransaction tx)
    {
        var options = Options(context, c, tx);
        var brief = context.Kernel.ReadSnapshot(context.AuthorityId).Brief.Brief;
        if (!options.Enabled) throw new InvalidOperationException("Graph is disabled.");
        if (string.IsNullOrWhiteSpace(brief.Statement) || options.BudgetMinutes <= 0 || options.ComputeQuota <= 0
            || brief.Autonomy != RebirthAutonomy.WithinMandate)
            throw new InvalidOperationException("No active project mandate for model mutations.");
        var started = Scalar(c, tx, "SELECT budget_started_utc FROM graph_state WHERE project_id=$id", ("$id", context.AuthorityId));
        if (started is not null && Now() - Convert.ToInt64(started) >= options.BudgetMinutes * 60_000L)
            throw new InvalidOperationException("The project execution budget expired.");
    }
}
