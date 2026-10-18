using System.Text.Json;
using Fufucode.Core.Automation.Harnesses;

namespace Fufucode.Core.Automation.Hosting;

/// <summary>Where an execution session is expected to leave its delivery card inside the worktree.</summary>
public sealed record ConvCapturePolicy(string CardRelativePath = "tmp/delivery.json", long? TimeoutMs = null);

/// <summary>
/// What the host observed for one run. <c>Outcome</c> is machine-readable: a delivered card, a
/// structured failure, or a deferral while a writer may still be alive. Text in a card is never a
/// terminal state by itself.
/// </summary>
public sealed record ConvDeliveryCapture(string RunId, string ThreadId, string TaskId,
    string Outcome, string Code, string Detail, string? PartId, long? RevisionNumber,
    string? ObservedHead, string? CardPath);

/// <summary>One observation pass, with the durable events it appended.</summary>
public sealed record ConvWatchdogReport(string RunId, string ThreadId,
    IReadOnlyList<ConvDeliveryCapture> Captures, IReadOnlyList<string> Events);

/// <summary>
/// CONV-1 host terminal-state capture. The host no longer depends on a model remembering to send a
/// message: when an execution session ends (or its process dies) the watchdog inspects the real
/// journal, the real process identity and the real repository, verifies <c>writerStopped</c> and the
/// run's HEAD, and writes exactly one durable <c>delivery.received</c> or <c>delivery.failed</c>
/// event. A missing, malformed or dishonest card is an explicit failure, never an endless silent
/// wait, and an abnormal exit is reported instead of being retried.
/// </summary>
public sealed class HostDeliveryWatchdog
{
    private readonly AutomationStore _store;
    private readonly HarnessRunStore _runs;
    private readonly DeliveryReceiver _receiver;
    private readonly RunReconciler _reconciler;
    private readonly IGitEvidence _git;
    private readonly ConvCapturePolicy _policy;

    public HostDeliveryWatchdog(AutomationStore store, HarnessRunStore runs, DeliveryReceiver receiver,
        IProcessProbe? probe = null, IGitEvidence? git = null, ConvCapturePolicy? policy = null,
        string hostId = "conv-watchdog")
    {
        _store = store;
        _runs = runs;
        _receiver = receiver;
        _git = git ?? GitCliEvidence.Shared;
        _policy = policy ?? new ConvCapturePolicy();
        _reconciler = new RunReconciler(store, runs, probe ?? SystemProcessProbe.Shared, _git, hostId);
    }

    public ConvCapturePolicy Policy => _policy;

