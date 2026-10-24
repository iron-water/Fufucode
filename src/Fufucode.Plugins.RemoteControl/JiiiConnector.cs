using System.Text.Json;
using Fufucode.App.Abstractions;
using Fufucode.App.Features.RemoteControl;
using Fufucode.Core.Plugins;
using Fufucode.Core.RemoteControl;

namespace Fufucode.Plugins.RemoteControl;

/// <summary>
/// “Jiii 连接”插件对协同核心的适配器。
///
/// 只做协议映射：
/// - 把远控适配器产生的 <see cref="RemoteUpdate"/> 映射为 Canonical 事件；
/// - 把来自其它连接器的事件映射为远控可展示的通知（当前只支持 notice）；
/// - 不保存业务状态，不直接写会话。
///
/// 已知边界：远控线协议 v1 没有通用事件类型，除 notice 外的入站事件会被明确拒绝，
/// 而不是静默丢弃。
/// </summary>
public sealed class JiiiConnector : ICompanionConnector, IDisposable
{
    public const string Id = "service.remote-control";

    private const int MaxPayloadChars = 16 * 1024;

    private readonly RemoteControlHost _host;
    private RemoteSessionAdapter? _adapter;
    private string? _sessionId;
    private long _revision;
    private bool _disposed;

    public JiiiConnector(RemoteControlHost host) => _host = host;

    public string ConnectorId => Id;

    public CompanionCapabilities Capabilities => new(
        PublishesFacts: true,
        PublishesEvents: true,
        AcceptsIntents: true,
        HandlesApproval: true,
        TransfersFiles: false);

    public CompanionConnectorState State => _disposed
        ? CompanionConnectorState.Stopped
        : _host.IsRunning ? CompanionConnectorState.Running : CompanionConnectorState.Stopped;

    /// <summary>由宿主在 Attach 之后调用，开始把远控增量映射成规范事件。</summary>
    public void Observe(RemoteSessionAdapter adapter)
    {
        if (ReferenceEquals(_adapter, adapter)) return;
        Detach();
        _adapter = adapter;
        _sessionId = adapter.Snapshot().SessionId;
        adapter.Updated += OnUpdated;
        PublishSnapshot(adapter.Snapshot());
    }

    public void Detach()
    {
        if (_adapter is not null)
        {
            _adapter.Updated -= OnUpdated;
            _adapter = null;
        }
    }

    private void OnUpdated(object? sender, RemoteUpdate update)
    {
        var core = ResolveCore();
        if (core is null) return;
        switch (update)
        {
            case RemoteSnapshotUpdate snapshot:
                PublishSnapshot(snapshot.Session);
                break;
            case RemoteApprovalAddedUpdate approval:
                Publish(core, "jiii.approval.requested", new
                {
                    approvalId = approval.Approval.ApprovalId,
                    toolCallId = approval.Approval.ToolCallId,
                    toolName = approval.Approval.ToolName,
                    summary = Truncate(approval.Approval.Summary),
                    requestedAtUtc = approval.Approval.RequestedAtUtc,
                }, sensitivity: "summary");
                break;
            case RemoteApprovalResolvedUpdate resolved:
                Publish(core, "jiii.approval.resolved", new
                {
                    approvalId = resolved.Resolution.ApprovalId,
                    decision = resolved.Resolution.Decision.ToString(),
                    accepted = resolved.Resolution.Accepted,
                }, sensitivity: "summary");
                break;
            case RemoteStatusUpdate status:
                Publish(core, "jiii.session.status", new
                {
                    activity = status.Activity,
                    connection = status.Connection,
                    canSend = status.CanSend,
                    canStop = status.CanStop,
                    queuedCount = status.QueuedCount,
                }, sensitivity: "summary");
                break;
            case RemoteNoticeUpdate notice:
                Publish(core, "jiii.session.notice", new { message = Truncate(notice.Message) },
                    sensitivity: "summary");
                break;
            case RemoteSessionsUpdate sessions:
                Publish(core, "jiii.sessions.changed", new
                {
                    currentSessionId = sessions.Catalog.CurrentSessionId,
                    count = sessions.Catalog.Sessions.Count,
                }, sensitivity: "public");
                break;
            case RemoteEntryUpdate:
                // 逐条时间线增量不跨端转发：它包含会话正文，跨端只允许摘要级事实。
                break;
        }
    }

    private void PublishSnapshot(RemoteSessionSnapshot snapshot)
    {
        var core = ResolveCore();
        if (core is null) return;
        _sessionId = snapshot.SessionId;
        core.Publish(new CanonicalFact(
            FactId: $"{Id}:session:{snapshot.SessionId}",
            Owner: Id,
            Aggregate: "desktop.session",
            Revision: Interlocked.Increment(ref _revision),
            ObservedAt: DateTimeOffset.UtcNow,
            StaleAfterMs: 30_000,
            Confidence: "observed",
            PayloadJson: JsonSerializer.Serialize(new
            {
                sessionId = snapshot.SessionId,
                title = Truncate(snapshot.Title),
                activity = snapshot.Activity,
                connection = snapshot.Connection,
                canSend = snapshot.CanSend,
                canStop = snapshot.CanStop,
                queuedCount = snapshot.QueuedCount,
                entryCount = snapshot.Entries.Count,
                pendingApprovalCount = snapshot.PendingApprovals.Count,
            })));
    }

    private void Publish(ICompanionCore core, string type, object payload, string sensitivity)
    {
        var json = JsonSerializer.Serialize(payload);
        core.Publish(new CanonicalEvent(
            EventId: $"{Id}:{Guid.NewGuid():N}",
            Owner: Id,
            Aggregate: _sessionId ?? "desktop.session",
            Revision: Interlocked.Increment(ref _revision),
            Type: type,
            OccurredAt: DateTimeOffset.UtcNow,
            CorrelationId: _sessionId,
            Sensitivity: sensitivity,
            PayloadJson: json.Length > MaxPayloadChars ? json[..MaxPayloadChars] : json));
    }

    public Task DeliverAsync(CanonicalEvent evt, CancellationToken cancellationToken = default)
    {
        if (_disposed) return Task.CompletedTask;
        // 远控线协议 v1 只有 notice 能承载跨端提示；其它类型明确拒绝，不假装已展示。
        if (evt.Type != "fufu.notice" && evt.Type != "jiii.notice")
            throw new NotSupportedException($"远控协议 v1 不支持入站事件类型 {evt.Type}。");
        var message = ReadMessage(evt.PayloadJson);
        if (string.IsNullOrWhiteSpace(message)) return Task.CompletedTask;
        _host.Broadcast(new RemoteNoticeUpdate(Truncate(message)!));
        return Task.CompletedTask;
    }

    private static string? ReadMessage(string payloadJson)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            return document.RootElement.TryGetProperty("message", out var value) &&
                   value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ICompanionCore? ResolveCore() => PluginPackageManager.CompanionCore;

    private static string? Truncate(string? value) =>
        value is null || value.Length <= 512 ? value : value[..512];

    public void Dispose()
    {
        _disposed = true;
        Detach();
    }
}
