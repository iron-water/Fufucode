namespace Fufucode.Core.RemoteControl;

/// <summary>
/// 远控服务所控制的会话桥接：提供快照、发送、停止与增量事件。
/// 实现方负责把真实会话（IChatSession / 后台宿主）投影为远控传输模型。
/// </summary>
public interface IRemoteSessionBridge
{
    /// <summary>工具调用审批闸门（手机端批准/拒绝经此流转）。</summary>
    IRemoteToolApprovalGate Approvals { get; }

    /// <summary>当前完整快照（连接建立与重连补齐）。</summary>
    RemoteSessionSnapshot Snapshot();

    /// <summary>提交一条用户消息；受理不等于执行完成。</summary>
    Task<RemoteCommandResult> SendAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>停止：清空队列并中止在途一轮。</summary>
    Task<RemoteCommandResult> StopAsync(CancellationToken cancellationToken = default);

    /// <summary>增量事件（消息块、工具状态、审批、状态变化）。可在任意线程引发。</summary>
    event EventHandler<RemoteUpdate>? Updated;
}

/// <summary>工具调用审批闸门：宿主登记待审批请求，手机端解析决定。</summary>
public interface IRemoteToolApprovalGate
{
    /// <summary>当前待审批请求（有序）。</summary>
    IReadOnlyList<RemoteApprovalRequest> Pending { get; }

    /// <summary>登记一个新的待审批工具调用。</summary>
    RemoteApprovalRequest Request(string toolCallId, string toolName, string argsJson, string? summary = null);

    /// <summary>解析审批；不存在或已失效时返回 Accepted = false。</summary>
    Task<RemoteApprovalResolution> ResolveAsync(string approvalId, RemoteApprovalDecision decision, CancellationToken cancellationToken = default);

    /// <summary>等待某个审批被决定（供宿主阻塞式执行使用）。</summary>
    Task<RemoteApprovalResolution> WaitAsync(string approvalId, CancellationToken cancellationToken = default);

    event EventHandler<RemoteApprovalRequest>? Requested;
    event EventHandler<RemoteApprovalResolution>? Resolved;
}

/// <summary>
/// 内存审批闸门：单次有效，重复解析返回「已失效」；用于独立模块与测试。
/// 真实宿主接线时可直接复用，或替换为与 Pi 审批通道对接的实现。
/// </summary>
public sealed class InMemoryToolApprovalGate : IRemoteToolApprovalGate
{
    private sealed class PendingApproval
    {
        public required RemoteApprovalRequest Request { get; init; }
        public TaskCompletionSource<RemoteApprovalResolution> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, PendingApproval> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RemoteApprovalResolution> _completed = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;

    public InMemoryToolApprovalGate(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

    public event EventHandler<RemoteApprovalRequest>? Requested;
    public event EventHandler<RemoteApprovalResolution>? Resolved;

    public IReadOnlyList<RemoteApprovalRequest> Pending
    {
        get { lock (_lock) return _pending.Values.Select(p => p.Request).ToArray(); }
    }

    public RemoteApprovalRequest Request(string toolCallId, string toolName, string argsJson, string? summary = null)
    {
        var request = new RemoteApprovalRequest(
            "apv-" + Guid.NewGuid().ToString("N")[..12],
            toolCallId,
            toolName,
            argsJson,
            summary,
            _clock.GetUtcNow());
        lock (_lock)
        {
            _pending[request.ApprovalId] = new PendingApproval { Request = request };
        }

        Requested?.Invoke(this, request);
        return request;
    }

    public Task<RemoteApprovalResolution> ResolveAsync(string approvalId, RemoteApprovalDecision decision, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(decision)) throw new ArgumentOutOfRangeException(nameof(decision));
        PendingApproval? entry;
        RemoteApprovalResolution resolution;
        lock (_lock)
        {
            if (!_pending.Remove(approvalId, out entry))
                return Task.FromResult(new RemoteApprovalResolution(
                    approvalId, string.Empty, decision, false, "审批不存在或已失效。"));
            resolution = new RemoteApprovalResolution(approvalId, entry.Request.ToolCallId, decision, true, null);
            if (_completed.Count >= 256) _completed.Remove(_completed.Keys.First());
            _completed[approvalId] = resolution;
        }
        entry.Completion.TrySetResult(resolution);
        Resolved?.Invoke(this, resolution);
        return Task.FromResult(resolution);
    }

