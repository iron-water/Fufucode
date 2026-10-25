using System.Collections.Concurrent;
using Fufucode.App.Abstractions;

namespace Fufucode.Core.Plugins;

/// <summary>
/// 协同核心的宿主实现：连接器注册、事件去重与扇出、意图路由、审计。
///
/// 边界：
/// - 不保存会话正文，不拥有任何业务真源；
/// - 不替连接器解释业务语义；
/// - 事件按 EventId 去重，重放不产生副作用；
/// - 意图按幂等键去重，结果未知时不自动重放。
/// </summary>
public sealed class CompanionCore : ICompanionCore, IDisposable
{
    /// <summary>有界审计：只保留最近的规范消息元数据，不含业务正文之外的额外副本。</summary>
    private const int MaxAuditEntries = 512;

    private readonly object _gate = new();
    private readonly Dictionary<string, ICompanionConnector> _connectors = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _seenEvents = new(StringComparer.Ordinal);
    private readonly Queue<string> _seenEventOrder = new();
    private readonly Dictionary<string, CanonicalReceipt> _receipts = new(StringComparer.Ordinal);
    private readonly Queue<string> _receiptOrder = new();
    private readonly ConcurrentQueue<CanonicalAuditEntry> _audit = new();
    private bool _disposed;

    /// <summary>每条边独立判断；一个连接器故障不影响其它连接器。</summary>
    public void RegisterConnector(ICompanionConnector connector)
    {
        ArgumentNullException.ThrowIfNull(connector);
        if (string.IsNullOrWhiteSpace(connector.ConnectorId))
            throw new ArgumentException("连接器 ID 不能为空。", nameof(connector));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_connectors.ContainsKey(connector.ConnectorId))
                throw new InvalidOperationException($"连接器 '{connector.ConnectorId}' 已注册。");
            _connectors[connector.ConnectorId] = connector;
        }
        Record("connector.registered", connector.ConnectorId, null);
    }

    public void UnregisterConnector(string connectorId)
    {
        if (string.IsNullOrWhiteSpace(connectorId)) return;
        lock (_gate) _connectors.Remove(connectorId);
        Record("connector.unregistered", connectorId, null);
    }

    public void Publish(CanonicalFact fact)
    {
        ArgumentNullException.ThrowIfNull(fact);
        if (string.IsNullOrWhiteSpace(fact.Owner))
            throw new ArgumentException("事实必须声明 Owner。", nameof(fact));
        Record("fact.published", fact.Owner, fact.FactId);
    }

    public void Publish(CanonicalEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        if (string.IsNullOrWhiteSpace(evt.Owner))
            throw new ArgumentException("事件必须声明 Owner。", nameof(evt));

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_seenEvents.Add(evt.EventId)) return; // 重放：不重复扇出
            _seenEventOrder.Enqueue(evt.EventId);
            while (_seenEventOrder.Count > MaxAuditEntries) _seenEvents.Remove(_seenEventOrder.Dequeue());
        }
        Record("event.published", evt.Owner, evt.EventId);
        FanOut(evt);
    }

    public async Task<CanonicalReceipt> SubmitIntentAsync(CanonicalIntent intent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        if (string.IsNullOrWhiteSpace(intent.Target))
            throw new ArgumentException("意图必须声明目标。", nameof(intent));

        if (intent.ExpiresAt is { } expiresAt && expiresAt <= DateTimeOffset.UtcNow)
        {
            return Receipt(intent, CompanionReceiptStatus.Expired, errorCode: "expired");
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_receipts.TryGetValue(intent.IdempotencyKey, out var existing))
                return existing;
        }

        ICompanionConnector? target;
        lock (_gate) _connectors.TryGetValue(intent.Target, out target);
        if (target is null)
        {
            return Receipt(intent, CompanionReceiptStatus.Rejected, errorCode: "connector_unavailable");
        }
        if (target is not ICompanionIntentSink sink)
        {
            return Receipt(intent, CompanionReceiptStatus.Rejected, errorCode: "intent_unsupported");
        }

        Record("intent.submitted", intent.Source, intent.IntentId);
        CanonicalReceipt receipt;
        try
        {
            receipt = await sink.HandleIntentAsync(intent, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            receipt = Receipt(intent, CompanionReceiptStatus.Unknown, errorCode: "cancelled");
        }
        catch (Exception error)
        {
            receipt = Receipt(intent, CompanionReceiptStatus.Failed, errorCode: error.GetType().Name);
        }

        // 只有明确终态才记入幂等表；unknown 仍记录下来，避免自动重放写操作。
        lock (_gate)
        {
            if (_receipts.Count >= MaxAuditEntries && _receiptOrder.Count > 0)
                _receipts.Remove(_receiptOrder.Dequeue());
            if (!_receipts.ContainsKey(intent.IdempotencyKey))
            {
                _receipts[intent.IdempotencyKey] = receipt;
                _receiptOrder.Enqueue(intent.IdempotencyKey);
            }
        }
        Record("intent.receipt", intent.Target, intent.IntentId, receipt.Status);
        return receipt;
    }

    public IReadOnlyList<CompanionConnectorStatus> Status
    {
        get
        {
            lock (_gate)
            {
                return _connectors.Values
                    .Select(connector => new CompanionConnectorStatus(
                        connector.ConnectorId,
                        connector.State,
                        connector.Capabilities,
                        ReceivedCount(connector.ConnectorId),
                        SentCount(connector.ConnectorId),
                        LastError(connector.ConnectorId)))
                    .ToArray();
            }
        }
    }

    public IReadOnlyList<CanonicalAuditEntry> RecentAudit(int max = 50) =>
        _audit.Reverse().Take(Math.Clamp(max, 1, MaxAuditEntries)).ToArray();

    private void FanOut(CanonicalEvent evt)
    {
        ICompanionConnector[] targets;
        lock (_gate)
        {
            targets = _connectors.Values
                .Where(connector => !string.Equals(connector.ConnectorId, evt.Owner, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        foreach (var connector in targets)
        {
            // 每条边独立投递：慢连接器不阻塞其它连接器。
            _ = DeliverAsync(connector, evt);
        }
    }

    private async Task DeliverAsync(ICompanionConnector connector, CanonicalEvent evt)
    {
        try
        {
            await connector.DeliverAsync(evt, CancellationToken.None).ConfigureAwait(false);
            _sentCount.AddOrUpdate(connector.ConnectorId, 1, (_, current) => current + 1);
            Record("event.delivered", connector.ConnectorId, evt.EventId);
        }
        catch (Exception error)
        {
            _lastError[connector.ConnectorId] = error.GetType().Name;
            Record("event.delivery_failed", connector.ConnectorId, evt.EventId, error.GetType().Name);
        }
    }

    private readonly ConcurrentDictionary<string, long> _sentCount = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _receivedCount = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string?> _lastError = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>连接器从自己那侧收到外部事件时调用，供状态面板显示。</summary>
    public void NoteReceived(string connectorId)
    {
        if (string.IsNullOrWhiteSpace(connectorId)) return;
        _receivedCount.AddOrUpdate(connectorId, 1, (_, current) => current + 1);
    }

    private long ReceivedCount(string connectorId) => _receivedCount.TryGetValue(connectorId, out var value) ? value : 0;

    private long SentCount(string connectorId) => _sentCount.TryGetValue(connectorId, out var value) ? value : 0;

    private string? LastError(string connectorId) => _lastError.TryGetValue(connectorId, out var value) ? value : null;

    private CanonicalReceipt Receipt(CanonicalIntent intent, string status, string? errorCode = null)
    {
        var receipt = new CanonicalReceipt(
            Guid.NewGuid().ToString("N"),
            intent.IntentId,
            intent.Target,
            status,
            DateTimeOffset.UtcNow,
            EvidenceJson: null,
            ErrorCode: errorCode);
        Record("intent.receipt", intent.Target, intent.IntentId, status);
        return receipt;
    }

    private void Record(string kind, string connectorId, string? id, string? detail = null) =>
        _audit.Enqueue(new CanonicalAuditEntry(DateTimeOffset.UtcNow, kind, connectorId, id, detail));

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _connectors.Clear();
        }
    }
}

/// <summary>审计条目：只记录路由元数据，不复制业务正文。</summary>
public sealed record CanonicalAuditEntry(
    DateTimeOffset At,
    string Kind,
    string ConnectorId,
    string? ReferenceId,
    string? Detail);
