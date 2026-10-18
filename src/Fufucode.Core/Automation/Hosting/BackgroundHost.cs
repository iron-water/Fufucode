using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Fufucode.Core.Automation;
using Fufucode.Core.Automation.Harnesses;

namespace Fufucode.Core.Automation.Hosting;

public sealed record HostOptions(string Database, string EvidenceDirectory, string HostId,
    string EndpointFile, int Port = 0, int IdleMs = 100, int PushMs = 60, int EventLimit = 200,
    int AdvanceMs = 2000);

/// <summary>
/// C2: the one background execution host. It claims a durable single-host slot, serves a line
/// protocol over loopback TCP, and drains the durable queue through the existing HarnessExecutor.
/// No AUTO, no second scheduler and no second event store: the queue only records what was accepted,
/// the harness journal still decides what ran and what its outcome was, and clients read the U1
/// event cursors. A client that goes away changes nothing; only an explicit stop ends the host, and
/// that stop is journaled so a clean exit stays distinguishable from a crash.
/// </summary>
public sealed class BackgroundHost : IAsyncDisposable
{
    private readonly HostOptions _options;
    private readonly AutomationStore _store;
    private readonly HostJournal _queue;
    private readonly HarnessExecutor _executor;
    private readonly RunReconciler _reconciler;
    private readonly AutoAdvancementEngine _advancement;
    private readonly IProcessProbe _probe;
    private readonly CancellationTokenSource _stopping = new();
    private TcpListener? _listener;
    private Task? _drain;
    private Task? _accept;
    private long _ownStartUtc;
    private long _nextAdvanceUtc;
    private readonly TaskCompletionSource _stopRecorded =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>Set only by an explicit stop, never by a process death: that is what keeps a clean
    /// exit distinguishable from a crash in the durable record.</summary>
    private volatile bool _stopRequested;

    public BackgroundHost(HostOptions options, IProcessProbe? probe = null)
    {
        _options = options;
        _probe = probe ?? SystemProcessProbe.Shared;
        _store = new AutomationStore(options.Database);
        _queue = new HostJournal(_store);
        _executor = new HarnessExecutor(_store, options.EvidenceDirectory, options.HostId,
            GitCliEvidence.Shared);
        _reconciler = new RunReconciler(_store, _executor.Runs, _probe, GitCliEvidence.Shared,
            options.HostId);
        // CONV-3: the same durable store drives the idle reconciliation. The host never runs a second
        // scheduler: when the queue is empty it asks the engine for the next deterministic step.
        _advancement = new AutoAdvancementEngine(_store, probe: _probe);
    }

    public AutomationStore Store => _store;
    public HostJournal Queue => _queue;
    public HarnessExecutor Executor => _executor;
    public RunReconciler Reconciler => _reconciler;
    public AutoAdvancementEngine Advancement => _advancement;
    public string HostId => _options.HostId;
    public int Port { get; private set; }
    public HostRecovery? Recovery { get; private set; }
    public Task DrainTask => _drain ?? Task.CompletedTask;
    public Task AcceptTask => _accept ?? Task.CompletedTask;
    /// <summary>Completes once the clean stop is journaled (never when the host was killed).</summary>
    public Task StopCompleted => _stopRecorded.Task;