    public Task<RemoteApprovalResolution> WaitAsync(string approvalId, CancellationToken cancellationToken = default)
    {
        PendingApproval? entry;
        lock (_lock)
        {
            if (_completed.TryGetValue(approvalId, out var resolved)) return Task.FromResult(resolved);
            _pending.TryGetValue(approvalId, out entry);
        }

        if (entry is null)
        {
            return Task.FromResult(new RemoteApprovalResolution(
                approvalId, string.Empty, RemoteApprovalDecision.Deny, false, "审批不存在或已失效。"));
        }

        return entry.Completion.Task.WaitAsync(cancellationToken);
    }
}

/// <summary>
/// 可更换底层闸门，但自身事件订阅保持不变。桌面切换会话时，远控服务已经订阅了
/// 本包装器；若不更换订阅对象，新会话的审批事件会被旧订阅遗漏。
/// </summary>
public sealed class DelegatingToolApprovalGate : IRemoteToolApprovalGate, IDisposable
{
    private readonly object _gate = new();
    private IRemoteToolApprovalGate _inner;
    private bool _disposed;

    public DelegatingToolApprovalGate(IRemoteToolApprovalGate inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _inner.Requested += OnRequested;
        _inner.Resolved += OnResolved;
    }

    public event EventHandler<RemoteApprovalRequest>? Requested;
    public event EventHandler<RemoteApprovalResolution>? Resolved;

    public IReadOnlyList<RemoteApprovalRequest> Pending
    {
        get { lock (_gate) return _inner.Pending; }
    }

    public RemoteApprovalRequest Request(string toolCallId, string toolName, string argsJson, string? summary = null)
    {
        IRemoteToolApprovalGate inner;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            inner = _inner;
        }
        return inner.Request(toolCallId, toolName, argsJson, summary);
    }

    public Task<RemoteApprovalResolution> ResolveAsync(string approvalId, RemoteApprovalDecision decision,
        CancellationToken cancellationToken = default)
    {
        IRemoteToolApprovalGate inner;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            inner = _inner;
        }
        return inner.ResolveAsync(approvalId, decision, cancellationToken);
    }

    public Task<RemoteApprovalResolution> WaitAsync(string approvalId, CancellationToken cancellationToken = default)
    {
        IRemoteToolApprovalGate inner;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            inner = _inner;
        }
        return inner.WaitAsync(approvalId, cancellationToken);
    }

    public void Replace(IRemoteToolApprovalGate next)
    {
        ArgumentNullException.ThrowIfNull(next);
        IRemoteToolApprovalGate previous;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            previous = _inner;
            previous.Requested -= OnRequested;
            previous.Resolved -= OnResolved;
            _inner = next;
            next.Requested += OnRequested;
            next.Resolved += OnResolved;
        }

        if (!ReferenceEquals(previous, next) && previous is IDisposable disposable) disposable.Dispose();
    }

    private void OnRequested(object? sender, RemoteApprovalRequest request) => Requested?.Invoke(this, request);
    private void OnResolved(object? sender, RemoteApprovalResolution resolution) => Resolved?.Invoke(this, resolution);

    public void Dispose()
    {
        IRemoteToolApprovalGate previous;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            previous = _inner;
            previous.Requested -= OnRequested;
            previous.Resolved -= OnResolved;
        }
        if (previous is IDisposable disposable) disposable.Dispose();
    }
}
