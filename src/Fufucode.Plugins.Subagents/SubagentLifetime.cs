using System;
using System.Threading;
using System.Threading.Tasks;

namespace Fufucode.Core.Backend;

/// <summary>
/// 一次插件激活代次共享的撤销状态与取消源。
/// Shutdown 先 <see cref="Revoke"/>（置非激活并取消在途 token），再 <see cref="DrainAsync"/>
/// 等待所有 lease 退出，最后才释放 CTS；Shutdown 等待被取消时保留 CTS 供重试，不提前 Dispose。
/// </summary>
internal sealed class SubagentGeneration
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cts = new();
    private int _pending;
    private bool _revoked;
    private bool _disposed;
    private bool _cancellationFinished;
    private TaskCompletionSource? _drained;

    /// <summary>旧会话每轮读取的实时状态；撤销后立即为 false，且不会因 CTS 释放而抛异常。</summary>
    public bool IsActive
    {
        get { lock (_gate) return !_revoked && !_disposed; }
    }

    /// <summary>测试/诊断用：CTS 是否已在最后一个 lease 退出后释放。</summary>
    internal bool IsDisposed
    {
        get { lock (_gate) return _disposed; }
    }

    /// <summary>进入一次执行；撤销后拒绝，确保旧会话不会再启动新调用。</summary>
    public bool TryEnter(out CancellationToken token)
    {
        lock (_gate)
        {
            if (_revoked || _disposed)
            {
                token = default;
                return false;
            }
            _pending++;
            token = _cts.Token;
            return true;
        }
    }

    /// <summary>撤销可见性并取消在途调用；重复调用无副作用。</summary>
    public void Revoke()
    {
        CancellationTokenSource? cts = null;
        lock (_gate)
        {
            if (_revoked) return;
            _revoked = true;
            if (!_disposed) cts = _cts;
        }
        if (cts is null) return;
        try
        {
            // 在锁外取消：Cancel 会同步执行 linked source 回调，持锁取消容易与 Exit 互锁。
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 理论上 Dispose 等待 _cancellationFinished，不会先于 Cancel；防御性保留。
        }
        finally
        {
            TaskCompletionSource? drained = null;
            lock (_gate)
            {
                _cancellationFinished = true;
                if (_pending == 0)
                {
                    DisposeLocked();
                    drained = _drained;
                }
            }
            drained?.TrySetResult();
        }
    }

    /// <summary>
    /// 等待在途 lease 全部退出后释放 CTS。等待期间不 Dispose，避免 Cancel/Dispose 竞态；
    /// 等待被取消时保留 CTS 与待退出计数，下次 Shutdown 可重试。
    /// </summary>
    public Task DrainAsync(CancellationToken cancellationToken)
    {
        Task drained;
        lock (_gate)
        {
            if (_disposed) return Task.CompletedTask;
            if (_pending == 0 && _cancellationFinished)
            {
                DisposeLocked();
                return Task.CompletedTask;
            }
            _drained ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            drained = _drained.Task;
        }
        return drained.WaitAsync(cancellationToken);
    }

    /// <summary>退出一次执行；撤销后最后一个退出者负责释放 CTS 并唤醒等待者。</summary>
    public void Exit()
    {
        TaskCompletionSource? drained = null;
        lock (_gate)
        {
            if (_pending <= 0) return;
            _pending--;
            if (_pending != 0 || !_revoked || !_cancellationFinished || _disposed) return;
            DisposeLocked();
            drained = _drained;
            _drained = null;
        }
        drained?.TrySetResult();
    }

    private void DisposeLocked()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Dispose();
    }
}
