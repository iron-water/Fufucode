using static Fufucode.Core.Automation.AutomationStore;
using System.Diagnostics;
using System.Text;
using Fufucode.App.Abstractions.Automation.Harnesses;

namespace Fufucode.Core.Automation;

/// <summary>Lifecycle of one managed checkout. Persisted as text, so an older reader never guesses.</summary>
public enum ConvWorktreeState
{
    /// <summary>Declared intention only. Nothing was created and no command was run.</summary>
    Intent,
    /// <summary>`git worktree add` was started; the result is not yet known.</summary>
    Creating,
    /// <summary>The checkout exists and its real HEAD/branch matched the declaration.</summary>
    Created,
    /// <summary>The checkout exists but does not match the declared base/branch; never auto-repaired.</summary>
    Mismatch,
    /// <summary>Creation was attempted and failed; the detail is preserved.</summary>
    CreateFailed,
    /// <summary>The owning task released the tree. The files are never deleted by this class.</summary>
    Released
}

/// <summary>The declaration that must be durable before any Git command is run.</summary>
public sealed record ConvWorktreeIntent(string WorktreeId, string ProjectId, string RepositoryRoot,
    string BaseSha, string Branch, string Path, long CreatedUtc);

/// <summary>The persisted record of one managed checkout.</summary>
public sealed record ConvWorktreeRecord(string WorktreeId, string ProjectId, string RepositoryRoot,
    string BaseSha, string Branch, string Path, ConvWorktreeState State, long CreatedUtc,
    string? VerifiedHead, string? VerifyError, long? ReleasedUtc);

/// <summary>
/// One read-only observation of a managed checkout. The verdict is machine-readable: an unknown
/// result is `indeterminate`, never a silent success.
/// </summary>
public sealed record ConvWorktreeReconcile(ConvWorktreeRecord Record, string? ObservedHead,
    bool? TrackedClean, string Verdict, string Detail);

/// <summary>
/// CONV-1 controlled Git worktree lifecycle. The declaration is written first, the checkout is
/// created with a real `git worktree add`, the real HEAD and branch are verified against the
/// declaration, an unknown outcome is reconciled instead of retried, and ownership release never
/// deletes a tree. Lease ownership is persisted in this class's own tables; the U1 write leases and
/// the existing read-only <c>GitWorktreeService</c> are left untouched.
/// </summary>
public sealed class WorktreeLifecycleManager
{
    private readonly AutomationStore _store;
    private readonly IGitEvidence _git;
    private readonly string _root;

