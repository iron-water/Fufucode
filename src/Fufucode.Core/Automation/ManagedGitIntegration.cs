using System.Text.Json;

namespace Fufucode.Core.Automation;

public enum ManagedIntegrationStatus { Preparing, Conflict, Prepared, Committing, Integrated, Publishing, Published, Interrupted }

public sealed record ManagedIntegrationTransaction(
    string Id, string RepositoryPath, string WorktreePath, string JournalPath,
    string CandidateCommit, string BaseCommit, string? PreviousIntegratedCommit,
    string? PreparedCommit, ManagedIntegrationStatus Status, string? Error = null,
    string? PublishTargetHead = null, string? PublishTargetRef = null);

/// <summary>
/// Journaled Git transactions, not a database/Git atomic transaction. The host serializes repository
/// writers and binds fresh trusted validation to PreparedCommit before CommitAsync. JSON is evidence,
/// never authority. Conflict trees and all candidate refs are deliberately retained.
/// </summary>
public static class ManagedGitIntegration
{
    public const string IntegratedRef = "refs/heads/autodev/integrated";
    private static Task<ManagedDevelopmentWorkspace.GitResult> Git(string cwd, string[] args, CancellationToken ct, bool failure = false)
        => ManagedDevelopmentWorkspace.Git(cwd, args, ct, allowFailure: failure);
    private static async Task<string> Head(string repo, string value, CancellationToken ct)
        => (await Git(repo, ["rev-parse", "--verify", value + "^{commit}"], ct)).Text.Trim();
    private static async Task Save(ManagedIntegrationTransaction transaction)
    {
        var temporary = transaction.JournalPath + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(transaction, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, transaction.JournalPath, true);
    }

    public static async Task<string?> GetIntegratedCommitAsync(string repositoryPath, CancellationToken cancellationToken = default)
    {
        var result = await Git(repositoryPath, ["rev-parse", "--verify", IntegratedRef + "^{commit}"], cancellationToken, true);
        return result.Code == 0 ? result.Text.Trim() : null;
    }

