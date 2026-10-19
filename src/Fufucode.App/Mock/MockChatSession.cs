using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Fufucode.App.Abstractions;
using Fufucode.App.Integration;

namespace Fufucode.App.Mock;

/// <summary>
/// <see cref="IChatSession"/> 的 mock 实现：用脚本化事件流模拟后端行为，不访问任何真实进程。
/// 演示范围：流式输出（ delta 节奏）、分块消息重建、工具卡片（成功/失败）、
/// 错误条目、排队、停止（清空队列并回传待恢复草稿）。
/// </summary>
public sealed class MockChatSession : IChatSession, IPromptSteering, IPresentationProfiled
{
    /// <summary>演示会话由脚本即时产生事件，不需要等待提示。</summary>
    public BackendPresentationProfile Presentation => BackendPresentationProfile.Scripted;

    private const string ErrorTriggerMarker = "失败";

    private readonly object _gate = new();
    private readonly List<string> _queue = new();

    private SessionConnection _connection = SessionConnection.Stopped;
    private SessionActivity _activity = SessionActivity.Idle;
    private SessionOutcome? _lastOutcome;
    private CancellationTokenSource? _runCts;
    private Task? _runningTask;
    private int _messageSeq;
    private int _cumulativeInputTokens;
    private int _cumulativeOutputTokens;

    public SessionConnection Connection => _connection;
    public SessionActivity Activity => _activity;
    public SessionOutcome? LastOutcome => _lastOutcome;
    public string? CurrentModel => MockScenario.Model;

    public bool CanSend => _connection == SessionConnection.Connected
                           && _activity != SessionActivity.Cancelling;

    public bool CanStop => _activity == SessionActivity.Running
                           || _activity == SessionActivity.Cancelling
                           || _queue.Count > 0;

    public int QueuedCount => _queue.Count;

    // ── IPromptSteering（P2-17 mock）：steer 与排队共用同一条队列演示，
    //    语义差异仅体现在排队条徽章上（steer 项由 InputViewModel 标注「插入」）。 ──
    public bool CanSteer => _connection == SessionConnection.Connected
                            && _activity == SessionActivity.Running;

    public Task SteerAsync(string text, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!CanSteer)
            {
                throw new InvalidOperationException("当前不可插入（会话未在运行中，mock）");
            }

