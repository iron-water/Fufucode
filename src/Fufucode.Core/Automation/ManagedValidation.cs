using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Fufucode.Core.Automation;

public sealed record ManagedValidationContract(string Id, string TicketId, string RunId,
    string AcceptanceDigest, string Workspace, string CandidateHash, string Executable,
    string[] Arguments, TimeSpan Timeout, string EnvironmentIdentity, string? ValidationPolicyDigest = null);

/// <summary>
/// Immutable, host-owned allowance for exact workspace-relative generated paths (no globs).
/// Nothing is excluded by default, including .git, bin and obj. Protected files override allowances.
/// Never construct this from model-provided exclusions; choose it when the host creates a runner.
/// </summary>
public sealed class ManagedValidationPolicy
{
    public static ManagedValidationPolicy Strict { get; } = new();
    public IReadOnlyList<string> GeneratedDirectories { get; }
    public IReadOnlyList<string> GeneratedFiles { get; }
    public IReadOnlyList<string> ProtectedFiles { get; }
    public string Digest { get; }

    public ManagedValidationPolicy(IEnumerable<string>? generatedDirectories = null,
        IEnumerable<string>? generatedFiles = null, IEnumerable<string>? protectedFiles = null)
    {
        GeneratedDirectories = Copy(generatedDirectories);
        GeneratedFiles = Copy(generatedFiles);
        ProtectedFiles = Copy(protectedFiles);
        Digest = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            SchemaVersion = 1, GeneratedDirectories, GeneratedFiles, ProtectedFiles
        })));
    }

    private static IReadOnlyList<string> Copy(IEnumerable<string>? values) => Array.AsReadOnly(
        (values ?? []).Select(Normalize).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());

    private static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Generated/protected paths cannot be empty.");
        var normalized = path.Replace('\\', '/');
        if (Path.IsPathRooted(normalized) || normalized.IndexOfAny([':', '*', '?', '\0', '<', '>', '|', '\"']) >= 0 ||
            normalized.Split('/').Any(part => part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ')))
            throw new ArgumentException("Policy paths must be exact normalized workspace-relative paths, without wildcards or traversal.");
        return OperatingSystem.IsWindows() ? normalized.ToUpperInvariant() : normalized;
    }

    internal bool Excludes(string relative, bool directory)
    {
        var path = Normalize(relative);
        if (ProtectedFiles.Contains(path, StringComparer.Ordinal)) return false;
        return GeneratedDirectories.Any(allowed => (directory && path == allowed) || path.StartsWith(allowed + "/", StringComparison.Ordinal)) ||
            (!directory && GeneratedFiles.Contains(path, StringComparer.Ordinal));
    }
}

public sealed record ManagedValidationContext(string TicketId, string RunId, string AcceptanceDigest);
public sealed record ManagedValidationPermission(string Executable, IReadOnlyList<string> Arguments,
    string WorkingDirectory, string ContractHash, ManagedValidationPolicy Policy);
public enum ManagedValidationStatus { Passed, Failed, Denied, Stale, Cancelled, TimedOut, IntegrityFailure }
public sealed record ManagedValidationOutput(string Path, string Sha256, long StoredBytes, long TotalBytes, bool Truncated);
public sealed record ManagedValidationReceipt(int SchemaVersion, string ContractHash, string ContractId,
    string TicketId, string RunId, string Producer, ManagedValidationStatus Status, int? ExitCode,
    DateTimeOffset StartedUtc, DateTimeOffset FinishedUtc, string WorkspaceBefore, string WorkspaceAfter,
    ManagedValidationOutput? StandardOutput, ManagedValidationOutput? StandardError, string? Error, string ValidationPolicyDigest);

/// <summary>
/// Host-owned runner, not a sandbox or an automatic approver. The host must exclude concurrent writers
/// and provide an async permission workflow. Run only trusted executables: approved processes retain OS
/// privileges. Receipts are trusted only through this live instance, never by loading historical JSON.
/// CandidateHash is Fingerprint(workspace, Policy), not a Git patch hash. Artifacts must be outside that tree.
/// Generated-path allowances are fixed by the host at construction, never accepted as execution parameters.
/// The host must protect acceptance/source inputs that live inside an explicitly allowed generated directory.
/// </summary>
public sealed class ManagedValidation
{
    private readonly string producer = "managed-validation/" + Guid.NewGuid().ToString("N");
    private readonly Dictionary<string, (ManagedValidationReceipt Receipt, string Workspace)> issued = new();
    private readonly SemaphoreSlim serial = new(1, 1);
    private readonly Func<ManagedValidationPermission, CancellationToken, Task<bool>>? permissionGate;
    private readonly string environmentIdentity;
    private readonly ManagedValidationContext context;
    private readonly string artifacts;
    private readonly int outputLimit;
    public ManagedValidationPolicy Policy { get; }