    public static async Task<ManagedIntegrationTransaction> PrepareAsync(ManagedDevelopmentWorkspace workspace,
        string applicationSandboxRoot, CancellationToken cancellationToken = default)
    {
        var manifest = (await ManagedDevelopmentWorkspace.LoadAsync(workspace.ManifestPath, cancellationToken)).Manifest;
        if (manifest.CandidateCommit is null || manifest.CandidateRef is null)
            throw new InvalidOperationException("Capture a versioned candidate before integration.");
        if (await Head(manifest.RepositoryPath, manifest.CandidateRef, cancellationToken) != manifest.CandidateCommit)
            throw new InvalidOperationException("Candidate reference changed.");
        var candidatePatch = await Git(manifest.RepositoryPath,
            ["diff", "--binary", "--full-index", "--src-prefix=a/", "--dst-prefix=b/", "--no-ext-diff", "--no-textconv", manifest.BaseHead, manifest.CandidateCommit, "--"], cancellationToken);
        var recordedPatch = await File.ReadAllBytesAsync(manifest.PatchPath, cancellationToken);
        if (!candidatePatch.Bytes.SequenceEqual(recordedPatch))
            throw new InvalidOperationException("Candidate commit and recorded patch differ.");
        var previous = await GetIntegratedCommitAsync(manifest.RepositoryPath, cancellationToken);
        var baseline = previous ?? manifest.BaseHead;
        // Reject unrelated histories before allocating a merge tree.
        await Git(manifest.RepositoryPath, ["merge-base", baseline, manifest.CandidateCommit], cancellationToken);
        var root = Path.GetFullPath(applicationSandboxRoot);
        var repo = Path.GetFullPath(manifest.RepositoryPath);
        if (root.StartsWith(repo.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || root == repo)
            throw new ArgumentException("Integration sandbox must be outside the user repository.");
        var id = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(root, "integration-" + id);
        Directory.CreateDirectory(directory);
        var tx = new ManagedIntegrationTransaction(id, repo, Path.Combine(directory, "tree"),
            Path.Combine(directory, "journal.json"), manifest.CandidateCommit, baseline, previous, null, ManagedIntegrationStatus.Preparing);
        await Save(tx);
        try
        {
            await Git(repo, ["worktree", "add", "--detach", "--", tx.WorktreePath, baseline], cancellationToken);
            var merged = await Git(tx.WorktreePath, ["-c", "user.name=AutoDev", "-c", "user.email=autodev@localhost",
                "-c", "core.hooksPath=", "merge", "--no-ff", "--no-edit", manifest.CandidateCommit], cancellationToken, true);
            if (merged.Code != 0)
            {
                tx = tx with { Status = ManagedIntegrationStatus.Conflict, Error = merged.Text + " Merge failed; inspect retained index and conflict tree." };
                await Save(tx);
                return tx;
            }
            var prepared = await Head(tx.WorktreePath, "HEAD", cancellationToken);
            await Git(repo, ["update-ref", "refs/autodev/integrations/" + id, prepared], cancellationToken);
            tx = tx with { Status = ManagedIntegrationStatus.Prepared, PreparedCommit = prepared };
            await Save(tx);
            return tx;
        }
        catch (Exception ex)
        {
            await Save(tx with { Status = ManagedIntegrationStatus.Interrupted, Error = ex.Message });
            throw;
        }
    }

    /// <summary>After conflict repair, record the actual committed result; requires fresh validation afterwards.</summary>
    public static async Task<ManagedIntegrationTransaction> RefreshPreparedAsync(ManagedIntegrationTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        await RequireClean(transaction.WorktreePath, cancellationToken);
        var commit = await Head(transaction.WorktreePath, "HEAD", cancellationToken);
        await Git(transaction.RepositoryPath, ["merge-base", "--is-ancestor", transaction.BaseCommit, commit], cancellationToken);
        await Git(transaction.RepositoryPath, ["merge-base", "--is-ancestor", transaction.CandidateCommit, commit], cancellationToken);
        await Git(transaction.RepositoryPath, ["update-ref", "refs/autodev/integrations/" + transaction.Id, commit], cancellationToken);
        var tx = transaction with { PreparedCommit = commit, Status = ManagedIntegrationStatus.Prepared, Error = null };
        await Save(tx);
        return tx;
    }

    public static async Task<ManagedIntegrationTransaction> CommitAsync(ManagedIntegrationTransaction transaction,
        string expectedPreparedCommit, bool hostValidated, CancellationToken cancellationToken = default)
    {
        if (!hostValidated) throw new UnauthorizedAccessException("Fresh host integration validation is required.");
        var tx = await LoadAsync(transaction.JournalPath, cancellationToken);
        if (tx.PreparedCommit != expectedPreparedCommit || tx.PreparedCommit is null)
            throw new InvalidOperationException("Validation does not identify this prepared version.");
        if (await Head(tx.WorktreePath, "HEAD", cancellationToken) != expectedPreparedCommit)
            throw new InvalidOperationException("Integration HEAD drifted after validation.");
        await RequireClean(tx.WorktreePath, cancellationToken);
        var current = await GetIntegratedCommitAsync(tx.RepositoryPath, cancellationToken);
        if (current == expectedPreparedCommit && tx.Status is ManagedIntegrationStatus.Committing or ManagedIntegrationStatus.Integrated)
        {
            tx = tx with { Status = ManagedIntegrationStatus.Integrated, Error = null };
            await Save(tx);
            return tx;
        }
        if (tx.Status != ManagedIntegrationStatus.Prepared || current != tx.PreviousIntegratedCommit)
            throw new InvalidOperationException("Integrated baseline changed; prepare and validate a new merge.");
        tx = tx with { Status = ManagedIntegrationStatus.Committing };
        await Save(tx);
        await Git(tx.RepositoryPath, ["update-ref", IntegratedRef, expectedPreparedCommit,
            current ?? new string('0', expectedPreparedCommit.Length)], cancellationToken);
        tx = tx with { Status = ManagedIntegrationStatus.Integrated };
        await Save(tx);
        return tx;
    }

    public static async Task<ManagedIntegrationTransaction> PublishAsync(ManagedIntegrationTransaction transaction,
        bool hostPermissionGranted, string expectedTargetHead, CancellationToken cancellationToken = default)
    {
        if (!hostPermissionGranted) throw new UnauthorizedAccessException("Separate current human publication authorization required.");
        var tx = await LoadAsync(transaction.JournalPath, cancellationToken);
        if (tx.Status is not (ManagedIntegrationStatus.Integrated or ManagedIntegrationStatus.Publishing or ManagedIntegrationStatus.Published) || tx.PreparedCommit is null)
            throw new InvalidOperationException("Only an integrated transaction can be published.");
        var targetRef = (await Git(tx.RepositoryPath, ["symbolic-ref", "--quiet", "HEAD"], cancellationToken)).Text.Trim();
        if (targetRef == IntegratedRef) throw new InvalidOperationException("User target must differ from the managed integrated ref.");
        await RequireClean(tx.RepositoryPath, cancellationToken);
        var actual = await Head(tx.RepositoryPath, "HEAD", cancellationToken);
        if (actual == tx.PreparedCommit && tx.Status is ManagedIntegrationStatus.Publishing or ManagedIntegrationStatus.Published &&
            tx.PublishTargetHead == expectedTargetHead && tx.PublishTargetRef == targetRef)
        {
            tx = tx with { Status = ManagedIntegrationStatus.Published };
            await Save(tx);
            return tx;
        }
        if (actual != expectedTargetHead) throw new InvalidOperationException("Publication target drifted; merge and validate the new target first.");
        await Git(tx.RepositoryPath, ["merge-base", "--is-ancestor", actual, tx.PreparedCommit], cancellationToken);
        tx = tx with { Status = ManagedIntegrationStatus.Publishing, PublishTargetHead = actual, PublishTargetRef = targetRef };
        await Save(tx);
        // Native ff-only checks index/worktree and synchronizes all three. Never update-ref/reset main.
        await Git(tx.RepositoryPath, ["-c", "core.hooksPath=", "merge", "--ff-only", "--no-edit", tx.PreparedCommit], cancellationToken);
        if (await Head(tx.RepositoryPath, "HEAD", cancellationToken) != tx.PreparedCommit)
            throw new InvalidOperationException("Publication did not reach the expected version; reconcile journal.");
        await RequireClean(tx.RepositoryPath, cancellationToken);
        tx = tx with { Status = ManagedIntegrationStatus.Published };
        await Save(tx);
        return tx;
    }

    public static async Task<ManagedIntegrationTransaction> LoadAsync(string journalPath, CancellationToken cancellationToken = default)
    {
        var tx = JsonSerializer.Deserialize<ManagedIntegrationTransaction>(await File.ReadAllTextAsync(journalPath, cancellationToken))
            ?? throw new InvalidOperationException("Empty integration journal.");
        if (Path.GetFullPath(tx.JournalPath) != Path.GetFullPath(journalPath) || !Enum.IsDefined(tx.Status))
            throw new InvalidOperationException("Invalid transaction identity.");
        var common = (await Git(tx.RepositoryPath, ["rev-parse", "--path-format=absolute", "--git-common-dir"], cancellationToken)).Text.Trim();
        var treeCommon = (await Git(tx.WorktreePath, ["rev-parse", "--path-format=absolute", "--git-common-dir"], cancellationToken)).Text.Trim();
        if (!string.Equals(common, treeCommon, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Integration tree repository mismatch.");
        return tx;
    }

    public static async Task<ManagedIntegrationTransaction> RecoverAsync(string journalPath, CancellationToken cancellationToken = default)
    {
        var tx = await LoadAsync(journalPath, cancellationToken);
        if (tx.Status == ManagedIntegrationStatus.Committing && await GetIntegratedCommitAsync(tx.RepositoryPath, cancellationToken) == tx.PreparedCommit)
            tx = tx with { Status = ManagedIntegrationStatus.Integrated };
        if (tx.Status == ManagedIntegrationStatus.Publishing && await Head(tx.RepositoryPath, "HEAD", cancellationToken) == tx.PreparedCommit &&
            (await Git(tx.RepositoryPath, ["symbolic-ref", "--quiet", "HEAD"], cancellationToken)).Text.Trim() == tx.PublishTargetRef)
        {
            await RequireClean(tx.RepositoryPath, cancellationToken);
            tx = tx with { Status = ManagedIntegrationStatus.Published };
        }
        // Never infer trusted validation from serialized state or retry publication without fresh authority.
        await Save(tx);
        return tx;
    }

    private static async Task RequireClean(string tree, CancellationToken ct)
    {
        if ((await Git(tree, ["status", "--porcelain=v1", "-z", "--untracked-files=all"], ct)).Bytes.Length != 0)
            throw new InvalidOperationException("Index/worktree contains changes or untracked files; retained without mutation.");
    }
}