    public WorktreeLifecycleManager(AutomationStore store, IGitEvidence? git = null,
        string? worktreeRoot = null)
    {
        _store = store;
        _git = git ?? GitCliEvidence.Shared;
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(
            worktreeRoot ?? Path.Combine(new AppDataLayout().Root, "worktrees")));
        Migrate();
    }

    public string WorktreeRoot => _root;

    private void Migrate()
    {
        using var c = _store.OpenConv();
        using var tx = c.BeginTransaction();
        using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText = """
            CREATE TABLE IF NOT EXISTS conv_worktrees (
              worktree_id TEXT PRIMARY KEY, project_id TEXT NOT NULL, repository_root TEXT NOT NULL,
              base_sha TEXT NOT NULL, branch TEXT NOT NULL, path TEXT NOT NULL,
              path_key TEXT NOT NULL UNIQUE, state TEXT NOT NULL, created_utc INTEGER NOT NULL,
              verified_head TEXT, verify_error TEXT, released_utc INTEGER);
            CREATE TABLE IF NOT EXISTS conv_worktree_releases (
              worktree_id TEXT PRIMARY KEY REFERENCES conv_worktrees(worktree_id),
              released_utc INTEGER NOT NULL, detail TEXT NOT NULL);
            """;
        q.ExecuteNonQuery();
        tx.Commit();
    }

    // ------------------------------------------------------------------ declaration

    /// <summary>
    /// Persist the intention before anything is created. The repository root, the base commit, the
    /// branch and the isolated path are all verified against the real repository now, so a later
    /// creation cannot invent an identity.
    /// </summary>
    public ConvWorktreeRecord DeclareIntent(ConvWorktreeIntent intent, long nowUtc)
    {
        foreach (var value in new[] { intent.WorktreeId, intent.ProjectId, intent.RepositoryRoot,
            intent.BaseSha, intent.Branch, intent.Path })
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (!Path.IsPathFullyQualified(intent.Path))
            throw new ArgumentException("A managed checkout path must be absolute.", nameof(intent));
        if (!Path.IsPathFullyQualified(intent.RepositoryRoot) || !Directory.Exists(intent.RepositoryRoot))
            throw new DirectoryNotFoundException(intent.RepositoryRoot);
        if (!GitCliEvidence.IsSha(intent.BaseSha))
            throw new ArgumentException("The baseline must be a full commit SHA.", nameof(intent));
        if (!IsUnderRoot(intent.Path))
            throw new ArgumentException("A managed checkout must stay inside the managed worktree root.",
                nameof(intent));
        if (!CommitExists(intent.RepositoryRoot, intent.BaseSha))
            throw new ArgumentException("The declared baseline is not a commit in this repository.",
                nameof(intent));
        if (BranchExists(intent.RepositoryRoot, intent.Branch))
            throw new InvalidOperationException($"Branch '{intent.Branch}' already exists.");
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(intent.Path));
        var key = PathKey(path);

        using var c = _store.OpenConv();
        using var tx = c.BeginTransaction();
        using (var existing = ConvSql(c, tx,
            "SELECT repository_root,base_sha,branch,path FROM conv_worktrees WHERE worktree_id=$id",
            ("$id", intent.WorktreeId)))
        using (var r = existing.ExecuteReader())
        {
            if (r.Read())
            {
                var same = r.GetString(0) == intent.RepositoryRoot && r.GetString(1) == intent.BaseSha &&
                    r.GetString(2) == intent.Branch && PathKey(r.GetString(3)) == key;
                if (!same)
                    throw new InvalidOperationException(
                        $"Managed checkout '{intent.WorktreeId}' was declared with different content.");
                tx.Commit();
                return GetWorktree(intent.WorktreeId);
            }
        }
        using (var clash = ConvSql(c, tx,
            "SELECT worktree_id FROM conv_worktrees WHERE path_key=$key", ("$key", key)))
        {
            if (clash.ExecuteScalar() is string owner)
                throw new InvalidOperationException(
                    $"Managed checkout path is already claimed by '{owner}'.");
        }
        using var insert = ConvSql(c, tx, """
            INSERT INTO conv_worktrees(worktree_id,project_id,repository_root,base_sha,branch,path,
                                       path_key,state,created_utc,verified_head,verify_error,released_utc)
            VALUES($id,$project,$repo,$base,$branch,$path,$key,$state,$now,NULL,NULL,NULL)
            """, ("$id", intent.WorktreeId), ("$project", intent.ProjectId),
            ("$repo", intent.RepositoryRoot), ("$base", intent.BaseSha), ("$branch", intent.Branch),
            ("$path", path), ("$key", key), ("$state", ConvWorktreeState.Intent.ToString()),
            ("$now", nowUtc));
        insert.ExecuteNonQuery();
        _store.AppendConvEvent(c, tx, Event("intent", intent.WorktreeId), "worktree.intent", intent.WorktreeId,
            null, null, null, Payload(intent.WorktreeId, ConvWorktreeState.Intent, null, null), nowUtc);
        tx.Commit();
        return GetWorktree(intent.WorktreeId);
    }

    // ------------------------------------------------------------------ creation

    /// <summary>
    /// Create the declared checkout once. A tree that already exists, or whose creation outcome is
    /// unknown, is reconciled instead of being created a second time.
    /// </summary>
    public async Task<ConvWorktreeRecord> CreateAsync(string worktreeId, long nowUtc,
        CancellationToken cancellationToken = default)
    {
        var record = GetWorktree(worktreeId);
        if (record.State is ConvWorktreeState.Created or ConvWorktreeState.Mismatch
            or ConvWorktreeState.Released)
            return record;
        using (var c = _store.OpenConv())
        using (var tx = c.BeginTransaction())
        {
            using var claim = ConvSql(c, tx, """
                UPDATE conv_worktrees SET state=$creating WHERE worktree_id=$id AND state=$intent
                """, ("$creating", ConvWorktreeState.Creating.ToString()), ("$id", worktreeId),
                ("$intent", ConvWorktreeState.Intent.ToString()));
            if (claim.ExecuteNonQuery() != 1)
            {
                // Another creation is already in flight, or the row moved on: never a second command.
                tx.Commit();
                return GetWorktree(worktreeId);
            }
            tx.Commit();
        }

        var path = record.Path;
        if (Directory.Exists(path))
        {
            // A directory that already exists is never overwritten and never deleted: reconcile it.
            return await ReconcileAsync(worktreeId, cancellationToken).ConfigureAwait(false)
                is { } observation ? GetWorktree(worktreeId) : record;
        }
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
        var (code, _, error) = await RunAsync(record.RepositoryRoot,
            ["worktree", "add", "-b", record.Branch, path, record.BaseSha], cancellationToken)
            .ConfigureAwait(false);
        if (code != 0)
        {
            // A non-zero `git worktree add` result is a failure to record, never silently retried.
            if (Directory.Exists(path))
                return await ReconcileAsync(worktreeId, cancellationToken).ConfigureAwait(false) is { }
                    ? GetWorktree(worktreeId) : record;
            Save(worktreeId, ConvWorktreeState.CreateFailed, null,
                Truncate($"git worktree add exit={code}: {error}"));
            return GetWorktree(worktreeId);
        }
        return await ReconcileAsync(worktreeId, cancellationToken).ConfigureAwait(false) is { }
            ? GetWorktree(worktreeId) : record;
    }

    // ------------------------------------------------------------------ observation

    /// <summary>
    /// Read-only reconciliation. The real HEAD and tracked-clean state are observed, compared with
    /// the declaration, and persisted; no worktree file is ever modified, moved or deleted.
    /// </summary>
    public async Task<ConvWorktreeReconcile> ReconcileAsync(string worktreeId,
        CancellationToken cancellationToken = default)
    {
        var record = GetWorktree(worktreeId);
        if (record.State == ConvWorktreeState.Released)
            return new(record, null, null, "released",
                "Ownership was released; the checkout is left untouched.");
        var head = await _git.HeadAsync(record.Path, cancellationToken).ConfigureAwait(false);
        if (head is null)
        {
            var indeterminate = record with { State = record.State == ConvWorktreeState.Released
                ? ConvWorktreeState.Released : record.State };
            Save(worktreeId, ConvWorktreeState.CreateFailed, null,
                $"No readable repository HEAD at '{record.Path}'.");
            return new(GetWorktree(worktreeId), null, null, "indeterminate",
                "The checkout is missing or is not a readable repository; nothing was created again.");
        }
        var clean = await _git.IsCleanAsync(record.Path, cancellationToken).ConfigureAwait(false);
        var branch = ReadBranch(record.Path);
        if (string.Equals(head, record.BaseSha, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(branch, record.Branch, StringComparison.Ordinal))
        {
            Save(worktreeId, ConvWorktreeState.Created, head, null);
            return new(GetWorktree(worktreeId), head, clean, "created",
                $"The checkout is at the declared baseline {record.BaseSha} on '{record.Branch}'.");
        }
        Save(worktreeId, ConvWorktreeState.Mismatch, head,
            Truncate($"observed HEAD {head} on '{branch ?? "(detached)"}', declared {record.BaseSha} on '{record.Branch}'"));
        return new(GetWorktree(worktreeId), head, clean, "head_mismatch",
            $"The checkout is at {head}, not the declared baseline {record.BaseSha}.");
    }

    /// <summary>
    /// Release ownership only. The checkout, its commits and any uncommitted work stay exactly where
    /// they are; this class never runs `git worktree remove` and never deletes a directory.
    /// </summary>
    public void ReleaseOwnership(string worktreeId, long nowUtc, string detail)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);
        var record = GetWorktree(worktreeId);
        if (record.State == ConvWorktreeState.Released) return;
        using var c = _store.OpenConv();
        using var tx = c.BeginTransaction();
        using var update = ConvSql(c, tx, """
            UPDATE conv_worktrees SET state=$released, released_utc=$now WHERE worktree_id=$id;
            INSERT INTO conv_worktree_releases(worktree_id,released_utc,detail) VALUES($id,$now,$detail);
            """, ("$released", ConvWorktreeState.Released.ToString()), ("$now", nowUtc),
            ("$id", worktreeId), ("$detail", detail));
        update.ExecuteNonQuery();
        _store.AppendConvEvent(c, tx, Event("released", worktreeId), "worktree.released", worktreeId, null, null,
            null, Payload(worktreeId, ConvWorktreeState.Released, null, detail), nowUtc);
        tx.Commit();
    }

    public ConvWorktreeRecord GetWorktree(string worktreeId)
    {
        using var c = _store.OpenConv();
        using var q = ConvSql(c, null,
            "SELECT worktree_id,project_id,repository_root,base_sha,branch,path,state,created_utc,verified_head,verify_error,released_utc FROM conv_worktrees WHERE worktree_id=$id",
            ("$id", worktreeId));
        using var r = q.ExecuteReader();
        if (!r.Read())
            throw new KeyNotFoundException($"Managed checkout '{worktreeId}' was not found.");
        return new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
            r.GetString(5), Enum.Parse<ConvWorktreeState>(r.GetString(6)), r.GetInt64(7),
            r.IsDBNull(8) ? null : r.GetString(8), r.IsDBNull(9) ? null : r.GetString(9),
            r.IsDBNull(10) ? null : r.GetInt64(10));
    }

    public IReadOnlyList<ConvWorktreeRecord> ListWorktrees(string? projectId = null)
    {
        using var c = _store.OpenConv();
        using var q = ConvSql(c, null,
            "SELECT worktree_id FROM conv_worktrees WHERE ($project IS NULL OR project_id=$project) ORDER BY created_utc, worktree_id",
            ("$project", projectId));
        using var r = q.ExecuteReader();
        var ids = new List<string>();
        while (r.Read()) ids.Add(r.GetString(0));
        return ids.Select(GetWorktree).ToList();
    }

    // ------------------------------------------------------------------ helpers

    private void Save(string worktreeId, ConvWorktreeState state, string? verifiedHead,
        string? verifyError)
    {
        using var c = _store.OpenConv();
        using var q = ConvSql(c, null, """
            UPDATE conv_worktrees SET state=$state, verified_head=$head, verify_error=$error
            WHERE worktree_id=$id
            """, ("$state", state.ToString()), ("$head", verifiedHead), ("$error", verifyError),
            ("$id", worktreeId));
        q.ExecuteNonQuery();
    }

    private bool IsUnderRoot(string path)
    {
        var full = Path.GetFullPath(path);
        var prefix = _root + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static string PathKey(string path) =>
        OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;

    private static string Event(string kind, string id) => $"conv:worktree:{id}:{kind}";

    private static string Payload(string id, ConvWorktreeState state, string? head, string? detail)
    {
        var text = "{\"worktreeId\":\"" + Escape(id) + "\",\"state\":\"" + state + "\"";
        if (head is not null) text += ",\"head\":\"" + Escape(head) + "\"";
        if (detail is not null) text += ",\"detail\":\"" + Escape(detail) + "\"";
        return text + "}";
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string Truncate(string value) => value.Length <= 512 ? value : value[..512];

    private string? ReadBranch(string worktree)
    {
        var (code, output, _) = RunAsync(worktree, ["rev-parse", "--abbrev-ref", "HEAD"],
            CancellationToken.None).GetAwaiter().GetResult();
        if (code != 0) return null;
        var branch = output.Trim();
        return branch == "HEAD" ? null : branch;
    }

    private bool CommitExists(string repository, string sha)
    {
        var (code, _, _) = RunAsync(repository, ["rev-parse", "--verify", "-q", sha + "^{commit}"],
            CancellationToken.None).GetAwaiter().GetResult();
        return code == 0;
    }

    private bool BranchExists(string repository, string branch)
    {
        var (code, _, _) = RunAsync(repository, ["show-ref", "--verify", "-q", "refs/heads/" + branch],
            CancellationToken.None).GetAwaiter().GetResult();
        return code == 0;
    }

    /// <summary>
    /// Real Git execution. Arguments are passed as an argument list, never as a shell string, and a
    /// timeout kills the whole process tree so a hung Git cannot leave a half-created checkout.
    /// </summary>
    private static async Task<(int Code, string Output, string Error)> RunAsync(string directory,
        string[] args, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directory)) return (-1, "", "directory missing");
        var start = new ProcessStartInfo("git")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false)
        };
        foreach (var arg in new[] { "--no-optional-locks", "-C", directory, "-c",
            "core.fsmonitor=false" }.Concat(args))
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start);
        if (process is null) return (-1, "", "git could not be started");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            return (-1, "", "git timed out; the outcome is unknown and must be reconciled");
        }
        return (process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
    }
}
