using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.Text.Json;
using Fufucode.App.Abstractions.Graph;
using Fufucode.Core;
using Fufucode.Core.Automation;
using Fufucode.Plugins.AutoDev.Graph;

namespace Fufucode.App.Integration;

public sealed record GraphValidationSpec(string Executable, string[] Arguments, TimeSpan Timeout);
public sealed record GraphIntegrationPermission(string TicketId, string RunId, string RepositoryPath,
    string BaseHead, string PatchHash);
public sealed record GraphCandidateValidation(string TicketId, string RunId, string ManifestPath,
    ManagedWorkspaceManifest Manifest, ManagedValidationContract Contract, ManagedValidationReceipt Receipt);
public sealed record GraphIntegratedValidation(GraphCandidateValidation Original, ManagedWorkspaceManifest Manifest,
    ManagedValidationContract Contract, ManagedValidationReceipt Receipt);
public sealed record GraphCandidateIntegration(ManagedWorkspaceManifest Manifest, bool RequiresPostIntegrationValidation)
{
    public ManagedIntegrationTransaction? Transaction { get; init; }
}
public sealed record GraphPublicationPermission(string TicketId, string RunId, string RepositoryPath,
    string ExpectedTargetHead, string IntegrationCommit);
public sealed record GraphIntegrationRepairBinding(string RepairTicketId, string OriginalTicketId, string OriginalRunId,
    string JournalPath, string WorktreePath, string CandidateCommit, string? PreparedCommit);
public sealed record GraphPublicationReceipt(string TicketId, string RunId, string IntegrationCommit,
    string JournalPath, ManagedIntegrationStatus Status);
public sealed record GraphIntegrationRecovery(string TicketId, string RunId, ManagedIntegrationTransaction Transaction,
    bool DatabaseRecorded, bool TicketDone, bool RequiresFreshValidation)
{
    public string Disposition { get; init; } = "Unknown";
    public string[] Findings { get; init; } = [];
    public bool ControlAllowsDelivery { get; init; }
    public bool IntegrationHeadMatches { get; init; }
    public bool IntegrationTreeClean { get; init; }
    public bool IntegratedRefContainsVersion { get; init; }
    // This Host does not own the executor process registry. Unknown is not absence or safe-to-retry.
    public bool ManagedProcessStateKnown => false;
    public bool NoSideEffectsProven => false;
}



/// <summary>Application-owned approval boundary. Historical receipt JSON is display data only.
/// The composition root must exclude other repository/workspace writers while using this service.</summary>
public sealed class GraphDevelopmentHost
{
    private readonly AppDataLayout layout;
    private readonly GraphProjectService graph;
    private readonly Func<ManagedValidationPermission, Task<bool>>? permissionGate;
    private readonly Func<GraphIntegrationPermission, Task<bool>>? integrationGate;
    private readonly Func<GraphPublicationPermission, Task<bool>>? publicationGate;
    private readonly SemaphoreSlim serial = new(1, 1);
    private readonly Dictionary<GraphCandidateValidation, ManagedValidation> live = new(ReferenceEqualityComparer.Instance);
    // Deliberately strict: no model-provided exclusions and no blanket bin/obj allowance.
    private readonly Dictionary<GraphCandidateValidation, ManagedIntegrationTransaction> transactions = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<GraphCandidateValidation, ManagedWorkspaceManifest> integrated = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<GraphCandidateValidation, string> integratedFingerprints = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<GraphIntegratedValidation, ManagedValidation> integratedLive = new(ReferenceEqualityComparer.Instance);
    private readonly ManagedValidationPolicy policy;

    public GraphDevelopmentHost(AppDataLayout layout, GraphProjectService graph,
        Func<ManagedValidationPermission, Task<bool>>? permissionGate,
        Func<GraphIntegrationPermission, Task<bool>>? integrationGate = null,
        ManagedValidationPolicy? hostPolicy = null,
        Func<GraphPublicationPermission, Task<bool>>? publicationGate = null)
    {
        this.layout = layout ?? throw new ArgumentNullException(nameof(layout));
        this.graph = graph ?? throw new ArgumentNullException(nameof(graph));
        this.permissionGate = permissionGate;
        this.integrationGate = integrationGate;
        this.publicationGate = publicationGate;
        policy = hostPolicy ?? ManagedValidationPolicy.Strict;
        if (policy.GeneratedFiles.Count != 0 || policy.GeneratedDirectories.Any(p =>
            !new[] { "bin", "obj" }.Contains(p.Split('/').Last(), StringComparer.OrdinalIgnoreCase)))
            throw new ArgumentException("Host policy may allow only explicit project bin/obj directories.", nameof(hostPolicy));
    }

