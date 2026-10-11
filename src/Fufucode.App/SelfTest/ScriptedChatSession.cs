using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Fufucode.App.Abstractions;

namespace Fufucode.App.SelfTest;

/// <summary>
/// 按需引发事件的 <see cref="IChatSession"/> 测试替身：状态由测试直接赋值，
/// 事件经 <see cref="Raise"/> 手动注入，用于确定性验证视图模型对事件序列的反应
/// （停止窗口的错误归一、排队显示、usage 覆盖等）。
/// </summary>
public sealed class ScriptedChatSession : IChatSession
{
    private readonly List<string> _queue = new();

    public SessionConnection Connection { get; set; } = SessionConnection.Connected;
    public SessionActivity Activity { get; set; } = SessionActivity.Idle;
    public SessionOutcome? LastOutcome { get; set; }
    public string? CurrentModel => "scripted-model";

    public bool CanSend => Connection == SessionConnection.Connected;
    public bool CanStop => Activity is SessionActivity.Running or SessionActivity.Cancelling || _queue.Count > 0;
    public int QueuedCount => _queue.Count;

    /// <summary>SendAsync 收到的文本（顺序）。</summary>
    public List<string> Sent { get; } = new();

    public event EventHandler<SessionEvent>? Event;

    /// <summary>引发一条会话事件（调用方负责先把状态字段置为对应值）。</summary>
    public void Raise(SessionEvent e) => Event?.Invoke(this, e);

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        Connection = SessionConnection.Connected;
        Raise(new SessionEvent.ConnectionChanged(Connection));
        return Task.CompletedTask;
    }

    public Task SendAsync(string text, CancellationToken cancellationToken = default)
    {
        Sent.Add(text);
        if (Activity is SessionActivity.Running or SessionActivity.Compacting
            or SessionActivity.Retrying or SessionActivity.Cancelling)
        {
            _queue.Add(text);
            Raise(new SessionEvent.QueueChanged(_queue.Count, _queue.ToArray()));
        }

        return Task.CompletedTask;
    }

    /// <summary>直接以给定快照广播 QueueChanged（模拟 coordinator 回报，P2-17 steer 徽章测试）。</summary>
    public void SimulateQueuedTexts(params string[] texts)
    {
        _queue.Clear();
        _queue.AddRange(texts);
        Raise(new SessionEvent.QueueChanged(_queue.Count, _queue.ToArray()));
    }

    /// <summary>模拟队列耗尽（如下一条被取出执行）。</summary>
    public void DrainQueue()
    {
        _queue.Clear();
        Raise(new SessionEvent.QueueChanged(0, Array.Empty<string>()));
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_queue.Count > 0)
        {
            var cleared = _queue.ToArray();
            _queue.Clear();
            Raise(new SessionEvent.QueueCleared(cleared));
            Raise(new SessionEvent.QueueChanged(0, Array.Empty<string>()));
        }

        return Task.CompletedTask;
    }
}
