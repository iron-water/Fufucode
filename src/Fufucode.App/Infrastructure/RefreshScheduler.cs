using System;
using Avalonia.Threading;

namespace Fufucode.App.Infrastructure;

/// <summary>
/// 刷新调度器：把高频流式事件合并成低频 UI 刷新（计划 §6.6：流式刷新合并执行）。
/// </summary>
public interface IRefreshScheduler
{
    /// <summary>请求一次刷新；同一调度器同时只保留一个待执行请求（后来的覆盖之）。</summary>
    void Schedule(Action action);
}

/// <summary>基于 DispatcherTimer 的合并刷新：按固定节拍在 UI 线程执行最近一次请求。</summary>
public sealed class DispatcherRefreshScheduler : IRefreshScheduler
{
    private readonly DispatcherTimer _timer;
    private Action? _pending;

    public DispatcherRefreshScheduler(TimeSpan interval)
    {
        _timer = new DispatcherTimer { Interval = interval };
        _timer.Tick += OnTick;
    }

    public void Schedule(Action action)
    {
        _pending = action;
        if (!_timer.IsEnabled)
        {
            _timer.Start();
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _timer.Stop();
        var pending = _pending;
        _pending = null;
        pending?.Invoke();
    }
}

/// <summary>同步刷新：立即执行。仅用于无头自测等需要确定性顺序的场景。</summary>
public sealed class SynchronousRefreshScheduler : IRefreshScheduler
{
    public static readonly SynchronousRefreshScheduler Instance = new();

    public void Schedule(Action action) => action();
}