    /// <summary>
    /// Claim the slot and start serving. Returns false when a live or uninspectable host already
    /// owns it: two hosts on one machine are competitors, never co-owners.
    /// </summary>
    public async Task<bool> StartAsync()
    {
        _ownStartUtc = _probe is SystemProcessProbe selfProbe
            ? selfProbe.Self.StartUtc!.Value.ToUnixTimeMilliseconds()
            : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var now = Now();
        if (!_queue.TryClaim(new HostInstance(_options.HostId, _probe.CurrentPid, _ownStartUtc, now),
                _probe))
            return false;
        _listener = new TcpListener(IPAddress.Loopback, _options.Port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        var endpointDirectory = Path.GetDirectoryName(Path.GetFullPath(_options.EndpointFile));
        if (endpointDirectory is not null) Directory.CreateDirectory(endpointDirectory);
        var endpoint = Path.GetFullPath(_options.EndpointFile);
        var pendingEndpoint = endpoint + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(pendingEndpoint, Port.ToString());
        if (File.Exists(endpoint)) File.Replace(pendingEndpoint, endpoint, destinationBackupFileName: null);
        else File.Move(pendingEndpoint, endpoint);
        // Recovery happens before the host serves anyone, so a client's first HELLO can state exactly
        // what the restart did instead of racing with a background pass.
        Recovery = _queue.RecoverAfterRestart(_executor.Runs, now);
        _drain = Task.Run(DrainAsync);
        _accept = Task.Run(AcceptAsync);
        await Task.CompletedTask;
        return true;
    }

    public HostInstance? Occupant => _queue.Current(_probe);
    public HostSlotState Slot => _queue.SlotState(_probe);

    private async Task DrainAsync()
    {
        try
        {
            while (!_stopping.IsCancellationRequested)
            {
                _queue.Heartbeat(_options.HostId, Now());
                var next = _queue.NextQueued();
                if (next is null || _stopping.IsCancellationRequested)
                {
                    // Idle reconciliation: a project with no active worker gets one deterministic step
                    // (settle reviews, authorize integration, dispatch a ready task) inside its recorded
                    // delegation, or an honest terminal/stalled reason. The engine never calls a model
                    // and never repeats a side effect, so an empty queue cannot spin.
                    if (!_stopping.IsCancellationRequested) await AdvanceIdleProjectsAsync();
                    await Task.Delay(_options.IdleMs, _stopping.Token);
                    continue;
                }
                await RunOneAsync(next);
            }
        }
        catch (OperationCanceledException) { /* stopping */ }
        finally
        {
            // The drain loop owns the release, so the run it was executing is finished and journaled
            // before the slot is marked stopped. A host that died never gets here: its residue stays.
            if (_stopRequested) FinishStop();
        }
    }

    /// <summary>
    /// One bounded advancement pass over the projects that share this database, rate-limited so an
    /// idle host does not busy-loop SQLite. Every failure is journaled as a stall, never retried as a
    /// model call and never allowed to end the host.
    /// </summary>
    private async Task AdvanceIdleProjectsAsync()
    {
        if (Now() < _nextAdvanceUtc) return;
        _nextAdvanceUtc = Now() + Math.Max(250, _options.AdvanceMs);
        try
        {
            if (_store.ReadControlSnapshot().Mode != DevelopmentMode.Automatic) return;
            foreach (var projectId in _store.ListThreads().Select(t => t.ProjectId).Distinct(StringComparer.Ordinal))
                await _advancement.AdvanceAsync(projectId, Now(), _stopping.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* stopping */ }
        catch (Exception)
        {
            // A stalled advancement is recorded by the engine's own receipt; the host keeps serving.
        }
    }

    /// <summary>
    /// Execute one accepted intent once. A failure of any kind abandons the intent with its reason:
    /// the queue never re-queues work that was already handed to the dispatcher, so no path here can
    /// resend an uncertain execution.
    /// </summary>
    private async Task RunOneAsync(HostRequest request)
    {
        HostDispatch dispatch;
        try { dispatch = HostJournal.ReadPayload(request.PayloadJson); }
        catch (Exception error)
        {
            _queue.MarkAbandoned(request.RunId, "payload " + error.GetType().Name, Now());
            return;
        }
        if (_queue.ControlBaseline(request.RunId) is { } baseline)
        {
            var worktree = _store.GetThread(request.ThreadId).Worktree;
            var head = await GitCliEvidence.Shared.HeadAsync(worktree,
                _stopping.Token).ConfigureAwait(false);
            var repair = _queue.IsControlRepair(request.RunId);
            if (!string.Equals(baseline, head, StringComparison.OrdinalIgnoreCase) ||
                repair && await GitCliEvidence.Shared.IsCleanAsync(worktree, _stopping.Token).ConfigureAwait(false) != true)
            {
                _queue.MarkAbandoned(request.RunId, repair ? "Repair baseline changed or has pending edits before execution." : "Worktree HEAD changed before control execution.", Now());
                return;
            }
        }
        if (_stopping.IsCancellationRequested) return;
        if (!_queue.MarkStarted(request.RunId, Now()))
        {
            // Someone else already moved this intent (a client abandoned it, or recovery settled it).
            // Starting it anyway would be a second execution of a row we no longer own.
            return;
        }
        try
        {
            using var executionCancellation = new CancellationTokenSource();
            using var observationCancellation = new CancellationTokenSource();
            ControlRunningExecution? requested = null;
            async Task ObserveStop()
            {
                try
                {
                    while (!observationCancellation.IsCancellationRequested)
                    {
                        var stop = _store.PendingControlStop(request.RunId, _options.HostId, _probe.CurrentPid, _ownStartUtc);
                        if (stop is not null)
                        {
                            requested = stop; executionCancellation.Cancel(); return;
                        }
                        await Task.Delay(100, observationCancellation.Token);
                    }
                }
                catch (OperationCanceledException) when (observationCancellation.IsCancellationRequested) { }
            }
            var observation = ObserveStop();
            HarnessRun run;
            try { run = await _executor.ExecuteAsync(dispatch.Runtime, dispatch.Request, executionCancellation.Token); }
            finally { observationCancellation.Cancel(); await observation; }
            _queue.MarkSettled(request.RunId, run.Status.ToString(), Now());
            if (requested is not null) _store.RecordControlStopAttempt(requested,
                run.Status == HarnessRunStatus.Completed ? "finished_before_cancel" : "attempt_finished_reconciliation_required", Now());
            else if (_store.UnconsumedControlStop(request.RunId, _options.HostId, _probe.CurrentPid, _ownStartUtc) is { } lateStop)
                _store.RecordControlStopAttempt(lateStop, "finished_before_cancel", Now());
        }
        catch (Exception error)
        {
            _queue.MarkAbandoned(request.RunId, error.GetType().Name, Now());
        }
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stopping.IsCancellationRequested && _listener is not null)
            {
                var client = await _listener.AcceptTcpClientAsync(_stopping.Token);
                _ = Task.Run(() => ServeConnectionAsync(client, _stopping.Token));
            }
        }
        catch (OperationCanceledException) { /* stopping */ }
        catch (SocketException) { /* listener closed */ }
        catch (ObjectDisposedException) { /* listener closed */ }
    }