    /// <summary>
    /// Observe one run's terminal state. A run whose writer may still be alive is deferred without
    /// touching the thread or the worktree; a terminal run yields exactly one received/failed event.
    /// </summary>
    public async Task<ConvWatchdogReport> ObserveAsync(string runId, long nowUtc,
        CancellationToken cancellationToken = default)
    {
        HarnessRun run;
        try { run = _runs.Get(runId); }
        catch (KeyNotFoundException)
        {
            return Fail(runId, "", "", "unknown-run",
                "The journal has no such run; nothing was dispatched.", nowUtc, cardPath: null);
        }
        var audit = await _reconciler.AuditAsync(runId, cancellationToken).ConfigureAwait(false);
        if (!audit.WriterStopped ||
            audit.Verdict is not (OwnerVerdict.ConfirmedFinished or OwnerVerdict.OwnedHere))
        {
            var deferred = new ConvDeliveryCapture(runId, run.ThreadId, run.TaskId ?? "", "delivery.deferred",
                "writer-active",
                $"Ownership is {audit.Verdict} ({audit.Reason}); the terminal state is not yet known.",
                null, null, audit.ObservedHead, null);
            return new(runId, run.ThreadId, [deferred], []);
        }
        if (run.Status is HarnessRunStatus.Dispatching or HarnessRunStatus.Running)
            return Fail(runId, run.ThreadId, run.TaskId ?? "", "abandoned-writer",
                $"The run is {run.Status} while its writer is gone ({audit.Reason}); the outcome is unknown.",
                nowUtc, cardPath: null);
        if (run.Status != HarnessRunStatus.Completed)
            return Fail(runId, run.ThreadId, run.TaskId ?? "", StatusCode(run.Status),
                $"The run is {run.Status}; only a Completed journal can carry a delivery.", nowUtc,
                cardPath: null);

        var thread = _store.GetThread(run.ThreadId);
        var cardPath = Path.Combine(thread.Worktree, _policy.CardRelativePath);
        string cardJson;
        try
        {
            if (!File.Exists(cardPath))
                return Fail(runId, run.ThreadId, run.TaskId ?? "", "card-missing",
                    $"No delivery card at '{cardPath}'.", nowUtc, cardPath);
            cardJson = await File.ReadAllTextAsync(cardPath, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException error)
        {
            return Fail(runId, run.ThreadId, run.TaskId ?? "", "card-unreadable", error.Message, nowUtc,
                cardPath);
        }
        JsonDocument document;
        try { document = JsonDocument.Parse(cardJson); }
        catch (JsonException)
        {
            return Fail(runId, run.ThreadId, run.TaskId ?? "", "card-invalid",
                "The delivery card is not valid JSON.", nowUtc, cardPath);
        }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Fail(runId, run.ThreadId, run.TaskId ?? "", "card-not-object",
                    "The delivery card must be one JSON object.", nowUtc, cardPath);
            var cardTask = Text(root, "taskId") ?? Text(root, "task");
            if (Flag(root, "writerStopped") != true)
                return Fail(runId, run.ThreadId, run.TaskId ?? "", "writer-not-stopped",
                    "The card does not state writerStopped=true.", nowUtc, cardPath);
            if (run.TaskId is null)
                return Fail(runId, run.ThreadId, "", "run-task-unknown",
                    "The run never journaled the task it was dispatched for.", nowUtc, cardPath);
            if (cardTask is not null && cardTask != run.TaskId)
                return Fail(runId, run.ThreadId, run.TaskId, "run-task-mismatch",
                    $"The card names task '{cardTask}' but the run was dispatched for '{run.TaskId}'.",
                    nowUtc, cardPath);
            var cardStatus = Text(root, "status");
            if (cardStatus is not ("module_ready" or "submitted"))
                return Fail(runId, run.ThreadId, run.TaskId, "card-status",
                    $"Card status '{cardStatus}' is not a deliverable status.", nowUtc, cardPath);
            var cardHead = Text(root, "head") ?? LastCommit(root) ?? run.SettleHead;
            if (cardHead is null || !GitCliEvidence.IsSha(cardHead))
                return Fail(runId, run.ThreadId, run.TaskId, "head-format",
                    "The card names no full commit SHA.", nowUtc, cardPath);
            if (run.SettleHead is not null &&
                !string.Equals(run.SettleHead, cardHead, StringComparison.OrdinalIgnoreCase))
                return Fail(runId, run.ThreadId, run.TaskId, "head-not-from-run",
                    $"The run left HEAD {run.SettleHead}; the card claims {cardHead}.", nowUtc, cardPath);
            var parts = ResolveParts(run.TaskId, root);
            if (parts is null)
                return Fail(runId, run.ThreadId, run.TaskId, "part-ambiguous",
                    "The task has several parts and the card names none of them.", nowUtc, cardPath);
            if (parts.Count == 0)
                return Fail(runId, run.ThreadId, run.TaskId, "unknown-part",
                    $"Task '{run.TaskId}' has no delivery part to attach the card to.", nowUtc, cardPath);
            var captures = new List<ConvDeliveryCapture>();
            var events = new List<string>();
            foreach (var partId in parts)
            {
                var outcome = await _receiver.ReceiveAsync(new DeliveryCandidate(runId, run.ThreadId,
                    run.TaskId, partId, cardHead, cardJson,
                    "conv-rev-" + Hash(runId + ":" + partId),
                    "conv:delivery:" + Hash(runId + ":" + partId), nowUtc)
                { CardStatus = cardStatus }, cancellationToken).ConfigureAwait(false);
                if (outcome.AcceptedForReview)
                {
                    captures.Add(new ConvDeliveryCapture(runId, run.ThreadId, run.TaskId,
                        "delivery.received", outcome.Code, outcome.Detail, partId,
                        outcome.RevisionNumber, outcome.ObservedHead, cardPath));
                    events.Add(Record("received", runId, partId, new
                    {
                        run = runId, thread = run.ThreadId, task = run.TaskId, part = partId,
                        revision = outcome.RevisionNumber, head = outcome.ObservedHead,
                        code = outcome.Code, card = cardPath
                    }, nowUtc));
                }
                else
                {
                    captures.Add(new ConvDeliveryCapture(runId, run.ThreadId, run.TaskId,
                        "delivery.failed", outcome.Code, outcome.Detail, partId, null,
                        outcome.ObservedHead, cardPath));
                    events.Add(Record("failed", runId, partId, new
                    {
                        run = runId, thread = run.ThreadId, task = run.TaskId, part = partId,
                        code = outcome.Code, detail = outcome.Detail, head = cardHead, card = cardPath
                    }, nowUtc));
                }
            }
            return new(runId, run.ThreadId, captures, events);
        }
    }

