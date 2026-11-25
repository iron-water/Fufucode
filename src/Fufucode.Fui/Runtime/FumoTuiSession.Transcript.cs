using Fufucode.App.Abstractions;
using Fufucode.Core;
using Fufucode.Core.Backend;

namespace Fufucode.Fui;

internal sealed partial class FumoTuiSession
{
    public async Task<FuiSessionInfo> InspectAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_closing, this);
        var native = _session.Chat as NativeCSharpChatSession;
        var attachments = native?.GetHistoryMessages()
            .Sum(message => message.Images?.Count ?? 0) ?? 0;
        string? thinking = null;
        try
        {
            thinking = (await _session.Coordinator.QueryThinkingLevelsAsync(ct)).CurrentLevel;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // The level remains unknown when the live coordinator cannot prove it.
        }

        FuiUsageInfo? usage = null;
        var partial = false;
        try
        {
            var report = await Task.Run(() => ReadOnlySessionReport.Load(
                _project.Manager.Layout, Project, Id), ct);
            partial = report.PartialTail;
            var totals = SessionUsageQuery.Query(report.History.CurrentBranchMessages).Session;
            usage = new(
                totals.Input.Value,
                totals.Output.Value,
                totals.TotalTokens.Value,
                totals.CacheRead.Value,
                totals.CacheWrite.Value);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            partial = true;
        }

        long? contextWindow = null;
        string? contextWindowSource = null;
        string? contextWindowUnknown = null;
        if (Model is { Length: > 0 } currentModel)
        {
            try
            {
                var model = (await ModelsAsync(ct))
                    .FirstOrDefault(item => string.Equals(item.Id, currentModel, StringComparison.OrdinalIgnoreCase));
                if (model?.ContextLength is > 0)
                {
                    contextWindow = model.ContextLength;
                    contextWindowSource = "端点模型目录";
                }
                else
                {
                    contextWindowUnknown = model is null
                        ? "端点模型目录未包含当前模型"
                        : "端点模型目录未报告上下文窗口";
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                contextWindowUnknown = "端点模型目录不可用：" + error.GetBaseException().Message;
            }
        }
        else
        {
            contextWindowUnknown = "未选择模型";
        }

        var pending = (_session.Chat as IQueuedChatSession)?.PendingQueue;
        var queued = pending?.QueuedTexts.Select((text, index) => new FuiQueueEntry(
            text, pending.Steering is { } steering && index < steering.Count && steering[index])).ToArray() ?? [];
        var recoverable = _session.Coordinator.Snapshot.RecoverableQueue.ToArray();
        return new(
            Project,
            Id,
            Model,
            thinking,
            _projection.MessageCount,
            _projection.ToolCount,
            _projection.ErrorCount,
            attachments,
            _session.Chat.QueuedCount,
            usage,
            partial,
            Thinking: _projection.ThinkingCount,
            ContextWindow: contextWindow,
            ContextWindowSource: contextWindowSource,
            ContextWindowUnknownReason: contextWindowUnknown,
            ExecutionMode: ExecutionMode,
            QueueEntries: queued,
            RecoverableQueue: recoverable);
    }

    public Task<string> RetryLastUserAsync(CancellationToken ct)
    {
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            ct.ThrowIfCancellationRequested();
            if (_session.Chat is not NativeCSharpChatSession native)
                throw new InvalidOperationException("当前会话没有 FUMO 原生发送接口，无法重试。");
            if (_workerPending || native.Activity != SessionActivity.Idle || native.QueuedCount != 0)
                throw new InvalidOperationException("当前回合仍在运行；请等待结束或先停止，历史未修改。");
            var previous = native.GetHistoryMessages()
                .LastOrDefault(message => message.Role == PureAgentRole.User && !string.IsNullOrWhiteSpace(message.Content));
            if (previous is null)
                throw new InvalidOperationException("当前分支没有可重发的用户文本消息，历史未修改。");
            var text = previous.Content;
            SubmitPrompt(() => native.SendAsync(text, ct));
            return Task.FromResult(text);
        }
    }
}