    public ManagedValidation(string artifactDirectory, string environmentIdentity, ManagedValidationContext context,
        Func<ManagedValidationPermission, CancellationToken, Task<bool>>? permissionGate, int outputLimitBytes = 1024 * 1024,
        ManagedValidationPolicy? policy = null)
    {
        artifacts = Path.GetFullPath(artifactDirectory);
        this.environmentIdentity = environmentIdentity;
        this.context = context ?? throw new ArgumentNullException(nameof(context));
        this.permissionGate = permissionGate;
        Policy = policy ?? ManagedValidationPolicy.Strict;
        outputLimit = outputLimitBytes > 0 ? outputLimitBytes : throw new ArgumentOutOfRangeException(nameof(outputLimitBytes));
    }

    public static string Fingerprint(string workspace, ManagedValidationPolicy? policy = null)
    {
        policy ??= ManagedValidationPolicy.Strict;
        var root = Path.GetFullPath(workspace);
        RejectLinks(root);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(policy.Digest));
        void Visit(string directory)
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
            {
                RejectLinks(path);
                var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                var dir = Directory.Exists(path);
                var excluded = policy.Excludes(relative, dir);
                if (!excluded)
                    hash.AppendData(Encoding.UTF8.GetBytes((dir ? "D" : "F") + relative.Length + ":" + relative + "\0"));
                // Still traverse allowed directories: protected files and reparse points must not disappear.
                if (dir) Visit(path);
                else if (!excluded)
                {
                    using var stream = File.OpenRead(path);
                    hash.AppendData(SHA256.HashData(stream));
                }
            }
        }
        Visit(root);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public async Task<ManagedValidationReceipt> RunAsync(ManagedValidationContract contract, CancellationToken token = default)
    {
        // Copy mutable arguments before any await, so the permission request and process share one snapshot.
        var c = contract with { Workspace = Path.GetFullPath(contract.Workspace),
            Executable = Path.GetFullPath(contract.Executable), Arguments = contract.Arguments.ToArray(),
            ValidationPolicyDigest = contract.ValidationPolicyDigest ?? ManagedValidationPolicy.Strict.Digest };
        if (!Path.IsPathFullyQualified(contract.Executable) || c.Timeout <= TimeSpan.Zero || c.Timeout > TimeSpan.FromDays(1) ||
            new[] { c.Id, c.TicketId, c.RunId, c.AcceptanceDigest, c.CandidateHash, c.EnvironmentIdentity }.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Complete contract, absolute executable and bounded positive timeout required.");
        if (Within(artifacts, c.Workspace) || Within(c.Workspace, artifacts))
            throw new ArgumentException("Artifacts and workspace must be disjoint.");
        await serial.WaitAsync(token);
        var started = DateTimeOffset.UtcNow;
        var digest = Hash(JsonSerializer.SerializeToUtf8Bytes(c));
        var before = "";
        var after = "";
        ManagedValidationOutput? stdout = null, stderr = null;
        int? exit = null;
        var status = ManagedValidationStatus.Failed;
        string? error = null;
        try
        {
            before = Fingerprint(c.Workspace, Policy);
            if (c.ValidationPolicyDigest != Policy.Digest || before != c.CandidateHash || c.EnvironmentIdentity != environmentIdentity ||
                c.TicketId != context.TicketId || c.RunId != context.RunId || c.AcceptanceDigest != context.AcceptanceDigest)
                status = ManagedValidationStatus.Stale;
            else if (permissionGate is null || !await permissionGate(new(c.Executable,
                Array.AsReadOnly(c.Arguments), c.Workspace, digest, Policy), token))
                status = ManagedValidationStatus.Denied;
            else
            {
                token.ThrowIfCancellationRequested();
                if (Fingerprint(c.Workspace, Policy) != before) status = ManagedValidationStatus.Stale;
                else
                {
                    RejectLinks(artifacts);
                    Directory.CreateDirectory(artifacts);
                    var run = Path.Combine(artifacts, Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(run);
                    var psi = new ProcessStartInfo(c.Executable) { WorkingDirectory = c.Workspace,
                        UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                    foreach (var argument in c.Arguments) psi.ArgumentList.Add(argument);
                    using var process = new Process { StartInfo = psi };
                    process.Start();
                    var outTask = Capture(process.StandardOutput.BaseStream, Path.Combine(run, "stdout.bin"));
                    var errTask = Capture(process.StandardError.BaseStream, Path.Combine(run, "stderr.bin"));
                    using var timeout = new CancellationTokenSource(c.Timeout);
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
                    try
                    {
                        await process.WaitForExitAsync(linked.Token);
                        status = process.ExitCode == 0 ? ManagedValidationStatus.Passed : ManagedValidationStatus.Failed;
                    }
                    catch (OperationCanceledException)
                    {
                        status = token.IsCancellationRequested ? ManagedValidationStatus.Cancelled : ManagedValidationStatus.TimedOut;
                        if (!process.HasExited) process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync(CancellationToken.None);
                    }
                    exit = process.ExitCode;
                    stdout = await outTask;
                    stderr = await errTask;
                }
            }
        }
        catch (OperationCanceledException) { status = ManagedValidationStatus.Cancelled; }
        catch (Exception ex) { status = ManagedValidationStatus.Failed; error = ex.Message; }
        try
        {
            after = Fingerprint(c.Workspace, Policy);
            if (before != after && status != ManagedValidationStatus.Stale) status = ManagedValidationStatus.IntegrityFailure;
            if (!OutputMatches(stdout) || !OutputMatches(stderr)) status = ManagedValidationStatus.IntegrityFailure;
        }
        catch (Exception ex) { status = ManagedValidationStatus.IntegrityFailure; error = ex.Message; }
        var receipt = new ManagedValidationReceipt(2, digest, c.Id, c.TicketId, c.RunId, producer,
            status, exit, started, DateTimeOffset.UtcNow, before, after, stdout, stderr, error, Policy.Digest);
        lock (issued) issued[digest] = (receipt, c.Workspace);
        serial.Release();
        return receipt;
    }

    /// <summary>Checks live provenance, unchanged candidate and output bytes; deserialized copies are not authority.
    /// This does not mean success: consumers must also require Status == Passed and the expected contract hash.
    /// </summary>
    public bool VerifyCurrent(ManagedValidationReceipt receipt)
    {
        try
        {
            lock (issued)
                return issued.TryGetValue(receipt.ContractHash, out var actual) && ReferenceEquals(actual.Receipt, receipt) &&
                    receipt.Producer == producer && receipt.WorkspaceBefore == receipt.WorkspaceAfter &&
                    Fingerprint(actual.Workspace, Policy) == receipt.WorkspaceAfter &&
                    OutputMatches(receipt.StandardOutput) && OutputMatches(receipt.StandardError);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private async Task<ManagedValidationOutput> Capture(Stream source, string path)
    {
        await using var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        var buffer = new byte[8192];
        long total = 0, stored = 0;
        int count;
        while ((count = await source.ReadAsync(buffer)) != 0)
        {
            total += count;
            var keep = (int)Math.Min(count, outputLimit - stored);
            if (keep > 0) { await target.WriteAsync(buffer.AsMemory(0, keep)); stored += keep; }
        }
        await target.FlushAsync();
        await target.DisposeAsync();
        return new(path, FileHash(path), stored, total, total > stored);
    }
    private static bool OutputMatches(ManagedValidationOutput? output) => output is null ||
        (File.Exists(output.Path) && new FileInfo(output.Path).Length == output.StoredBytes && FileHash(output.Path) == output.Sha256);
    private static string FileHash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static bool Within(string child, string parent) => Path.GetRelativePath(parent, child) is var r &&
        (r == "." || (!Path.IsPathRooted(r) && r != ".." && !r.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)));
    private static void RejectLinks(string path)
    {
        for (var current = new DirectoryInfo(Path.GetFullPath(path)); current is not null; current = current.Parent)
            if ((current.Exists || File.Exists(current.FullName)) && (File.GetAttributes(current.FullName) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Symbolic links/reparse points are not supported: " + path);
    }
}