    public async Task<GraphCandidateValidation> ValidateCandidateAsync(string ticketId, string manifestPath,
        GraphValidationSpec validationSpec, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(validationSpec);
        if (!Path.IsPathFullyQualified(validationSpec.Executable) || validationSpec.Arguments is null ||
            validationSpec.Arguments.Any(a => a is null) || validationSpec.Timeout <= TimeSpan.Zero ||
            validationSpec.Timeout > TimeSpan.FromDays(1)) throw new ArgumentException("Invalid validation specification.");
        var spec = validationSpec with { Arguments = validationSpec.Arguments.ToArray() };
        await serial.WaitAsync(ct);
        try
        {
            var binding = Bind(ticketId, manifestPath);
            var workspace = await ManagedDevelopmentWorkspace.LoadAsync(binding.Path, ct);
            CheckManifest(workspace.Manifest, binding.ProjectPath);
            await VerifyPatchTree(workspace.Manifest, ct);
            // Never capture here: capture would overwrite the executor's fixed candidate.
            var fingerprint = ManagedValidation.Fingerprint(workspace.Manifest.WorktreePath, policy);
            var environment = "graph-host/" + Environment.ProcessId;
            var runner = new ManagedValidation(Path.Combine(layout.Root, "auto-dev", "validation", Guid.NewGuid().ToString("N")),
                environment, new(ticketId, binding.RunId, binding.Digest),
                permissionGate is null ? null : async (permission, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    var approved = await permissionGate(permission);
                    token.ThrowIfCancellationRequested();
                    var current = Bind(ticketId, binding.Path);
                    if (current != binding) throw new InvalidOperationException("Graph candidate changed during approval.");
                    return approved;
                }, policy: policy);
            var contract = new ManagedValidationContract(Guid.NewGuid().ToString("N"), ticketId, binding.RunId,
                binding.Digest, workspace.Manifest.WorktreePath, fingerprint, Path.GetFullPath(spec.Executable), spec.Arguments,
                spec.Timeout, environment, policy.Digest);
            var receipt = await runner.RunAsync(contract, ct);
            var result = new GraphCandidateValidation(ticketId, binding.RunId, binding.Path, workspace.Manifest, contract, receipt);
            lock (live) live.Add(result, runner);
            // Persistence conveys no authority; only VerifyCurrent on this host can do that.
            var display = Path.Combine(layout.Root, "auto-dev", "validation-receipts");
            RejectLinks(display);
            Directory.CreateDirectory(display);
            await File.WriteAllTextAsync(Path.Combine(display, contract.Id + ".json"), JsonSerializer.Serialize(result), ct);
            return result;
        }
        finally { serial.Release(); }
    }

