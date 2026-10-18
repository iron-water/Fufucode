using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using System.Text;
using System.Text.Json;
using Fufucode.Core.Automation;

namespace Fufucode.Core.Automation.Hosting;

/// <summary>A frame received from the host: a reply to a request or a pushed event.</summary>
public sealed record HostFrame(string Type, string? Id, JsonElement Payload);

/// <summary>
/// Client for the one background host. Deliberately thin and disposable: closing a client stops
/// reading, it never stops the host and never stops a run in flight. A background reader routes
/// frames, so a reply is matched to its request id while pushed EVENT frames queue for the subscriber.
/// Reconnection is a fresh connection plus an EVENTS read from the last cursor the client actually
/// processed, which is how missed events are replayed instead of lost.
/// </summary>
public sealed class HostClient : IAsyncDisposable
{
    private readonly TcpClient _socket;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<HostFrame>> _pending = new();
    private readonly Channel<HostFrame> _pushed = Channel.CreateUnbounded<HostFrame>();
    private readonly CancellationTokenSource _closed = new();
    private readonly Task _pump;
    private long _nextId;

    private HostClient(TcpClient socket)
    {
        _socket = socket;
        _reader = new StreamReader(socket.GetStream(), new UTF8Encoding(false));
        _writer = new StreamWriter(socket.GetStream(), new UTF8Encoding(false)) { AutoFlush = true };
        _pump = Task.Run(PumpAsync);
    }

    public static async Task<HostClient> ConnectAsync(int port,
        CancellationToken cancellationToken = default)
    {
        var socket = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try { await socket.ConnectAsync(IPAddress.Loopback, port, timeout.Token); }
        catch (SocketException error)
        {
            socket.Dispose();
            throw new InvalidOperationException(
                $"No host is listening on loopback port {port}: {error.SocketErrorCode}", error);
        }
        return new HostClient(socket);
    }

    /// <summary>Read the port recorded by the host, or null when no host published one.</summary>
    public static int? ReadEndpoint(string endpointFile)
    {
        try
        {
            using var stream = new FileStream(endpointFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return int.TryParse(reader.ReadToEnd().Trim(), out var port) && port is > 0 and <= 65535 ? port : null;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    public async Task<HostFrame> SendAsync(string type, object? fields = null,
        CancellationToken cancellationToken = default)
    {
        var id = "c" + Interlocked.Increment(ref _nextId);
        var reply = new TaskCompletionSource<HostFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = reply;
        var envelope = new Dictionary<string, object?> { ["type"] = type, ["id"] = id };
        if (fields is not null)
            foreach (var property in fields.GetType().GetProperties())
                envelope[Camel(property.Name)] = property.GetValue(fields);
        var json = JsonSerializer.Serialize(envelope);
        await _sendGate.WaitAsync(cancellationToken);
        try { await _writer.WriteLineAsync(json); }
        finally { _sendGate.Release(); }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closed.Token);
        linked.CancelAfter(TimeSpan.FromSeconds(30));
        try { await reply.Task.WaitAsync(linked.Token); }
        catch (OperationCanceledException)
        {
            _pending.TryRemove(id, out _);
            throw new TimeoutException($"The host never answered {type}.");
        }
        return await reply.Task;
    }

    private async Task PumpAsync()
    {
        try
        {
            while (!_closed.IsCancellationRequested)
            {
                var line = await _reader.ReadLineAsync(_closed.Token);
                if (line is null) break;
                if (line.Trim().Length == 0) continue;
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement.Clone();
                var type = root.TryGetProperty("type", out var value) ? value.GetString() ?? "" : "";
                var id = root.TryGetProperty("id", out var field) &&
                    field.ValueKind == JsonValueKind.String ? field.GetString() : null;
                var frame = new HostFrame(type, id, root);
                if (id is not null && _pending.TryRemove(id, out var waiting))
                { waiting.TrySetResult(frame); continue; }
                _pushed.Writer.TryWrite(frame);
            }
        }
        catch (IOException) { /* the link is gone */ }
        catch (OperationCanceledException) { /* disposed */ }
        catch (ObjectDisposedException) { /* disposed */ }
        catch (JsonException) { /* unparsable line: keep the link, the host never sends those */ }
        finally
        {
            // Anything still waiting is answered with the fact that the host closed the link.
            foreach (var pair in _pending)
                if (_pending.TryRemove(pair.Key, out var pending))
                    pending.TrySetException(new EndOfStreamException("The host closed the connection."));
            _pushed.Writer.TryComplete();
        }
    }

    /// <summary>Next pushed frame (an EVENT, or a host-side notice). Null when the link ended.</summary>
    public async Task<HostFrame?> NextPushedAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var frame = await _pushed.Reader.ReadAsync(cancellationToken);
            return frame;
        }
        catch (ChannelClosedException) { return null; }
    }