    /// <summary>
    /// One client connection. The run in flight belongs to the host, not to the connection: an
    /// abrupt disconnect only ends this task, and a reconnect replays from the durable cursors.
    /// </summary>
    private async Task ServeConnectionAsync(TcpClient client, CancellationToken hostToken)
    {
        using (client)
        {
            client.NoDelay = true;
            using var writer = new StreamWriter(client.GetStream(), new UTF8Encoding(false))
            { AutoFlush = true };
            using var reader = new StreamReader(client.GetStream(), new UTF8Encoding(false));
            var sendGate = new SemaphoreSlim(1, 1);
            using var pushCts = CancellationTokenSource.CreateLinkedTokenSource(hostToken);
            var pusher = (Task?)null;
            try
            {
                while (true)
                {
                    var line = await reader.ReadLineAsync();
                    if (line is null) break;
                    if (line.Trim().Length == 0) continue;
                    JsonElement frame;
                    try { frame = JsonDocument.Parse(line).RootElement.Clone(); }
                    catch (JsonException)
                    {
                        await SendAsync(writer, sendGate, new { type = HostProtocol.Error,
                            code = "bad-json" });
                        continue;
                    }
                    var type = Text(frame, "type");
                    var id = Text(frame, "id");
                    switch (type)
                    {
                        case HostProtocol.Hello:
                            await SendAsync(writer, sendGate, new
                            {
                                type = HostProtocol.Hello, id, hostId = _options.HostId,
                                processId = _probe.CurrentPid, startUtc = _ownStartUtc,
                                port = Port, protocol = HostProtocol.Version,
                                // What the durable journal says right now, so a fresh client can start
                                // reading events from a proven watermark instead of guessing one.
                                eventCursor = _queue.LastEventCursor(),
                                slot = _queue.SlotState(_probe).ToString(),
                                recovered = Recovery?.Actions ?? [],
                                stopping = _stopping.IsCancellationRequested
                            });
                            break;
                        case HostProtocol.Ping:
                            await SendAsync(writer, sendGate, new
                            {
                                type = HostProtocol.Pong, id, hostId = _options.HostId,
                                heartbeatUtc = _queue.GetHeartbeat(_options.HostId)
                            });
                            break;
                        case HostProtocol.Snapshot:
                            await SendAsync(writer, sendGate, new
                            {
                                type = HostProtocol.Snapshot, id, host = Snapshot()
                            });
                            break;
                        case HostProtocol.Events:
                            {
                                var cursor = ReadLong(frame, "cursor");
                                var limit = (int)Math.Clamp(ReadLong(frame, "limit", _options.EventLimit),
                                    1, 1_000);
                                var page = _store.ListEventsAfter(cursor, limit);
                                await SendAsync(writer, sendGate, new
                                {
                                    type = HostProtocol.Events, id, cursor, limit,
                                    nextCursor = page.Count == 0 ? cursor : page[^1].Cursor,
                                    events = page.Select(Describe).ToList()
                                });
                                break;
                            }
                        case HostProtocol.Subscribe:
                            if (pusher is null)
                            {
                                var from = ReadLong(frame, "cursor");
                                await SendAsync(writer, sendGate,
                                    new { type = HostProtocol.Subscribed, id, cursor = from });
                                pusher = Task.Run(() => PushAsync(writer, sendGate, from, pushCts.Token));
                            }
                            else await SendAsync(writer, sendGate,
                                new { type = HostProtocol.Subscribed, id });
                            break;
                        case HostProtocol.Command:
                            await CommandAsync(writer, sendGate, id, frame);
                            break;
                        case HostProtocol.Stop:
                            // Explicit stop: the reply goes out first, the host then lets the run in
                            // flight finish and releases the slot as a clean shutdown.
                            await SendAsync(writer, sendGate,
                                new { type = HostProtocol.Stopping, id, hostId = _options.HostId,
                                    running = _queue.Count(HostRequestState.Started) });
                            RequestStop();
                            return;
                        case HostProtocol.Bye:
                            await SendAsync(writer, sendGate, new { type = HostProtocol.Closed, id });
                            return;
                        default:
                            await SendAsync(writer, sendGate, new { type = HostProtocol.Error, id,
                                code = "unknown-request", detail = type });
                            break;
                    }
                }
            }
            catch (IOException) { /* the client went away; the host and its runs are untouched */ }
            catch (OperationCanceledException) { /* host stopping */ }
            catch (ObjectDisposedException) { /* socket closed */ }
            finally
            {
                pushCts.Cancel();
                if (pusher is not null) { try { await pusher; } catch { /* closed */ } }
                sendGate.Dispose();
            }
        }
    }

