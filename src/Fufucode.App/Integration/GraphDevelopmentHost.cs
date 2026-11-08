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
public sealed record GraphCandidateIntegration(ManagedWorkspaceManifest Manifest, bool RequiresPostIntegrationValidation);

/// <summary>Application-owned approval boundary. Historical receipt JSON is display data only.
/// The composition root must exclude other repository/workspace writers while using this service.</summary>
public sealed class GraphDevelopmentHost
{
    private readonly AppDataLayout layout;
    private readonly GraphProjectService graph;
    private readonly Func<ManagedValidationPermission, Task<bool>>? permissionGate;
    private readonly Func<GraphIntegrationPermission, Task<bool>>? integrationGate;
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
        ManagedValidationPolicy? hostPolicy = null)
    {
        this.layout = layout ?? throw new ArgumentNullException(nameof(layout));
        this.graph = graph ?? throw new ArgumentNullException(nameof(graph));
        this.permissionGate = permissionGate;
        this.integrationGate = integrationGate;
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
            var m = result.Manifest;
            var request = new GraphIntegrationPermission(result.TicketId, result.RunId, m.RepositoryPath, m.BaseHead, m.PatchHash);
            if (integrationGate is null || !await integrationGate(request)) throw new UnauthorizedAccessException("Separate integration approval required.");
            ct.ThrowIfCancellationRequested();
            if (!VerifyCurrent(result)) throw new InvalidOperationException("Candidate changed during integration approval.");
            var workspace = await ManagedDevelopmentWorkspace.LoadAsync(result.ManifestPath, ct);
            if (workspace.Manifest != m && JsonSerializer.Serialize(workspace.Manifest) != JsonSerializer.Serialize(m))
                throw new InvalidOperationException("Manifest changed after validation.");
            var transaction = await ManagedGitIntegration.PrepareAsync(workspace,
                Path.Combine(layout.Root, "auto-dev", "integration"), ct);
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
            return new(integrated, true);
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
            var environment = "graph-target/" + Environment.ProcessId;
            var runner = new ManagedValidation(Path.Combine(layout.Root, "auto-dev", "validation", Guid.NewGuid().ToString("N")),
                environment, new(original.TicketId, original.RunId, binding.Digest),
                permissionGate is null ? null : async (permission, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    var approved = await permissionGate(permission);
                    token.ThrowIfCancellationRequested();
                    if (Bind(original.TicketId, original.ManifestPath) != binding)
                        throw new InvalidOperationException("Graph changed during target approval.");
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
            // Always reap before deleting the private index, even when cancellation was requested.
            await process.WaitForExitAsync(CancellationToken.None);
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
    {
        if (!VerifyIntegratedCurrent(result)) throw new InvalidOperationException("A current integrated host verification is required.");
        graph.RequireDeliveryAuthorization(result.Original.TicketId);
        var transaction = transactions[result.Original];
        transactions[result.Original] = ManagedGitIntegration.CommitAsync(transaction, transaction.PreparedCommit!, true).GetAwaiter().GetResult();
        graph.RecordIntegratedVersion(result.Original.TicketId, result.Original.RunId,
            result.Manifest.BaseHead, result.Manifest.CandidateCommit!, transaction.PreparedCommit!, transaction.JournalPath,
            result.Receipt.ContractHash);
        graph.CompleteHostVerifiedWork(result.Original.TicketId, result.Original.RunId,
            result.Contract.AcceptanceDigest, result.Receipt.StandardOutput?.Path
                ?? throw new InvalidOperationException("A durable verification output is required."),
            result.Receipt.Producer, coveredCriteria, () => VerifyIntegratedCurrent(result), true,
            continuation, continuationReason);
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
