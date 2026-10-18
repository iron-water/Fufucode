using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Fufucode.Core.Automation.Harnesses;

/// <summary>
/// One bounded invocation, streamed events, durable dispatch guard. Can be hosted by
/// a CLI or the application service. Does not claim to be a detached supervisor.
/// </summary>
/// <param name="hostId">Identity of the process hosting this invocation; recorded as owner evidence.</param>
/// <param name="git">Repository observer used for launch-time HEAD. Optional: probes only where callers ask.</param>
public sealed class HarnessExecutor(AutomationStore project, string evidenceDirectory,
    string hostId = "cli", IGitEvidence? git = null)
{
    public HarnessRunStore Runs { get; } = new(project);
    public string HostId => hostId;

    public async Task<HarnessRun> ExecuteAsync(HarnessRuntime runtime, HarnessRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RunId);
        // The dispatched task identity is part of the journal, so it must exist at claim time: a run
        // can never be recorded without a task and then be delivered against some other task's card.
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TaskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Prompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtime.Version);
        var thread = project.GetThread(request.ThreadId);
        var task = project.GetTask(request.TaskId);
        if (task.ThreadId != thread.ThreadId ||
            thread.Harness != runtime.Harness || thread.Model != request.Model)
            throw new InvalidOperationException("Thread/task/runtime identity mismatch or frozen thread.");
        if (thread.SavedSessionId is not null)
            throw new NotSupportedException("Existing external sessions require an explicit ownership import; never auto-adopt.");
        if (!Directory.Exists(thread.Worktree)) throw new DirectoryNotFoundException(thread.Worktree);
        var adapter = HarnessAdapters.Get(runtime.Harness);
        // Never use caller-controlled run IDs as paths.
        var directory = Path.Combine(Path.GetFullPath(evidenceDirectory), HarnessRunStore.Hash(request.RunId));
        cancellationToken.ThrowIfCancellationRequested();
        if (!Runs.Claim(request, thread, runtime, thread.Status == ThreadStatus.Active && project.IsTaskUnlocked(task.TaskId)))
            return Runs.Get(request.RunId);
        Directory.CreateDirectory(directory);
        var initial = Runs.Get(request.RunId);
        Process? process = null;
        Task? stderrTask = null;
        Task? inputTask = null;
        var launched = false;
        var session = initial.SessionId;
        var locator = initial.SessionLocator;
        var terminal = false;
        var failed = false;
        string? output = null;
        long sequence = 0;
        void Publish(string kind, object payload) => project.AppendEvent(
            $"harness:{request.RunId}:{++sequence}", $"harness.{kind}", request.ThreadId,
            request.TaskId, null, request.RunId, JsonSerializer.Serialize(payload), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        try
        {
            var start = adapter.Prepare(runtime, request, thread.Worktree, directory, locator);
            Publish("dispatching", new { runtime.Id, runtime.Harness, runtime.Version, request.Model });
            process = new Process { StartInfo = start };
            process.Start();
            launched = true;
            var observedHead = git is null ? null : await git.HeadAsync(thread.Worktree, cancellationToken)
                .ConfigureAwait(false);
            Runs.Save(request.RunId, HarnessRunStatus.Running);
            // Both identities are journaled before the first awaited read, so a supervisor killed
            // mid-run leaves a verifiable host plus a verifiable writer instead of an anonymous claim.
            var self = SystemProcessProbe.Shared;
            Runs.SaveSupervisor(request.RunId, hostId, self.CurrentPid, self.Self.StartUtc!.Value, observedHead);
            Runs.SaveWriter(request.RunId, process.Id,
                new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero));
            Publish("running", new { hostId, supervisorPid = self.CurrentPid, writerPid = process.Id,
                startedUtc = process.StartTime.ToUniversalTime() });
            // stderr may contain credentials/tool content; bound and keep local, never echo it in status.
            stderrTask = DrainErrorAsync(process.StandardError, Path.Combine(directory, "stderr.log"), cancellationToken);
            inputTask = SendAsync(process, request.Prompt, cancellationToken);
            using var log = new StreamWriter(Path.Combine(directory, "events.jsonl"), false, new UTF8Encoding(false));
            long bytes = 0;
            await foreach (var line in LinesAsync(process.StandardOutput, cancellationToken))
            {
                bytes += Encoding.UTF8.GetByteCount(line);
                if (bytes > 64 * 1024 * 1024) throw new InvalidDataException("Run event limit exceeded.");
                using var json = JsonDocument.Parse(line);
                var signal = adapter.Parse(json.RootElement);
                await log.WriteLineAsync(line);
                await log.FlushAsync(cancellationToken);
                if (signal.Kind == "session")
                {
                    if (string.IsNullOrWhiteSpace(signal.SessionId) || (session is not null && session != signal.SessionId))
                        throw new InvalidDataException("Harness returned an unexpected session identity.");
                    session = signal.SessionId;
                    if (runtime.Harness is "codex" or "dsh") locator = session;
                    Runs.Save(request.RunId, HarnessRunStatus.Running, session, locator);
                }
                if (signal.Kind == "message") output = signal.Text;
                if (signal.Success == false) failed = true;
                if (signal.Kind == "terminal") terminal = true;
                Publish(signal.Kind, new { signal, nativeType = json.RootElement.TryGetProperty("type", out var type) ? type.GetString() : null });
            }
            await inputTask;
            await process.WaitForExitAsync(cancellationToken);
            await stderrTask;
            // Provenance is read here, after this run's own writer exited and before the outcome is
            // journaled: it is what this run left behind, not what the worktree shows later. Without an
            // injected observer nothing is guessed, and the run simply cannot be certified as a delivery.
            var settleHead = git is null ? null
                : await git.HeadAsync(thread.Worktree, cancellationToken).ConfigureAwait(false);
            if (runtime.Harness is "pi" or "omp")
                (session, locator) = ResolveSession(directory, session, locator);
            var status = process.ExitCode == 0 && terminal && !failed && session is not null && output is not null
                ? HarnessRunStatus.Completed : HarnessRunStatus.Failed;
            // Missing identity after a launched process cannot safely permit a replacement session.
            if (session is null || locator is null || !terminal) status = HarnessRunStatus.Unknown;
            // Status and provenance land in one statement, so no crash can leave a Completed row whose
            // delivery binding is missing and strand a run that really finished.
            Runs.Save(request.RunId, status, session, locator, output,
                status == HarnessRunStatus.Completed ? null : $"Exit={process.ExitCode}; terminal={terminal}; failure={failed}. Inspect local evidence.",
                settleHead);
            Publish("settled", new { status, session, exitCode = process.ExitCode, settleHead });
        }
        catch (Exception error)
        {
            if (launched && process is not null && !process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                }
                catch { /* Unknown remains claimed even if termination cannot be confirmed. */ }
            }
            // Once dispatched, uncertain transport/identity cannot be retried automatically, and no
            // settle HEAD is attested: this host lost or killed the writer, so it cannot honestly say
            // what the repository held when the run ended. A later reading of the same worktree may
            // already contain another run's commit, which is exactly what must not be borrowed here.
            Runs.Save(request.RunId, launched ? HarnessRunStatus.Unknown : HarnessRunStatus.Failed,
                session, locator, error: error is OperationCanceledException ? "Cancelled; reconcile before resuming." : error.GetType().Name);
            Publish("fault", new { launched, error = error.GetType().Name });
        }
        finally
        {
            if (inputTask is not null) { try { await inputTask; } catch { /* Outcome already journaled. */ } }
            if (stderrTask is not null) { try { await stderrTask; } catch { /* Fault is already journaled. */ } }
            process?.Dispose();
        }
        return Runs.Get(request.RunId);
    }

    private static async Task SendAsync(Process process, string prompt, CancellationToken ct)
    {
        await process.StandardInput.WriteAsync(prompt.AsMemory(), ct);
        await process.StandardInput.FlushAsync(ct);
        process.StandardInput.Close();
    }

    private static async Task DrainErrorAsync(StreamReader reader, string path, CancellationToken ct)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
        var buffer = new char[4096];
        var remaining = 256 * 1024;
        int n;
        while ((n = await reader.ReadAsync(buffer.AsMemory(), ct)) != 0)
        {
            var write = Math.Min(n, remaining);
            if (write > 0) await writer.WriteAsync(buffer.AsMemory(0, write));
            remaining -= write;
        }
    }

    private static async IAsyncEnumerable<string> LinesAsync(StreamReader reader,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var line = new StringBuilder();
        var buffer = new char[4096];
        int n;
        while ((n = await reader.ReadAsync(buffer.AsMemory(), ct)) != 0)
            for (var i = 0; i < n; i++)
            {
                if (buffer[i] == '\n')
                {
                    var value = line.ToString().TrimEnd('\r');
                    line.Clear();
                    if (value.Length != 0) yield return value;
                }
                else
                {
                    line.Append(buffer[i]);
                    if (line.Length > 8 * 1024 * 1024) throw new InvalidDataException("Event line too large.");
                }
            }
        if (line.Length != 0) throw new InvalidDataException("Truncated event stream.");
    }

    private static (string? Id, string? Locator) ResolveSession(string directory, string? id, string? locator)
    {
        if (locator is not null && File.Exists(locator)) return (id, locator);
        var sessions = Path.Combine(directory, "sessions");
        if (!Directory.Exists(sessions)) return (id, null);
        var matches = new List<(string, string)>();
        foreach (var file in Directory.EnumerateFiles(sessions, "*.jsonl", SearchOption.AllDirectories))
        {
            using var reader = File.OpenText(file);
            // OMP prepends a mutable title record; session identity is the next header.
            for (var i = 0; i < 8; i++)
            {
                var line = reader.ReadLine();
                if (line is null) break;
                using var header = JsonDocument.Parse(line);
                if (!header.RootElement.TryGetProperty("type", out var type) || type.GetString() != "session" ||
                    !header.RootElement.TryGetProperty("id", out var field)) continue;
                var found = field.GetString();
                if (found is not null && (id is null || id == found)) matches.Add((found, Path.GetFullPath(file)));
                break;
            }
        }
        return matches.Count == 1 ? matches[0] : (id, null);
    }
}