    private async Task PushAsync(StreamWriter writer, SemaphoreSlim gate, long from,
        CancellationToken cancellationToken)
    {
        var cursor = from;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var page = _store.ListEventsAfter(cursor, _options.EventLimit);
                foreach (var automationEvent in page)
                {
                    await SendAsync(writer, gate, new { type = HostProtocol.Event,
                        @event = Describe(automationEvent) });
                    cursor = automationEvent.Cursor;
                }
                await Task.Delay(_options.PushMs, cancellationToken);
            }
        }
        catch (IOException) { /* client gone */ }
        catch (OperationCanceledException) { /* connection closed */ }
        catch (ObjectDisposedException) { /* socket closed */ }
    }

    private async Task CommandAsync(StreamWriter writer, SemaphoreSlim gate, string? id,
        JsonElement frame)
    {
        var action = Text(frame, "action");
        var payload = frame.TryGetProperty("payload", out var value) ? value : default;
        switch (action)
        {
            case "dispatch":
                {
                    var queued = Text(payload, "payloadJson");
                    var runId = Text(payload, "runId");
                    if (queued is null || runId is null)
                    {
                        await SendAsync(writer, gate, new { type = HostProtocol.Error, id,
                            code = "bad-command", detail = "dispatch needs runId and payloadJson" });
                        return;
                    }
                    HostDispatch dispatch;
                    try { dispatch = HostJournal.ReadPayload(queued); }
                    catch (Exception error)
                    {
                        await SendAsync(writer, gate, new { type = HostProtocol.Error, id,
                            code = "bad-payload", detail = error.GetType().Name });
                        return;
                    }
                    if (dispatch.Request.RunId != runId)
                    {
                        await SendAsync(writer, gate, new { type = HostProtocol.Error, id,
                            code = "run-mismatch", detail = dispatch.Request.RunId });
                        return;
                    }
                    try
                    {
                        var accepted = _queue.Enqueue(runId, dispatch.Request.ThreadId,
                            dispatch.Request.TaskId, queued, Now());
                        await SendAsync(writer, gate, new { type = HostProtocol.Queued, id, runId,
                            accepted, state = _queue.Get(runId)?.State.ToString(),
                            detail = _queue.Get(runId)?.Detail });
                    }
                    catch (Exception error)
                    {
                        await SendAsync(writer, gate, new { type = HostProtocol.Error, id,
                            code = error.GetType().Name, detail = error.Message });
                    }
                    return;
                }
            case "abandon":
                {
                    var runId = Text(payload, "runId");
                    if (runId is null || _queue.Get(runId) is null)
                    {
                        await SendAsync(writer, gate, new
                        { type = HostProtocol.Error, id, code = "unknown-run", runId });
                        return;
                    }
                    var applied = _queue.MarkAbandoned(runId,
                        Text(payload, "reason") ?? "client request", Now());
                    await SendAsync(writer, gate, new { type = HostProtocol.Ack, id, action, runId,
                        applied, state = _queue.Get(runId)?.State.ToString(),
                        detail = _queue.Get(runId)?.Detail });
                    return;
                }
            default:
                await SendAsync(writer, gate, new { type = HostProtocol.Error, id,
                    code = "unknown-action", detail = action });
                return;
        }
    }

    /// <summary>Everything a reconnecting client needs to see without trusting its own memory.</summary>
    public HostSnapshot Snapshot()
    {
        var state = _queue.ReadSnapshot();
        return new(_options.HostId, _probe.CurrentPid, _ownStartUtc,
            Now(), _stopping.IsCancellationRequested, Port,
            state.Queued, state.Started, state.Settled, state.Abandoned, state.EventCursor, state.ActiveRuns,
            Recovery is null ? [] : [.. Recovery.Requeued, .. Recovery.Abandoned, .. Recovery.Settled]);
    }

    private static object Describe(AutomationEvent automationEvent) => new
    {
        cursor = automationEvent.Cursor, eventId = automationEvent.EventId,
        eventType = automationEvent.Type, threadId = automationEvent.ThreadId,
        taskId = automationEvent.TaskId, partId = automationEvent.PartId,
        runId = automationEvent.RunId, payloadJson = automationEvent.PayloadJson,
        occurredUtc = automationEvent.OccurredUtc
    };

    private void RequestStop()
    {
        _stopRequested = true;
        _stopping.Cancel();
    }

    private void FinishStop()
    {
        _queue.Release(_options.HostId, Now());
        var endpoint = Path.GetFullPath(_options.EndpointFile);
        if (HostClient.ReadEndpoint(endpoint) == Port) File.Delete(endpoint);
        _listener?.Stop();
        _stopRecorded.TrySetResult();
    }

    /// <summary>Explicit stop without a client: the run in flight still finishes first.</summary>
    public async Task StopAsync()
    {
        RequestStop();
        try { if (_drain is not null) await _drain; } catch { /* journaled already */ }
        await StopCompleted;
    }

    public async ValueTask DisposeAsync()
    {
        _stopping.Cancel();
        _listener?.Stop();
        _stopRecorded.TrySetResult();
        if (_drain is not null) { try { await _drain; } catch { /* closing */ } }
        if (_accept is not null) { try { await _accept; } catch { /* closing */ } }
        _stopping.Dispose();
        _store.Dispose();
        GC.SuppressFinalize(this);
    }

    private static async Task SendAsync(StreamWriter writer, SemaphoreSlim gate, object message)
    {
        var json = JsonSerializer.Serialize(message, WriteOptions);
        await gate.WaitAsync();
        try { await writer.WriteLineAsync(json); }
        finally { gate.Release(); }
    }

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private static string? Text(JsonElement frame, string name) =>
        frame.ValueKind == JsonValueKind.Object && frame.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long ReadLong(JsonElement frame, string name, long fallback = 0) =>
        frame.ValueKind == JsonValueKind.Object && frame.TryGetProperty(name, out var value) &&
        value.TryGetInt64(out var number) ? number : fallback;

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