    /// <summary>Live bridge for the controller: additionally bind any evidence to the exact criterion.
    /// Does not accept a ticket, write kernel Done, or trust deserialized receipt copies.</summary>
    public bool VerifyCurrent(GraphCandidateValidation result)
    {
        try
        {
            ManagedValidation? runner;
            lock (live) if (!live.TryGetValue(result, out runner)) return false;
            var binding = Bind(result.TicketId, result.ManifestPath);
            graph.RequireDeliveryAuthorization(result.TicketId);
            return binding.RunId == result.RunId && binding.Digest == result.Contract.AcceptanceDigest &&
                Same(binding.ProjectPath, result.Manifest.RepositoryPath) &&
                result.Receipt.Status == ManagedValidationStatus.Passed &&
                result.Receipt.ContractHash == Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(result.Contract))) &&
                FileHash(result.Manifest.PatchPath) == result.Manifest.PatchHash && runner.VerifyCurrent(result.Receipt);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or JsonException)
        { return false; }
    }

    public async Task<GraphCandidateIntegration> IntegrateCandidateAsync(GraphCandidateValidation result, CancellationToken ct = default)
    {
        await serial.WaitAsync(ct);
        try
        {
            if (!VerifyCurrent(result)) throw new InvalidOperationException("A current live passing validation is required.");
            graph.RequireDeliveryAuthorization(result.TicketId);
            var m = result.Manifest;
            var request = new GraphIntegrationPermission(result.TicketId, result.RunId, m.RepositoryPath, m.BaseHead, m.PatchHash);
            if (integrationGate is null || !await integrationGate(request)) throw new UnauthorizedAccessException("Separate integration approval required.");
            ct.ThrowIfCancellationRequested();
            if (!VerifyCurrent(result)) throw new InvalidOperationException("Candidate changed during integration approval.");
            graph.RequireDeliveryAuthorization(result.TicketId);
            var workspace = await ManagedDevelopmentWorkspace.LoadAsync(result.ManifestPath, ct);
            if (workspace.Manifest != m && JsonSerializer.Serialize(workspace.Manifest) != JsonSerializer.Serialize(m))
                throw new InvalidOperationException("Manifest changed after validation.");
            var integrationRoot = Path.Combine(layout.Root, "auto-dev", "integration");
            RejectLinks(integrationRoot);
            var transactionId = Guid.NewGuid().ToString("N");
            var directory = Path.Combine(integrationRoot, "integration-" + transactionId);
            Directory.CreateDirectory(directory);
            // Durable ticket/run intent precedes the first Git mutation, closing the orphan-binding gap.
            var intent = new HostIntegrationState(result.TicketId, result.RunId, result.ManifestPath,
                result.Contract.AcceptanceDigest, Path.Combine(directory, "journal.json"),
                workspace.Manifest.CandidateCommit ?? throw new InvalidOperationException("Versioned candidate required."), null, "Preparing");
            await SaveState(intent);
            var transaction = await ManagedGitIntegration.PrepareAsync(workspace, integrationRoot, ct, transactionId);
            await SaveState(intent with { PreparedCommit = transaction.PreparedCommit, Status = transaction.Status.ToString() });
            if (transaction.PreparedCommit is null)
                throw new InvalidOperationException("Integration requires repair; retained journal: " + transaction.JournalPath + ": " + transaction.Error);
            var integrated = workspace.Manifest;
            var targetFingerprint = ManagedValidation.Fingerprint(transaction.WorktreePath, policy);
            lock (live)
            {
                transactions[result] = transaction;
                this.integrated[result] = integrated;
                integratedFingerprints[result] = targetFingerprint;
            }
            // Applying a patch is not delivery verification. Controller must validate target, never mark Done here.
            return new(integrated, true) { Transaction = transaction };
        }
        finally { serial.Release(); }
    }

    /// <summary>Validate the applied target, not the isolated candidate. Caller must exclude concurrent writers.</summary>
    public async Task<GraphIntegratedValidation> ValidateIntegratedAsync(GraphCandidateValidation original,
        GraphValidationSpec spec, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        await serial.WaitAsync(ct);
        try
        {
            ManagedWorkspaceManifest manifest;
            lock (live)
                if (!integrated.TryGetValue(original, out manifest!))
                    throw new InvalidOperationException("This host must have integrated the original live candidate.");
            if (original.Receipt.ContractHash != Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(original.Contract))))
                throw new InvalidOperationException("Original approved contract changed.");
            if (!Same(spec.Executable, original.Contract.Executable) || spec.Timeout != original.Contract.Timeout ||
                spec.Arguments is null || !spec.Arguments.SequenceEqual(original.Contract.Arguments, StringComparer.Ordinal))
                throw new ArgumentException("Post-integration validation must use the original approved specification.");
            var binding = Bind(original.TicketId, original.ManifestPath);
            graph.RequireDeliveryAuthorization(original.TicketId);
            if (binding.RunId != original.RunId || binding.Digest != original.Contract.AcceptanceDigest)
                throw new InvalidOperationException("Ticket/run acceptance changed.");
            var current = await ManagedDevelopmentWorkspace.LoadAsync(original.ManifestPath, ct);
            if (JsonSerializer.Serialize(current.Manifest) != JsonSerializer.Serialize(manifest))
                throw new InvalidOperationException("Integrated manifest changed.");
            var transaction = transactions[original];
            await RequireHead(transaction.WorktreePath, transaction.PreparedCommit!, ct);
            string fingerprint;
            lock (live) fingerprint = integratedFingerprints[original];
            if (ManagedValidation.Fingerprint(transaction.WorktreePath, policy) != fingerprint)
                throw new InvalidOperationException("Applied target content changed before validation.");
            var environment = "graph-target/" + Environment.ProcessId + "/" + transaction.PreparedCommit;
            // Starting a replacement validation invalidates earlier capabilities for this integration.
            lock (live)
                foreach (var previous in integratedLive.Keys.Where(v => ReferenceEquals(v.Original, original)).ToArray())
                    integratedLive.Remove(previous);
            var runner = new ManagedValidation(Path.Combine(layout.Root, "auto-dev", "validation", Guid.NewGuid().ToString("N")),
                environment, new(original.TicketId, original.RunId, binding.Digest),
                permissionGate is null ? null : async (permission, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    var approved = await permissionGate(permission);
                    token.ThrowIfCancellationRequested();
                    if (Bind(original.TicketId, original.ManifestPath) != binding)
                        throw new InvalidOperationException("Graph changed during target approval.");
                    graph.RequireDeliveryAuthorization(original.TicketId);
                    await RequireHead(transaction.WorktreePath, transaction.PreparedCommit!, token);
                    return approved;
                }, policy: policy);
            var contract = new ManagedValidationContract(Guid.NewGuid().ToString("N"), original.TicketId, original.RunId,
                binding.Digest, transaction.WorktreePath, fingerprint, original.Contract.Executable,
                original.Contract.Arguments.ToArray(), original.Contract.Timeout, environment, policy.Digest);
            var receipt = await runner.RunAsync(contract, ct);
            var result = new GraphIntegratedValidation(original, manifest, contract, receipt);
            lock (live) integratedLive.Add(result, runner);
            return result;
        }
        finally { serial.Release(); }
    }

    public bool VerifyIntegratedCurrent(GraphIntegratedValidation result)
    {
        try
        {
            ManagedValidation runner;
            lock (live)
                if (!integratedLive.TryGetValue(result, out runner!) || !integrated.ContainsKey(result.Original)) return false;
            var binding = Bind(result.Original.TicketId, result.Original.ManifestPath);
            graph.RequireDeliveryAuthorization(result.Original.TicketId);
            return binding.RunId == result.Contract.RunId && binding.Digest == result.Contract.AcceptanceDigest &&
                Same(transactions[result.Original].WorktreePath, result.Contract.Workspace) &&
                result.Receipt.Status == ManagedValidationStatus.Passed &&
                result.Receipt.ContractHash == Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(result.Contract))) &&
                FileHash(result.Manifest.PatchPath) == result.Manifest.PatchHash &&
                JsonSerializer.Serialize(JsonSerializer.Deserialize<ManagedWorkspaceManifest>(File.ReadAllText(result.Original.ManifestPath))) == JsonSerializer.Serialize(result.Manifest) &&
                ReadHead(result.Contract.Workspace) == transactions[result.Original].PreparedCommit && runner.VerifyCurrent(result.Receipt);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or JsonException)
        { return false; }
    }

    private static string ReadHead(string repository)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = repository,
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var key in psi.Environment.Keys.Where(k => k.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray()) psi.Environment.Remove(key);
        psi.ArgumentList.Add("rev-parse"); psi.ArgumentList.Add("HEAD");
        using var process = System.Diagnostics.Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException("Cannot read target HEAD.");
        return stdout.Trim();
    }
    private static Task RequireHead(string repository, string expected, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (ReadHead(repository) != expected) throw new InvalidOperationException("Target HEAD changed.");
        return Task.CompletedTask;
    }

    private async Task VerifyPatchTree(ManagedWorkspaceManifest m, CancellationToken ct)
    {
        var directory = Path.Combine(layout.Root, "auto-dev", "validation-indexes");
        RejectLinks(directory);
        Directory.CreateDirectory(directory);
        var index = Path.Combine(directory, Guid.NewGuid().ToString("N"));
        async Task<byte[]> Git(params string[] arguments)
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = m.WorktreePath,
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var key in psi.Environment.Keys.Where(k => k.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray())
                psi.Environment.Remove(key);
            psi.Environment["GIT_LITERAL_PATHSPECS"] = "1";
            psi.Environment["GIT_INDEX_FILE"] = index;
            psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
            foreach (var a in arguments) psi.ArgumentList.Add(a);
            using var process = System.Diagnostics.Process.Start(psi)!;
            using var output = new MemoryStream();
            var copy = process.StandardOutput.BaseStream.CopyToAsync(output);
            var error = process.StandardError.ReadToEndAsync();
            // Reap the entire Git child tree on Stop before deleting its private index.
            try { await process.WaitForExitAsync(ct); }
            catch
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
                await Task.WhenAll(copy, error);
                throw;
            }
            await copy;
            var message = await error;
            ct.ThrowIfCancellationRequested();
            if (process.ExitCode != 0) throw new InvalidOperationException(message);
            return output.ToArray();
        }
        try
        {
            await Git("read-tree", m.BaseHead);
            await Git("add", "-A", "--", ".");
            var patch = await Git("diff", "--cached", "--binary", "--full-index", "--src-prefix=a/", "--dst-prefix=b/", "--no-ext-diff", "--no-textconv", m.BaseHead, "--");
            if (Convert.ToHexString(SHA256.HashData(patch)) != m.PatchHash)
                throw new InvalidOperationException("Retained patch does not match the current workspace candidate.");
        }
        finally { if (File.Exists(index)) File.Delete(index); if (File.Exists(index + ".lock")) File.Delete(index + ".lock"); }
    }

    /// <summary>Only a live, integrated receipt can support internal completion. Criterion coverage is an explicit host contract, not inferred from exit zero.</summary>
    public void CompleteInternalWork(GraphIntegratedValidation result, string[] coveredCriteria,
        string continuation, string continuationReason)
        => Task.Run(() => CompleteInternalWorkAsync(result, coveredCriteria, continuation, continuationReason)).GetAwaiter().GetResult();

    public async Task CompleteInternalWorkAsync(GraphIntegratedValidation result, string[] coveredCriteria,
        string continuation, string continuationReason, CancellationToken ct = default)
    {
        await serial.WaitAsync(ct);
        try
        {
            if (!VerifyIntegratedCurrent(result)) throw new InvalidOperationException("A current integrated host verification is required.");
            graph.RequireDeliveryAuthorization(result.Original.TicketId);
            // Validate every deterministic completion input BEFORE changing Git. A rejected request
            // must not advance the integrated ref or mint a durable integration-version entry.
            var ticket = graph.ReadSnapshot().Tickets.Single(t => t.Id == result.Original.TicketId);
            if (continuation is not ("active_goal" or "successor" or "no_followup") || string.IsNullOrWhiteSpace(continuationReason))
                throw new ArgumentException("An explicit continuation and reason are required.");
            var coverage = coveredCriteria?.Distinct(StringComparer.Ordinal).ToArray() ?? [];
            if (coverage.Length == 0 || coverage.Any(c => !ticket.Acceptance.Contains(c, StringComparer.Ordinal)) ||
                ticket.Acceptance.Any(c => !coverage.Contains(c, StringComparer.Ordinal)))
                throw new ArgumentException("Completion requires explicit coverage of every current acceptance criterion.");
            var output = result.Receipt.StandardOutput?.Path ?? throw new InvalidOperationException("A durable verification output is required.");
            var transaction = transactions[result.Original];
            var state = LoadState(transaction.JournalPath);
            if (state.AcceptanceDigest != result.Contract.AcceptanceDigest || state.CandidateCommit != result.Manifest.CandidateCommit ||
                state.PreparedCommit != transaction.PreparedCommit)
                throw new InvalidOperationException("Host integration binding changed.");
            graph.RequireDeliveryAuthorization(result.Original.TicketId);
            transaction = await ManagedGitIntegration.CommitAsync(transaction, transaction.PreparedCommit!, true, ct);
            transactions[result.Original] = transaction;
            await SaveState(state with { Status = "GitIntegrated" });
            graph.RequireDeliveryAuthorization(result.Original.TicketId);
            if (!HasDatabaseVersion(state, transaction))
                graph.RecordIntegratedVersion(result.Original.TicketId, result.Original.RunId,
                    result.Manifest.BaseHead, result.Manifest.CandidateCommit!, transaction.PreparedCommit!, transaction.JournalPath,
                    result.Receipt.ContractHash);
            // A crash between Git and DB is deliberately recoverable but not falsely atomic.
            await SaveState(state with { Status = "DatabaseRecorded" });
            ct.ThrowIfCancellationRequested();
            if (ticket.Status != "Done")
                graph.CompleteHostVerifiedWork(result.Original.TicketId, result.Original.RunId,
                    result.Contract.AcceptanceDigest, output, result.Receipt.Producer, coverage,
                    () => { graph.RequireDeliveryAuthorization(result.Original.TicketId); return VerifyIntegratedCurrent(result); }, true,
                    continuation, continuationReason);
            await SaveState(state with { Status = "Completed" });
        }
        finally { serial.Release(); }
    }

    // This file is an index into retained Git evidence. No field conveys validation/publication authority.
    private sealed record HostIntegrationState(string TicketId, string RunId, string ManifestPath,
        string AcceptanceDigest, string JournalPath, string CandidateCommit, string? PreparedCommit, string Status,
        string? RepairTicketId = null);
    private string CheckedJournal(string journalPath)
    {
        var path = Path.GetFullPath(journalPath);
        var root = Path.GetFullPath(Path.Combine(layout.Root, "auto-dev", "integration"));
        var relative = Path.GetRelativePath(root, path);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar) ||
            Path.GetFileName(path) != "journal.json") throw new ArgumentException("Journal outside this application's integration sandbox.");
        RejectLinks(path);
        return path;
    }
    private string StatePath(string journalPath) => Path.Combine(Path.GetDirectoryName(CheckedJournal(journalPath))!, "host-state.json");
    private async Task SaveState(HostIntegrationState state)
    {
        var path = StatePath(state.JournalPath);
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(state));
        File.Move(temporary, path, true);
    }
    private HostIntegrationState LoadState(string journalPath)
    {
        var path = StatePath(journalPath);
        RejectLinks(path);
        var state = JsonSerializer.Deserialize<HostIntegrationState>(File.ReadAllText(path))
            ?? throw new InvalidOperationException("Empty Host integration state.");
        if (!Same(state.JournalPath, CheckedJournal(journalPath))) throw new InvalidOperationException("Host journal identity changed.");
        return state;
    }
    private bool HasDatabaseVersion(HostIntegrationState state, ManagedIntegrationTransaction transaction)
        => graph.ReadSnapshot().Entries.Where(e => e.TicketId == state.TicketId && e.Kind == "integration-version" && e.Actor == "host")
            .Any(e =>
            {
                using var data = JsonDocument.Parse(e.Text);
                var value = data.RootElement;
                return value.GetProperty("runId").GetString() == state.RunId &&
                    value.GetProperty("candidateCommit").GetString() == transaction.CandidateCommit &&
                    value.GetProperty("integrationCommit").GetString() == transaction.PreparedCommit &&
                    Same(value.GetProperty("journalPath").GetString()!, transaction.JournalPath);
            });

    /// <summary>Reattach retained Git state only to a NEW live candidate receipt. Historical JSON cannot complete work.</summary>
    public async Task<GraphCandidateIntegration> ResumeIntegrationAsync(GraphCandidateValidation original,
        string journalPath, CancellationToken ct = default)
    {
        await serial.WaitAsync(ct);
        try
        {
            if (!VerifyCurrent(original)) throw new InvalidOperationException("A new live candidate verification is required after restart.");
            graph.RequireDeliveryAuthorization(original.TicketId);
            var state = LoadState(journalPath);
            var tx = await ManagedGitIntegration.RecoverAsync(CheckedJournal(journalPath), ct);
            if (state.TicketId != original.TicketId || state.RunId != original.RunId || !Same(state.ManifestPath, original.ManifestPath) ||
                state.AcceptanceDigest != original.Contract.AcceptanceDigest || tx.CandidateCommit != original.Manifest.CandidateCommit ||
                !Same(tx.RepositoryPath, original.Manifest.RepositoryPath) || tx.PreparedCommit is null ||
                tx.Status is not (ManagedIntegrationStatus.Prepared or ManagedIntegrationStatus.Integrated or ManagedIntegrationStatus.Published))
                throw new InvalidOperationException("Retained transaction needs repair or belongs to a different current candidate.");
            await RequireHead(tx.WorktreePath, tx.PreparedCommit, ct);
            await RequireCleanTree(tx.WorktreePath, ct);
            transactions[original] = tx;
            integrated[original] = original.Manifest;
            integratedFingerprints[original] = ManagedValidation.Fingerprint(tx.WorktreePath, policy);
            await SaveState(state with { PreparedCommit = tx.PreparedCommit });
            return new(original.Manifest, true) { Transaction = tx };
        }
        finally { serial.Release(); }
    }

    /// <summary>Physical Git/DB reconciliation only: never sets Done and never restores a verification capability.</summary>
    public async Task<GraphIntegrationRecovery> RecoverIntegrationAsync(string journalPath, CancellationToken ct = default)
    {
        await serial.WaitAsync(ct);
        try
        {
            var state = LoadState(journalPath);
            var binding = Bind(state.TicketId, state.ManifestPath);
            if (binding.RunId != state.RunId || binding.Digest != state.AcceptanceDigest)
                throw new InvalidOperationException("Recovery belongs to an obsolete Graph run/acceptance contract.");
            var workspace = await ManagedDevelopmentWorkspace.LoadAsync(state.ManifestPath, ct);
            var checkedJournal = CheckedJournal(journalPath);
            var directory = Path.GetDirectoryName(checkedJournal)!;
            ManagedIntegrationTransaction tx;
            try
            {
                tx = File.Exists(checkedJournal)
                    ? await ManagedGitIntegration.RecoverAsync(checkedJournal, ct)
                    : Interrupted("Host intent exists without a durable Git journal; retained for explicit reconciliation, not blind replay.");
            }
            catch (Exception e) when (e is IOException or InvalidOperationException)
            {
                // Missing/unregistered trees are retained diagnostics, not proof that nothing ran.
                tx = Interrupted("Physical Git reconciliation failed: " + e.Message);
            }
            ManagedIntegrationTransaction Interrupted(string error) => new(Path.GetFileName(directory).Replace("integration-", ""), binding.ProjectPath,
                Path.Combine(directory, "tree"), checkedJournal, state.CandidateCommit, workspace.Manifest.BaseHead,
                null, state.PreparedCommit, ManagedIntegrationStatus.Interrupted, error);
            if (tx.CandidateCommit != state.CandidateCommit || tx.CandidateCommit != workspace.Manifest.CandidateCommit ||
                !Same(tx.RepositoryPath, binding.ProjectPath)) throw new InvalidOperationException("Recovery Git identity mismatch.");
            var recorded = HasDatabaseVersion(state, tx);
            var done = graph.ReadSnapshot().Tickets.Single(t => t.Id == state.TicketId).Status == "Done";
            var findings = new List<string> { "Candidate manifest/hash and repository identity verified.", "Git journal state: " + tx.Status,
                recorded ? "Matching durable database integration-version exists." : "No matching durable database integration-version; JSON cannot authorize database repair.",
                "Executor managed-process registry is not owned by this Host; process side effects remain unassessed." };
            var controls = true;
            try { graph.RequireDeliveryAuthorization(state.TicketId); }
            catch (UnauthorizedAccessException e) { controls = false; findings.Add(e.Message); }
            var headMatches = false;
            var clean = false;
            if (Directory.Exists(tx.WorktreePath))
            {
                try
                {
                    headMatches = tx.PreparedCommit is not null && ReadHead(tx.WorktreePath) == tx.PreparedCommit;
                    await RequireCleanTree(tx.WorktreePath, ct); clean = true;
                }
                catch (Exception e) when (e is IOException or InvalidOperationException) { findings.Add(e.Message); }
            }
            if (tx.Error is not null) findings.Add(tx.Error);
            if (!headMatches) findings.Add("Prepared integration commit does not match the retained worktree HEAD or is not yet available.");
            var actualIntegrated = await ManagedGitIntegration.GetIntegratedCommitAsync(tx.RepositoryPath, ct);
            var refContains = tx.PreparedCommit is not null && actualIntegrated is not null &&
                (await ReadGitAsync(tx.RepositoryPath, ["merge-base", "--is-ancestor", tx.PreparedCommit, actualIntegrated], ct)).Code == 0;
            if (recorded && !refContains) findings.Add("Database integration version is not reachable from the actual managed integrated ref; explicit reconciliation required.");
            var disposition = !controls ? "CurrentControlBlocksDelivery" : recorded && !refContains ? "GitDatabaseMismatchRequiresDecision" :
                !headMatches || !clean || tx.Status is ManagedIntegrationStatus.Conflict or ManagedIntegrationStatus.Interrupted
                ? "RetainedRepairRequired" : recorded && done ? "ReconciledDelivery" : "FreshIntegrationValidationRequired";
            // A clean tree alone is NOT proof of no side effects or safe execution retry.
            return new(state.TicketId, state.RunId, tx, recorded, done, !done || !recorded || !headMatches || !clean || !refContains)
            { Disposition = disposition, Findings = findings.ToArray(), ControlAllowsDelivery = controls,
                IntegrationHeadMatches = headMatches, IntegrationTreeClean = clean, IntegratedRefContainsVersion = refContains };
        }
        finally { serial.Release(); }
    }

    public async Task<GraphIntegrationRecovery[]> RecoverIntegrationsAsync(CancellationToken ct = default)
    {
        var root = Path.Combine(layout.Root, "auto-dev", "integration");
        RejectLinks(root);
        if (!Directory.Exists(root)) return [];
        var results = new List<GraphIntegrationRecovery>();
        foreach (var directory in Directory.EnumerateDirectories(root, "integration-*"))
        {
            ct.ThrowIfCancellationRequested();
            RejectLinks(directory);
            if (File.Exists(Path.Combine(directory, "host-state.json")))
                results.Add(await RecoverIntegrationAsync(Path.Combine(directory, "journal.json"), ct));
            else if (File.Exists(Path.Combine(directory, "journal.json")))
            {
                var orphan = await ManagedGitIntegration.LoadAsync(CheckedJournal(Path.Combine(directory, "journal.json")), ct);
                results.Add(new("", "", orphan, false, false, true)
                { Disposition = "UnboundGitJournalRequiresDecision", Findings = ["Retained Git transaction has no durable Host ticket/run binding; do not replay or infer validation authority."] });
            }
        }
        return results.ToArray();
    }

    public string GetIntegrationJournalPath(GraphIntegratedValidation result)
    {
        lock (live)
        {
            if (!integratedLive.ContainsKey(result) || !transactions.TryGetValue(result.Original, out var transaction))
                throw new InvalidOperationException("Only this Host's actual integration validation identifies a transaction.");
            return CheckedJournal(transaction.JournalPath);
        }
    }

    public async Task<string> ReadTargetHeadAsync(CancellationToken ct = default)
    {
        var snapshot = graph.ReadSnapshot();
        if (snapshot.IsHistorical) throw new InvalidOperationException("Current Graph required.");
        var result = await ReadGitAsync(snapshot.ProjectPath, ["rev-parse", "--verify", "HEAD^{commit}"], ct);
        if (result.Code != 0) throw new InvalidOperationException("Publication target HEAD is unavailable.");
        return result.Output.Trim();
    }

    public async Task<GraphPublicationReceipt> PublishIntegratedAsync(string journalPath, CancellationToken ct = default)
        => await PublishIntegratedAsync(journalPath, await ReadTargetHeadAsync(ct), ct);

    /// <summary>Creates ordinary executor work with a durable physical integration binding. The executor
    /// must consume GetIntegrationRepairBindingAsync, not infer a working directory from prompt text.</summary>
    public async Task<GraphIntegrationRepairBinding> CreateIntegrationRepairAsync(string journalPath,
        string diagnostics, CancellationToken ct = default)
    {
        await serial.WaitAsync(ct);
        try
        {
            var state = LoadState(journalPath);
            var binding = Bind(state.TicketId, state.ManifestPath);
            graph.RequireDeliveryAuthorization(state.TicketId);
            var tx = await ManagedGitIntegration.LoadAsync(CheckedJournal(journalPath), ct);
            if (binding.RunId != state.RunId || binding.Digest != state.AcceptanceDigest ||
                !Same(binding.ProjectPath, tx.RepositoryPath) || tx.CandidateCommit != state.CandidateCommit ||
                tx.Status is not (ManagedIntegrationStatus.Conflict or ManagedIntegrationStatus.Prepared or ManagedIntegrationStatus.Interrupted))
                throw new InvalidOperationException("Only a current retained integration attempt can produce repair work.");
            var snapshot = graph.ReadSnapshot();
            if (state.RepairTicketId is not null)
                return await ReadRepairBinding(state.RepairTicketId, ct);
            var original = snapshot.Tickets.Single(t => t.Id == state.TicketId);
            if (original.Status != "Review") throw new InvalidOperationException("Only a reviewing delivery can create integration repair work.");
            var repair = graph.CreateTicket("Repair integration: " + original.Title,
                "Repair the retained integration attempt without weakening original acceptance or tests. " + diagnostics,
                original.ModuleIds, original.Acceptance);
            var result = new GraphIntegrationRepairBinding(repair.Id, state.TicketId, state.RunId,
                tx.JournalPath, tx.WorktreePath, tx.CandidateCommit, tx.PreparedCommit);
            await SaveState(state with { RepairTicketId = repair.Id });
            graph.AddEntry(repair.Id, null, "integration-repair", JsonSerializer.Serialize(result), tx.JournalPath);
            return result;
        }
        finally { serial.Release(); }
    }

    public async Task<GraphIntegrationRepairBinding?> GetIntegrationRepairBindingAsync(string repairTicketId, CancellationToken ct = default)
    {
        await serial.WaitAsync(ct);
        try
        {
            if (!graph.ReadSnapshot().Entries.Any(e => e.TicketId == repairTicketId && e.Kind == "integration-repair")) return null;
            return await ReadRepairBinding(repairTicketId, ct);
        }
        finally { serial.Release(); }
    }
    private async Task<GraphIntegrationRepairBinding> ReadRepairBinding(string repairTicketId, CancellationToken ct)
    {
        var snapshot = graph.ReadSnapshot();
        if (snapshot.IsHistorical) throw new InvalidOperationException("Current Graph required.");
        var entry = snapshot.Entries.Last(e => e.TicketId == repairTicketId && e.Kind == "integration-repair");
        var value = JsonSerializer.Deserialize<GraphIntegrationRepairBinding>(entry.Text) ?? throw new InvalidOperationException("Invalid repair binding.");
        var state = LoadState(value.JournalPath);
        var binding = Bind(state.TicketId, state.ManifestPath);
        graph.RequireDeliveryAuthorization(state.TicketId);
        var tx = await ManagedGitIntegration.LoadAsync(CheckedJournal(value.JournalPath), ct);
        if (state.RepairTicketId != repairTicketId || value.RepairTicketId != repairTicketId || value.OriginalTicketId != state.TicketId || value.OriginalRunId != state.RunId ||
            state.RunId != binding.RunId || state.AcceptanceDigest != binding.Digest || !Same(tx.RepositoryPath, binding.ProjectPath) ||
            value.CandidateCommit != tx.CandidateCommit || !Same(value.WorktreePath, tx.WorktreePath) ||
            snapshot.Tickets.Single(t => t.Id == state.TicketId).Status != "Review" ||
            tx.Status is not (ManagedIntegrationStatus.Conflict or ManagedIntegrationStatus.Prepared or ManagedIntegrationStatus.Interrupted))
            throw new InvalidOperationException("Repair binding no longer matches the current retained Git attempt.");
        return value with { PreparedCommit = tx.PreparedCommit };
    }

    /// <summary>Publication has its own CURRENT human approval gate. Restarted Host instances default to denial.</summary>
    public async Task<GraphPublicationReceipt> PublishIntegratedAsync(string journalPath,
        string expectedTargetHead, CancellationToken ct = default)
    {
        await serial.WaitAsync(ct);
        try
        {
            var state = LoadState(journalPath);
            var binding = Bind(state.TicketId, state.ManifestPath);
            graph.RequireDeliveryAuthorization(state.TicketId);
            var tx = await ManagedGitIntegration.RecoverAsync(CheckedJournal(journalPath), ct);
            if (binding.RunId != state.RunId || binding.Digest != state.AcceptanceDigest || tx.PreparedCommit is null ||
                !Same(binding.ProjectPath, tx.RepositoryPath) || tx.CandidateCommit != state.CandidateCommit ||
                !HasDatabaseVersion(state, tx) || graph.ReadSnapshot().Tickets.Single(t => t.Id == state.TicketId).Status != "Done")
                throw new InvalidOperationException("Publication requires a matching durable integration version and completed ticket.");
            var currentIntegrated = await ManagedGitIntegration.GetIntegratedCommitAsync(tx.RepositoryPath, ct);
            if (currentIntegrated is null || (await ReadGitAsync(tx.RepositoryPath,
                ["merge-base", "--is-ancestor", tx.PreparedCommit, currentIntegrated], ct)).Code != 0)
                throw new InvalidOperationException("Durable database version no longer belongs to the actual integrated ref; reconcile first.");
            var permission = new GraphPublicationPermission(state.TicketId, state.RunId, tx.RepositoryPath, expectedTargetHead, tx.PreparedCommit);
            if (publicationGate is null || !await publicationGate(permission))
                throw new UnauthorizedAccessException("Separate current human publication approval required.");
            ct.ThrowIfCancellationRequested();
            graph.RequireDeliveryAuthorization(state.TicketId);
            if (Bind(state.TicketId, state.ManifestPath) != binding || !HasDatabaseVersion(state, tx) ||
                graph.ReadSnapshot().Tickets.Single(t => t.Id == state.TicketId).Status != "Done")
                throw new InvalidOperationException("Graph delivery changed during publication approval.");
            tx = await ManagedGitIntegration.PublishAsync(tx, true, expectedTargetHead, ct);
            graph.RequireDeliveryAuthorization(state.TicketId);
            var receipt = new GraphPublicationReceipt(state.TicketId, state.RunId, tx.PreparedCommit!, tx.JournalPath, tx.Status);
            var path = Path.Combine(Path.GetDirectoryName(tx.JournalPath)!, "publication.json");
            await File.WriteAllTextAsync(path + ".tmp", JsonSerializer.Serialize(receipt));
            File.Move(path + ".tmp", path, true);
            return receipt;
        }
        finally { serial.Release(); }
    }

    private static async Task RequireCleanTree(string tree, CancellationToken ct)
    {
        var result = await ReadGitAsync(tree, ["status", "--porcelain=v1", "-z", "--untracked-files=all"], ct);
        if (result.Code != 0 || result.Output.Length != 0) throw new InvalidOperationException("Retained integration tree is not clean.");
    }
    private static async Task<(int Code, string Output)> ReadGitAsync(string tree, string[] arguments, CancellationToken ct)
    {
        var start = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = tree,
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var key in start.Environment.Keys.Where(k => k.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
        foreach (var a in arguments) start.ArgumentList.Add(a);
        ct.ThrowIfCancellationRequested();
        using var process = System.Diagnostics.Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(ct); }
        catch { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(CancellationToken.None); await Task.WhenAll(output, error); throw; }
        var text = await output; await error;
        return (process.ExitCode, text);
    }

    private sealed record Binding(string Path, string ProjectPath, string RunId, string Digest);
    private Binding Bind(string ticketId, string manifestPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ticketId);
        if (!Path.IsPathFullyQualified(manifestPath)) throw new ArgumentException("Absolute manifest path required.");
        var path = Path.GetFullPath(manifestPath);
        var root = Path.Combine(layout.Root, "auto-dev", "workspaces");
        var relative = Path.GetRelativePath(root, path);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar) ||
            Path.GetFileName(path) != "manifest.json") throw new ArgumentException("Manifest is outside the application workspace sandbox.");
        RejectLinks(path);
        var snapshot = graph.ReadSnapshot();
        if (snapshot.IsHistorical) throw new InvalidOperationException("Current Graph required.");
        var ticket = snapshot.Tickets.Single(t => t.Id == ticketId);
        var matches = snapshot.Entries.Where(e => e.TicketId == ticketId && e.Kind == "run-result" && e.Actor == "executor")
            .Where(e => Matches(e, path)).ToArray();
        if (matches.Length != 1) throw new InvalidOperationException("Candidate must identify exactly one stored Graph run-result.");
        var entry = matches[0];
        using var data = JsonDocument.Parse(entry.Text);
        var runId = data.RootElement.GetProperty("id").GetString()!;
        var run = snapshot.Runs.Single(r => r.Id == runId && r.TicketId == ticketId);
        if (entry.Reference != runId || run.EndedUtc is null || run.Status != "Completed")
            throw new InvalidOperationException("A matched completed ticket/run is required.");
        // Exact kernel canonicalization: Clean(criteria), Web JSON string array, UTF8 SHA256 uppercase.
        var criteria = ticket.Acceptance.Select(v => v?.Trim() ?? "").Where(v => v.Length != 0).Distinct(StringComparer.Ordinal).ToArray();
        var digest = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(criteria, new JsonSerializerOptions(JsonSerializerDefaults.Web))));
        return new(path, Path.GetFullPath(snapshot.ProjectPath), runId, digest);
    }
    private static bool Matches(GraphEntry entry, string path)
    {
        using var document = JsonDocument.Parse(entry.Text);
        return document.RootElement.TryGetProperty("candidateManifestPath", out var p) && p.ValueKind == JsonValueKind.String &&
            Path.IsPathFullyQualified(p.GetString()!) && Same(p.GetString()!, path);
    }
    private static void CheckManifest(ManagedWorkspaceManifest m, string project)
    {
        if (!Same(m.RepositoryPath, project) || m.Status != ManagedWorkspaceStatus.CandidateReady || string.IsNullOrEmpty(m.PatchHash))
            throw new InvalidOperationException("Candidate repository/status mismatch.");
    }
    private static string FileHash(string path) { RejectLinks(path); using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)); }
    private static bool Same(string a, string b) => string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar),
        Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static void RejectLinks(string path)
    {
        for (var current = new DirectoryInfo(Path.GetFullPath(path)); current is not null; current = current.Parent)
            if ((Directory.Exists(current.FullName) || File.Exists(current.FullName)) && (File.GetAttributes(current.FullName) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Links/reparse points are not supported.");
    }
}