            _queue.Add(text);
            RaiseQueueChanged();
        }

        return Task.CompletedTask;
    }

    public event EventHandler<SessionEvent>? Event;

    /// <summary>连接 mock 后端。真实实现里这对应后端会话启动。</summary>
    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_connection is SessionConnection.Connected or SessionConnection.Starting)
            {
                return Task.CompletedTask;
            }

            _connection = SessionConnection.Starting;
            Raise(new SessionEvent.ConnectionChanged(_connection));

            _connection = SessionConnection.Connected;
            Raise(new SessionEvent.ConnectionChanged(_connection));
            Raise(new SessionEvent.ActivityChanged(_activity));
        }

        return Task.CompletedTask;
    }

    public Task SendAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Task.CompletedTask;
        }

        CancellationTokenSource linked;
        lock (_gate)
        {
            if (_connection != SessionConnection.Connected)
            {
                throw new InvalidOperationException("会话未连接，无法提交（mock）");
            }

            if (_activity is SessionActivity.Running or SessionActivity.Cancelling)
            {
                _queue.Add(text);
                RaiseQueueChanged();
                return Task.CompletedTask;
            }

            _runCts?.Dispose();
            _runCts = new CancellationTokenSource();
            linked = CancellationTokenSource.CreateLinkedTokenSource(_runCts.Token, cancellationToken);
            _runningTask = Task.Run(() => RunTurnAsync(text, linked.Token), linked.Token);
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        List<string> cleared;
        Task? running;
        CancellationTokenSource? cts;

        lock (_gate)
        {
            cleared = new List<string>(_queue);
            _queue.Clear();
            cts = _runCts;
            running = _runningTask;

            if (_activity == SessionActivity.Running)
            {
                _activity = SessionActivity.Cancelling;
                Raise(new SessionEvent.ActivityChanged(_activity));
            }

            if (cleared.Count > 0)
            {
                Raise(new SessionEvent.QueueCleared(cleared));
            }

            RaiseQueueChanged();
        }

        if (cts is not null)
        {
            cts.Cancel();
        }

        // 等待进行中的回合收敛（有界），避免"停止后仍在继续"的假象
        if (running is not null)
        {
            try
            {
                await running.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // 正常的停止路径
            }
            catch (TimeoutException)
            {
                // mock 回合不会长时间阻塞；真实实现里这里应升级为强制终止
            }
        }
    }

    /// <summary>生成一段长会话（≥500 条），用于滚动与输入的卡顿自测。</summary>
    public async Task GenerateLongConversationAsync(int messageCount, CancellationToken cancellationToken = default)
    {
        for (var i = 0; i < messageCount; i++)
        {
            var userText = $"第 {i + 1} 条：请总结当前进度";
            await RunTurnAsync(userText, cancellationToken, longConversation: true);
            // 定期让出控制权，让合并刷新有时间落盘到 UI
            if (i % 8 == 0)
            {
                await Task.Delay(1, cancellationToken);
            }
        }
    }

    private async Task RunTurnAsync(string userText, CancellationToken cancellationToken, bool longConversation = false)
    {
        try
        {
            // 用户消息通过事件流回放（不经返回值）
            var userId = NextMessageId();
            Raise(new SessionEvent.MessageStarted(userId, MessageRole.User, null, 1));
            Raise(new SessionEvent.BlockStarted(userId, 0, MessageBlockKind.Text, null, null));
            Raise(new SessionEvent.TextAppended(userId, 0, userText));
            Raise(new SessionEvent.BlockFinished(userId, 0, new TextBlock(userText)));
            Raise(new SessionEvent.MessageFinished(userId, FinalUserMessage(userId, userText), null));

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            SetActivity(SessionActivity.Running);

            try
            {
                var assistantId = NextMessageId();
                if (longConversation)
                {
                    var blocks = PlayLongConversationMessage(assistantId);
                    _lastOutcome = SessionOutcome.Completed;
                    Raise(new SessionEvent.MessageFinished(assistantId,
                        new ChatMessage(assistantId, MessageRole.Assistant, MockScenario.Model, blocks, null, true),
                        CumulativeUsage()));
                    Raise(new SessionEvent.TurnSettled(SessionOutcome.Completed, null));
                }
                else if (userText.Contains(ErrorTriggerMarker, StringComparison.OrdinalIgnoreCase))
                {
                    await MockScenario.PlayFailureAsync(Raise, Delay, assistantId, cancellationToken);
                    _lastOutcome = SessionOutcome.Failed;
                    Raise(new SessionEvent.SessionError(
                        SessionErrorKind.ModelFailed,
                        "模拟模型请求失败：MOCK-503（演示错误条目）",
                        null));
                    Raise(new SessionEvent.TurnSettled(SessionOutcome.Failed, "模型请求失败"));
                }
                else
                {
                    var finalBlocks = await MockScenario.PlayAsync(userText, Raise, Delay, assistantId, cancellationToken);
                    _lastOutcome = SessionOutcome.Completed;
                    Raise(new SessionEvent.MessageFinished(assistantId,
                        new ChatMessage(assistantId, MessageRole.Assistant, MockScenario.Model, finalBlocks, null, true),
                        CumulativeUsage()));
                    Raise(new SessionEvent.TurnSettled(SessionOutcome.Completed, null));
                }
            }
            catch (OperationCanceledException)
            {
                _lastOutcome = SessionOutcome.Interrupted;
                Raise(new SessionEvent.TurnSettled(SessionOutcome.Interrupted, "已停止"));
            }
            catch (Exception ex)
            {
                _lastOutcome = SessionOutcome.Failed;
                Raise(new SessionEvent.SessionError(SessionErrorKind.Unknown, ex.Message, null));
                Raise(new SessionEvent.TurnSettled(SessionOutcome.Failed, ex.Message));
            }
            finally
            {
                SetActivity(SessionActivity.Idle);
            }
        }
        finally
        {
            TryDequeueNext();
        }
    }

    private IReadOnlyList<MessageBlock> PlayLongConversationMessage(string messageId)
    {
        const string body = "当前进度：M2 界面部分由 mock 数据驱动，外壳与时间线已可用。" +
                            "真实链路要等 M2 集成轨道 A 的 Core 之后才能验证。这是长会话压力演示消息。";
        Raise(new SessionEvent.MessageStarted(messageId, MessageRole.Assistant, MockScenario.Model, 1));
        Raise(new SessionEvent.BlockStarted(messageId, 0, MessageBlockKind.Text, null, null));
        Raise(new SessionEvent.TextAppended(messageId, 0, body));
        var block = new TextBlock(body);
        Raise(new SessionEvent.BlockFinished(messageId, 0, block));
        return new MessageBlock[] { block };
    }

    private void TryDequeueNext()
    {
        string? next = null;
        CancellationTokenSource? linked = null;
        lock (_gate)
        {
            if (_queue.Count > 0)
            {
                next = _queue[0];
                _queue.RemoveAt(0);
                RaiseQueueChanged();
                _runCts?.Dispose();
                _runCts = new CancellationTokenSource();
                linked = CancellationTokenSource.CreateLinkedTokenSource(_runCts.Token);
                _runningTask = Task.Run(() => RunTurnAsync(next!, linked.Token), linked.Token);
            }
        }
    }

    /// <summary>自测/截图用：把会话置于运行中并按给定文本广播队列（不触发真实执行）。</summary>
    public void SimulateRunningQueue(IReadOnlyList<string> texts)
    {
        _queue.Clear();
        _queue.AddRange(texts);
        SetActivity(SessionActivity.Running);
        RaiseQueueChanged();
    }

    private void SetActivity(SessionActivity activity)
    {
        lock (_gate)
        {
            _activity = activity;
            Raise(new SessionEvent.ActivityChanged(activity));
        }
    }

    private void RaiseQueueChanged() => Raise(new SessionEvent.QueueChanged(_queue.Count, _queue.ToArray()));

    private void Raise(SessionEvent e) => Event?.Invoke(this, e);

    private static Task Delay(int milliseconds, CancellationToken cancellationToken)
        => Task.Delay(milliseconds, cancellationToken);

    private string NextMessageId() => "msg-" + Interlocked.Increment(ref _messageSeq);

    private static ChatMessage FinalUserMessage(string id, string text)
        => new(id, MessageRole.User, null, new MessageBlock[] { new TextBlock(text) }, null, true);

    private UsageInfo CumulativeUsage()
    {
        _cumulativeInputTokens += 128;
        _cumulativeOutputTokens += 256;
        return new UsageInfo
        {
            InputTokens = _cumulativeInputTokens,
            OutputTokens = _cumulativeOutputTokens,
            CacheReadTokens = null,
            CacheWriteTokens = null,
        };
    }
}