    /// <summary>
    /// Observe every run that still holds a thread or worktree. Each run is reported independently:
    /// a live writer defers and an abnormal exit fails, and neither blocks the others.
    /// </summary>
    public async Task<ConvWatchdogReport> SweepAsync(long nowUtc,
        CancellationToken cancellationToken = default)
    {
        var captures = new List<ConvDeliveryCapture>();
        var events = new List<string>();
        foreach (var run in _runs.ListActive())
        {
            var report = await ObserveAsync(run.RunId, nowUtc, cancellationToken).ConfigureAwait(false);
            captures.AddRange(report.Captures);
            events.AddRange(report.Events);
        }
        return new("", "", captures, events);
    }

    // ------------------------------------------------------------------ helpers

    private ConvWatchdogReport Fail(string runId, string threadId, string taskId, string code,
        string detail, long nowUtc, string? cardPath)
    {
        var capture = new ConvDeliveryCapture(runId, threadId, taskId, "delivery.failed", code,
            detail, null, null, null, cardPath);
        var @event = Record("failed", runId, null, new
        {
            run = runId, thread = threadId, task = taskId, code, detail, card = cardPath
        }, nowUtc);
        return new(runId, threadId, [capture], [@event]);
    }

    private string Record(string kind, string runId, string? partId, object payload, long nowUtc)
    {
        var eventId = "conv:delivery:" + Hash(runId + (partId is null ? "" : ":" + partId)) + ":" + kind;
        try
        {
            _store.AppendEvent(eventId, "conv." + kind, "", null, partId, runId,
                JsonSerializer.Serialize(payload), nowUtc);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("already exists"))
        {
            // Idempotent replay: event was already recorded for this run+part+kind.
        }
        return eventId;
    }

    private static string StatusCode(HarnessRunStatus status) => status switch
    {
        HarnessRunStatus.Failed => "run-failed",
        HarnessRunStatus.Interrupted => "run-interrupted",
        HarnessRunStatus.Unknown => "unknown-requires-resolution",
        _ => "run-not-terminal"
    };

    /// <summary>
    /// Map a card to the real parts of its own task. A card naming parts resolves against the
    /// registered rows; a single-part task needs no card hint, and a multi-part task without a hint
    /// is ambiguous and refused rather than guessed.
    /// </summary>
    private IReadOnlyList<string>? ResolveParts(string taskId, JsonElement root)
    {
        var registered = _store.ListParts(taskId).Select(p => p.PartId).ToList();
        if (root.TryGetProperty("parts", out var value) && value.ValueKind == JsonValueKind.Array)
        {
            var named = value.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!).ToList();
            if (named.Count == 0) return registered.Count == 1 ? registered : null;
            return named.All(registered.Contains) ? named : null;
        }
        return registered.Count == 1 ? registered : null;
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static bool? Flag(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True
            or JsonValueKind.False ? value.GetBoolean() : null;

    private static string? LastCommit(JsonElement root) =>
        root.TryGetProperty("commits", out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().LastOrDefault().ValueKind == JsonValueKind.String
                ? value.EnumerateArray().Last().GetString() : null
            : null;

    private static string Hash(string value) => HarnessRunStore.Hash(value)[..32];
}