    public async Task<HostSnapshot?> SnapshotAsync(CancellationToken cancellationToken = default)
    {
        var frame = await SendAsync(HostProtocol.Snapshot, null, cancellationToken);
        return frame.Payload.TryGetProperty("host", out var host)
            ? host.Deserialize<HostSnapshot>(ReadOptions) : null;
    }

    /// <summary>Ask the host to queue one dispatch intent. Durable before anything starts.</summary>
    public async Task<JsonElement> DispatchAsync(string runId, string payloadJson,
        CancellationToken cancellationToken = default)
    {
        var frame = await SendAsync(HostProtocol.Command, new CommandEnvelope("dispatch",
            new Dictionary<string, object?> { ["runId"] = runId, ["payloadJson"] = payloadJson }),
            cancellationToken);
        return frame.Payload;
    }

    public async Task<JsonElement> AbandonAsync(string runId, string reason,
        CancellationToken cancellationToken = default)
    {
        var frame = await SendAsync(HostProtocol.Command, new CommandEnvelope("abandon",
            new Dictionary<string, object?> { ["runId"] = runId, ["reason"] = reason }),
            cancellationToken);
        return frame.Payload;
    }

    /// <summary>Read durable events after a cursor. This is how a reconnecting client catches up.</summary>
    public async Task<(long Cursor, List<AutomationEvent> Events)> EventsAsync(long cursor,
        int limit = 200, CancellationToken cancellationToken = default)
    {
        var frame = await SendAsync(HostProtocol.Events, new { cursor, limit }, cancellationToken);
        var next = frame.Payload.TryGetProperty("nextCursor", out var value) &&
                   value.TryGetInt64(out var number) ? number : cursor;
        return (next, Read(frame.Payload));
    }

    /// <summary>Read every event after a cursor, paging through the durable journal.</summary>
    public async Task<(long Cursor, List<AutomationEvent> Events)> CatchUpAsync(long cursor,
        CancellationToken cancellationToken = default)
    {
        var all = new List<AutomationEvent>();
        var seen = cursor;
        while (true)
        {
            var (next, page) = await EventsAsync(seen, 200, cancellationToken);
            all.AddRange(page);
            if (page.Count == 0 || next == seen) break;
            seen = next;
        }
        return (seen, all);
    }

    private static List<AutomationEvent> Read(JsonElement payload)
    {
        var events = new List<AutomationEvent>();
        if (!payload.TryGetProperty("events", out var list) ||
            list.ValueKind != JsonValueKind.Array) return events;
        foreach (var item in list.EnumerateArray())
        {
            string? Text(string name) => item.TryGetProperty(name, out var property) &&
                property.ValueKind == JsonValueKind.String ? property.GetString() : null;
            long Number(string name) => item.TryGetProperty(name, out var property) &&
                property.TryGetInt64(out var parsed) ? parsed : 0;
            events.Add(new AutomationEvent(Number("cursor"), Text("eventId") ?? "",
                Text("eventType") ?? "", Text("threadId") ?? "", Text("taskId"), Text("partId"),
                Text("runId"), Text("payloadJson") ?? "", Number("occurredUtc")));
        }
        return events;
    }

    public Task<HostFrame> SubscribeAsync(long cursor, CancellationToken cancellationToken = default) =>
        SendAsync(HostProtocol.Subscribe, new { cursor }, cancellationToken);

    /// <summary>Explicit stop: only this ends the host, and it is journaled as a clean stop.</summary>
    public Task<HostFrame> StopAsync(CancellationToken cancellationToken = default) =>
        SendAsync(HostProtocol.Stop, null, cancellationToken);

    public async Task ByeAsync(CancellationToken cancellationToken = default)
    {
        try { await SendAsync(HostProtocol.Bye, null, cancellationToken); }
        catch (IOException) { /* the link is already gone */ }
    }

    private sealed record CommandEnvelope(string Action, object Payload);

    private static readonly JsonSerializerOptions ReadOptions = new()
    { PropertyNameCaseInsensitive = true };

    private static string Camel(string name) =>
        name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name[1..];

    public async ValueTask DisposeAsync()
    {
        _closed.Cancel();
        try { _writer.Dispose(); } catch { /* closed */ }
        try { _reader.Dispose(); } catch { /* closed */ }
        _socket.Dispose();
        try { await _pump.WaitAsync(TimeSpan.FromSeconds(5)); } catch { /* closing */ }
        _sendGate.Dispose();
        _closed.Dispose();
    }
}
